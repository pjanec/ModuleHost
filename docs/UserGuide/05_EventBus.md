## Event Bus

### Overview

The **FdpEventBus** is a high-performance, lock-free event communication system designed for frame-based simulations. It enables **decoupled communication** between systems through a double-buffering mechanism that guarantees **deterministic**, **thread-safe** event delivery.

**What Problems Does the Event Bus Solve:**
- **System Decoupling:** Systems communicate without direct references
- **Temporal Ordering:** Events published in frame N visible in frame N+1 (predictable)
- **Thread Safety:** Lock-free publishing from multiple threads
- **Zero Garbage:** Stack-allocated `ReadOnlySpan<T>` for consumption
- **High Throughput:** 1M+ events/second single-threaded, 500K+ multi-threaded

**When to Use Events:**
- **Commands:** Player input, AI decisions (`JumpCommand`, `AttackCommand`)
- **Notifications:** Damage dealt, achievements unlocked (`DamageEvent`, `DeathEvent`)
- **Triggers:** Explosions, collisions (`ExplosionEvent`, `CollisionEvent`)
- **Chain Reactions:** Events that trigger other events

---

### Core Concepts

#### Double Buffering Architecture

The event bus uses **two buffers** per event type:
- **PENDING Buffer:** Receives events published this frame
- **CURRENT Buffer:** Contains events from last frame (readable by systems)

**Lifecycle:**
```
Frame N:
  ┌─────────────┐
  │ PENDING     │ ◄─── Publish() writes here
  │ [JumpCmd]   │
  └─────────────┘
  
  ┌─────────────┐
  │ CURRENT     │ ◄─── Consume() reads from here (empty)
  │ []          │
  └─────────────┘

End of Frame N: SwapBuffers()
  ↓ Buffers swap

Frame N+1:
  ┌─────────────┐
  │ PENDING     │ ◄─── Ready for new events
  │ []          │
  └─────────────┘
  
  ┌─────────────┐
  │ CURRENT     │ ◄─── JumpCmd now visible!
  │ [JumpCmd]   │
  └─────────────┘
```

**Key Insight:** Events published in frame N are consumed in frame N+1. This **1-frame delay** is intentional for thread safety and determinism.

---

#### Event Type Registry

Every event type MUST have a unique ID specified via the `[EventId(n)]` attribute.

**Basic Event Definition:**
```csharp
using Fdp.Kernel;

[EventId(1)]
public struct DamageEvent
{
    public Entity Target;
    public float Amount;
    public Entity Source;
}

[EventId(2)]
public struct ExplosionEvent
{
    public float X, Y, Z;
    public float Radius;
    public int ParticleCount;
}
```

**Rules:**
- ✅ **Must be struct** (value type)
- ✅ **Must have `[EventId(n)]` attribute**
- ✅ **IDs must be unique** across all event types in your simulation
- ✅ **Should be unmanaged** (no managed references) for best performance
- ❌ **Missing `[EventId]` throws `InvalidOperationException`** at runtime

**Event Type ID Access:**
```csharp
int damageId = EventType<DamageEvent>.Id;  // Returns 1
int explosionId = EventType<ExplosionEvent>.Id; // Returns 2
```

---

#### Publish/Consume Pattern

**Publishing Events:**
```csharp
// From any thread, any time during frame
bus.Publish(new DamageEvent 
{ 
    Target = enemy, 
    Amount = 50.0f,
    Source = player 
});
```

**Consuming Events:**
```csharp
// After SwapBuffers(), from systems
var damages = bus.Consume<DamageEvent>();

foreach (var dmg in damages)
{
    // Process each damage event
    ApplyDamage(dmg.Target, dmg.Amount);
}
```

**Critical API Details:**
- `Publish<T>(T event)` - Add event to PENDING buffer (thread-safe)
- `SwapBuffers()` - Flip buffers (call once per frame)
- `Consume<T>()` - Returns `ReadOnlySpan<T>` from CURRENT buffer (zero-copy)

---

### Usage Examples

#### Example 1: Basic Publish and Consume

