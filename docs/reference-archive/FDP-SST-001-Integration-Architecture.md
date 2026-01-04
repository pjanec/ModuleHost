# FDP-SST-001: Integration Architecture Specification

| Metadata | Details |
| :---- | :---- |
| **Document ID** | FDP-SST-001 |
| **Type** | Technical Specification (Core Architecture) |
| **Status** | APPROVED - Definitive Design |
| **Date** | January 2, 2026 |
| **Dependencies** | Fdp.Kernel (existing), DDS, specification.md |

---

## 1. Executive Summary

This document defines the **integration architecture** between the Fast Data Plane (FDP) ECS engine and the Shared Simulation State (SST) layer for ModuleHost.

**Core Architectural Decision:**
The **ModuleHost Kernel owns the FDP EntityRepository**. Modules are plugins that register ComponentSystems with FDP, enabling flexible node configurations from physics-heavy simulators to passive monitoring stations.

**Key Features:**
- Single unified ECS (FDP) for all simulation state
- Host-centric model: FDP owned by kernel, not by SimModule
- True Copy-On-Write (COW) with ref-counted pages for async safety
- Dual coordinate representation (Cartesian + Geodetic) on same entities
- Physics-optional architecture (nodes without SimModule possible)
- System Provider pattern for module registration

---

## 2. The Ownership Model

### 2.1 Host-Centric FDP Architecture

```
┌─────────────────────────────────────────────────────────┐
│         ModuleHost Kernel (Backend.Host.exe)            │
│  ┌───────────────────────────────────────────────────┐  │
│  │         FDP EntityRepository (OWNED BY HOST)      │  │
│  │  ┌─────────────────────────────────────────────┐  │  │
│  │  │ Tier 1: Unmanaged Components (Native)       │  │  │
│  │  │  - PositionCartesian (Vec3)                 │  │  │
│  │  │  - VelocityCartesian (Vec3)                 │  │  │
│  │  │  - PhysicsState (Mass, Drag, etc.)          │  │  │
│  │  └─────────────────────────────────────────────┘  │  │
│  │  ┌─────────────────────────────────────────────┐  │  │
│  │  │ Tier 2: Managed Components (Heap)           │  │  │
│  │  │  - PositionGeodetic (Lat/Lon/Alt)           │  │  │
│  │  │  - IdentityDescriptor (Strings, Arrays)     │  │  │
│  │  │  - StatusDescriptor (Complex state)         │  │  │
│  │  └─────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────┘  │
│           ↑ Register Systems     ↑ Register Systems     │
│  ┌────────┴─────────┐   ┌────────┴──────────┐          │
│  │   SimModule      │   │   SSTModule        │          │
│  │  (OPTIONAL)      │   │   (DDS Gateway)    │          │
│  │ ┌──────────────┐ │   │ ┌────────────────┐ │          │
│  │ │Physics       │ │   │ │NetworkSync     │ │          │
│  │ │Collision     │ │   │ │DescriptorEgress│ │          │
│  │ │Movement      │ │   │ │DescriptorIngress│ │          │
│  │ └──────────────┘ │   │ └────────────────┘ │          │
│  └──────────────────┘   └────────────────────┘          │
└─────────────────────────────────────────────────────────┘
```

### 2.2 Lifecycle Ownership

| Component | Owner | Lifecycle |
|-----------|-------|-----------|
| **EntityRepository** | ModuleHost Kernel | Created at host startup, destroyed at shutdown |
| **ComponentSystems** | Modules (Plugins) | Registered during `Initialize()`, unregistered at `Stop()` |
| **Entity Data** | EntityRepository | Created/destroyed via unified API |
| **DDS Gateway** | SSTModule | Optional module, publishes FDP Tier-2 to network |
| **Physics Logic** | SimModule | Optional module, updates FDP Tier-1 |

### 2.3 Node Configuration Flexibility

**Configuration A: Full Simulator Node**
```json
{
  "modules": [
    "SimModule",      // Physics, AI, Ballistics
    "SSTModule",      // DDS Gateway
    "InputModule",    // Joystick/Hardware
    "ViewportModule"  // IG/Rendering
  ]
}
```
- Has FDP ✅
- Has Physics ✅
- Publishes to DDS ✅

**Configuration B: Passive Monitor Node**
```json
{
  "modules": [
    "SSTModule"  // DDS Gateway only
  ]
}
```
- Has FDP ✅ (owned by kernel)
- Has Physics ❌ (SimModule not loaded)
- Subscribes from DDS ✅
- Zero physics overhead 🎯

**Configuration C: Headless Simulation Node**
```json
{
  "modules": [
    "SimModule"  // Physics only, no UI
  ]
}
```
- Has FDP ✅
- Has Physics ✅
- No DDS publishing ❌ (if SSTModule not loaded)

---

## 3. Coordinate System Strategy

### 3.1 Dual-Component Representation

Entities requiring both internal (physics) and external (network) representations have **two position components**:

```csharp
// Tier 1: Internal Physics (Unmanaged, Cartesian)
[StructLayout(LayoutKind.Sequential)]
public struct PositionCartesian
{
    public Vector3 LocalPosition;  // Meters in Local Tangent Plane
    public Quaternion Orientation;
}

// Tier 2: Public SST (Managed, Geodetic)
[StructLayout(LayoutKind.Sequential)]
public struct PositionGeodetic
{
    public long EntityId;      // DDS Key
    public double Latitude;    // WGS84
    public double Longitude;   // WGS84
    public double Altitude;    // MSL
    public Vector3 Velocity;   // m/s (Geodetic frame)
}
```

### 3.2 The Transform Bridge

**Responsibility:** SimModule's `CoordinateTransformSystem` runs in `Phase.PostSimulation`.

