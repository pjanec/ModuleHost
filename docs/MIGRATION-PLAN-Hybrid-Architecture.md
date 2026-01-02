# Migration Plan: SoD → Hybrid GDB+SoD Architecture

**Date:** January 4, 2026  
**Status:** APPROVED - Requirements Evolution  
**Priority:** P0 - Architectural Foundation

---

## Executive Summary

**From:** Pure Snapshot-on-Demand (SoD)  
**To:** Hybrid Global Double Buffering (GDB) + Snapshot-on-Demand (SoD)

**Why:** 
- Simpler implementation for high-frequency modules (Recorder, Network)
- Better memory utilization (GDB for dense, SoD for sparse)
- Same flexibility, less complexity

**Impact:** Significant but manageable - mostly additive changes to FDP, interface simplification for ModuleHost

---

## Change Summary

### Architecture Changes

| Aspect | Old (Pure SoD) | New (Hybrid GDB+SoD) |
|--------|----------------|----------------------|
| **Core Strategy** | Single SoD for all | GDB for fast, SoD for slow |
| **World Topology** | 1 Live + Snapshots | 1 Live + 2 Replicas (Fast/Slow) |
| **Primary API** | `ISimWorldSnapshot` | `ISimulationView` (simpler) |
| **Snapshot Creation** | `SnapshotManager.CreateSnapshot()` | `EntityRepository.SyncFrom()` |
| **Event History** | In snapshot object | `EventAccumulator` + Bus injection |
| **Module API** | `Tick(snapshot)` | `Run(view)` or `Tick(factory)` |

---

## Detailed Changes by Component

### 1. FDP Kernel Changes

#### 1.1 NEW: `EntityRepository.SyncFrom()`

**File:** `Fdp.Kernel/EntityRepository.Sync.cs` (NEW)

```csharp
public sealed partial class EntityRepository
{
    /// <summary>
    /// Synchronizes this repository to match the source.
    /// Used for both GDB (full sync) and SoD (filtered sync).
    /// </summary>
    public void SyncFrom(EntityRepository source, BitMask256? mask = null)
    {
        // 1. Sync EntityIndex (metadata)
        _entityIndex.SyncFrom(source._entityIndex);
        
        // 2. Sync component tables
        foreach (var typeId in _componentTables.Keys)
        {
            if (mask.HasValue && !mask.Value.IsSet(typeId)) continue;
            
            var myTable = _componentTables[typeId];
            var srcTable = source._componentTables[typeId];
            
            if (myTable is IUnmanagedComponentTable unmanaged)
            {
                ((NativeChunkTable)unmanaged).SyncDirtyChunks((NativeChunkTable)srcTable);
            }
            else
            {
                ((ManagedComponentTable)myTable).SyncDirtyChunks((ManagedComponentTable)srcTable);
            }
        }
        
        // 3. Sync version
        this._globalVersion = source._globalVersion;
    }
}
```

**Priority:** P0 (Week 1)  
**Tests:** 8 tests

---

#### 1.2 NEW: `NativeChunkTable.SyncDirtyChunks()`

**File:** `Fdp.Kernel/NativeChunkTable.cs`

```csharp
public void SyncDirtyChunks(NativeChunkTable<T> source)
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        // Version check (optimization)
        uint srcVer = source.GetChunkVersion(i);
        if (_chunkVersions[i] == srcVer) continue;
        
        // Liveness check
        if (!source.IsChunkAllocated(i))
        {
            if (this.IsChunkAllocated(i)) this.ClearChunk(i);
            continue;
        }
        
        // The copy
        EnsureChunkAllocated(i);
        Unsafe.CopyBlock(
            this.GetChunkDataPtr(i),
            source.GetChunkDataPtr(i),
            FdpConfig.CHUNK_SIZE_BYTES
        );
        
        // Update version
        _chunkVersions[i] = srcVer;
    }
}
```

**Priority:** P0 (Week 1)  
**Tests:** 6 tests

---

#### 1.3 NEW: `EventAccumulator`

**File:** `Fdp.Kernel/EventAccumulator.cs` (NEW)

```csharp
public class EventAccumulator
{
    private readonly Queue<FrameEventData> _history = new();
    
    public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex)
    {
        var frameData = liveBus.ExtractAndRetireBuffers();
        frameData.FrameIndex = frameIndex;
        _history.Enqueue(frameData);
    }
    
    public void FlushToReplica(FdpEventBus replicaBus)
    {
        while (_history.TryDequeue(out var frameData))
        {
            foreach (var stream in frameData.NativeStreams)
            {
                replicaBus.InjectIntoCurrent(stream.TypeId, stream.GetRawBytes());
                stream.Dispose();
            }
            
            foreach (var stream in frameData.ManagedStreams)
            {
                replicaBus.InjectManagedIntoCurrent(stream.TypeId, stream.GetList());
            }
        }
    }
}
```

