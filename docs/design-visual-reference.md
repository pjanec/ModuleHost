# Design Overview - Quick Reference

**Visual guide to the ModuleHost architecture**

---

## System Architecture

```
┌────────────────────────────────────────────────────────┐
│                   USER MODULES                          │
│  SimModule  │  SSTModule  │  AIModule  │  UIModule     │
└──────┬──────┴──────┬──────┴─────┬──────┴──────┬────────┘
       │             │            │             │
       └─────────────┴────────────┴─────────────┘
                     │
       ┌─────────────▼──────────────────┐
       │    IModule Interface           │
       │  - RegisterSystems()           │
       │  - Tick(snapshot, commands)    │
       └─────────────┬──────────────────┘
                     │
       ┌─────────────▼──────────────────┐
       │    ModuleHostKernel             │
       │  - RunFrame()                   │
       │  - ExecutePhase()               │
       │  - ExecuteSyncPoint()           │
       └─────────────┬──────────────────┘
                     │
       ┌─────────────▼──────────────────┐
       │    EntityRepository (FDP)       │
       │  - Tier 1 (Unmanaged)           │
       │  - Tier 2 (Managed)             │
       └─────────────────────────────────┘
```

---

## Frame Execution Flow

```
START FRAME N
│
├─ Phase: NetworkIngest
│  └─ NetworkIngestSystem (DDS→FDP)
│
├─ Phase: Input
│  └─ InputModule systems
│
├─ Phase: Simulation
│  ├─ PhysicsSystem (Tier 1)
│  ├─ CollisionSystem
│  └─ MovementSystem
│
├─ Phase: PostSimulation
│  └─ CoordinateTransformSystem (Tier1→Tier2)
│
├─ ⏸️  SYNC POINT ⏸️
│  ├─ UpdateShadowBuffers() [memcpy dirty chunks]
│  ├─ CreateSnapshots()
│  └─ TriggerBackgroundModules()
│       ├─ AI Task (Thread Pool)
│       ├─ Analytics Task (Thread Pool)
│       └─ UI Task (Thread Pool)
│
├─ Phase: Structural
│  └─ PlaybackCommandBuffers() [from previous frame]
│
├─ Phase: Export
│  └─ NetworkSyncSystem (FDP→DDS)
│
END FRAME N
```

---

## Data Flow

### Synchronous Path (Main Thread)

```
┌─────────────┐
│   Input     │
│  Hardware   │
└──────┬──────┘
       │
       ▼
┌─────────────┐     ┌──────────────┐
│  Tier 1     │────▶│  Physics     │
│  Position   │◀────│  Systems     │
│  Velocity   │     └──────────────┘
└──────┬──────┘
       │
       ▼
┌─────────────┐     ┌──────────────┐
│ Coordinate  │────▶│  Tier 2      │
│ Transform   │     │  Geodetic    │
└─────────────┘     └──────┬───────┘
                           │
                           ▼
                    ┌──────────────┐
                    │     DDS      │
                    │   Publish    │
                    └──────────────┘
```

### Asynchronous Path (Background Threads)

```
┌─────────────┐
│  Snapshot   │ ←─── Shadow Buffer (dirty chunks only)
│   (Frame N) │
└──────┬──────┘
       │
       ├──▶ AI Module (Thread 1)
       │      │
       │      ▼
       │    ┌──────────────┐
       │    │ CommandBuffer│
       │    │ CreateEntity │
       │    │ SetComponent │
       │    └──────┬───────┘
       │           │
       ├──▶ UI Module (Thread 2)
       │      │
       │      ▼
       │    [Updates UI State]
       │
       └──▶ Analytics (Thread 3)
              │
              ▼
            [Logs metrics]
```

At **Frame N+1**, CommandBuffers are played back on main thread.

---

## Class Hierarchy

### Snapshot Subsystem

```
ISimWorldSnapshot (interface)
│
├─ HybridSnapshot
│  ├─ Tier1Snapshot → ShadowBuffer → unsafe byte*
│  └─ Tier2Snapshot → object[] (ArrayPool)
│
└─ SnapshotManager
   └─ Dictionary<Guid, ShadowBuffer>
```

