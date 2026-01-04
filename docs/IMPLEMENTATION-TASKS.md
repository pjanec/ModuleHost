# Implementation Task Cards - Hybrid GDB+SoD Architecture

**Project:** Module Host  
**Sprint Planning:** Week 1-4  
**Total Story Points:** 89  

---

## Epic 1: FDP Synchronization Core (Week 1-2)

**Epic Priority:** P0  
**Total Story Points:** 34  
**Dependencies:** None (foundational)

---

### TASK-001: EntityRepository.SyncFrom() API

**Priority:** P0  
**Story Points:** 8  
**Sprint:** Week 1  
**Assignee:** [Backend Team]

**Description:**
Implement the core synchronization API that enables both GDB and SoD strategies. This method copies data from a source repository to this repository, with optional component filtering.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncFrom(EntityRepository source, BitMask256? mask = null)`
- [ ] Syncs EntityIndex (entity metadata)
- [ ] Syncs component tables (filtered if mask provided)
- [ ] Syncs global version
- [ ] Full sync (mask=null) copies all dirty chunks
- [ ] Filtered sync (mask=bits) copies only specified types
- [ ] Performance: <2ms for 100K entities (30% dirty)

**Technical Notes:**
```csharp
// File: Fdp.Kernel/EntityRepository.Sync.cs (new partial class)
public sealed partial class EntityRepository
{
    public void SyncFrom(EntityRepository source, BitMask256? mask = null)
    {
        _entityIndex.SyncFrom(source._entityIndex);
        
        foreach (var typeId in _componentTables.Keys)
        {
            if (mask.HasValue && !mask.Value.IsSet(typeId)) continue;
            // Delegate to table sync
        }
        
        this._globalVersion = source._globalVersion;
    }
}
```

**Tests Required:**
- Full sync copies all dirty chunks
- Filtered sync copies only masked components
- Version check prevents redundant copies
- Static entities = zero bandwidth
- Tier 2 shallow copy works
- EntityIndex sync works
- Sync from empty source
- Sync to empty destination

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 8 tests pass
- [ ] Performance benchmark passes
- [ ] Code coverage >90%
- [ ] Documentation updated

**Related:** TASK-002, TASK-003

---

### TASK-002: NativeChunkTable.SyncDirtyChunks()

**Priority:** P0  
**Story Points:** 5  
**Sprint:** Week 1  
**Assignee:** [Backend Team]

**Description:**
Implement dirty chunk synchronization for Tier 1 (unmanaged) components. Uses version tracking to skip unchanged chunks.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncDirtyChunks(NativeChunkTable<T> source)`
- [ ] Version check prevents copying unchanged chunks
- [ ] Uses Unsafe.CopyBlock for memcpy (64KB chunks)
- [ ] Updates chunk versions after copy
- [ ] Handles liveness (clears destination if source chunk empty)
- [ ] Performance: <1ms for 1000 chunks (30% dirty)

**Technical Notes:**
```csharp
// File: Fdp.Kernel/NativeChunkTable.cs
public void SyncDirtyChunks(NativeChunkTable<T> source)
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        if (_chunkVersions[i] == source.GetChunkVersion(i))
            continue;  // Optimization
        
        Unsafe.CopyBlock(/* ... */);
        _chunkVersions[i] = source.GetChunkVersion(i);
    }
}
```

**Tests Required:**
- Dirty chunk copied
- Clean chunk skipped
- Version updated correctly
- Chunk allocation on demand
- Chunk clearing when source empty
- Performance (<1ms for 1000 chunks)

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 6 tests pass
- [ ] Performance benchmark passes
- [ ] Benchmark vs baseline (should be 3x faster than naive copy)

**Related:** TASK-001, TASK-003

---

### TASK-003: ManagedComponentTable.SyncDirtyChunks()

**Priority:** P0  
**Story Points:** 5  
**Sprint:** Week 1  
**Assignee:** [Backend Team]