**Priority:** P0 (Week 2)  
**Tests:** 5 tests

---

### 2. ModuleHost Changes

#### 2.1 NEW: `ISimulationView` Interface

**File:** `ModuleHost.Core/Abstractions/ISimulationView.cs` (NEW)

**Changes from `ISimWorldSnapshot`:**
- ❌ Removed: `FrameNumber`, `FromFrame`, `SnapshotId` → Simplified to `Tick`, `Time`
- ❌ Removed: `GetEventHistory<T>()` → Replaced by `ConsumeEvents<T>()`
- ✅ Simplified: Single `GetComponentRO<T>()` for both tiers
- ✅ Added: `Query()` support

```csharp
public interface ISimulationView
{
    // Metadata
    uint Tick { get; }
    float Time { get; }
    
    // Component access (unified, read-only)
    ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
    T GetManagedComponentRO<T>(Entity e) where T : class;
    
    // Existence
    bool IsAlive(Entity e);
    
    // Events (current + history)
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    
    // Query
    EntityQueryBuilder Query();
}
```

**Implementation:** `EntityRepository` implements this natively!

---

#### 2.2 NEW: `ISnapshotProvider` Strategy Pattern

**File:** `ModuleHost.Core/Providers/ISnapshotProvider.cs` (NEW)

```csharp
public interface ISnapshotProvider : IDisposable
{
    ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    void ReleaseView(ISimulationView view);
}
```

**Implementations:**

##### A. `DoubleBufferProvider` (GDB - Fast)
```csharp
public class DoubleBufferProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly EntityRepository _replica; // Persistent
    private readonly EventAccumulator _events = new();
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
    {
        _replica.SyncFrom(_live); // GDB: ignore mask, sync all
        _events.Capture(_live.Bus);
        _events.FlushTo(_replica.Bus, lastSeenTick);
        return _replica;
    }
    
    public void ReleaseView(ISimulationView view)
    {
        // Do nothing - replica stays alive
    }
}
```

##### B. `OnDemandProvider` (SoD - Slow)
```csharp
public class OnDemandProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly ConcurrentStack<EntityRepository> _pool = new();
    private readonly EventAccumulator _events = new();
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
    {
        if (!_pool.TryPop(out var snapshot))
            snapshot = new EntityRepository();
        
        snapshot.SyncFrom(_live, mask); // SoD: filtered sync
        _events.Capture(_live.Bus);
        _events.FlushTo(snapshot.Bus, lastSeenTick);
        
        return snapshot;
    }
    
    public void ReleaseView(ISimulationView view)
    {
        var repo = (EntityRepository)view;
        repo.SoftClear();
        _pool.Push(repo);
    }
}
```

##### C. `SharedSnapshotProvider` (GDB - Convoy Pattern)
```csharp
public class SharedSnapshotProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly EntityRepository _replica;
    private readonly EventAccumulator _events = new();
    private int _activeReaders = 0;
    
    public bool TryUpdateReplica()
    {
        if (_activeReaders > 0) return false; // Locked
        
        _replica.SyncFrom(_live);
        _events.Capture(_live.Bus);
        _events.FlushTo(_replica.Bus);
        return true;
    }
    
    public ISimulationView AcquireView(...)
    {
        Interlocked.Increment(ref _activeReaders);
        return _replica;
    }
    
    public void ReleaseView(ISimulationView view)
    {
        Interlocked.Decrement(ref _activeReaders);
    }
}
```

---

#### 2.3 UPDATED: `IModule` Interface

**File:** `ModuleHost.Framework/IModule.cs`

**Option A: Minimal Change (Recommended)**
```csharp
public interface IModule
{
    ModuleDefinition GetDefinition();
    ComponentMask GetSnapshotRequirements();
    EventTypeMask GetEventRequirements();
    
    void Initialize(IModuleContext context);
    void Start();
    void Stop();
    
    void RegisterSystems(ISystemRegistry registry);
    
    // CHANGED: ISimWorldSnapshot → ISimulationView
    JobHandle Tick(
        FrameTime time,
        ISimulationView view,  // ← Changed
        ICommandBuffer commands);
    
    void DrawDiagnostics();
}
```