**Logic (WITH CRITICAL EXECUTION CONSTRAINTS):**
```csharp
public class CoordinateTransformSystem : ComponentSystem
{
    private IGeographicTransform _geoTransform;

    // CRITICAL: System registration with explicit constraints
    public static SystemDefinition GetDefinition()
    {
        return new SystemDefinition
        {
            Phase = Phase.PostSimulation,  // MUST run AFTER Phase.Simulation
            UpdateOrder = 0,                // First in PostSim (before network export)
            RequiresBefore = new[] { Phase.Export },  // MUST complete before DDS publishes
            RequiresAfter = new[] { "PhysicsSystem", "MovementSystem" }  // All Tier-1 updates done
        };
    }

    public override void OnUpdate(EntityRepository repo)
    {
        repo.SetPhase(Phase.PostSimulation);
        
        // Query entities with BOTH coordinate representations
        var query = repo.Query()
            .WithComponent<PositionCartesian>()   // Source (Tier 1)
            .WithComponent<PositionGeodetic>();   // Target (Tier 2)
        
        foreach (var entity in query)
        {
            // ===== RULE 1: AUTHORITY CHECK (Prevent Ghosting) =====
            // Only transform if WE own the Cartesian physics position
            // If we're receiving this entity from network, skip (ingress handles it)
            if (!repo.HasAuthority(entity, ComponentType<PositionCartesian>.Id))
                continue;  // This is a "ghost" - use network Geodetic directly
            
            // ===== RULE 2: UNIDIRECTIONAL FLOW (Tier 1 → Tier 2) =====
            // NEVER write to Cartesian here. Only read from it.
            ref readonly var cartesian = ref repo.GetComponentRO<PositionCartesian>(entity);
            ref var geodetic = ref repo.GetComponentRW<PositionGeodetic>(entity);
            
            // ===== RULE 3: TRANSFORM =====
            // Convert Local physics coords → Global network coords
            geodetic.Latitude = _geoTransform.GetLatitude(cartesian.LocalPosition);
            geodetic.Longitude = _geoTransform.GetLongitude(cartesian.LocalPosition);
            geodetic.Altitude = _geoTransform.GetAltitude(cartesian.LocalPosition);
            geodetic.Velocity = _geoTransform.TransformVelocity(cartesian.LinearVelocity);
            
            // Mark as dirty for DDS publication (happens in Phase.Export)
            repo.MarkDirty(entity, ComponentType<PositionGeodetic>.Id);
        }
    }
}
```

**Execution Guarantee Matrix:**
| Constraint | Enforcement | Consequence if Violated |
|------------|-------------|-------------------------|
| **Phase = PostSimulation** | FDP PhaseConfig validation | WrongPhaseException |
| **After Physics** | System dependency graph | Transform uses stale T-1 position → jitter |
| **Before Export** | UpdateOrder = 0 | DDS publishes stale Geodetic → desync |
| **Authority Check** | Runtime if-guard | Ghost entities get overwritten → snap/pop |
| **Unidirectional** | Code convention | Feedback loop → instability |

**When SimModule Absent:**
- No `PositionCartesian` components exist
- Entities only have `PositionGeodetic` (populated from DDS)
- No transform system runs (zero overhead)

### 3.3 Coordinate System Service

```csharp
public interface IGeographicTransform
{
    // Tangent plane origin (set per battlespace)
    void SetOrigin(double lat, double lon, double alt);
    
    // Conversions
    PositionGeodetic ToGeodetic(in PositionCartesian local);
    PositionCartesian ToCartesian(in PositionGeodetic geo);
    
    // Utilities
    Vector3 ToECEF(double lat, double lon, double alt);
    (double lat, double lon, double alt) FromECEF(Vector3 ecef);
}
```

**Implementation Note:**
Use proven library (e.g., GeographicLib, or custom optimized for WGS84).

---

## 4. Snapshot-on-Demand Implementation

### 4.1 Architectural Decision: SoD over COW

**Decision:** After analyzing the tradeoffs, we are implementing **Snapshot-on-Demand (SoD)** instead of kernel-level Copy-On-Write.

**Rationale:**
1. **Simplicity:** Keeps FDP kernel "boring" (in a good way) - no complex pointer swizzling
2. **Performance:** Physics hot path remains untouched - zero overhead
3. **Debugging:** Single source of truth - no phantom memory versions
4. **Compatibility:** Works with existing FDP allocator design

**Trade-off Accepted:**
- Snapshots cost bandwidth (memcpy) instead of being "free" (RefCount increment)
- Mitigation: Dirty chunk tracking + shadow buffer reuse

### 4.2 The Two-Tier Snapshot Strategy

| Tier | Data Type | Snapshot Method | Cost |
|------|-----------|-----------------|------|
| **Tier 1 (Unmanaged)** | Structs (Position, Health, Ammo) | **Shadow Buffer + Dirty Tracking** | memcpy only changed chunks |
| **Tier 2 (Managed)** | Records (Callsign, Orders) | **Reference Array Copy** | Array.Copy references |

### 4.3 Tier 1: Shadow Buffer with Dirty Tracking

#### The Problem
Naively copying all Tier 1 data (100K entities × 256 bytes) = 25MB per snapshot = bandwidth explosion.

#### The Solution: Persistent Shadow Buffers

Instead of allocating new buffers, maintain **persistent shadow buffers** per background module and only copy **changed chunks**.

```csharp
public class SnapshotManager
{
    // One shadow buffer per active background consumer
    private Dictionary<Guid, ShadowBuffer> _shadowBuffers = new();
    
    public class ShadowBuffer
    {
        public byte* Data;              // Pinned memory (matches live layout)
        public ulong[] ChunkVersions;   // Last captured version per chunk
        public int ChunkCount;
    }
    
    public ISimWorldSnapshot CreateSnapshot(Guid consumerId, ComponentMask mask)
    {
        // Get or create shadow buffer
        if (!_shadowBuffers.TryGetValue(consumerId, out var shadow))
        {
            shadow = AllocateShadowBuffer(mask);
            _shadowBuffers[consumerId] = shadow;
        }
        
        // SYNC POINT: Must be called when main thread is paused
        UpdateShadowBuffer(shadow, mask);
        
        return new Tier1Snapshot(shadow, _fdp);
    }
    
    private void UpdateShadowBuffer(ShadowBuffer shadow, ComponentMask mask)
    {
        for (int chunkIdx = 0; chunkIdx < shadow.ChunkCount; chunkIdx++)
        {
            var liveVersion = _fdp.GetChunkVersion(chunkIdx);
            
            if (liveVersion > shadow.ChunkVersions[chunkIdx])
            {
                // DIRTY: Chunk changed since last snapshot
                UnsafeUtility.MemCpy(
                    shadow.Data + (chunkIdx * 64 * 1024),  // Dest
                    _fdp.GetChunkPtr(chunkIdx),            // Source
                    64 * 1024                               // Size
                );
                
                shadow.ChunkVersions[chunkIdx] = liveVersion;
            }
            // else: CLEAN - shadow buffer already has correct data
        }
    }
}
```