From `EventBusTests.cs` lines 76-96:

```csharp
using Fdp.Kernel;

[EventId(1)]
public struct SimpleEvent
{
    public int Value;
}

// Frame 1: Publish event
var bus = new FdpEventBus();
bus.Publish(new SimpleEvent { Value = 42 });

// Frame 1: Try to consume (events not swapped yet)
var consumed1 = bus.Consume<SimpleEvent>();
Assert.Equal(0, consumed1.Length); // Empty! Events in PENDING buffer

// End of Frame 1: Swap buffers
bus.SwapBuffers();

// Frame 2: Now events are visible
var consumed2 = bus.Consume<SimpleEvent>();
Assert.Equal(1, consumed2.Length);
Assert.Equal(42, consumed2[0].Value); // ✅ Event visible!
```

**Expected Output:**
- Frame 1: Published event goes to PENDING buffer
- Frame 1: Consume returns empty (events not visible yet)
- After SwapBuffers(): PENDING → CURRENT
- Frame 2: Consume returns 1 event with Value=42

---

#### Example 2: Multiple Events of Same Type

From `EventBusTests.cs` lines 98-115:

```csharp
[EventId(1)]
public struct SimpleEvent
{
    public int Value;
}

var bus = new FdpEventBus();

// Publish 3 events in same frame
bus.Publish(new SimpleEvent { Value = 1 });
bus.Publish(new SimpleEvent { Value = 2 });
bus.Publish(new SimpleEvent { Value = 3 });

bus.SwapBuffers();

// Consume all events
var events = bus.Consume<SimpleEvent>();

Assert.Equal(3, events.Length);
Assert.Equal(1, events[0].Value);
Assert.Equal(2, events[1].Value);
Assert.Equal(3, events[2].Value);
```

**Key Insight:** All events of the same type are batched together and iterable via `ReadOnlySpan<T>`.

---

#### Example 3: Multiple Event Types Isolated

From `EventBusTests.cs` lines 117-137:

```csharp
[EventId(1)]
public struct SimpleEvent { public int Value; }

[EventId(2)]
public struct DamageEvent { public float Amount; }

var bus = new FdpEventBus();

// Mix different event types
bus.Publish(new SimpleEvent { Value = 100 });
bus.Publish(new DamageEvent { Amount = 50.0f });
bus.Publish(new Simple Event { Value = 200 });

bus.SwapBuffers();

// Each type has isolated stream
var simpleEvents = bus.Consume<SimpleEvent>();
var damageEvents = bus.Consume<DamageEvent>();

Assert.Equal(2, simpleEvents.Length);  // 2 SimpleEvents
Assert.Equal(1, damageEvents.Length);  // 1 DamageEvent

Assert.Equal(100, simpleEvents[0].Value);
Assert.Equal(200, simpleEvents[1].Value);
Assert.Equal(50.0f, damageEvents[0].Amount);
```

**Key Insight:** Event types are **isolated** - each type has its own buffer pair.

---

#### Example 4: Multi-Threaded Publishing

From `Event BusTests.cs` lines 220-248:

```csharp
[EventId(1)]
public struct SimpleEvent { public int Value; }

var bus = new FdpEventBus();

const int ThreadCount = 10;
const int EventsPerThread = 1000;
const int ExpectedTotal = ThreadCount * EventsPerThread; // 10,000

// 10 threads publishing simultaneously
Parallel.For(0, ThreadCount, threadId =>
{
    for (int i = 0; i < EventsPerThread; i++)
    {
        bus.Publish(new SimpleEvent { Value = threadId * 1000 + i });
    }
});

bus.SwapBuffers();
var events = bus.Consume<SimpleEvent>();

// Verify all 10,000 events captured
Assert.Equal(ExpectedTotal, events.Length);

// Verify uniqueness (no overwrites)
var uniqueValues = new HashSet<int>();
foreach (var evt in events)
{
    Assert.True(uniqueValues.Add(evt.Value)); // All unique!
}
```

**Expected Output:**
- All 10,000 events successfully captured
- No data loss despite concurrent publishing
- No duplicate or corrupted values

