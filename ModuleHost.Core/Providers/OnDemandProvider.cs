// File: ModuleHost.Core/Providers/OnDemandProvider.cs
using System;
using System.Collections.Concurrent;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Snapshot-on-Demand provider.
    /// Maintains pool of EntityRepository snapshots.
    /// Acquires from pool, syncs with mask, releases back to pool.
    /// </summary>
    public sealed class OnDemandProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256 _componentMask;
        private readonly ConcurrentStack<EntityRepository> _pool;
        private uint _lastSeenTick;
        
        private readonly Action<EntityRepository>? _schemaSetup;
        
        public OnDemandProvider(
            EntityRepository liveWorld, 
            EventAccumulator eventAccumulator,
            BitMask256 componentMask,
            Action<EntityRepository>? schemaSetup = null,
            int initialPoolSize = 5)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _componentMask = componentMask;
            _schemaSetup = schemaSetup;
            _pool = new ConcurrentStack<EntityRepository>();
            
            // Warmup: Pre-allocate snapshots from config
            WarmupPool(initialPoolSize);
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.SoD;
        
        /// <summary>
        /// Update is no-op for SoD (sync happens on acquire).
        /// </summary>
        public void Update()
        {
            // SoD: Sync happens on-demand during AcquireView
            // Update tick for event filtering
            _lastSeenTick = _liveWorld.GlobalVersion;
        }
        
        /// <summary>
        /// Acquires snapshot from pool, syncs with mask.
        /// Thread-safe: Can be called from module threads.
        /// </summary>
        public ISimulationView AcquireView()
        {
            // Try pop from pool
            if (!_pool.TryPop(out var snapshot))
            {
                // Pool empty, create new
                snapshot = CreateSnapshot();
            }
            
            // Sync from live world (with component mask filtering)
            snapshot.SyncFrom(_liveWorld, _componentMask);
            
            // Flush event history (only events after lastSeenTick)
            // Note: If multiple modules acquire consecutively, lastSeenTick might need management per module?
            // The instructions say "FlushToReplica(snapshot.Bus, _lastSeenTick)".
            // This implies _lastSeenTick is global for the provider (updated at sync point).
            // This is correct as long as Update() is called every frame before modules run.
            _eventAccumulator.FlushToReplica(snapshot.Bus, _lastSeenTick);
            
            return snapshot;
        }
        
        /// <summary>
        /// Returns snapshot to pool (after soft clear).
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            // Unsafe cast because we know we issued it
            if (view is EntityRepository snapshot)
            {
                // Soft clear (reset state, don't deallocate)
                // Assuming EntityRepository has SoftClear() or similar.
                // Looking at EntityRepository.cs, I saw Clear() but not sure about SoftClear.
                // Wait, BATCH-01 mentioned SoftClear is typically `Clear()` but keeping buffers.
                // The provided code snippet in instructions calls `SoftClear()`.
                // Does EntityRepository have `SoftClear()`?
                // I need to check. If not, I should use `Clear()`.
                // Checking EntityRepository.cs: line 265 `internal void Clear()`.
                // It is internal. I might need a public helper or expose it.
                // But `ModuleHost.Core` might define an extension?
                // Or maybe I should implement it or use `Clear()` if accessible.
                
                // If SoftClear is missing, I might need to add it or use Reflection via UnsafeShim or similar hack, 
                // OR ask to add it.
                // EntityRepository.Clear() is internal.
                // However, `SyncFrom` usually overwrites. But we want to reuse buffers.
                // `SyncFrom` handles clearing if needed or overwrite.
                
                // Releasing view implies we are done with it. 
                // To be safe and avoid holding old references, we should clear.
                
                // Let's assume for now I can call `SoftClear()` (maybe implemented in partial class elsewhere or I need to add it).
                // Or maybe it's just `Clear()` if I can change visibility.
                
                // I will try to call `SoftClear()` as instructed. If compiles fail, I'll investigate.
                
                snapshot.SoftClear();
                
                // Return to pool for reuse
                _pool.Push(snapshot);
            }
            else
            {
                throw new ArgumentException("View is not an EntityRepository (SoD provider issue)");
            }
        }
        
        private void WarmupPool(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var snapshot = CreateSnapshot();
                _pool.Push(snapshot);
            }
        }
        
        private EntityRepository CreateSnapshot()
        {
            var snapshot = new EntityRepository();
            
            // TODO: Register component types matching live world schema
            // For now, assume schema set up externally or lazily via SyncFrom (if SyncFrom handles registration, which it likely doesn't for unmanaged)
            _schemaSetup?.Invoke(snapshot);
            
            return snapshot;
        }
        
        public void Dispose()
        {
            // Dispose all pooled snapshots
            while (_pool.TryPop(out var snapshot))
            {
                snapshot.Dispose();
            }
        }
    }
}
