# BATCH-02: FDP Event System & ISimulationView

**Phase:** Week 2 - FDP Synchronization Core (Part 2)  
**Difficulty:** Medium-High  
**Story Points:** 13  
**Estimated Duration:** 2-3 days  
**Dependencies:** BATCH-01 (SyncFrom must work correctly)

---

## 📋 Batch Overview

This batch implements the **event accumulation system** and the **unified read-only interface** for background modules. The EventAccumulator bridges the event stream from live world to replicas, enabling slow modules to see accumulated event history. ISimulationView provides a clean, unified API for both GDB and SoD scenarios.

**Critical Success Factors:**
- Event accumulation must preserve all events (no loss)
- Buffer pooling to avoid allocations
- ISimulationView must be simple and consistent
- EntityRepository must implement ISimulationView natively

---

## 📚 Required Reading

**Before starting, read these documents:**

1. **Primary References:**
   - `/docs/API-REFERENCE.md` - Sections: EventAccumulator, ISimulationView
   - `/docs/HYBRID-ARCHITECTURE-QUICK-REFERENCE.md` - Event history section
   - `/docs/detailed-design-overview.md` - Layer 0: Event Accumulator

2. **Design Context:**
   - `/docs/reference-archive/FDP-GDB-SoD-unified.md` - Section: Event Accumulator design
   - `/docs/IMPLEMENTATION-TASKS.md` - Tasks 005-007

**Key Concepts to Understand:**
- Why slow modules need event history (they run every 6 frames, miss events)
- How EventAccumulator captures and flushes events
- Buffer pooling strategy (zero allocations per frame)
- ISimulationView as abstraction over EntityRepository and SimSnapshot

---

## 🎯 Tasks in This Batch

### TASK-005: EventAccumulator Implementation (8 SP)

**Priority:** P0 (Critical Path)  
**Files:** 
- `Fdp.Kernel/EventAccumulator.cs` (new)
- `Fdp.Kernel/FdpEventBus.cs` (modifications)

**Description:**  
Implement event history capture and replay system that allows background modules to see events from frames they missed.

**Acceptance Criteria:**
- [ ] Class: `EventAccumulator` with methods `CaptureFrame()` and `FlushToReplica()`
- [ ] Captures events from live bus without clearing them
- [ ] Stores frame index with each capture
- [ ] Flushes accumulated events to replica bus (filtered by lastSeenTick)
- [ ] Handles both native (unmanaged) and managed events
- [ ] Zero allocations per frame (buffer pooling)
- [ ] Performance: <100μs to flush 6 frames (1K events/frame)

**Implementation Notes:**

```csharp
// File: Fdp.Kernel/EventAccumulator.cs
namespace Fdp.Kernel
{
    /// <summary>
    /// Captures event history from live bus and flushes to replica buses.
    /// Enables slow modules to see accumulated events since last run.
    /// </summary>
    public sealed class EventAccumulator
    {
        private readonly Queue<FrameEventData> _history = new();
        private readonly int _maxHistoryFrames;
        
        public EventAccumulator(int maxHistoryFrames = 10)
        {
            _maxHistoryFrames = maxHistoryFrames;
        }
        
        /// <summary>
        /// Captures events from live bus for a frame.
        /// Called on main thread after simulation phase.
        /// </summary>
        public void CaptureFrame(FdpEventBus liveBus, uint frameIndex)
        {
            // Extract event buffers (non-destructive)
            var frameData = liveBus.SnapshotCurrentBuffers();
            frameData.FrameIndex = frameIndex;
            
            _history.Enqueue(frameData);
            
            // Trim old history
            while (_history.Count > _maxHistoryFrames)
            {
                var old = _history.Dequeue();
                old.Dispose(); // Return buffers to pool
            }
        }
        
        /// <summary>
        /// Flushes accumulated history to replica bus.
        /// Only events AFTER lastSeenTick are flushed.
        /// Called on main thread during sync point.
        /// </summary>
        public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick)
        {
            foreach (var frameData in _history)
            {
                if (frameData.FrameIndex <= lastSeenTick)
                    continue; // Already seen
                
                // Inject events into replica bus (append to existing)
                replicaBus.InjectEvents(frameData);
            }
        }
    }
    
    /// <summary>
    /// Captured event data for a single frame.
    /// Pooled to avoid allocations.
    /// </summary>
    public struct FrameEventData : IDisposable
    {
        public uint FrameIndex;
        public List<(int TypeId, byte[] Buffer)> NativeEvents;
        public List<(int TypeId, object[] Objects)> ManagedEvents;
        
        public void Dispose()
        {
            // Return buffers to pool
            // (Implementation details)
        }
    }
}
```

