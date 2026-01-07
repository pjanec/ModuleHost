# ModuleHost Advanced Features - Design & Implementation Plan

**Date:** 2026-01-07  
**Status:** Design Document  
**Purpose:** Comprehensive design for implementing remaining ModuleHost features based on gap analysis

---

## Executive Summary

This document provides a detailed architectural design for implementing the remaining advanced features of ModuleHost. The design is based on a comprehensive gap analysis between the original vision documents and the current implementation state.

### Current State Summary

**What's Implemented (Production-Ready):**
- ✅ Core FDP Kernel (EntityRepository, Component Tables, Event Bus)
- ✅ Basic Module Registration and Execution
- ✅ Double Buffer Provider (GDB) for Fast Tier
- ✅ On-Demand Provider (SoD) for Slow Tier  
- ✅ Shared Snapshot Provider (base implementation)
- ✅ System Scheduler with Phase-based execution
- ✅ Timer-based scheduling (UpdateFrequency)
- ✅ Command Buffer pattern for thread-safe mutations

**What's Missing (To Be Implemented):**
- ❌ Non-Blocking Execution ("World C" / Triple Buffering)
- ❌ Reactive Scheduling (Event & Component Change Triggers)
- ❌ Convoy Pattern (Auto-grouping modules with same frequency)
- ❌ Resilience & Safety (Circuit Breakers, Timeouts, Watchdogs)
- ❌ Flexible Execution Modes (Explicit policies beyond Fast/Slow)
- ❌ Entity Lifecycle Manager (Dark Construction/Teardown)
- ❌ Network Gateway (DDS/SST Integration)
- ❌ Geographic Transform Services (Cartesian ↔ Geodetic)

---

## Priority Order

Based on architectural dependencies and user requirements:

1. **Non-Blocking Execution** (Critical for frame stability)
2. **Reactive Scheduling** (Critical for responsiveness)
3. **Convoy & Pooling** (Performance optimization)
4. **Resilience & Safety** (Production stability)
5. **Flexible Execution Modes** (API refinement)
6. **Entity Lifecycle Manager** (Distributed coordination)
7. **Network Gateway** (Federation capability)
8. **Geographic Transform** (Domain-specific)

---

## Chapter 1: Non-Blocking Execution ("World C")

### 1.1 Specification

**Objective:** Decouple slow module execution from main thread frame rate.

**Requirements:**
- Main simulation loop (60Hz) must never block waiting for background modules
- Slow modules (e.g., 10Hz AI) can take multiple frames to complete (e.g., 50ms)
- Main thread continues updating while slow modules process old snapshots
- Commands from slow modules are harvested and applied when ready

**Why Needed:**
- **Frame Stability:** Current `Task.WaitAll()` causes micro-stutters when modules spike
- **CPU Utilization:** Enables heavy compute tasks to use spare cores without blocking critical path
- **Scalability:** Supports arbitrary module execution times without affecting gameplay

**Constraints:**
- Triple Buffering required (World A: Live, World B: Fast Replicas, World C: Slow Snapshots)
- Command latency is acceptable for AI/Analytics but must be deterministic
- Snapshots must remain valid (pinned) until module releases them

### 1.2 Design

**Architecture Changes:**

1. **Module State Tracking**
   - Add execution states: Idle, Running, Completed
   - Track active tasks and leased views per module

2. **Triple Buffering Strategy**
   - **World A (Live):** Updated every frame by main thread
   - **World B (Fast Replica):** Synced every frame, used by FrameSynced modules
   - **World C (Slow Snapshot):** Created on-demand, held until module completes

3. **Check-and-Harvest Loop**
   - Replace blocking `Task.WaitAll()` with non-blocking task checking
   - Harvest completed modules at frame start
   - Dispatch new modules at frame end
   - Skip still-running async modules (they hold onto World C)

**Data Structures:**

```csharp
// ModuleEntry additions
private class ModuleEntry
{
    // Existing fields...
    
    // Async State (NEW)
    public Task? CurrentTask { get; set; }
    public ISimulationView? LeasedView { get; set; }
    public float AccumulatedDeltaTime { get; set; }
    public uint LastRunTick { get; set; }
}
```

**Execution Flow:**

