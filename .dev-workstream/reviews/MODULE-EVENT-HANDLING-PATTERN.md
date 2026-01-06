# Event Handling in Modules - Architectural Guidance

**Date:** January 5, 2026  
**Source:** Architecture clarification  
**Status:** ✅ **CANONICAL** - Follow this pattern

---

## ✅ The Right Way: ISimulationView.ConsumeEvents<T>()

**Modules MUST access events ONLY through `ISimulationView`:**

```csharp
public class CombatModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // ✅ CORRECT: Consume through view
        ReadOnlySpan<CollisionEvent> collisions = view.ConsumeEvents<CollisionEvent>();
        
        foreach (ref readonly var collision in collisions)
        {
            // Process event
        }
    }
}
```

---

## ❌ The Wrong Way: Direct FdpEventBus Access

**NEVER do this:**

```csharp
public class CombatModule : IModule
{
    private readonly FdpEventBus _bus; // ❌ WRONG!
    
    public CombatModule(FdpEventBus bus) { _bus = bus; } // ❌ WRONG!
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var events = _bus.Consume<CollisionEvent>(); // ❌ RACE CONDITION!
    }
}
```

---

## Why ISimulationView is Required

### 1. Time Dilation & Frequency Mismatch

**Problem:** AI module runs at 10Hz (every 6 frames). If it reads live EventBus directly:
- Frame 1-5: Events published
- Frame 6: AI module reads... **MISSES events from frames 1-5!**

**Solution:** `EventAccumulator` stores history. `ISimulationView.ConsumeEvents` returns **all events since last module execution**.

**Example:**
```
Frame 1-5: 15 DamageEvents published to live bus
Frame 6: AIModule.Tick called
  → view.ConsumeEvents<DamageEvent>() returns ALL 15 events
  → EventAccumulator cleared for this module
```

---

### 2. Thread Safety

**Problem:** Modules run on background threads. Reading live FdpEventBus = race condition.

**Solution:** `ISimulationView` backed by **isolated EventBus replica**:
- ModuleHostKernel flushes events Accumulator → Replica EventBus
- Module reads from **its own private copy**
- No contention with main thread

---

### 3. Bandwidth Filtering (SoD Providers)

**Problem:** Analytics module doesn't need 1000 physics events per frame.

**Solution:**
```csharp
public class AnalyticsModule : IModule
{
    // Declare what you need (SoD provider reads this)
    public EventTypeMask GetEventRequirements()
    {
        var mask = new EventTypeMask();
        mask.Set(typeof(KillEvent));     // Only need kills
        // NOT setting CollisionEvent      // Don't waste bandwidth
        return mask;
    }
}
```

SoD provider copies **ONLY** KillEvents to snapshot → bandwidth saved!

---

## Event Publishing from Modules - Complete Implementation

**Architecture:** Events from modules MUST go through `IEntityCommandBuffer`, just like component modifications.

**Why:** Modules run on background threads. Direct FdpEventBus access = race conditions.

---

### Implementation Steps

#### 1. Extend IEntityCommandBuffer Interface

**File:** `ModuleHost.Core/Abstractions/IEntityCommandBuffer.cs`

```csharp
public interface IEntityCommandBuffer
{
    // Existing methods
    Entity CreateEntity();
    void SetComponent<T>(Entity entity, T component) where T : struct;
    void DestroyEntity(Entity entity);
    void AddComponent<T>(Entity entity, T component) where T : struct;
    
    // NEW: Event Publishing
    void PublishEvent<T>(T evt) where T : unmanaged;
    void PublishManagedEvent<T>(T evt) where T : class;
}
```

---

#### 2. Update EntityCommandBuffer Implementation

**File:** `Fdp.Kernel/EntityCommandBuffer.cs`

**Add OpCodes:**
```csharp
private enum OpCode : byte
{
    CreateEntity = 0,
    SetComponent = 1,
    DestroyEntity = 2,
    AddComponent = 3,
    // ... existing ...
    PublishEvent = 8,           // NEW: Unmanaged event
    PublishManagedEvent = 9     // NEW: Managed event
}
```

