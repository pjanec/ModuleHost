using System;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Global Double Buffering provider.
    /// Maintains persistent replica synced every frame.
    /// Zero-copy acquisition (returns EntityRepository as ISimulationView).
    /// </summary>
    public sealed class DoubleBufferProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EntityRepository _replica;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256? _mask;
        private uint _lastSyncTick;
        
        // Constructor with Mask
        public DoubleBufferProvider(
            EntityRepository liveWorld, 
            EventAccumulator eventAccumulator, 
            BitMask256 mask,
            Action<EntityRepository>? schemaSetup = null)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _mask = mask;
            
            // Create persistent replica
            _replica = new EntityRepository();
            schemaSetup?.Invoke(_replica);
        }

        // Backward compatible constructor (Full Sync)
        public DoubleBufferProvider(
            EntityRepository liveWorld, 
            EventAccumulator eventAccumulator, 
            Action<EntityRepository>? schemaSetup = null)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _mask = null; // Implies Full Sync
            
            _replica = new EntityRepository();
            schemaSetup?.Invoke(_replica);
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.GDB;
        
        /// <summary>
        /// Updates replica to match live world.
        /// Called on main thread at sync point (after simulation, before module dispatch).
        /// </summary>
        public void Update()
        {
            // Sync using the configured mask (or default snapshotable mask if null)
            _replica.SyncFrom(_liveWorld, _mask);
            
            // Flush event history
            // We flush events that happened since the last sync
            _eventAccumulator.FlushToReplica(_replica.Bus, _lastSyncTick);
            
            // Track current tick for next flush
            _lastSyncTick = _liveWorld.GlobalVersion;
        }
        
        /// <summary>
        /// Acquires view (zero-copy, returns persistent replica).
        /// Thread-safe: Can be called from module threads.
        /// </summary>
        public ISimulationView AcquireView()
        {
            // GDB: Zero-copy, return replica directly
            // EntityRepository implements ISimulationView natively
            return _replica;
        }
        
        /// <summary>
        /// Releases view (no-op for GDB, replica persists).
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            // GDB: No-op (replica is persistent, not pooled)
        }
        
        public void Dispose()
        {
            _replica?.Dispose();
        }
    }
}
