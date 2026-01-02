# BATCH-01: FDP Core Foundation

**Phase:** Week 1 - FDP Synchronization Core (Part 1)  
**Difficulty:** High  
**Story Points:** 21  
**Estimated Duration:** 4-5 days  
**Dependencies:** None (foundational work)

---

## 📋 Batch Overview

This batch implements the **core synchronization API** that enables the hybrid GDB+SoD architecture. You will implement the `SyncFrom()` mechanism that allows one EntityRepository to synchronize with another, with optional component filtering.

**Critical Success Factors:**
- Dirty chunk tracking must work correctly (version-based optimization)
- Both Tier 1 (unmanaged) and Tier 2 (managed) synchronization
- Performance targets must be met
- Zero allocations during sync (GDB scenario)

---

## 📚 Required Reading

**Before starting, read these documents:**

1. **Primary References:**
   - `/docs/HYBRID-ARCHITECTURE-QUICK-REFERENCE.md` - Architecture overview
   - `/docs/API-REFERENCE.md` - Sections: EntityRepository.SyncFrom, NativeChunkTable, EntityIndex
   - `/docs/MEMORY-LAYOUT-DIAGRAMS.md` - Diagram 0 (World A) and Diagram 2 (GDB)

2. **Detailed Specifications:**
   - `/docs/IMPLEMENTATION-SPECIFICATION.md` - Section: Core Design Decisions (Decision 1)
   - `/docs/detailed-design-overview.md` - Layer 0: FDP Synchronization Core
   - `/docs/IMPLEMENTATION-TASKS.md` - Tasks 001-004

3. **Design Rationale:**
   - `/docs/reference-archive/FDP-GDB-SoD-unified.md` - Section: SyncFrom mechanism

**Key Concepts to Understand:**
- Chunk versioning (how it enables dirty tracking)
- Difference between GDB (mask=null, 100%) and SoD (mask=bits, filtered)
- Tier 1 (memcpy) vs Tier 2 (shallow copy) synchronization
- EntityIndex structure (IsAlive, Generation, ComponentMasks)

---

## 🎯 Tasks in This Batch

### TASK-001: EntityRepository.SyncFrom() API (8 SP)

**Priority:** P0 (Critical Path)  
**File:** `Fdp.Kernel/EntityRepository.Sync.cs` (new partial class)

**Description:**  
Implement the core synchronization method that copies data from one repository to another with optional filtering.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncFrom(EntityRepository source, BitMask256? mask = null)`
- [ ] Syncs EntityIndex (call EntityIndex.SyncFrom)
- [ ] Iterates component tables, applies mask filtering
- [ ] Delegates to table-specific SyncDirtyChunks methods
- [ ] Syncs global version
- [ ] Full sync (mask=null) copies all dirty chunks
- [ ] Filtered sync (mask=bits) copies only specified component types
- [ ] Performance: <2ms for 100K entities, 30% dirty chunks

**Implementation Notes:**
```csharp
// File: Fdp.Kernel/EntityRepository.Sync.cs
public sealed partial class EntityRepository
{
    public void SyncFrom(EntityRepository source, BitMask256? mask = null)
    {
        // 1. Sync EntityIndex
        _entityIndex.SyncFrom(source._entityIndex);
        
        // 2. Sync component tables (with optional filtering)
        foreach (var typeId in _componentTables.Keys)
        {
            if (mask.HasValue && !mask.Value.IsSet(typeId))
                continue;  // Skip filtered components
            
            var myTable = _componentTables[typeId];
            var srcTable = source._componentTables[typeId];
            
            // Delegate to table-specific sync
            if (myTable is IUnmanagedComponentTable tier1)
            {
                ((NativeChunkTable)tier1).SyncDirtyChunks((NativeChunkTable)srcTable);
            }
            else
            {
                ((ManagedComponentTable)myTable).SyncDirtyChunks((ManagedComponentTable)srcTable);
            }
        }
        
        // 3. Sync global version
        this._globalVersion = source._globalVersion;
    }
}
```

**Tests Required (8 tests):**

Create file: `Fdp.Tests/EntityRepositorySyncTests.cs`

1. **FullSync_CopiesAllDirtyChunks**
   - Setup: Create source repo with entities, modify some
   - Execute: `replica.SyncFrom(source)`
   - Verify: All dirty chunks copied, version matches

2. **FilteredSync_CopiesOnlyMaskedComponents**
   - Setup: Source has Position, Health, Velocity
   - Execute: `snapshot.SyncFrom(source, mask)` where mask = Position only
   - Verify: Only Position table synced, others unchanged

3. **StaticEntities_NotCopied_WhenVersionMatches**
   - Setup: Source and dest have same chunk version
   - Execute: `SyncFrom()`
   - Verify: Chunk not copied (dirty tracking optimization)

4. **EntityIndex_SyncedCorrectly**
   - Setup: Source has entities created/destroyed
   - Execute: `SyncFrom()`
   - Verify: IsAlive flags match, Generation counters match

5. **Tier2_ShallowCopy_Works**
   - Setup: Source has managed components (records)
   - Execute: `SyncFrom()`
   - Verify: Dest points to same record instances (shallow copy)

6. **GlobalVersion_Synced**
   - Setup: Source GlobalVersion = 142
   - Execute: `SyncFrom()`
   - Verify: Dest GlobalVersion = 142

7. **EmptySource_ClearsDestination**
   - Setup: Source has no entities, Dest has entities
   - Execute: `SyncFrom()`
   - Verify: Dest tables cleared

8. **Performance_MeetsTarget**
   - Setup: 100K entities, 30% dirty chunks
   - Execute: `SyncFrom()` (full)
   - Verify: Completes in <2ms

**Performance Benchmark:**
```csharp
[Benchmark]
public void SyncFrom_FullSync_100K_Entities()
{
    // Setup: 100K entities, 30% dirty
    var stopwatch = Stopwatch.StartNew();
    replica.SyncFrom(live);
    stopwatch.Stop();
    
    Assert.True(stopwatch.ElapsedMilliseconds < 2);
}
```

---

### TASK-002: NativeChunkTable.SyncDirtyChunks() (5 SP)

**Priority:** P0 (Required by TASK-001)  
**File:** `Fdp.Kernel/NativeChunkTable.cs`

**Description:**  
Implement dirty chunk synchronization for Tier 1 (unmanaged) components using version tracking.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncDirtyChunks(NativeChunkTable<T> source)`
- [ ] Version check prevents copying unchanged chunks
- [ ] Uses `Unsafe.CopyBlock` for memcpy (64KB chunks)
- [ ] Updates chunk versions after copy
- [ ] Handles chunk liveness (clears destination if source empty)
- [ ] Performance: <1ms for 1000 chunks, 30% dirty

