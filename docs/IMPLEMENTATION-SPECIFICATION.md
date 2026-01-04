# Module Host - Implementation Specification

**Version:** 2.0  
**Date:** January 4, 2026  
**Status:** UPDATED - Hybrid Architecture  

---

## Document Purpose

This is the **MASTER SPECIFICATION** for implementing the Module Host system. It contains:

1. ✅ All architectural decisions and rationale
2. ✅ Complete interface definitions
3. ✅ Implementation requirements
4. ✅ Verification steps and test criteria
5. ✅ Implementation checklist with progress tracking

**This document is the single source of truth for implementation.**

**⚠️ ARCHITECTURAL EVOLUTION:** This spec has been updated to use a **Hybrid GDB+SoD** strategy instead of pure SoD.  
**See:** [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Architecture Overview](#architecture-overview)
3. [Core Design Decisions](#core-design-decisions)
4. [Interface Specifications](#interface-specifications)
5. [FDP Kernel Requirements](#fdp-kernel-requirements)
6. [ModuleHost Components](#modulehost-components)
7. [Implementation Phases](#implementation-phases)
8. [Verification & Testing](#verification--testing)
9. [Success Criteria](#success-criteria)
10. [Reference Documents](#reference-documents)

---

## Executive Summary

### What We're Building

A **modular, high-performance simulation host** that enables:
- **60Hz physics simulation** (synchronous main thread)
- **Background modules** (AI, analytics, UI) running asynchronously on replicas/snapshots
- **Event-driven reactive scheduling** (modules wake on data changes)
- **Zero data loss** with dynamic buffer expansion
- **Network synchronization** via DDS/SST
- **Flexible buffering strategies** (GDB for fast, SoD for slow)

### Key Innovation: Hybrid State Architecture

Instead of pure Snapshot-on-Demand (SoD), we use a **Hybrid** approach:

**Global Double Buffering (GDB)** for high-frequency modules:
- Full replica (`EntityRepository`) synced every frame
- Used by: Flight Recorder (100% data), Network (60Hz)
- **Benefit:** Simpler, zero allocation, stable memory

**Snapshot-on-Demand (SoD)** for low-frequency modules:
- Filtered snapshots (only requested components)
- Used by: AI (10Hz), Analytics (5Hz)
- **Benefit:** Bandwidth efficient, decoupled timing

**Both strategies share the same core:**
- `EntityRepository.SyncFrom(source, mask)` API
- Dirty chunk tracking (copy only changes)
- Event accumulation (history for slow modules)
- Tier 2 immutability (records + immutable collections)

**Decision Documented In:** [reference-archive/FDP-GDB-SoD-unified.md](reference-archive/FDP-GDB-SoD-unified.md)

### Architecture Model

**Host-Centric Design:**
- FDP = Passive data kernel (mechanism)
- ModuleHost = Active orchestrator (policy)
- Modules = Logic providers (agnostic to strategy)

**3-World Topology:**
```
World A (Live)  → World B (Fast Replica - GDB) → Recorder, Network
                → World C (Slow Replica - SoD/GDB) → AI, Analytics
```

---

## Architecture Overview

### System Layers

```
┌─────────────────────────────────────────┐
│  Layer 8: Resilience (Watchdogs)        │
├─────────────────────────────────────────┤
│  Layer 7: ELM (Entity Lifecycle)        │
├─────────────────────────────────────────┤
│  Layer 6: DDS Gateway (Network Sync)    │
├─────────────────────────────────────────┤
│  Layer 5: Coordinate Services           │
├─────────────────────────────────────────┤
│  Layer 4: Command Buffer (Async)        │
├─────────────────────────────────────────┤
│  Layer 3: Host Kernel (Orchestrator)    │
├─────────────────────────────────────────┤
│  Layer 2: Module Framework              │
├─────────────────────────────────────────┤
│  Layer 1: Snapshot Core (SoD)           │
├─────────────────────────────────────────┤
│  Layer 0: FDP EntityRepository          │
└─────────────────────────────────────────┘
```

**Detailed Design:** [detailed-design-overview.md](detailed-design-overview.md)

### Frame Execution Flow

```
START FRAME N
│
├─ Phase: NetworkIngest (DDS → FDP)
├─ Phase: Input
├─ Phase: Simulation (Physics - 60Hz)
├─ Phase: PostSimulation (Coordinate Transform)
│
├─ ⏸️  SYNC POINT (Create Snapshots) ⏸️
│  ├─ UpdateShadowBuffers() [memcpy dirty chunks]
│  ├─ CreateEventHistory(3 seconds with filtering)
│  ├─ CHECK: Event-driven triggers
│  └─ TriggerBackgroundModules(filtered snapshot)
│
├─ Phase: Structural (Playback CommandBuffers)
├─ Phase: Export (FDP → DDS)
│
END FRAME N
```

**Visual Reference:** [design-visual-reference.md](design-visual-reference.md)

---

## Core Design Decisions

### 1. Hybrid State Architecture (GDB + SoD)

**Decision:** Use strategy pattern - GDB for high-frequency modules, SoD for low-frequency modules

**Rationale:**
- ✅ **GDB Benefits:** Simpler for dense data (Recorder needs 100%), zero allocation, stable memory
- ✅ **SoD Benefits:** Bandwidth efficient for sparse data (AI needs <50%), decoupled timing
- ✅ **Flexibility:** Can assign strategy per-module based on actual needs
- ✅ **Unified Core:** Both strategies use same `SyncFrom()` API, dirty tracking, event accumulation

**3-World Topology:**
```
World A (Live)  → World B (Fast Replica - GDB) → Recorder, Network (60Hz, 100% data)
                → World C (Slow Replica - SoD) → AI, Analytics (10Hz, filtered data)
```

**Strategy Assignment:**
| Module Type | Strategy | Reason |
|-------------|----------|--------|
| **Recorder** | GDB (World B) | Needs 100% data anyway → full copy simpler |
| **Network** | GDB (World B) | High frequency (60Hz) → stable replica better |
| **AI** | SoD (World C) | Low frequency (10Hz), needs <50% → filtered efficient |
| **Analytics** | SoD (World C) | Very low frequency (5Hz), minimal data |

**Performance Targets:**
- GDB sync: <2ms for 100K entities (30% dirty, 100% data)
- SoD sync: <500μs for 100K entities (30% dirty, 50% filtered)

**Document:** [reference-archive/FDP-GDB-SoD-unified.md](reference-archive/FDP-GDB-SoD-unified.md)

**Key APIs:**
```csharp
// Core synchronization (used by both GDB and SoD)
EntityRepository.SyncFrom(source, mask);  // mask=null for GDB, filtered for SoD

// Event accumulation (for slow modules)
EventAccumulator.CaptureFrame(liveBus, frameIndex);
EventAccumulator.FlushToReplica(replicaBus, lastSeenTick);

// Strategy pattern
ISnapshotProvider.AcquireView(mask, lastSeenTick);
ISnapshotProvider.ReleaseView(view);
```

**Verification:**
```
✓ GDB sync completes in <2ms (100% data, 30% dirty)
✓ SoD sync completes in <500μs (50% filtered data)
✓ World B syncs every frame (Recorder + Network)
✓ World C syncs on demand (AI when idle)
✓ Event accumulation provides history for slow modules
✓ Physics loop unchanged (<10ms)
✓ Zero GC allocations per frame (GDB reuses replica)
```

---

### 2. Event History with Filtering

**Decision:** Retain event buffers for 3 seconds (180 frames @ 60Hz) with per-module filtering

**Rationale:**
- Background modules can run for seconds (AI pathfinding: 2s, analytics: 5s)
- Without history, modules are "deaf" 5 out of 6 frames
- Event filtering reduces bandwidth by 99%

**Document:** [FDP-EventsInSnapshots.md](FDP-EventsInSnapshots.md)

**Verification:**
```
✓ AI module (10Hz) receives ALL explosions since last run
✓ Event history spans 180 frames minimum
✓ Pruning doesn't remove referenced events
✓ UI module gets UI events only (not physics events)
```

---

### 3. Event-Driven Scheduling

**Decision:** Modules wake on component changes OR event arrival (not just time-based)

**Rationale:**
- Reactive > polling (AI reacts instantly to explosions)
- Performance: Checking triggers = 100ns vs. waking thread = 50μs (500x faster)
- Events are "interrupts" (override timer)

**Document:** [FDP-module-scheduling-support.md](FDP-module-scheduling-support.md)

**Verification:**
```
✓ Explosion event triggers AI module immediately (<1ms)
✓ Component change (Health) wakes monitoring module within 1 frame
✓ Scheduler checks complete in <200ns per module
✓ No spurious wake-ups (modules only run when needed)
```

---

### 4. Dynamic Buffer Expansion

**Decision:** Event buffers resize automatically (no fixed limits)

**Rationale:**
- Reliability > fixed size (10K explosion events shouldn't lose data)
- Graceful degradation (resize like `List<T>`)

**Verification:**
```
✓ 1M events in single frame → no data loss
✓ Buffers resize correctly (double capacity)
✓ Pool reuse after pruning works
```

---

### 5. Generic API with JIT Branching

**Decision:** Use generic `SetComponent<T>()` instead of explicit `SetStruct`/`SetRecord`

**Rationale:**
- Consistency with FDP's existing pattern
- JIT eliminates branching at compile time (zero overhead)
- Simpler API surface

**Document:** [design-refinements-part2-api-simplification.md](design-refinements-part2-api-simplification.md)

**Verification:**
```
✓ JIT generates separate methods for struct vs class
✓ No runtime branching in hot path
✓ API consistent with EntityRepository.GetComponent<T>()
```

---

## Interface Specifications

### Layer 1: Core Abstractions

#### ISimulationView

**Purpose:** Unified read-only interface for accessing simulation state (used by all modules)

**Key Insight:** Both `EntityRepository` (GDB replica) and `SimSnapshot` (SoD) implement this interface!

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Read-only view of simulation state.
    /// Implemented by EntityRepository (GDB) and SimSnapshot (SoD).
    /// </summary>
    public interface ISimulationView
    {
        // Metadata
        uint Tick { get; }
        float Time { get; }
        
        // Component access (unified, read-only)
        ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
        T GetManagedComponentRO<T>(Entity e) where T : class;
        
        // Existence
        bool IsAlive(Entity e);
        
        // Events (accumulated history + current combined)
        ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
        
        // Query
        EntityQueryBuilder Query();
    }
}
```

**Key Changes from `ISimWorldSnapshot`:**
- ✅ Simpler metadata (Tick/Time instead of FrameNumber/FromFrame/SimulationTime)
- ✅ RO suffix on methods (explicit read-only intent)
- ✅ `ConsumeEvents()` instead of `GetEventHistory()` (simpler)
- ✅ No `IDisposable` (GDB replicas don't need disposal)
- ✅ `EntityRepository` implements natively (zero-cost abstraction for GDB)

**Implementations:**
1. `EntityRepository` (Fdp.Kernel) - Used by GDB strategy
2. `SimSnapshot` (ModuleHost.Core) - Used by SoD strategy

**Tests Required:**
```
✓ GetComponentRO<Position>() returns correct value
✓ GetManagedComponentRO<Identity>() returns correct record
✓ ConsumeEvents<Explosion>() returns accumulated history
✓ IsAlive() checks entity validity
✓ Query() returns functional query builder
✓ Works with both EntityRepository and SimSnapshot
```

---

#### ISnapshotProvider

**Purpose:** Strategy pattern for acquiring simulation views (GDB vs SoD)

```csharp
namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Strategy for acquiring/releasing simulation views.
    /// Different implementations: GDB (persistent replica), SoD (pooled snapshot).
    /// </summary>
    public interface ISnapshotProvider : IDisposable
    {
        /// <summary>
        /// Acquires a view of the simulation state.
        /// </summary>
        /// <param name="mask">Component filter (null = all components)</param>
        /// <param name="lastSeenTick">Last tick this consumer saw (for event accumulation)</param>
        /// <returns>View instance (GDB: replica, SoD: pooled snapshot)</returns>
        ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
        
        /// <summary>
        /// Releases the view (GDB: no-op, SoD: return to pool).
        /// </summary>
        void ReleaseView(ISimulationView view);
    }
}
```

**Implementations:**

**A. DoubleBufferProvider (GDB - Fast Lane)**
```csharp
public class DoubleBufferProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly EntityRepository _replica;  // Persistent
    private readonly EventAccumulator _events = new();
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
    {
        // GDB: Sync entire replica (ignore mask for simplicity)
        _replica.SyncFrom(_live);
        
        // Accumulate events
        _events.CaptureFrame(_live.Bus, _live.GlobalVersion);
        _events.FlushToReplica(_replica.Bus, lastSeenTick);
        
        return _replica;  // Return persistent replica
    }
    
    public void ReleaseView(ISimulationView view)
    {
        // GDB: No-op (replica stays alive)
    }
}
```

**B. OnDemandProvider (SoD - Slow Lane)**
```csharp
public class OnDemandProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly ConcurrentStack<EntityRepository> _pool = new();
    private readonly EventAccumulator _events = new();
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
    {
        // SoD: Get pooled snapshot
        if (!_pool.TryPop(out var snapshot))
            snapshot = new EntityRepository();
        
        // Filtered sync (only requested components)
        snapshot.SyncFrom(_live, mask);
        
        // Accumulate events
        _events.CaptureFrame(_live.Bus, _live.GlobalVersion);
        _events.FlushToReplica(snapshot.Bus, lastSeenTick);
        
        return snapshot;  // Return pooled snapshot
    }
    
    public void ReleaseView(ISimulationView view)
    {
        // SoD: Clear and return to pool
        var repo = (EntityRepository)view;
        repo.SoftClear();
        _pool.Push(repo);
    }
}
```

**C. SharedSnapshotProvider (GDB - Convoy Pattern)**
```csharp
public class SharedSnapshotProvider : ISnapshotProvider
{
    private readonly EntityRepository _live;
    private readonly EntityRepository _replica;
    private readonly EventAccumulator _events = new();
    private int _activeReaders = 0;
    
    /// <summary>
    /// Try to update the shared replica (only succeeds if no active readers).
    /// </summary>
    public bool TryUpdateReplica()
    {
        if (_activeReaders > 0) return false;  // Locked by readers
        
        _replica.SyncFrom(_live);
        _events.CaptureFrame(_live.Bus, _live.GlobalVersion);
        _events.FlushToReplica(_replica.Bus, _replica.GlobalVersion);
        return true;
    }
    
    public ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick)
    {
        Interlocked.Increment(ref _activeReaders);
        return _replica;  // Shared replica (multiple readers)
    }
    
    public void ReleaseView(ISimulationView view)
    {
        Interlocked.Decrement(ref _activeReaders);
    }
}
```

**Tests Required:**
```
✓ DoubleBufferProvider syncs every AcquireView()
✓ OnDemandProvider pools snapshots correctly
✓ SharedSnapshotProvider convoy pattern (blocks sync when readers active)
✓ Filtered mask works (SoD only copies requested components)
✓ Event accumulation works for all providers
✓ Memory lifecycle (no leaks, proper pooling)
✓ Thread safety (concurrent AcquireView calls)
```

---

### Layer 2: Module Framework

#### IModule

**Purpose:** Core contract for all simulation logic

**Updated Signature:**

```csharp
namespace ModuleHost.Framework
{
    public interface IModule
    {
        // Identity & Configuration
        ModuleDefinition GetDefinition();
        
        // Snapshot requirements
        ComponentMask GetSnapshotRequirements();
        EventTypeMask GetEventRequirements();
        
        // Lifecycle
        void Initialize(IModuleContext context);
        void Start();
        void Stop();
        
        // Synchronous execution path
        void RegisterSystems(ISystemRegistry registry);
        
        // Asynchronous execution path
        // CHANGED: ISimWorldSnapshot → ISimulationView
        JobHandle Tick(
            FrameTime time,
            ISimulationView view,  // ← New interface (simpler)
            ICommandBuffer commands);
        
        // Diagnostics
        void DrawDiagnostics();
    }
}
```

**Change:** Only the interface type changed (`ISimWorldSnapshot` → `ISimulationView`)

**Module Implementation Example:**
```csharp
public class AiModule : IModule
{
    public JobHandle Tick(FrameTime time, ISimulationView view, ICommandBuffer commands)
    {
        // Module doesn't know if 'view' is GDB replica or SoD snapshot!
        // It just reads data:
        
        var query = view.Query()
            .With<Position>()
            .With<Team>()
            .Build();
        
        query.ForEach(entity => {
            var pos = view.GetComponentRO<Position>(entity);
            var team = view.GetManagedComponentRO<Team>(entity);
            
            // Decision logic...
            
            // Write commands (not directly to view)
            commands.SetComponent(entity, new Orders { ... });
        });
        
        return default;
    }
}
```

**Tests Required:**
✓ GetComponent<Identity>() returns correct immutable record
✓ GetEventHistory<Explosion>() returns all events since FromFrame
✓ TryGetComponent returns false for missing components
✓ Dispose() releases Tier2 arrays to pool
```

---

#### ISnapshotManager

**Purpose:** Factory for creating snapshots with union masks

```csharp
namespace ModuleHost.Core.Snapshots
{
    public interface ISnapshotManager
    {
        // Primary API
        ISimWorldSnapshot CreateSnapshot(
            Guid consumerId,
            ComponentMask componentMask,
            EventTypeMask eventMask);
        
        // Union snapshot for multiple modules
        ISimWorldSnapshot CreateUnionSnapshot(
            IEnumerable<IModule> readyModules);
        
        // Lifecycle
        void ReleaseShadowBuffer(Guid consumerId);
        
        // Diagnostics
        int GetActiveShadowBufferCount();
        long GetTotalShadowBufferBytes();
    }
}
```

**Implementation:** `SnapshotManager`

**Tests Required:**
```
✓ CreateSnapshot() only copies requested components (via mask)
✓ CreateUnionSnapshot() combines masks from multiple modules
✓ Shadow buffer is reused across frames
✓ Only dirty chunks are memcpy'd
✓ ReleaseShadowBuffer() returns buffer to pool
```

---

### Layer 2: Module Framework

#### IModule

**Purpose:** Core contract for all simulation logic

```csharp
namespace ModuleHost.Framework
{
    public interface IModule
    {
        // Identity & Configuration
        ModuleDefinition GetDefinition();
        
        // Snapshot requirements
        ComponentMask GetSnapshotRequirements();
        EventTypeMask GetEventRequirements();
        
        // Lifecycle
        void Initialize(IModuleContext context);
        void Start();
        void Stop();
        
        // Synchronous execution path
        void RegisterSystems(ISystemRegistry registry);
        
        // Asynchronous execution path
        JobHandle Tick(
            FrameTime time,
            ISimWorldSnapshot snapshot,
            ICommandBuffer commands);
        
        // Diagnostics
        void DrawDiagnostics();
    }
}
```

**Tests Required:**
```
✓ Module declares component requirements correctly
✓ Module declares event requirements correctly
✓ RegisterSystems() registers to correct phases
✓ Tick() completes within MaxExpectedRuntimeMs
✓ CommandBuffer is used for structural changes
```

---

#### ModuleDefinition

**Purpose:** Module metadata and scheduling configuration

```csharp
namespace ModuleHost.Framework
{
    public record ModuleDefinition
    {
        public required string Id { get; init; }
        public required string Version { get; init; }
        
        // Execution
        public bool IsSynchronous { get; init; } = true;
        public Phase Phase { get; init; }
        public int UpdateOrder { get; init; }
        
        // Background scheduling
        public int TargetFrequencyHz { get; init; } = 0;  // 0 = sync only
        public int MaxExpectedRuntimeMs { get; init; } = 1000;
        public int MaxEventHistoryFrames { get; init; } = 180;  // 3s @ 60Hz
        
        // Event-driven scheduling (NEW)
        public Type[] WatchComponents { get; init; } = Array.Empty<Type>();
        public Type[] WatchEvents { get; init; } = Array.Empty<Type>();
    }
}
```

**Tests Required:**
```
✓ IsSynchronous modules run on main thread
✓ TargetFrequencyHz controls async module frequency
✓ WatchComponents triggers module on component change
✓ WatchEvents triggers module immediately (interrupts timer)
```

---

### Layer 3: Host Kernel

#### ModuleHostKernel

**Purpose:** Main orchestrator (owns FDP, runs frame loop, manages modules)

```csharp
namespace ModuleHost.Core
{
    public class ModuleHostKernel
    {
        // Core components
        private readonly EntityRepository _repository;
        private readonly FdpEventBus _eventBus;
        private readonly SnapshotManager _snapshotManager;
        private readonly SystemRegistry _systemRegistry;
        private readonly BackgroundScheduler _backgroundScheduler;
        
        // Lifecycle
        public ModuleHostKernel(HostConfiguration config);
        public void Initialize();
        public void Start();
        public void Stop();
        
        // Main loop
        public void RunFrame();
    }
}
```

**RunFrame() Implementation:**

```csharp
public void RunFrame()
{
    ulong frameNumber = _repository.GlobalVersion;
    _repository.Tick();
    
    // 1. Synchronous phases
    ExecutePhase(Phase.NetworkIngest);
    ExecutePhase(Phase.Input);
    ExecutePhase(Phase.Simulation);
    ExecutePhase(Phase.PostSimulation);
    
    // 2. SYNC POINT
    _eventBus.SwapBuffers(frameNumber);  // Retire events to history
    ExecuteSyncPoint();                  // Create snapshots, trigger modules
    
    // 3. Structural changes
    ProcessCommandBuffers();
    
    // 4. Export
    ExecutePhase(Phase.Export);
    
    // 5. Cleanup
    PruneEventHistory();
}
```

**Tests Required:**
```
✓ Frame executes in <16.67ms @ 60Hz
✓ Phases execute in correct order
✓ Sync point completes in <2ms
✓ Command buffers applied before Export
✓ Event history pruned correctly
```

---

### Layer 4: Command Buffer

#### ICommandBuffer

**Purpose:** Thread-safe deferred entity operations for background modules

```csharp
namespace ModuleHost.Core.Commands
{
    public interface ICommandBuffer
    {
        // Structural operations
        Entity CreateEntity(string debugName = "");
        void DestroyEntity(Entity entity);
        
        // Component operations (generic JIT-optimized)
        void SetComponent<T>(Entity entity, T value);
        void RemoveComponent<T>(Entity entity);
        
        // Playback (called by host on main thread)
        void Playback(EntityRepository repository);
    }
}
```

**Tests Required:**
```
✓ CreateEntity() returns temp ID (negative)
✓ SetComponent() queues command correctly
✓ Playback() resolves temp IDs to real IDs
✓ Playback() executes all commands in order
✓ Thread-safe (multiple modules can queue concurrently)
```

---

## FDP Kernel Requirements

### Summary of Required APIs

**Document:** [fdp-api-requirements.md](fdp-api-requirements.md)

#### P0 - Week 1: Event-Driven Scheduling

```csharp
// NativeChunkTable.cs
public bool HasChanges(uint sinceVersion)
{
    for (int i = 0; i < _chunkVersions.Length; i++)
    {
        if (_chunkVersions[i] > sinceVersion)
            return true;
    }
    return false;
}

// EntityRepository.cs
public bool HasComponentChanged<T>(uint sinceTick);
public bool HasComponentChangedByType(Type type, uint sinceTick);

// FdpEventBus.cs
public bool HasEvents<T>() where T : unmanaged;
public bool HasEventsByType(Type eventType);
private HashSet<int> _activeEventIds = new();  // Track active events
```

**Verification:**
```
✓ HasChanges() scans 100 chunks in <100ns
✓ HasComponentChanged<Health>() returns true when Health modified
✓ HasEvents<Explosion>() returns true when explosion published
✓ _activeEventIds updated on Publish()
```

---

#### P0 - Week 2: Event History

```csharp
// FdpEventBus.cs
private Dictionary<ulong, FrameEventData> _history = new();

public void SwapBuffers(ulong frameNumber)
{
    // Retire buffers to history (don't clear!)
    var frameData = new FrameEventData();
    foreach (var stream in _nativeStreams.Values)
    {
        var retired = stream.SwapAndRetire();  // Returns buffer clone
        if (retired.Count > 0)
            frameData.NativeBuffers[stream.TypeId] = retired;
    }
    _history[frameNumber] = frameData;
}

public EventHistoryView GetEventHistory(
    ulong fromFrame,
    ulong toFrame,
    EventTypeMask filter)
{
    // Return filtered view of history
}

public void PruneHistory(ulong cutoff)
{
    // Remove old frames, return buffers to pool
}

// NativeEventStream.cs
public INativeEventStream SwapAndRetire()
{
    // Clone read buffer for history
    var clone = new NativeEventStream<T>();
    clone.CopyFrom(_frontBuffer, _count);
    
    // Swap for next frame
    Swap();
    
    return clone;
}
```

**Verification:**
```
✓ SwapBuffers() retains events in _history
✓ GetEventHistory() returns events from 180 frames
✓ Event filtering works (AI gets Explosion, not UIClick)
✓ PruneHistory() doesn't remove active snapshots
✓ Buffer pool reuse works
```

---

#### P1 - Week 3: Component Snapshots

```csharp
// EntityRepository.cs
public ulong GetChunkVersion<T>(int chunkIndex) where T : unmanaged
{
    var table = GetTable<T>(false);
    return table.GetChunkVersion(chunkIndex);
}

public unsafe byte* GetChunkPtr<T>(int chunkIndex) where T : unmanaged
{
    var table = GetTable<T>(false);
    return table.GetChunkPtr(chunkIndex);
}

// ManagedComponentTable.cs
public T?[] GetSnapshotArray<T>(int chunkIndex) where T : class
{
    var liveChunk = _chunks[chunkIndex];
    var snapshot = ArrayPool<T?>.Shared.Rent(liveChunk.Length);
    Array.Copy(liveChunk, snapshot, liveChunk.Length);  // Shallow copy
    return snapshot;
}
```

**Verification:**
```
✓ GetChunkVersion() returns correct version
✓ GetChunkPtr() returns valid pointer
✓ GetSnapshotArray() returns shallow copy
✓ Tier2 records are immutable (verified at compile time)
```

---

#### P0 - Week 1-2: Tier 2 Immutability Enforcement

**Critical Requirement:** Tier 2 components MUST be immutable C# records for SoD snapshot safety.

**Why Critical:**
- SoD uses shallow reference copying for Tier 2
- If objects are mutable, snapshots see main thread mutations (torn reads)
- This requirement is fundamental to system correctness

**Three-Layer Enforcement Strategy:**

##### Layer 1: Runtime Registration Validation

```csharp
// FDP/Fdp.Kernel/ManagedComponentTable.cs
public void RegisterComponent<T>() where T : class
{
    var type = typeof(T);
    
    // 1. Check if it's a record type
    if (!IsRecordType(type))
    {
        throw new InvalidOperationException(
            $"Tier 2 component '{type.Name}' must be a record type for immutability.\n" +
            $"Change: 'public class {type.Name}' → 'public record {type.Name}'");
    }
    
    // 2. Check all properties use init accessors
    var mutableProperties = type.GetProperties()
        .Where(p => p.CanWrite && p.SetMethod?.IsInitOnly == false)
        .ToList();
    
    if (mutableProperties.Any())
    {
        var propNames = string.Join(", ", mutableProperties.Select(p => p.Name));
        throw new InvalidOperationException(
            $"Tier 2 record '{type.Name}' has mutable properties: {propNames}.\n" +
            $"All properties must use 'init' accessors, not 'set'.");
    }
    
    // 3. Check for mutable fields (public fields are discouraged)
    var mutableFields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => !f.IsInitOnly)
        .ToList();
    
    if (mutableFields.Any())
    {
        var fieldNames = string.Join(", ", mutableFields.Select(f => f.Name));
        throw new InvalidOperationException(
            $"Tier 2 record '{type.Name}' has mutable fields: {fieldNames}.\n" +
            $"Use 'public required Type PropName {{ get; init; }}' instead of fields.");
    }
}

// Helper method
private static bool IsRecordType(Type type)
{
    // Records have a compiler-generated Equals method with specific signature
    var printMemberMethod = type.GetMethod("PrintMembers", 
        BindingFlags.NonPublic | BindingFlags.Instance);
    
    if (printMemberMethod != null)
        return true;
    
    // Also check for <Clone>$ method (record clone)
    var cloneMethod = type.GetMethod("<Clone>$", 
        BindingFlags.NonPublic | BindingFlags.Instance);
    
    return cloneMethod != null;
}
```

**When Validated:** At component registration time (startup, not per-frame)

**Error Message Example:**
```
InvalidOperationException: Tier 2 component 'Identity' must be a record type for immutability.
Change: 'public class Identity' → 'public record Identity'
```

---

##### Layer 2: JIT Serializer Validation

```csharp
// FDP/Fdp.Kernel/Serialization/FdpAutoSerializer.cs
public static class FdpAutoSerializer
{
    public static Action<BinaryWriter, T> GenerateSerializer<T>() where T : class
    {
        var type = typeof(T);
        
        // HARD CHECK: Validate immutability before generating serializer
        ValidateImmutability(type);
        
        // ... continue with expression tree generation
    }
    
    private static void ValidateImmutability(Type type)
    {
        // Same checks as registration, but HARD EXCEPTION
        if (!IsRecordType(type))
        {
            throw new InvalidOperationException(
                $"FATAL: Attempting to serialize mutable Tier 2 component '{type.Name}'.\n" +
                $"ALL Tier 2 components must be immutable records.\n" +
                $"This should have been caught at registration!\n" +
                $"Fix: 'public record {type.Name}'");
        }
        
        // Check for collection properties with mutable element types
        var properties = type.GetProperties();
        foreach (var prop in properties)
        {
            if (typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) &&
                prop.PropertyType != typeof(string))
            {
                // Check if collection is immutable (ImmutableList, etc.)
                if (!prop.PropertyType.FullName.StartsWith("System.Collections.Immutable"))
                {
                    throw new InvalidOperationException(
                        $"Property '{prop.Name}' in '{type.Name}' uses mutable collection '{prop.PropertyType.Name}'.\n" +
                        $"Use ImmutableList<T>, ImmutableArray<T>, or similar.\n" +
                        $"Example: 'public ImmutableList<Waypoint> Route {{ get; init; }}'");
                }
            }
        }
    }
}
```

**When Validated:** When serializer is generated (first use, cached thereafter)

**Why Important:** Catches edge cases like mutable nested collections

---

##### Layer 3: Roslyn Analyzer (Compile-Time)

```csharp
// FDP.Analyzers/Tier2ImmutabilityAnalyzer.cs
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class Tier2ImmutabilityAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FDP001";
    
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        title: "Tier 2 components must be immutable records",
        messageFormat: "Component '{0}' should be declared as 'record' not 'class' for Tier 2 safety",
        category: "FDP.Design",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All managed components must be immutable records to ensure snapshot consistency.");
    
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => 
        ImmutableArray.Create(Rule);
    
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        
        context.RegisterSyntaxNodeAction(AnalyzeClassDeclaration, SyntaxKind.ClassDeclaration);
    }
    
    private void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
        
        if (symbol == null) return;
        
        // Check if class is used as Tier 2 component
        // (Look for repo.AddComponent<T>, repo.SetComponent<T> calls with this type)
        // Or check for custom [Tier2Component] attribute
        
        var isTier2Component = HasTier2ComponentAttribute(symbol) || 
                               IsUsedAsManagedComponent(symbol, context.Compilation);
        
        if (isTier2Component && !symbol.IsRecord)
        {
            var diagnostic = Diagnostic.Create(
                Rule, 
                classDecl.Identifier.GetLocation(), 
                symbol.Name);
            
            context.ReportDiagnostic(diagnostic);
        }
    }
    
    private bool HasTier2ComponentAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes()
            .Any(a => a.AttributeClass?.Name == "Tier2ComponentAttribute");
    }
}
```

**Code Fix Provider:**
```csharp
[ExportCodeFixProvider(LanguageNames.CSharp)]
public class Tier2ImmutabilityCodeFixProvider : CodeFixProvider
{
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        // ... code fix logic ...
        
        // Offer fix: Change 'class' to 'record'
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Change to record",
                createChangedDocument: c => ChangeToRecord(context.Document, classDecl, c),
                equivalenceKey: "ChangeToRecord"),
            diagnostic);
    }
}
```

**When Validated:** At compile time (IntelliSense shows error immediately)

**Benefits:**
- Catches errors BEFORE runtime
- IDE shows red squiggle + suggested fix
- Can't accidentally commit broken code

---

**Testing Requirements:**

```csharp
// Test Layer 1: Runtime Registration
[Fact]
public void RegisterComponent_ClassType_ThrowsException()
{
    // Arrange
    var table = new ManagedComponentTable<InvalidClass>();
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(
        () => table.RegisterComponent<InvalidClass>());
    
    Assert.Contains("must be a record type", ex.Message);
}

[Fact]
public void RegisterComponent_RecordType_Succeeds()
{
    // Arrange
    var table = new ManagedComponentTable<ValidRecord>();
    
    // Act & Assert (should not throw)
    table.RegisterComponent<ValidRecord>();
}

[Fact]
public void RegisterComponent_RecordWithMutableProperty_ThrowsException()
{
    // Arrange (record with property using 'set' instead of 'init')
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(...);
    Assert.Contains("mutable properties", ex.Message);
}

// Test Layer 2: JIT Serializer
[Fact]
public void GenerateSerializer_MutableType_ThrowsHardException()
{
    // Arrange
    var serializer = new FdpAutoSerializer();
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(
        () => serializer.GenerateSerializer<MutableClass>());
    
    Assert.Contains("FATAL", ex.Message);
}

[Fact]
public void GenerateSerializer_MutableCollection_ThrowsException()
{
    // Record with List<T> instead of ImmutableList<T>
    var ex = Assert.Throws<InvalidOperationException>(...);
    Assert.Contains("Use ImmutableList", ex.Message);
}

// Test Layer 3: Roslyn Analyzer
[Fact]
public void Analyzer_ClassUsedAsManagedComponent_ReportsDiagnostic()
{
    var code = @"
        public class BadComponent  // Should be record
        {
            public string Name { get; init; }
        }
    ";
    
    var expected = new DiagnosticResult
    {
        Id = "FDP001",
        Message = "Component 'BadComponent' should be declared as 'record' not 'class'",
        Severity = DiagnosticSeverity.Error
    };
    
    VerifyDiagnostic(code, expected);
}
```

---

**Verification Checklist:**

```
Tier 2 Immutability Enforcement:

Layer 1 (Runtime Registration):
✓ Class type rejected with clear error
✓ Record with mutable property rejected
✓ Record with mutable field rejected
✓ Valid immutable record accepted
✓ Error message suggests exact fix

Layer 2 (JIT Serializer):
✓ Mutable type throws FATAL exception
✓ Mutable collection (List<T>) rejected
✓ ImmutableList<T> accepted
✓ Nested mutable types detected

Layer 3 (Roslyn Analyzer):
✓ Analyzer detects class used as Tier 2
✓ Error shown in IDE immediately
✓ Code fix offered (class → record)
✓ Build fails if mutable Tier 2 found

Integration:
✓ Cannot register mutable component
✓ Cannot serialize mutable component  
✓ Cannot compile mutable component
✓ All three layers tested independently
✓ All three layers tested together
```

---

**Examples:**

**❌ BAD (All three layers reject):**
```csharp
public class Identity  // ← Layer 3: Analyzer error (compile time)
{
    public string Callsign { get; set; }  // ← Mutable!
}

// Layer 1: Throws at registration
repo.RegisterComponent<Identity>();  // Exception!

// Layer 2: Throws at serialization
serializer.GenerateSerializer<Identity>();  // FATAL!
```

**✅ GOOD:**
```csharp
public record Identity  // ← Record type
{
    public required string Callsign { get; init; }  // ← Immutable
    public ImmutableList<string> Aliases { get; init; } = ImmutableList<string>.Empty;
}

// All layers accept:
repo.RegisterComponent<Identity>();  // ✓ Pass
serializer.GenerateSerializer<Identity>();  // ✓ Pass
// Analyzer: ✓ No diagnostic
```

---

**Implementation Priority:** **P0 (Critical)**

This must be implemented in Week 1-2 alongside FDP event APIs because:
1. Prevents subtle data corruption bugs
2. Easy to test
3. Protects system correctness from day 1

---

## ModuleHost Components

### Implementation Checklist

**Document:** [implementation-checklist.md](implementation-checklist.md)

#### Phase 1: Snapshot Core (Week 1-2)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `ComponentMask` | BitMask for component filtering | ⏳ TODO | 5 tests |
| `EventTypeMask` | BitMask for event filtering | ⏳ TODO | 5 tests |
| `ShadowBuffer` | Persistent buffer with dirty tracking | ⏳ TODO | 8 tests |
| `SnapshotManager` | Snapshot factory | ⏳ TODO | 12 tests |
| `Tier1Snapshot` | Unmanaged component view | ⏳ TODO | 6 tests |
| `Tier2Snapshot` | Managed component view | ⏳ TODO | 6 tests |
| `HybridSnapshot` | Combined snapshot | ⏳ TODO | 10 tests |
| `EventHistoryView` | Event batch iterator | ⏳ TODO | 8 tests |

**Verification Steps:**
```
1. Create snapshot with Position + Health components
   ✓ Shadow buffer allocated
   ✓ Only requested components copied
   ✓ Dirty chunks identified correctly

2. Create snapshot 6 frames later
   ✓ Shadow buffer reused
   ✓ Only modified chunks memcpy'd
   ✓ Static entities = 0 bandwidth

3. Get event history spanning 60 frames
   ✓ All explosions since FromFrame returned
   ✓ Events ordered chronologically
   ✓ Event filtering works (no UI events in AI snapshot)
```

---

#### Phase 2: Module Framework (Week 3)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `IModule` interface | Core module contract | ⏳ TODO | N/A |
| `ModuleDefinition` | Module metadata | ⏳ TODO | 3 tests |
| `ModuleLoader` | DLL discovery & loading | ⏳ TODO | 8 tests |
| `SystemRegistry` | System registration | ⏳ TODO | 6 tests |
| `BackgroundScheduler` | Async module scheduler | ⏳ TODO | 12 tests |

**Verification Steps:**
```
1. Load module from DLL
   ✓ Module discovered in /modules directory
   ✓ Dependencies resolved
   ✓ IModule interface implemented

2. Register synchronous systems
   ✓ Systems registered to correct phases
   ✓ UpdateOrder respected

3. Schedule background module
   ✓ TargetFrequencyHz = 10Hz → runs every 6 frames
   ✓ WatchEvents = [Explosion] → runs immediately on explosion
   ✓ WatchComponents = [Health] → runs on health change
```

---

#### Phase 3: Host Kernel (Week 4)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `ModuleHostKernel` | Main orchestrator | ⏳ TODO | 15 tests |
| `PhaseExecutor` | Executes systems for phase | ⏳ TODO | 6 tests |
| `HostConfiguration` | Configuration | ⏳ TODO | 3 tests |

**Verification Steps:**
```
1. Execute one frame
   ✓ All phases execute in order
   ✓ Total time <16.67ms @ 60Hz
   ✓ Sync point <2ms

2. Trigger background module
   ✓ Snapshot created with union mask
   ✓ Module runs on thread pool
   ✓ CommandBuffer queued

3. Playback command buffers
   ✓ Executed in Structural phase
   ✓ TempID → RealID mapping works
   ✓ Entities created correctly
```

---

#### Phase 4: Command Buffer (Week 4)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `ICommandBuffer` interface | Deferred operations | ⏳ TODO | N/A |
| `EntityCommandBuffer` | Implementation | ⏳ TODO | 10 tests |
| `ICommand` implementations | Create/Set/Remove commands | ⏳ TODO | 12 tests |

**Verification Steps:**
```
1. Queue structural changes from background
   ✓ CreateEntity() returns temp ID
   ✓ SetComponent() queued correctly
   ✓ Thread-safe queuing

2. Playback on main thread
   ✓ Commands execute in order
   ✓ Temp IDs resolved to real IDs
   ✓ Components applied correctly
```

---

#### Phase 5: Services (Week 5)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `GeographicTransform` | Coordinate conversion | ⏳ TODO | 8 tests |
| `CoordinateTransformSystem` | Tier1 → Tier2 sync | ⏳ TODO | 6 tests |
| `NetworkIngestSystem` | DDS → FDP | ⏳ TODO | 10 tests |
| `NetworkSyncSystem` | FDP → DDS | ⏳ TODO | 8 tests |

---

#### Phase 6: Advanced (Week 6)

| Component | Description | Status | Tests |
|-----------|-------------|--------|-------|
| `EntityLifecycleModule` | ELM protocol | ⏳ TODO | 12 tests |
| `SnapshotLeaseManager` | Lease expiry | ⏳ TODO | 6 tests |
| `ModuleCircuitBreaker` | Failure isolation | ⏳ TODO | 8 tests |
| `FrameWatchdog` | Hung frame detection | ⏳ TODO | 4 tests |

---

## Verification & Testing

### Unit Tests

**Target:** 95% code coverage

**Categories:**

1. **Snapshot Tests** (50+ tests)
   - Component filtering
   - Dirty tracking
   - Event history
   - Array pooling
   - Thread safety

2. **Scheduling Tests** (40+ tests)
   - Event-driven triggers
   - Component change detection
   - Timer-based scheduling
   - Union mask calculation

3. **Command Buffer Tests** (30+ tests)
   - Temp ID resolution
   - Thread-safe queuing
   - Playback ordering
   - Component type dispatch

4. **Module Framework Tests** (30+ tests)
   - Module loading
   - Dependency resolution
   - System registration
   - Phase ordering

5. **Integration Tests** (20+ tests)
   - Full frame execution
   - Multi-module scenarios
   - Network sync
   - Event-driven reactivity

---

### Performance Tests

**Target:** All metrics within spec

| Metric | Target | Test Method |
|--------|--------|-------------|
| **Frame Time** | <16.67ms @ 60Hz | 1000 frame average |
| **Sync Point** | <2ms (99th percentile) | 1000 sample distribution |
| **Snapshot Bandwidth** | <500 MB/s @ 30% activity | Monitor memcpy volume |
| **Module Wake Check** | <200ns per module | Microbenchmark |
| **Event History Query** | <50μs for 180 frames | Time GetEventHistory() |
| **GC Pressure** | Zero allocations/frame | GC.GetTotalMemory() |

---

### Scenario Tests

**Test 1: AI Reacts to Explosion**
```
Given: AI module at 10Hz (TargetFrequencyHz=10, WatchEvents=[Explosion])
When: Explosion event published on Frame 2
Then:
  ✓ AI module wakes immediately (< 1ms)
  ✓ Snapshot contains explosion event
  ✓ AI processes explosion
  ✓ AI queues response via CommandBuffer
```

**Test 2: Long-Running Analytics**
```
Given: Analytics module running for 2 seconds
When: 120 frames elapse (2s @ 60Hz)
Then:
  ✓ Event history spans all 120 frames
  ✓ No events lost
  ✓ Snapshot still valid
  ✓ Module completes without errors
```

**Test 3: Event Filtering Performance**
```
Given: 100K physics collision events per frame
  And: UI module requires only UI events (10/frame)
When: UI snapshot created
Then:
  ✓ Snapshot contains only 10 UI events (not 100K)
  ✓ Snapshot creation <1ms
  ✓ Event bandwidth <1KB
```

**Test 4: Dynamic Buffer Growth**
```
Given: Normal event volume = 100 events/frame
When: Sudden spike to 10K events (explosions, particles)
Then:
  ✓ Buffers resize automatically
  ✓ No data loss
  ✓ No exceptions
  ✓ Buffers return to pool after pruning
```

**Test 5: Concurrent Modules**
```
Given: 5 background modules running concurrently
When: All modules queue commands
Then:
  ✓ CommandBuffers thread-safe
  ✓ All commands executed correctly
  ✓ No ID conflicts
  ✓ Entities created in correct order
```

---

## Success Criteria

### Functional Requirements

- ✅ Modules load dynamically from DLLs
- ✅ Synchronous systems execute in correct phase order
- ✅ Background modules run asynchronously on snapshots
- ✅ Snapshots are consistent (no torn reads)
- ✅ Event history spans 3 seconds minimum
- ✅ Event-driven scheduling works (< 1ms reaction)
- ✅ Command buffers enable async entity creation
- ✅ Network sync works bidirectionally (DDS ↔ FDP)
- ✅ No data loss under any event volume

### Performance Requirements

- ✅ 60 Hz stable with 100K entities
- ✅ Sync point <2ms (99th percentile)
- ✅ Zero GC allocations per frame (steady state)
- ✅ <10ms physics budget maintained
- ✅ Module wake check <200ns
- ✅ Event history query <50μs

### Safety Requirements

- ✅ Snapshot expiry prevents memory leaks
- ✅ Circuit breakers prevent cascading failures
- ✅ Watchdogs detect hung frames
- ✅ No data races (validated with ThreadSanitizer)
- ✅ Tier 2 immutability enforced (compile-time)

---

## Reference Documents

### Architecture & Design

| Document | Purpose |
|----------|---------|
| [ADR-001-Snapshot-on-Demand.md](ADR-001-Snapshot-on-Demand.md) | COW vs SoD decision |
| [advisor-review-alignment.md](advisor-review-alignment.md) | Advisor validation |
| [detailed-design-overview.md](detailed-design-overview.md) | Complete class design |
| [design-visual-reference.md](design-visual-reference.md) | Diagrams & flows |

### Requirements

| Document | Purpose |
|----------|---------|
| [FDP-SST-001-Integration-Architecture.md](FDP-SST-001-Integration-Architecture.md) | Original requirements |
| [fdp-api-requirements.md](fdp-api-requirements.md) | FDP changes needed |
| [FDP-EventsInSnapshots.md](FDP-EventsInSnapshots.md) | Event history design |
| [FDP-module-scheduling-support.md](FDP-module-scheduling-support.md) | Event-driven scheduling |

### API Refinements

| Document | Purpose |
|----------|---------|
| [design-refinements-categories-api.md](design-refinements-categories-api.md) | Module categories & APIs |
| [design-refinements-part2-api-simplification.md](design-refinements-part2-api-simplification.md) | Generic API with JIT |
| [api-refinements-final.md](api-refinements-final.md) | Final API decisions |

### Implementation

| Document | Purpose |
|----------|---------|
| [implementation-checklist.md](implementation-checklist.md) | Task tracking |
| [fdp-api-requirements-summary.md](fdp-api-requirements-summary.md) | FDP quick reference |

---

## Implementation Phases & Timeline

### Week 1: FDP Event-Driven APIs

**Priority:** Highest value, no blockers

**Tasks:**
- [ ] Add `HasChanges()` to `NativeChunkTable`
- [ ] Add `HasComponentChanged()` to `EntityRepository`
- [ ] Add `HasEvents()` to `FdpEventBus`
- [ ] Add `_activeEventIds` tracking
- [ ] Unit tests for all APIs
- [ ] Integration test: Module wakes on event

**Deliverable:** Event-driven scheduling functional

---

### Week 2: FDP Event History

**Priority:** Critical for long-running modules

**Tasks:**
- [ ] Refactor `FdpEventBus.SwapBuffers(frameNumber)`
- [ ] Implement `SwapAndRetire()` in `NativeEventStream`
- [ ] Add dynamic buffer growth
- [ ] Implement `GetEventHistory()` with filtering
- [ ] Implement `PruneHistory()` with safety checks
- [ ] Unit tests for event retention
- [ ] Integration test: 180-frame history

**Deliverable:** Event history for background modules

---

### Week 3: Snapshot Core

**Priority:** Foundation for all async modules

**Tasks:**
- [ ] Implement `ComponentMask` and `EventTypeMask`
- [ ] Implement `ShadowBuffer` with dirty tracking
- [ ] Implement `SnapshotManager`
- [ ] Implement `Tier1Snapshot`, `Tier2Snapshot`, `HybridSnapshot`
- [ ] Implement `EventHistoryView`
- [ ] Add FDP component snapshot APIs
- [ ] Unit tests for all components
- [ ] Integration test: Full snapshot with events

**Deliverable:** Complete snapshot system

---

### Week 4: Module Framework & Host

**Priority:** Orchestration layer

**Tasks:**
- [ ] Implement `IModule` interface
- [ ] Implement `ModuleDefinition`
- [ ] Implement `ModuleLoader`
- [ ] Implement `SystemRegistry`
- [ ] Implement `BackgroundScheduler`
- [ ] Implement `ModuleHostKernel`
- [ ] Implement `EntityCommandBuffer`
- [ ] Unit tests for all components
- [ ] Integration test: Full frame execution

**Deliverable:** Host can run modules

---

### Week 5: Services

**Priority:** Network sync & coordinate transform

**Tasks:**
- [ ] Implement `GeographicTransform`
- [ ] Implement `CoordinateTransformSystem`
- [ ] Implement `NetworkIngestSystem` (multi-topic)
- [ ] Implement `NetworkSyncSystem`
- [ ] Unit tests
- [ ] Integration test: DDS ↔ FDP sync

**Deliverable:** Network synchronization works

---

### Week 6: Advanced Features

**Priority:** Resilience & ELM

**Tasks:**
- [ ] Implement `EntityLifecycleModule`
- [ ] Implement `SnapshotLeaseManager`
- [ ] Implement `ModuleCircuitBreaker`
- [ ] Implement `FrameWatchdog`
- [ ] Unit tests
- [ ] Integration test: ELM dark construction

**Deliverable:** Production-ready system

---

## Status Tracking

**Current Status:** ✅ DESIGN COMPLETE - Ready for Implementation

**Last Updated:** January 3, 2026

**Next Milestone:** Week 1 - FDP Event-Driven APIs

---

## Sign-Off

**Architecture Review:** ✅ APPROVED  
**Advisor Validation:** ✅ APPROVED  
**Requirements Complete:** ✅ YES  
**Implementation Ready:** ✅ YES

**Ready to begin coding Week 1 tasks.**

---

*End of Implementation Specification*