**Key Optimization:**
For **sleeping entities** (static buildings, parked vehicles), `chunkVersion` doesn't change → **zero bandwidth**.

#### Performance Analysis

| Scenario | Entities | Changed/Frame | Bandwidth | Notes |
|----------|----------|---------------|-----------|-------|
| **Static world** | 100K | 0% | **0 MB/s** | Perfect case |
| **Normal gameplay** | 100K | 10% active | ~150 MB/s | 10K entities × 256B × 60Hz |
| **Heavy combat** | 100K | 30% active | ~450 MB/s | Acceptable (DDR4 bandwidth = 25GB/s) |

### 4.4 Tier 2: Reference Array Copy with Immutable Records

#### Why Tier 2 Still Needs Immutability

Even without COW, Tier 2 **must** use immutable records because we're doing **reference sharing**.

```csharp
// Tier 2 Snapshot Strategy
public class ManagedComponentTable<T> where T : class
{
    private T?[][] _chunks;  // Live data
    
    public T?[] CreateSnapshot(int chunkIndex)
    {
        var liveChunk = _chunks[chunkIndex];
        
        // SHALLOW COPY: Copy the reference array, not the objects
        var snapshot = ArrayPool<T?>.Shared.Rent(liveChunk.Length);
        Array.Copy(liveChunk, snapshot, liveChunk.Length);
        
        return snapshot;
        // Result: snapshot[5] and liveChunk[5] point to SAME object
    }
}
```

**The Immutability Contract:**
```csharp
// ❌ BAD: Mutation (breaks snapshot safety)
var identity = repo.GetComponent<IdentityDescriptor>(entity);
identity.Callsign = "BRAVO";  // Mutates shared object!

// ✅ GOOD: Replacement (safe)
var identity = repo.GetComponent<IdentityDescriptor>(entity);
var newIdentity = identity with { Callsign = "BRAVO" };
repo.SetComponent(entity, newIdentity);  // Atomic replacement
```

**Why This Works:**
- Live array now points to `newIdentity`
- Snapshot array still points to `oldIdentity`
- Both objects are **immutable** - neither can change
- GC cleans up `oldIdentity` when snapshot disposed

### 4.5 The Sync Point (Critical Constraint)

**The Rule:** Tier 1 snapshots can **only** be taken when the main thread is paused.

**Why?**
If physics is writing `Position.X` while we're doing `memcpy`, we get **torn reads** (half old, half new).

#### Host Kernel Frame Structure

```csharp
public void RunFrame()
{
    // 1. Input Phase (Main thread writes)
    ExecutePhase(Phase.Input);
    
    // 2. Simulation Phase (Main thread writes)
    ExecutePhase(Phase.Simulation);
    
    // 3. PostSimulation (Main thread writes)
    ExecutePhase(Phase.PostSimulation);
    
    // ===== SYNC POINT: MAIN THREAD PAUSES =====
    
    // 4. Snapshot Creation (No writers active!)
    var pendingBackgroundModules = GetReadyBackgroundModules();
    if (pendingBackgroundModules.Any())
    {
        var unionMask = CalculateUnionMask(pendingBackgroundModules);
        var snapshot = _snapshotManager.CreateSnapshot(unionMask);
        
        // Fire & forget - background modules run on thread pool
        foreach (var module in pendingBackgroundModules)
        {
            Task.Run(() => module.RunAsync(snapshot));
        }
    }
    
    // ===== END SYNC POINT =====
    
    // 5. Export Phase (Main thread reads for DDS publish)
    ExecutePhase(Phase.Export);
}
```

**Constraint:** The sync point must complete in **<2ms** to maintain 60Hz (16.67ms frame budget).

### 4.6 Snapshot Interface

```csharp
public interface ISimWorldSnapshot : IDisposable
{
    // Tier 1 Access (From Shadow Buffer)
    // Returns struct copy - safe because shadow buffer is stable
    T GetStruct<T>(Entity entity) where T : unmanaged;
    
    // Bulk access for performance
    ReadOnlySpan<T> GetStructSpan<T>() where T : unmanaged;
    
    // Tier 2 Access (From Reference Array)
    // Returns reference to immutable record
    T GetRecord<T>(Entity entity) where T : class;
    
    // Metadata
    ulong FrameNumber { get; }
    double SimulationTime { get; }
}

public class Tier1Snapshot : ISimWorldSnapshot
{
    private ShadowBuffer _shadow;
    
    public T GetStruct<T>(Entity entity) where T : unmanaged
    {
        // Direct copy from shadow buffer (safe - no writer active)
        var offset = CalculateOffset<T>(entity);
        return Unsafe.Read<T>(_shadow.Data + offset);
    }
    
    public void Dispose()
    {
        // Shadow buffer is persistent - just mark snapshot as released
        // (Shadow buffer gets reused next time)
    }
}
```

---

## 4.5 Structural Changes in Async Mode (Gap 2 Resolution)

### 4.5.1 The Problem

Background modules operate on **read-only snapshots**. They cannot directly create/destroy entities.

**Example:**
- AI module decides to fire weapon
- Needs to spawn bullet entity
- Snapshot is immutable → Cannot call `repo.CreateEntity()`

### 4.5.2 Solution: Thread-Safe Command Buffer

**Design:**
Background modules **record** structural changes to a **thread-safe CommandBuffer**, which is **played back** on the main thread during the next frame's structural sync point.

```csharp
public class EntityCommandBuffer
{
    // Thread-safe queue for deferred operations
    private ConcurrentQueue<ICommand> _commands = new();
    
    // Atomic counter for unique IDs
    private long _nextTempId = -1;  // Negative IDs = temporary
    
    // API: Create entity (deferred)
    public Entity CreateEntity(string debugName = "")
    {
        // Allocate temporary ID (resolved at playback)
        var tempId = Interlocked.Decrement(ref _nextTempId);
        var tempEntity = new Entity(tempId);
        
        _commands.Enqueue(new CreateEntityCommand
        {
            TempEntity = tempEntity,
            DebugName = debugName
        });
        
        return tempEntity;  // Return temp handle for subsequent commands
    }
    
    // API: Set component (deferred)
    public void SetComponent<T>(Entity entity, T component) where T : unmanaged
    {
        _commands.Enqueue(new SetComponentCommand<T>
        {
            Entity = entity,
            Value = component
        });
    }
    
    // API: Destroy entity (deferred)
    public void DestroyEntity(Entity entity)
    {
        _commands.Enqueue(new DestroyEntityCommand { Entity = entity });
    }
    
    // Playback on main thread (Phase.Structural sync point)
    public void Playback(EntityRepository repo)
    {
        // ID remapping: TempID → RealID
        var idMap = new Dictionary<long, long>();
        
        while (_commands.TryDequeue(out var cmd))
        {
            cmd.Execute(repo, idMap);
        }
    }
}
```

