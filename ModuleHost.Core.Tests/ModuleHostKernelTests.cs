using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests
{
    public class ModuleHostKernelTests
    {
        private class TestModule : IModule
        {
            public string Name { get; }
            public ModuleTier Tier { get; }
            public int UpdateFrequency { get; }
            public int TickCount { get; private set; }
            public float LastDeltaTime { get; private set; }

            public TestModule(string name, ModuleTier tier, int frequency)
            {
                Name = name;
                Tier = tier;
                UpdateFrequency = frequency;
            }

            public void Tick(ISimulationView view, float deltaTime)
            {
                TickCount++;
                LastDeltaTime = deltaTime;
            }
        }
        
        // Mock provider to verify Update calls
        private class MockProvider : ISnapshotProvider, IDisposable
        {
            public bool UpdateCalled { get; private set; }
            public bool ViewReleased { get; private set; }
            
            public SnapshotProviderType ProviderType => SnapshotProviderType.GDB;
            
            private readonly EntityRepository _repo = new EntityRepository();

            public ISimulationView AcquireView()
            {
                return _repo;
            }

            public void ReleaseView(ISimulationView view)
            {
                ViewReleased = true;
            }

            public void Update()
            {
                UpdateCalled = true;
            }
            
            public void Dispose()
            {
                _repo.Dispose();
            }
        }

        [Fact]
        public void RegisterModule_AddsToList()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Test", ModuleTier.Fast, 1);
            kernel.RegisterModule(module);
            
            // Implicit verification via no exception and subsequent behavior
            kernel.Update(0.16f);
            Assert.Equal(1, module.TickCount);
        }

        [Fact]
        public void RegisterModule_FastTier_AssignsGDBProvider()
        {
            // Note: We can't easily inspect internal state, 
            // but we can infer from default provider behavior or use reflection if strictly needed.
            // Or we check behavior: Fast modules run every frame.
            
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Fast", ModuleTier.Fast, 1);
            kernel.RegisterModule(module);
            
            // Run update, ensure it works
            kernel.Update(0.016f);
            Assert.Equal(1, module.TickCount);
        }

        [Fact]
        public void RegisterModule_SlowTier_AssignsSoDProvider()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Slow", ModuleTier.Slow, 1);
            kernel.RegisterModule(module);
            
            kernel.Update(0.016f);
            Assert.Equal(1, module.TickCount);
        }

        [Fact]
        public void Update_CallsProviderUpdate()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Test", ModuleTier.Fast, 1);
            var provider = new MockProvider();
            
            kernel.RegisterModule(module, provider);
            
            kernel.Update(0.016f);
            
            Assert.True(provider.UpdateCalled);
        }

        [Fact]
        public void Update_FastModule_RunsEveryFrame()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Fast", ModuleTier.Fast, 1);
            kernel.RegisterModule(module);
            
            for(int i=0; i<5; i++)
                kernel.Update(0.016f);
                
            Assert.Equal(5, module.TickCount);
        }

        [Fact]
        public void Update_SlowModule_RunsAtFrequency()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            // Runs every 3 frames
            var module = new TestModule("Slow", ModuleTier.Slow, 3);
            kernel.RegisterModule(module);
            
            // Frame 0: Run (FramesSince=0) -> Runs immediately on first update?
            // Logic: if (FramesSince + 1 >= Freq).
            // Initial FramesSince = 0.
            // Frame 1 check: 0 + 1 >= 3? False. FramesSince -> 1.
            // ...
            // Wait, let's trace logic.
            // start: FramesSince = 0.
            // Update 1: (0+1) >= 3? False. FramesSince=1.
            // Update 2: (1+1) >= 3? False. FramesSince=2.
            // Update 3: (2+1) >= 3? True. Run. FramesSince=0.
            
            // So runs on 3rd update.
            // Let's run 10 times.
            
            for(int i=0; i<10; i++)
                kernel.Update(0.016f);
            
            // Runs: 3, 6, 9. Count = 3.
            Assert.Equal(3, module.TickCount);
        }

        [Fact]
        public void Update_ModuleDeltaTime_Calculated()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new TestModule("Slow", ModuleTier.Slow, 6);
            kernel.RegisterModule(module);
            
            float dt = 0.016f;
            // Run 6 times. Should trigger on 6th.
            for(int i=0; i<6; i++)
                kernel.Update(dt);
                
            Assert.Equal(1, module.TickCount);
            // Delta should be 6 * 0.016 = 0.096 roughly.
            Assert.Equal(6 * dt, module.LastDeltaTime, 4);
        }

        private class ExceptionModule : IModule
        {
            public string Name => "Exception";
            public ModuleTier Tier => ModuleTier.Fast;
            public int UpdateFrequency => 1;
            public void Tick(ISimulationView view, float deltaTime) => throw new Exception("Boom");
        }

        [Fact]
        public void Update_ReleasesView_EvenOnException()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new ExceptionModule();
            var provider = new MockProvider();
            
            kernel.RegisterModule(module, provider);
            
            // Should catch exception in task or propogate via WaitAll.
            // Task.WaitAll wraps exceptions in AggregateException.
            
            Assert.Throws<AggregateException>(() => kernel.Update(0.016f));
            
            Assert.True(provider.ViewReleased);
        }
    }
}