**FdpEventBus Modifications:**

Add these methods to `FdpEventBus`:

```csharp
// File: Fdp.Kernel/FdpEventBus.cs

/// <summary>
/// Creates a snapshot of current event buffers without consuming them.
/// Used by EventAccumulator to capture history.
/// </summary>
public FrameEventData SnapshotCurrentBuffers()
{
    var data = new FrameEventData();
    data.NativeEvents = new List<(int, byte[])>();
    data.ManagedEvents = new List<(int, object[])>();
    
    // Copy native events
    foreach (var stream in _nativeStreams.Values)
    {
        var buffer = stream.GetReadBuffer(); // Get current buffer
        var copy = new byte[buffer.Length];
        Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length);
        data.NativeEvents.Add((stream.TypeId, copy));
    }
    
    // Copy managed events
    foreach (var stream in _managedStreams.Values)
    {
        var list = stream.GetReadList(); // Get current list
        var copy = list.ToArray(); // Shallow copy (events are immutable)
        data.ManagedEvents.Add((stream.TypeId, copy));
    }
    
    return data;
}

/// <summary>
/// Injects pre-captured events into this bus.
/// Used by EventAccumulator to replay history to replicas.
/// </summary>
public void InjectEvents(FrameEventData frameData)
{
    // Inject native events
    foreach (var (typeId, buffer) in frameData.NativeEvents)
    {
        if (_nativeStreams.TryGetValue(typeId, out var stream))
        {
            stream.InjectBuffer(buffer); // Append to current
        }
    }
    
    // Inject managed events
    foreach (var (typeId, objects) in frameData.ManagedEvents)
    {
        if (_managedStreams.TryGetValue(typeId, out var stream))
        {
            stream.InjectObjects(objects); // Append to current
        }
    }
}
```

**Tests Required (6 tests):**

Create file: `Fdp.Tests/EventAccumulatorTests.cs`

1. **CaptureFrame_StoresEventData**
   - Setup: Live bus with events
   - Execute: `CaptureFrame()`
   - Verify: Events captured in history queue

2. **CaptureMultipleFrames_MaintainsOrder**
   - Setup: Capture frames 1, 2, 3
   - Verify: Queue has correct order and frame indices

3. **FlushToReplica_InjectsAccumulatedEvents**
   - Setup: History has frames 1-6, lastSeenTick=3
   - Execute: `FlushToReplica(replicaBus, 3)`
   - Verify: Only frames 4-6 injected

4. **FlushToReplica_HandlesNativeEvents**
   - Setup: Native events (ExplosionEvent) in history
   - Execute: Flush
   - Verify: Replica bus receives native events correctly

5. **FlushToReplica_HandlesManagedEvents**
   - Setup: Managed events (DamageEvent) in history
   - Execute: Flush
   - Verify: Replica bus receives managed events correctly

6. **Performance_FlushSixFrames_UnderTarget**
   - Setup: 6 frames, 1K events/frame
   - Execute: `FlushToReplica()`
   - Verify: Completes in <100μs

---

### TASK-006: ISimulationView Interface (3 SP)

**Priority:** P0 (Required by TASK-007)  
**File:** `ModuleHost.Core/Abstractions/ISimulationView.cs` (new)

**Description:**  
Define the unified read-only interface for accessing simulation state. This replaces the old `ISimWorldSnapshot` and works for both GDB and SoD scenarios.

