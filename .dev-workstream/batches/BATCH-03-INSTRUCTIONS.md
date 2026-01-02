# BATCH-03: Snapshot Providers - Strategy Pattern

**Phase:** Week 3 - Strategy Pattern Implementation  
**Difficulty:** High  
**Story Points:** 33  
**Estimated Duration:** 5-6 days  
**Dependencies:** BATCH-01 (SyncFrom), BATCH-02 (EventAccumulator, ISimulationView)

---

## ⚠️ IMPORTANT: Read Basic Instructions First!

**Before starting this batch, review:**

📖 **`.dev-workstream/README.md`** - Your complete workflow guide including:
- Definition of Done (DoD) checklist
- Critical rules (NO warnings, ALL tests must pass, etc.)
- Code review checklist
- Communication protocols

**This is mandatory for every batch!** The README contains essential guidelines that apply to all your work.

---

## 📋 Batch Overview

This batch implements the **Strategy Pattern** for snapshot provisioning. You will create three provider implementations that abstract how modules get their simulation views:

1. **DoubleBufferProvider** - GDB strategy (persistent replica)
2. **OnDemandProvider** - SoD strategy (pooled snapshots)
3. **SharedSnapshotProvider** - Convoy pattern (multiple modules share one snapshot)

**Critical Success Factors:**
- Clean abstraction through ISnapshotProvider interface
- GDB provider must be zero-overhead (returns EntityRepository directly)
- SoD provider must use pooling (no allocations per frame)
- Convoy provider must handle reference counting correctly
- All providers must work with EventAccumulator

---

## 📚 Required Reading

**Before starting, read these documents:**

1. **Primary References:**
   - `/docs/API-REFERENCE.md` - Sections: ISnapshotProvider, DoubleBufferProvider, OnDemandProvider
   - `/docs/HYBRID-ARCHITECTURE-QUICK-REFERENCE.md` - Strategy pattern diagram
   - `/docs/detailed-design-overview.md` - Layer 1: Snapshot Providers

2. **Design Context:**
   - `/docs/IMPLEMENTATION-SPECIFICATION.md` - Section: Snapshot Provider Pattern
   - `/docs/IMPLEMENTATION-TASKS.md` - Tasks 008-011
   - `/docs/reference-archive/FDP-GDB-SoD-unified.md` - Provider strategies

3. **Architecture Diagrams:**
   - `strategy_pattern_flow.png` - Provider pattern visualization
   - `hybrid_architecture_topology.png` - 3-world topology

**Key Concepts to Understand:**
- Strategy pattern: ISnapshotProvider abstracts GDB vs SoD
- GDB: Zero-copy (return EntityRepository as ISimulationView)
- SoD: Pooling (acquire/release from pool)
- Convoy: Multiple modules share one snapshot (reference counting)
- Provider lifecycle: AcquireView → Module.Tick() → ReleaseView

---

## 🎯 Tasks in This Batch

### TASK-008: ISnapshotProvider Interface (5 SP)

**Priority:** P0 (Critical Path)  
**File:** `ModuleHost.Core/Abstractions/ISnapshotProvider.cs` (new)

**Description:**  
Define the strategy interface that abstracts how modules acquire simulation views. This decouples modules from the underlying GDB/SoD implementation.

**Acceptance Criteria:**
- [ ] Interface defined with methods: `AcquireView()`, `ReleaseView(ISimulationView)`
- [ ] Property: `ProviderType` (enum: GDB, SoD, Shared)
- [ ] Complete XML documentation explaining lifecycle
- [ ] Thread-safety requirements documented
- [ ] Compiles without errors

**Implementation:**

```csharp
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
```

**Tests Required (3 tests):**

Create file: `ModuleHost.Core.Tests/ISnapshotProviderTests.cs`

1. **Interface_Compiles**
   - Verify: ISnapshotProvider compiles

2. **Interface_HasAllMembers**
   - Verify: AcquireView, ReleaseView, Update, ProviderType present

3. **ProviderType_EnumHasAllValues**
   - Verify: GDB, SoD, Shared enum values exist

---

### TASK-009: DoubleBufferProvider (GDB) (8 SP)

