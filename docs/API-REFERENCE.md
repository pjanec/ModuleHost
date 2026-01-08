# API Reference - Hybrid GDB+SoD Architecture

**Version:** 2.0  
**Date:** January 4, 2026  
**Namespace Index:**
- Fdp.Kernel
- ModuleHost.Core.Abstractions
- ModuleHost.Core.Providers
- ModuleHost.Framework

---

## Table of Contents

1. [FDP Kernel APIs](#fdp-kernel-apis)
2. [Core Abstractions](#core-abstractions)
3. [Snapshot Providers](#snapshot-providers)
4. [Module Framework](#module-framework)
5. [Utility Types](#utility-types)
6. [Examples](#examples)

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

## Core Abstractions

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

*Last Updated: January 4, 2026*