**Option B: Pull-Based (More Flexible)**
```csharp
public interface IModule
{
    // ... same ...
    
    // Module orchestrates its own snapshot acquisition
    void Tick(ISnapshotFactory factory, ICommandBuffer commands);
}
```

**Decision:** Use Option A (minimal breaking change)

---

### 3. ModuleHostKernel Changes

**File:** `ModuleHost.Core/ModuleHostKernel.cs`

**NEW: 3-World Topology**

```csharp
public class ModuleHostKernel
{
    // Worlds
    private EntityRepository _liveWorld;           // World A
    private EntityRepository _fastReplica;         // World B (GDB)
    private ISnapshotProvider _slowProvider;       // World C (GDB or SoD)
    
    // Providers
    private DoubleBufferProvider _fastProvider;
    
    // Modules
    private List<IModule> _fastModules = new();    // Recorder, Network
    private List<IModule> _slowModules = new();    // AI, Analytics
    
    public void RunFrame()
    {
        _liveWorld.Tick();
        
        // 1. Synchronous phases
        ExecutePhase(Phase.NetworkIngest);
        ExecutePhase(Phase.Input);
        ExecutePhase(Phase.Simulation);
        ExecutePhase(Phase.PostSimulation);
        
        // 2. FAST SYNC (Every Frame)
        var fastView = _fastProvider.AcquireView(BitMask256.All, _liveWorld.GlobalVersion);
        foreach (var mod in _fastModules)
        {
            Task.Run(() => mod.Tick(FrameTime.Current, fastView, _commandBuffer));
        }
        _fastProvider.ReleaseView(fastView);
        
        // 3. SLOW SYNC (Conditional)
        if (_slowProvider is SharedSnapshotProvider shared)
        {
            if (shared.TryUpdateReplica())
            {
                foreach (var mod in _slowModules)
                {
                    var slowView = shared.AcquireView(...);
                    Task.Run(() => {
                        try { mod.Tick(..., slowView, ...); }
                        finally { shared.ReleaseView(slowView); }
                    });
                }
            }
        }
        
        // 4. Command playback
        ProcessCommandBuffers();
        
        // 5. Export
        ExecutePhase(Phase.Export);
    }
}
```

---

## Migration Path (3 Phases)

### Phase 1: Foundation (Week 1-2)

**Goal:** Add core FDP APIs without breaking existing code

**Tasks:**
1. ✅ Add `EntityRepository.SyncFrom()` (new method, no breaking changes)
2. ✅ Add `NativeChunkTable.SyncDirtyChunks()`
3. ✅ Add `ManagedComponentTable.SyncDirtyChunks()`
4. ✅ Add `EventAccumulator` class
5. ✅ Add unit tests (25 tests total)

**Deliverable:** FDP supports synchronization without changing existing APIs

---

### Phase 2: Interface Evolution (Week 3)

**Goal:** Introduce new abstractions alongside old ones

**Tasks:**
1. ✅ Create `ISimulationView` interface
2. ✅ Make `EntityRepository` implement `ISimulationView`
3. ✅ Create `ISnapshotProvider` interface
4. ✅ Implement `DoubleBufferProvider`
5. ✅ Implement `OnDemandProvider`
6. ✅ Implement `SharedSnapshotProvider`
7. ✅ Add unit tests (15 tests total)

**Deliverable:** New strategy pattern available, old `ISimWorldSnapshot` still works

---

### Phase 3: Migration & Cleanup (Week 4)

**Goal:** Migrate ModuleHost to new architecture

**Tasks:**
1. ✅ Update `ModuleHostKernel` to use 3-world topology
2. ✅ Update `IModule.Tick()` signature: `ISimWorldSnapshot` → `ISimulationView`
3. ✅ Configure module-to-strategy mapping
4. ✅ Deprecate old `SnapshotManager` (if it exists)
5. ✅ Update all tests
6. ✅ Performance validation

**Deliverable:** Full hybrid architecture operational

---

## Compatibility Strategy

### Backward Compatibility Options

**Option A: Adapter Pattern**
```csharp
// Make ISimWorldSnapshot extend ISimulationView
public interface ISimWorldSnapshot : ISimulationView
{
    [Obsolete("Use Tick instead")]
    ulong FrameNumber { get; }
    
    [Obsolete("Use ConsumeEvents instead")]
    IEnumerable<ReadOnlySpan<T>> GetEventHistory<T>() where T : unmanaged;
}
```

**Option B: Clean Break (Recommended)**
- Just use `ISimulationView`
- Simpler, cleaner
- Breaking change is minimal (interface name + 2 method renames)

