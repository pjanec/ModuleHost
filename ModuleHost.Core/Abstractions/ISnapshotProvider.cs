// File: ModuleHost.Core/Abstractions/ISnapshotProvider.cs

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Defines how a module acquires read-only views of simulation state.
    /// Implementations provide different strategies (GDB, SoD, Shared).
    /// </summary>
    public interface ISnapshotProvider
    {
        /// <summary>
        /// Provider type (for diagnostics and routing).
        /// </summary>
        SnapshotProviderType ProviderType { get; }
        
        /// <summary>
        /// Acquires a read-only view of the simulation state.
        /// 
        /// Lifecycle:
        /// - GDB: Returns persistent EntityRepository (zero-copy)
        /// - SoD: Acquires from pool, syncs from live, returns snapshot
        /// - Shared: Increments ref count, returns shared snapshot
        /// 
        /// MUST call ReleaseView when done (even for GDB).
        /// </summary>
        /// <returns>Read-only simulation view</returns>
        ISimulationView AcquireView();
        
        /// <summary>
        /// Releases a previously acquired view.
        /// 
        /// Behavior:
        /// - GDB: No-op (persistent replica)
        /// - SoD: Returns to pool for reuse
        /// - Shared: Decrements ref count, releases when count = 0
        /// 
        /// CRITICAL: Always call this, even if view wasn't used.
        /// </summary>
        void ReleaseView(ISimulationView view);
        
        /// <summary>
        /// Updates the provider state (called at sync point).
        /// 
        /// - GDB: Syncs replica from live world
        /// - SoD: No-op (sync happens on acquire)
        /// - Shared: Syncs shared snapshot
        /// </summary>
        void Update();
    }
    
    /// <summary>
    /// Provider strategy type.
    /// </summary>
    public enum SnapshotProviderType
    {
        /// <summary>Global Double Buffering (persistent replica)</summary>
        GDB,
        
        /// <summary>Snapshot-on-Demand (pooled snapshots)</summary>
        SoD,
        
        /// <summary>Shared snapshot (convoy pattern)</summary>
        Shared
    }
}
