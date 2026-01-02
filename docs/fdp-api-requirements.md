# FDP API Requirements - UPDATED with Event-Driven Scheduling

**Date:** January 3, 2026  
**Status:** Final Requirements

---

## Critical Feedback Integration

### 1. ✅ Buffer Overflow Handling
**Requirement:** Buffers must expand gracefully (no data loss)

**Solution:** Dynamic buffer growth with pool reuse:
```csharp
public unsafe class NativeEventStream<T> where T : unmanaged
{
    private byte* _buffer;
    private int _capacity;
    private int _count;
    
    public void Write(T value)
    {
        if (_count >= _capacity)
        {
            // Double capacity (like List<T>)
            int newCapacity = _capacity * 2;
            byte* newBuffer = (byte*)NativeMemory.Alloc((nuint)(newCapacity * sizeof(T)));
            
            // Copy existing data
            Buffer.MemoryCopy(_buffer, newBuffer, newCapacity * sizeof(T), _count * sizeof(T));
            
            // Free old buffer
            NativeMemory.Free(_buffer);
            
            _buffer = newBuffer;
            _capacity = newCapacity;
        }
        
        // Safe write
        ((T*)_buffer)[_count++] = value;
    }
}
```

**Result:** No fixed buffer limits, no data loss. Buffer grows as needed.

---

### 2. ✅ Extended History Duration

**Problem:** Background modules can run for **seconds**, not milliseconds!

**Examples:**
- AI pathfinding: 500ms - 2 seconds
- Analytics aggregation: 1-5 seconds
- ML inference: 100ms - 1 second

**Old Limit:** 10 frames @ 60Hz = **166ms** ❌ WAY TOO LOW

**New Limit:** Configurable per module, default **180 frames** @ 60Hz = **3 seconds**

```csharp
public record ModuleDefinition
{
    public int TargetFrequencyHz { get; init; } = 10;
    public int MaxExpectedRuntimeMs { get; init; } = 1000;  // NEW
    public int MaxEventHistoryFrames { get; init; } = 180;   // NEW (3 seconds @ 60Hz)
}

public class SnapshotManager
{
    private int CalculateHistoryLimit()
    {
        // Find longest-running module
        int maxFrames = 180;  // Default 3s
        
        foreach (var module in _backgroundModules)
        {
            var def = module.GetDefinition();
            
            // Calculate frames needed: Runtime(ms) * FrameRate(Hz) / 1000 + safety margin
            int neededFrames = (def.MaxExpectedRuntimeMs * 60 / 1000) + 30;  // +30 frame safety
            maxFrames = Math.Max(maxFrames, neededFrames);
        }
        
        return maxFrames;
    }
}
```

**Safety Mechanism:**
```csharp
public class SnapshotManager
{
    public void PruneHistory(ulong currentFrame)
    {
        int historyLimit = CalculateHistoryLimit();
        ulong cutoffFrame = currentFrame - (ulong)historyLimit;
        
        // Also check active snapshots
        ulong oldestActive = GetOldestActiveSnapshotFrame();
        
        // Never prune frames still referenced by active snapshots
        ulong safeCutoff = Math.Min(cutoffFrame, oldestActive);
        
        _eventBus.PruneHistory(safeCutoff);
    }
}
```

---

### 3. ✅ Event Filtering in Snapshots

**Requirement:** Modules declare which events they need → snapshot only includes those

**Module Declaration:**
```csharp
public interface IModule
{
    ComponentMask GetSnapshotRequirements();
    
    // NEW: Event requirements
    EventTypeMask GetEventRequirements();
}

public class AIModule : IModule
{
    public EventTypeMask GetEventRequirements()
    {
        var mask = new EventTypeMask();
        mask.Set(EventType<ExplosionEvent>.Id);
        mask.Set(EventType<GunfireEvent>.Id);
        mask.Set(EventType<DetectionEvent>.Id);
        // Does NOT need UI events, network events, etc.
        return mask;
    }
}
```

**EventTypeMask (like ComponentMask):**
```csharp
public struct EventTypeMask
{
    private BitMask256 _mask;
    
    public void Set(int eventTypeId) => _mask.SetBit(eventTypeId);
    public bool IsSet(int eventTypeId) => _mask.IsSet(eventTypeId);
    
    public static EventTypeMask operator |(EventTypeMask a, EventTypeMask b)
    {
        return new EventTypeMask { _mask = a._mask | b._mask };
    }
}
```