**Performance:** Lock-free publishing via `Interlocked.Increment` for thread safety.

---

#### Example 5: Three-Frame Event Lifecycle

From `EventBusTests.cs` lines 431-462:

```csharp
[EventId(1)]
public struct SimpleEvent { public int Value; }

var bus = new FdpEventBus();

// Frame 1: Publish A
bus.Publish(new SimpleEvent { Value = 1 });
Assert.Equal(0, bus.Consume<SimpleEvent>().Length); // Not visible yet

// End of Frame 1
bus.SwapBuffers();

// Frame 2: Consume A, Publish B
var frame2Events = bus.Consume<SimpleEvent>();
Assert.Equal(1, frame2Events.Length);
Assert.Equal(1, frame2Events[0].Value); // ✅ Event A visible

bus.Publish(new SimpleEvent { Value = 2 }); // Publish B

// End of Frame 2
bus.SwapBuffers();

// Frame 3: Consume B (A is gone)
var frame3Events = bus.Consume<SimpleEvent>();
Assert.Equal(1, frame3Events.Length);
Assert.Equal(2, frame3Events[0].Value); // ✅ Event B visible, A cleared

// End of Frame 3
bus.SwapBuffers();

// Frame 4: Nothing
var frame4Events = bus.Consume<SimpleEvent>();
Assert.Equal(0, frame4Events.Length); // All cleared
```

**Key Insights:**
- Events live for **exactly 1 frame**
- After `SwapBuffers()`, old CURRENT buffer is cleared
- Chain of events spans multiple frames naturally

---

#### Example 6: Chain Reaction Pattern

From `EventBusTests.cs` lines 491-516:

```csharp
[EventId(2)]
public struct DamageEvent 
{ 
    public Entity Target;
    public float Amount;
}

[EventId(3)]
public struct ExplosionEvent 
{ 
    public float X, Y, Z;
    public float Radius;
}

var bus = new FdpEventBus();

// Frame 1: Damage dealt
bus.Publish(new DamageEvent { Target = entity, Amount = 100.0f });
bus.SwapBuffers();

// Frame 2: Process damage, trigger death
var damageEvents = bus.Consume<DamageEvent>();
Assert.Equal(1, damageEvents.Length);

// Simulate death logic
if (damageEvents[0].Amount >= 100.0f)
{
    // Fatal damage → publish explosion
    bus.Publish(new ExplosionEvent { X = 10, Y = 20, Z = 30, Radius = 5.0f });
}

bus.SwapBuffers();

// Frame 3: Process explosion
var explosionEvents = bus.Consume<ExplosionEvent>();
Assert.Equal(1, explosionEvents.Length);
Assert.Equal(10, explosionEvents[0].X);
```

**Pattern:** Events trigger events, processing naturally across frames.

---

### API Reference

#### FdpEventBus Class

```csharp
public class FdpEventBus : IDisposable
{
    /// <summary>
    /// Publish an event to the PENDING buffer.
    /// Thread-safe, lock-free.
    /// </summary>
    public void Publish<T>(T evt) where T : unmanaged;
    
    /// <summary>
    /// Swap buffers: PENDING becomes CURRENT, old CURRENT cleared.
    /// Call once per frame, after input processing.
    /// NOT THREAD-SAFE - must be called from main thread only.
    /// </summary>
    public void SwapBuffers();
    
    /// <summary>
    /// Consume events from CURRENT buffer.
    /// Returns zero-copy ReadOnlySpan.
    /// Multiple calls in same frame return same data.
    /// </summary>
    public ReadOnlySpan<T> Consume<T>() where T : unmanaged;
    
    /// <summary>
    /// Get all active event streams (for serialization/flight recorder).
    /// </summary>
    public IEnumerable<IEventStream> GetAllActiveStreams();
    
    /// <summary>
    /// Dispose all buffers.
    /// </summary>
    public void Dispose();
}
```

---