### 4.5.3 Command Execution

```csharp
internal interface ICommand
{
    void Execute(EntityRepository repo, Dictionary<long, long> idMap);
}

internal class CreateEntityCommand : ICommand
{
    public Entity TempEntity;
    public string DebugName;
    
    public void Execute(EntityRepository repo, Dictionary<long, long> idMap)
    {
        // Allocate real ID from repository
        var realEntity = repo.CreateEntity();
        
        // Map temporary → real
        idMap[TempEntity.Id] = realEntity.Id;
        
        Debug.Log($"Resolved {TempEntity} → {realEntity} ({DebugName})");
    }
}

internal class SetComponentCommand<T> : ICommand where T : unmanaged
{
    public Entity Entity;
    public T Value;
    
    public void Execute(EntityRepository repo, Dictionary<long, long> idMap)
    {
        // Resolve entity ID if it was created in same buffer
        var resolvedId = idMap.GetValueOrDefault(Entity.Id, Entity.Id);
        var resolvedEntity = new Entity(resolvedId);
        
        repo.SetComponent(resolvedEntity, Value);
    }
}
```

### 4.5.4 Background Module Usage

```csharp
public class AIModule : IModule
{
    public JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer cmd)
    {
        return Task.Run(() =>
        {
            // Analyze snapshot (safe, read-only)
            var enemies = snapshot.Query().WithComponent<EnemyTag>();
            
            foreach (var target in enemies)
            {
                if (ShouldShootAt(target))
                {
                    // Create bullet entity (deferred)
                    var bullet = cmd.CreateEntity("Bullet");
                    cmd.SetComponent(bullet, new PositionCartesian { ... });
                    cmd.SetComponent(bullet, new Projectile { Target = target });
                    
                    // These operations are queued, not executed immediately
                }
            }
        });
    }
}
```

### 4.5.5 Host Kernel Integration

```csharp
public void RunFrame()
{
    _fdp.Tick();
    
    // 1. Execute critical systems (Phase.Simulation)
    ExecuteCriticalSystems();
    
    // 2. Create snapshot for async modules
    using var snapshot = _fdp.CreateSnapshot(500);
    
    // 3. Trigger background modules (these populate command buffers)
    var backgroundHandles = TriggerBackgroundModules(snapshot);
    
    // 4. Collect completed command buffers from PREVIOUS frame
    var completedBuffers = CollectCompletedBuffers();
    
    // 5. Structural sync point: Playback all commands
    _fdp.SetPhase(Phase.Structural);
    foreach (var buffer in completedBuffers)
    {
        buffer.Playback(_fdp);
    }
    
    // 6. Export phase (DDS publish)
    ExecuteExportSystems();
}
```

### 4.5.6 Performance Characteristics

| Operation | Cost | Notes |
|-----------|------|-------|
| **CreateEntity (queuing)** | O(1) | `ConcurrentQueue.Enqueue` ~15ns |
| **SetComponent (queuing)** | O(1) | Value copied to queue |
| **Playback** | O(N commands) | Runs once per frame on main thread |
| **ID Resolution** | O(N creates) | HashMap lookup per deferred component set |

---

## 4.6 COW Fork Trigger Implementation (Gap 3 Resolution)

### 4.6.1 The Accessor Problem

FDP's current component accessors return raw references:
```csharp
public ref T GetComponent<T>(Entity entity) where T : unmanaged
{
    return ref _array[entity.Index];  // Direct pointer - no fork check!
}
```

**This bypasses COW entirely.**

### 4.6.2 Solution: Write Barrier Integration

We must wrap component setters with fork logic:

```csharp
// TIER 1 (Unmanaged - NO FORK, direct write)
public ref T GetComponentRW<T>(Entity entity) where T : unmanaged
{
    // Tier 1 is NEVER copied - always exclusive to physics
    return ref _unmanagedTable.GetRef(entity.Index);
}

// TIER 2 (Managed - WITH FORK)
public ref T GetComponentRW<T>(Entity entity) where T : class
{
    var page = _managedTable.GetPageForEntity(entity);
    
    // FORK TRIGGER
    if (page.RefCount > 1)
    {
        page = ForkPage(page);  // COW magic happens here
        _managedTable.UpdateMapping(entity, page);
    }
    
    return ref page.Data[entity.LocalIndex];
}

// Alternative design: Use SetComponent instead of returning ref
public void SetComponent<T>(Entity entity, T value) where T : class
{
    var page = _managedTable.GetPageForEntity(entity);
    
    // FORK TRIGGER (same as above)
    if (page.RefCount > 1)
    {
        page = ForkPage(page);
        _managedTable.UpdateMapping(entity, page);
    }
    
    page.Data[entity.LocalIndex] = value;
    MarkDirty(entity, ComponentType<T>.Id);
}
```

### 4.6.3 Tier Separation Justification

| Tier | Fork Behavior | Rationale |
|------|---------------|-----------|
| **Tier 1 (Unmanaged)** | **NO FORK** | Physics is always exclusive. Snapshots pin old pages,main thread writes to new pages via normal chunk allocation |
| **Tier 2 (Managed)** | **WITH FORK** | Complex objects cannot be "chunked." COW at page level |

**Critical Rule:**
> Tier 1 writes add **conditional branch** cost (0.5ns) which is FORBIDDEN.
> Tier 2 writes add fork logic (~50μs when triggered) which is ACCEPTABLE.

### 4.6.4 The Fork Operation (Detailed)

```csharp
private ManagedPage<T> ForkPage<T>(ManagedPage<T> source) where T : class
{
    // 1. Allocate new page from pool
    var newPage = new ManagedPage<T>
    {
        Data = new T[source.Capacity],
        RefCount = 1,  // Owned exclusively by main thread
        Generation = _globalVersion
    };
    
    // 2. SHALLOW COPY references (not objects themselves!)
    Array.Copy(source.Data, newPage.Data, source.AllocatedCount);
    
    // 3. Release old page (decrement ref)
    int remaining = Interlocked.Decrement(ref source.RefCount);
    if (remaining == 0)
    {
        // Last reference gone - return to pool
        PagePool.Return(source);
    }
    
    return newPage;
    
    // Cost: ~50μs for 64KB page containing 1000 references
}
```

