// File: ModuleHost.Core.Tests/Integration/ProviderIntegrationTests.cs
using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core.Tests.Integration
{
    public class ProviderIntegrationTests
    {
        struct Position { public int X; }
        struct Velocity { public int X; }

        [Fact]
        public void AllProviders_WorkWithModules()
        {
            using var live = new EntityRepository();
            var accumulator = new EventAccumulator();
            
            // Register components on Live
            live.RegisterComponent<Position>();
            live.RegisterComponent<Velocity>();
            
            // Define Schema Setup for Replicas/Snapshots
            Action<EntityRepository> schemaSetup = repo => {
                repo.RegisterComponent<Position>();
                repo.RegisterComponent<Velocity>();
            };
            
            // Create test entities
            for (int i = 0; i < 100; i++)
            {
                var e = live.CreateEntity();
                live.AddComponent(e, new Position { X = i });
                live.AddComponent(e, new Velocity { X = 1 });
            }
            live.Tick(); // Needed for SyncFrom to pick up latest version
            
            // Test GDB Provider
            // GDB usually takes schemaSetup too now, though manually tested before.
            using var gdbProvider = new DoubleBufferProvider(live, accumulator, schemaSetup);
            gdbProvider.Update(); // Sync replica
            
            var gdbView = gdbProvider.AcquireView();
            Assert.Equal(100, CountEntities(gdbView));
            // Verify component data
            bool gdbHasData = false;
            gdbView.Query().Build().ForEach(e => {
                 var p = gdbView.GetComponentRO<Position>(e);
                 if (p.X >= 0) gdbHasData = true;
            });
            Assert.True(gdbHasData);
            gdbProvider.ReleaseView(gdbView);
            
            // Test SoD Provider
            var mask = new BitMask256();
            mask.SetBit(ComponentType<Position>.ID);
            
            using var sodProvider = new OnDemandProvider(live, accumulator, mask, schemaSetup);
            var sodView = sodProvider.AcquireView();
            
            Assert.Equal(100, CountEntities(sodView));
            
            // Verify filtering (has Position, not Velocity)
            bool sodHasPos = false;
            
            sodView.Query().Build().ForEach(e => {
                 var p = sodView.GetComponentRO<Position>(e);
                 if (p.X >= 0) sodHasPos = true;
            });
            Assert.True(sodHasPos);
            
            // Check Velocity missing
             var oneEntity = sodView.Query().Build().FirstOrNull();
             Assert.True(oneEntity.Index >= 0);
             
             Assert.Throws<InvalidOperationException>(() => sodView.GetComponentRO<Velocity>(oneEntity));
            
            sodProvider.ReleaseView(sodView);
            
            // Test Shared Provider (UPDATED FOR BATCH 3)
            var pool = new SnapshotPool(schemaSetup);
            using var sharedProvider = new SharedSnapshotProvider(live, accumulator, mask, pool);
            var shared1 = sharedProvider.AcquireView();
            var shared2 = sharedProvider.AcquireView();
            Assert.Same(shared1, shared2); // Same snapshot
            
            Assert.Equal(100, CountEntities(shared1));
            
            sharedProvider.ReleaseView(shared1);
            sharedProvider.ReleaseView(shared2);
        }

        private int CountEntities(ISimulationView view)
        {
            return view.Query().Build().Count();
        }
    }
}