**Implementation Notes:**
```csharp
// File: Fdp.Kernel/NativeChunkTable.cs
public void SyncDirtyChunks(NativeChunkTable<T> source) where T : unmanaged
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        // **OPTIMIZATION:** Version check
        uint srcVer = source.GetChunkVersion(i);
        if (_chunkVersions[i] == srcVer)
            continue;  // Chunk unchanged, skip memcpy
        
        // Liveness check
        if (!source.IsChunkAllocated(i))
        {
            if (this.IsChunkAllocated(i))
                this.ClearChunk(i);
            continue;
        }
        
        // **THE COPY:** memcpy (Tier 1)
        EnsureChunkAllocated(i);
        Unsafe.CopyBlock(
            this.GetChunkDataPtr(i),
            source.GetChunkDataPtr(i),
            FdpConfig.CHUNK_SIZE_BYTES  // 64KB
        );
        
        // Update version
        _chunkVersions[i] = srcVer;
    }
}
```

**Tests Required (6 tests):**

Create file: `Fdp.Tests/NativeChunkTableSyncTests.cs`

1. **DirtyChunk_Copied**
   - Setup: Source chunk version = 10, Dest version = 9
   - Execute: `SyncDirtyChunks()`
   - Verify: Chunk copied, dest version = 10

2. **CleanChunk_Skipped**
   - Setup: Source chunk version = 10, Dest version = 10
   - Execute: `SyncDirtyChunks()`
   - Verify: No memcpy occurred (performance optimization)

3. **ChunkVersion_UpdatedCorrectly**
   - Setup: Multiple chunks with different versions
   - Execute: `SyncDirtyChunks()`
   - Verify: All dest versions match source

4. **ChunkAllocation_OnDemand**
   - Setup: Source has chunk allocated, Dest does not
   - Execute: `SyncDirtyChunks()`
   - Verify: Dest chunk allocated and copied

5. **ChunkClearing_WhenSourceEmpty**
   - Setup: Source chunk not allocated, Dest chunk allocated
   - Execute: `SyncDirtyChunks()`
   - Verify: Dest chunk cleared

6. **Performance_1000Chunks_30PercentDirty**
   - Setup: 1000 chunks, 300 dirty
   - Execute: `SyncDirtyChunks()`
   - Verify: Completes in <1ms

---

### TASK-003: ManagedComponentTable.SyncDirtyChunks() (5 SP)

**Priority:** P0 (Required by TASK-001)  
**File:** `Fdp.Kernel/ManagedComponentTable.cs`

