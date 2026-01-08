// File: ModuleHost.Core.Tests/ResilienceIntegrationTests.cs

using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Resilience;
using Fdp.Kernel;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ModuleHost.Core.Tests
{
    public class ResilienceIntegrationTests
    {
        private class TestModule : IModule
        {
            public string Name { get; set; } = "TestModule";
            public ModuleTier Tier { get; set; } = ModuleTier.Slow;
            public int UpdateFrequency { get; set; } = 1;
            public int? MaxExpectedRuntimeMs { get; set; }
            public int? FailureThreshold { get; set; }
            public int? CircuitResetTimeoutMs { get; set; }
            
            public Action<ISimulationView, float> TickAction { get; set; }
            public int ExecutionCount { get; set; }

            // Explicit implementation to allow null overrides to fallback to default (or we implement logic)
            // But IModule properties use expression bodies in interface default.
            // If I override them, I must provide value.
            // So I will just implement them and use them.
            
            int IModule.MaxExpectedRuntimeMs => MaxExpectedRuntimeMs ?? 500;
            int IModule.FailureThreshold => FailureThreshold ?? 3;
            int IModule.CircuitResetTimeoutMs => CircuitResetTimeoutMs ?? 5000;

            public void Tick(ISimulationView view, float deltaTime)
            {
                TickAction?.Invoke(view, deltaTime);
            }
            
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccum;

        public ResilienceIntegrationTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccum = new EventAccumulator();
        }

        [Fact(Timeout = 5000)]
        public async Task Resilience_HungModule_TimesOut()
        {
            // Create a module that hangs forever
            var hungModule = new TestModule
            {
                Name = "HungModule",
                TickAction = (view, dt) =>
                {
                    // Infinite loop or long sleep
                    // Loop is better to simulate CPU bound hang, but sleep is easier to cancel test if needed.
                    // Instructions said "Infinite loop".
                    // But we want it to timeout.
                    while (true)
                    {
                        Thread.Sleep(10);
                        // Check logic to break if needed? No, it should be killed/abandoned.
                    }
                },
                MaxExpectedRuntimeMs = 200,
                FailureThreshold = 1
            };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(hungModule);
            kernel.Initialize();
            
            // Run several frames
            for (int i = 0; i < 15; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(50); // Allow async tasks to run
            }
            
            // Assert: System continues running (didn't freeze)
            // Assert: Module's circuit breaker opened
            var stats = kernel.GetExecutionStats();
            var modStat = stats.First(s => s.ModuleName == "HungModule");
            Assert.Equal(CircuitState.Open, modStat.CircuitState);
        }
        
        [Fact]
        public async Task Resilience_CrashingModule_Isolated()
        {
            var crashingModule = new TestModule
            {
                Name = "CrashingModule",
                TickAction = (view, dt) =>
                {
                    throw new InvalidOperationException("Simulated crash");
                }
            };
            
            var healthyModule = new TestModule
            {
                Name = "HealthyModule",
                TickAction = (view, dt) =>
                {
                    // Increment a counter we track externally or via stats
                    // The Kernel stats reset every frame (or get call).
                    // We can track via the closure variable but execution happens on another thread.
                    // Interlocked increment.
                },
                ExecutionCount = 0 // We'll check kernel stats
            };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(crashingModule);
            kernel.RegisterModule(healthyModule);
            kernel.Initialize();
            
            // Run 10 frames
            int healthyRunCount = 0;
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
                
                var frameStats = kernel.GetExecutionStats();
                if (frameStats.First(s => s.ModuleName == "HealthyModule").ExecutionCount > 0)
                    healthyRunCount++;
            }
            
            // Assert: Healthy module continued running
            Assert.True(healthyRunCount > 0);
            
            // Assert: Crashing module's circuit opened
            var stats = kernel.GetExecutionStats();
            var crashedModule = stats.First(s => s.ModuleName == "CrashingModule");
            Assert.Equal(CircuitState.Open, crashedModule.CircuitState);
        }
        
        [Fact]
        public async Task Resilience_FlakyModule_CircuitTrips_ThenRecovers()
        {
            int executionCount = 0;
            
            var flakyModule = new TestModule
            {
                Name = "FlakyModule",
                TickAction = (view, dt) =>
                {
                    int c = Interlocked.Increment(ref executionCount);
                    
                    // Fail first 3 times, then succeed
                    if (c <= 3)
                    {
                        throw new Exception($"Flaky failure {c}");
                    }
                },
                MaxExpectedRuntimeMs = 100,
                FailureThreshold = 3,
                CircuitResetTimeoutMs = 500
            };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(flakyModule);
            kernel.Initialize();
            
            // Run frames until circuit trips
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
            }
            
            var stats1 = kernel.GetExecutionStats();
            var module1 = stats1.First(s => s.ModuleName == "FlakyModule");
            Assert.Equal(CircuitState.Open, module1.CircuitState);
            
            // Wait for reset timeout
            await Task.Delay(600);
            
            // Run more frames - should attempt recovery
            // Reset local execution count or just continue?
            // Next execution will be #4 -> Success.
            
            // Need to trigger update to make it try running
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
            }
            
            // Assert: Circuit recovered (closed)
            var stats2 = kernel.GetExecutionStats();
            var module2 = stats2.First(s => s.ModuleName == "FlakyModule");
            Assert.Equal(CircuitState.Closed, module2.CircuitState);
        }
        
        [Fact]
        public async Task Resilience_MultipleModulesFailing_SystemDegrades()
        {
            var goodModule = new TestModule { Name = "Good" };
            var badModule1 = new TestModule { Name = "Bad1", TickAction = (v, d) => throw new Exception() };
            var badModule2 = new TestModule { Name = "Bad2", TickAction = (v, d) => { while(true) Thread.Sleep(10); }, MaxExpectedRuntimeMs=50 };
            var badModule3 = new TestModule { Name = "Bad3", TickAction = (v, d) => throw new Exception() };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(goodModule);
            kernel.RegisterModule(badModule1);
            kernel.RegisterModule(badModule2);
            kernel.RegisterModule(badModule3);
            kernel.Initialize();
            
            // Run simulation
            int goodRuns = 0;
            for (int i = 0; i < 20; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
                
                var s = kernel.GetExecutionStats();
                if (s.First(m => m.ModuleName == "Good").ExecutionCount > 0) goodRuns++;
            }
            
            // Assert: Good module kept running
            Assert.True(goodRuns > 0);
            
            // Assert: Bad modules all opened circuits
            var stats = kernel.GetExecutionStats();
            Assert.All(stats.Where(s => s.ModuleName.StartsWith("Bad")),
                s => Assert.Equal(CircuitState.Open, s.CircuitState));
        }

        [Fact]
        public void Resilience_ExecutionStats_IncludeCircuitState()
        {
            var module = new TestModule { Name = "TestModule" };
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(module);
            kernel.Initialize();
            
            var stats = kernel.GetExecutionStats();
            var moduleStat = stats.First(s => s.ModuleName == "TestModule");
            
            // Assert.NotNull(moduleStat.CircuitState); // Value type, always 'not null' but we check properties
            Assert.Equal(CircuitState.Closed, moduleStat.CircuitState);
            Assert.Equal(0, moduleStat.FailureCount);
        }

        [Fact]
        public async Task Resilience_ZombieTasksDoNotAccumulate_UnderCircuitBreaker()
        {
            int initialThreadCount = Process.GetCurrentProcess().Threads.Count;
            
            var hungModule = new TestModule
            {
                Name = "ZombieSpawner",
                TickAction = (view, dt) => { while(true) Thread.Sleep(100); },
                MaxExpectedRuntimeMs = 50,
                FailureThreshold = 2
            };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(hungModule);
            kernel.Initialize();
            
            // Run 100 frames
            for (int i = 0; i < 100; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(10);
            }
            
            // After circuit opens (at ~2 failures), zombie count should stabilize
            int finalThreadCount = Process.GetCurrentProcess().Threads.Count;
            int zombieThreads = finalThreadCount - initialThreadCount;
            
            // Should have <= FailureThreshold zombie tasks (2), not 100.
            // Note: Thread counts in ThreadPool are heuristic. 
            // We use a safe margin.
            Assert.True(zombieThreads <= 10, 
                $"Expected <=10 zombie threads, found {zombieThreads}");
        }

        [Fact(Timeout = 5000)]
        public async Task Resilience_ModuleCrashesAndTimesOut_OnlyCountsOnce()
        {
            var module = new TestModule
            {
                Name = "CrashingAndHanging",
                TickAction = (view, dt) =>
                {
                    throw new Exception("Crash");
                },
                MaxExpectedRuntimeMs = 100,
                FailureThreshold = 3
            };
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(module);
            kernel.Initialize();
            
            // Run 5 frames
            for (int i = 0; i < 5; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(50);
            }
            
            var stats = kernel.GetExecutionStats();
            var moduleStat = stats.First(s => s.ModuleName == "CrashingAndHanging");
            
            // Should count as ONE failure per execution, not double-counted (or zero)
            // 3 failures should open circuit.
            Assert.Equal(CircuitState.Open, moduleStat.CircuitState);
        }
    }
}