#### EventId Attribute

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public class EventIdAttribute : Attribute
{
    public int Id { get; }
    
    public EventIdAttribute(int id)
    {
        Id = id;
    }
}
```

**Usage:**
```csharp
[EventId(42)]
public struct MyEvent
{
    public int Data;
}
```

---

#### Event Type Static API

```csharp
public static class EventType<T> where T : unmanaged
{
    /// <summary>
    /// Get the event type ID (from [EventId] attribute).
    /// Cached after first access.
    /// Throws InvalidOperationException if attribute missing.
    /// </summary>
    public static int Id { get; }
}
```

---

### Best Practices

#### ✅ DO: Define Events as Unmanaged Structs

```csharp
// ✅ GOOD: Unmanaged struct
[EventId(1)]
public struct DamageEvent
{
    public Entity Target;
    public float Amount;
    public Vector3 ImpactPoint;
}

// ❌ BAD: Class (not allowed)
[EventId(2)]
public class BadEvent // Error: must be struct
{
    public int Data;
}

// ❌ BAD: Contains managed reference
[EventId(3)]
public struct BadManagedEvent
{
    public string Message; // Error: not unmanaged!
}
```

**Why:** Unmanaged structs enable:
- Stack allocation (no GC pressure)
- Memcpy for buffer swaps (fast)
- Direct pointer access (serialization)

---

#### ✅ DO: Call SwapBuffers() Once Per Frame

```csharp
// ✅ GOOD: ModuleHost handles this
var moduleHost = new ModuleHostKernel(repository);
moduleHost.Update(deltaTime); // Calls SwapBuffers() internally

// ✅ GOOD: Manual control
void GameLoop()
{
    repository.Tick();
    ProcessInput(); // Publishes events
    bus.SwapBuffers(); // ← Call EXACTLY ONCE per frame
    ExecuteSystems(); // Consumes events
}
```

**Why:** Multiple calls clear events prematurely.

---

#### ✅ DO: Consume Multiple Times in Same Frame (Safe)

```csharp
var bus = repository.Bus;
bus.SwapBuffers();

// System A consumes
var events1 = bus.Consume<DamageEvent>();
ProcessDamage(events1);

// System B consumes AGAIN (same frame)
var events2 = bus.Consume<DamageEvent>();
ProcessSound(events2); // Same data!

Assert.True(events1.Length == events2.Length); // Identical
```

**Why:** Multiple `Consume<T>()` calls in same frame return the **same data** (CURRENT buffer unchanged).

---

#### ⚠️ DON'T: Forget [EventId] Attribute

```csharp
// ❌ BAD: Missing attribute
public struct InvalidEvent
{
    public int Value;
}

// Runtime error:
var bus = new FdpEventBus();
bus.Publish(new InvalidEvent { Value = 1 }); 
// Throws TypeInitializationException:
// "InvalidEvent is missing required [EventId] attribute"
```

**Solution:** Always add `[EventId(n)]`.

---

#### ⚠️ DON'T: Expect Same-Frame Delivery

```csharp
// ❌ WRONG EXPECTATION:
bus.Publish(new JumpEvent());
var events = bus.Consume<JumpEvent>(); // Expecting event immediately
Assert.Equal(1, events.Length); // ❌ FAILS - events not visible yet!

// ✅ CORRECT:
bus.Publish(new JumpEvent()); // Frame N: Publish
bus.SwapBuffers();             // End of Frame N
var events = bus.Consume<JumpEvent>(); // Frame N+1: Consume
Assert.Equal(1, events.Length); // ✅ WORKS
```

**Why:** Double buffering introduces intentional 1-frame delay.

---

#### ⚠️ DON'T: Reuse Event IDs

```csharp
// ❌ BAD: Duplicate ID
[EventId(1)]
public struct DamageEvent { }

[EventId(1)] // ← Same ID!
public struct HealEvent { }