**Acceptance Criteria:**
- [ ] Interface defined with properties: `Tick`, `Time`
- [ ] Methods: `GetComponentRO<T>()`, `GetManagedComponentRO<T>()`, `IsAlive()`, `ConsumeEvents<T>()`, `Query()`
- [ ] No IDisposable (GDB replicas don't need disposal)
- [ ] Complete XML documentation
- [ ] Compiles without errors

**Implementation:**

```csharp
// File: ModuleHost.Core/Abstractions/ISimulationView.cs
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Read-only view of simulation state.
    /// Abstraction over EntityRepository (GDB) and SimSnapshot (SoD).
    /// Modules use this interface without knowing the underlying strategy.
    /// </summary>
    public interface ISimulationView
    {
        /// <summary>
        /// Current simulation tick (frame number).
        /// </summary>
        uint Tick { get; }
        
        /// <summary>
        /// Current simulation time in seconds.
        /// </summary>
        float Time { get; }
        
        /// <summary>
        /// Gets read-only reference to unmanaged component (Tier 1).
        /// Throws if entity doesn't have component.
        /// </summary>
        ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
        
        /// <summary>
        /// Gets managed component (Tier 2).
        /// Returns immutable record/class.
        /// Throws if entity doesn't have component.
        /// </summary>
        T GetManagedComponentRO<T>(Entity e) where T : class;
        
        /// <summary>
        /// Checks if entity is alive (not destroyed).
        /// </summary>
        bool IsAlive(Entity e);
        
        /// <summary>
        /// Consumes all accumulated events of type T.
        /// Returns zero-copy span of events.
        /// Events include history since module's last run.
        /// </summary>
        ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
        
        /// <summary>
        /// Creates a query builder for iterating entities.
        /// </summary>
        EntityQueryBuilder Query();
    }
}
```

**Tests Required (3 tests):**

Create file: `ModuleHost.Core.Tests/ISimulationViewTests.cs`

1. **Interface_Compiles**
   - Verify: ISimulationView compiles without errors

2. **Interface_HasAllRequiredMembers**
   - Verify: Tick, Time, GetComponentRO, GetManagedComponentRO, IsAlive, ConsumeEvents, Query all present

3. **Interface_NoDisposable**
   - Verify: ISimulationView does NOT inherit IDisposable

---

### TASK-007: EntityRepository Implements ISimulationView (2 SP)

**Priority:** P0 (Enables GDB scenario)  
**File:** `Fdp.Kernel/EntityRepository.cs` (modification)

**Description:**  
Make EntityRepository implement ISimulationView natively. This allows GDB providers to return the repository directly as a view (zero overhead).

**Acceptance Criteria:**
- [ ] `EntityRepository : ISimulationView` declaration
- [ ] All interface members implemented
- [ ] Tick property maps to `GlobalVersion`
- [ ] Time property maps to `SimulationTime`
- [ ] Zero overhead (direct passthrough to existing methods)
- [ ] Compiles without errors

**Implementation:**

```csharp
// File: Fdp.Kernel/EntityRepository.cs

public sealed partial class EntityRepository : ISimulationView
{
    // Properties
    uint ISimulationView.Tick => _globalVersion;
    
    float ISimulationView.Time => _simulationTime;
    
    // Methods (most already exist, just expose via interface)
    
    ref readonly T ISimulationView.GetComponentRO<T>(Entity e)
    {
        // Delegate to existing GetComponentRO
        return ref GetComponentRO<T>(e);
    }
    
    T ISimulationView.GetManagedComponentRO<T>(Entity e)
    {
        // Delegate to existing method
        return GetManagedComponentRO<T>(e);
    }
    
    bool ISimulationView.IsAlive(Entity e)
    {
        return IsAlive(e);
    }
    
    ReadOnlySpan<T> ISimulationView.ConsumeEvents<T>()
    {
        // Delegate to event bus
        return _eventBus.ConsumeEvents<T>();
    }
    
    EntityQueryBuilder ISimulationView.Query()
    {
        return Query();
    }
}
```

**Tests Required (4 tests):**

Create file: `Fdp.Tests/EntityRepositoryAsViewTests.cs`

1. **EntityRepository_ImplementsISimulationView**
   - Verify: Can cast EntityRepository to ISimulationView

2. **Tick_ReturnsGlobalVersion**
   - Setup: GlobalVersion = 42
   - Verify: ((ISimulationView)repo).Tick == 42

3. **GetComponentRO_WorksThroughInterface**
   - Setup: Entity with Position component
   - Execute: view.GetComponentRO<Position>(entity)
   - Verify: Returns correct component

4. **AllMethods_WorkThroughInterface**
   - Verify: All ISimulationView methods callable via interface
   - Verify: No boxing/overhead (reference equality checks)

---

## 🔍 Integration Tests

**After all 3 tasks complete**, create integration test:

**File:** `Fdp.Tests/Integration/EventAccumulationIntegrationTests.cs`

### Integration Test: EventHistoryForSlowModule

```csharp
[Fact]
public void EventHistory_SlowModule_SeesAccumulatedEvents()
{
    using var live = new EntityRepository();
    using var replica = new EntityRepository(); // GDB replica
    
    var accumulator = new EventAccumulator();
    
    // Simulate 6 frames
    for (uint frame = 1; frame <= 6; frame++)
    {
        // Live world generates events
        live.Bus.PublishEvent(new ExplosionEvent { Frame = frame });
        
        // Capture event history
        accumulator.CaptureFrame(live.Bus, frame);
        
        // Advance frame
        live.Bus.SwapBuffers(); // Next frame
    }
    
    // Slow module runs at frame 6 (last saw frame 0)
    replica.SyncFrom(live);
    accumulator.FlushToReplica(replica.Bus, lastSeenTick: 0);
    
    // Verify: Replica sees ALL 6 events
    var events = replica.Bus.ConsumeEvents<ExplosionEvent>();
    Assert.Equal(6, events.Length);
    
    for (int i = 0; i < 6; i++)
    {
        Assert.Equal((uint)(i + 1), events[i].Frame);
    }
}

[Fact]
public void EventHistory_FiltersOldEvents()
{
    // Setup: Module last saw frame 3
    // History has frames 1-6
    
    accumulator.FlushToReplica(replica.Bus, lastSeenTick: 3);
    
    // Verify: Only frames 4-6 flushed
    var events = replica.Bus.ConsumeEvents<ExplosionEvent>();
    Assert.Equal(3, events.Length);
    Assert.Equal(4u, events[0].Frame);
    Assert.Equal(5u, events[1].Frame);
    Assert.Equal(6u, events[2].Frame);
}
```

---

## ⚠️ Critical Rules

**Mandatory Requirements:**

1. ⛔ **NO event loss** - Accumulator must capture ALL events
2. ⛔ **Buffer pooling required** - Zero allocations per frame (reuse buffers)
3. ⛔ **ISimulationView must be simple** - No complex APIs
4. ⛔ **Zero overhead for EntityRepository** - Direct passthrough
5. ⛔ **Frame index tracking** - Must filter by lastSeenTick correctly

**Performance Constraints:**

- EventAccumulator.CaptureFrame: Must not block simulation
- EventAccumulator.FlushToReplica: <100μs for 6 frames
- ISimulationView methods: Same performance as direct EntityRepository calls

**Architecture Constraints:**

- EventAccumulator called on main thread (sync point)
- No locks needed (phase-based execution)
- FdpEventBus modifications must preserve existing behavior
- ISimulationView: No IDisposable (GDB replicas persist)

---

## 📊 Success Metrics

**Batch is DONE when:**

- [x] All 3 tasks complete (TASK-005 through TASK-007)
- [x] All 13 unit tests passing
- [x] 2 integration tests passing
- [x] Zero compiler warnings
- [x] Performance benchmarks pass:
  - EventAccumulator flush: <100μs for 6 frames
  - ISimulationView overhead: Zero (reference equality)

---

## 🚨 Common Pitfalls

**Watch Out For:**

1. **Event buffer ownership** - Don't dispose buffers still in accumulator
2. **Shallow copy for managed events** - Events must be immutable
3. **Frame index off-by-one** - Test boundary conditions (lastSeenTick = N)
4. **Buffer aliasing** - Ensure snapshot creates independent copies
5. **ISimulationView boxing** - Explicit interface implementation to avoid boxing

---

## 💡 Implementation Tips

**Best Practices:**

1. **Start with TASK-006** (ISimulationView) - Simple interface definition
2. **Then TASK-007** (EntityRepository impl) - Straightforward passthrough
3. **Finally TASK-005** (EventAccumulator) - Most complex, benefits from interface being ready

**Testing Strategy:**

1. Test EventAccumulator with simple native events first
2. Add managed event tests
3. Test filtering (lastSeenTick)
4. Integration tests validate full flow

**Performance:**

- Use ArrayPool<byte> for event buffers if needed
- Avoid LINQ in hot paths
- Profile if flush takes >100μs

---

## 📋 Deliverables

**When batch complete, submit:**

1. **Batch Report:** `reports/BATCH-02-REPORT.md`
2. **Questions (if any):** `reports/BATCH-02-QUESTIONS.md`
3. **Blockers (if any):** `reports/BLOCKERS-ACTIVE.md`

**Report Must Include:**

- Status of all 3 tasks
- Test results (13 unit + 2 integration)
- Performance measurements
- Files created/modified list
- Integration test outcomes

---

## 🎯 Next Batch Preview

**BATCH-03** (following this) will implement:
- ISnapshotProvider interface
- DoubleBufferProvider (GDB)
- OnDemandProvider (SoD)
- SharedSnapshotProvider (convoy)

These depend on EventAccumulator and ISimulationView working correctly!

---

**Questions? Create:** `reports/BATCH-02-QUESTIONS.md`  
**Blocked? Update:** `reports/BLOCKERS-ACTIVE.md`  
**Done? Submit:** `reports/BATCH-02-REPORT.md`

**Good luck! 🚀**