```
Update(deltaTime):
  1. HARVEST PHASE
     - For each module with CurrentTask:
       - If IsCompleted: Playback commands, ReleaseView, reset state
       - Else: Skip (still running), accumulate time
       
  2. SYNC PHASE
     - Update all FrameSynced providers (GDB sync)
     - Check Async triggers for SoD updates
     
  3. DISPATCH PHASE
     - For each idle module that ShouldRun:
       - AcquireView (creates World C if needed)
       - Task.Run(module.Tick)
       - If FrameSynced: add to waitList
     - WaitAll(waitList) for synchronized modules only
     - Harvest FrameSynced modules immediately
```

**Provider Implications:**

- **OnDemandProvider:** Already supports pooling; increase pool size for concurrency
- **SharedSnapshotProvider:** Reference counting ensures snapshot stays alive while any module uses it
- **DoubleBufferProvider:** May need triple-buffer variant if slow modules need persistent replicas

---

## Chapter 2: Reactive Scheduling

### 2.1 Specification

**Objective:** Enable modules to wake on specific events or component changes, not just timers.

**Requirements:**
- Modules can declare `WatchEvents` (list of event types to monitor)
- Modules can declare `WatchComponents` (list of component types to monitor)
- Trigger checks must be O(1) or very small O(n) — no entity iteration
- Triggers override `UpdateFrequency` timer

**Why Needed:**
- **Responsiveness:** AI at 1Hz shouldn't ignore being shot for 900ms
- **Efficiency:** Prevents polling modules from running just to check conditions
- **Bandwidth Optimization:** Modules sleep until meaningful changes occur

**Constraints:**
- Event triggers have high precision (event happened → module runs)
- Component triggers have coarse precision (table dirty → module runs, may have false positives)
- Metadata must be static/declarative for performance

### 2.2 Design

**Architecture Changes:**

1. **Component Dirty Tracking**
   - Add `uint LastWriteTick` to `IComponentTable` interface
   - Update on every `Set()` or `GetRW()` call
   - Atomic write (single integer, aligned)

2. **Event Tracking**
   - Add `HashSet<int> _activeEventIds` to `FdpEventBus`
   - Populate during `Publish()`, clear during `SwapBuffers()`
   - Fast lookup via hash set

3. **Module Metadata**
   - Extend `IModule` with `WatchComponents` and `WatchEvents` properties
   - Cache Type→ID mappings in kernel during initialization

**API Changes:**

```csharp
// IModule additions
public interface IModule
{
    // Existing...
    
    // NEW: Reactive triggers
    IReadOnlyList<Type>? WatchComponents { get; }
    IReadOnlyList<Type>? WatchEvents { get; }
}

// EntityRepository additions
public bool HasComponentChanged(Type componentType, uint sinceTick)
{
    if (_componentTables.TryGetValue(componentType, out var table))
        return table.LastWriteTick > sinceTick;
    return false;
}

// FdpEventBus additions
private readonly HashSet<int> _activeEventIds = new();

public bool HasEvent(Type eventType)
{
    int id = GetEventTypeId(eventType);
    return _activeEventIds.Contains(id);
}
```

**Trigger Logic:**

```csharp
private bool ShouldRunThisFrame(ModuleEntry entry)
{
    // 1. Timer check (existing)
    int freq = Math.Max(1, entry.UpdateFrequency);
    bool timerDue = (entry.FramesSinceLastRun + 1) >= freq;
    if (timerDue) return true;
    
    // 2. Event triggers (NEW)
    if (entry.WatchEvents != null)
        foreach (var evtType in entry.WatchEvents)
            if (_liveWorld.Bus.HasEvent(evtType))
                return true;
    
    // 3. Component triggers (NEW)
    if (entry.WatchComponents != null)
        foreach (var compType in entry.WatchComponents)
            if (_liveWorld.HasComponentChanged(compType, entry.LastRunTick))
                return true;
    
    return false;
}
```

**Performance Optimization:**
- Cache Type→ID mappings during `Initialize()` to avoid reflection in hot path
- Component checks use single integer comparison (LastWriteTick)
- Event checks use HashSet lookup O(1)
- No entity iteration required

---

## Chapter 3: Convoy & Pooling Patterns

### 3.1 Specification

**Objective:** Optimize memory and sync performance for modules running at the same frequency.

