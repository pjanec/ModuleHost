# BATCH 02: Reactive Scheduling - CORRECTED

**Batch ID:** BATCH-02  
**Phase:** Foundation - Reactive Scheduling  
**Priority:** HIGH (P1)  
**Estimated Effort:** 1 week  
**Dependencies:** BATCH-01 (requires LastRunTick tracking)  
**Developer:** TBD  
**Assigned Date:** TBD

**⚠️ CRITICAL UPDATE:** Task 2.1 revised based on performance feedback from FDP scheduling design document to avoid cache contention.

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 2 (Reactive Scheduling) **[UPDATED]**
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 02 section
4. **BATCH-01 Review:** `../reviews/BATCH-01-REVIEW.md` (understand what changed)
5. **Current Implementation:** Review FDP Event Bus and Component Tables

---

## 🎯 Batch Objectives

### Primary Goal
Enable modules to wake on specific events or component changes, not just timers.

### Success Criteria
- ✅ Modules wake immediately when watched events fire
- ✅ Modules wake when watched component tables are modified
- ✅ Trigger check overhead <0.1ms per module
- ✅ No entity iteration required (O(chunks) scan, typically ~100 chunks for 100k entities)
- ✅ **No cache contention or false sharing**
- ✅ All tests passing

### Why This Matters
Currently, modules run on fixed timers (e.g., every 6 frames). A 10Hz AI module ignores being shot for up to 100ms. Reactive scheduling lets it sleep until critical events happen, improving responsiveness while reducing CPU waste.

---

## 📋 Tasks

### Task 2.1: Component Dirty Tracking (LAZY SCAN) ⭐⭐

**Objective:** Add `HasChanges()` method to FDP component tables using lazy scan approach to avoid cache contention.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 2, Section 2.2 - "Component Dirty Tracking" **(UPDATED)**

**⚠️ CRITICAL PERFORMANCE NOTE:**

**DO NOT** write to a shared `LastWriteTick` field on every `Set()/GetRW()` call! This causes:
- **Cache Line Contention:** Multiple threads writing to same memory location
- **False Sharing:** Field shares cache line with other data, causing unnecessary invalidations
- **Performance Degradation:** Severe impact under high write load

**✅ CORRECT APPROACH: Lazy Scan**

FDP already maintains `_chunkVersions` array (one version per chunk). Instead of updating a global field on every write, **scan this array on-demand** during trigger checks:
- Called only once per module per frame (during `ShouldRunThisFrame`)
- Scan cost: ~10-50 nanoseconds for 100k entities (~100 chunks)
- L1-cache friendly: sequential int array scan
- Zero overhead during component writes

**Files to Modify:**

1. **`FDP/Fdp.Kernel/IComponentTable.cs`:**
   ```csharp
   public interface IComponentTable : IDisposable
   {
       // Existing properties...
       
       // NEW: Check if table changed since version (lazy scan)
       /// <summary>
       /// Efficiently checks if this table has been modified since the specified version.
       /// Uses lazy scan of chunk versions (O(chunks), typically ~100 chunks for 100k entities).
       /// PERFORMANCE: 10-50ns scan time, L1-cache friendly, no write contention.
       /// </summary>
       bool HasChanges(uint sinceVersion);
   }
   ```

2. **`FDP/Fdp.Kernel/NativeChunkTable.cs`:**
   ```csharp
   public sealed unsafe class NativeChunkTable<T> : IComponentTable where T : unmanaged
   {
       // Existing fields (_chunkVersions array already exists)...
       
       // ❌ DO NOT ADD:
       // private uint _lastWriteTick; // WRONG - causes cache contention!
       
       // ✅ NEW: Lazy scan implementation
       public bool HasChanges(uint sinceVersion)
       {
           // Fast L1 cache scan of chunk versions array
           // For 100k entities (~100 chunks):
           // - Sequential int reads:  ~10-50 nanoseconds total
           // - L1 cache friendly: one array, sequential access, CPU prefetching
           // - No writes: zero contention
           for (int i = 0; i < _totalChunks; i++)
           {
               // Each chunk already tracks its version (existing field)
               if (_activeChunkVersions[i] > sinceVersion)
                   return true;
           }
           return false;
       }
       
       // 📌 NOTE:  GetRefRW and Set remain UNCHANGED
       // They already update _chunk Versions[chunkIndex] appropriately
       // No modification needed to write path
   }
   ```

3. **`FDP/Fdp.Kernel/ManagedComponentTable.cs`:**
   ```csharp
   public sealed class ManagedComponentTable<T> : IComponentTable where T : class
   {
       // Similar implementation to NativeChunkTable
       public bool HasChanges(uint sinceVersion)
       {
           for (int i = 0; i < _allocatedChunks; i++)
           {
               if(GetChunkVersion(i) > sinceVersion)
                   return true;
           }
           return false;
       }
       
       // Write path remains unchanged
   }
   ```

