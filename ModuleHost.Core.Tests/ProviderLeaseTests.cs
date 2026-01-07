using System;
using ModuleHost.Core.Providers;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;
using Xunit;

namespace ModuleHost.Core.Tests
{
    public class ProviderLeaseTests
    {
        private EntityRepository _liveWorld;
        private EventAccumulator _eventAccumulator;
        private BitMask256 _mask;

        public ProviderLeaseTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccumulator = new EventAccumulator();
            _mask = new BitMask256();
            for(int i=0; i<256; i++) _mask.SetBit(i);
        }

        [Fact]
        public void OnDemandProvider_PoolSize_Configurable()
        {
            // Create with poolSize=5
            var provider = new OnDemandProvider(_liveWorld, _eventAccumulator, _mask, null, 5);
            
            // Acquire 5 views
            var views = new ISimulationView[5];
            for(int i=0; i<5; i++)
            {
                views[i] = provider.AcquireView();
            }

            // Assert: No pool exhaustion (all return valid views)
            Assert.All(views, v => Assert.NotNull(v));
            
            // Release them
             foreach(var v in views) provider.ReleaseView(v);
        }

        [Fact]
        public void OnDemandProvider_ConcurrentLeases_DoesntExhaust()
        {
            var provider = new OnDemandProvider(_liveWorld, _eventAccumulator, _mask, null, 2);
            
            var views = new ISimulationView[5];
            for(int i=0; i<5; i++)
            {
                views[i] = provider.AcquireView();
            }
            Assert.All(views, v => Assert.NotNull(v));
             foreach(var v in views) provider.ReleaseView(v);
        }

        [Fact]
        public void SharedSnapshotProvider_RefCount_IncrementsOnAcquire()
        {
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, 1);
            
            var v1 = provider.AcquireView();
            var v2 = provider.AcquireView();
            
            // They should be the SAME instance
            Assert.Same(v1, v2);
            
            provider.ReleaseView(v1);
            provider.ReleaseView(v2);
        }

        [Fact]
        public void SharedSnapshotProvider_ViewValidAcrossFrames_WithDetach()
        {
            // Scenario: Async module holds view across Update()
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, 1);
            
            // Frame 1 acquire
            var v1 = provider.AcquireView();
            
            // Frame 2 starts (Update called while v1 held - simulating busy state)
            // Note: Update locks, checks leases. Since v1 held (count=1), it should detach.
            provider.Update(); 
            
            _liveWorld.Tick(); // Advance live world
            
            // Frame 2 acquires view (should be NEW/Different because old one detached)
            var v2 = provider.AcquireView();
            
            Assert.NotSame(v1, v2);
            
            provider.ReleaseView(v1);
            provider.ReleaseView(v2);
        }

        [Fact]
        public void SharedSnapshotProvider_ReusesIfReleased()
        {
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, 1);
            
            var v1 = provider.AcquireView();
            provider.ReleaseView(v1); // Count = 0
            
            provider.Update(); // Should reusing v1 (it is currentRecyclable and count=0)
            
            var v2 = provider.AcquireView();
            
            Assert.Same(v1, v2);
            
            provider.ReleaseView(v2);
        }
    }
}