**Requirements:**
- Modules with identical frequency share a single snapshot (Convoy Pattern)
- Union of component requirements determines shared snapshot content
- Snapshots are pooled and reused to eliminate GC pressure
- Reference counting ensures snapshot stays alive until all convoy members finish

**Why Needed:**
- **Memory:** 5 AI modules × 100MB snapshot = 500MB; Convoy = 100MB
- **Sync Performance:** Syncing 1 snapshot is 5× faster than syncing 5
- **GC Elimination:** Pooling avoids `new EntityRepository()` allocations

**Constraints:**
- Thread-safe sharing (read-only snapshots)
- Lifecycle tied to slowest consumer in convoy
- Auto-detection based on configuration

### 3.2 Design

**Architecture Changes:**

1. **Snapshot Pool**
   - `ConcurrentStack<EntityRepository>` for pooled instances
   - `SoftClear()` on return (reset indices, keep buffer capacity)
   - Schema setup cached for consistent initialization

2. **Convoy Detection**
   - Group modules by: `{Mode, Strategy, Frequency}`
   - Calculate Union Mask for each group
   - Assign single `SharedSnapshotProvider` to group

3. **Reference Counting**
   - `_activeReaders` counter in `SharedSnapshotProvider`
   - Increment on `AcquireView()`, decrement on `ReleaseView()`
   - Return to pool when count hits zero

**Data Structures:**

```csharp
public class SnapshotPool
{
    private readonly ConcurrentStack<EntityRepository> _pool = new();
    private readonly Action<EntityRepository>? _schemaSetup;
    
    public EntityRepository Get()
    {
        if (_pool.TryPop(out var repo))
            return repo;
        
        var newRepo = new EntityRepository();
        _schemaSetup?.Invoke(newRepo);
        return newRepo;
    }
    
    public void Return(EntityRepository repo)
    {
        repo.SoftClear();
        _pool.Push(repo);
    }
}

public class SharedSnapshotProvider
{
    private readonly BitMask256 _unionMask;
    private readonly SnapshotPool _pool;
    private EntityRepository? _currentSnapshot;
    private int _activeReaders;
    
    public ISimulationView AcquireView()
    {
        lock (_lock)
        {
            if (_currentSnapshot == null)
            {
                _currentSnapshot = _pool.Get();
                _currentSnapshot.SyncFrom(_liveWorld, _unionMask);
            }
            _activeReaders++;
            return _currentSnapshot;
        }
    }
    
    public void ReleaseView(ISimulationView view)
    {
        lock (_lock)
        {
            _activeReaders--;
            if (_activeReaders == 0)
            {
                _pool.Return(_currentSnapshot!);
                _currentSnapshot = null;
            }
        }
    }
}
```

**Auto-Grouping Logic:**

```csharp
private void AutoAssignProviders()
{
    var pool = new SnapshotPool(_schemaSetup);
    
    var groups = _modules
        .Where(m => m.Provider == null)
        .GroupBy(m => new { 
            m.Policy.Mode, 
            m.Policy.Strategy, 
            m.Policy.TargetFrequencyHz 
        });
    
    foreach (var group in groups)
    {
        if (group.Count() == 1)
        {
            // Single module: OnDemandProvider
            var entry = group.First();
            entry.Provider = new OnDemandProvider(...);
        }
        else
        {
            // Convoy: SharedSnapshotProvider
            var unionMask = new BitMask256();
            foreach (var m in group)
                unionMask.BitwiseOr(GetComponentMask(m.Module));
            
            var sharedProvider = new SharedSnapshotProvider(..., unionMask, pool);
            foreach (var entry in group)
                entry.Provider = sharedProvider;
        }
    }
}
```

---

## Chapter 4: Resilience & Safety

### 4.1 Specification

**Objective:** Prevent faulty modules from crashing or freezing the host.

**Requirements:**
- **Hang Detection:** Timeout for module execution
- **Failure Isolation:** Exceptions caught and logged without crashing kernel
- **Circuit Breaking:** Disable repeatedly failing modules
- **Graceful Degradation:** System continues without faulty module

**Why Needed:**
- **Production Stability:** 1 buggy analytics module shouldn't kill 19 working modules
- **Development Experience:** Clear errors instead of frozen app
- **Operational Resilience:** Auto-recovery from transient failures

**Constraints:**
- Cannot forcefully kill C# threads (Thread.Abort is dangerous)
- "Zombie" tasks may leak thread pool threads if truly hung
- Timeout granularity limited by Task infrastructure