### 4.6.5 Example: Object NOT Cloned

```csharp
// Entity has IdentityDescriptor with List<Waypoint> (100KB)
var entity = ...;
var identity = _fdp.GetComponent<IdentityDescriptor>(entity);
identity.Callsign = "BRAVO-2";  // TRIGGERS FORK

// FORK HAPPENS:
// - Old page still has reference to SAME List<Waypoint> instance
// - New page also has reference to SAME List<Waypoint> instance
// - Only the Callsign string is new (immutable in C#, so copy-on-write anyway)

// Result: 100KB List NOT duplicated! ✅
```

**Only if you replace the entire list:**
```csharp
identity.Waypoints = new List<Waypoint> { ... };  // NOW we allocate new list
```

### 4.6.6 Performance Validation Test

```csharp
[Test]
public void COW_DoesNotCloneUnchangedObjects()
{
    var entity = _fdp.CreateEntity();
    var largeList = new List<int>(10000);  // Large object
    _fdp.SetComponent(entity, new TestComponent { Data = largeList });
    
    // Create snapshot (RefCount = 2)
    using var snapshot = _fdp.CreateSnapshot(100);
    
    // Modify component (should trigger fork)
    var comp = _fdp.GetComponent<TestComponent>(entity);
    comp.SomeField = 42;  // Changed field, but Data reference unchanged
    _fdp.SetComponent(entity, comp);
    
    // Verify: Both old and new page point to SAME list instance
    var snapData = snapshot.GetComponent<TestComponent>(entity).Data;
    var liveData = _fdp.GetComponent<TestComponent>(entity).Data;
    
    Assert.AreSame(snapData, liveData);  // ✅ Same reference
}

### 5.1 The IModule Contract (Revised)

```csharp
public interface IModule
{
    // Identity
    ModuleDefinition GetDefinition();
    
    // Lifecycle
    void Initialize(IModuleContext context);
    void Start();
    void Stop();
    
    // NEW: System Registration
    void RegisterSystems(ISystemRegistry registry);
    
    // Execution (for modules that need custom async logic)
    JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer output);
    
    // Diagnostics
    object? GetIntrospectionRoot();
    void DrawDiagnostics();
}
```

### 5.2 System Registry API

```csharp
public interface ISystemRegistry
{
    // Register a FDP ComponentSystem
    void RegisterSystem(ComponentSystem system, Phase phase, int order);
    
    // Register a custom update delegate
    void RegisterUpdate(Action<EntityRepository> update, Phase phase, int order);
}
```

### 5.3 Module Implementation Patterns

#### Pattern A: Pure System Provider (SimModule)
```csharp
public class SimulationModule : IModule
{
    private PhysicsSystem _physics;
    private CollisionSystem _collision;
    private MovementSystem _movement;
    private CoordinateTransformSystem _transform;
    
    public void Initialize(IModuleContext ctx)
    {
        var geoService = ctx.Services.Get<IGeographicTransform>();
        
        _physics = new PhysicsSystem();
        _collision = new CollisionSystem();
        _movement = new MovementSystem();
        _transform = new CoordinateTransformSystem(geoService);
    }
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Phase.Simulation: Physics logic
        registry.RegisterSystem(_physics, Phase.Simulation, order: 0);
        registry.RegisterSystem(_collision, Phase.Simulation, order: 100);
        registry.RegisterSystem(_movement, Phase.Simulation, order: 200);
        
        // Phase.PostSimulation: Coordinate sync
        registry.RegisterSystem(_transform, Phase.PostSimulation, order: 0);
    }
    
    public JobHandle Tick(...) => JobHandle.Completed; // Not used
}
```

#### Pattern B: Hybrid with Async Background (AIModule)
```csharp
public class AIModule : IModule
{
    private AIReasoningSystem _reasoning;
    private Task<AIDecisions>? _backgroundTask;
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Lightweight critical path system
        registry.RegisterSystem(_reasoning, Phase.Simulation, order: 50);
    }
    
    public JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer cmd)
    {
        // Launch heavy pathfinding on thread pool
        if (_backgroundTask == null || _backgroundTask.IsCompleted)
        {
            _backgroundTask = Task.Run(() => 
                HeavyPathfinding(snapshot) // Uses COW snapshot safely
            );
        }
        
        // Apply completed results
        if (_backgroundTask.IsCompleted)
        {
            var decisions = _backgroundTask.Result;
            ApplyDecisions(decisions, cmd);
        }
        
        return JobHandle.Completed;
    }
}
```

#### Pattern C: Pure Async (AnalyticsModule)
```csharp
public class AnalyticsModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        // No FDP systems - pure observer
    }
    
    public JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer cmd)
    {
        // Capture snapshot for background analysis
        var task = Task.Run(() => AnalyzeSimulationState(snapshot));
        return JobHandle.FromTask(task);
    }
}
```

### 5.4 Host Kernel Execution Flow

```csharp
public class ModuleHostKernel
{
    private EntityRepository _fdp;
    private List<IModule> _modules;
    private Dictionary<Phase, List<ComponentSystem>> _systemsByPhase;
    
    public void RunFrame()
    {
        var time = _timeKeeper.Advance();
        
        // 1. Tick global version
        _fdp.Tick();
        
        // 2. Execute FDP systems by phase
        foreach (var phase in _phaseOrder) // [Input, Simulation, PostSim, Export]
        {
            _fdp.SetPhase(phase);
            
            foreach (var system in _systemsByPhase[phase])
            {
                system.OnUpdate(_fdp);
            }
        }
        
        // 3. Create snapshot for async modules
        using var snapshot = _fdp.CreateSnapshot(maxAgeMs: 500);
        
        // 4. Execute async module ticks (background tier)
        var backgroundTasks = new List<JobHandle>();
        foreach (var module in _backgroundModules)
        {
            var cmd = new EntityCommandBuffer();
            var handle = module.Tick(time, snapshot, cmd);
            backgroundTasks.Add(handle);
        }
        
        // 5. Wait for critical tasks (if any)
        // ... (existing scheduler logic)
    }
}
```

---

## 6. SST Descriptor Mapping

### 6.1 FDP Component → DDS Topic Mapping

| FDP Component (Tier 2) | DDS Topic | Update Frequency |
|------------------------|-----------|------------------|
| `PositionGeodetic` | `SST.Position` | 30-60 Hz |
| `IdentityDescriptor` | `SST.Identity` | On change |
| `StatusDescriptor` | `SST.Status` | On change |
| `SensorDescriptor` | `SST.Sensor` | 10 Hz |
| `DamageDescriptor` | `SST.Damage` | On event |

### 6.2 DDS Gateway Implementation (SSTModule)

```csharp
public class NetworkSyncSystem : ComponentSystem
{
    private DDSPublisher _publisher;
    
