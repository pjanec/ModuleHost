# BATCH 02: Reactive Scheduling

**Batch ID:** BATCH-02  
**Phase:** Foundation - Reactive Scheduling  
**Priority:** HIGH (P1)  
**Estimated Effort:** 1 week  
**Dependencies:** BATCH-01 (requires LastRunTick tracking)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 2 (Reactive Scheduling)
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
- ✅ No entity iteration required (O(1) or small O(n))
- ✅ All tests passing

### Why This Matters
Currently, modules run on fixed timers (e.g., every 6 frames). A 10Hz AI module ignores being shot for up to 100ms. Reactive scheduling lets it sleep until critical events happen, improving responsiveness while reducing CPU waste.

---

## 📋 Tasks

### Task 2.1: Component Dirty Tracking ⭐⭐

**Objective:** Add `LastWriteTick` to FDP component tables for coarse-grained change detection.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 2, Section 2.2 - "Component Dirty Tracking"

**Files to Modify:**

1. **`FDP/Fdp.Kernel/IComponentTable.cs`:**
   ```csharp
   public interface IComponentTable : IDisposable
   {
       // Existing properties...
       
       // NEW: Global table version for dirty tracking
       uint LastWriteTick { get; }
   }
   ```

2. **`FDP/Fdp.Kernel/NativeChunkTable.cs`:**
   ```csharp
   public sealed unsafe class NativeChunkTable<T> : IComponentTable where T : unmanaged
   {
       private uint _lastWriteTick;
       
       public uint LastWriteTick => _lastWriteTick;
       
       // Modify GetRefRW:
       public ref T GetRefRW(int entityId, uint currentVersion)
       {
           _lastWriteTick = currentVersion;
           // ... rest of method
       }
       
       // Modify Set:
       public void Set(int entityId, in T component, uint version)
       {
           _lastWriteTick = version;
           // ... rest of method
       }
   }
   ```

3. **`FDP/Fdp.Kernel/ManagedComponentTable.cs`:**
   - Add same `_lastWriteTick` field and property
   - Update in `Set()` and `GetRW()` methods

4. **`FDP/Fdp.Kernel/EntityRepository.cs`:**
   ```csharp
   public bool HasComponentChanged(Type componentType, uint sinceTick)
   {
       if (_componentTables.TryGetValue(componentType, out var table))
           return table.LastWriteTick > sinceTick;
       return false;
   }
   ```

**Acceptance Criteria:**
- [ ] `IComponentTable` interface updated
- [ ] Both NativeChunkTable and ManagedComponentTable implement LastWriteTick
- [ ] Tick updates atomically on writes
- [ ] `HasComponentChanged()` method works correctly
- [ ] Thread-safe under concurrent writes

**Unit Tests to Write:**

```csharp
// File: FDP/Fdp.Tests/ComponentDirtyTrackingTests.cs

[Fact]
public void NativeChunkTable_Set_UpdatesLastWriteTick()
{
    // Create table, set component at tick 5
    // Assert: LastWriteTick == 5
}

[Fact]
public void NativeChunkTable_GetRefRW_UpdatesLastWriteTick()
{
    // Get RW reference at tick 10
    // Assert: LastWriteTick == 10
}

[Fact]
public void ManagedComponentTable_Set_UpdatesLastWriteTick()
{
    // Same as native but for managed
}

[Fact]
public void EntityRepository_HasComponentChanged_DetectsWrites()
{
    // Set component at tick 5
    // Assert: HasComponentChanged(typeof(Comp), 4) == true
    // Assert: HasComponentChanged(typeof(Comp), 5) == false
    // Assert: HasComponentChanged(typeof(Comp), 6) == false
}

[Fact]
public void ComponentDirtyTracking_ThreadSafe_ConcurrentWrites()
{
    // 10 threads writing to table concurrently
    // Assert: LastWriteTick is one of the write ticks
    // Assert: No crashes or corruption
}
```