### 4.2 Design

**Architecture Changes:**

1. **Circuit Breaker State Machine**
   - States: Closed (normal), Open (failed), HalfOpen (testing)
   - Failure threshold configurable per module
   - Reset timeout after which module can retry

2. **Safe Execution Wrapper**
   - Wrap `Task.Run(module.Tick)` with timeout logic
   - Use `Task.WhenAny` to race execution vs. delay
   - Catch exceptions and record failures

3. **Zombie Detection**
   - Mark entry as zombie if timeout occurs
   - Don't schedule again until task completes or circuit trips

**Data Structures:**

```csharp
public enum CircuitState { Closed, Open, HalfOpen }

public class ModuleCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly int _resetTimeoutMs;
    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state = CircuitState.Closed;
    
    public bool CanRun()
    {
        if (_state == CircuitState.Closed) return true;
        if (_state == CircuitState.Open)
        {
            if ((DateTime.UtcNow - _lastFailureTime).TotalMilliseconds > _resetTimeoutMs)
            {
                _state = CircuitState.HalfOpen;
                return true; // Try once
            }
            return false;
        }
        return _state == CircuitState.HalfOpen;
    }
    
    public void RecordSuccess()
    {
        if (_state == CircuitState.HalfOpen)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
        }
    }
    
    public void RecordFailure(string error)
    {
        _lastFailureTime = DateTime.UtcNow;
        _failureCount++;
        if (_state == CircuitState.HalfOpen || _failureCount >= _failureThreshold)
            _state = CircuitState.Open;
    }
}
```

**Safe Execution:**

```csharp
private async Task ExecuteModuleSafe(ModuleEntry entry, ISimulationView view, float dt)
{
    if (entry.CircuitBreaker != null && !entry.CircuitBreaker.CanRun())
        return; // Skip
    
    try
    {
        int timeout = entry.Policy.MaxExpectedRuntimeMs;
        using var cts = new CancellationTokenSource(timeout);
        
        var tickTask = Task.Run(() => entry.Module.Tick(view, dt), cts.Token);
        var completedTask = await Task.WhenAny(tickTask, Task.Delay(timeout));
        
        if (completedTask == tickTask)
        {
            await tickTask; // Propagate exceptions
            entry.CircuitBreaker?.RecordSuccess();
        }
        else
        {
            // TIMEOUT: Task is zombie, mark failure
            entry.CircuitBreaker?.RecordFailure("Timeout");
            LogError($"Module {entry.Module.Name} timed out ({timeout}ms)");
        }
    }
    catch (Exception ex)
    {
        entry.CircuitBreaker?.RecordFailure(ex.Message);
        LogError($"Module {entry.Module.Name} crashed: {ex}");
    }
}
```

**Configuration:**

```csharp
// ModuleEntry additions
public class ModuleEntry
{
    // Existing...
    
    public ModuleCircuitBreaker? CircuitBreaker { get; set; }
}

// Registration
public void RegisterModule(IModule module)
{
    var entry = new ModuleEntry
    {
        Module = module,
        CircuitBreaker = new ModuleCircuitBreaker(
            module.Policy.FailureThreshold,
            module.Policy.CircuitResetTimeoutMs
        )
    };
    _modules.Add(entry);
}
```

---

## Chapter 5: Flexible Execution Modes

### 5.1 Specification

**Objective:** Replace binary Fast/Slow tier with explicit execution policy configuration.

**Requirements:**
- Modules declare: RunMode (Synchronous/FrameSynced/Asynchronous)
- Modules declare: DataStrategy (Direct/GDB/SoD)
- Modules declare: Component/Event requirements
- Support arbitrary combinations (e.g., Async+GDB for heavy AI with full context)

**Why Needed:**
- **Expressiveness:** Physics needs Direct (main thread), Network needs FrameSynced+GDB, AI needs Async+SoD
- **Clarity:** No hidden "Tier" magic; everything explicit in policy
- **Flexibility:** Enables domain-specific optimizations

### 5.2 Design

**API Changes:**

