# API Reference - Hybrid GDB+SoD Architecture

**Version:** 2.1  
**Date:** January 8, 2026  
**Updated:** Post-BATCH-05.1 (Component Mask Optimization)  
**Namespace Index:**
- Fdp.Kernel
- ModuleHost.Core.Abstractions
- ModuleHost.Core.Providers
- ModuleHost.Core.Resilience

> **Note:** For conceptual guides and workflows, see [FDP-ModuleHost-User-Guide.md](./FDP-ModuleHost-User-Guide.md)

---

## Table of Contents

1. [FDP Kernel APIs](#fdp-kernel-apis)
   - [Component Dirty Tracking](#component-dirty-tracking) ⭐ BATCH-01
   - [Event Bus Active Tracking](#event-bus-active-tracking) ⭐ BATCH-02
   - [Event Bus Cleanup](#event-bus-cleanup) ⭐ BATCH-03
2. [Core Abstractions](#core-abstractions)
   - [ExecutionPolicy](#executionpolicy) ⭐ BATCH-05
   - [RunMode & DataStrategy](#runmode--datastrategy) ⭐ BATCH-05
3. [Snapshot Providers](#snapshot-providers)
   - [SnapshotPool](#snapshotpool) ⭐ BATCH-03
4. [Module Framework](#module-framework)
5. [Resilience & Safety](#resilience--safety) ⭐ BATCH-04
   - [ModuleCircuitBreaker](#modulecircuitbreaker)
   - [ModuleStats](#modulestats)
6. [Utility Types](#utility-types)
7. [Examples](#examples)

---

## FDP Kernel APIs

### EntityRepository

**Namespace:** `Fdp.Kernel`  
**Assembly:** Fdp.Kernel.dll

#### Constructor

```csharp
public EntityRepository()
```

Creates a new entity repository instance. Can be used as:
- Live world (main simulation)
- GDB replica (persistent)
- SoD snapshot (pooled)

---

#### SyncFrom()

```csharp
public void SyncFrom(
    EntityRepository source,
    BitMask256? mask = null)
```

**Description:**  
Synchronizes this repository to match the source repository. Core API for both GDB and SoD strategies.

**Parameters:**
- `source` - Source repository (typically the live world)
- `mask` - Optional component filter
  - `null` - Sync all components (GDB usage)
  - `BitMask256` - Sync only specified types (SoD usage)

**Behavior:**
1. Syncs entity metadata (IsAlive, Generation)
2. Syncs component tables (with optional filtering)
3. Uses dirty tracking to skip unchanged chunks
4. Updates global version

**Performance:**
- Full sync (no mask): <2ms for 100K entities, 30% dirty
- Filtered sync (50% mask): <500μs for 100K entities, 30% dirty

**Usage:**
```csharp
// GDB: Full sync
replica.SyncFrom(liveWorld);

// SoD: Filtered sync
var aiMask = new BitMask256();
aiMask.Set(typeof(Position));
aiMask.Set(typeof(Team));
snapshot.SyncFrom(liveWorld, aiMask);
```

**Thread Safety:** NOT thread-safe. Must be called from main thread during sync point.

**See Also:** `NativeChunkTable.SyncDirtyChunks()`, `ManagedComponentTable.SyncDirtyChunks()`

---

#### ISimulationView Implementation

**EntityRepository implements `ISimulationView`** natively, enabling GDB to return the repository directly.

```csharp
public sealed partial class EntityRepository : ISimulationView
{
    public uint Tick { get; }  // = GlobalVersion
    public float Time { get; }  // = SimulationTime
    
    public ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
    public T GetManagedComponentRO<T>(Entity e) where T : class;
    public bool IsAlive(Entity e);
    public ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    public EntityQueryBuilder Query();
}
```

---

### NativeChunkTable<T>

**Namespace:** `Fdp.Kernel`

#### SyncDirtyChunks()

```csharp
public void SyncDirtyChunks(NativeChunkTable<T> source)
    where T : unmanaged
```

**Description:**  
Synchronizes dirty chunks from source to this table. Uses version tracking for optimization.

**Algorithm:**
1. Iterate all chunks in source
2. Compare chunk versions
3. If versions match → skip (chunk unchanged)
4. If versions differ → `Unsafe.CopyBlock` (memcpy 64KB)
5. Update chunk version

**Performance:**
- <1ms for 1000 chunks (30% dirty)
- 3x faster than naive full copy

**Optimization:**  
Dirty tracking prevents copying 70% of chunks in typical scenarios (static terrain, inactive entities).

**Usage:**
```csharp
// Called internally by EntityRepository.SyncFrom()
myTable.SyncDirtyChunks(sourceTable);
```

**Thread Safety:** NOT thread-safe.

---

### ManagedComponentTable<T>

**Namespace:** `Fdp.Kernel`

#### SyncDirtyChunks()

```csharp
public void SyncDirtyChunks(ManagedComponentTable<T> source)
    where T : class
```

**Description:**  
Synchronizes dirty chunks for managed (Tier 2) components. Uses `Array.Copy` for reference copying.

**Behavior:**
- **Shallow copy** - copies references, not deep clones
- **Requires immutability** - Tier 2 must be immutable records
- Uses version tracking like Tier 1

**Performance:**
- <500μs for 1000 chunks (30% dirty)

**Important:**  
Because this is shallow copy, Tier 2 components **MUST** be immutable records. Otherwise, live world mutations will corrupt snapshots!

**See Also:** Tier 2 Immutability Enforcement (IMPLEMENTATION-SPECIFICATION.md)

---

### Component Dirty Tracking

**Implemented in:** BATCH-01 ⭐  
**Namespace:** `Fdp.Kernel`

#### IComponentTable.HasChanges()

```csharp
bool HasChanges(uint sinceVersion)
```

**Description:**  
Checks if a component table has been modified since a specific version. Used for reactive scheduling.

**Parameters:**
- `sinceVersion` - Version to compare against (typically module's last seen tick)

**Returns:** `true` if any chunk in this table has version > `sinceVersion`

**Algorithm:**  
Lazy O(chunks) scan comparing chunk versions. Typically <50ns for tables with few chunks.

**Usage:**
```csharp
// Check if Position component changed since tick 1000
if (positionTable.HasChanges(1000))
{
    Console.WriteLine("Position data modified!");
}
```

---

#### EntityRepository.HasComponentChanged()

```csharp
public bool HasComponentChanged(Type componentType, uint sinceTick)
```

**Description:**  
Public API wrapping `HasChanges()` for type-based lookup.

**Parameters:**
- `componentType` - Component type to check
- `sinceTick` - Tick number for comparison

**Returns:** `true` if component data changed

**Usage:**
```csharp
// Reactive module trigger
if (repo.HasComponentChanged(typeof(Health), myLastTick))
{
    RunHealthAnalysis();
}
```

**Performance:** O(chunks) + type lookup, typically <100ns

**See Also:** Reactive Scheduling (User Guide)

---

### Event Bus Active Tracking

**Implemented in:** BATCH-02 ⭐  
**Namespace:** `Fdp.Kernel`

#### FdpEventBus.HasEvent<T>()

```csharp
public bool HasEvent<T>() where T : unmanaged
public bool HasManagedEvent<T>() where T : class
public bool HasEvent(Type eventType)
```

**Description:**  
O(1) check if event type exists in current frame's event bus.

**Implementation:**  
Uses `HashSet<int>` populated during `Swap Buffers()` for constant-time lookup.

**Returns:** `true` if at least one event of type `T` exists

**Usage:**
```csharp
// Reactive trigger: module only runs if ExplosionEvent exists
if (bus.HasEvent<ExplosionEvent>())
{
    var explosions = bus.ConsumeEvents<ExplosionEvent>();
    ProcessExplosions(explosions);
}
```

**Performance:** ~5ns HashSet lookup

**See Also:**  
- `ConsumeEvents<T>()` - Retrieve actual events
- Reactive Scheduling (User Guide)

---

### Event Bus Cleanup

**Implemented in:** BATCH-03 ⭐  
**Namespace:** `Fdp.Kernel`

#### FdpEventBus.ClearAll()

```csharp
public void ClearAll()
```

**Description:**  
Resets all event buffers and active event tracking. Used when recycling pooled snapshots to prevent "ghost events" from previous usage.

**Behavior:**
- Clears all native event buffers
- Clears all managed event buffers
- Clears active event ID set
- Resets internal state

**Called by:** `EntityRepository.SoftClear()` during snapshot pool return

**Usage:**
```csharp
// Internal: Snapshot pool recycling
public void Return(EntityRepository repo)
{
    repo.SoftClear(); // → calls Bus.ClearAll()
    _pool.Push(repo);
}
```

**Important:**  
Without this, reused snapshots would contain stale events from previous frames, causing incorrect reactive triggers.

**See Also:** SnapshotPool, Convoy Pattern (User Guide)

---

### EventAccumulator

**Namespace:** `Fdp.Kernel`  
**Assembly:** Fdp.Kernel.dll

```csharp
public class EventAccumulator
{
    public EventAccumulator();
    
    public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex);
    public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick);
}
```

**Description:**  
Bridges live event stream to replica event buses. Enables slow modules to see accumulated event history.

---

#### CaptureFrame()

```csharp
public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex)
```

**Description:**  
Captures and queues events from the live bus for one frame.

**Parameters:**
- `liveBus` - Live world event bus
- `frameIndex` - Current frame number (for filtering)

**Behavior:**
- Extracts event buffers from live bus (doesn't clear them)
- Queues as `FrameEventData` with timestamp
- Buffers are pooled (zero allocations)

**Usage:**
```csharp
// Call every frame (main thread)
_accumulator.CaptureFrame(_liveWorld.Bus, _frameNumber);
```

---

#### FlushToReplica()

```csharp
public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick)
```

**Description:**  
Flushes accumulated event history to replica bus.

**Parameters:**
- `replicaBus` - Replica's event bus
- `lastSeenTick` - Last tick this replica saw (for filtering)

**Behavior:**
- Dequeues all captured frames
- Skips frames <= lastSeenTick (already seen)
- Injects remaining events into replica bus (appends to current)
- Returns buffers to pool

**Performance:**
- <100μs to flush 6 frames (1K events/frame)

**Usage:**
```csharp
// Before returning replica to module
_accumulator.FlushToReplica(_replica.Bus, _replica.GlobalVersion);
```

**Result:**  
Replica now has events from `[lastSeenTick+1 ... current]`, enabling slow modules to see all events since last run.

---

### Entity Lifecycle States

**Implemented in:** BATCH-06 ⭐  
**Namespace:** `Fdp.Kernel`

```csharp
public enum EntityLifecycle
{
    Constructing,  // Entity being initialized
    Active,        // Fully initialized and active
    TearDown       // Being destroyed
}
```

**Description:**  
Lifecycle states for cooperative entity initialization/destruction across distributed modules.

**State Flow:**
```
CreateStagedEntity() → Constructing → Active → TearDown → Destroyed
```

---

#### EntityRepository.CreateStagedEntity()

```csharp
public Entity CreateStagedEntity()
```

**Description:**  
Creates an entity in `Constructing` state (not visible to default queries).

**Returns:** `Entity` - New entity handle

**Usage:**
```csharp
var entity = repo.CreateStagedEntity();
repo.AddComponent(entity, new VehicleState { ... });

// Entity won't appear in normal queries until state → Active
```

**Default:** `CreateEntity()` defaults to `Active` for backward compatibility.

---

#### EntityRepository.SetLifecycleState()

```csharp
public void SetLifecycleState(Entity entity, EntityLifecycle state)
```

**Description:**  
Manually set entity lifecycle state.

**Parameters:**
- `entity` - Entity to modify
- `state` - New lifecycle state

**Usage:**
```csharp
// Activate after initialization complete
repo.SetLifecycleState(entity, EntityLifecycle.Active);

// Mark for destruction
repo.SetLifecycleState(entity, EntityLifecycle.TearDown);
```

**Note:** Prefer using EntityLifecycleModule for coordination.

---

#### QueryBuilder Lifecycle Filtering

```csharp
public QueryBuilder WithLifecycle(EntityLifecycle state);
public QueryBuilder IncludeConstructing();
public QueryBuilder IncludeTearDown();
public QueryBuilder IncludeAll();
```

**Description:**  
Filter queries by entity lifecycle state.

**Default Behavior:** Queries only return `Active` entities.

**Usage:**
```csharp
// Default: Only active entities
var query = repo.Query()
    .With<Position>()
    .Build();

// Explicit active filter
var activeQuery = repo.Query()
    .WithLifecycle(EntityLifecycle.Active)
    .Build();

// Include constructing entities (editor/debug)
var allQuery = repo.Query()
    .IncludeAll()
    .Build();

// Only constructing entities
var stagingQuery = repo.Query()
    .WithLifecycle(EntityLifecycle.Constructing)
    .Build();
```

**Performance:** O(1) bitwise check (EntityHeader.LifecycleState), zero overhead.

**See Also:** Entity Lifecycle Management (User Guide)

---

## Core Abstractions

### ExecutionPolicy

**Implemented in:** BATCH-05 ⭐  
**Namespace:** `ModuleHost.Core.Abstractions`

```csharp
public struct ExecutionPolicy
{
    public RunMode Mode { get; set; }
    public DataStrategy Strategy { get; set; }
    public int TargetFrequencyHz { get; set; }
    public int MaxExpectedRuntimeMs { get; set; }
    public int FailureThreshold { get; set; }
    public int CircuitResetTimeoutMs { get; set; }
    
    // Factory methods
    public static ExecutionPolicy Synchronous();
    public static ExecutionPolicy FastReplica();
    public static ExecutionPolicy SlowBackground(int hz);
    public static ExecutionPolicy Custom();
    
    // Fluent API
    public ExecutionPolicy WithMode(RunMode mode);
    public ExecutionPolicy WithStrategy(DataStrategy strategy);
    public ExecutionPolicy WithFrequency(int hz);
    public ExecutionPolicy WithTimeout(int ms);
    
    public void Validate();
}
```

**Description:**  
Configuration struct defining how and when a module executes. Replaces the old `ModuleTier` enum with fine-grained control.

**Properties:**
- `Mode` - Synchronous/FrameSynced/Asynchronous execution
- `Strategy` - Direct/GDB/SoD data access
- `TargetFrequencyHz` - Desired execution frequency (1-60Hz)
- `MaxExpectedRuntimeMs` - Timeout for circuit breaker (default: 100ms)
- `FailureThreshold` - Consecutive failures before circuit opens (default: 3)
- `CircuitResetTimeoutMs` - Cooldown before retry (default: 5000ms)

**Factory Methods:**

```csharp
// Main thread, direct access (physics, input)
ExecutionPolicy.Synchronous()
// → Mode=Synchronous, Strategy=Direct, 60Hz

// Frame-synced, double-buffered (network, recorder)
ExecutionPolicy.FastReplica()
// → Mode=FrameSynced, Strategy=GDB, 60Hz

// Background async, snapshot-on-demand (AI, analytics)
ExecutionPolicy.SlowBackground(10)  // 10Hz
// → Mode=Asynchronous, Strategy=SoD

// Custom configuration
ExecutionPolicy.Custom()
    .WithMode(RunMode.FrameSynced)
    .WithStrategy(DataStrategy.GDB)
    .WithFrequency(30)
    .WithTimeout(50)
```

**Validation Rules:**
- Synchronous mode MUST use Direct strategy
- Asynchronous mode CANNOT use Direct strategy
- TargetFrequencyHz must be 1-60Hz
- Validated automatically during `ModuleHostKernel.Initialize()`

**See Also:** RunMode, DataStrategy, IModule.Policy

---

### RunMode & DataStrategy

**Implemented in:** BATCH-05 ⭐  
**Namespace:** `ModuleHost.Core.Abstractions`

#### RunMode Enum

```csharp
public enum RunMode
{
    Synchronous,    // Main thread, blocks simulation
    FrameSynced,    // Every frame, async dispatch
    Asynchronous    // Sporadic, async dispatch
}
```

**Values:**
- `Synchronous` - Runs on main thread (only for fast, essential tasks like physics/input)
- `FrameSynced` - Dispatched every frame but runs asynchronously
- `Asynchronous` - Dispatched at target frequency (e.g., 10Hz for AI)

#### DataStrategy Enum

```csharp
public enum DataStrategy
{
    Direct,    // Live world access (Synchronous only)
    GDB,       // Double-buffered replica (fast modules)
    SoD        // Snapshot-on-demand (slow modules)
}
```

**Values:**
- `Direct` - Access live `EntityRepository` directly (no snapshot)
- `GDB` - Use persistent double-buffered replica
- `SoD` - Use pooled snapshot with filtered sync

**Valid Combinations:**
| Mode | Direct | GDB | SoD |
|------|--------|-----|-----|
| Synchronous | ✅ | ❌ | ❌ |
| FrameSynced | ❌ | ✅ | ⚠️ Rare |
| Asynchronous | ❌ | ⚠️ Rare | ✅ |

**See Also:** ExecutionPolicy, Provider Assignment (User Guide)

---

### ISimulationView

**Namespace:** `ModuleHost.Core.Abstractions`  
**Assembly:** ModuleHost.Core.dll

```csharp
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

**Description:**  
Unified read-only interface for accessing simulation state. Implemented by both `EntityRepository` (GDB) and `SimSnapshot` (SoD).

**Design Philosophy:**  
Modules are agnostic to whether they receive a GDB replica or SoD snapshot. They just use `ISimulationView`.

---

#### Properties

##### Tick

```csharp
uint Tick { get; }
```

Current simulation tick (frame number). Maps to `EntityRepository.GlobalVersion`.

##### Time

```csharp
float Time { get; }
```

Current simulation time in seconds. Maps to `EntityRepository.SimulationTime`.

---

#### Methods

##### GetComponentRO<T>()

```csharp
ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged
```

**Description:**  
Gets read-only reference to Tier 1 (unmanaged) component.

**Type Constraint:** `T : unmanaged` (blittable struct)

**Returns:** `ref readonly T` - Read-only reference (zero-copy)

**Throws:**
- `ComponentNotFoundException` if entity doesn't have component
- `EntityDeadException` if entity is not alive

**Usage:**
```csharp
var position = view.GetComponentRO<Position>(entity);
Console.WriteLine($"Entity at ({position.X}, {position.Y}, {position.Z})");

// Compile error - read-only!
// position.X = 10;  ❌
```

**Performance:** O(1), ~20ns

---

##### GetManagedComponentRO<T>()

```csharp
T GetManagedComponentRO<T>(Entity e) where T : class
```

**Description:**  
Gets Tier 2 (managed) component. Returns record instance.

**Type Constraint:** `T : class` (must be immutable record)

**Returns:** `T` - Component instance (immutable record)

**Throws:** Same as `GetComponentRO<T>()`

**Usage:**
```csharp
var identity = view.GetManagedComponentRO<Identity>(entity);
Console.WriteLine($"Callsign: {identity.Callsign}");

// ✅ Allowed - creates new instance (non-destructive mutation)
var updated = identity with { Callsign = "NewCallsign" };

// ❌ Not allowed in modules anyway (they can't write to view)
```

**Performance:** O(1), ~30ns

**Important:**  
Tier 2 components **MUST** be immutable records. This is enforced by 3-layer validation system.

---

##### IsAlive()

```csharp
bool IsAlive(Entity e)
```

**Description:**  
Checks if entity is alive (not destroyed).

**Returns:** `true` if alive, `false` otherwise

**Usage:**
```csharp
if (!view.IsAlive(target))
{
    Console.WriteLine("Target destroyed, aborting attack");
    return;
}
```

**Performance:** O(1), ~10ns

---

##### ConsumeEvents<T>()

```csharp
ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged
```

**Description:**  
Gets all events of type `T` accumulated since module's last run.

**Type Constraint:** `T : unmanaged` (event struct)

**Returns:** `ReadOnlySpan<T>` - Zero-copy span of events

**Behavior:**
- For GDB: Events accumulated by `EventAccumulator`
- For SoD: Same, flushed to snapshot bus
- **Combines current frame + history**

**Usage:**
```csharp
var explosions = view.ConsumeEvents<ExplosionEvent>();
if (explosions.Length > 0)
{
    Console.WriteLine($"Detected {explosions.Length} explosions!");
    foreach (var explosion in explosions)
    {
        ReactToExplosion(explosion);
    }
}
```

**Performance:** O(1), ~50ns + iteration cost

**Note:**  
Events are read-only. Modules cannot modify event data.

---

##### Query()

```csharp
EntityQueryBuilder Query()
```

**Description:**  
Creates a query builder for iterating entities with specific components.

**Returns:** `EntityQueryBuilder` - Fluent builder for queries

**Usage:**
```csharp
var query = view.Query()
    .With<Position>()
    .With<Health>()
    .Without<Destroyed>()
    .Build();

query.ForEach(entity => {
    var pos = view.GetComponentRO<Position>(entity);
    var health = view.GetComponentRO<Health>(entity);
    // Process...
});
```

**Performance:** Query building: O(number of archetypes)
Iteration: O(matching entities)

---

## Snapshot Providers

### ISnapshotProvider

**Namespace:** `ModuleHost.Core.Providers`  
**Assembly:** ModuleHost.Core.dll

```csharp
public interface ISnapshotProvider : IDisposable
{
    ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    void ReleaseView(ISimulationView view);
}
```

**Description:**  
Strategy pattern for acquiring/releasing simulation views.

**Implementations:**
1. `DoubleBufferProvider` - GDB (persistent replica)
2. `OnDemandProvider` - SoD (pooled snapshots)
3. `SharedSnapshotProvider` - GDB convoy pattern

---

#### AcquireView()

```csharp
ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
```

**Description:**  
Acquires a view of simulation state.

**Parameters:**
- `mask` - Component filter (provider may ignore for GDB)
- `lastSeenTick` - Last tick consumer saw (for event accumulation)

**Returns:** `ISimulationView` instance
- GDB: Returns persistent replica
- SoD: Returns pooled snapshot

**Usage:**
```csharp
// Called by ModuleHost for each module
var view = provider.AcquireView(moduleMask, moduleLastTick);
module.Tick(time, view, commands);
provider.ReleaseView(view);
```

---

#### ReleaseView()

```csharp
void ReleaseView(ISimulationView view)
```

**Description:**  
Releases the view.

**Behavior:**
- GDB: No-op (replica stays alive)
- SoD: Clears and returns snapshot to pool

---

### DoubleBufferProvider

**Namespace:** `ModuleHost.Core.Providers`

```csharp
public class DoubleBufferProvider : ISnapshotProvider
{
    public DoubleBufferProvider(EntityRepository liveWorld);
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    public void ReleaseView(ISimulationView view);
    public void Dispose();
}
```

**Description:**  
GDB strategy - maintains persistent replica synced every call.

**Usage:**
```csharp
var provider = new DoubleBufferProvider(liveWorld);

// Every frame
var view = provider.AcquireView(BitMask256.All, lastTick);
// view is the SAME replica instance each time
provider.ReleaseView(view);  // No-op
```

**Performance:**
- AcquireView: <2ms (full sync)
- ReleaseView: ~0ns (no-op)

**Memory:**
- Allocates 1x full EntityRepository (persistent)
- Zero per-frame allocations

**Best For:**
- High-frequency modules (>=30Hz)
- Modules needing 100% data (Flight Recorder)

---

### OnDemandProvider

**Namespace:** `ModuleHost.Core.Providers`

```csharp
public class OnDemandProvider : ISnapshotProvider
{
    public OnDemandProvider(EntityRepository liveWorld);
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    public void ReleaseView(ISimulationView view);
    public void Dispose();
}
```

**Description:**  
SoD strategy - uses pooled snapshots with filtered sync.

**Usage:**
```csharp
var provider = new OnDemandProvider(liveWorld);
var aiMask = new BitMask256(typeof(Position), typeof(Team));

// Periodic
var view = provider.AcquireView(aiMask, lastTick);
// view is a DIFFERENT snapshot each time (from pool)
provider.ReleaseView(view);  // Returns to pool
```

**Performance:**
- AcquireView: <500μs (filtered sync, 50% data)
- ReleaseView: ~50μs (clear + pool)

**Memory:**
- Pool grows as needed (typically 2-3 snapshots)
- Per-snapshot: ~50% of full repository (filtered)

**Best For:**
- Low-frequency modules (<30Hz)
- Modules needing sparse data (AI with position/team only)

---

### SharedSnapshotProvider

**Namespace:** `ModuleHost.Core.Providers`

```csharp
public class SharedSnapshotProvider : ISnapshotProvider
{
    public SharedSnapshotProvider(EntityRepository liveWorld);
    
    public bool TryUpdateReplica();  // ← Extra method
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    public void ReleaseView(ISimulationView view);
    public void Dispose();
}
```

**Description:**  
GDB with convoy pattern - multiple slow modules share one replica.

**Convoy Pattern:**  
Replica only syncs when ALL readers have released (slowest defines pace).

**Usage:**
```csharp
var provider = new SharedSnapshotProvider(liveWorld);

// Orchestrator checks if can sync
if (provider.TryUpdateReplica())
{
    // Replica updated, dispatch all slow modules
    foreach (var module in slowModules)
    {
        var view = provider.AcquireView(mask, lastTick);
        Task.Run(() => {
            try { module.Tick(time, view, commands); }
            finally { provider.ReleaseView(view); }
        });
    }
}
```

**Performance:**
- TryUpdateReplica: <2ms if no readers, 0ns if locked
- AcquireView: ~20ns (just increments counter)
- ReleaseView: ~10ns (decrements counter)

**Thread Safety:** Thread-safe (uses `Interlocked` for reader count)

**Best For:**
- Multiple slow modules with overlapping data needs
- When slowest module defines acceptable latency

---

### SnapshotPool

**Implemented in:** BATCH-03 ⭐  
**Namespace:** `ModuleHost.Core.Providers`

```csharp
public class SnapshotPool
{
    public SnapshotPool(Action<EntityRepository> schemaSetup, int warmupCount = 0);
    
    public EntityRepository Rent();
    public void Return(EntityRepository repo);
}
```

**Description:**  
Thread-safe pool for `EntityRepository` instances. Reduces GC pressure by reusing snapshots.

**Constructor:**
- `schemaSetup` - Action to register components on new repositories
- `warmupCount` - Pre-allocate this many repositories

**Methods:**
- `Rent()` - Get repository from pool (or create new)
- `Return(repo)` - Clear via `Soft Clear()` and return to pool

**Thread Safety:** Uses `ConcurrentStack<T>` (lock-free)

**Performance:**
- Rent: ~50ns (if available in pool)
- Return: ~100ns + SoftClear cost

**Used By:** OnDemandProvider, SharedSnapshotProvider

**See Also:** Snapshot Pooling (User Guide)

---

## Module Framework

### IModule

**Namespace:** `ModuleHost.Framework`  
**Assembly:** ModuleHost.Framework.dll

```csharp
public interface IModule
{
    /// <summary>
    /// Module name (for diagnostics and logging).
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Execution policy defining how and when this module runs.
    /// Replaces Tier and UpdateFrequency.
    /// </summary>
    ExecutionPolicy Policy { get; }

    /// <summary>
    /// Optional: Components this module reads. 
    /// Used to optimize convoy snapshot synchronization.
    /// Returns null to sync ALL components (default).
    /// </summary>
    IEnumerable<Type>? GetRequiredComponents() { get; }
    
    /// <summary>
    /// Optional: Events this module watches for reactive scheduling.
    /// </summary>
    IReadOnlyList<Type>? WatchEvents { get; }
    
    /// <summary>
    /// Register systems for this module.
    /// </summary>
    void RegisterSystems(ISystemRegistry registry);
    
    /// <summary>
    /// Main execution method.
    /// </summary>
    void Tick(ISimulationView view, float deltaTime);

    // ==========================================
    // DEPRECATED (Kept for backward compatibility)
    // ==========================================
    
    [Obsolete("Use Policy.Mode instead")]
    ModuleTier Tier { get; }
    
    [Obsolete("Use Policy.TargetFrequencyHz instead")]
    int UpdateFrequency { get; }
    
    [Obsolete("Use Policy.MaxExpectedRuntimeMs instead")]
    int MaxExpectedRuntimeMs { get; }
    
    [Obsolete("Use Policy.FailureThreshold instead")]
    int FailureThreshold { get; }
    
    [Obsolete("Use Policy.CircuitResetTimeoutMs instead")]
    int CircuitResetTimeoutMs { get; }
}
```

**Change from v1.0:**  
`ISimWorldSnapshot` → `ISimulationView` in `Tick()` signature.

---

#### Tick()
 
 ```csharp
 void Tick(ISimulationView view, float deltaTime)
 ```
 
 **Description:**  
 Main module logic executed asynchronously.
 
 **Parameters:**
 - `view` - Read-only simulation view covering the module's execution window
 - `deltaTime` - Accumulated time delta since this module last ran (seconds)
 
 **Contract:**
 - **Read from view** (never mutate!)
 - **No side effects** on shared state (thread safety)
 - **Return quickly** (respect `MaxExpectedRuntimeMs`)
 - **Use external CommandBuffer** (if available) or other thread-safe mechanism for output
 
 **Example:**
 ```csharp
 public void Tick(ISimulationView view, float deltaTime)
 {
     // Read state
     var enemies = view.Query().With<Enemy>().Build();
     
     foreach (var enemy in enemies)
     {
         var pos = view.GetComponentRO<Position>(enemy);
         // ... logic ...
     }
 }
 ```

---

## Resilience & Safety

**Implemented in:** BATCH-04 ⭐  
**Namespace:** `ModuleHost.Core.Resilience`

ModuleHost includes built-in fault isolation to prevent faulty modules from crashing or hanging the simulation.

**See Also:** Resilience & Safety (User Guide)

---

### ModuleCircuitBreaker

```csharp
public class ModuleCircuitBreaker
{
    public ModuleCircuitBreaker(int failureThreshold = 3, int resetTimeoutMs = 5000);
    
    public CircuitState State { get; }
    public int FailureCount { get; }
    
    public bool CanRun();
    public void RecordSuccess();
    public void RecordFailure(string reason);
    public void Reset();
}

public enum CircuitState
{
    Closed,     // Normal operation
    Open,       // Module disabled (too many failures)
    HalfOpen    // Probation period (testing recovery)
}
```

**Description:**  
Three-state circuit breaker for module fault isolation. Tracks module health and disables modules that fail repeatedly.

**Constructor:**
- `failureThreshold` - Consecutive failures before opening (default: 3)
- `resetTimeoutMs` - Cooldown before attempting recovery (default: 5000ms)

**Properties:**
- `State` - Current circuit state (Closed/Open/HalfOpen)
- `FailureCount` - Consecutive failures recorded

**Methods:**

#### CanRun()
```csharp
public bool CanRun()
```
Check if module should execute this frame.

**Returns:**
- `Closed` → `true` (normal operation)
- `Open` → `false` (disabled, unless timeout elapsed → transitions to HalfOpen)
- `HalfOpen` → `true` (allow ONE execution for recovery test)

#### RecordSuccess()
```csharp
public void RecordSuccess()
```
Record successful execution. Resets failure count and closes circuit if in HalfOpen state.

#### RecordFailure()
```csharp
public void RecordFailure(string reason)
```
Record failure (exception or timeout). Increments count and opens circuit if threshold exceeded.

**State Transitions:**
- `Closed` → `Open` (when failureCount >= threshold)
- `Open` → `HalfOpen` (after resetTimeout elapsed)
- `HalfOpen` → `Closed` (on success)
- `HalfOpen` → `Open` (on failure)

**Thread Safety:** Thread-safe (uses `lock`)

**Usage:**
```csharp
var breaker = new ModuleCircuitBreaker(failureThreshold: 3, resetTimeoutMs: 5000);

// Before execution
if (!breaker.CanRun())
{
    Console.WriteLine("Module disabled by circuit breaker");
    return;
}

try
{
    module.Tick(view, deltaTime);
    breaker.RecordSuccess();
}
catch (Exception ex)
{
    breaker.RecordFailure(ex.Message);
}
```

**See Also:**  
- ModuleStats
- IModule resilience properties
- Zombie Tasks (User Guide)

---

### ModuleStats

```csharp
public struct ModuleStats
{
    public string ModuleName { get; set; }
    public int ExecutionCount { get; set; }
    public CircuitState CircuitState { get; set; }
    public int FailureCount { get; set; }
}
```

**Description:**  
Diagnostic struct returned by `ModuleHostKernel.GetExecutionStats()`.

**Properties:**
- `ModuleName` - Module identifier
- `ExecutionCount` - Times executed since last stats retrieval
- `CircuitState` - Current circuit breaker state
- `FailureCount` - Consecutive failures

**Usage:**
```csharp
var stats = kernel.GetExecutionStats();
foreach (var s in stats)
{
    Console.WriteLine($"{s.ModuleName}: {s.CircuitState}, " +
                      $"Runs={s.ExecutionCount}, Failures={s.FailureCount}");
}
```

**Note:** `GetExecutionStats()` resets `ExecutionCount` to 0 after returning (read-once behavior).

---

## Entity Lifecycle Management

**Implemented in:** BATCH-06 ⭐  
**Namespace:** `ModuleHost.Core.ELM`

Cooperative entity initialization and destruction across distributed modules.

**See Also:** Entity Lifecycle Management (User Guide)

---

### EntityLifecycleModule

```csharp
public class EntityLifecycleModule : IModule
{
    public EntityLifecycleModule(int[] participatingModules, int timeoutFrames = 300);
    
    public void BeginConstruction(Entity entity, int typeId, uint currentFrame, IEntityCommandBuffer cmd);
    public void BeginDestruction(Entity entity, uint currentFrame, FixedString64 reason, IEntityCommandBuffer cmd);
    
    public (int pending, int constructed, int destroyed, int timeouts) GetStatistics();
}
```

**Description:**  
Central coordinator for entity lifecycle. Tracks ACKs from participating modules.

**Constructor:**
- `participatingModules` - Module IDs that must ACK (e.g., `new[] { 1, 2, 3 }`)
- `timeoutFrames` - Frames before timeout (default: 300 = 5s at 60 FPS)

**Methods:**

#### BeginConstruction()
```csharp
public void BeginConstruction(Entity entity, int typeId, uint currentFrame, IEntityCommandBuffer cmd)
```

Starts construction flow. Publishes `ConstructionOrder` event.

**Parameters:**
- `entity` - Entity to construct (should be in `Constructing` state)
- `typeId` - Entity type identifier (for module filtering)
- `currentFrame` - Current frame number (for timeout tracking)
- `cmd` - Command buffer for event publishing

**Flow:**
1. Publishes `ConstructionOrder{ Entity, TypeId }`
2. Waits for `ConstructionAck` from all participating modules
3. On all ACKs → Sets entity to `Active`
4. On NACK/timeout → Destroys entity

#### BeginDestruction()
```csharp
public void BeginDestruction(Entity entity, uint currentFrame, FixedString64 reason, IEntityCommandBuffer cmd)
```

Starts destruction flow. Publishes `DestructionOrder` event.

**Parameters:**
- `entity` - Entity to destroy
- `currentFrame` - Current frame number
- `reason` - Destruction reason (for logging/debugging)
- `cmd` - Command buffer

**Flow:**
1. Sets entity to `TearDown` state
2. Publishes `DestructionOrder{ Entity, Reason }`
3. Waits for `DestructionAck` from all participating modules
4. On all ACKs → Destroys entity
5. On timeout → Force-destroys entity

#### GetStatistics()
```csharp
public (int pending, int constructed, int destroyed, int timeouts) GetStatistics()
```

Returns ELM statistics for monitoring.

**Returns:**
- `pending` - Entities awaiting ACKs
- `constructed` - Successfully activated entities
- `destroyed` - Successfully destroyed entities
- `timeouts` - Entities that timed out

---

### Lifecycle Events

**Namespace:** `ModuleHost.Core.ELM`

All lifecycle events are **unmanaged structs** (zero GC).

```csharp
public struct ConstructionOrder
{
    public Entity Entity;
    public int TypeId;
}

public struct ConstructionAck
{
    public Entity Entity;
    public int ModuleId;
    public bool Success;
    public FixedString64 ErrorMessage;  // If Success=false
}

public struct DestructionOrder
{
    public Entity Entity;
    public FixedString64 Reason;
}

public struct DestructionAck
{
    public Entity Entity;
    public int ModuleId;
    public bool Success;
}
```

**Usage:**
```csharp
// Module reacts to construction order
foreach (var order in view.ConsumeEvents<ConstructionOrder>())
{
    if (order.TypeId == MY_ENTITY_TYPE)
    {
        // Perform setup
        cmd.AddComponent(order.Entity, new MyComponent { ... });
        
        // ACK success
        cmd.PublishEvent(new ConstructionAck
        {
            Entity = order.Entity,
            ModuleId = MY_MODULE_ID,
            Success = true
        });
    }
}
```

**Performance:** Unmanaged events ensure zero GC pressure for lifecycle coordination.

---

## Distributed Ownership & Network Integration

**Implemented in:** BATCH-07 + BATCH-07.1 ⭐  
**Namespace:** `ModuleHost.Core.Network`

Network integration for distributed simulation with partial descriptor ownership.

**See Also:** Distributed Ownership & Network Integration (User Guide)

---

### NetworkOwnership

```csharp
public struct NetworkOwnership
{
    public int PrimaryOwnerId;
    public Dictionary<long, int> PartialOwners;
    public int LocalNodeId;
    
    public bool OwnsDescriptor(long descriptorTypeId);
    public void SetDescriptorOwner(long descriptorTypeId, int ownerId);
    public int GetOwner(long descriptorTypeId);
}
```

**Description:**  
Tracks network ownership for an entity. Supports partial (per-descriptor) ownership per SST protocol.

**Fields:**
- `PrimaryOwnerId` - EntityMaster descriptor owner (default fallback)
- `PartialOwners` - Per-descriptor ownership map (key: descriptor type ID, value: owner node ID)
- `LocalNodeId` - This node's ID for ownership comparison

**Methods:**

#### OwnsDescriptor()
```csharp
public bool OwnsDescriptor(long descriptorTypeId)
```

Returns `true` if this node owns the specified descriptor.

**Behavior:**
1. Check `PartialOwners` dictionary
2. If found, compare with `LocalNodeId`
3. If not found, check `PrimaryOwnerId` (fallback to EntityMaster owner)

**Usage:**
```csharp
var ownership = view.GetComponentRO<NetworkOwnership>(entity);
if (ownership.OwnsDescriptor(descriptorTypeId: 2))
{
    // We own WeaponState descriptor, publish it
}
```

#### SetDescriptorOwner()
```csharp
public void SetDescriptorOwner(long descriptorTypeId, int ownerId)
```

Sets ownership for a specific descriptor.

**Parameters:**
- `descriptorTypeId` - Descriptor type (e.g., 1=EntityState, 2=WeaponState, 0=EntityMaster)
- `ownerId` - New owner node ID

**Special Case:** If `descriptorTypeId == 0` (EntityMaster), updates `PrimaryOwnerId`.

**Usage:**
```csharp
own.SetDescriptorOwner(2, newOwnerId: 3);  // Transfer WeaponState to Node 3
```

#### GetOwner()
```csharp
public int GetOwner(long descriptorTypeId)
```

Returns the owner node ID for a descriptor.

**Returns:** Owner ID from `PartialOwners`, or `PrimaryOwnerId` if not found.

---

### DescriptorOwnershipMap

```csharp
public class DescriptorOwnershipMap
{
    public void RegisterMapping(long descriptorTypeId, params Type[] componentTypes);
    public Type[] GetComponentsForDescriptor(long descriptorTypeId);
    public long GetDescriptorForComponent(Type componentType);
}
```

**Description:**  
Maps SST descriptor types to FDP component types for ownership tracking.

**Why Needed:**
- Network uses rich descriptors (multiple fields per message)
- FDP uses atomic components (normalized ECS)
- Ownership must apply to correct components

**Usage:**
```csharp
var map = new DescriptorOwnershipMap();

// EntityState descriptor controls Position, Velocity, Orientation
map.RegisterMapping(
    descriptorTypeId: 1,
    typeof(Position),
    typeof(Velocity),
    typeof(Orientation)
);

// When EntityState ownership changes...
var components = map.GetComponentsForDescriptor(1);
foreach (var type in components)
{
    // Update FDP component metadata
    view.GetComponentTable(type).Metadata.OwnerId = newOwnerId;
}
```

---

### OwnershipUpdate Message

```csharp
public struct OwnershipUpdate
{
    public long EntityId;
    public long DescrTypeId;
    public long DescrInstanceId;
    public int NewOwner;
}
```

**Description:**  
SST ownership transfer message. Sent when descriptor ownership changes between nodes.

**Fields:**
- `EntityId` - Network entity ID (not FDP Entity)
-`DescrTypeId` - Descriptor type ID
- `DescrInstanceId` - Descriptor instance ID (0 for single-instance descriptors)
- `NewOwner` - New owner node ID

**Transfer Protocol:**
1. Initiator sends `OwnershipUpdate`
2. Current owner receives → stops publishing descriptor
3. New owner receives → publishes descriptor to "confirm"
4. FDP component metadata synced

**Usage:**
```csharp
var update = new OwnershipUpdate
{
    EntityId = 100,
    DescrTypeId = 2,  // WeaponState
    NewOwner = 3       // Transfer to Node 3
};

networkGateway.SendOwnershipUpdate(update);
```

**See Also:** SST Rules (`bdc-sst-rules.md`)

---

### DdsInstanceState

```csharp
public enum DdsInstanceState
{
    Alive,
    NotAliveDisposed,
    NotAliveNoWriters
}
```

**Description:**  
DDS descriptor instance state for disposal handling.

**Values:**
- `Alive` - Normal, active descriptor
- `NotAliveDisposed` - Descriptor explicitly disposed (ownership return or entity deletion)
- `NotAliveNoWriters` - All writers gone (optional handling)

**Disposal Rules (SST):**
1. **EntityMaster disposed** → Entity deleted
2. **Non-master disposed by partial owner** → Ownership returns to EntityMaster owner
3. **Non-master disposed by primary owner** → Ignore (entity deletion in progress)

**Usage:**
```csharp
foreach (var sample in reader.TakeSamples())
{
    if (sample.InstanceState == DdsInstanceState.NotAliveDisposed)
    {
        HandleDescriptorDisposal(sample.Data.EntityId);
        continue;
    }
    
    // Normal ingress...
}
```

---

### IDataSample

```csharp
public interface IDataSample
{
    object Data { get; }
    DdsInstanceState InstanceState { get; }
}
```

**Description:**  
Wrapper for DDS samples that includes instance state metadata.

**Fields:**
- `Data` - Descriptor data (e.g., `EntityStateDescriptor`)
- `InstanceState` - DDS instance state (Alive/Disposed/NoWriters)

**Why Wrapper:**
- DDS provides instance state separately from data
- Need both for disposal handling
- Cleaner than tuple or out parameters

---

###IDataReader

```csharp
public interface IDataReader : IDisposable
{
    IEnumerable<IDataSample> TakeSamples();
}
```

**Description:**  
Abstraction over DDS DataReader for testability and DDS independence.

**Methods:**

#### TakeSamples()
```csharp
IEnumerable<IDataSample> TakeSamples()
```

Reads and removes available samples from DDS topic.

**Returns:** Enumerable of samples with instance state.

**Behavior:**
- Consumes samples (read + take)
- Returns wrapper with data + instance state
- Empty if no new samples

---

## Utility Types

### BitMask256

**Namespace:** `Fdp.Kernel`

```csharp
public struct BitMask256
{
    public static BitMask256 All { get; }
    public static BitMask256 None { get; }
    
    public void Set(Type componentType);
    public void Set(int typeId);
    public bool IsSet(int typeId);
    public void Clear(int typeId);
    
    public BitMask256 BitwiseOr(BitMask256 other);  // BATCH-03
}
```

**Usage:**
```csharp
// Create mask for AI
var aiMask = new BitMask256();
aiMask.Set(typeof(Position));
aiMask.Set(typeof(Team));
aiMask.Set(typeof(Health));

// Use in SoD
snapshot.SyncFrom(live, aiMask);

// Convoy union (BATCH-03)
// Combine requirements from multiple modules
var mask1 = module1Mask;  // Position, Health
var mask2 = module2Mask;  // Health, Velocity
var unionMask = mask1.BitwiseOr(mask2);  // Position | Health | Velocity
```

---

### ComponentMask / EventTypeMask

**Namespace:** `ModuleHost.Framework`

```csharp
public class ComponentMask
{
    public ComponentMask(BitMask256 mask);
    public BitMask256 Mask { get; }
}

public class EventTypeMask
{
    public EventTypeMask();
    public void Set(Type eventType);
}
```

**Usage:**
```csharp
public ComponentMask GetSnapshotRequirements()
{
    var mask = new BitMask256();
    mask.Set(typeof(Position));
    mask.Set(typeof(Team));
    return new ComponentMask(mask);
}
```

---

## Examples

### Example 1: Simple Read-Only Query

```csharp
public JobHandle Tick(FrameTime time, ISimulationView view, ICommandBuffer commands)
{
    var query = view.Query()
        .With<Position>()
        .With<Velocity>()
        .Build();
    
    foreach (var entity in query)
    {
        var pos = view.GetComponentRO<Position>(entity);
        var vel = view.GetComponentRO<Velocity>(entity);
        
        Console.WriteLine($"Entity {entity} at ({pos.X}, {pos.Y}) moving at {vel.X}");
    }
    
    return default;
}
```

---

### Example 2: Event-Driven Logic

```csharp
public JobHandle Tick(FrameTime time, ISimulationView view, ICommandBuffer commands)
{
    var explosions = view.ConsumeEvents<ExplosionEvent>();
    if (explosions.Length == 0)
        return default;  // No events, early exit
    
    foreach (var explosion in explosions)
    {
        Console.WriteLine($"Explosion at ({explosion.X}, {explosion.Y}) radius {explosion.Radius}");
        
        // Find nearby entities
        var nearby = FindEntitiesNear(view, explosion.Position, explosion.Radius);
        foreach (var entity in nearby)
        {
            // Issue scatter command
            commands.SetComponent(entity, new Orders {
                Type = OrderType.Scatter,
                Destination = CalculateSafeLocation(explosion)
            });
        }
    }
    
    return default;
}
```

---

### Example 3: Optimistic Concurrency

```csharp
public JobHandle Tick(FrameTime time, ISimulationView view, ICommandBuffer commands)
{
    var enemy = FindTarget(view);
    if (!view.IsAlive(enemy))
        return default;  // Already dead
    
    // Capture state
    var enemyGen = enemy.Generation;
    var enemyPos = view.GetComponentRO<Position>(enemy);
    
    // Long computation (world advances meanwhile)
    var path = CalculatePathToTarget(enemyPos);
    
    // Validate before commanding
    commands.EnqueueValidated(new AttackCommand {
        Target = enemy,
        ExpectedGeneration = enemyGen,  // ← Will validate
        ExpectedPosition = enemyPos,
        AttackPath = path
    });
    
    return default;
}
```

---

### Example 4: Provider Configuration

```csharp
public class HostSetup
{
    public void ConfigureHost(ModuleHostKernel host)
    {
        // Create providers
        var fastGdb = new DoubleBufferProvider(host.LiveWorld);
        var slowSod = new OnDemandProvider(host.LiveWorld);
        
        // High-frequency modules → GDB
        host.RegisterModule(new FlightRecorderModule(), fastGdb);
        host.RegisterModule(new NetworkModule(), fastGdb);
        
        // Low-frequency modules → SoD
        host.RegisterModule(new AiModule(), slowSod);
        host.RegisterModule(new AnalyticsModule(), slowSod);
    }
}
```

---

## Performance Guidelines

| Operation | Target | Notes |
|-----------|--------|-------|
| `GetComponentRO<T>()` | <20ns | Direct array access |
| `GetManagedComponentRO<T>()` | <30ns | Array access + cast |
| `IsAlive()` | <10ns | Bit check |
| `ConsumeEvents<T>()` | <50ns | Span creation |
| `Query().Build()` | <1μs | Archetype iteration |
| `SyncFrom()` (full) | <2ms | 100K entities, 30% dirty |
| `SyncFrom()` (filtered 50%) | <500μs | 100K entities, 30% dirty |
| `EventAccumulator.Flush()` | <100μs | 6 frames, 1K events/frame |

---

## Thread Safety

| API | Thread Safe? | Notes |
|-----|--------------|-------|
| `EntityRepository.SyncFrom()` | ❌ NO | Main thread only (sync point) |
| `NativeChunkTable.SyncDirtyChunks()` | ❌ NO | Main thread only |
| `EventAccumulator.CaptureFrame()` | ❌ NO | Main thread only |
| `ISimulationView.GetComponentRO()` | ✅ YES | Read-only, safe for background threads |
| `ISimulationView.ConsumeEvents()` | ✅ YES | Read-only |
| `SharedSnapshotProvider.AcquireView()` | ✅ YES | Thread-safe (uses Interlocked) |
| `ICommandBuffer.SetComponent()` | ✅ YES | Thread-safe (ConcurrentQueue) |

---

## Best Practices

### 1. Choose the Right Provider

```csharp
// ✅ GOOD: Recorder needs 100% data → GDB
host.RegisterModule(new RecorderModule(), doubleBufferProvider);

// ✅ GOOD: AI needs 50% data, runs 10Hz → SoD
host.RegisterModule(new AiModule(), onDemandProvider);

// ❌ BAD: Recorder with SoD (will sync 100% anyway, overhead for no benefit)
host.RegisterModule(new RecorderModule(), onDemandProvider);
```

---

### 2. Declare Requirements Accurately

```csharp
public ComponentMask GetSnapshotRequirements()
{
    var mask = new BitMask256();
    // ✅ GOOD: Only what you need
    mask.Set(typeof(Position));
    mask.Set(typeof(Team));
    
    // ❌ BAD: Requesting everything (defeats SoD purpose)
    return ComponentMask.All;
}
```

---

### 3. Never Mutate View

```csharp
// ❌ BAD: Mutating view data
ref var pos = ref view.GetComponentRO<Position>(entity);
pos.X = 10;  // WRONG! Corrupts snapshot

// ✅ GOOD: Use commands
commands.SetComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
```

---

### 4. Handle Missing Entities

```csharp
// ✅ GOOD: Defensive programming
if (!view.IsAlive(target))
{
    Console.WriteLine("Target destroyed");
    return default;
}

var pos = view.GetComponentRO<Position>(target);
```

---

## See Also

- [IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md) - Master specification
- [MODULE-IMPLEMENTATION-EXAMPLES.md](MODULE-IMPLEMENTATION-EXAMPLES.md) - Complete examples
- [HYBRID-ARCHITECTURE-QUICK-REFERENCE.md](HYBRID-ARCHITECTURE-QUICK-REFERENCE.md) - Quick start guide
- [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md) - Migration from v1.0

---

## Geographic Transform Services

**Namespace:** `ModuleHost.Core.Geographic`  
**Assembly:** ModuleHost.Core.dll  
**Added:** BATCH-08 (January 2026)

Bridge between FDP's local Cartesian physics and global WGS84 geodetic coordinates for network interoperability.

---

### IGeographicTransform

Abstract interface for coordinate transformation.

```csharp
public interface IGeographicTransform
{
    void SetOrigin(double latitudeDeg, double longitudeDeg, double altitudeMeters);
    Vector3 ToCartesian(double latitudeDeg, double longitudeDeg, double altitudeMeters);
    (double lat, double lon, double alt) ToGeodetic(Vector3 localPosition);
}
```

#### SetOrigin

```csharp
void SetOrigin(double latitudeDeg, double longitudeDeg, double altitudeMeters)
```

Sets the origin point for the local tangent plane coordinate system.

**Parameters:**
- `latitudeDeg` - Latitude in degrees (-90 to 90)
- `longitudeDeg` - Longitude in degrees (-180 to 180)
- `altitudeMeters` - Altitude in meters above WGS84 ellipsoid

**Throws:**
- `ArgumentOutOfRangeException` - If latitude outside valid range

**Example:**
```csharp
var transform = new WGS84Transform();
transform.SetOrigin(37.7749, -122.4194, 0);  // San Francisco
```

**Important:** Choose origin near simulation center. Accuracy degrades beyond ~100km.

#### ToCartesian

```csharp
Vector3 ToCartesian(double latitudeDeg, double longitudeDeg, double altitudeMeters)
```

Converts geodetic coordinates to local Cartesian (ENU tangent plane).

**Parameters:**
- `latitudeDeg` - Latitude in degrees
- `longitudeDeg` - Longitude in degrees
- `altitudeMeters` - Altitude in meters

**Returns:** Local position (X=East, Y=North, Z=Up) in meters

**Throws:**
- `ArgumentOutOfRangeException` - If latitude outside valid range

**Example:**
```csharp
var localPos = transform.ToCartesian(37.8, -122.42, 1000);
// Returns Vector3 relative to origin
```

**Precision:**
- Sub-centimeter within 10km
- ~10cm within 100km
- Degrades beyond 100km

#### ToGeodetic

```csharp
(double lat, double lon, double alt) ToGeodetic(Vector3 localPosition)
```

Converts local Cartesian position to geodetic coordinates.

**Parameters:**
- `localPosition` - Local ENU position in meters

**Returns:** Tuple of (latitude°, longitude°, altitude meters)

**Example:**
```csharp
var (lat, lon, alt) = transform.ToGeodetic(new Vector3(100, 50, 10));
Console.WriteLine($"Position: {lat:F6}°, {lon:F6}°, {alt:F1}m");
```

---

### WGS84Transform

Concrete implementation using WGS84 ellipsoid model.

```csharp
public class WGS84Transform : IGeographicTransform
{
    public void SetOrigin(double latitudeDeg, double longitudeDeg, double altitudeMeters);
    public Vector3 ToCartesian(double latitudeDeg, double longitudeDeg, double altitudeMeters);
    public (double lat, double lon, double alt) ToGeodetic(Vector3 localPosition);
}
```

**Implementation Details:**
- Uses double precision for ECEF calculations (prevents jitter)
- ENU tangent plane aligned to origin latitude/longitude
- 5-iteration ECEF→Geodetic conversion for accuracy
- WGS84 constants: a=6,378,137m, f=1/298.257223563

**Thread Safety:** Not thread-safe. Create one instance per thread or use locking.

---

### PositionGeodetic

Managed component storing global geodetic coordinates.

```csharp
public class PositionGeodetic
{
    public double Latitude { get; set; }    // Degrees (-90 to 90)
    public double Longitude { get; set; }   // Degrees (-180 to 180)
    public double Altitude { get; set; }    // Meters above WGS84 ellipsoid
}
```

**Usage:**
```csharp
repo.RegisterComponent<PositionGeodetic>();  // Managed component

var entity = repo.CreateEntity();
repo.AddComponent(entity, new PositionGeodetic
{
    Latitude = 37.7749,
    Longitude = -122.4194,
    Altitude = 100
});
```

**Note:** Managed component (class) for double precision. Use `GetManagedComponentRO<>` / `SetManagedComponent<>`.

---

### CoordinateTransformSystem

Synchronizes geodetic coordinates from physics for owned entities.

```csharp
[UpdateInPhase(SystemPhase.PostSimulation)]
public class CoordinateTransformSystem : IModuleSystem
{
    public CoordinateTransformSystem(IGeographicTransform transform);
    public void Execute(ISimulationView view, float deltaTime);
}
```

#### Constructor

```csharp
public CoordinateTransformSystem(IGeographicTransform transform)
```

**Parameters:**
- `transform` - Geographic transform service (injected)

#### Execute

Runs after physics, updates `PositionGeodetic` from `Position` for owned entities.

**Query:**
```csharp
.With<Position>()
.WithManaged<PositionGeodetic>()
.With<NetworkOwnership>()
```

**Logic:**
```
For each entity:
  IF PrimaryOwnerId == LocalNodeId:  // We own it
    Read Position (physics)
    Convert to geodetic
    IF change > epsilon:  // 1e-6° or 0.1m
      Update PositionGeodetic (for network)
```

**Example:**
```csharp
var transform = new WGS84Transform();
transform.SetOrigin(37.7749, -122.4194, 0);

var system = new CoordinateTransformSystem(transform);
// Registered automatically by GeographicTransformModule
```

**Performance:** ~500 cycles per owned entity (iterative ECEF conversion)

---

### NetworkSmoothingSystem

Interpolates remote entity positions from geodetic updates.

```csharp
[UpdateInPhase(SystemPhase.Input)]
public class NetworkSmoothingSystem : IModuleSystem
{
    public NetworkSmoothingSystem(IGeographicTransform transform);
    public void Execute(ISimulationView view, float deltaTime);
}
```

#### Constructor

```csharp
public NetworkSmoothingSystem(IGeographicTransform transform)
```

**Parameters:**
- `transform` - Geographic transform service (injected)

#### Execute

Runs before physics, smooths `Position` toward `PositionGeodetic` for remote entities.

**Query:**
```csharp
.With<Position>()
.WithManaged<PositionGeodetic>()
.With<NetworkOwnership>()
```

**Logic:**
```
For each entity:
  IF PrimaryOwnerId != LocalNodeId:  // Remote entity
    Read PositionGeodetic (from network)
    Convert to local Cartesian (target)
    Lerp Position toward target (smoothing)
    Update Position (for rendering)
```

**Smoothing Formula:**
```csharp
float t = Math.Clamp(deltaTime * 10.0f, 0f, 1f);
Position.Value = Vector3.Lerp(currentPos, targetPos, t);
```

**Convergence:** ~0.1 seconds to reach target

**Example:**
```csharp
var system = new NetworkSmoothingSystem(transform);
// Runs every frame for smooth remote entity movement
```

**Performance:** ~200 cycles per remote entity (trig functions)

---

### GeographicTransformModule

Module packaging both transform systems.

```csharp
public class GeographicTransformModule : IModule
{
    public GeographicTransformModule(
        double originLatitudeDeg,
        double originLongitudeDeg,
        double originAltitudeMeters
    );
    
    public string ModuleName { get; }
    public Type[] GetRequiredComponents();
    public void RegisterSystems(ISystemRegistry registry);
    public void Tick(ISimulationView view, float deltaTime);
}
```

#### Constructor

```csharp
public GeographicTransformModule(
    double originLatitudeDeg,
    double originLongitudeDeg,
    double originAltitudeMeters
)
```

**Parameters:**
- `originLatitudeDeg` - Origin latitude (-90 to 90)
- `originLongitudeDeg` - Origin longitude (-180 to 180)
- `originAltitudeMeters` - Origin altitude (meters)

**Example:**
```csharp
var geoModule = new GeographicTransformModule(
    latitudeDeg: 37.7749,    // San Francisco
    longitudeDeg: -122.4194,
    altitudeMeters: 0
);

kernel.RegisterModule(geoModule);
```

#### RegisterSystems

Registers both systems with the module host.

```csharp
public void RegisterSystems(ISystemRegistry registry)
{
    registry.RegisterSystem(new NetworkSmoothingSystem(_transform));    // Input phase
    registry.RegisterSystem(new CoordinateTransformSystem(_transform)); // PostSim phase
}
```

**Execution Order:**
1. Input: NetworkSmoothingSystem (remote entities)
2. Simulation: Physics/game logic
3. PostSimulation: CoordinateTransformSystem (owned entities)

#### Tick

Executes both systems in sequence.

```csharp
public void Tick(ISimulationView view, float deltaTime)
```

**Internal:** Calls Execute() on both systems in registration order.

---

### Usage Examples

#### Complete Setup

```csharp
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Geographic;
using ModuleHost.Core.Network;

// 1. Create kernel
var kernel = new ModuleHostKernel();

// 2. Register components
kernel.Repository.RegisterComponent<Position>();
kernel.Repository.RegisterComponent<NetworkOwnership>();
kernel.Repository.RegisterComponent<PositionGeodetic>();  // Managed

// 3. Add geographic module
var geoModule = new GeographicTransformModule(
    originLatitudeDeg: 37.7749,
    originLongitudeDeg: -122.4194,
    originAltitudeMeters: 0
);
kernel.RegisterModule(geoModule);

// 4. Use it
var entity = kernel.Repository.CreateEntity();

kernel.Repository.AddComponent(entity, new Position
{
    Value = new Vector3(100, 0, 50)  // 100m east, 50m up
});

kernel.Repository.AddComponent(entity, new PositionGeodetic
{
    Latitude = 37.7749,
    Longitude = -122.4194,
    Altitude = 50
});

kernel.Repository.AddComponent(entity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1  // We own it
});

// Systems will automatically sync coordinates
```

####  Owned vs Remote

```csharp
// Owned entity: Position → PositionGeodetic (physics drives network)
var ownedEntity = repo.CreateEntity();
repo.AddComponent(ownedEntity, new Position { Value = Vector3.Zero });
repo.AddComponent(ownedEntity, new PositionGeodetic());
repo.AddComponent(ownedEntity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1  // We control physics
});

// CoordinateTransformSystem updates PositionGeodetic each frame

// Remote entity: PositionGeodetic → Position (network drives rendering)
var remoteEntity = repo.CreateEntity();
repo.AddComponent(remoteEntity, new Position { Value = Vector3.Zero });
repo.AddComponent(remoteEntity, new PositionGeodetic
{
    Latitude = 37.8,
    Longitude = -122.42,
    Altitude = 100
});
repo.AddComponent(remoteEntity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 2  // Node 2 controls physics
});

// NetworkSmoothingSystem interpolates Position each frame
```

#### Manual Conversion

```csharp
// For debugging or custom logic
var transform = new WGS84Transform();
transform.SetOrigin(37.7749, -122.4194, 0);

// Geodetic → Local
var localPos = transform.ToCartesian(37.8, -122.42, 1000);
Console.WriteLine($"Local: {localPos}");  // Vector3 in meters

// Local → Geodetic
var (lat, lon, alt) = transform.ToGeodetic(new Vector3(1000, 500, 100));
Console.WriteLine($"Geodetic: {lat:F6}°, {lon:F6}°, {alt:F1}m");
```

---

### Performance Characteristics

**CoordinateTransformSystem:**
- Runs: PostSimulation phase (once per frame)
- Cost: ~500 cycles per owned entity
- Optimization: Skips if change < epsilon (1e-6° or 0.1m)
- Future: Add dirty checking (`HasComponentChanged<Position>`)

**NetworkSmoothingSystem:**
- Runs: Input phase (once per frame)
- Cost: ~200 cycles per remote entity
- Smoothing: Lerp convergence over ~0.1s

**Total for 200 Networked Entities:**
- 100 owned + 100 remote
- ~70,000 cycles/frame
- ~0.023ms @ 3GHz
- Negligible overhead

---

### Integration with Network Gateway

**Outbound (Owned Entity):**
```
1. Physics updates Position
2. CoordinateTransformSystem → PositionGeodetic
3. EntityStateTranslator reads PositionGeodetic
4. Publishes to DDS network
```

**Inbound (Remote Entity):**
```
1. DDS receives EntityStateDescriptor
2. EntityStateTranslator updates PositionGeodetic
3. NetworkSmoothingSystem → Position (interpolated)
4. Rendering uses Position
```

**See Also:**
- [Distributed Ownership & Network Integration](FDP-ModuleHost-User-Guide.md#distributed-ownership--network-integration)
- [Geographic Transform Services](FDP-ModuleHost-User-Guide.md#geographic-transform-services)

---

*Last Updated: January 8, 2026*