### Module Framework

```
IModule (interface)
│
├─ SimulationModule
│  ├─ PhysicsSystem
│  ├─ CollisionSystem
│  └─ CoordinateTransformSystem
│
├─ SSTModule
│  ├─ NetworkSyncSystem
│  └─ NetworkIngestSystem
│
└─ AIModule
   └─ PathfindingSystem
```

### Host Kernel

```
ModuleHostKernel
│
├─ EntityRepository (FDP)
├─ SnapshotManager
├─ SystemRegistry
├─ BackgroundScheduler
└─ SnapshotLeaseManager
```

---

## Key Interfaces Summary

### Layer 1: Snapshot Core

```csharp
ISimWorldSnapshot
{
    T GetStruct<T>(Entity e);       // Tier 1 (unmanaged)
    T GetRecord<T>(Entity e);       // Tier 2 (managed)
    void Dispose();                 // Release resources
}

ISnapshotManager
{
    ISimWorldSnapshot CreateSnapshot(Guid consumerId, ComponentMask mask);
    void ReleaseShadowBuffer(Guid consumerId);
}
```

### Layer 2: Module Framework

```csharp
IModule
{
    void RegisterSystems(ISystemRegistry);  // Synchronous path
    JobHandle Tick(snapshot, commands);     // Async path (optional)
}

ISystemRegistry
{
    void RegisterSystem(ComponentSystem, Phase, int order);
}

IModuleContext
{
    EntityRepository Repository { get; }
    T GetService<T>();
}
```

### Layer 4: Command Buffer

```csharp
ICommandBuffer
{
    Entity CreateEntity(string name);        // Returns temp ID
    void SetComponent<T>(Entity, T value);   // Queued
    void Playback(EntityRepository);         // Execute on main thread
}
```

### Layer 5: Geographic

```csharp
IGeographicTransform
{
    void SetOrigin(double lat, double lon, double alt);
    PositionGeodetic ToGeodetic(PositionCartesian local);
    PositionCartesian ToCartesian(PositionGeodetic geo);
}
```

---

## Memory Layout

### Tier 1: Unmanaged (Shadow Buffer)

```
┌──────────────────────────────────────┐
│  Chunk 0 (64KB)                      │
│  ┌────────┬────────┬────────┐        │
│  │ Pos[0] │ Pos[1] │ ... │Pos[N]│    │  PositionCartesian
│  └────────┴────────┴────────┘        │
│  ┌────────┬────────┬────────┐        │
│  │ Vel[0] │ Vel[1] │ ... │Vel[N]│    │  Velocity
│  └────────┴────────┴────────┘        │
├──────────────────────────────────────┤
│  Chunk 1 (64KB)                      │
│  ...                                 │
└──────────────────────────────────────┘

Shadow Buffer (memcpy only if dirty)
┌──────────────────────────────────────┐
│  Chunk 0 Copy (if Version > Last)    │
├──────────────────────────────────────┤
│  Chunk 1 Copy (if Version > Last)    │
└──────────────────────────────────────┘
```

### Tier 2: Managed (Reference Array)

```
Live Array:
┌─────┬─────┬─────┬─────┐
│ ref │ ref │ ref │ ref │
└──┬──┴──┬──┴──┬──┴──┬──┘
   │     │     │     │
   ▼     ▼     ▼     ▼
 [Obj] [Obj] [Obj] [Obj]  ← Immutable records

Snapshot Array (shallow copy):
┌─────┬─────┬─────┬─────┐
│ ref │ ref │ ref │ ref │
└──┬──┴──┬──┴──┬──┴──┬──┘
   │     │     │     │
   └─────┴─────┴─────┴───── Point to SAME objects
```

---

## Component Types

### Tier 1 (Struct - Unmanaged)

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct PositionCartesian
{
    public Vector3 LocalPosition;
    public Quaternion Orientation;
}

[StructLayout(LayoutKind.Sequential)]
public struct Velocity
{
    public Vector3 Linear;
    public Vector3 Angular;
}