**Priority:** P0 (Core GDB strategy)  
**File:** `ModuleHost.Core/Providers/DoubleBufferProvider.cs` (new)

**Description:**  
Implements Global Double Buffering strategy. Maintains a persistent replica that's synced every frame. Returns the EntityRepository directly as ISimulationView (zero overhead).

**Acceptance Criteria:**
- [ ] Class implements ISnapshotProvider
- [ ] Constructor accepts live EntityRepository and EventAccumulator
- [ ] Maintains persistent replica (EntityRepository)
- [ ] Update() syncs replica from live (full sync, no mask)
- [ ] Update() flushes event history via EventAccumulator
- [ ] AcquireView() returns replica cast to ISimulationView (zero-copy)
- [ ] ReleaseView() is no-op (replica persists)
- [ ] Thread-safety: Update() called on main thread, AcquireView() on module thread
- [ ] Performance: <2ms sync for 100K entities

**Implementation:**

```csharp
// File: ModuleHost.Core/Providers/DoubleBufferProvider.cs
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
        private uint _lastSyncTick;
        
        public DoubleBufferProvider(EntityRepository liveWorld, EventAccumulator eventAccumulator)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            
            // Create persistent replica
            _replica = new EntityRepository();
            
            // TODO: Register all component types that live world has
            // This ensures replica has matching schema
            // For now, assume schema is set up externally
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.GDB;
        
        /// <summary>
        /// Updates replica to match live world.
        /// Called on main thread at sync point (after simulation, before module dispatch).
        /// </summary>
        public void Update()
        {
            // Full sync (no mask - GDB copies everything)
            _replica.SyncFrom(_liveWorld);
            
            // Flush event history
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
            // We could validate that view == _replica, but skip for performance
        }
        
        public void Dispose()
        {
            _replica?.Dispose();
        }
    }
}
```

**Tests Required (6 tests):**

Create file: `ModuleHost.Core.Tests/DoubleBufferProviderTests.cs`

1. **Constructor_CreatesReplica**
   - Verify: Replica EntityRepository created

2. **Update_SyncsReplicaFromLive**
   - Setup: Live world with entities
   - Execute: provider.Update()
   - Verify: Replica matches live world

3. **Update_FlushesEventHistory**
   - Setup: EventAccumulator with history
   - Execute: provider.Update()
   - Verify: Replica bus has accumulated events

4. **AcquireView_ReturnsReplica**
   - Execute: var view = provider.AcquireView()
   - Verify: view is ISimulationView
   - Verify: view.Tick == replica.GlobalVersion

5. **AcquireView_ZeroCopy**
   - Execute: var view1 = provider.AcquireView()
   - Execute: var view2 = provider.AcquireView()
   - Verify: ReferenceEquals(view1, view2) (same replica)

6. **ReleaseView_NoOp**
   - Execute: provider.ReleaseView(view)
   - Verify: No exception, replica still usable

---

### TASK-010: OnDemandProvider (SoD) (12 SP)

**Priority:** P0 (Core SoD strategy)  
**File:** `ModuleHost.Core/Providers/OnDemandProvider.cs` (new)

**Description:**  
Implements Snapshot-on-Demand strategy. Maintains a pool of EntityRepository instances. On acquire, pulls from pool, syncs with component mask, returns. On release, returns to pool.

**Acceptance Criteria:**
- [ ] Class implements ISnapshotProvider
- [ ] Constructor accepts live world, EventAccumulator, and BitMask256 (component filter)
- [ ] Maintains pool of EntityRepository (ConcurrentStack or similar)
- [ ] AcquireView() pops from pool (or creates new if empty)
- [ ] AcquireView() syncs with mask filtering
- [ ] AcquireView() flushes event history
- [ ] ReleaseView() returns to pool (SoftClear first)
- [ ] Update() is no-op (SoD syncs on acquire, not at sync point)
- [ ] Pool warmup strategy (pre-allocate 2-3 snapshots)
- [ ] Performance: <500μs sync for filtered components

**Implementation:**

