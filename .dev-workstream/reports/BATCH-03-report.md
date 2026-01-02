# BATCH-03 Review: Snapshot Providers

## Summary
Implements the Snapshot Provider strategy pattern for BATCH-03, enabling flexible simulation view acquisition for modules.

## Features Implemented
1. **ISnapshotProvider Interface** (`TASK-008`)
   - Defined strategy pattern (`AcquireView`, `ReleaseView`, `Update`).
   - `SnapshotProviderType` enum (GDB, SoD, Shared).

2. **DoubleBufferProvider (GDB)** (`TASK-009`)
   - Implemented Global Double Buffering.
   - Maintains persistent replica `EntityRepository`.
   - `Update()` syncs replica from live world.
   - `AcquireView()` returns replica (Reference Copy/Zero Overhead).

3. **OnDemandProvider (SoD)** (`TASK-010`)
   - Implemented Snapshot-on-Demand with pooling.
   - Uses `ConcurrentStack<EntityRepository>` for thread-safe pooling.
   - `AcquireView()` creates or reuses snapshot, syncs with component mask.
   - `ReleaseView()` soft-clears and returns to pool.
   - Added `schemaSetup` delegate to support initializing snapshot schema (tables).

4. **SharedSnapshotProvider (Shared)** (`TASK-011`)
   - Implemented Convoy pattern (Shared View).
   - Reference counting ensures single snapshot shared among modules in a phase/frame.
   - Thread-safe `Acquire`/`Release` using locks.
   - Disposes/Recycles snapshot when reference count drops to zero.

5. **EntityRepository Extensions**
   - Added `SoftClear()` to `EntityRepository` to support efficient reuse without deallocation.

## Design Decisions
- **Schema Synchronization**: To handle the requirement that `SyncFrom` expects destination tables to exist, added an optional `Action<EntityRepository> schemaSetup` to providers. This allows initialization of component tables on created snapshots.
- **Reference Counting**: Used `lock` instead of purely `Interlocked` in `SharedSnapshotProvider` to prevent race conditions during the "Release and Dispose" phase.
- **Pooling**: Used `ConcurrentStack` for SoD. While it allocates nodes, the heavyweight `EntityRepository` is reused, satisfying the primary zero-allocation goal for simulation state.

## Testing
- **Unit Tests**: comprehensive tests for each provider (`DoubleBufferProviderTests`, `OnDemandProviderTests`, `SharedSnapshotProviderTests`) covering lifecycle, syncing, and event flushing.
- **Integration Tests**: `ProviderIntegrationTests` verifies that all provider types work correctly with actual entities and components, including filtering logic (SoD) and sharing logic (Shared).
- **Status**: 25 Tests Passed.

## Performance
- **Zero Allocation**: Views are reused (Pool or Persistent). Event history uses pooled buffers (`EventAccumulator` from BATCH-02).
- **Thread Safety**: Providers are thread-safe for parallel module execution.

## Next Steps
- Proceed to **BATCH-04**: Module System & Host Logic.
- Implement `ModuleHost` to use these providers.
