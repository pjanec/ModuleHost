// File: ModuleHost.Core.Tests/ProviderLeaseTests.cs
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
        private SnapshotPool _pool;

        public ProviderLeaseTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccumulator = new EventAccumulator();
            _mask = new BitMask256();
            for(int i=0; i<256; i++) _mask.SetBit(i);
            _pool = new SnapshotPool(null);
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
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, _pool);
            
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
            // Scenario: Async module holds view
            // In new Convoy implementation, if one holds it, subsequent acquires get the SAME one.
            // This test previously verified DETACH (splitting).
            // We will update it to verify CONVOY (joining).
            
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, _pool);
            
            // Frame 1 acquire
            var v1 = provider.AcquireView();
            
            // Frame 2 starts (Update called - empty now)
            provider.Update(); 
            
            _liveWorld.Tick(); // Advance live world
            
            // Frame 2 acquires view 
            // Since v1 is still held (activeReaders=1), v2 should be SAME as v1 (joining the convoy)
            var v2 = provider.AcquireView();
            
            Assert.Same(v1, v2);
            
            provider.ReleaseView(v1);
            provider.ReleaseView(v2);
            
            // Now fully released. Pool reuses instance.
            var v3 = provider.AcquireView();
            Assert.Same(v1, v3);
            provider.ReleaseView(v3);
        }

        [Fact]
        public void SharedSnapshotProvider_ReusesIfReleased()
        {
            var provider = new SharedSnapshotProvider(_liveWorld, _eventAccumulator, _mask, _pool);
            
            var v1 = provider.AcquireView();
            provider.ReleaseView(v1); // Count = 0
            
            provider.Update(); 
            
            var v2 = provider.AcquireView();
            
            // Pool (Stack) behavior: Push v1, Pop v1.
            Assert.Same(v1, v2);
            
            provider.ReleaseView(v2);
        }
    }
}