[StructLayout(LayoutKind.Sequential)]
public struct Health
{
    public float Current;
    public float Maximum;
}
```

### Tier 2 (Record - Managed)

```csharp
public record PositionGeodetic
{
    public required long EntityId { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required double Altitude { get; init; }
}

public record IdentityDescriptor
{
    public required long EntityId { get; init; }
    public required string Callsign { get; init; }
    public required DisEntityType Type { get; init; }
}

public record OrdersDescriptor
{
    public required long EntityId { get; init; }
    public required ImmutableList<Waypoint> Route { get; init; }
}
```

---

## Resilience Mechanisms

### 1. Snapshot Lease Expiry

```
Module gets snapshot at T=0
Module takes 50ms to process
T=50ms: Still valid ✅

Module gets snapshot at T=0
Module hangs (bug)
T=2000ms: HARD EXPIRY ❌
  → Snapshot.Invalidate()
  → Next GetStruct() throws SnapshotExpiredException
  → Module aborts gracefully
```

### 2. Circuit Breaker

```
Module executes → Success ✅ (failureCount = 0)
Module executes → Success ✅
Module executes → Exception ❌ (failureCount = 1)
Module executes → Exception ❌ (failureCount = 2)
Module executes → Exception ❌ (failureCount = 3)
  → State = OPEN 🔴
  → Module skipped for 5 seconds
T+5s → State = HALF_OPEN (one retry attempt)
Module executes → Success ✅
  → State = CLOSED 🟢 (back to normal)
```

### 3. Frame Watchdog

```
Frame starts
  → FrameWatchdog(200ms timeout) created
Frame executes systems...
  ├─ Input: 2ms
  ├─ Simulation: 8ms
  ├─ PostSim: 1ms
  └─ Sync Point: 1ms
Total: 12ms < 200ms ✅
  → Watchdog.Dispose() (cancel timer)

Frame starts
  → FrameWatchdog(200ms timeout) created
Frame hangs in buggy system
  ... 200ms elapses ...
  → Watchdog timeout fires ❌
  → Emergency stop
  → Stack dump
  → Crash report generated
```

---

## Performance Targets

| Metric | Target | Notes |
|--------|--------|-------|
| **Frame Time** | <16.67ms | 60 Hz |
| **Sync Point** | <2ms | Snapshot creation |
| **Snapshot Bandwidth** | <500 MB/s | 10-30% active entities |
| **GC Pressure** | Zero | Steady state (pooling) |
| **Physics Overhead** | Zero | Hot path unchanged |

---

## Implementation Phases

### ✅ Phase 0: Design (Complete)
- Architecture decided
- Interfaces defined
- Classes outlined

### 📋 Phase 1: Snapshot Core (Week 1-2)
- `ShadowBuffer` class
- `SnapshotManager` class
- `HybridSnapshot` implementation
- Unit tests

### 📋 Phase 2: Module Framework (Week 3)
- `IModule` interface
- `ModuleLoader` class
- `SystemRegistry` class
- Module discovery

### 📋 Phase 3: Host Kernel (Week 4)
- `ModuleHostKernel` class
- Phase execution
- Command buffer integration
- Main loop

### 📋 Phase 4: Services (Week 5)
- `GeographicTransform` class
- `CoordinateTransformSystem` class
- DDS gateway systems
- Integration tests

### 📋 Phase 5: Advanced (Week 6)
- ELM implementation
- Resilience mechanisms
- End-to-end testing
- Performance validation

---

## Success Criteria

### Functional:
- ✅ Modules load dynamically
- ✅ Systems execute in correct phase order
- ✅ Snapshots are consistent (no torn reads)
- ✅ Background modules create entities via CommandBuffer
- ✅ DDS sync works bidirectionally
- ✅ ELM creates entities across distributed nodes

### Performance:
- ✅ 60 Hz stable with 100K entities
- ✅ Sync point <2ms
- ✅ Zero GC allocations per frame
- ✅ <10ms physics budget maintained

### Safety:
- ✅ Snapshot expiry prevents memory leaks
- ✅ Circuit breakers prevent cascading failures
- ✅ Watchdogs detect hung frames
- ✅ No data races (validated with ThreadSanitizer)

---

**Ready to begin detailed design of Layer 1 (Snapshot Core)!**