```csharp
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
        
        public OnDemandProvider(
            EntityRepository liveWorld, 
            EventAccumulator eventAccumulator,
            BitMask256 componentMask)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _componentMask = componentMask;
            _pool = new ConcurrentStack<EntityRepository>();
            
            // Warmup: Pre-allocate 2 snapshots to avoid first-run allocation
            WarmupPool(2);
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
            _eventAccumulator.FlushToReplica(snapshot.Bus, _lastSeenTick);
            
            return snapshot;
        }
        
        /// <summary>
        /// Returns snapshot to pool (after soft clear).
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            if (view is EntityRepository snapshot)
            {
                // Soft clear (reset state, don't deallocate)
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
            // For now, assume schema set up externally
            
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
```

**Tests Required (8 tests):**

Create file: `ModuleHost.Core.Tests/OnDemandProviderTests.cs`

1. **Constructor_WarmsUpPool**
   - Verify: Pool has 2 snapshots after construction

2. **AcquireView_PopsFromPool**
   - Execute: var view = provider.AcquireView()
   - Verify: Pool size decreased

3. **AcquireView_CreatesNewWhenPoolEmpty**
   - Setup: Exhaust pool
   - Execute: var view = provider.AcquireView()
   - Verify: New snapshot created, sync works

4. **AcquireView_SyncsWithMask**
   - Setup: Mask includes Position only
   - Execute: var view = provider.AcquireView()
   - Verify: Snapshot has Position, not Velocity

5. **AcquireView_FlushesEventHistory**
   - Setup: EventAccumulator with history
   - Execute: var view = provider.AcquireView()
   - Verify: Snapshot bus has events

6. **ReleaseView_ReturnsToPool**
   - Execute: provider.ReleaseView(view)
   - Verify: Pool size increased

7. **ReleaseView_SoftClearsSnapshot**
   - Execute: provider.ReleaseView(view)
   - Verify: Snapshot state cleared (next acquire gets clean snapshot)

8. **PoolReuse_WorksCorrectly**
   - Execute: view1 = Acquire → Release → view2 = Acquire
   - Verify: Same snapshot reused (pool working)

---

### TASK-011: SharedSnapshotProvider (Convoy) (8 SP)

**Priority:** P1 (Optional, can defer to later batch if time constrained)  
**File:** `ModuleHost.Core/Providers/SharedSnapshotProvider.cs` (new)

**Description:**  
Implements convoy pattern where multiple modules share a single snapshot. Uses reference counting to know when all modules are done, then releases snapshot back to pool.

**Acceptance Criteria:**
- [ ] Class implements ISnapshotProvider
- [ ] Constructor accepts live world, EventAccumulator, BitMask256, and expectedModuleCount
- [ ] Maintains single shared snapshot (EntityRepository)
- [ ] AcquireView() increments reference count
- [ ] AcquireView() syncs on first acquire only
- [ ] ReleaseView() decrements reference count
- [ ] ReleaseView() returns to pool when count reaches 0
- [ ] Thread-safety: Reference counting uses Interlocked
- [ ] Update() syncs shared snapshot (if active)

**Implementation:**