    public override void OnUpdate(EntityRepository repo)
    {
        repo.SetPhase(Phase.Export);
        
        // Query entities with public descriptors
        var query = repo.Query()
            .WithComponent<PositionGeodetic>();
        
        foreach (var entity in query)
        {
            // Authority check: Only publish if we own this descriptor
            if (!repo.HasAuthority(entity, ComponentType<PositionGeodetic>.Id))
                continue;
            
            // Dirty check: Only publish if changed
            if (!repo.IsDirty(entity, ComponentType<PositionGeodetic>.Id))
                continue;
            
            // Read & publish
            ref readonly var pos = ref repo.GetComponentRO<PositionGeodetic>(entity);
            _publisher.Write(pos); // Direct to DDS DataWriter
        }
    }
}
```

### 6.3 DDS Ingress (Network → FDP)

```csharp
public class NetworkIngestSystem : ComponentSystem
{
    private DDSSubscriber _subscriber;
    
    public override void OnUpdate(EntityRepository repo)
    {
        repo.SetPhase(Phase.NetworkIngest);
        
        // Read incoming DDS samples
        var samples = _subscriber.Take<PositionGeodetic>();
        
        foreach (var sample in samples)
        {
            var entity = new Entity(sample.EntityId);
            
            // Authority guard: Only accept if we DON'T own this
            if (repo.HasAuthority(entity, ComponentType<PositionGeodetic>.Id))
                continue; // Loopback or conflict
            
            // Update FDP
            if (!repo.IsAlive(entity))
                repo.CreateEntity(entity); // Remote entity appearing
            
            repo.SetComponent(entity, sample);
        }
    }
}
```

---

## 7. Entity Lifecycle Manager (ELM) Integration

### 7.1 FDP Primitives for Dark Construction

FDP **already provides** the machinery:

```csharp
// In EntityRepository.cs (existing)
public Entity CreateStagedEntity(
    ulong requiredModulesMask, 
    ulong authorityMask)
{
    var entity = AllocateEntityId();
    
    // Mark as Constructing (not Active)
    _entityHeaders[entity.Index].Lifecycle = EntityLifecycle.Constructing;
    _entityHeaders[entity.Index].RequiredMask = requiredModulesMask;
    _entityHeaders[entity.Index].AckedMask = 0;
    
    return entity;
}

public void AcknowledgeStage(Entity entity, int moduleId)
{
    _entityHeaders[entity.Index].AckedMask |= (1UL << moduleId);
    
    // Check if all required modules ACKed
    if (IsStageComplete(entity))
    {
        ActivateStagedEntity(entity);
    }
}

public void ActivateStagedEntity(Entity entity)
{
    _entityHeaders[entity.Index].Lifecycle = EntityLifecycle.Active;
    // Now visible to queries
}
```

### 7.2 ELM DDS Protocol Implementation

**ELM runs in Drill Orchestrator backend.**

```csharp
public class EntityLifecycleModule : IModule
{
    private EntityRepository _fdp;
    private DDSPublisher _constructionOrderPublisher;
    private DDSSubscriber _ackSubscriber;
    
    // Routing table: EntityType → Required Module IDs
    private Dictionary<DISEntityType, List<int>> _routingTable;
    
    public void SpawnEntity(DISEntityType type, ...)
    {
        // 1. Lookup required modules
        var requiredModules = _routingTable[type];
        ulong requiredMask = BuildMask(requiredModules);
        
        // 2. Reserve entity in FDP
        var entity = _fdp.CreateStagedEntity(
            requiredModulesMask: requiredMask,
            authorityMask: 0 // Will be set by contributors
        );
        
        // 3. Publish Construction Order
        _constructionOrderPublisher.Publish(new ConstructionOrder
        {
            EntityId = entity.Id,
            EntityType = type,
            InitialData = new { ... }
        });
        
        // 4. Wait for ACKs (handled by callback)
    }
    
    private void OnAckReceived(ConstructionAck ack)
    {
        var entity = new Entity(ack.EntityId);
        _fdp.AcknowledgeStage(entity, ack.ModuleId);
        
        // FDP automatically activates when all ACKs received
    }
}
```

### 7.3 Module Contribution

**Physics Module receives Construction Order:**
```csharp
private void OnConstructionOrder(ConstructionOrder order)
{
    var entity = new Entity(order.EntityId);
    
    // Populate physics components
    _fdp.SetComponent(entity, new PositionCartesian { ... });
    _fdp.SetComponent(entity, new PhysicsState { ... });
    
    // Send ACK
    _ackPublisher.Publish(new ConstructionAck
    {
        EntityId = order.EntityId,
        ModuleId = MY_MODULE_ID,
        Status = AckStatus.Ready
    });
}
```

**Result:**
- Entity exists in FDP but `Lifecycle = Constructing`
- Not visible to normal queries
- Once all ACKs received → `Lifecycle = Active`
- Atomic "reveal" ✅

---

## 8. Phase Execution Model

### 8.1 Phase Definitions

| Phase | Permission | Purpose | Systems |
|-------|------------|---------|---------|
| **NetworkIngest** | `UnownedOnly` | Accept DDS samples for non-owned entities | DDS Ingress |
| **Input** | `OwnedOnly` | Read hardware, write player input | Input Handlers |
| **Simulation** | `ReadWriteAll` | Physics, AI, game logic | Physics, Collision, AI |
| **PostSimulation** | `ReadWriteAll` | Constraints, transforms, cleanup | Coord Transform, Damage |
| **Export** | `ReadOnly` | DDS publish, diagnostics | DDS Gateway, Recorder |

### 8.2 Phase Permissions Enforcement

**FDP Config:**
```csharp
var phaseConfig = new PhaseConfig
{
    Permissions = new Dictionary<string, PhasePermission>
    {
        { "NetworkIngest", PhasePermission.UnownedOnly },
        { "Input", PhasePermission.OwnedOnly },
        { "Simulation", PhasePermission.ReadWriteAll },
        { "PostSimulation", PhasePermission.ReadWriteAll },
        { "Export", PhasePermission.ReadOnly }
    },
    
    ValidTransitions = new Dictionary<string, string[]>
    {
        { "NetworkIngest", new[] { "Input" } },
        { "Input", new[] { "Simulation" } },
        { "Simulation", new[] { "PostSimulation" } },
        { "PostSimulation", new[] { "Export" } },
        { "Export", new[] { "NetworkIngest" } }
    }
};