**Add Recording Logic:**
```csharp
// Recording: Unmanaged Event
public void PublishEvent<T>(T evt) where T : unmanaged
{
    int size = Unsafe.SizeOf<T>();
    int typeId = EventType<T>.Id; // Requires EventType<T> registry
    
    EnsureCapacity(1 + 4 + 4 + size); // Op + TypeID + Size + Data
    
    _buffer[_position++] = (byte)OpCode.PublishEvent;
    WriteInt(typeId);
    WriteInt(size);
    
    // Copy event data to buffer
    fixed (byte* dst = &_buffer[_position])
    {
        Unsafe.Copy(dst, ref evt);
    }
    _position += size;
}

// Recording: Managed Event
public void PublishManagedEvent<T>(T evt) where T : class
{
    int typeId = GetManagedEventTypeId<T>(); // Similar to ComponentType
    int objIndex = _managedObjects.Count;
    _managedObjects.Add(evt); // Store reference
    
    EnsureCapacity(1 + 4 + 4);
    _buffer[_position++] = (byte)OpCode.PublishManagedEvent;
    WriteInt(typeId);
    WriteInt(objIndex);
}
```

---

#### 3. Update Playback Logic

**Add Event Playback Cases:**
```csharp
public unsafe void Playback(EntityRepository repo)
{
    int readPos = 0;
    
    while (readPos < _position)
    {
        var opCode = (OpCode)_buffer[readPos++];
        
        switch (opCode)
        {
            // ... existing cases ...
            
            case OpCode.PublishEvent:
            {
                int typeId = ReadInt(ref readPos);
                int size = ReadInt(ref readPos);
                
                // Publish raw bytes to event bus
                fixed (byte* ptr = &_buffer[readPos])
                {
                    // Use shim to invoke generic Publish<T> based on cached typeId
                    EventPlaybackShim.PublishUnmanaged(repo.EventBus, typeId, (IntPtr)ptr);
                }
                
                readPos += size;
                break;
            }
            
            case OpCode.PublishManagedEvent:
            {
                int typeId = ReadInt(ref readPos);
                int objIndex = ReadInt(ref readPos);
                object evt = _managedObjects[objIndex];
                
                // Publish managed event
                EventPlaybackShim.PublishManaged(repo.EventBus, typeId, evt);
                break;
            }
        }
    }
    
    Clear(); // Reset buffer for reuse
}
```

---

#### 4. EventPlaybackShim Helper

**File:** `Fdp.Kernel/Internal/EventPlaybackShim.cs`

```csharp
internal static class EventPlaybackShim
{
    // Cache of Publish<T> methods by typeId
    private static readonly Dictionary<int, Delegate> _unmanaged = new();
    private static readonly Dictionary<int, Delegate> _managed = new();
    
    public static unsafe void PublishUnmanaged(FdpEventBus bus, int typeId, IntPtr data)
    {
        if (!_unmanaged.TryGetValue(typeId, out var del))
        {
            // Build delegate: void Publish<T>(T evt)
            var eventType = EventTypeRegistry.GetType(typeId); // Get Type from ID
            var publishMethod = typeof(FdpEventBus)
                .GetMethod(nameof(FdpEventBus.Publish))
                .MakeGenericMethod(eventType);
            
            del = CreatePublishDelegate(publishMethod, eventType);
            _unmanaged[typeId] = del;
        }
        
        // Invoke: Publish<T>(*(T*)data)
        InvokeUnmanagedPublish(del, bus, data);
    }
    
    public static void PublishManaged(FdpEventBus bus, int typeId, object evt)
    {
        if (!_managed.TryGetValue(typeId, out var del))
        {
            var eventType = EventTypeRegistry.GetType(typeId);
            var publishMethod = typeof(FdpEventBus)
                .GetMethod(nameof(FdpEventBus.PublishManaged))
                .MakeGenericMethod(eventType);
            
            del = Delegate.CreateDelegate(
                typeof(Action<,>).MakeGenericType(typeof(FdpEventBus), eventType),
                publishMethod
            );
            _managed[typeId] = del;
        }
        
        // Invoke: PublishManaged<T>(evt)
        ((dynamic)del).Invoke(bus, evt);
    }
}
```