**Decision:** Clean break (we haven't implemented old interface yet)

---

## Testing Strategy

### New Tests Required

**FDP Kernel (20 tests):**
```
SyncFrom Tests (8):
✓ Full sync copies all dirty chunks
✓ Filtered sync copies only masked components
✓ Version check prevents unnecessary copies
✓ Static entities = zero bandwidth
✓ Tier 2 shallow copy works
✓ EntityIndex sync works
✓ Sync from empty source
✓ Sync to empty destination

SyncDirtyChunks Tests (6):
✓ Dirty chunk copied
✓ Clean chunk skipped
✓ Version updated correctly
✓ Chunk allocation on demand
✓ Chunk clearing when source empty
✓ Performance (memcpy 100 chunks <1ms)

EventAccumulator Tests (6):
✓ Capture single frame
✓ Capture multiple frames
✓ Flush history to replica
✓ Buffer pooling works
✓ Managed events accumulated
✓ Unmanaged events accumulated
```

**ModuleHost (15 tests):**
```
Provider Tests (12):
✓ DoubleBufferProvider syncs every call
✓ OnDemandProvider pools snapshots
✓ SharedSnapshotProvider convoy pattern
✓ Acquire/Release lifecycle
✓ Filtered mask works (SoD)
✓ Full mask works (GDB)
✓ Multiple readers on shared provider
✓ Slow reader blocks next sync
✓ Fast reader doesn't block sync
✓ Event history injected correctly
✓ Memory reuse (no leaks)
✓ Thread safety

Integration Tests (3):
✓ 3-world topology works
✓ Fast modules see every frame
✓ Slow modules see accumulated events
```

---

## Performance Validation

### Benchmarks Required

```csharp
[Benchmark]
public void SyncFrom_FullGDB_100KEntities()
{
    // Target: <2ms for 30% dirty chunks
}

[Benchmark]
public void SyncFrom_FilteredSoD_100KEntities()
{
    // Target: <500μs for 10% data
}

[Benchmark]
public void EventAccumulator_6Frames()
{
    // Target: <100μs to flush
}
```

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Breaking existing modules | Low | High | Minimal API change (interface name only) |
| Memory overhead (2 replicas) | Medium | Low | Acceptable on PC (RAM cheap) |
| Complexity increase | Medium | Medium | Strategy pattern isolates complexity |
| Performance regression | Low | High | Extensive benchmarking |

---

## Decision Log

### Why Hybrid vs Pure SoD?

**Cons of Pure SoD:**
- Recorder needs 100% of data → copying 100% anyway, might as well use GDB
- Union masks for fast modules add complexity
- ArrayPool thrashing for high-frequency modules

**Pros of Hybrid:**
- GDB for dense/fast = simpler + more stable
- SoD for sparse/slow = bandwidth efficient
- Same flexibility (can configure per-module)

**Decision:** ✅ Hybrid is better starting point

---

### Why `ISimulationView` vs `ISimWorldSnapshot`?

**Reasons:**
- Simpler (fewer properties)
- More accurate name (it's a "view", not necessarily a "snapshot")
- `EntityRepository` implements it natively (GDB doesn't need wrapper object)
- Events handled by `ConsumeEvents()` instead of special history API

**Decision:** ✅ Use `ISimulationView`

---

## Success Criteria

```
Phase 1 Complete When:
✓ FDP.Kernel has SyncFrom() API
✓ All 20 FDP tests pass
✓ No breaking changes to existing code

Phase 2 Complete When:
✓ ISimulationView interface defined
✓ All 3 providers implemented
✓ All 15 provider tests pass
✓ EntityRepository implements ISimulationView

Phase 3 Complete When:
✓ ModuleHostKernel uses 3-world topology
✓ Fast modules (Recorder/Network) on World B
✓ Slow modules (AI) on World C
✓ Full frame runs <16.67ms
✓ Event history works for slow modules
✓ No memory leaks (validated with profiler)
```

---

## Next Steps

1. ✅ **Approve this migration plan**
2. ⏳ **Update IMPLEMENTATION-SPECIFICATION.md** with new architecture
3. ⏳ **Update detailed-design-overview.md** with new interfaces
4. ⏳ **Begin Week 1 implementation** (Phase 1)

---

**Status:** ✅ READY FOR IMPLEMENTATION

**Last Updated:** January 4, 2026

---

*This migration preserves all optimizations (dirty tracking, event filtering, immutability enforcement) while adding flexibility and simplifying high-frequency module handling.*
