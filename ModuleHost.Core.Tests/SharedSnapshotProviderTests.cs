using System;
using System.Threading;
using System.Linq;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Providers;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class SharedSnapshotProviderTests
    {
        private struct Position { public float X, Y; }
        private struct Velocity { public float X, Y; }
        private struct TransientState { public int Temp; } // Not registered in mask

        private EntityRepository _liveWorld;
        private EventAccumulator _eventAccum;
        private BitMask256 _unionMask;
        private SnapshotPool _pool;

        public SharedSnapshotProviderTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccum = new EventAccumulator();
            _unionMask = new BitMask256();
            
            // Setup default mask with Position
            _liveWorld.RegisterComponent<Position>();
            _liveWorld.RegisterComponent<Velocity>();
            _liveWorld.RegisterComponent<TransientState>();
            
            _unionMask.SetBit(ComponentType<Position>.ID);
            
            _pool = new SnapshotPool(SetupSchema);
        }

        private void SetupSchema(EntityRepository repo)
        {
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<TransientState>();
        }

        private SharedSnapshotProvider CreateProvider(SnapshotPool? pool = null)
        {
            return new SharedSnapshotProvider(_liveWorld, _eventAccum, _unionMask, pool ?? _pool);
        }

        [Fact]
        public void SharedSnapshotProvider_FirstAcquire_CreatesSnapshot()
        {
            using var provider = CreateProvider();
            var view = provider.AcquireView();
            Assert.NotNull(view);
            Assert.NotSame(_liveWorld, view);
        }

        [Fact]
        public void SharedSnapshotProvider_MultipleAcquires_SameSnapshot()
        {
            using var provider = CreateProvider();
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            var view3 = provider.AcquireView();
            
            Assert.Same(view1, view2);
            Assert.Same(view2, view3);
        }

        [Fact]
        public void SharedSnapshotProvider_RefCount_IncrementsCorrectly()
        {
            // We can't access internal state directly, but we can infer behavior via release
            using var provider = CreateProvider();
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            
            // If ref count was not working, Release(view1) might return it to pool prematurely
            provider.ReleaseView(view1);
            
            // If it was returned, view2 might be invalid or next acquire would be same/diff?
            // A better test is via pool count if we inject a monitored pool
            
            var view3 = provider.AcquireView();
            Assert.Same(view2, view3); // Still same snapshot because view2 is holding it
        }

        [Fact]
        public void SharedSnapshotProvider_OnlyPoolsWhenAllReleased()
        {
            var pool = new SnapshotPool(SetupSchema, warmupCount: 0);
            using var provider = CreateProvider(pool);
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            
            provider.ReleaseView(view1); // count = 1
            Assert.Equal(0, pool.PooledCount); // Not returned yet
            
            provider.ReleaseView(view2); // count = 0
            Assert.Equal(1, pool.PooledCount); // Now returned
        }

        [Fact]
        public void SharedSnapshotProvider_UnionMask_SyncsAllComponents()
        {
            // Setup: Create union mask with Position + Velocity
            var mask = new BitMask256();
            mask.SetBit(ComponentType<Position>.ID);
            mask.SetBit(ComponentType<Velocity>.ID);
            
            // Live world has entities with both components
            var e = _liveWorld.CreateEntity();
            _liveWorld.AddComponent(e, new Position { X = 1, Y = 2 });
            _liveWorld.AddComponent(e, new Velocity { X = 3, Y = 4 });
            _liveWorld.AddComponent(e, new TransientState { Temp = 99 }); // Should NOT sync if not in mask
            
            using var provider = new SharedSnapshotProvider(_liveWorld, _eventAccum, mask, _pool);
            var view = provider.AcquireView() as EntityRepository;
            
            // Assert: Both components synced
            Assert.True(view.HasComponent<Position>(e));
            Assert.True(view.HasComponent<Velocity>(e));
            
            // TransientState validation: if mask excludes it, it should not be there?
            // Actually EntityRepository.SyncFrom only syncs what is in mask?
            // Assuming SyncFrom implementation obeys mask.
            Assert.False(view.HasComponent<TransientState>(e));
        }

        [Fact]
        public void SharedSnapshotProvider_TooManyReleases_Throws()
        {
            using var provider = CreateProvider();
            var view = provider.AcquireView();
            
            provider.ReleaseView(view);
            Assert.Throws<InvalidOperationException>(() => provider.ReleaseView(view));
        }

        [Fact]
        public void SharedSnapshotProvider_ViewValidAcrossFrames()
        {
            using var provider = CreateProvider();
            var view = provider.AcquireView();
            
            // Simulate time passing in live world (ticks)
            for (int i = 0; i < 10; i++)
            {
                _liveWorld.Tick();
            }
            
            // View should still be readable and valid
            Assert.NotNull(view);
            // Access property
            // view is EntityRepository or ISimulationView
            Assert.Equal(1u, ((EntityRepository)view).GlobalVersion); // It captured version 1 (initially)
        }
    }
}