// Runtime: Undefined behavior (ID collision)
```

**Solution:** Use unique IDs. Consider reserving ranges:
- 1-100: Core events
- 101-200: Combat events
- 201-300: UI events

---

### Troubleshooting

#### Problem: Events Not Visible Same Frame

**Symptoms:**
```csharp
bus.Publish(evt);
var consumed = bus.Consume<MyEvent>();
Assert.Equal(0, consumed.Length); // Always 0!
```

**Cause:** Forgot to call `SwapBuffers()` or expecting same-frame delivery.

**Solution:**
```csharp
// Frame 1
bus.Publish(evt); // → PENDING buffer

// End of Frame 1
bus.SwapBuffers(); // PENDING → CURRENT

// Frame 2
var consumed = bus.Consume<MyEvent>(); // ← Now visible!
```

---

#### Problem: TypeInitializationException on First Publish

**Symptoms:**
```
System.TypeInitializationException: The type initializer for 'EventType`1' threw an exception.
---> System.InvalidOperationException: MyEvent is missing required [EventId] attribute.
```

**Cause:** Event struct missing `[EventId(n)]` attribute.

**Solution:**
```csharp
// ❌ BEFORE:
public struct MyEvent { }

// ✅ AFTER:
[EventId(42)]
public struct MyEvent { }
```

---

#### Problem: Events Cleared Too Early

**Symptoms:**
- Published events never consumed
- Debugger shows events in buffer, but `Consume()` returns empty

**Cause:** Called `SwapBuffers()` multiple times in one frame.

**Solution:**
```csharp
// ❌ BAD:
bus.SwapBuffers(); // First call
// ... some code ...
bus.SwapBuffers(); // Second call - clears CURRENT buffer!

// ✅ GOOD:
bus.SwapBuffers(); // Call ONCE per frame
```

**Debug Technique:** Log `SwapBuffers()` calls:
```csharp
public void SwapBuffers()
{
    Console.WriteLine($"[Frame {frameCount}] SwapBuffers()");
    // ... swap logic
}
```

---

#### Problem: Thread Safety Violation During SwapBuffers

**Symptoms:**
- Crashes during `SwapBuffers()`
- Corrupted event data

**Cause:** `SwapBuffers()` called from background thread or concurrent with `Publish()`.

**Solution:**
```csharp
// ✅ CORRECT: SwapBuffers on main thread only
void MainThreadUpdate()
{
    repository.Tick();
    ProcessInput(); // ← Can call from main thread
    bus.SwapBuffers(); // ← MAIN THREAD ONLY
    ExecuteSystems();
}

// ✅ CORRECT: Publish from any thread
void BackgroundAI()
{
    bus.Publish(new ThinkEvent()); // ← Thread-safe
}

// ❌ WRONG: SwapBuffers from background thread
void BackgroundThread()
{
    bus.SwapBuffers(); // ❌ NOT THREAD-SAFE!
}
```

---

### Performance Characteristics

#### Publish Throughput

From benchmark tests (`EventBusTests.cs` lines 669-688):

**Single-Threaded:**
- **1M+ events/second** for small structs (4-16 bytes)
- **500K+ events/second** for larger structs (256 bytes)
- Lock-free via `Interlocked.Increment`

**Multi-Threaded:**
- **500K+ events/second** with 8 threads publishing concurrently
- Scales linearly up to ~4-8 threads
- No contention (each thread increments atomic counter independently)

---

#### Buffer Expansion

**Auto-Expansion:**
- Initial capacity: **1024 events**
- Expansion: **2x** when full (1024 → 2048 → 4096 → 8192 → ...)
- Allocation: O(n) during expansion, amortized O(1) insertion

From `EventBusTests.cs` lines 342-365:

```csharp
const int EventCount = 2500; // Exceeds initial 1024

for (int i = 0; i < EventCount; i++)
{
    bus.Publish(new SimpleEvent { Value = i });
}

bus.SwapBuffers();
var events = bus.Consume<SimpleEvent>();

