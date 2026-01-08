using System;
using System.Threading;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests
{
    public class CommandBufferIntegrationTests
    {
        struct TestComponent { public int Value; }

        private class CommandModule : IModule
        {
            public string Name => "CommandModule";
            public ModuleTier Tier => ModuleTier.Fast; // Run every frame
            public int UpdateFrequency => 1;
            public int MaxExpectedRuntimeMs => 1000;
            public bool DidRun { get; private set; }
            public Action<ISimulationView, IEntityCommandBuffer>? OnTick;

            public void Tick(ISimulationView view, float deltaTime)
            {
                DidRun = true;
                var cmd = view.GetCommandBuffer();
                OnTick?.Invoke(view, cmd);
            }
        }

        [Fact]
        public void Module_CanAcquireCommandBuffer()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new CommandModule();
            bool acquired = false;
            module.OnTick = (view, cmd) => 
            {
                Assert.NotNull(cmd);
                acquired = true;
            };

            kernel.RegisterModule(module);
            kernel.Initialize();
            kernel.Update(0.016f);
            
            Assert.True(module.DidRun);
            Assert.True(acquired);
        }

        [Fact]
        public void Module_CanQueueCreateEntity()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new CommandModule();
            module.OnTick = (view, cmd) => 
            {
                cmd.CreateEntity();
            };

            kernel.RegisterModule(module);
            kernel.Initialize();
            kernel.Update(0.016f);

            Assert.Equal(1, live.EntityCount);
        }

        [Fact]
        public void Module_CanQueueAddComponent()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<TestComponent>(); // Must be registered on live
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            // Pre-create entity on live
            var e = live.CreateEntity();
            live.Tick();
            
            var module = new CommandModule();
            module.OnTick = (view, cmd) => 
            {
                // cmd.AddComponent takes Entity. In Module (View), we see Entity e.
                // We queue AddComponent to e.
                // Note: e in View matches e in Live (if GDB is synced)
                cmd.AddComponent(e, new TestComponent { Value = 100 });
            };

            kernel.RegisterModule(module);
            kernel.Initialize();
            kernel.Update(0.016f);

            Assert.True(live.HasComponent<TestComponent>(e));
            Assert.Equal(100, live.GetComponentRO<TestComponent>(e).Value);
        }

        [Fact]
        public void MultipleModules_IndependentCommandBuffers()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<TestComponent>();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var m1 = new CommandModule { };
            m1.OnTick = (view, cmd) => cmd.CreateEntity();
            
            var m2 = new CommandModule { };
            m2.OnTick = (view, cmd) => cmd.CreateEntity();

            kernel.RegisterModule(m1);
            kernel.RegisterModule(m2);
            kernel.Initialize();
            kernel.Update(0.016f);

            Assert.Equal(2, live.EntityCount);
        }

        [Fact]
        public void CommandPlayback_AppliesInOrder()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<TestComponent>();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new CommandModule();
            module.OnTick = (view, cmd) => 
            {
                var e = cmd.CreateEntity();
                cmd.AddComponent(e, new TestComponent { Value = 1 });
                cmd.SetComponent(e, new TestComponent { Value = 2 });
            };

            kernel.RegisterModule(module);
            kernel.Initialize();
            kernel.Update(0.016f);

            Assert.Equal(1, live.EntityCount);
            // Get first entity
            var entity = live.Query().Build().FirstOrNull();
            
            Assert.NotEqual(Entity.Null, entity);
            Assert.Equal(2, live.GetComponentRO<TestComponent>(entity).Value);
        }

        [Fact]
        public void CommandBuffer_ClearsAfterPlayback_NoPersistence()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new CommandModule();
            int callCount = 0;
            module.OnTick = (view, cmd) => 
            {
                // Only queue command on first frame
                if (callCount == 0)
                {
                    cmd.CreateEntity();
                }
                callCount++;
            };

            kernel.RegisterModule(module);
            kernel.Initialize();
            
            // Frame 1: Module runs, queues command. Kernel plays back.
            kernel.Update(0.016f);
            Assert.Equal(1, live.EntityCount); // Command executed
            
            // Frame 2: Module runs (no new commands). Kernel SHOULD play back empty buffer (no-op).
            // If buffer wasn't cleared, it would replay CreateEntity -> Count = 2.
            kernel.Update(0.016f);
            Assert.Equal(1, live.EntityCount); // Still 1
        }
    
        [Fact]
        public void EmptyCommandBuffer_NoOp()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, acc);
            
            var module = new CommandModule();
            module.OnTick = (view, cmd) => { /* Do nothing */ };

            kernel.RegisterModule(module);
            kernel.Initialize();
            kernel.Update(0.016f);
            
            Assert.True(module.DidRun);
            Assert.Equal(0, live.EntityCount);
        }
    }
}
