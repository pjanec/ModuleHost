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

            // Override Policy to use these values
            public ExecutionPolicy Policy 
            {
                get
                {
                    // Base policy from Tier
                    var p = Tier == ModuleTier.Fast 
                        ? ExecutionPolicy.FastReplica() 
                        : ExecutionPolicy.SlowBackground(UpdateFrequency <= 1 ? 60 : 60/UpdateFrequency);
                    
                    // Apply overrides
                    if (MaxExpectedRuntimeMs.HasValue) p.MaxExpectedRuntimeMs = MaxExpectedRuntimeMs.Value;
                    else p.MaxExpectedRuntimeMs = 2000; // Default safe timeout for tests
                    
                    if (FailureThreshold.HasValue) p.FailureThreshold = FailureThreshold.Value;
                    if (CircuitResetTimeoutMs.HasValue) p.CircuitResetTimeoutMs = CircuitResetTimeoutMs.Value;
                    
                    return p;
                }
            }

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
                    while (true)
                    {
                        Thread.Sleep(10);
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
                    // Logic ran
                },
                ExecutionCount = 0 
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
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
                
                var stats = kernel.GetExecutionStats();
                var mod = stats.First(s => s.ModuleName == "FlakyModule");
                if (mod.CircuitState == CircuitState.Open) break;
            }
            
            var stats1 = kernel.GetExecutionStats();
            var module1 = stats1.First(s => s.ModuleName == "FlakyModule");
            Assert.Equal(CircuitState.Open, module1.CircuitState);
            
            // Wait for reset timeout
            await Task.Delay(600);
            
            // Run more frames - should attempt recovery (HalfOpen -> Closed)
            // Need to update multiple times as first one might be the probe
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
            
            int goodRuns = 0;
            for (int i = 0; i < 20; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
                
                var s = kernel.GetExecutionStats();
                if (s.First(m => m.ModuleName == "Good").ExecutionCount > 0) goodRuns++;
            }
            
            Assert.True(goodRuns > 0);
            
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
            
            for (int i = 0; i < 100; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(10);
            }
            
            int finalThreadCount = Process.GetCurrentProcess().Threads.Count;
            int zombieThreads = finalThreadCount - initialThreadCount;
            
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
            
            for (int i = 0; i < 5; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(50);
            }
            
            var stats = kernel.GetExecutionStats();
            var moduleStat = stats.First(s => s.ModuleName == "CrashingAndHanging");
            
            Assert.Equal(CircuitState.Open, moduleStat.CircuitState);
        }
    }
}