```csharp
// File: ModuleHost.Core/Providers/SharedSnapshotProvider.cs
using System;
using System.Threading;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Shared snapshot provider (convoy pattern).
    /// Multiple modules share one snapshot with reference counting.
    /// </summary>
    public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256 _componentMask;
        private readonly int _expectedModuleCount;
        
        private EntityRepository? _sharedSnapshot;
        private int _referenceCount;
        private uint _lastSeenTick;
        private readonly object _syncLock = new object();
        
        public SharedSnapshotProvider(
            EntityRepository liveWorld,
            EventAccumulator eventAccumulator,
            BitMask256 componentMask,
            int expectedModuleCount)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _componentMask = componentMask;
            _expectedModuleCount = expectedModuleCount;
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.Shared;
        
        /// <summary>
        /// Update syncs shared snapshot (if active).
        /// </summary>
        public void Update()
        {
            lock (_syncLock)
            {
                if (_sharedSnapshot != null)
                {
                    // Sync shared snapshot
                    _sharedSnapshot.SyncFrom(_liveWorld, _componentMask);
                    _eventAccumulator.FlushToReplica(_sharedSnapshot.Bus, _lastSeenTick);
                    _lastSeenTick = _liveWorld.GlobalVersion;
                }
            }
        }
        
        /// <summary>
        /// Acquires shared snapshot (increments ref count).
        /// First acquire syncs, subsequent acquires reuse.
        /// </summary>
        public ISimulationView AcquireView()
        {
            lock (_syncLock)
            {
                // First acquire? Create and sync
                if (_sharedSnapshot == null)
                {
                    _sharedSnapshot = new EntityRepository();
                    // TODO: Register components
                    
                    _sharedSnapshot.SyncFrom(_liveWorld, _componentMask);
                    _eventAccumulator.FlushToReplica(_sharedSnapshot.Bus, _lastSeenTick);
                    _lastSeenTick = _liveWorld.GlobalVersion;
                }
                
                // Increment ref count (thread-safe)
                Interlocked.Increment(ref _referenceCount);
                
                return _sharedSnapshot;
            }
        }
        
        /// <summary>
        /// Releases view (decrements ref count).
        /// When count reaches 0, disposes shared snapshot.
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            int newCount = Interlocked.Decrement(ref _referenceCount);
            
            if (newCount == 0)
            {
                // Last module done, release snapshot
                lock (_syncLock)
                {
                    _sharedSnapshot?.SoftClear();
                    _sharedSnapshot?.Dispose();
                    _sharedSnapshot = null;
                }
            }
            
            if (newCount < 0)
            {
                throw new InvalidOperationException("ReleaseView called more times than AcquireView");
            }
        }
        
        public void Dispose()
        {
            _sharedSnapshot?.Dispose();
        }
    }
}
```

**Tests Required (6 tests):**

Create file: `ModuleHost.Core.Tests/SharedSnapshotProviderTests.cs`

1. **FirstAcquire_CreatesSharedSnapshot**
   - Execute: var view = provider.AcquireView()
   - Verify: Shared snapshot created and synced

2. **SecondAcquire_ReusesSharedSnapshot**
   - Execute: view1 = Acquire, view2 = Acquire
   - Verify: ReferenceEquals(view1, view2)

3. **ReferenceCount_IncrementedOnAcquire**
   - Execute: Acquire 3 times
   - Verify: Ref count = 3

4. **ReferenceCount_DecrementedOnRelease**
   - Execute: Acquire 3, Release 2
   - Verify: Ref count = 1, snapshot still alive

5. **LastRelease_DisposesSnapshot**
   - Execute: Acquire 2, Release 2
   - Verify: Ref count = 0, snapshot disposed

6. **Update_SyncsSharedSnapshot**
   - Setup: Shared snapshot active
   - Execute: provider.Update()
   - Verify: Snapshot synced with latest data

---

## 🔍 Integration Tests

**After all tasks complete**, create integration test:

**File:** `ModuleHost.Core.Tests/Integration/ProviderIntegrationTests.cs`

### Integration Test: AllProvidersWork

```csharp
[Fact]
public void AllProviders_WorkWithModules()
{
    using var live = new EntityRepository();
    var accumulator = new EventAccumulator();
    
    // Register components
    live.RegisterComponent<Position>();
    live.RegisterComponent<Velocity>();
    
    // Create test entities
    for (int i = 0; i < 100; i++)
    {
        var e = live.CreateEntity();
        live.AddComponent(e, new Position { X = i });
        live.AddComponent(e, new Velocity { X = 1 });
    }
    
    // Test GDB Provider
    using var gdbProvider = new DoubleBufferProvider(live, accumulator);
    gdbProvider.Update(); // Sync replica
    
    var gdbView = gdbProvider.AcquireView();
    Assert.Equal(100u, CountEntities(gdbView));
    gdbProvider.ReleaseView(gdbView);
    
    // Test SoD Provider
    var mask = new BitMask256();
    mask.SetBit(ComponentType<Position>.ID);
    
    using var sodProvider = new OnDemandProvider(live, accumulator, mask);
    var sodView = sodProvider.AcquireView();
    Assert.Equal(100u, CountEntities(sodView));
    // Verify filtering (has Position, not Velocity)
    sodProvider.ReleaseView(sodView);
    
    // Test Shared Provider
    using var sharedProvider = new SharedSnapshotProvider(live, accumulator, mask, 2);
    var shared1 = sharedProvider.AcquireView();
    var shared2 = sharedProvider.AcquireView();
    Assert.Same(shared1, shared2); // Same snapshot
    sharedProvider.ReleaseView(shared1);
    sharedProvider.ReleaseView(shared2);
}

private uint CountEntities(ISimulationView view)
{
    uint count = 0;
    foreach (var e in view.Query().Build())
        count++;
    return count;
}
```