**Deliverables:**
- [ ] Modified: `FDP/Fdp.Kernel/IComponentTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/NativeChunkTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/ManagedComponentTable.cs`
- [ ] Modified: `FDP/Fdp.Kernel/EntityRepository.cs`
- [ ] New test file: `FDP/Fdp.Tests/ComponentDirtyTrackingTests.cs`
- [ ] 5+ unit tests passing

---

### Task 2.2: Event Bus Active Tracking ⭐⭐

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
    
    public void Publish<T>(T evt) where T : unmanaged
    {
        int eventId = EventType<T>.Id;
        _activeEventIds.Add(eventId);
        
        // ... existing publish logic
    }
    
    public void PublishManaged<T>(T evt) where T : class
    {
        int eventId = GetManagedTypeId<T>();
        _activeEventIds.Add(eventId);
        
        // ... existing publish logic
    }
    
    public bool HasEvent(Type eventType)
    {
        int id = GetEventTypeId(eventType);
        return _activeEventIds.Contains(id);
    }
    
    public void SwapBuffers()
    {
        _activeEventIds.Clear();
        
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
        return eventType.FullName!.GetHashCode() & 0x7FFFFFFF;
    }
}
```

**Acceptance Criteria:**
- [ ] `_activeEventIds` HashSet added
- [ ] Populated during `Publish()` and `PublishManaged()`
- [ ] Cleared during `SwapBuffers()`
- [ ] `HasEvent(Type)` method returns correct results
- [ ] Both unmanaged and managed events tracked
- [ ] No performance regression in publish path

**Unit Tests to Write:**

```csharp
// File: FDP/Fdp.Tests/EventBusActiveTrackingTests.cs

[Fact]
public void FdpEventBus_Publish_AddsToActiveSet()
{
    // Publish<TestEvent>()
    // Assert: HasEvent(typeof(TestEvent)) == true
}

[Fact]
public void FdpEventBus_PublishManaged_AddsToActiveSet()
{
    // PublishManaged<TestManagedEvent>()
    // Assert: HasEvent(typeof(TestManagedEvent)) == true
}

[Fact]
public void FdpEventBus_SwapBuffers_ClearsActiveSet()
{
    // Publish event
    // SwapBuffers()
    // Assert: HasEvent(typeof(TestEvent)) == false
}

[Fact]
public void FdpEventBus_HasEvent_ReturnsFalseForUnpublished()
{
    // Don't publish anything
    // Assert: HasEvent(typeof(TestEvent)) == false
}

[Fact]
public void FdpEventBus_MultiplePublishes_SameEventOnce()
{
    // Publish<TestEvent>() 5 times
    // Assert: _activeEventIds.Count == 1 (deduplicated)
}