**Snapshot Creation with Event Filtering:**
```csharp
public class SnapshotManager
{
    public ISimWorldSnapshot CreateUnionSnapshot(IEnumerable<IModule> modules)
    {
        var componentMask = new ComponentMask();
        var eventMask = new EventTypeMask();
        
        foreach (var module in modules)
        {
            componentMask |= module.GetSnapshotRequirements();
            eventMask |= module.GetEventRequirements();  // NEW
        }
        
        // Create snapshot with filtered events
        var tier1 = CreateTier1Snapshot(componentMask);
        var tier2 = CreateTier2Snapshot(componentMask);
        var events = _eventBus.GetEventHistory(fromFrame, toFrame, eventMask);  // FILTERED!
        
        return new HybridSnapshot(tier1, tier2, events, fromFrame, toFrame);
    }
}
```

**Updated FdpEventBus API:**
```csharp
public class FdpEventBus
{
    /// <summary>
    /// Gets event history with type filtering.
    /// Only includes event types present in the mask.
    /// </summary>
    public EventHistoryView GetEventHistory(
        ulong fromFrame, 
        ulong toFrame,
        EventTypeMask filter)  // NEW PARAMETER
    {
        var view = new EventHistoryView();
        
        for (ulong frame = fromFrame; frame <= toFrame; frame++)
        {
            if (_history.TryGetValue(frame, out var frameData))
            {
                // Filter by mask
                var filteredData = new FrameEventData();
                
                foreach (var kvp in frameData.NativeBuffers)
                {
                    if (filter.IsSet(kvp.Key))  // Only include if in mask
                    {
                        filteredData.NativeBuffers[kvp.Key] = kvp.Value;
                    }
                }
                
                view.AddFrame(frame, filteredData);
            }
        }
        
        return view;
    }
}
```

**Performance Benefit:**
- UI Module only gets UI events (not thousands of physics collision events)
- AI Module only gets gameplay events (not UI/network events)
- **50-90% reduction in event data per snapshot**

---

### 4. ✅ Event-Driven Scheduling (from FDP-module-scheduling-support.md)

**Requirement:** Modules wake up on component changes OR event arrival (not just time-based)

#### A. FDP Changes: Component Change Detection

**Add to NativeChunkTable:**
```csharp
internal unsafe class NativeChunkTable<T> where T : unmanaged
{
    // EXISTING
    private ulong[] _chunkVersions;
    
    // NEW: Fast change detection for scheduler
    public bool HasChanges(uint sinceVersion)
    {
        // Scan chunk versions (100s of chunks = nanoseconds)
        for (int i = 0; i < _chunkVersions.Length; i++)
        {
            if (_chunkVersions[i] > sinceVersion)
                return true;
        }
        return false;
    }
}
```

**Add to EntityRepository:**
```csharp
public sealed class EntityRepository
{
    public bool HasComponentChanged<T>(uint sinceVersion)
    {
        var table = GetTable<T>(false);
        return table.HasChanges(sinceVersion);
    }
    
    // Non-generic version (for scheduler without reflection)
    public bool HasComponentChangedByType(Type componentType, uint sinceVersion)
    {
        if (_componentTables.TryGetValue(componentType, out var table))
        {
            return table.HasChanges(sinceVersion);
        }
        return false;
    }
}
```

**Add to IComponentTable:**
```csharp
public interface IComponentTable
{
    // ... existing ...
    
    // NEW
    bool HasChanges(uint sinceVersion);
}
```

#### B. FDP Changes: Event Presence Detection