_fdp = new EntityRepository(phaseConfig);
```

**Runtime Safety:**
```csharp
// This throws WrongPhaseException:
_fdp.SetPhase(Phase.Export);
_fdp.SetComponent(entity, newValue); // ❌ Export is ReadOnly!
```

---

## 9. Pros/Cons & Trade-Offs

### 9.1 Architecture Advantages

| Feature | Benefit |
|---------|---------|
| **Host-Centric Ownership** | Any node config (full sim, passive monitor, headless compute) |
| **Single ECS** | No state duplication, unified recording/replay |
| **True COW** | Zero object cloning for unchanged data, safe async access |
| **Dual Coordinates** | Physics uses Cartesian, network uses Geodetic (correct domains) |
| **System Provider Pattern** | Modules are pure plugins, FDP is universal engine |
| **FDP Reuse** | Leverage existing recording, phases, events, determinism |

### 9.2 Implementation Costs

| Change | Complexity | Risk |
|--------|------------|------|
| **Add ManagedPage COW** | Medium | Low (isolated to FDP kernel) |
| **Host Kernel Integration** | Medium | Low (clear interface) |
| **Coordinate Transform Service** | Low | Low (well-understood math) |
| **DDS Gateway** | High | Medium (network edge cases) |
| **ELM Protocol** | Medium | Low (uses FDP primitives) |

### 9.3 Performance Impact

| Aspect | Expected Impact |
|--------|-----------------|
| **Memory** | +1-2% (RefCount overhead) |
| **CPU (Normal)** | Negligible (RefCount=1 fast path) |
| **CPU (COW Fork)** | ~50μs per page fork (rare) |
| **GC Pressure** | **Reduced** (avoid deep clones) |
| **Latency** | **Improved** (async modules don't block) |

---

## 10. Implementation Roadmap

### Phase 1: FDP COW Extension (Week 1)
- [ ] Implement `ManagedPage<T>` with RefCount
- [ ] Add `EntityRepository.CreateSnapshot()`
- [ ] Implement write barrier with fork logic
- [ ] Unit tests for COW behavior

### Phase 2: Host Kernel Foundation (Week 2)
- [ ] ModuleHost initializes EntityRepository
- [ ] `ISystemRegistry` implementation
- [ ] Phase-based system execution loop
- [ ] Module loading with system registration

### Phase 3: Core Modules (Week 3)
- [ ] Implement `IGeographicTransform` service
- [ ] SimModule with CoordinateTransformSystem
- [ ] SSTModule with NetworkSyncSystem
- [ ] Basic DDS Gateway (Position only)

### Phase 4: ELM Integration (Week 4)
- [ ] EntityLifecycleModule using FDP staging
- [ ] DDS ConstructionOrder/Ack topics
- [ ] Module interest registration
- [ ] End-to-end dark construction test

### Phase 5: Distributed Testing (Week 5-6)
- [ ] Multi-node configuration
- [ ] Authority handover protocol
- [ ] Physics + Monitor node test
- [ ] Performance benchmarking

---

## 10. Resilience & Safety Mechanisms

### 10.1 Watchdogs & Timeouts

#### 10.1.1 Critical Path Watchdog

**Purpose:** Detect hung modules in the synchronous frame pipeline.

```csharp
public class ModuleHostKernel
{
    private const int CRITICAL_TIMEOUT_MS = 200;  // Max frame time
    
    public void RunFrame()
    {
        using var watchdog = new FrameWatchdog(CRITICAL_TIMEOUT_MS);
        
        try
        {
            ExecuteCriticalSystems();
        }
        catch (TimeoutException ex)
        {
            // Critical module hung - emergency stop
            _logger.Fatal($"Frame timeout! Slowest module: {ex.ModuleName}");
            ExecuteEmergencyStop();
        }
    }
}

internal class FrameWatchdog : IDisposable
{
    private CancellationTokenSource _cts;
    private Task _watchdogTask;
    
    public FrameWatchdog(int timeoutMs)
    {
        _cts = new CancellationTokenSource();
        _watchdogTask = Task.Run(async () =>
        {
            await Task.Delay(timeoutMs, _cts.Token);
            if (!_cts.IsCancellationRequested)
            {
                // Timeout occurred - trigger stack dump
                DumpAllThreadStacks();
                throw new TimeoutException("Critical frame timeout");
            }
        });
    }
    
    public void Dispose()
    {
        _cts.Cancel();  // Frame completed successfully
    }
}
```

#### 10.1.2 Background Task Watchdog

**Purpose:** Detect stalled async modules without blocking main loop.

```csharp
public class BackgroundWatchdog
{
    private Dictionary<Guid, TaskMonitor> _monitors = new();
    
    public void RegisterTask(Guid moduleId, Task task, int timeoutMs)
    {
        _monitors[moduleId] = new TaskMonitor
        {
            Task = task,
            StartTime = DateTime.UtcNow,
            TimeoutMs = timeoutMs
        };
    }
    
    // Called every frame
    public void CheckTimeouts()
    {
        foreach (var (moduleId, monitor) in _monitors)
        {
            if (!monitor.Task.IsCompleted &&
                (DateTime.UtcNow - monitor.StartTime).TotalMilliseconds > monitor.TimeoutMs)
            {
                _logger.Warn($"Background module {moduleId} exceeded timeout");
                
                // Force snapshot expiry (see 10.3)
                ForceExpireSnapshot(monitor.SnapshotId);
                
                // Record failure
                RecordModuleStall(moduleId);
            }
        }
    }
}
```

### 10.2 Circuit Breaker Pattern

**Purpose:** Prevent repeatedly invoking failing modules.

```csharp
public class ModuleCircuitBreaker
{
    private int _failureCount = 0;
    private DateTime _lastFailure;
    private const int FAILURE_THRESHOLD = 3;
    private const int RESET_WINDOW_MS = 5000;
    