**Description:**  
Implement dirty chunk synchronization for Tier 2 (managed) components using shallow copy.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncDirtyChunks(ManagedComponentTable<T> source) where T : class`
- [ ] Version check prevents redundant copies
- [ ] Uses `Array.Copy` for reference arrays (shallow copy)
- [ ] Updates chunk versions after copy
- [ ] Performance: <500μs for 1000 chunks, 30% dirty

**Implementation Notes:**
```csharp
// File: Fdp.Kernel/ManagedComponentTable.cs
public void SyncDirtyChunks(ManagedComponentTable<T> source) where T : class
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        // Version check (same optimization as Tier 1)
        uint srcVer = source.GetChunkVersion(i);
        if (_chunkVersions[i] == srcVer)
            continue;
        
        // Liveness check
        if (!source.IsChunkAllocated(i))
        {
            if (this.IsChunkAllocated(i))
                this.ClearChunk(i);
            continue;
        }
        
        // Shallow copy (references, not deep clone)
        EnsureChunkAllocated(i);
        Array.Copy(
            source._chunks[i],
            this._chunks[i],
            FdpConfig.CHUNK_ENTITY_COUNT  // e.g., 1024
        );
        
        // Update version
        _chunkVersions[i] = srcVer;
    }
}
```

**Tests Required (4 tests):**

Create file: `Fdp.Tests/ManagedComponentTableSyncTests.cs`

1. **ShallowCopy_Works**
   - Setup: Source has record instances
   - Execute: `SyncDirtyChunks()`
   - Verify: Dest references same instances (not deep clone)

2. **ImmutableRecords_Enforced**
   - Setup: Component type is immutable record
   - Execute: `SyncDirtyChunks()`
   - Verify: Shallow copy safe (no mutation risk)

3. **VersionTracking_Works**
   - Setup: Mix of dirty and clean chunks
   - Execute: `SyncDirtyChunks()`
   - Verify: Only dirty chunks copied

4. **Performance_500Microseconds**
   - Setup: 1000 chunks, 30% dirty
   - Execute: `SyncDirtyChunks()`
   - Verify: Completes in <500μs

---

### TASK-004: EntityIndex.SyncFrom() (3 SP)

**Priority:** P0 (Required by TASK-001)  
**File:** `Fdp.Kernel/EntityIndex.cs`

**Description:**  
Sync entity metadata (IsAlive, Generation, ComponentMasks) from source to destination.

**Acceptance Criteria:**
- [ ] Method signature: `public void SyncFrom(EntityIndex source)`
- [ ] Copies IsAlive flags (bitset)
- [ ] Copies Generation counters
- [ ] Copies ComponentMasks
- [ ] Maintains sparse structure
- [ ] Performance: <100μs for 100K entities

**Implementation Notes:**
```csharp
// File: Fdp.Kernel/EntityIndex.cs
public void SyncFrom(EntityIndex source)
{
    // Copy IsAlive flags (bitset)
    Array.Copy(source._isAlive, this._isAlive, source._isAlive.Length);
    
    // Copy Generation counters
    Array.Copy(source._generations, this._generations, source._generations.Length);
    
    // Copy ComponentMasks
    Array.Copy(source._componentMasks, this._componentMasks, source._componentMasks.Length);
}
```

**Tests Required (3 tests):**

Create file: `Fdp.Tests/EntityIndexSyncTests.cs`

1. **FullMetadataSync**
   - Setup: Source has entities with various states
   - Execute: `SyncFrom()`
   - Verify: IsAlive, Generation, Masks all match

2. **SparseEntities_Handled**
   - Setup: Source has entities 0, 100, 1000 (sparse)
   - Execute: `SyncFrom()`
   - Verify: Sparse structure maintained

3. **Performance_100K_Entities**
   - Setup: 100K entities
   - Execute: `SyncFrom()`
   - Verify: Completes in <100μs

---

## 🔍 Integration Tests

**After all 4 tasks complete**, create integration test:

**File:** `Fdp.Tests/Integration/SyncIntegrationTests.cs`

### Integration Test: FullSystemSync

```csharp
[Fact]
public void FullSystemSync_GDB_Scenario()
{
    // Setup: Create live world with entities
    var live = new EntityRepository();
    var replica = new EntityRepository();
    
    // Create 1000 entities in live
    for (int i = 0; i < 1000; i++)
    {
        var e = live.CreateEntity();
        live.SetComponent(e, new Position { X = i, Y = i * 2 });
        live.SetComponent(e, new Velocity { X = 1, Y = 1 });
        live.SetComponent(e, new Identity { Callsign = $"Unit_{i}" });
    }
    
    // Execute: GDB sync (full, no mask)
    replica.SyncFrom(live);
    
    // Verify: All data copied correctly
    for (int i = 0; i < 1000; i++)
    {
        var liveEntity = new Entity((uint)i, 1);
        var replicaEntity = new Entity((uint)i, 1);
        
        Assert.True(replica.IsAlive(replicaEntity));
        
        var livePos = live.GetComponent<Position>(liveEntity);
        var replicaPos = replica.GetComponent<Position>(replicaEntity);
        Assert.Equal(livePos, replicaPos);
        
        var liveIdentity = live.GetComponent<Identity>(liveEntity);
        var replicaIdentity = replica.GetComponent<Identity>(replicaEntity);
        Assert.Same(liveIdentity, replicaIdentity);  // Shallow copy!
    }
    
    // Verify: Global version matches
    Assert.Equal(live.GlobalVersion, replica.GlobalVersion);
}