**Add to FdpEventBus:**
```csharp
public class FdpEventBus
{
    // Fast event presence cache
    private readonly HashSet<int> _activeEventIds = new();
    private bool _anyEventPublished = false;
    
    public void Publish<T>(T evt) where T : unmanaged
    {
        var stream = GetOrCreateNativeStream<T>();
        stream.Write(evt);
        
        // Mark event as active
        _activeEventIds.Add(EventType<T>.Id);
        _anyEventPublished = true;
    }
    
    // NEW: Check if events exist (without consuming)
    public bool HasEvents<T>() where T : unmanaged
    {
        if (!_anyEventPublished) return false;
        return _activeEventIds.Contains(EventType<T>.Id);
    }
    
    // Non-generic version (for scheduler)
    public bool HasEventsByType(Type eventType)
    {
        if (!_anyEventPublished) return false;
        
        // Get type ID via reflection (cached)
        int typeId = GetEventTypeId(eventType);
        return _activeEventIds.Contains(typeId);
    }
    
    public void SwapBuffers(ulong frameNumber)
    {
        // ... existing swap logic ...
        
        // Clear event presence cache
        _activeEventIds.Clear();
        _anyEventPublished = false;
    }
}
```

#### C. Module Declaration

**Extended ModuleDefinition:**
```csharp
public record ModuleDefinition
{
    public required string Id { get; init; }
    
    // Execution
    public bool IsSynchronous { get; init; } = true;
    public int TargetFrequencyHz { get; init; } = 0;  // 0 = synchronous only
    
    // NEW: Event-driven scheduling
    public Type[] WatchComponents { get; init; } = Array.Empty<Type>();
    public Type[] WatchEvents { get; init; } = Array.Empty<Type>();
    
    // If ANY watched component/event changes → wake module
    // If TargetFrequencyHz > 0, ALSO wake on timer
}
```

**Example Module:**
```csharp
public class AIModule : IModule
{
    public ModuleDefinition GetDefinition() => new()
    {
        Id = "AI",
        IsSynchronous = false,
        TargetFrequencyHz = 10,  // Runs at 10Hz normally
        
        // NEW: Wake on changes
        WatchComponents = new[]
        {
            typeof(Health),         // Wake if any health changes
            typeof(TargetDescriptor) // Wake if any target assignment
        },
        
        WatchEvents = new[]
        {
            typeof(ExplosionEvent),  // Wake immediately on explosion
            typeof(DetectionEvent)   // Wake on enemy detected
        }
    };
}
```

#### D. Scheduler Implementation

**BackgroundScheduler with Event-Driven Logic:**
```csharp
public class BackgroundScheduler
{
    private struct ScheduledModule
    {
        public IModule Module;
        public ModuleDefinition Definition;
        public int TargetHz;
        public ulong LastRunFrame;
        public uint LastRunTick;
        public Task CurrentTask;
    }
    
    public IEnumerable<IModule> GetReadyModules(
        ulong currentFrame,
        EntityRepository repository,
        FdpEventBus eventBus)
    {
        foreach (var sm in _scheduled)
        {
            bool shouldRun = false;
            
            // 1. Check for events (HIGH PRIORITY - interrupts timer)
            if (sm.Definition.WatchEvents.Length > 0)
            {
                foreach (var eventType in sm.Definition.WatchEvents)
                {
                    if (eventBus.HasEventsByType(eventType))
                    {
                        shouldRun = true;
                        break;  // Event detected → run immediately
                    }
                }
            }
            
            // 2. If no events, check timer + component changes
            if (!shouldRun && sm.TargetHz > 0)
            {
                ulong framesSinceLastRun = currentFrame - sm.LastRunFrame;
                ulong framesPerTick = 60 / (ulong)sm.TargetHz;  // e.g., 10Hz = every 6 frames
                
                bool timerDue = framesSinceLastRun >= framesPerTick;
                
                if (timerDue)
                {
                    // Check component changes
                    if (sm.Definition.WatchComponents.Length > 0)
                    {
                        foreach (var compType in sm.Definition.WatchComponents)
                        {
                            if (repository.HasComponentChangedByType(compType, sm.LastRunTick))
                            {
                                shouldRun = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // No component watch → run on timer alone
                        shouldRun = true;
                    }
                }
            }
            
            if (shouldRun)
            {
                yield return sm.Module;
            }
        }
    }
}
```

**Key Insight from FDP-module-scheduling-support.md:**
> "Events are high-priority interrupts. If a module depends on Events, the Scheduler **MUST ignore the Timer** if an event is present."

**Implementation:**
- Events trigger immediately (interrupt timer)
- Component changes require timer to be due (prevents thrashing)
- If both exist, module runs

---