Assert.Equal(2500, events.Length); // All events captured
// Buffer expanded: 1024 → 2048 → 4096
```

**Performance Impact:**
- First 1024 events: 0 allocations
- Event 1025: Allocate 2048-sized buffer, copy 1024 events (~0.5ms)
- Events 1025-2048: 0 allocations
- Event 2049: Allocate 4096-sized buffer, copy 2048 events (~1ms)

**Recommendation:** Pre-size buffers if you consistently exceed 1024 events/frame.

---

#### Memory Footprint

**Per Event Type:**
- 2 buffers (double buffering)
- Each buffer: `capacity * sizeof(T)` bytes

**Example:**
```csharp
[EventId(1)]
public struct DamageEvent // 24 bytes
{
    public Entity Target;  // 8 bytes
    public float Amount;   // 4 bytes
    public Entity Source;  // 8 bytes
    public Vector3 Impact; // 12 bytes → padded to 24
}

// Memory usage at capacity 4096:
// - Buffer A: 4096 * 24 = 98 KB
// - Buffer B: 4096 * 24 = 98 KB
// - Total: 196 KB per DamageEvent stream
```

**With 20 event types at 4096 capacity:**
- Total memory: ~4 MB

---

### Serialization Support

The event bus provides APIs for **Flight Recorder** and **Network Sync** integration.

#### Get All Active Streams

```csharp
public interface IEventStream
{
    int EventTypeId { get; }
    int ElementSize { get; }
    int Count { get; }
    ReadOnlySpan<byte> GetRawBytes();
}

// Usage:
var streams = bus.GetAllActiveStreams();

foreach (var stream in streams)
{
    Console.WriteLine($"EventType {stream.EventTypeId}: {stream.Count} events, {stream.ElementSize} bytes each");
    
    // Serialize for network or disk
    var bytes = stream.GetRawBytes();
    SaveToFile(bytes);
}
```

From `EventBusTests.cs` lines 532-548:

```csharp
bus.Publish(new SimpleEvent { Value = 1 });
bus.Publish(new DamageEvent { Amount = 50 });
bus.Publish(new ExplosionEvent { Radius = 10 });

bus.SwapBuffers();

var streams = bus.GetAllActiveStreams().ToList();

Assert.Equal(3, streams.Count); // 3 active event types

var typeIds = streams.Select(s => s.EventTypeId).OrderBy(id => id).ToList();
Assert.Equal(new[] { 1, 2, 3 }, typeIds); // Correct IDs
```

---

#### Raw Byte Access for Serialization

From `EventBusTests.cs` lines 550-577:

```csharp
[EventId(1)]
public struct SimpleEvent { public int Value; }

bus.Publish(new SimpleEvent { Value = 42 });
bus.Publish(new SimpleEvent { Value = 99 });

bus.SwapBuffers();

var streams = bus.GetAllActiveStreams().ToList();
var simpleStream = streams.First(s => s.EventTypeId == 1);
var rawBytes = simpleStream.GetRawBytes();

Assert.Equal(2 * sizeof(int), rawBytes.Length); // 8 bytes total

unsafe
{
    fixed (byte* ptr = rawBytes)
    {
        int* values = (int*)ptr;
        Assert.Equal(42, values[0]);
        Assert.Equal(99, values[1]);
    }
}
```

**Use Cases:**
- Flight Recorder: Serialize events to file for replay
- Network: Send events to remote clients
- Determinism Validation: Hash event data for checksum

---

### Thread Safety Guarantees

#### Safe Operations

✅ **Publish() - Lock-Free, Thread-Safe:**
```csharp
// From ANY thread, ANY time
Parallel.For(0, 1000, i =>
{
    bus.Publish(new MyEvent { Value = i }); // ← Safe
});
```

Implementation uses `Interlocked.Increment` for lock-free slot reservation.

✅ **Consume() - Main Thread (After SwapBuffers):**
```csharp
// From main thread, after SwapBuffers()
var events = bus.Consume<MyEvent>(); // ← Safe (read-only)
```

Returns `ReadOnlySpan<T>` (immutable view).

---

#### Unsafe Operations

❌ **SwapBuffers() - Main Thread ONLY:**
```csharp
// ❌ NEVER call from background thread
void BackgroundTask()
{
    bus.SwapBuffers(); // CRASH!
}

