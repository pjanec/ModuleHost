// File: ModuleHost.Core.Tests/ReactiveSchedulingTests.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests
{
    public class ReactiveSchedulingTests
    {
        class MockModule : IModule
        {
            public string Name => "MockModule";
            public ModuleTier Tier => ModuleTier.Slow; // Async
            public int UpdateFrequency => 1; // Default
            public ModuleExecutionPolicy Policy { get; set; } = ModuleExecutionPolicy.DefaultFast; // FrameSynced for testing
            public void RegisterSystems(ISystemRegistry registry) { }
            public void Tick(ISimulationView view, float deltaTime) { }
        }

        class MockAsyncModule : IModule
        {
            public string Name => "MockAsyncModule";
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;
            public ModuleExecutionPolicy Policy { get; set; } = ModuleExecutionPolicy.DefaultSlow;
            public TimeSpan TaskDuration { get; set; }
            public int RunCount { get; private set; }
            
            public void RegisterSystems(ISystemRegistry registry) { }
            
            public void Tick(ISimulationView view, float deltaTime)
            {
                RunCount++;
                if (TaskDuration > TimeSpan.Zero)
                {
                    System.Threading.Thread.Sleep(TaskDuration);
                }
            }
        }

        [EventId(2001)]
        struct TestEvent { public int X; }

        struct TestComponent { public int X; }

        [Fact]
        public void ShouldRun_Interval_RespectsTime()
        {
            using var repo = new EntityRepository();
            var accumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(repo, accumulator);

            var module = new MockModule 
            { 
                Policy = ModuleExecutionPolicy.FixedInterval(100, ModuleMode.FrameSynced) // FrameSynced
            };
            kernel.RegisterModule(module);
            kernel.Initialize();

            // 1. Update with small delta (50ms) -> Accumulated 50 < 100 -> No Run
            kernel.Update(0.050f);
            var stats = kernel.GetExecutionStats();
            Assert.Equal(0, stats.First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 2. Update with more delta (60ms) -> Accumulated 110 > 100 -> Run
            kernel.Update(0.060f);
            stats = kernel.GetExecutionStats();
            Assert.Equal(1, stats.First(s => s.ModuleName == "MockModule").ExecutionCount); 
        }

        [Fact]
        public void ShouldRun_OnEvent_TriggersOnlyWhenEventPresent()
        {
            using var repo = new EntityRepository();
            var accumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(repo, accumulator);

            repo.Bus.Register<TestEvent>();

            var module = new MockModule 
            { 
                Policy = ModuleExecutionPolicy.OnEvent<TestEvent>(ModuleMode.FrameSynced)
            };
            kernel.RegisterModule(module);
            kernel.Initialize();

            // 1. No Event
            repo.Bus.SwapBuffers(); 
            kernel.Update(0.1f);
            Assert.Equal(0, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 2. Publish Event
            repo.Bus.Publish(new TestEvent{ X = 1 });
            kernel.Update(0.1f);
            Assert.Equal(1, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 3. No new event next frame
            kernel.Update(0.1f);
            Assert.Equal(0, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);
        }

        [Fact]
        public void ShouldRun_OnComponentChange_TriggersOnlyWhenChanged()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<TestComponent>();
            var accumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(repo, accumulator);

            var module = new MockModule 
            { 
                Policy = ModuleExecutionPolicy.OnComponentChange<TestComponent>(ModuleMode.FrameSynced)
            };
            kernel.RegisterModule(module);
            kernel.Initialize();

            // 1. No changes
            kernel.Update(0.1f);
            Assert.Equal(0, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 2. Modify Component
            var e = repo.CreateEntity();
            repo.SetComponent(e, new TestComponent{ X = 10 });
            
            // Run Update
            kernel.Update(0.1f);
            
            Assert.Equal(1, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 3. No change next frame
            kernel.Update(0.1f);
            Assert.Equal(0, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);
        }

        [Fact]
        public async Task ReactiveScheduling_AsyncModule_TracksVersionCorrectly()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<TestComponent>();
            var accumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(repo, accumulator);

            var asyncModule = new MockAsyncModule 
            { 
                Policy = ModuleExecutionPolicy.OnComponentChange<TestComponent>(ModuleMode.Async),
                TaskDuration = TimeSpan.FromMilliseconds(50) // Takes ~3 frames at 16ms
            };
            
            kernel.RegisterModule(asyncModule);
            kernel.Initialize();
            
            // Frame 1: Trigger module
            var e = repo.CreateEntity();
            repo.SetComponent(e, new TestComponent { X = 1 });
            kernel.Update(0.016f);
            
            // Frame 2: Module still running, NEW change happens
            repo.SetComponent(e, new TestComponent { X = 2 });
            kernel.Update(0.016f);
            
            // Frame 3: Module still running (total 32ms < 50ms)
            // Wait a bit to ensure task completes eventually
            await Task.Delay(100);
            kernel.Update(0.016f); // Harvest
            
            // Frame 4: Should re-trigger because change happened WHILE running in Frame 2
            kernel.Update(0.016f);
            
            // Wait for 2nd run to finish (it starts async)
            await Task.Delay(100);
            
            Assert.Equal(2, asyncModule.RunCount); // Ran twice
        }
    }
}