**Description:**
Implement dirty chunk synchronization for Tier 2 (managed) components. Uses Array.Copy for reference copying.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncDirtyChunks(ManagedComponentTable<T> source)`
- [ ] Version check prevents redundant copies
- [ ] Uses Array.Copy for reference arrays
- [ ] Shallow copy (references, not deep clone)
- [ ] Validates immutability (Tier 2 must be records)
- [ ] Performance: <500μs for 1000 chunks (30% dirty)

**Technical Notes:**
```csharp
// File: Fdp.Kernel/ManagedComponentTable.cs
public void SyncDirtyChunks(ManagedComponentTable<T> source)
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        if (_chunkVersions[i] == source.GetChunkVersion(i))
            continue;
        
        Array.Copy(source._chunks[i], this._chunks[i], CHUNK_SIZE);
        _chunkVersions[i] = source.GetChunkVersion(i);
    }
}
```

**Tests Required:**
- Reference copy works (not deep clone)
- Immutable records enforced
- Version tracking works
- Performance passes

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 4 tests pass
- [ ] Performance benchmark passes

**Related:** TASK-001, TASK-002

---

### TASK-004: EntityIndex.SyncFrom()

**Priority:** P0  
**Story Points:** 3  
**Sprint:** Week 1  
**Assignee:** [Backend Team]

**Description:**
Sync entity metadata (IsAlive, Generation) from source to destination.

**Acceptance Criteria:**
- [ ] Copies IsAlive flags
- [ ] Copies Generation counters
- [ ] Maintains sparse structure
- [ ] Performance: <100μs for 100K entities

**Tests Required:**
- Full metadata sync
- Sparse entities handled
- Performance

**Definition of Done:**
- [ ] Code implemented
- [ ] 3 tests pass
- [ ] Performance validated

**Related:** TASK-001

---

### TASK-005: EventAccumulator Implementation

**Priority:** P0  
**Story Points:** 8  
**Sprint:** Week 2  
**Assignee:** [Backend Team]

**Description:**
Implement event accumulator that captures events from live bus and flushes to replica buses. Enables slow modules to see accumulated event history.

**Acceptance Criteria:**
- [ ] `CaptureFrame(FdpEventBus, frameIndex)` extracts and queues events
- [ ] `FlushToReplica(FdpEventBus, lastSeenTick)` injects history
- [ ] Handles both native and managed events
- [ ] Buffer pooling (no allocations per frame)
- [ ] Filters by lastSeenTick (skips old events)
- [ ] Performance: <100μs to flush 6 frames

**Technical Notes:**
```csharp
// File: Fdp.Kernel/EventAccumulator.cs
public class EventAccumulator
{
    private Queue<FrameEventData> _history = new();
    