## Complete FDP API Requirements Summary

### Part 1: Event History

| API | Complexity | Priority | Notes |
|-----|------------|----------|-------|
| `FdpEventBus.SwapBuffers(frameNumber)` | HIGH | P0 | Retire to history, not clear |
| `FdpEventBus.GetEventHistory(from, to, mask)` | HIGH | P0 | With event type filtering |
| `FdpEventBus.PruneHistory(cutoff)` | MEDIUM | P0 | Safe pruning (check active snapshots) |
| `NativeEventStream.SwapAndRetire()` | HIGH | P0 | Dynamic buffer growth |
| Buffer expansion logic | MEDIUM | P0 | No fixed limits |

### Part 2: Component Snapshots

| API | Complexity | Priority | Notes |
|-----|------------|----------|-------|
| `EntityRepository.GetChunkVersion<T>(idx)` | LOW | P1 | Expose existing field |
| `EntityRepository.GetChunkPtr<T>(idx)` | LOW | P1 | Expose existing field |
| `ManagedComponentTable.GetSnapshotArray()` | MEDIUM | P1 | Shallow copy |

### Part 3: Event-Driven Scheduling

| API | Complexity | Priority | Notes |
|-----|------------|----------|-------|
| `NativeChunkTable.HasChanges(sinceVersion)` | **LOW** | **P0** | Critical for scheduling |
| `EntityRepository.HasComponentChanged<T>(tick)` | LOW | P0 | Generic wrapper |
| `EntityRepository.HasComponentChangedByType(type, tick)` | LOW | P0 | Non-generic for scheduler |
| `FdpEventBus.HasEvents<T>()` | LOW | P0 | Check without consuming |
| `FdpEventBus.HasEventsByType(type)` | LOW | P0 | Non-generic for scheduler |
| `IComponentTable.HasChanges(tick)` | LOW | P0 | Interface contract |

---

## Implementation Plan (Revised)

### Week 1: Event-Driven Scheduling (Highest Value First!)

**Why First:** Enables smart scheduling immediately, blocks nothing else

- [ ] Add `HasChanges()` to `NativeChunkTable` (10 lines)
- [ ] Add `HasComponentChanged()` to `EntityRepository` (5 lines)
- [ ] Add `HasEvents()` to `FdpEventBus` (15 lines)
- [ ] Add `_activeEventIds` tracking to `Publish()` (5 lines)
- [ ] Test: Module wakes on component change
- [ ] Test: Module wakes on event arrival

**Deliverable:** Smart scheduling works (before full event history)

### Week 2: Event History Foundation

- [ ] Refactor `FdpEventBus.SwapBuffers()` → history retention
- [ ] Implement dynamic buffer growth (no data loss)
- [ ] Implement `SwapAndRetire()` in `NativeEventStream`
- [ ] Add `GetEventHistory()` with filtering
- [ ] Add `PruneHistory()` with safety checks

**Deliverable:** Events retained for background modules

### Week 3: Component Snapshots

- [ ] Add component snapshot APIs to FDP
- [ ] Implement `SnapshotManager` in ModuleHost
- [ ] Integrate event filtering via `EventTypeMask`
- [ ] Test with 180-frame history (3 seconds @ 60Hz)

**Deliverable:** Full snapshots (components + events)

### Week 4: Integration & Validation

- [ ] Test long-running module (2 second AI)
- [ ] Test buffer overflow (10K events)
- [ ] Test event filtering (verify performance)
- [ ] Test event-driven wake (verify latency)

**Deliverable:** Production-ready system

---

## Performance Characteristics

### Event-Driven Scheduling

**Cost to check if module should run:**
- Component check: Scan ~100 uint64s = **<100ns**
- Event check: HashSet lookup = **O(1) ~10ns**
- **Total:** **<200ns per module** (vs. waking thread = ~50μs)

**Benefit:** 250x faster than waking a thread!

### Event History Filtering

**Scenario:** 100K physics events per frame, AI needs 10 gameplay events

**Without filtering:** 100K * 180 frames = **18M events** in snapshot
**With filtering:** 10 * 180 frames = **1.8K events** in snapshot

**Bandwidth saved:** 99.99%!

---

**Status:** ✅ All requirements updated with feedback. Ready for implementation!