```csharp
public struct ExecutionPolicy
{
    public RunMode Mode;              // How it runs
    public DataStrategy Strategy;     // What data structure
    public int TargetFrequencyHz;     // Scheduling frequency
    public int MaxExpectedRuntimeMs;  // Timeout
    public int FailureThreshold;      // Resilience config
    
    // Factory methods for common profiles
    public static ExecutionPolicy Synchronous() => new()
    {
        Mode = RunMode.Synchronous,
        Strategy = DataStrategy.Direct
    };
    
    public static ExecutionPolicy FastReplica() => new()
    {
        Mode = RunMode.FrameSynced,
        Strategy = DataStrategy.GDB,
        TargetFrequencyHz = 60,
        MaxExpectedRuntimeMs = 15
    };
    
    public static ExecutionPolicy SlowBackground(int hz) => new()
    {
        Mode = RunMode.Asynchronous,
        Strategy = DataStrategy.SoD,
        TargetFrequencyHz = hz,
        MaxExpectedRuntimeMs = 100,
        FailureThreshold = 5
    };
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
    GDB,     // Persistent replica
    SoD      // Pooled snapshot
}

// IModule changes
public interface IModule
{
    string Name { get; }
    ExecutionPolicy Policy { get; }  // Replaces Tier + UpdateFrequency
    void RegisterSystems(ISystemRegistry registry) { }
    void Tick(ISimulationView view, float deltaTime);
    
    // Reactive
    IReadOnlyList<Type>? WatchComponents { get; }
    IReadOnlyList<Type>? WatchEvents { get; }
}
```

**Grouping Logic:**

```csharp
private void AutoAssignProviders()
{
    var groups = _modules.GroupBy(m => new 
    { 
        m.Policy.Mode, 
        m.Policy.Strategy, 
        m.Policy.TargetFrequencyHz 
    });
    
    foreach (var group in groups)
    {
        switch (group.Key.Strategy)
        {
            case DataStrategy.Direct:
                // No provider needed (live world)
                break;
                
            case DataStrategy.GDB:
                // Single persistent replica shared by all in group
                var gdbProvider = new DoubleBufferProvider(...);
                foreach (var m in group) m.Provider = gdbProvider;
                break;
                
            case DataStrategy.SoD:
                if (group.Count() == 1)
                {
                    // OnDemandProvider
                    var m = group.First();
                    m.Provider = new OnDemandProvider(...);
                }
                else
                {
                    // SharedSnapshotProvider (Convoy)
                    var unionMask = CalculateUnionMask(group);
                    var sharedProvider = new SharedSnapshotProvider(..., unionMask, pool);
                    foreach (var m in group) m.Provider = sharedProvider;
                }
                break;
        }
    }
}
```

---

## Chapter 6: Entity Lifecycle Manager (ELM)

### 6.1 Specification

**Objective:** Coordinate atomic entity creation/destruction across multiple distributed modules.

**Requirements:**
- Support "Dark Construction": entity staged until all modules initialize it
- Support coordinated teardown: entity cleanup before deletion
- Use FDP's existing `EntityLifecycle` states (Constructing, Active, TearDown)
- Publish `ConstructionOrder`/`DestructionOrder` events
- Collect ACKs from participating modules
- Flip to Active/Destroy when all ACKs received

**Why Needed:**
- **Distributed Authority:** No single module knows everything about an entity
- **Consistency:** Prevents pop-in or uninitialized behavior
- **Cross-Machine:** Enables federation with coordinated spawn/despawn

**Constraints:**
- Requires event protocol between modules
- Modules must opt-in to lifecycle participation
- Default queries filter out Constructing/TearDown entities

### 6.2 Design

**Data Structures:**