4. **`FDP/Fdp.Kernel/EntityRepository.cs`:**
   ```csharp
   /// <summary>
   /// Checks if a component table has been modified since the specified tick.
   /// Uses lazy scan of chunk versions (fast, no writes).
   /// </summary>
   public bool HasComponentChanged(Type componentType, uint sinceTick)
   {
       if (_componentTables.TryGetValue(componentType, out var table))
           return table.HasChanges(sinceTick);
       return false;
   }
   ```

**Acceptance Criteria:**
- [ ] `IComponentTable.HasChanges()` method added
- [ ] Both NativeChunkTable and ManagedComponentTable implement HasChanges()
- [ ] Implementation uses lazy scan (NO writes to shared state)
- [ ] `EntityRepository.HasComponentChanged()` method works correctly
- [ ] Performance: scan completes in <50ns for typical entity counts
- [ ] **Zero cache contention** during component writes

**Unit Tests to Write:**

```csharp
// File: FDP/Fdp.Tests/ComponentDirtyTrackingTests.cs

[Fact]
public void NativeChunkTable_HasChanges_DetectsWrite()
{
    var table = CreateTable<Position>();
    uint initialVersion = 5;
    
    // No changes yet
    Assert.False(table.HasChanges(initialVersion));
    
    // Write component at version 10
    var entity = CreateEntity();
    table.Set(entity.Id, new Position { X = 1 }, version: 10);
    
    // Should detect change
    Assert.True(table.HasChanges(initialVersion));
    Assert.True(table.HasChanges(9));
    Assert.False(table.HasChanges(10)); // Same version
    Assert.False(table.HasChanges(11)); // Future version
}

[Fact]
public void NativeChunkTable_HasChanges_MultipleChunks()
{
    var table = CreateLargeTable<Position>(); // Multiple chunks
    uint check Version = 100;
    
    // Write to chunk 0
    table.Set(entity1.Id, new Position(), version: 105);
    // Write to chunk 5
    table.Set(entity2.Id, new Position(), version: 110);
    
    // Should detect changes in any chunk
    Assert.True(table.HasChanges(checkVersion));
}

[Fact]
public void ManagedComponentTable_HasChanges_DetectsWrite()
{
    // Same test as Native but for managed components
}

[Fact]
public void EntityRepository_HasComponentChanged_DetectsTableChanges()
{
    var repo = new EntityRepository();
    repo.RegisterComponent<Position>();
    
    uint tick1 = repo.GlobalVersion;
    
    var entity = repo.CreateEntity();
    repo.SetComponent(entity, new Position { X = 1 });
    
    uint tick2 = repo.GlobalVersion;
    
    // Should detect change
    Assert.True(repo.HasComponentChanged(typeof(Position), tick1));
    Assert.False(repo.HasComponentChanged(typeof(Position), tick2));
}

[Fact]
public void ComponentDirtyTracking_PerformanceScan()
{
    // Setup: Table with 100k entities (~100 chunks)
    var table = CreateTableWith100kEntities<Position>();
    
    //  Measure: HasChanges() scan time
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 10000; i++)
    {
        table.HasChanges(0);
    }
    sw.Stop();
    
    // Target: < 50ns per scan
    double nsPerScan = (sw.Elapsed.TotalMilliseconds * 1_000_000) / 10000;
    Assert.True(nsPerScan < 50, $"Scan took {nsPerScan}ns (target: <50ns)");
}

[Fact]
public void ComponentDirtyTracking_NoCacheContention_ConcurrentWrites()
{
    var table = CreateTable<Position>();
    
    // 10 threads writing concurrently
    var tasks = Enumerable.Range(0, 10).Select(threadId => Task.Run(() =>
    {
        for (int i = 0; i < 1000; i++)
        {
            var entity = CreateEntity(threadId * 1000 + i);
            table.Set(entity.Id, new Position { X = i }, version: (uint)(i + 100));
        }
    })).ToArray();
    
    Task.WaitAll(tasks);
    
    // Assert: All writes completed (no crashes, corruption)
    // Assert: HasChanges works correctly
    Assert.True(table.HasChanges(0));
}
```

**Deliverables:**
- [ ] Modified: `FDP/Fdp.Kernel/IComponentTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/NativeChunkTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/ManagedComponentTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/EntityRepository.cs`
- [ ] New test file: `FDP/Fdp.Tests/ComponentDirtyTrackingTests.cs`
- [ ] 6+ unit tests passing
- [ ] Performance test showing <50ns scan time

---

### Task 2.2: Event Bus Active Tracking ⭐⭐

[UNCHANGED - same as before]

**Objective:** Track which event types were published in current frame for O(1) lookup.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 2, Section 2.2 - "Event Tracking"

**Current Code Location:**
- File: `FDP/Fdp.Kernel/FdpEventBus.cs`
- Current logic: Events published and swapped, but no active event tracking

**What to Add:**