    public enum State { Closed, Open, HalfOpen }
    public State CurrentState { get; private set; } = State.Closed;
    
    public bool ShouldExecute()
    {
        switch (CurrentState)
        {
            case State.Closed:
                return true;  // Normal operation
            
            case State.Open:
                // Check if we should try again
                if ((DateTime.UtcNow - _lastFailure).TotalMilliseconds > RESET_WINDOW_MS)
                {
                    CurrentState = State.HalfOpen;
                    return true;  // One attempt
                }
                return false;  // Stay broken
            
            case State.HalfOpen:
                return false;  // Waiting for test result
            
            default:
                return false;
        }
    }
    
    public void RecordSuccess()
    {
        _failureCount = 0;
        CurrentState = State.Closed;
    }
    
    public void RecordFailure(Exception ex)
    {
        _failureCount++;
        _lastFailure = DateTime.UtcNow;
        
        if (_failureCount >= FAILURE_THRESHOLD)
        {
            CurrentState = State.Open;
            _logger.Error($"Circuit breaker OPEN after {_failureCount} failures");
        }
    }
}
```

#### 10.2.1 Integration with Module Execution

```csharp
public JobHandle ExecuteModule(IModule module, ...)
{
    var breaker = _circuitBreakers[module.GetDefinition().Id];
    
    if (!breaker.ShouldExecute())
    {
        _logger.Warn($"Skipping module {module.Id} - circuit breaker OPEN");
        return JobHandle.Completed;
    }
    
    try
    {
        var handle = module.Tick(...);
        handle.OnComplete(() => breaker.RecordSuccess());
        return handle;
    }
    catch (Exception ex)
    {
        breaker.RecordFailure(ex);
        throw;
    }
}
```

### 10.3 Snapshot Lease Expiry (Force Release)

**Purpose:** Prevent memory exhaustion from hung background modules.

```csharp
public class SnapshotLeaseManager
{
    private Dictionary<Guid, SnapshotLease> _activeLeases = new();
    private const int HARD_EXPIRY_MS = 2000;  // Absolute maximum
    
    public ISimWorldSnapshot CreateLease(int requestedMaxAgeMs)
    {
        var snapshot = _fdp.CreateSnapshot(requestedMaxAgeMs);
        var lease = new SnapshotLease(snapshot, requestedMaxAgeMs);
        
        _activeLeases[lease.Id] = lease;
        return lease;
    }
    
    // Called every frame
    public void EnforceLeases()
    {
        var now = DateTime.UtcNow;
        var expiredLeases = new List<Guid>();
        
        foreach (var (id, lease) in _activeLeases)
        {
            var age = (now - lease.CreatedAt).TotalMilliseconds;
            
            if (age > HARD_EXPIRY_MS)
            {
                _logger.Warn($"Force-expiring snapshot {id} (age: {age}ms)");
                
                // Invalidate snapshot (future accesses throw)
                lease.Invalidate();
                
                // Force disposal on main thread
                lease.ForceDispose();
                
                expiredLeases.Add(id);
            }
        }
        
        foreach (var id in expiredLeases)
        {
            _activeLeases.Remove(id);
        }
    }
}

public class SnapshotLease : ISimWorldSnapshot
{
    private FdpSnapshot _snapshot;
    private volatile bool _isInvalidated = false;
    
    public void Invalidate()
    {
        _isInvalidated = true;
    }
    
    // Any query method
    public EntityQuery Query()
    {
        if (_isInvalidated)
            throw new SnapshotExpiredException($"Snapshot {Id} expired (exceeded MaxAge)");
        
        return _snapshot.Query();
    }
    
    public void ForceDispose()
    {
        // Called from main thread to reclaim memory
        _snapshot?.Dispose();
        _snapshot = null;
    }
}
```

#### 10.3.1 Background Module Handling

**Graceful degradation:**
```csharp
public class AIModule : IModule
{
    public JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer cmd)
    {
        return Task.Run(() =>
        {
            try
            {
                // Long-running pathfinding
                var path = ComputePath(snapshot, ...);  // May take >2s
                
                cmd.SetComponent(entity, new Path { Waypoints = path });
            }
            catch (SnapshotExpiredException)
            {
                // Snapshot was force-expired - abort gracefully
                _logger.Debug("Pathfinding aborted - snapshot expired");
                // Do NOT write to command buffer
            }
        });
    }
}
```

### 10.4 Module Definition Schema (With Resilience Metadata)

```csharp
public struct ModuleDefinition
{
    // ... existing fields ...
    
    // Resilience configuration
    public int MaxSnapshotAgeMs;      // Default: 500ms
    public int CriticalTimeoutMs;     // Default: 200ms per module
    public int CircuitBreakerThreshold; // Default: 3 failures
    public bool EnableCircuitBreaker;  // Default: true for background, false for critical
}
```

---

## 11. Open Questions & Future Work

### 11.1 Decisions Needed
- [ ] **Geographic Origin Management**: Per-drill? Per-battlespace? Dynamic update?
- [ ] **Authority Handover**: 2PC or optimistic? Timeout handling?
- [ ] **DDS QoS Profiles**: Specific settings for each descriptor type?
- [ ] **Snapshot Expiry**: Hard kill or graceful degradation when `MaxSnapshotAge` exceeded?

### 11.2 Future Enhancements
- [ ] **Spatial Partitioning**: FDP scenes/regions for large worlds
- [ ] **Hot Module Reload**: Unload/reload modules without stopping drill
- [ ] **Multi-Tenant ECS**: Story layers for Continuous Mode
- [ ] **Delta Compression**: Optimize DDS bandwidth for Position updates

---

## 12. Conclusion

This architecture provides a **unified, flexible, and performant** foundation for ModuleHost by:

1. **Leveraging FDP's strengths** (determinism, recording, phases) instead of reinventing
2. **Maintaining flexibility** through host-centric ownership (physics-optional nodes)
3. **Ensuring safety** via true COW with minimal overhead
4. **Simplifying integration** with single ECS and system provider pattern

**Next Steps:**
1. Approve this specification
2. Write MOD-001 (Module Framework details)
3. Write DDS-GW-001 (Gateway protocol)
4. Write GEO-001 (Geographic transform)
5. Begin Phase 1 implementation

**Status:** Ready for review and approval.
