using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using System.Collections.Generic;
using System.Threading;

namespace ModuleHost.Core.Tests
{
    public class ReactiveSchedulingTests
    {
        class MockModule : IModule
        {
            public string Name => "MockModule";
            
            // Use ExecutionPolicy from BATCH-05
            public ExecutionPolicy Policy { get; set; } = ExecutionPolicy.FastReplica();
            
            public List<Type> TriggersEvents { get; } = new();
            public List<Type> TriggersComponents { get; } = new();

            // Implementing Interface Properties
            public IReadOnlyList<Type> WatchEvents => TriggersEvents;
            public IReadOnlyList<Type> WatchComponents => TriggersComponents;

            // Legacy
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;

            public void RegisterSystems(ISystemRegistry registry) { }
            public void Tick(ISimulationView view, float deltaTime) { }
        }

        class MockAsyncModule : IModule
        {
            public string Name => "MockAsyncModule";
            public ExecutionPolicy Policy { get; set; }
            public TimeSpan TaskDuration { get; set; }
            public int RunCount { get; private set; }
            
            public List<Type> TriggersComponents { get; } = new();
            public IReadOnlyList<Type> WatchComponents => TriggersComponents; 

            // Legacy
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;

            public void RegisterSystems(ISystemRegistry registry) { }
            
            public void Tick(ISimulationView view, float deltaTime)
            {
                RunCount++;
                if (TaskDuration > TimeSpan.Zero)
                {
                    Thread.Sleep(TaskDuration);
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
               // 10Hz = every 6 frames at 60Hz base
                Policy = ExecutionPolicy.Synchronous().WithFrequency(10) 
            };
            kernel.RegisterModule(module);
            kernel.Initialize();

            // Run 5 frames (should not run yet, since 0+1 < 6)
            for(int i=0; i<5; i++) 
            {
                kernel.Update(0.016f); 
            }
            
            var stats = kernel.GetExecutionStats();
            var mStat = stats.First(s => s.ModuleName == "MockModule");
            Assert.Equal(0, mStat.ExecutionCount);

            // Frame 6
            kernel.Update(0.016f);
            stats = kernel.GetExecutionStats();
            mStat = stats.First(s => s.ModuleName == "MockModule");
            Assert.Equal(1, mStat.ExecutionCount); 
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
                // 1Hz to avoid periodic noise
                Policy = ExecutionPolicy.Synchronous().WithFrequency(1)
            };
            module.TriggersEvents.Add(typeof(TestEvent));
            
            kernel.RegisterModule(module);
            kernel.Initialize();

            // 1. No Event
            kernel.Update(0.1f);
            Assert.Equal(0, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 2. Publish Event
            repo.Bus.Publish(new TestEvent{ X = 1 });
            kernel.Update(0.1f);
            Assert.Equal(1, kernel.GetExecutionStats().First(s => s.ModuleName == "MockModule").ExecutionCount);

            // 3. No new event next frame
            kernel.Update(0.1f);
            // Should reset execution count in stats call (GetExecutionStats resets), but actually GetExecutionStats returns accumulated since last call?
            // ModuleHostKernel.GetExecutionStats() does: entry.ExecutionCount; then resets entry.ExecutionCount = 0.
            // So if we call it again, we expect 0 if it didn't run.
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
                 Policy = ExecutionPolicy.Synchronous().WithFrequency(1)
            };
            module.TriggersComponents.Add(typeof(TestComponent));
            
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
                Policy = ExecutionPolicy.SlowBackground(1),
                TaskDuration = TimeSpan.FromMilliseconds(50) 
            };
            asyncModule.TriggersComponents.Add(typeof(TestComponent));
            
            kernel.RegisterModule(asyncModule);
            kernel.Initialize();
            
            // Frame 1: Trigger module
            var e = repo.CreateEntity();
            repo.SetComponent(e, new TestComponent { X = 1 });
            kernel.Update(0.016f);
            
            // Frame 2: Module still running, NEW change happens
            repo.SetComponent(e, new TestComponent { X = 2 });
            kernel.Update(0.016f);
            
            // Wait for task
            await Task.Delay(100);
            kernel.Update(0.016f); // Harvest
            
            // Should re-trigger
            kernel.Update(0.016f);
            
            await Task.Delay(100);
            
            Assert.Equal(2, asyncModule.RunCount); 
        }
    }
}
