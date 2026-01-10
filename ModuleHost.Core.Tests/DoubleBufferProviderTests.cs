using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests
{
    public class DoubleBufferProviderTests
    {
        [EventId(201)]
        public struct TestEvent { public int Value; }

        struct TestComponent { public int X; }

        [Fact]
        public void Constructor_CreatesReplica()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var provider = new DoubleBufferProvider(live, acc);

            ISimulationView view = provider.AcquireView();
            Assert.NotNull(view);
            Assert.NotSame(live, view); // Should be a replica, not live
        }

        [Fact]
        public void Update_SyncsReplicaFromLive()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<TestComponent>();
            
            var acc = new EventAccumulator();
            using var provider = new DoubleBufferProvider(live, acc);
            
            // Register component on replica (manually for now, as provider TODO says)
            // Ideally provider would have mechanism to sync schema, but SyncFrom usually copies data.
            // EntityRepository.SyncFrom requires target table to exist.
            var replica = (EntityRepository)provider.AcquireView();
            replica.RegisterComponent<TestComponent>();

            // Setup live data
            var e = live.CreateEntity();
            live.AddComponent(e, new TestComponent { X = 42 });
            live.Tick(); // Advance tick

            // Act
            provider.Update();

            // Assert
            var replicaC = replica.GetComponentRO<TestComponent>(e);
            Assert.Equal(42, replicaC.X);
            // GlobalVersion starts at 1. Tick() increments to 2. SyncFrom should copy it.
            Assert.Equal(2u, replica.GlobalVersion);
        }

        [Fact]
        public void Update_FlushesEventHistory()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator(maxHistoryFrames: 10);
            using var provider = new DoubleBufferProvider(live, acc);
            
            // Publish event on live
            live.Tick();
            live.Bus.Publish(new TestEvent { Value = 123 });
            live.Bus.SwapBuffers(); // Make available for capture
            
            // Capture
            acc.CaptureFrame(live.Bus, 1);
            
            // Act: Update should flush to replica
            provider.Update();
            
            // Verify replica received event
            var replica = (EntityRepository)provider.AcquireView();
            var events = replica.Bus.Consume<TestEvent>();
            
            Assert.Equal(1, events.Length);
            Assert.Equal(123, events[0].Value);
        }

        [Fact]
        public void AcquireView_ReturnsReplica()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var provider = new DoubleBufferProvider(live, acc);
            
            var view = provider.AcquireView();
            Assert.True(view is EntityRepository);
        }

        [Fact]
        public void AcquireView_ZeroCopy()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var provider = new DoubleBufferProvider(live, acc);
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            
            Assert.Same(view1, view2);
        }

        [Fact]
        public void ReleaseView_NoOp()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            using var provider = new DoubleBufferProvider(live, acc);
            
            var view = provider.AcquireView();
            provider.ReleaseView(view);
            
            // Should still be valid/alive
            var view2 = provider.AcquireView();
            Assert.Same(view, view2);
            Assert.True(view.Tick > 0); // Should be accessible and valid
        }
        [DataPolicy(DataPolicy.Transient)]
        struct TransComp { public int Val; }
        
        [Fact]
        public void Update_ExcludesTransientComponents()
        {
             using var live = new EntityRepository();
             live.RegisterComponent<TransComp>();
             live.RegisterComponent<TestComponent>();
             
             var acc = new EventAccumulator();
             // Default constructor = null mask = Auto Filter Transient
             using var provider = new DoubleBufferProvider(live, acc);
             
             var e = live.CreateEntity();
             live.AddComponent(e, new TransComp { Val = 1 });
             live.AddComponent(e, new TestComponent { X = 2 });
             live.Tick();
             
             provider.Update();
             
             var replica = (EntityRepository)provider.AcquireView();
             
             // TransComp should be missing
             // Note: SyncFrom automatically registers components on destination if missing
             Assert.False(replica.HasComponent<TransComp>(e));
             // TestComponent should be present
             Assert.True(replica.HasComponent<TestComponent>(e));
        }
    }
}