---

### Module Usage Pattern

**In any module:**
```csharp
public class CombatModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Detect collision
        if (collision)
        {
            // ✅ CORRECT: Queue event via command buffer
            cmd.PublishEvent(new DamageEvent
            {
                Victim = player,
                Attacker = projectile,
                Amount = 10.0f,
                Tick = view.Tick
            });
            
            // Check for kill
            if (health <= 0)
            {
                cmd.PublishEvent(new KillEvent
                {
                    Victim = player,
                    Killer = attacker,
                    Position = position,
                    Tick = view.Tick
                });
            }
        }
        
        // ❌ WRONG: NEVER access FdpEventBus directly
        // _eventBus.Publish(evt); // RACE CONDITION!
    }
}
```

---

### Why This Architecture

**1. Ordering Guarantee**
- Events generated by modules happen logically at Phase 3 (playback)
- Prevents events appearing mid-physics update
- Deterministic sequence

**2. Thread Safety**
- No locks on EventBus during module execution
- Events queued in thread-local command buffers
- Playback on main thread only

**3. Determinism**
- Event sequence determined by playback order
- Easier debugging and replay
- Consistent across runs

**4. Consistency with Component Mutations**
- Same pattern: queue mutation, playback on main thread
- Developers learn one pattern
- Reduces cognitive load

---

## For DEMO Batches

**DEMO-03 (Current):**
- Continue commenting out event publishing
- Use `// TODO: Add when command buffer supports PublishEvent`

**DEMO-04 (Implement Event Publishing):**
- Add `PublishEvent` / `PublishManagedEvent` to `IEntityCommandBuffer`
- Implement OpCodes in `EntityCommandBuffer`
- Create `EventPlaybackShim` helper
- Update PhysicsModule to publish DamageEvent/KillEvent
- Add tests for event publishing
- Update documentation

---

**This is the canonical implementation for event publishing in modules!** 🎯


---

## Implementation Checklist

**Ensure ISimulationView provides:**

```csharp
public interface ISimulationView
{
    // Unmanaged events (Tier 1)
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    
    // Managed events (Tier 2)
    IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class;
    
    // Command buffer for mutations + event publishing
    IEntityCommandBuffer GetCommandBuffer();
}
```

**In EntityRepository.View.cs:**
```csharp
ReadOnlySpan<T> ISimulationView.ConsumeEvents<T>() where T : unmanaged
{
    // Delegates to this repository's EventBus (might be replica)
    // EventAccumulator has already populated this bus with history
    return _eventBus.Consume<T>();
}
```

---

## Module Pattern Summary

```csharp
public class ExampleModule : IModule
{
    public string Name => "Example";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6; // 10 Hz
    
    // 1. Declare event requirements (for SoD filtering)
    public EventTypeMask GetEventRequirements()
    {
        var mask = new EventTypeMask();
        mask.Set(typeof(DamageEvent));
        return mask;
    }
    
    // 2. Consume events through view (guaranteed history)
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Read events (accumulated since last run)
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        
        foreach (ref readonly var dmg in damageEvents)
        {
            // React to damage
            if (dmg.Amount > 50)
            {
                // TODO: Publish event when command buffer supports it
                // cmd.PublishEvent(new HighDamageAlert { ... });
            }
        }
    }
}
```

---

## For DEMO Batches

**DEMO-02 (Current):** Modules consume events correctly ✅

**DEMO-03 (Current):** Continue using `view.ConsumeEvents` ✅

**DEMO-04 (Planned):**
- Add `IEntityCommandBuffer.PublishEvent<T>(T evt)`
- Implement command playback for events
- Update PhysicsModule to publish DamageEvent/KillEvent
- Update docs with event publishing pattern

---

**This is the authoritative pattern - follow strictly!** 🎯