[Fact]
public void FilteredSync_SoD_Scenario()
{
    // Similar test but with mask filtering
    var aiMask = new BitMask256();
    aiMask.Set(typeof(Position));
    aiMask.Set(typeof(Team));
    
    snapshot.SyncFrom(live, aiMask);
    
    // Verify: Only Position and Team copied, Velocity NOT copied
    Assert.True(snapshot.HasComponent<Position>(entity));
    Assert.True(snapshot.HasComponent<Team>(entity));
    Assert.False(snapshot.HasComponent<Velocity>(entity));
}
```

---

## ⚠️ Critical Rules

**Mandatory Requirements:**

1. ⛔ **NO compiler warnings** - Treat warnings as errors
2. ⛔ **Chunk versioning MUST work** - This is the performance optimization
3. ⛔ **Tier 2 shallow copy only** - Never deep clone records
4. ⛔ **Performance targets MUST be met** - Not negotiable
5. ⛔ **Unsafe.CopyBlock required** - For Tier 1 (memcpy performance)

**Architecture Constraints:**

- Dirty tracking is version-based (increment on write)
- GDB = full sync (mask=null), SoD = filtered (mask=bits)
- EntityRepository is partial class (sync code in separate file)
- Chunk size = 64KB (FdpConfig.CHUNK_SIZE_BYTES)

---

## 📊 Success Metrics

**Batch is DONE when:**

- [x] All 4 tasks complete (TASK-001 through TASK-004)
- [x] All 21 unit tests passing
- [x] 2 integration tests passing
- [x] Zero compiler warnings
- [x] Performance benchmarks pass:
  - SyncFrom (full): <2ms for 100K entities
  - SyncDirtyChunks (T1): <1ms for 1000 chunks
  - SyncDirtyChunks (T2): <500μs for 1000 chunks
  - EntityIndex.SyncFrom: <100μs for 100K entities

---

## 🚨 Common Pitfalls

**Watch Out For:**

1. **Forgetting version updates** - Always update `_chunkVersions[i]` after copy
2. **Deep cloning Tier 2** - Use `Array.Copy` for references, not deep clone
3. **Skipping version check** - This is the key optimization!
4. **Unsafe pointer errors** - Ensure chunk allocated before `Unsafe.CopyBlock`
5. **Mask filtering errors** - Test both filtered and full sync thoroughly

---

## 💡 Implementation Tips

**Best Practices:**

1. **Start with TASK-004** (EntityIndex) - It's simplest, builds confidence
2. **Then TASK-002** (NativeChunkTable) - Core memcpy logic
3. **Then TASK-003** (ManagedComponentTable) - Similar to T2 but simpler
4. **Finally TASK-001** (SyncFrom) - Orchestrates all the above

**Testing Strategy:**

1. Write tests as you implement (TDD)
2. Run tests frequently (`dotnet test`)
3. Performance test last (after correctness verified)
4. Integration tests validate full system behavior

**Debugging:**

- Use breakpoints to inspect chunk versions
- Verify `Unsafe.CopyBlock` copies correct bytes
- Check that filtered sync skips correct tables
- Profile if performance targets not met

---

## 📋 Deliverables

**When batch complete, submit:**

1. **Batch Report:** `reports/BATCH-01-REPORT.md`
   - Use template: `templates/BATCH-REPORT-TEMPLATE.md`
   - Include all sections (see template)

2. **Questions (if any):** `reports/BATCH-01-QUESTIONS.md`
   - Use template: `templates/QUESTIONS-TEMPLATE.md`

3. **Blockers (if any):** `reports/BLOCKERS-ACTIVE.md`
   - Update immediately when blocked

**Report Must Include:**

- Status of all 4 tasks
- Test results (21 unit + 2 integration)
- Performance benchmark results
- Files created/modified list
- Any additional work done
- Known issues (if any)

---

## 🎯 Next Batch Preview

**BATCH-02** (following this) will implement:
- EventAccumulator (event history bridging)
- ISimulationView interface
- EntityRepository implements ISimulationView

These depend on `SyncFrom()` working correctly!

---

**Questions? Create:** `reports/BATCH-01-QUESTIONS.md`  
**Blocked? Update:** `reports/BLOCKERS-ACTIVE.md`  
**Done? Submit:** `reports/BATCH-01-REPORT.md`

**Good luck! 🚀**