[Fact]
public void FdpEventBus_MixedEvents_AllTracked()
{
    // Publish EventA, EventB, EventC
    // Assert: HasEvent for all three returns true
}
```

**Deliverables:**
- [ ] Modified: `FDP/Fdp.Kernel/FdpEventBus.cs`
- [ ] New test file: `FDP/Fdp.Tests/EventBusActiveTrackingTests.cs`
- [ ] 6+ unit tests passing

---

### Task 2.3: IModule Reactive API ⭐

**Objective:** Extend `IModule` interface with watch lists.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 2, Section 2.2 - "API Changes"

**Files to Modify:**

1. **`ModuleHost.Core/Abstractions/IModule.cs`:**
   ```csharp
   public interface IModule
   {
       string Name { get; }
       ModuleTier Tier { get; }  // Will be replaced in BATCH-05
       int UpdateFrequency { get; }
       
       void RegisterSystems(ISystemRegistry registry) { }
       void Tick(ISimulationView view, float deltaTime);
       
       // NEW: Reactive triggers (nullable for backward compatibility)
       IReadOnlyList<Type>? WatchComponents { get; }
       IReadOnlyList<Type>? WatchEvents { get; }
   }
   ```

2. **`ModuleHost.Core/ModuleHostKernel.cs`:**
   - Add Type→ID caching dictionary for performance
   - Cache mappings during `Initialize()` to avoid reflection in hot path

**Acceptance Criteria:**
- [ ] `WatchComponents` property added
- [ ] `WatchEvents` property added
- [ ] Existing modules compile with default null implementations
- [ ] Type→ID cache implemented in kernel
- [ ] Cache populated during initialization

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ReactiveModuleApiTests.cs

[Fact]
public void IModule_WatchComponents_DefaultNull()
{
    var module = new TestModule(); // Doesn't override
    Assert.Null(module.WatchComponents);
}

[Fact]
public void IModule_WatchEvents_DefaultNull()
{
    var module = new TestModule();
    Assert.Null(module.WatchEvents);
}

[Fact]
public void ReactiveModule_WatchLists_PopulatedCorrectly()
{
    var module = new ReactiveTestModule
    {
        WatchComponents = new[] { typeof(Position), typeof(Health) },
        WatchEvents = new[] { typeof(DamageEvent) }
    };
    
    Assert.Equal(2, module.WatchComponents.Count);
    Assert.Equal(1, module.WatchEvents.Count);
}

[Fact]
public void ModuleHostKernel_Initialize_CachesTypeIds()
{
    // Register module with watch lists
    // Call Initialize()
    // Assert: Type→ID cache populated
    // Assert: No reflection calls during ShouldRunThisFrame
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/Abstractions/IModule.cs`
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (caching logic)
- [ ] New test file: `ModuleHost.Core.Tests/ReactiveModuleApiTests.cs`
- [ ] 4+ unit tests passing

---

### Task 2.4: Trigger Logic in ShouldRunThisFrame ⭐⭐⭐

**Objective:** Implement reactive trigger checks in scheduler.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 2, Section 2.2 - "Trigger Logic"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Method: `ShouldRunThisFrame(ModuleEntry entry)` (around line 245)
- Current logic: Only checks `FramesSinceLastRun >= Frequency`

**New Implementation:**

```csharp
private bool ShouldRunThisFrame(ModuleEntry entry)
{
    // 1. Timer Check (EXISTING - keep this)
    int freq = Math.Max(1, entry.Module.UpdateFrequency);
    bool timerDue = (entry.FramesSinceLastRun + 1) >= freq;
    
    if (timerDue) return true;
    
    // 2. Event Triggers (NEW - immediate)
    if (entry.Module.WatchEvents != null && entry.Module.WatchEvents.Count > 0)
    {
        foreach (var evtType in entry.Module.WatchEvents)
        {
            // Use cached ID for performance
            if (_liveWorld.Bus.HasEvent(evtType))
            {
                return true;
            }
        }
    }
    
    // 3. Component Triggers (NEW - since last run)
    if (entry.Module.WatchComponents != null && entry.Module.WatchComponents.Count > 0)
    {
        uint lastRunTick = entry.LastRunTick;
        
        foreach (var compType in entry.Module.WatchComponents)
        {
            if (_liveWorld.HasComponentChanged(compType, lastRunTick))
            {
                return true;
            }
        }
    }
    
    return false;
}
```

**Acceptance Criteria:**
- [ ] Timer check remains unchanged
- [ ] Event trigger check added
- [ ] Component trigger check added
- [ ] Triggers override frequency timer
- [ ] Performance: <0.1ms overhead per module
- [ ] No false negatives (always wakes when should)

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Tests/ReactiveSchedulingIntegrationTests.cs

[Fact]
public async Task ReactiveScheduling_EventTrigger_WakesModule()
{
    // Setup: Module at 10Hz watching DamageEvent
    // Publish DamageEvent at frame 2
    // Assert: Module runs at frame 2 (not waiting until frame 6)
}

[Fact]
public async Task ReactiveScheduling_ComponentChangeTrigger_WakesModule()
{
    // Setup: Module watching Health component
    // Modify Health component at frame 3
    // Assert: Module runs at frame 4 (harvest then dispatch)
}