// ✅ ONLY from main thread
void MainThread()
{
    bus.SwapBuffers(); // Safe
}
```

**Concurrent SwapBuffers:**
```csharp
// ❌ NEVER call concurrently
Task.Run(() => bus.SwapBuffers()); // Thread A
Task.Run(() => bus.SwapBuffers()); // Thread B
// RACE CONDITION!
```

---

### Cross-References

**Related Sections:**
- [Systems & Scheduling](#systems--scheduling) - Systems consume events from the bus
- [Entity Component System (ECS)](#entity-component-system-ecs) - Events complement component-based state
- [Modules & ModuleHost](#modules--modulehost) - ModuleHost manages bus lifecycle (Tick, SwapBuffers)
- [Flight Recorder & Deterministic Replay](#flight-recorder--deterministic-replay) - Records events for replay

**API Reference:**
- See [API Reference - Event Bus](API-REFERENCE.md#event-bus)

**Example Code:**
- `FDP/Fdp.Tests/EventBusTests.cs` - Comprehensive event bus tests (717 lines)
- `FDP/Fdp.Tests/EventBusFlightRecorderIntegrationTests.cs` - Event recording
- `FDP/Fdp.Tests/EventAccumulationIntegrationTests.cs` - Event accumulation patterns

**Related Batches:**
- None (core FDP feature)

---

---

## Modules & ModuleHost

### What is a Module?

A **Module** is a collection of related systems that operate on a **snapshot** of the simulation state with configurable execution strategy.

**Key Differences: Component System vs Module:**

| Aspect | ComponentSystem | Module |
|--------|----------------|--------|
| Execution | Main thread | Configurable (Sync/FrameSynced/Async) |
| State Access | Direct (EntityRepository) | Snapshot (ISimulationView) |
| Update Frequency | Every frame | Configurable (e.g., 10Hz) |
| Scheduling | Fixed phase | Reactive (events/components) |
| Use Case | Physics, rendering | AI, pathfinding, analytics, network |

### Module Interface (Modern API)

```csharp
using ModuleHost.Core;

public interface IModule
{
    /// <summary>
    /// Module name for diagnostics and logging.
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Execution policy defining how this module runs.
    /// Replaces old Tier + UpdateFrequency.
    /// </summary>
    ExecutionPolicy Policy { get; }
    
    /// <summary>
    /// Register systems for this module (called during initialization).
    /// </summary>
    void RegisterSystems(ISystemRegistry registry) { }
    
    /// <summary>
    /// Main module execution method.
    /// Can be called from main thread or background thread based on Policy.
    /// </summary>
    void Tick(ISimulationView view, float deltaTime);
    
    /// <summary>
    /// Component types to watch for changes (reactive scheduling).
    /// Module wakes when any of these components are modified.
    /// </summary>
    IReadOnlyList<Type>? WatchComponents { get; }
    
    /// <summary>
    /// Event types to watch (reactive scheduling).
    /// Module wakes when any of these events are published.
    /// </summary>
    IReadOnlyList<Type>? WatchEvents { get; }
}
```

### Execution Policies

**ExecutionPolicy** defines how a module runs:

```csharp
public struct ExecutionPolicy
{
    public RunMode Mode;              // How it runs (thread model)
    public DataStrategy Strategy;     // What data structure
    public int TargetFrequencyHz;     // Scheduling frequency (0 = every frame)
    public int MaxExpectedRuntimeMs;  // Timeout for circuit breaker
    public int FailureThreshold;      // Consecutive failures before disable
}

public enum RunMode
{
    Synchronous,  // Main thread, blocks frame
    FrameSynced,  // Background thread, main waits
    Asynchronous  // Background thread, fire-and-forget
}

public enum DataStrategy
{
    Direct,  // Use live world (only valid for Synchronous)
    GDB,     // Persistent double-buffered replica
    SoD      // Pooled snapshot on-demand
}
```

**Factory Methods for Common Patterns:**

```csharp
// Physics, Input - must run on main thread
Policy = ExecutionPolicy.Synchronous();