```csharp
// Events (ModuleHost.Core/LifecycleEvents.cs)
[EventId(9001)]
public struct ConstructionOrder
{
    public Entity Entity;
    public int TypeId;
}

[EventId(9002)]
public struct ConstructionAck
{
    public Entity Entity;
    public int ModuleId;
}

[EventId(9003)]
public struct DestructionOrder
{
    public Entity Entity;
}

[EventId(9004)]
public struct DestructionAck
{
    public Entity Entity;
    public int ModuleId;
}

// ELM Module (ModuleHost.Core/ELM/EntityLifecycleModule.cs)
public class EntityLifecycleModule : IModule
{
    public string Name => "EntityLifecycleManager";
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    private readonly Dictionary<Entity, EntityConstructionState> _pendingConstruction = new();
    private readonly Dictionary<Entity, EntityDestructionState> _pendingDestruction = new();
    private readonly int _requiredAckMask; // Bitmask of participating modules
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new LifecycleSystem(this));
    }
    
    public void Tick(ISimulationView view, float deltaTime) { }
}

[UpdateInPhase(SystemPhase.BeforeSync)]
public class LifecycleSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var repo = view as EntityRepository;
        var cmd = view.GetCommandBuffer();
        
        // Process construction ACKs
        foreach (var evt in view.GetEvents<ConstructionAck>())
        {
            if (_manager.TryGetPendingConstruction(evt.Entity, out var state))
            {
                state.AckMask |= (1 << evt.ModuleId);
                if (state.AckMask == _manager.RequiredMask)
                {
                    // All ACKs received: Activate
                    cmd.SetLifecycleState(evt.Entity, EntityLifecycle.Active);
                    _manager.RemovePending(evt.Entity);
                }
            }
        }
        
        // Process destruction ACKs (similar logic)
        // ...
    }
}
```

**Integration:**

- `EntityQuery` by default filters `IsActive == true`
- Systems that need to see staging use `.WithLifecycle(Constructing)` override
- Example: PhysicsSetupSystem listens for `ConstructionOrder`, initializes rigid body, sends ACK

---

## Chapter 7: Network Gateway (DDS/SST Integration)

### 7.1 Specification

**Objective:** Bridge FDP EntityRepository with external DDS network descriptors.

**Requirements:**
- **Ingress:** DDS Descriptors → FDP Components (polling, not callbacks)
- **Egress:** FDP Components → DDS Descriptors (owned entities only)
- **Translator Pattern:** Decouple rich descriptors from atomic components
- **Ownership:** Respect "only owner writes" rule from `bdc-sst-rules.md`
- **EntityMaster:** Links to FDP entity lifecycle

**Why Needed:**
- Interoperability with federation
- Distributed simulation across nodes
- Legacy system integration

### 7.2 Design

**Architecture:**

```csharp
// Translator Interface
public interface IDescriptorTranslator
{
    string TopicName { get; }
    
    // Ingress: DDS → FDP (Phase 1: Input)
    void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view);
    
    // Egress: FDP → DDS (Phase 4: Export)
    void ScanAndPublish(ISimulationView view, IDataWriter writer);
}

// Example Translator
public class EntityStateTranslator : IDescriptorTranslator
{
    public string TopicName => "SST.EntityState";
    
    public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
    {
        foreach (var sample in reader.TakeSamples())
        {
            var desc = (EntityStateDescriptor)sample;
            var entity = MapNetworkIdToLocal(desc.EntityId);
            
            // Map descriptor → multiple components
            cmd.SetComponent(entity, new Position { Value = desc.Location });
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
            cmd.SetComponent(entity, new NetworkTarget { Value = desc.Location, Timestamp = desc.Time });
        }
    }
    
    public void ScanAndPublish(ISimulationView view, IDataWriter writer)
    {
        // Query owned entities
        var query = view.Query().With<Position>().With<NetworkOwnership>().OwnershipIs(local).Build();
        
        foreach (var entity in query)
        {
            var pos = view.GetComponentRO<Position>(entity);
            var vel = view.GetComponentRO<Velocity>(entity);
            
            var desc = new EntityStateDescriptor
            {
                EntityId = MapLocalToNetworkId(entity),
                Location = pos.Value,
                Velocity = vel.Value
            };
            
            writer.Write(desc);
        }
    }
}

// SST Module
public class SSTModule : IModule
{
    public string Name => "SSTGateway";
    public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
    
    private readonly List<IDescriptorTranslator> _translators = new();
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new NetworkIngestSystem(_translators));
        registry.RegisterSystem(new NetworkSyncSystem(_translators));
    }
}

[UpdateInPhase(SystemPhase.Input)]
public class NetworkIngestSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        foreach (var translator in _translators)
        {
            if (_readers.TryGetValue(translator.TopicName, out var reader))
                translator.PollIngress(reader, cmd, view);
        }
    }
}
```

---

## Chapter 8: Geographic Transform Services

### 8.1 Specification

**Objective:** Convert between FDP's Cartesian physics and network's Geodetic coordinates.