---

## ⚠️ Critical Rules

**Mandatory Requirements:**

1. ⛔ **Always call ReleaseView** - Even for GDB (consistency)
2. ⛔ **SoftClear before return to pool** - Prevents stale state
3. ⛔ **Reference counting must be thread-safe** - Use Interlocked
4. ⛔ **Pool must be thread-safe** - Use ConcurrentStack
5. ⛔ **No allocations per frame** - Warmup pool, reuse snapshots

**Phase-Based Execution Context:**

- Update() called on main thread (sync point)
- AcquireView() can be called from module threads
- No locking needed in Update() (phase separation)
- Locking needed in Acquire/Release (multi-threaded access)

**Performance Constraints:**

- GDB Update(): <2ms for 100K entities
- SoD AcquireView(): <500μs for filtered sync
- Shared AcquireView(): <100μs after first acquire (reuse)

---

## 📊 Success Metrics

**Batch is DONE when:**

- [x] All 4 tasks complete (TASK-008 through TASK-011)
- [x] All 23 unit tests passing (3 + 6 + 8 + 6)
- [x] 1 integration test passing
- [x] Zero compiler warnings
- [x] Performance benchmarks pass
- [x] All providers work with EventAccumulator

**Note:** If TASK-011 (SharedSnapshotProvider) is too complex, mark it P1 and defer to BATCH-04. This is acceptable - focus on getting GDB and SoD providers perfect first.

---

## 🚨 Common Pitfalls

**Watch Out For:**

1. **Forgetting ReleaseView** - Always call, even if acquire failed
2. **Pool exhaustion** - Warmup prevents first-run allocation
3. **Reference count bugs** - Test multiple acquire/release cycles
4. **Disposing active snapshots** - Check ref count before dispose
5. **Thread-safety in Shared provider** - Use Interlocked for ref count

---

## 💡 Implementation Tips

**Best Practices:**

1. **Start with TASK-008** (Interface) - Simplest, sets foundation
2. **Then TASK-009** (GDB) - Straightforward, builds confidence
3. **Then TASK-010** (SoD) - More complex, uses what you learned
4. **Finally TASK-011** (Shared) - Most complex, can defer if needed

**Testing Strategy:**

1. Test each provider independently first
2. Test pool lifecycle (warmup, acquire, release, reuse)
3. Test thread-safety (concurrent acquires)
4. Integration test validates all providers work together

**Performance:**

- Use BenchmarkDotNet for accurate measurements
- Profile if targets not met
- Pool warmup is critical (avoids first-run cost)

---

## 📋 Deliverables

**When batch complete, submit:**

1. **Batch Report:** `reports/BATCH-03-REPORT.md`
2. **Questions (if any):** `reports/BATCH-03-QUESTIONS.md`
3. **Blockers (if any):** `reports/BLOCKERS-ACTIVE.md`

**Report Must Include:**

- Status of all 4 tasks (or 3 if TASK-011 deferred)
- Test results (23 unit + 1 integration, or 17 + 1 if deferred)
- Performance measurements
- Files created/modified list
- Reasoning if TASK-011 deferred

---

## 🎯 Next Batch Preview

**BATCH-04** (following this) will implement:
- ModuleHostKernel (orchestrates modules)
- Module lifecycle management
- Provider assignment logic
- Integration with FDP

These depend on ISnapshotProvider working correctly!

---

**Questions? Create:** `reports/BATCH-03-QUESTIONS.md`  
**Blocked? Update:** `reports/BLOCKERS-ACTIVE.md`  
**Done? Submit:** `reports/BATCH-03-REPORT.md`

**Remember: Read `.dev-workstream/README.md` before starting!**

**Good luck! 🚀**