    public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex);
    public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick);
}
```

**Tests Required:**
- Capture single frame
- Capture multiple frames
- Flush history to replica
- Buffer pooling works
- Managed events accumulated
- Unmanaged events accumulated

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 6 tests pass
- [ ] Memory profiling (no leaks)
- [ ] Performance benchmark passes

**Related:** TASK-006

---

### TASK-006: ISimulationView Interface

**Priority:** P0  
**Story Points:** 3  
**Sprint:** Week 2  
**Assignee:** [Backend Team]

**Description:**
Define the unified read-only interface for accessing simulation state. This is the core abstraction that makes modules agnostic to GDB vs SoD.

**Acceptance Criteria:**
- [ ] Interface defined in ModuleHost.Core.Abstractions
- [ ] Methods: GetComponentRO, GetManagedComponentRO, IsAlive, ConsumeEvents, Query
- [ ] Properties: Tick, Time
- [ ] No IDisposable (GDB replicas don't need disposal)
- [ ] XML documentation complete

**Technical Notes:**
```csharp
// File: ModuleHost.Core/Abstractions/ISimulationView.cs
public interface ISimulationView
{
    uint Tick { get; }
    float Time { get; }
    ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
    T GetManagedComponentRO<T>(Entity e) where T : class;
    bool IsAlive(Entity e);
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    EntityQueryBuilder Query();
}
```

**Tests Required:**
- Interface compiles
- EntityRepository can implement
- SimSnapshot can implement

**Definition of Done:**
- [ ] Interface defined
- [ ] Documentation complete
- [ ] Reviewed by team

**Related:** TASK-007

---

### TASK-007: EntityRepository Implements ISimulationView

**Priority:** P0  
**Story Points:** 2  
**Sprint:** Week 2  
**Assignee:** [Backend Team]

**Description:**
Make EntityRepository implement ISimulationView natively. This enables GDB to return the repository directly as a view.

**Acceptance Criteria:**
- [ ] `public sealed partial class EntityRepository : ISimulationView`
- [ ] All interface methods implemented
- [ ] Zero overhead (direct passthrough to existing methods)
- [ ] Tick returns GlobalVersion
- [ ] Time returns SimulationTime

**Tests Required:**
- All ISimulationView methods work
- Performance (no overhead vs direct calls)

**Definition of Done:**
- [ ] Code implemented
- [ ] Tests pass
- [ ] Performance validated (zero overhead)

**Related:** TASK-006

---

## Epic 2: Snapshot Providers (Week 3)

**Epic Priority:** P0  
**Total Story Points:** 34  
**Dependencies:** Epic 1 (FDP Core)

---

### TASK-008: ISnapshotProvider Interface

**Priority:** P0  
**Story Points:** 2  
**Sprint:** Week 3  
**Assignee:** [Backend Team]

**Description:**
Define the strategy pattern interface for acquiring/releasing simulation views.

**Acceptance Criteria:**
- [ ] Interface defined in ModuleHost.Core.Providers
- [ ] Methods: AcquireView, ReleaseView
- [ ] IDisposable for cleanup
- [ ] XML documentation complete

**Technical Notes:**
```csharp
public interface ISnapshotProvider : IDisposable
{
    ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    void ReleaseView(ISimulationView view);
}
```

**Definition of Done:**
- [ ] Interface defined
- [ ] Documentation complete

**Related:** TASK-009, TASK-010, TASK-011

---

### TASK-009: DoubleBufferProvider Implementation

**Priority:** P0  
**Story Points:** 8  
**Sprint:** Week 3  
**Assignee:** [Backend Team]

**Description:**
Implement GDB provider (persistent replica, synced every frame).

**Acceptance Criteria:**
- [ ] Maintains persistent EntityRepository replica
- [ ] AcquireView() syncs from live, flushes events, returns replica
- [ ] ReleaseView() is no-op (replica stays alive)
- [ ] EventAccumulator integrated
- [ ] Performance: <2ms per AcquireView

**Tests Required:**
- Syncs every AcquireView()
- Events accumulated correctly
- Replica persists across calls
- Performance passes

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 4 tests pass
- [ ] Performance benchmark passes

**Related:** TASK-008

---

### TASK-010: OnDemandProvider Implementation

**Priority:** P0  
**Story Points:** 8  
**Sprint:** Week 3  
**Assignee:** [Backend Team]

**Description:**
Implement SoD provider (pooled snapshots, filtered sync).

**Acceptance Criteria:**
- [ ] Maintains ConcurrentStack pool of EntityRepository instances
- [ ] AcquireView() gets pooled snapshot, filtered sync, returns it
- [ ] ReleaseView() clears and returns to pool
- [ ] EventAccumulator integrated
- [ ] Performance: <500μs for filtered sync (50% data)

**Tests Required:**
- Pooling works (snapshots reused)
- Filtered mask applied
- Events accumulated
- Memory lifecycle (no leaks)
- Performance passes

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 5 tests pass
- [ ] Performance benchmark passes
- [ ] Memory profiling clean

**Related:** TASK-008

---

### TASK-011: SharedSnapshotProvider Implementation

**Priority:** P1 (Nice to have)  
**Story Points:** 10  
**Sprint:** Week 3  
**Assignee:** [Backend Team]

**Description:**
Implement GDB provider with convoy pattern (multiple slow modules share one replica).

**Acceptance Criteria:**
- [ ] Maintains shared replica
- [ ] TryUpdateReplica() checks _activeReaders lock
- [ ] AcquireView() increments reader count (Interlocked)
- [ ] ReleaseView() decrements reader count
- [ ] Thread-safe (multiple concurrent readers)
- [ ] Convoy pattern: slowest reader blocks next sync

**Tests Required:**
- Convoy pattern works (slow reader blocks sync)
- Thread safety (concurrent readers)
- Reader count correct
- Events accumulated
- Performance

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 5 tests pass
- [ ] Thread safety validated
- [ ] Performance benchmark passes

**Related:** TASK-008

---

### TASK-012: Provider Integration Tests

**Priority:** P0  
**Story Points:** 5  
**Sprint:** Week 3  
**Assignee:** [QA Team]

**Description:**
End-to-end tests for all three providers.

**Tests Required:**
- All providers work with same module code
- Event accumulation consistent across providers
- Performance comparison (GDB vs SoD)

**Definition of Done:**
- [ ] 3 integration tests pass
- [ ] Performance report generated

**Related:** TASK-009, TASK-010, TASK-011

---

## Epic 3: ModuleHost Integration (Week 4)

**Epic Priority:** P0  
**Total Story Points:** 21  
**Dependencies:** Epic 2 (Providers)

---

### TASK-013: 3-World Topology in ModuleHostKernel

**Priority:** P0  
**Story Points:** 8  
**Sprint:** Week 4  
**Assignee:** [Backend Team]

**Description:**
Refactor ModuleHostKernel to support 3-world topology (Live + Fast GDB + Slow SoD/GDB).

**Acceptance Criteria:**
- [ ] Allocate _liveWorld, _fastReplica EntityRepositories
- [ ] Create _fastProvider (DoubleBufferProvider)
- [ ] Create _slowProvider (OnDemandProvider or SharedSnapshotProvider)
- [ ] Separate _fastModules and _slowModules lists
- [ ] RunFrame() orchestrates fast/slow sync

**Technical Notes:**
```csharp
public class ModuleHostKernel
{
    private EntityRepository _liveWorld;
    private EntityRepository _fastReplica;
    
    private DoubleBufferProvider _fastProvider;
    private ISnapshotProvider _slowProvider;
    