// Network, Flight Recorder - low-latency background
Policy = ExecutionPolicy.FastReplica();

// AI, Analytics - slow background computation
Policy = ExecutionPolicy.SlowBackground(10); // 10 Hz
```

### Background Thread Execution

**Modules can run on background threads without blocking the main simulation:**

#### Synchronous Mode
```csharp
public class PhysicsModule : IModule
{
    public string Name => "Physics";
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Runs on MAIN THREAD
        // Has Direct access to live EntityRepository
        // Blocks frame until complete
    }
}
```

**Use for:** Physics, input handling, critical systems that must run every frame

#### FrameSynced Mode
```csharp
public class FlightRecorderModule : IModule
{
    public string Name => "Recorder";
    public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Runs on BACKGROUND THREAD
        // Accesses persistent GDB replica
        // Main thread WAITS for completion
    }
}
```

**Use for:** Network sync, flight recorder, logging

**How it works:**
1. Main thread creates persistent replica (GDB - Generalized Double Buffer)
2. Replica synced every frame
3. Module dispatched to thread pool
4. Main thread waits for completion before continuing
5. Commands harvested and applied to live world

#### Asynchronous Mode
```csharp
public class AIDecisionModule : IModule
{
    public string Name => "AI";
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10); // 10 Hz
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Runs on BACKGROUND THREAD
        // Accesses pooled snapshot (SoD - Snapshot on Demand)
        // Main thread DOESN'T WAIT
        // Can span multiple frames
    }
}
```

**Use for:** AI decision making, pathfinding, analytics

**How it works:**
1. Module scheduled to run (every 6 frames for 10Hz)
2. On-demand snapshot created and leased
3. Module dispatched to thread pool
4. Main thread continues immediately
5. When module completes (possibly after multiple frames):
   - Commands harvested
   - View released back to pool
   - Module can run again

### Reactive Scheduling

**Modules can wake on specific triggers, not just timers:**

```csharp
public class CombatAIModule : IModule
{
    public string Name => "CombatAI";
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(1); // 1 Hz baseline
    
    // Wake immediately when these events fire
    public IReadOnlyList<Type>? WatchEvents => new[]
    {
        typeof(DamageEvent),
        typeof(EnemySpottedEvent)
    };
    
    // Wake when these components change
    public IReadOnlyList<Type>? WatchComponents => new[]
    {
        typeof(Health),
        typeof(TargetInfo)
    };
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Module sleeps normally, wakes when:
        // - 1 second passes (1 Hz baseline)
        // - DamageEvent published
        // - Health component modified on any entity
    }
}
```

**Benefits:**
- **Responsiveness:** AI reacts within 1 frame instead of waiting up to 1 second
- **Efficiency:** Module sleeps when nothing relevant happens
- **Scalability:** Reduces CPU usage for idle modules

### Component Systems within Modules

Modules can register **Component Systems** that execute within the module's context:

```csharp
public class AIModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new BehaviorTreeSystem());
        registry.RegisterSystem(new TargetSelectionSystem());
        registry.RegisterSystem(new PathFollowingSystem());
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Systems run automatically in registered order
        // All systems share the same snapshot view
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
public class BehaviorTreeSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Runs WITHIN the module's thread context
        // If module is Async, this runs on background thread
        // If module is Sync, this runs on main thread
    }
}
```

**Why Use Systems in Modules?**
- **Organization:** Separate concerns within a module
- **Ordering:** Systems run in phase order automatically
- **Reusability:** Same system can be used in different modules

### Example Module

```csharp
public class PathfindingModule : IModule
{
    public string Name => "Pathfinding";
    
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10); // 10 Hz
    
    public IReadOnlyList<Type>? WatchEvents => new[]
    {
        typeof(FindPathRequest)
    };
    
    public IReadOnlyList<Type>? WatchComponents => null;
    
    private PathfindingService _pathfinder;
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new PathRequestSystem());
        registry.RegisterSystem(new PathExecuteSystem());
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Request system processes new path requests
        // Execute system performs A* pathfinding
        // Results published via command buffer events
    }
}
```