**Requirements:**
- Transform Lat/Lon/Alt ↔ Local XYZ via tangent plane origin
- Support dead reckoning/smoothing for remote entities
- Maintain dual representation: `Position` (physics) + `PositionGeodetic` (network)
- Authority check: only sync direction we own

### 8.2 Design

```csharp
// Service Interface
public interface IGeographicTransform
{
    void SetOrigin(double lat, double lon, double alt);
    Vector3 ToCartesian(double lat, double lon, double alt);
    (double lat, double lon, double alt) ToGeodetic(Vector3 localPos);
}

// System (PostSimulation phase)
[UpdateInPhase(SystemPhase.PostSimulation)]
public class CoordinateTransformSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Outbound: Physics → Geodetic (for locally owned entities)
        var outbound = view.Query()
            .With<Position>()
            .With<PositionGeodetic>()
            .WithOwned<Position>()
            .Build();
        
        foreach (var entity in outbound)
        {
            var localPos = view.GetComponentRO<Position>(entity);
            var geoPos = view.GetManagedComponentRO<PositionGeodetic>(entity);
            
            var (lat, lon, alt) = _geo.ToGeodetic(localPos.Value);
            
            if (Math.Abs(geoPos.Latitude - lat) > 1e-6)
            {
                var newGeo = geoPos with { Latitude = lat, Longitude = lon, Altitude = alt };
                cmd.SetManagedComponent(entity, newGeo);
            }
        }
    }
}

// Smoothing System (Input phase)
[UpdateInPhase(SystemPhase.Input)]
public class NetworkSmoothingSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query()
            .With<Position>()
            .With<NetworkTarget>()
            .WithoutOwned<Position>()
            .Build();
        
        foreach (var entity in query)
        {
            var currentPos = view.GetComponentRO<Position>(entity);
            var target = view.GetComponentRO<NetworkTarget>(entity);
            
            float t = Math.Clamp(deltaTime * 10.0f, 0f, 1f);
            Vector3 newPos = Vector3.Lerp(currentPos.Value, target.Value, t);
            
            if (view is EntityRepository repo)
            {
                // Direct write (main thread optimization)
                ref var mutablePos = ref repo.GetComponentRW<Position>(entity);
                mutablePos.Value = newPos;
            }
        }
    }
}
```

---

## Implementation Roadmap

### Phase 1: Foundation (Weeks 1-2)
- Implement Non-Blocking Execution
- Implement Reactive Scheduling
- Test with existing examples (CarKinem)

### Phase 2: Optimization (Weeks 3-4)
- Implement Convoy & Pooling
- Implement Resilience & Safety
- Refactor to Flexible Execution Modes

### Phase 3: Services (Weeks 5-6)
- Implement Entity Lifecycle Manager
- Create ELM integration tests

### Phase 4: Integration (Weeks 7-8)
- Implement Network Gateway Core
- Implement Geographic Transform
- End-to-end federation testing

---

## Testing Strategy

### Unit Tests
- Provider state machines (acquire/release)
- Circuit breaker transitions
- Convoy grouping logic
- Event/component dirty tracking

### Integration Tests
- Non-blocking with slow modules
- Reactive wake on event/component change
- Convoy memory sharing
- Circuit breaker recovery

### Performance Tests
- Frame time stability with long-running modules
- Memory usage with convoy pooling
- GC pressure reduction
- Reactive scheduling latency

---

## Migration Path

### Backward Compatibility
- Keep `ModuleTier` enum for legacy code
- Auto-convert Tier → ExecutionPolicy
- Deprecation warnings for old API

### Breaking Changes
- `IModule.Tier` → `IModule.Policy` (API change)
- `UpdateFrequency` → `Policy.TargetFrequencyHz`
- Providers no longer auto-created (explicit policy required)

---

## Appendix: Key Decisions

1. **Non-Blocking:** Use Task-based, not triple-buffer array swaps → Simpler, leverages .NET scheduler
2. **Reactive:** Coarse-grained component tracking (table-level) → Avoids per-entity overhead
3. **Convoy:** Auto-grouping by policy → Zero configuration for users
4. **Resilience:** Circuit breaker, not task cancellation → Safe in managed environment
5. **ELM:** Event-based ACK protocol → Decoupled, supports cross-machine
6. **Network:** Polling, not callbacks → Deterministic, better for ECS batching
7. **Geo:** Dual representation → Preserves physics precision while supporting network

---

**End of Design Document**