[Fact]
public async Task ReactiveScheduling_NoTrigger_ModuleSleeps()
{
    // Setup: Module at 10Hz watching event that never fires
    // Run 5 frames with no event
    // Assert: Module doesn't run (sleeps past timer)
}

[Fact]
public async Task ReactiveScheduling_TriggerOverridesTimer()
{
    // Setup: Module last ran at frame 1, freq=6 (next should be frame 7)
    // Publish watched event at frame 3
    // Assert: Module runs at frame 3
}

[Fact]
public async Task ReactiveScheduling_MultipleWatches_AnyTriggers()
{
    // Setup: Module watching EventA, EventB, CompX, CompY
    // Only modify CompX
    // Assert: Module runs (any watch triggers)
}
```

**Performance Test to Write:**

```csharp
// File: ModuleHost.Benchmarks/TriggerCheckOverhead.cs

[Benchmark]
[Arguments(10, 5)] // 10 modules, 5 with watches
public void TriggerCheck_Overhead(int totalModules, int watchingModules)
{
    // Setup modules with and without watches
    // Run ShouldRunThisFrame 10000 times
    // Measure: Time per check
    // Target: <0.1ms per module
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (ShouldRunThisFrame)
- [ ] New test file: `ModuleHost.Tests/ReactiveSchedulingIntegrationTests.cs`
- [ ] New benchmark: `ModuleHost.Benchmarks/TriggerCheckOverhead.cs`
- [ ] 5+ integration tests passing
- [ ] Benchmark showing <0.1ms overhead

---

## ✅ Definition of Done

This batch is complete when:

- [ ] All 4 tasks completed
- [ ] FDP dirty tracking implemented and tested
- [ ] Event bus tracking implemented and tested
- [ ] IModule API extended
- [ ] Trigger logic working correctly
- [ ] All unit tests passing (15+ tests total)
- [ ] All integration tests passing (5+ tests)
- [ ] Performance benchmark <0.1ms per module
- [ ] No compiler warnings
- [ ] Changes committed to git
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Trigger check overhead | <0.1ms per module | <0.5ms |
| Event HasEvent lookup | <10μs | <50μs |
| Component HasChanged lookup | <10μs | <50μs |
| Module wake latency | 1 frame | 2 frames |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| All unit tests | Passing |
| All integration tests | Passing |
| Compiler warnings | 0 |

---

## 🚧 Potential Challenges

### Challenge 1: Type→ID Mapping Performance
**Issue:** Reflection in hot path would kill performance  
**Solution:** Cache Type→ID mappings during initialization  
**Ask if:** Caching strategy is unclear

### Challenge 2: Component Granularity
**Issue:** Table-level dirty tracking gives false positives  
**Solution:** This is acceptable per design (coarse-grained)  
**Ask if:** Concerned about false wake-ups

### Challenge 3: Event ID Calculation
**Issue:** Different paths for managed vs unmanaged events  
**Solution:** Use EventType<>.Id for unmanaged, hash for managed  
**Ask if:** ID collision detection needed

### Challenge 4: Thread Safety
**Issue:** LastWriteTick written from multiple threads  
**Solution:** uint writes are atomic on 32/64-bit, alignment guaranteed  
**Ask if:** Seeing race conditions in tests

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-02-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-02-QUESTIONS.md`

---

## 🔗 References

**Primary Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 2  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 02 section  
**Workflow README:** `../README.md`

**Code to Review:**
- `FDP/Fdp.Kernel/` - Event bus and component tables
- `ModuleHost.Core/ModuleHostKernel.cs` - Scheduling logic
- `BATCH-01-REVIEW.md` - What changed in previous batch

---

## 💡 Implementation Tips

1. **Start with FDP changes** (Tasks 2.1, 2.2) - they're independent
2. **Test dirty tracking thoroughly** - this is critical for correctness
3. **Benchmark early** - O(1) lookups should be very fast
4. **Use existing test patterns** in FDP.Tests
5. **Document the false positive rate** of component triggers

**This batch touches FDP (shared lib) - be extra careful with breaking changes!**

Good luck! 🚀