    private List<IModule> _fastModules = new();
    private List<IModule> _slowModules = new();
}
```

**Tests Required:**
- 3 worlds allocated correctly
- Fast modules dispatched every frame
- Slow modules dispatched conditionally
- Command buffers processed

**Definition of Done:**
- [ ] Code implemented and reviewed
- [ ] All 4 tests pass
- [ ] Architecture diagram updated

**Related:** TASK-014, TASK-015

---

### TASK-014: Module-to-Strategy Configuration

**Priority:** P0  
**Story Points:** 5  
**Sprint:** Week 4  
**Assignee:** [Backend Team]

**Description:**
Implement configuration API for assigning modules to strategies.

**Acceptance Criteria:**
- [ ] `RegisterModule(IModule, ISnapshotProvider)` method
- [ ] Auto-assignment based on frequency threshold
- [ ] Configuration validation
- [ ] Default strategy assignment

**Technical Notes:**
```csharp
host.RegisterModule(new RecorderModule(), fastProvider);
host.RegisterModule(new AiModule(), slowProvider);

// Or auto-assign
host.UseAutoStrategy(fastThreshold: 30);  // >=30Hz → GDB
```

**Tests Required:**
- Manual assignment works
- Auto-assignment works
- Validation catches errors

**Definition of Done:**
- [ ] Code implemented
- [ ] All 3 tests pass
- [ ] Configuration examples documented

**Related:** TASK-013

---

### TASK-015: IModule.Tick() Signature Update

**Priority:** P0  
**Story Points:** 3  
**Sprint:** Week 4  
**Assignee:** [Backend Team]

**Description:**
Update IModule interface to use ISimulationView instead of ISimWorldSnapshot.

**Acceptance Criteria:**
- [ ] Interface updated: `JobHandle Tick(FrameTime, ISimulationView, ICommandBuffer)`
- [ ] All example modules updated
- [ ] Breaking change documented

**Tests Required:**
- Interface compiles
- Example modules compile

**Definition of Done:**
- [ ] Interface updated
- [ ] Examples updated
- [ ] Migration guide updated

**Related:** TASK-013

---

### TASK-016: End-to-End Integration Test

**Priority:** P0  
**Story Points:** 5  
**Sprint:** Week 4  
**Assignee:** [QA Team]

**Description:**
Full system integration test with multiple modules on different strategies.

**Test Scenario:**
- Recorder (GDB, 60Hz) + Network (GDB, 60Hz) on World B
- AI (SoD, 10Hz) + Analytics (SoD, 5Hz) on World C
- Run for 180 frames (3 seconds)
- Verify all modules see correct data
- Verify event accumulation works for slow modules
- Verify performance targets met

**Acceptance Criteria:**
- [ ] Full frame cycle <16.67ms
- [ ] Fast modules see every frame
- [ ] Slow modules see accumulated events
- [ ] No data corruption
- [ ] No memory leaks

**Definition of Done:**
- [ ] Integration test passes
- [ ] Performance report generated
- [ ] Memory profiling clean

**Related:** All tasks

---

## Supporting Tasks

### TASK-017: Update Documentation

**Priority:** P1  
**Story Points:** 3  
**Sprint:** Week 4  
**Assignee:** [Tech Writer]

**Tasks:**
- [ ] Update API reference
- [ ] Update architecture diagrams
- [ ] Create migration guide for existing modules
- [ ] Code examples for each provider

**Definition of Done:**
- [ ] All docs updated
- [ ] Peer reviewed
- [ ] Published

---

### TASK-018: Performance Benchmarking Suite

**Priority:** P1  
**Story Points:** 5  
**Sprint:** Week 4  
**Assignee:** [Performance Team]

**Benchmarks:**
- [ ] SyncFrom() full vs filtered
- [ ] SyncDirtyChunks() vs naive copy
- [ ] EventAccumulator flush
- [ ] Provider AcquireView() overhead
- [ ] Full frame throughput

**Definition of Done:**
- [ ] Benchmark suite implemented
- [ ] Baseline measurements recorded
- [ ] Performance report published

---

## Summary

**Total Tasks:** 18  
**Total Story Points:** 89  

**Week 1:** Tasks 1-4 (21 SP) - FDP Core Part 1  
**Week 2:** Tasks 5-7 (13 SP) - FDP Core Part 2  
**Week 3:** Tasks 8-12 (34 SP) - Providers  
**Week 4:** Tasks 13-18 (21 SP) - Integration

**Risk Mitigation:**
- Weekly checkpoints
- Performance validation each week
- Integration tests starting Week 3
- Buffer time in Week 4 for issues

---

**TaskTracking Template:**
```
TASK-XXX: [Title]
Status: [ ] TODO | [ ] IN PROGRESS | [ ] REVIEW | [ ] DONE
Assignee: [Name]
Sprint: Week X
Story Points: X
Blocked By: [Task IDs]
Notes: [Any issues, decisions]
```

---

*Last Updated: January 4, 2026*
