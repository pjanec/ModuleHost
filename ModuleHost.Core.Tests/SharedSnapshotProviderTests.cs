using System;
using System.Reflection;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests
{
    public class SharedSnapshotProviderTests
    {
        [EventId(103)]
        public struct TestEvent { public int Value; }
        #pragma warning disable 649
        struct Pos { public int X; }
        #pragma warning restore 649

        private int GetRefCount(SharedSnapshotProvider provider)
        {
            var field = typeof(SharedSnapshotProvider).GetField("_referenceCount", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)field!.GetValue(provider)!;
        }

        private EntityRepository? GetInternalSnapshot(SharedSnapshotProvider provider)
        {
            var field = typeof(SharedSnapshotProvider).GetField("_sharedSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            return (EntityRepository?)field!.GetValue(provider);
        }

        [Fact]
        public void FirstAcquire_CreatesSharedSnapshot()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 2);
            
            Assert.Null(GetInternalSnapshot(provider));
            
            var view = provider.AcquireView();
            Assert.NotNull(view);
            Assert.NotNull(GetInternalSnapshot(provider));
            Assert.Equal(1, GetRefCount(provider));
            Assert.True(view is EntityRepository);
        }

        [Fact]
        public void SecondAcquire_ReusesSharedSnapshot()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 2);
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            
            Assert.Same(view1, view2);
            Assert.Equal(2, GetRefCount(provider));
        }

        [Fact]
        public void ReferenceCount_IncrementedOnAcquire()
        {
             using var live = new EntityRepository();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 3);
            
            provider.AcquireView();
            provider.AcquireView();
            provider.AcquireView();
            
            Assert.Equal(3, GetRefCount(provider));
        }

        [Fact]
        public void ReferenceCount_DecrementedOnRelease()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 2);
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            var view3 = provider.AcquireView();
            
            provider.ReleaseView(view1);
            provider.ReleaseView(view2);
            
            Assert.Equal(1, GetRefCount(provider));
            Assert.NotNull(GetInternalSnapshot(provider)); // Still alive
        }

        [Fact]
        public void LastRelease_DisposesSnapshot()
        {
            using var live = new EntityRepository();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 2);
            
            var view1 = provider.AcquireView();
            var view2 = provider.AcquireView();
            
            provider.ReleaseView(view1);
            provider.ReleaseView(view2);
            
            Assert.Equal(0, GetRefCount(provider));
            Assert.Null(GetInternalSnapshot(provider)); // Should be null/disposed
            
            // To be sure it's disposed, we could check IsDisposed on EntityRepository if exposed.
            // But checking null is enough based on implementation.
        }

        [Fact]
        public void Update_SyncsSharedSnapshot()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<Pos>();
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            using var provider = new SharedSnapshotProvider(live, acc, mask, 2);
            
            // Activate snapshot
            var view = provider.AcquireView(); 
            var repo = (EntityRepository)view;
            repo.RegisterComponent<Pos>();

            Assert.Equal(1u, repo.GlobalVersion); // Assuming live was 1
            
            // Advance live
            live.Tick(); // 2
            
            // Act: Update
            provider.Update();
            
            // Verify
            Assert.Equal(2u, repo.GlobalVersion);
        }
    }
}