```csharp
public class FdpEventBus : IDisposable
{
    // Existing fields...
    
    // NEW: Track active event IDs for this frame
    private readonly HashSet<int> _activeEventIds = new();
    private bool _anyEventPublished; // Early-out optimization
    
    public void Publish<T>(T evt) where T : unmanaged
    {
        int eventId = EventType<T>.Id;
        _activeEventIds.Add(eventId);
        _anyEventPublished = true;
        
        // ... existing publish logic
    }
    
    public void PublishManaged<T>(T evt) where T : class
    {
        int eventId = GetManagedTypeId<T>();
        _activeEventIds.Add(eventId);
        _anyEventPublished = true;
        
        // ... existing publish logic
    }
    
    public bool HasEvent(Type eventType)
    {
        if (!_anyEventPublished) return false; // Fast path
        int id = GetEventTypeId(eventType);
        return _activeEventIds.Contains(id);
    }
    
    public void SwapBuffers()
    {
        _activeEventIds.Clear();
        _anyEventPublished = false;
        
        // ... existing swap logic
    }
    
    private int GetEventTypeId(Type eventType)
    {
        if (eventType.IsValueType)
        {
            // Use EventType<>.Id via reflection (cache this)
            return (int)typeof(EventType<>)
                .MakeGenericType(eventType)
                .GetField("Id", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null);
        }
        else
        {
            return GetManagedTypeId(eventType);
        }
    }
    
    private int GetManagedTypeId<T>() where T : class
    {
        return typeof(T).FullName!.GetHashCode() & 0x7FFFFFFF;
    }
}
```

**Acceptance Criteria:**
- [ ] `_activeEventIds` HashSet added
- [ ] `_anyEventPublished` flag added for early-out
- [ ] Populated during `Publish()` and `PublishManaged()`
- [ ] Cleared during `SwapBuffers()`
- [ ] `HasEvent(Type)` method returns correct results
- [ ] Both unmanaged and managed events tracked
- [ ] No performance regression in publish path

**Unit Tests to Write:**

[Same as before - 6 tests]

**Deliverables:**
- [ ] Modified: `FDP/Fdp.Kernel/FdpEventBus.cs`
- [ ] New test file: `FDP/Fdp.Tests/EventBusActiveTrack ingTests.cs`
- [ ] 6+ unit tests passing

---

### Task 2.3: IModule Reactive API ⭐

[UNCHANGED - same as before]

---

### Task 2.4: Trigger Logic in ShouldRunThisFrame ⭐⭐⭐

[UNCHANGED - same as before]

---

## ✅ Definition of Done

[Same as before]

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Trigger check overhead | <0.1ms per module | <0.5ms |
| Event HasEvent lookup | <10μs | <50μs |
| Component HasChanges scan | <50ns for 100k entities | <200ns |
| Module wake latency | 1 frame | 2 frames |

### Quality Targets
[Same as before]

---

## 🚧 Potential Challenges

### Challenge 1: Chunk Version Array Access
**Issue:** _chunkVersions or _activeChunkVersions field name may vary  
**Solution:** Check actual field name in NativeChunkTable  
**Ask if:** Field name doesn't match or array structure unclear

### Challenge 2: Component Granularity (FALSE POSITIVES)
**Issue:** Table-level dirty tracking gives false positives (any chunk write triggers all watchers)  
**Solution:** This is acceptable per design (coarse-grained tracking)  
**Benefit:** 10-50ns scan vs. complex per-entity tracking  
**Ask if:** Concerned about false wake-up rate

### Challenge 3: Event ID Calculation
[Same as before]

### Challenge 4: Performance Validation
**Issue:** Need to verify scan is fast enough  
**Solution:** Add performance test (included in Task 2.1 tests)  
**Ask if:** Scan time exceeds 50ns on your hardware

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-02-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-02-QUESTIONS.md`

---

## 🔗 References

**Primary Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 2 **[UPDATED]**  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 02 section  
**Workflow README:** `../README.md`  
**FDP Scheduling Design:** Reference doc that informed this correction

**Code to Review:**
- `FDP/Fdp.Kernel/` - Event bus and component tables
- `ModuleHost.Core/ModuleHostKernel.cs` - Scheduling logic
- `BATCH-01-REVIEW.md` - What changed in previous batch

---

## 💡 Implementation Tips

1. **Verify chunk version field name** - might be `_chunkVersions`, `_activeChunkVersions`, or similar
2. **Test performance early** - 100-chunk scan should be <50ns
3. **Don't modify write path** - GetRefRW/Set already update chunk versions
4. **Document false positives** - important to understand tradeoff
5. **Consider early-out** - if first chunk matches, return immediately

**This batch touches FDP (shared lib) - be extra careful with breaking changes!**

**⚠️ KEY DIFFERENCE FROM INITIAL DESIGN:**  
We do NOT add `_lastWriteTick` field or write to it. We scan existing chunk version array lazily. This avoids cache contention.

Good luck! 🚀
