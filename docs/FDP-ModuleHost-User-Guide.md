# FDP Kernel & ModuleHost User Guide

**Version:** 2.0  
**Date:** 2026-01-07  
**Audience:** Developers building high-performance distributed simulations  

---

## Table of Contents

1. [Core Principles](#core-principles)
2. [Entity Component System (ECS)](#entity-component-system-ecs)
3. [Systems & Scheduling](#systems--scheduling)
4. [Modules & ModuleHost](#modules--modulehost)
5. [Event Bus](#event-bus)
6. [Simulation Views & Execution Modes](#simulation-views--execution-modes)
7. [Flight Recorder & Deterministic Replay](#flight-recorder--deterministic-replay)
8. [Network Integration](#network-integration)
9. [Best Practices](#best-practices)
10. [Common Patterns](#common-patterns)
11. [Anti-Patterns to Avoid](#anti-patterns-to-avoid)

---

## Core Principles

### Data-Oriented Design (DOD)

FDP is built on **Data-Oriented Design**, not Object-Oriented Design. This means:

1. **Data and behavior are separate**
   - Components hold data (structs)
   - Systems hold behavior (classes with logic)

2. **Systems operate on data, not instances**
   - Systems query components
   - Systems never reference other systems
   - Communication happens through data (components, singletons, events)

3. **Cache-friendly access patterns**
   - Components stored in contiguous arrays
   - Parallel iteration over entities
   - Minimal pointer chasing

**Example - The Wrong Way (OOP):**
```csharp
// ❌ ANTI-PATTERN: Systems referencing systems
public class CarKinematicsSystem
{
    private SpatialHashSystem _spatialSystem; // DON'T DO THIS!
    
    void OnUpdate()
    {
        var grid = _spatialSystem.Grid; // Tight coupling!
    }
}
```

**Example - The Right Way (DOD):**
```csharp
// ✅ CORRECT: Systems communicate via data
public class SpatialHashSystem
{
    void OnUpdate()
    {
        // Build grid, publish as singleton
        World.SetSingleton(new SpatialGridData { Grid = _grid });
    }
}

public class CarKinematicsSystem
{
    void OnUpdate()
    {
        // Read singleton (data-driven dependency)
        var gridData = World.GetSingleton<SpatialGridData>();
        // Use gridData.Grid...
    }
}
```

---

## Entity Component System (ECS)

### What is an Entity?

An **Entity** is a lightweight identifier (ID + Generation) that groups components together.

```csharp
public struct Entity
{
    public int Id;         // Index into entity array
    public int Generation; // Prevents stale references
}
```

**Key Points:**
- Entities are just IDs, not objects
- Generation prevents reusing stale entity references
- Always store **full Entity handles**, not just IDs

### Components

**Components are pure data structs:**

#### 1. Unmanaged Components (Preferred)

```csharp
// ✅ GOOD: Blittable struct
[StructLayout(LayoutKind.Sequential)]
public struct VehicleState
{
    public Vector2 Position;
    public Vector2 Forward;
    public float Speed;
    // No methods, no logic!
}
```

**Characteristics:**
- Must be `unmanaged` types (no managed references)
- Stored in NativeArrays (off-heap, cache-friendly)
- Can be safely accessed in parallel
- Copied bitwise (shallow copy)
- **Fast snapshotting** for replays and background threads

#### 2. Managed Components (Use with Caution)

**⚠️ CRITICAL: Immutability Requirement**

Managed components **MUST** be **immutable records** if they will be used in snapshots (for Flight Recorder, Network, or Background Modules).

```csharp
// ✅ CORRECT: Immutable record
public record AIBehaviorTree
{
    public required BehaviorNode RootNode { get; init; }
    public required int CurrentNodeId { get; init; }
    
    // With-expressions create copies
    public AIBehaviorTree WithCurrentNode(int nodeId) => 
        this with { CurrentNodeId = nodeId };
}
```

**Why Immutability?**
- **Shallow Copy Safety:** Snapshots use shallow copy (performance)
- **Thread Safety:** Background modules read snapshots; mutable state = torn reads
- **Snapshot Integrity:** Prevents accidental state mutation in replicas

**Example of Problems with Mutable State:**
```csharp
// ❌ WRONG: Mutable class
public class AIBehaviorTree
{
    public BehaviorNode RootNode; // Mutable!
    
    public void SetNode(BehaviorNode node)
    {
        RootNode = node; // Main thread modifies this
    }
}

// What happens:
// Frame 1: Main thread creates snapshot (shallow copy)
// Frame 2: Background AI module reads snapshot
// Frame 2: Main thread modifies RootNode
// Result: AI module sees torn/inconsistent state!
```

#### 3. Transient (Mutable) Managed Components

**When Can You Use Mutable Components?**

You **CAN** use mutable managed components IF:
1. They are **main-thread only** (never accessed by background modules)
2. They are marked as **transient** (excluded from snapshots)
3. They are used for UI, debug visualization, or temporary caches

**Marking Components as Transient:**

**Option A: Attribute**
```csharp
[TransientComponent]
public class UIRenderCache
{
    public Dictionary<int, Texture> Cache = new(); // Safe: main-thread only
}
```

**Option B: Registration Flag**
```csharp
repository.RegisterManagedComponent<UIRenderCache>(snapshotable: false);
```

**How Transient Components Work:**
- **Excluded from Snapshots:** `SyncFrom()` without explicit mask uses `GetSnapshotableMask()` which excludes transient components
- **Never Copied to Background Worlds:** Flight Recorder, AI modules, Network Gateway never see them
- **Main Thread Only:** Only accessible by Synchronous modules

**Transient Component Use Cases:**
- `UIRenderCache` - Heavy dictionaries for rendering
- `DebugVisualization` - Editor-only gizmos
- `TempCalculationBuffer` - Intermediate computation scratch space
- `EditorSelection` - UI state that never needs replay

#### 4. Singleton Components (Global State)

```csharp
public struct SpatialGridData
{
    public SpatialHashGrid Grid;
}

// Usage:
World.SetSingleton(new SpatialGridData { Grid = _grid });
var data = World.GetSingleton<SpatialGridData>();
```

### Component Registration

```csharp
var repository = new EntityRepository();

// Register unmanaged components
repository.RegisterComponent<Position>();
repository.RegisterComponent<Velocity>();

// Register immutable managed components
repository.RegisterManagedComponent<AIBehaviorTree>();

// Register transient managed components (excluded from snapshots)
repository.RegisterManagedComponent<UIRenderCache>(snapshotable: false);
```

### NativeArrays for Static Data

**Use NativeArrays for read-mostly lookup tables:**

```csharp
public struct VehicleStats
{
    public float MaxSpeed;
    public float Acceleration;
    public float TurnRate;
}

public class VehicleSystem : ComponentSystem
{
    private NativeArray<VehicleStats> _vehicleTypes;
    
    protected override void OnCreate()
    {
        // Allocate off-heap, persistent
        _vehicleTypes = new NativeArray<VehicleStats>(100, Allocator.Persistent);
        
        // Initialize lookup table
        _vehicleTypes[0] = new VehicleStats { MaxSpeed = 50, ... }; // Sedan
        _vehicleTypes[1] = new VehicleStats { MaxSpeed = 30, ... }; // Truck
        // etc.
    }
    
    protected override void OnUpdate()
    {
        var query = World.Query().With<VehicleTypeId>().With<Velocity>().Build();
        
        foreach (var entity in query)
        {
            var typeId = World.GetComponent<VehicleTypeId>(entity);
            var stats = _vehicleTypes[typeId.Value]; // Fast array lookup
            
            // Use stats for simulation
        }
    }
    
    protected override void OnDestroy()
    {
        _vehicleTypes.Dispose(); // CRITICAL: Must dispose!
    }
}
```

**Benefits:**
- **Off-Heap:** No GC pressure
- **Cache-Friendly:** Contiguous memory
- **Thread-Safe:** Read-only access from multiple threads
- **Fast:** Direct array indexing

**When to Use:**
- Configuration tables (vehicle types, weapon stats, etc.)
- Static game data that doesn't change per entity
- **Don't use for:** Per-entity data (use components instead)

### ModuleHost Owns Frame Lifecycle

**IMPORTANT:** If you're using **ModuleHostKernel**, you don't need to manually call `Tick()` or `SwapBuffers()`.

**Simple Game Loop with ModuleHost:**

```csharp
// Setup (once)
var repository = new EntityRepository();
var moduleHost = new ModuleHostKernel(repository);
moduleHost.RegisterModule(new PhysicsModule());
moduleHost.RegisterModule(new AIModule());
moduleHost.Initialize();

// Game loop
while (running)
{
    float deltaTime = GetDeltaTime();
    
    // ONE CALL - ModuleHost handles everything:
    // - repository.Tick()
    // - Input phase
    // - bus.SwapBuffers()
    // - All other phases
    moduleHost.Update(deltaTime);
    
    // Render...
}
```

**Why This Matters:**
- ✅ **Simplicity:** Game loop is just one call
- ✅ **Correct Ordering:** Tick → Input → Swap → Simulation is guaranteed
- ✅ **No Errors:** Can't forget Tick() or SwapBuffers() or get ordering wrong

### Manual Repository Management (Without ModuleHost)

**⚠️ Advanced Topic:** Only needed if you're NOT using ModuleHostKernel.

If you're using a raw `EntityRepository` without ModuleHost, you **must** manually manage the frame lifecycle. This requires understanding and correctly calling several critical operations.

#### The Required Manual Loop

```csharp
public class GameSimulation
{
    private EntityRepository _repository;
    private PhysicsSystem _physicsSystem;
    private AISystem _aiSystem;
    
    public void Update(float deltaTime)
    {
        // 1. TICK THE REPOSITORY
        _repository.Tick();
        
        // 2. PROCESS INPUT
        ProcessInput();
        
        // 3. SWAP EVENT BUFFERS
        _repository.Bus.SwapBuffers();
        
        // 4. RUN SIMULATION SYSTEMS
        _physicsSystem.OnUpdate();
        _aiSystem.OnUpdate();
        
        // 5. FLUSH COMMAND BUFFERS (if using deferred commands)
        FlushCommandBuffers();
    }
}
```

#### What Each Step Does

##### 1. Repository.Tick() - Advance Global Version

**What it does:**
```csharp
_repository.Tick();
// Internally: _globalVersion++;
```

**Purpose:**
- Increments the global frame counter (`GlobalVersion`)
- Timestamps all component modifications with current version
- Enables change detection for:
  - **Flight Recorder** - Tracks which components changed this frame
  - **Reactive Systems** - Wake modules when specific components change
  - **Network Sync** - Identify dirty entities to replicate
  - **Delta Compression** - Only serialize what changed

**When to call:**
- **First thing** in your update loop
- **Before** any component modifications
- **Exactly once** per frame

**What happens if you forget:**

```csharp
// ❌ BAD - Missing Tick()
void Update()
{
    // Missing: _repository.Tick();
    
    var entity = _repository.CreateEntity();
    _repository.SetComponent(entity, new Position { X = 10 });
    // Component stamped with Version 1 (initial)
    
    flightRecorder.CaptureFrame(sinceTick: 1);
    // Query: "Components changed since version 1"
    // Result: NOTHING (version still 1!)
    // Replay will be broken - entities appear frozen!
}

// ✅ GOOD - Tick() called
void Update()
{
    _repository.Tick(); // Version becomes 2
    
    var entity = _repository.CreateEntity();
    _repository.SetComponent(entity, new Position { X = 10 });
    // Component stamped with Version 2
    
    flightRecorder.CaptureFrame(sinceTick: 1);
    // Query: "Components changed since version 1"
    // Result: Position component found!
    // Replay works correctly
}
```

##### 2. Process Input

**What it does:**
- Read keyboard, mouse, gamepad input
- Publish input events **directly to the bus**

**Example:**
```csharp
void ProcessInput()
{
    if (Keyboard.IsKeyPressed(Keys.Space))
    {
        // Publish directly to bus (not command buffer!)
        _repository.Bus.Publish(new JumpCommand
        {
            Entity = _playerEntity,
            Force = 10f
        });
    }
}
```

**Why direct publish?**
- Input needs **zero latency** - visible same frame
- Bypass command buffers (which delay by 1 frame)
- Events go directly to PENDING buffer

##### 3. Bus.SwapBuffers() - Make Events Visible

**What it does:**
```csharp
_repository.Bus.SwapBuffers();
// Internally:
// - PENDING buffer becomes CURRENT
// - Old CURRENT buffer cleared and becomes new PENDING
// - Double-buffering flip
```

**Purpose:**
- Makes input events visible to simulation systems
- Clears previous frame's events
- Implements double-buffering pattern for thread-safety

**The Double-Buffer Pattern:**

```
Before SwapBuffers():
  ┌─────────────┐
  │ PENDING     │ ← Input writes here (JumpCommand)
  │ [Jump]      │
  └─────────────┘
  
  ┌─────────────┐
  │ CURRENT     │ ← Systems read from here (empty from last frame)
  │ []          │
  └─────────────┘

After SwapBuffers():
  ┌─────────────┐
  │ PENDING     │ ← Now empty, ready for next frame
  │ []          │
  └─────────────┘
  
  ┌─────────────┐
  │ CURRENT     │ ← JumpCommand now visible!
  │ [Jump]      │
  └─────────────┘
```

**When to call:**
- **After** input processing
- **Before** simulation systems run
- **Exactly once** per frame

**Critical Ordering:**

```csharp
// ✅ CORRECT ORDER
ProcessInput();           // Writes to PENDING
_repository.Bus.SwapBuffers();  // PENDING → CURRENT
_physicsSystem.OnUpdate(); // Reads from CURRENT (sees input!)

// ❌ WRONG ORDER - Events delayed by 1 frame
_repository.Bus.SwapBuffers();  // Swap first (wrong!)
ProcessInput();           // Writes to PENDING
_physicsSystem.OnUpdate(); // Reads from CURRENT (doesn't see input yet!)
// Player presses jump at Frame N, but character jumps at Frame N+1
```

**What happens if you forget:**

```csharp
// ❌ BAD - Missing SwapBuffers()
void Update()
{
    _repository.Tick();
    ProcessInput();
    
    // Missing: _repository.Bus.SwapBuffers();
    
    _physicsSystem.OnUpdate(); // Reads CURRENT buffer (still has old events!)
    // Input events stuck in PENDING buffer
    // Never seen by systems
    // Game doesn't respond to input!
}
```

##### 4. Run Systems

**What it does:**
- Execute your simulation logic
- Systems query entities and modify components
- Systems consume events from CURRENT buffer

**Example:**
```csharp
public class JumpSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Consume events from CURRENT buffer (visible after SwapBuffers)
        var jumpCommands = World.GetEvents<JumpCommand>();
        
        foreach (var cmd in jumpCommands)
        {
            var vel = World.GetComponent<Velocity>(cmd.Entity);
            vel.Y = cmd.Force; // Apply jump
            World.SetComponent(cmd.Entity, vel);
        }
    }
}
```

##### 5. Flush Command Buffers (Optional)

**What it does:**
- Apply deferred component changes
- Publish deferred events to PENDING buffer

**When needed:**
- If systems use command buffers for thread-safe deferred writes
- Not needed for direct `SetComponent()` calls

#### Complete Manual Example

```csharp
public class StandaloneSimulation
{
    private EntityRepository _repository;
    private FdpEventBus _bus;
    private List<ComponentSystem> _systems;
    
    public void Initialize()
    {
        _repository = new EntityRepository();
        _bus = _repository.Bus;
        
        // Register systems
        _systems = new List<ComponentSystem>
        {
            new InputSystem(),
            new PhysicsSystem(),
            new AISystem(),
            new RenderSystem()
        };
        
        foreach (var system in _systems)
        {
            system.OnCreate();
        }
    }
    
    public void Update(float deltaTime)
    {
        // 1. TICK - Advance version counter
        _repository.Tick();
        // Now: GlobalVersion = N
        
        // 2. INPUT - Process and publish input events
        ProcessInput();
        // Events written to PENDING buffer
        
        // 3. SWAP - Make input visible
        _bus.SwapBuffers();
        // PENDING → CURRENT (input events now visible)
        // Old CURRENT cleared → new PENDING
        
        // 4. SIMULATION - Run all systems
        foreach (var system in _systems)
        {
            system.World = _repository; // Inject world reference
            system.DeltaTime = deltaTime;
            system.OnUpdate();
        }
        // Systems read from CURRENT buffer
        // Systems write components (stamped with Version N)
    }
    
    private void ProcessInput()
    {
        // Poll input devices
        var keyboard = GetKeyboardState();
        
        if (keyboard.IsKeyPressed(Keys.W))
        {
            // Direct publish to bus (not command buffer!)
            _bus.Publish(new MoveCommand
            {
                Direction = Vector3.Forward,
                Speed = 5f
            });
        }
    }
}
```

#### Common Mistakes

**❌ Forgetting Tick():**
- Change detection breaks
- Flight Recorder captures empty frames
- Replays show frozen simulation

**❌ Forgetting SwapBuffers():**
- Input events never visible
- Game doesn't respond to player
- Events accumulate in PENDING buffer

**❌ Wrong Order (Swap before Input):**
- Input delayed by 1 frame
- "Input lag" sensation
- Player presses button at Frame N, effect at Frame N+1

**❌ Calling Tick() Multiple Times:**
- Version increments too fast
- Change detection becomes unreliable
- Call exactly once per frame!

**❌ Calling SwapBuffers() Multiple Times:**
- Events cleared prematurely
- Systems might miss events
- Call exactly once per frame!

#### Why ModuleHost is Recommended

All of this manual orchestration goes away with ModuleHost:

```csharp
// Instead of 30+ lines of lifecycle management:
_moduleHost.Update(deltaTime);

// ModuleHost automatically:
// - Calls Tick()
// - Runs Input phase systems
// - Calls SwapBuffers()
// - Runs all other phases in correct order
// - You can't forget or mis-order anything!
```

**Use Manual Management Only If:**
- You need extremely custom control
- You're integrating FDP into existing engine
- You're building a framework on top of FDP
- You understand the double-buffering and versioning implications

**Otherwise: Use ModuleHost!**

---

## Systems & Scheduling

### What is a System?

A **System** is a class containing logic that operates on entities with specific components.

```csharp
using Fdp.Kernel;

public class MovementSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        float dt = DeltaTime;
        
        var query = World.Query()
            .With<Position>()
            .With<Velocity>()
            .Build();
        
        foreach (var entity in query)
        {
            var pos = World.GetComponent<Position>(entity);
            var vel = World.GetComponent<Velocity>(entity);
            
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
            pos.Z += vel.Z * dt;
            
            World.SetComponent(entity, pos);
        }
    }
}
```

### System Groups

**System Groups** define execution phases and ordering within a frame:

```
Frame Start
    ↓
Input Phase (SystemPhase.Input)
    ├─ InputSystem
    ├─ NetworkIngestSystem
    └─ ...
    ↓
BeforeSync Phase (SystemPhase.BeforeSync)
    ├─ EntityLifecycleSystem
    └─ ...
    ↓
Simulation Phase (SystemPhase.Simulation)
    ├─ PhysicsSystem
    ├─ AILogicSystem
    └─ ...
    ↓
PostSimulation Phase (SystemPhase.PostSimulation)
    ├─ AnimationSystem
    └─ CoordinateTransformSystem
    ↓
Export Phase (SystemPhase.Export)
    ├─ NetworkSyncSystem
    └─ ...
    ↓
Frame End
```

**Purpose of Each Phase:**

- **Input:** Poll external sources (user input, network packets)
- **BeforeSync:** Lifecycle management, pre-simulation setup
- **Simulation:** Main game logic, physics
- **PostSimulation:** Post-processing, animation blending
- **Export:** Write to network, file output

### System Scheduling Attributes

```csharp
using Fdp.Kernel;

// Run in specific phase
[UpdateInPhase(SystemPhase.Simulation)]
public class PhysicsSystem : ComponentSystem { }

// Run before another system
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateBefore(typeof(RenderSystem))]
public class CameraSystem : ComponentSystem { }

// Run after another system
[UpdateInPhase(SystemPhase.Input)]
[UpdateAfter(typeof(InputSystem))]
public class PlayerControllerSystem : ComponentSystem { }

// Multiple constraints
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SpatialHashSystem))]
[UpdateBefore(typeof(CollisionSystem))]
public class MovementSystem : ComponentSystem { }
```

### Component Systems vs Module Systems

**Component Systems:**
- Run on **main thread**
- Access **live EntityRepository** directly
- Execute **every frame** (or based on group)
- Use for: Physics, input, tight loops

**Module Systems (IModuleSystem):**
- Run **within a module's context** (potentially background thread)
- Access **snapshot** (ISimulationView)
- Execute based on **module's ExecutionPolicy**
- Use for: AI, pathfinding, analytics

**Example Module System:**

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class BehaviorTreeSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query().With<AIAgent>().With<BehaviorTree>().Build();
        
        foreach (var entity in query)
        {
            var agent = view.GetComponentRO<AIAgent>(entity);
            var tree = view.GetManagedComponentRO<BehaviorTree>(entity);
            
            // Execute AI logic
            tree.Update(agent, deltaTime);
        }
    }
}
```

**Where Module Systems Run:**
- **Synchronous Module:** Main thread, same as Component Systems
- **FrameSynced Module:** Background thread, but main thread **waits** for completion
- **Asynchronous Module:** Background thread, main thread **doesn't wait**

**Why Use Module Systems?**
- **Execution Control:** Run at 10Hz instead of 60Hz
- **Thread Isolation:** Heavy computation doesn't block main thread
- **Snapshot Safety:** Guaranteed consistent view of world state

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

---

## Simulation Views & Execution Modes (Advanced)

### Understanding the "World" Nomenclature

ModuleHost uses the terms **"World A"**, **"World B"**, and **"World C"** to describe different instances of the simulation state:

**World A - "Live World" (Main Thread)**
- The **authoritative** simulation state
- Updated every frame on the main thread
- Synchronous modules access this directly
- All entity mutations ultimately apply here
- **One instance** per simulation

**World B - "Fast Replica" (Background Thread)**
- A **persistent replica** synced every frame
- Uses GDB (Generalized Double Buffer) strategy
- FrameSynced modules read this
- **Shared by all FrameSynced modules**
- Low-latency (0.5-1ms sync time)
- Survives across frames

**World C - "Slow Snapshot" (Background Thread)**
- **On-demand snapshots** pooled and reused
- Uses SoD (Snapshot on Demand) strategy
- Asynchronous modules read this
- **One per module or convoy**
- Higher latency but memory efficient
- Created when module runs, destroyed when module completes

**Visual Representation:**

```
Main Thread              Background Threads
─────────────────────────────────────────────
┌───────────┐            
│ World A   │            
│ (Live)    │            
│           │ ◄─── Synchronous modules access directly
│ 100k      │      
│ entities  │            
└─────┬─────┘            
      │                  
      │ SyncFrom()       
      ↓                  
┌───────────┐            
│ World B   │ ◄────────── FrameSynced modules (Recorder, Network)
│ (GDB)     │            
│ Persistent│            Every frame: sync diffs from World A
│ 100k      │            
│ entities  │            
└─────┬─────┘            
      │                  
      │ SyncFrom()       
      ↓                  
┌───────────┐            
│ World C   │ ◄────────── Async modules (AI, Pathfinding)
│ (SoD)     │            
│ Pooled    │            Created on-demand, held across frames
│ 20k       │            (Only components module needs)
│ entities  │            
└───────────┘            
```

**Key Differences:**

| Aspect | World A (Live) | World B (GDB) | World C (SoD) |
|--------|---------------|---------------|---------------|
| **Lifetime** | Permanent | Permanent | Temporary (leased) |
| **Sync Frequency** | N/A (original) | Every frame | When module runs |
| **Contents** | All components | All snapshotable | Union of module requirements |
| **Mutability** | Mutable | Read-only | Read-only |
| **Thread** | Main only | Background | Background |
| **Who Uses** | Synchronous modules | FrameSynced modules | Async modules |

### Data Strategies Explained

#### Direct Access (World A)

**When to Use:**
- Physics systems
- Input handling
- Rendering preparation
- Anything that MUST run on main thread every frame

**Characteristics:**
```csharp
Policy = ExecutionPolicy.Synchronous(); // Implies Direct strategy

public void Tick(ISimulationView view, float deltaTime)
{
    // 'view' is actually the live EntityRepository
    // Direct read/write access
    // Zero overhead
    // Runs on main thread
}
```

**Pros:**
- ✅ Zero overhead (no snapshot)
- ✅ Immediate visibility of changes
- ✅ Can mutate state directly

**Cons:**
- ❌ Blocks main thread
- ❌ Limited to 16ms execution time (60 FPS)

#### GDB (Generalized Double Buffer) - World B

**When to Use:**
- Flight Recorder (needs consistent snapshots every frame)
- Network sync (frequent updates, low latency)
- Analytics that must run every frame

**Characteristics:**
```csharp
Policy = ExecutionPolicy.FastReplica(); // Implies GDB strategy

// How it works internally:
class DoubleBufferProvider
{
    private EntityRepository _replicaA;
    private EntityRepository _replicaB;
    private int _currentIndex;
    
    public void Update()
    {
        // Swap buffers
        _currentIndex = 1 - _currentIndex;
        
        // Sync new "current" from live world
        GetCurrent().SyncFrom(_liveWorld, mask: AllSnapshotable);
    }
    
    public ISimulationView AcquireView()
    {
        return GetCurrent(); // Returns synced replica
    }
}
```

**Data Flow:**
```
Frame N:
  World A changes → [buffer swap] → World B reads Frame N-1
  
Frame N+1:
  World A changes → [buffer swap] → World B reads Frame N
```

**Pros:**
- ✅ **Low latency:** 1-frame delay
- ✅ **Consistent:** Entire snapshot from same frame
- ✅ **Fast sync:** Only diffs copied (~0.5ms for 100k entities)
- ✅ **Persistent:** No allocation overhead

**Cons:**
- ❌ **Memory:** Full replica (2x for double buffer)
- ❌ **Sync cost:** Still paid every frame even if module doesn't run

**Performance:**
- Sync time: 0.5-1ms for 100k entities with 20 component types
- Memory: 2x live world size (double buffer)

#### SoD (Snapshot on Demand) - World C

**When to Use:**
- AI decision making (10 Hz)
- Pathfinding (irregular updates)
- Analytics (1 Hz or on-event)
- Anything that runs infrequently or spans multiple frames

**Characteristics:**
```csharp
Policy = ExecutionPolicy.SlowBackground(10); // Implies SoD strategy

// How it works internally:
class OnDemandProvider
{
    private SnapshotPool _pool;
    private BitMask256 _componentMask;
    
    public ISimulationView AcquireView()
    {
        // Get snapshot from pool (or create new)
        var snapshot = _pool.Get();
        
        // Sync ONLY components this module needs
        snapshot.SyncFrom(_liveWorld, _componentMask);
        
        return snapshot;
    }
    
    public void ReleaseView(ISimulationView view)
    {
        // Return to pool for reuse
        _pool.Return((EntityRepository)view);
    }
}
```

**Data Flow:**
```
Frame 1: Module triggers
  World A → Create World C snapshot
  Module starts on background thread
  Main thread continues
  
Frame 2: Module still running
  Module reads World C (frozen at Frame 1)
  World A continues evolving
  
Frame 3: Module completes
  Commands harvested from World C
  World C returned to pool
```

**Pros:**
- ✅ **Memory efficient:** Snapshot created only when needed
- ✅ **Selective sync:** Only components module needs
- ✅ **Convoy optimization:** Multiple modules share one snapshot
- ✅ **No frame blocking:** Main thread continues
- ✅ **Pooled:** Zero allocation in steady state

**Cons:**
- ❌ **Stale data:** Module sees world state from when it started
- ❌ **Variable latency:** Commands applied when module completes

**Performance:**
- Sync time: 0.1-0.5ms for subset of components
- Memory: 1x snapshot per module (or per convoy)
- Pool overhead: <0.01ms for Get/Return

### Convoy Pattern (Automatic Grouping)

**Problem:** 5 AI modules at 10 Hz would create 5 snapshots:

```
Frame where all 5 trigger:
  SyncFrom() × 5 = 2.5ms
  Memory: 5 × 100MB = 500MB
```

**Solution: Convoy Detection**

```csharp
// These modules automatically form a convoy:
var aiModule1 = new DecisionModule();
var aiModule2 = new PathfindingModule();
var aiModule3 = new PerceptionModule();

// All have:
// - TargetFrequencyHz = 10
// - RunMode = Asynchronous
// - DataStrategy = SoD

// ModuleHost creates ONE SharedSnapshotProvider:
var unionMask = Mask(Position, Velocity, Health, AIState, NavMesh);
var sharedSnapshot = new SharedSnapshotProvider(liveWorld, unionMask, pool);

// All 3 modules share it:
module1.Provider = sharedSnapshot;
module2.Provider = sharedSnapshot;
module3.Provider = sharedSnapshot;

// Result:
// - ONE SyncFrom() call = 0.5ms
// - ONE snapshot = 100MB
// - Reference counting ensures snapshot lives until slowest module completes
```

**Savings:**
- **Memory:** 80% reduction (500MB → 100MB)
- **Sync Time:** 80% reduction (2.5ms → 0.5ms)

### Execution Policy Parameters Explained

```csharp
public struct ExecutionPolicy
{
    public RunMode Mode;              // Threading model
    public DataStrategy Strategy;     // Which world to access
    public int TargetFrequencyHz;     // How often to run
    public int MaxExpectedRuntimeMs;  // Timeout threshold
    public int FailureThreshold;      // Circuit breaker limit
    public int CircuitResetTimeoutMs; // Recovery time
}
```

#### RunMode

**Synchronous:**
- Main thread execution
- Blocks frame
- Direct access to World A
- Use for: Physics, input, rendering prep

**FrameSynced:**
- Background thread execution
- Main thread **waits** for completion
- Accesses World B (GDB)
- Use for: Network sync, flight recorder

**Asynchronous:**
- Background thread execution
- Main thread **continues** (fire-and-forget)
- Accesses World C (SoD)
- Use for: AI, pathfinding, analytics

#### DataStrategy

**Direct:**
- No snapshot (World A)
- Only valid with Synchronous mode
- Zero overhead

**GDB (Generalized Double Buffer):**
- Persistent replica (World B)
- Synced every frame
- Low latency

**SoD (Snapshot on Demand):**
- Pooled snapshot (World C)
- Created when needed
- Memory efficient

#### TargetFrequencyHz

**Frequency in Hertz (updates per second):**

```csharp
TargetFrequencyHz = 60;  // Every frame (16.67ms)
TargetFrequencyHz = 30;  // Every 2 frames (33ms)
TargetFrequencyHz = 10;  // Every 6 frames (100ms)
TargetFrequencyHz = 1;   // Every 60 frames (1 second)
TargetFrequencyHz = 0;   // Reserved: "every frame" (same as 60)
```

**Actual scheduling:**
- ModuleHost converts Hz to frame count
- `frameInterval = 60 / TargetFrequencyHz`
- Module runs when `framesSinceLastRun >= frameInterval`

**Reactive override:**
- If `WatchEvents` or `WatchComponents` trigger, module runs **immediately**
- Frequency acts as maximum sleep time

#### MaxExpectedRuntimeMs

**Timeout for circuit breaker:**

```csharp
MaxExpectedRuntimeMs = 16;   // Must complete in 1 frame (Synchronous/FrameSynced)
MaxExpectedRuntimeMs = 100;  // Can take up to 100ms (Async at 10 Hz)
MaxExpectedRuntimeMs = 1000; // Slow analytics (1 second)
```

**What happens on timeout:**
1. Module marked as "timed out"
2. Task becomes "zombie" (can't be killed, but ignored)
3. Circuit breaker records failure
4. Error logged

**Choosing a value:**
- **Synchronous:** `MaxExpectedRuntimeMs = 16` (must fit in frame)
- **FrameSynced:** `MaxExpectedRuntimeMs = 15` (leaves margin)
- **Async:** `MaxExpectedRuntimeMs >= (1000 / FrequencyHz)`

#### FailureThreshold

**Number of consecutive failures before circuit opens:**

```csharp
FailureThreshold = 1;  // Immediate (Synchronous - any failure is fatal)
FailureThreshold = 3;  // Tolerant (FrameSynced - allow transient errors)
FailureThreshold = 5;  // Very tolerant (Async - background work can retry)
```

**Failure sources:**
- Timeout (module took longer than `MaxExpectedRuntimeMs`)
- Exception thrown in `Tick()`
- Command buffer errors

**Circuit breaker states:**
```
Closed (Normal)
  ↓ (failure count reaches threshold)
Open (Disabled)
  ↓ (after CircuitResetTimeoutMs)
HalfOpen (Testing)
  ↓ (success) → Closed
  ↓ (failure) → Open
```

#### CircuitResetTimeoutMs

**Time to wait before attempting recovery:**

```csharp
CircuitResetTimeoutMs = 1000;   // 1 second (aggressive retry)
CircuitResetTimeoutMs = 5000;   // 5 seconds (moderate)
CircuitResetTimeoutMs = 10000;  // 10 seconds (conservative)
```

**Recovery process:**
1. Circuit opens (module disabled)
2. Wait `CircuitResetTimeoutMs`
3. Transition to HalfOpen
4. Allow ONE execution attempt
5. Success → Closed (resume normal operation)
6. Failure → Open (wait again)

### Example Policies

```csharp
// Physics - Must run on main thread every frame
public ExecutionPolicy PhysicsPolicy => new()
{
    Mode = RunMode.Synchronous,
    Strategy = DataStrategy.Direct,
    TargetFrequencyHz = 60,
    MaxExpectedRuntimeMs = 16,
    FailureThreshold = 1,  // Any failure is fatal
    CircuitResetTimeoutMs = 1000
};

// Network Sync - Background but low latency
public ExecutionPolicy NetworkPolicy => new()
{
    Mode = RunMode.FrameSynced,
    Strategy = DataStrategy.GDB,
    TargetFrequencyHz = 60,
    MaxExpectedRuntimeMs = 15,
    FailureThreshold = 3,  // Allow transient network errors
    CircuitResetTimeoutMs = 5000
};

// AI - Slow background computation
public ExecutionPolicy AIPolicy => new()
{
    Mode = RunMode.Asynchronous,
    Strategy = DataStrategy.SoD,
    TargetFrequencyHz = 10,
    MaxExpectedRuntimeMs = 100,
    FailureThreshold = 5,  // Very tolerant
    CircuitResetTimeoutMs = 10000
};

// Analytics - Very slow, infrequent
public ExecutionPolicy AnalyticsPolicy => new()
{
    Mode = RunMode.Asynchronous,
    Strategy = DataStrategy.SoD,
    TargetFrequencyHz = 1,  // Once per second
    MaxExpectedRuntimeMs = 500,
    FailureThreshold = 10,  // Extremely tolerant
    CircuitResetTimeoutMs = 30000
};
```

---

## Event Bus

### What are Events?

**Events** are lightweight messages used for communication between systems and modules. They are the primary way to send commands or notifications.

**Events are:**
- **Frame-local:** Created each frame, cleared automatically
- **One-time consumption:** Single reader pattern
- **Type-safe:** Defined as structs/classes
- **Double-buffered:** Producers write to pending, consumers read from current

**Event Definition:**

```csharp
[EventId(1001)]
public struct DamageEvent
{
    public Entity Victim;
    public Entity Attacker;
    public float Amount;
    public Vector3 HitLocation;
}

[EventId(1002)]
public struct MoveCommand
{
    public Entity Target;
    public Vector3 Destination;
    public float Speed;
}
```

### Publishing Events

**From Main Thread (Direct):**
```csharp
// Zero-latency: Event visible same frame after SwapBuffers()
World.Bus.Publish(new DamageEvent
{
    Victim = entity,
    Attacker = shooter,
    Amount = 50f
});
```

**From System (Deferred via Command Buffer):**
```csharp
protected override void OnUpdate()
{
    var cmd = World.GetCommandBuffer();
    
    // Thread-safe, batched, applied next frame
    cmd.PublishEvent(new DamageEvent { ... });
}
```

**From Background Module (Deferred):**
```csharp
public void Tick(ISimulationView view, float deltaTime)
{
    var cmd = view.GetCommandBuffer();
    
    // Harvested when module completes
    cmd.PublishEvent(new MoveCommand { ... });
}
```

### Consuming Events

```csharp
public class CombatSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var events = World.GetEvents<DamageEvent>();
        
        foreach (var evt in events)
        {
            if (!World.IsAlive(evt.Victim))
                continue;
            
            var health = World.GetComponent<Health>(evt.Victim);
            health.Current -= evt.Amount;
            World.SetComponent(evt.Victim, health);
            
            if (health.Current <= 0)
            {
                World.DestroyEntity(evt.Victim);
            }
        }
    }
}
```

---

## Simulation Views & Execution Modes

### The Triple-Buffering Strategy

ModuleHost uses multiple "world" instances to support concurrent execution:

```
┌─────────────────────────────────────────────┐
│ WORLD A (Live - Main Thread)                │
│ - Updated every frame                       │
│ - Direct access by Synchronous modules      │
└─────────────────────────────────────────────┘
                    ↓
        ┌──────────────────────────┐
        │ Sync Provider (GDB)      │
        └──────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│ WORLD B (Fast Replica - Background Thread)  │
│ - Synced every frame                        │
│ - Persistent, double-buffered               │
│ - Used by FrameSynced modules               │
└─────────────────────────────────────────────┘

                    ↓
        ┌──────────────────────────┐
        │ Pool (SoD)               │
        └──────────────────────────┘
                    ↓
┌─────────────────────────────────────────────┐
│ WORLD C (Slow Snapshot - Background Thread) │
│ - Created on-demand                         │
│ - Held across multiple frames               │
│ - Pooled and reused                         │
│ - Used by Asynchronous modules              │
└─────────────────────────────────────────────┘
```

### Data Strategies

**Direct Access (Synchronous Only):**
- **No snapshot** - module accesses live repository
- **Main thread only**
- **Zero overhead**
- Use for: Physics, input

**GDB (Generalized Double Buffer):**
- **Persistent replica** synced every frame
- **Low latency** (~0.5ms sync for 100k entities)
- **Background thread** safe
- Use for: Network, recorder, low-latency analytics

**SoD (Snapshot on Demand):**
- **Pooled snapshot** created when module runs
- **Can span multiple frames** (module holds lease)
- **Memory efficient** (shared between convoys)
- Use for: AI, pathfinding, expensive analytics

###Convoy Pattern (Automatic Optimization)

**Modules with identical execution characteristics share a single snapshot:**

```csharp
// These 3 modules run at 10Hz on background threads
var aiModule1 = new DecisionModule();
var aiModule2 = new PathfindingModule();
var aiModule3 = new PerceptionModule();

// ModuleHost automatically detects convoy:
// - All have TargetFrequencyHz = 10
// - All use DataStrategy.SoD
// - All use RunMode.Asynchronous

// Result:
// - ONE snapshot created (union of component requirements)
// - ONE sync operation
// - 80% memory savings (100MB vs 500MB)
// - 70% sync time savings
```

---

## Flight Recorder & Deterministic Replay

### What is the Flight Recorder?

The **Flight Recorder** captures a deterministic record of the simulation for:
- **Replay:** Step through history frame-by-frame
- **Debugging:** Identify when bugs occurred
- **Network sync:** Reconcile divergent clients
- **Analytics:** Post-process simulation data

### How It Works

**Delta Compression:**
```csharp
Frame 0: Full Snapshot (baseline)
Frame 1: Delta (only changes since Frame 0)
Frame 2: Delta (only changes since Frame 1)
...
Frame 100: Full Snapshot (new baseline)
```

**Recording Process:**
1. Each frame, recorder queries: "Components changed since last frame?"
2. Uses `EntityRepository.GlobalVersion` to detect changes
3. Writes delta to binary stream
4. Periodically writes full snapshot (baseline)

### Design Implications

**Critical Rules for Replay:**

1. **Tick the Repository:**
   ```csharp
   void Update()
   {
       _repository.Tick(); // MUST call every frame!
       // ... simulation ...
   }
   ```

2. **Managed Components Must Be Immutable:**
   ```csharp
   // ✅ GOOD: Immutable record
   public record BehaviorState
   {
       public required int CurrentNode { get; init; }
   }
   
   // ❌ BAD: Mutable class
   public class BehaviorState
   {
       public int CurrentNode; // Shallow copy breaks replay!
   }
   ```

3. **Mark Transient Components:**
   ```csharp
   [TransientComponent]
   public class UIRenderCache { } // Never recorded
   ```

4. **Deterministic Logic:**
   ```csharp
   // ❌ BAD: Non-deterministic
   var random = new Random(); // Different on replay!
   
   // ✅ GOOD: Seeded RNG stored in component
   var rng = World.GetComponent<RandomState>(entity);
   var value = rng.Next();
   World.SetComponent(entity, rng); // Save state
   ```

### Transient Components and Recording

**Transient components are automatically excluded:**

```csharp
repository.RegisterManagedComponent<UIRenderCache>(snapshotable: false);

// Flight Recorder uses AllSnapshotable mask
// UIRenderCache is never serialized
// Replays are smaller and faster
```

### Example Integration

```csharp
var repository = new EntityRepository();
var recorder = new FlightRecorder(repository);

// Main loop
void Update(float deltaTime)
{
    repository.Tick(); // Increment version
    
    // Run simulation...
    
    // Capture frame
    recorder.CaptureFrame(repository.GlobalVersion - 1);
}

// Replay
void Replay(int frameIndex)
{
    recorder.RestoreFrame(frameIndex, repository);
    // Repository now matches historical state
}
```

---

## Network Integration

### Network Gateway Pattern

The **Network Gateway** bridges FDP's atomic components with rich network descriptors.

**Problem:** Network uses denormalized data (EntityStateDescriptor), FDP uses normalized components (Position, Velocity, etc.)

**Solution:** Translator Pattern

### Ingress (Network → FDP)

```csharp
public class EntityStateTranslator : IDescriptorTranslator
{
    public string TopicName => "SST.EntityState";
    
    public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
    {
        foreach (var sample in reader.TakeSamples())
        {
            var desc = (EntityStateDescriptor)sample;
            
            // Map network ID to local entity
            var entity = MapNetworkIdToEntity(desc.EntityId);
            
            // Translate rich descriptor to atomic components
            cmd.SetComponent(entity, new Position { Value = desc.Location });
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
            cmd.SetComponent(entity, new NetworkTarget 
            { 
                Value = desc.Location, 
                Timestamp = desc.Timestamp 
            });
        }
    }
}
```

### Egress (FDP → Network)

```csharp
public void ScanAndPublish(ISimulationView view, IDataWriter writer)
{
    // Query locally owned entities
    var query = view.Query()
        .With<Position>()
        .With<Velocity>()
        .With<NetworkOwnership>()
        .Build();
    
    foreach (var entity in query)
    {
        var ownership = view.GetComponentRO<NetworkOwnership>(entity);
        
        // Only publish if we own this entity
        if (!ownership.IsLocallyOwned)
            continue;
        
        // Build descriptor from components
        var pos = view.GetComponentRO<Position>(entity);
        var vel = view.GetComponentRO<Velocity>(entity);
        
        var descriptor = new EntityStateDescriptor
        {
            EntityId = MapEntityToNetworkId(entity),
            OwnerId = ownership.OwnerId,
            Location = pos.Value,
            Velocity = vel.Value,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        writer.Write(descriptor);
    }
}
```

### Network Data Flow

```
┌─────────────────────────────────────────────┐
│ DDS Network Topic (EntityState)             │
└─────────────────────────────────────────────┘
                    ↓
        NetworkIngestSystem (Input Phase)
                    ↓
            Translator.PollIngress()
                    ↓
┌─────────────────────────────────────────────┐
│ FDP Components (Position, Velocity, etc.)   │
│ + NetworkTarget (for smoothing)             │
└─────────────────────────────────────────────┘
                    ↓
        NetworkSmoothingSystem (Input Phase)
                    ↓
    Interpolate NetworkTarget → Position
                    ↓
        Simulation Systems Process
                    ↓
        NetworkSyncSystem (Export Phase)
                    ↓
            Translator.ScanAndPublish()
                    ↓
┌─────────────────────────────────────────────┐
│ DDS Network Topic (EntityState)             │
└─────────────────────────────────────────────┘
```

### Ownership Model

**Rule:** Only the owner writes to network.

```csharp
public struct NetworkOwnership
{
    public int OwnerId;          // Which node owns this entity
    public bool IsLocallyOwned;  // Do we own it?
}

// Ingress: Ignore updates for entities we own
if (view.HasComponent<NetworkOwnership>(entity))
{
    var ownership = view.GetComponentRO<NetworkOwnership>(entity);
    if (ownership.IsLocallyOwned)
        return; // Skip - we're authoritative
}

// Egress: Only publish entities we own
if (!ownership.IsLocallyOwned)
    continue; // Skip - not our entity
```

### Smoothing Remote Entities

```csharp
[UpdateInPhase(SystemPhase.Input)]
public class NetworkSmoothingSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query()
            .With<Position>()
            .With<NetworkTarget>()
            .Build();
        
        foreach (var entity in query)
        {
            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
            if (ownership.IsLocallyOwned)
                continue; // Don't smooth our own entities
            
            var current = view.GetComponentRO<Position>(entity);
            var target = view.GetComponentRO<NetworkTarget>(entity);
            
            // Lerp towards target (dead reckoning)
            float t = Math.Clamp(deltaTime * 10f, 0f, 1f);
            var newPos = Vector3.Lerp(current.Value, target.Value, t);
            
            view.GetCommandBuffer().SetComponent(entity, new Position { Value = newPos });
        }
    }
}
```

---

## Best Practices

### Component Design

1. **Prefer unmanaged components**
   ```csharp
   public struct Position { public Vector3 Value; }
   ```

2. **Managed components must be immutable records**
   ```csharp
   public record BehaviorTree { public required Node Root { get; init; } }
   ```

3. **Use transient components for mutable UI/debug state**
   ```csharp
   [TransientComponent]
   public class DebugGizmos { public List<Line> Lines; }
   ```

4. **Keep components data-only**
   ```csharp
   // ✅ GOOD
   public struct Health { public float Current; public float Max; }
   
   // ❌ BAD
   public struct Health 
   { 
       public float Current;
       public void Damage(float amount) { } // No methods!
   }
   ```

### System Design

1. **Systems never reference other systems**
2. **Communicate via data (components, singletons, events)**
3. **Cache queries in OnCreate()**
4. **Use ForEachParallel() for performance**

### Module Design

1. **Choose appropriate ExecutionPolicy:**
   - Synchronous: Physics, input (main thread required)
   - FrameSynced: Network, recorder (low latency, blocks frame)
   - Asynchronous: AI, analytics (expensive, non-blocking)

2. **Use reactive scheduling for event-driven modules**
3. **Keep Tick() logic focused - delegate to systems**
4. **Always use ISimulationView, never cast to EntityRepository**

### Performance

1. **NativeArrays for static lookup tables**
2. **Convoy pattern for module groups (automatic)**
3. **Profile before optimizing**
4. **Avoid allocations in hot paths**

---

## Network Ownership & Distributed Simulation

### The Ownership Model

In distributed simulations, **not every node simulates every entity**. Each entity has an **owner** - the node responsible for its authoritative state.

**NetworkOwnership Component:**

```csharp
public struct NetworkOwnership
{
    public int OwnerId;           // Which node owns this entity
    public bool IsLocallyOwned;   // Do WE own it?
}
```

**Example:**
```
Node 1 owns: Tank-001, Infantry-Squad-A
Node 2 owns: Tank-002, Aircraft-001
Node 3 owns: Artillery-Battery-B

Each node simulates ALL entities, but only PUBLISHES owned entities to network.
```

### Ownership Rules for Components

**Rule:** Only the owner **writes** component state to the network. All nodes **read** from network.

#### Ingress (Network → FDP)

```csharp
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    foreach (var sample in reader.TakeSamples())
    {
        var desc = (EntityStateDescriptor)sample;
        var entity = MapToEntity(desc.EntityId);
        
        // Check ownership
        if (view.HasComponent<NetworkOwnership>(entity))
        {
            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
            
            // We own this entity - IGNORE incoming network updates
            if (ownership.IsLocallyOwned)
                return; // Our local simulation is authoritative
        }
        
        // We don't own it - UPDATE from network data
        cmd.SetComponent(entity, new Position { Value = desc.Location });
        cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
    }
}
```

**Why?**
- Without this check: Owner's local state gets overwritten by stale network data
- Result: "Rubber-banding" - entity jumps between local and network positions

#### Egress (FDP → Network)

```csharp
public void ScanAndPublish(ISimulationView view, IDataWriter writer)
{
    var query = view.Query()
        .With<Position>()
        .With<Velocity>()
        .With<NetworkOwnership>()
        .Build();
    
    foreach (var entity in query)
    {
        var ownership = view.GetComponentRO<NetworkOwnership>(entity);
        
        // Only publish if WE own this entity
        if (!ownership.IsLocallyOwned)
            continue; // Another node is publishing this
        
        // Build descriptor and publish
        var descriptor = new EntityStateDescriptor
        {
            EntityId = MapToNetworkId(entity),
            OwnerId = ownership.OwnerId,
            Location = view.GetComponentRO<Position>(entity).Value,
            Velocity = view.GetComponentRO<Velocity>(entity).Value
        };
        
        writer.Write(descriptor);
    }
}
```

**Why?**
- Without this check: All 3 nodes publish same entity → Network flooded with duplicate data
- With check: Only owner publishes → Clean, efficient network traffic

### Ownership Rules for Events

**Events have THREE ownership models** depending on event type:

#### 1. Entity-Sourced Events (Ownership Required)

Events that originate from a specific entity's action.

**Examples:**
- `WeaponFireEvent` - Tank fires weapon
- `DamageEvent` - Entity takes damage
- `DetonationEvent` - Munition explodes

**Rule:** Only the node owning the **source entity** publishes to network.

**Code:**
```csharp
public void PublishEvents(ISimulationView view, IDataWriter writer)
{
    var events = view.GetEvents<WeaponFireEvent>();
    
    foreach (var evt in events)
    {
        // Check ownership of FIRING entity
        var ownership = view.GetComponentRO<NetworkOwnership>(evt.FiringEntity);
        
        // Only publish if WE own the firing entity
        if (!ownership.IsLocallyOwned)
            continue; // Another node will publish this
        
        // Translate and publish
        var pdu = new FirePDU
        {
            FiringEntityId = MapToNetworkId(evt.FiringEntity),
            TargetEntityId = MapToNetworkId(evt.TargetEntity),
            WeaponType = evt.WeaponType,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        writer.Write(pdu);
    }
}
```

**Why this matters:**

```
Scenario: Tank-001 (owned by Node 1) fires at Tank-002

Without ownership check:
  - Node 1 sees fire event → publishes to network
  - Node 2 sees fire event → publishes to network
  - Node 3 sees fire event → publishes to network
  Result: Network sees 3 fire events for single shot!

With ownership check:
  - Node 1 owns Tank-001 → publishes FirePDU
  - Node 2 doesn't own Tank-001 → skips
  - Node 3 doesn't own Tank-001 → skips
  Result: Network sees 1 fire event (correct!)
```

#### 2. Global/Broadcast Events (No Ownership)

Events not tied to any  specific entity.

**Examples:**
- `MissionObjectiveComplete` - Scenario event
- `TimeOfDayChanged` - Environment change
- `PhaseTransition` - Simulation state change

**Rule:** Published by designated **authority node** (e.g., mission server, environment manager).

**Code:**
```csharp
public void PublishEvents(ISimulationView view, IDataWriter writer)
{
    var events = view.GetEvents<MissionObjectiveComplete>();
    
    foreach (var evt in events)
    {
        // NO ownership check - but typically only mission server generates these
        // (Enforced by game logic, not network layer)
        
        var pdu = new MissionObjectivePDU
        {
            ObjectiveId = evt.ObjectiveId,
            Status = evt.Status,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        writer.Write(pdu);
    }
}
```

**Design note:** Usually only ONE node (by role) generates these events. If multiple nodes could generate them, use a coordinator pattern or leader election.

#### 3. Multi-Entity Events (Complex Ownership)

Events involving multiple entities where both might be owned by different nodes.

**Examples:**
- `CollisionEvent` - Two entities collide
- `FormationJoinedEvent` - Entity joins another's formation

**Rule:** Owner of **primary/aggressor entity** publishes. Use deterministic tie-breaking.

**Code:**
```csharp
public void PublishEvents(ISimulationView view, IDataWriter writer)
{
    var events = view.GetEvents<CollisionEvent>();
    
    foreach (var evt in events)
    {
        var ownershipA = view.GetComponentRO<NetworkOwnership>(evt.EntityA);
        var ownershipB = view.GetComponentRO<NetworkOwnership>(evt.EntityB);
        
        // Deterministic rule: Publish if we own EntityA, 
        // OR if we own EntityB but EntityA doesn't exist locally
        bool shouldPublish = ownershipA.IsLocallyOwned || 
                            (ownershipB.IsLocallyOwned && !ownershipA.IsLocallyOwned);
        
        if (!shouldPublish)
            continue;
        
        var pdu = new CollisionPDU
        {
            EntityAId = MapToNetworkId(evt.EntityA),
            EntityBId = MapToNetworkId(evt.EntityB),
            ImpactPoint = evt.ImpactPoint,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        writer.Write(pdu);
    }
}
```

**Why deterministic rule?**
Both nodes might detect collision locally (physics running on both). Without a rule, both nodes publish → duplicate event. The rule ensures exactly ONE node publishes.

### Common Ownership Patterns

#### Pattern 1: Static Ownership (Pre-assigned)

```csharp
// At entity creation
var entity = view.CreateEntity();
cmd.SetComponent(entity, new NetworkOwnership
{
    OwnerId = GetLocalNodeId(),
    IsLocallyOwned = true
});

// This entity is ALWAYS owned by creating node
```

**Use for:** Player avatar, locally spawned objects

#### Pattern 2: Dynamic Ownership (Transferable)

```csharp
// Transfer ownership when player enters vehicle
public void OnPlayerEnterVehicle(Entity vehicle, Entity player)
{
    var playerOwnership = view.GetComponentRO<NetworkOwnership>(player);
    
    // Vehicle adopts player's ownership
    cmd.SetComponent(vehicle, new NetworkOwnership
    {
        OwnerId = playerOwnership.OwnerId,
        IsLocallyOwned = playerOwnership.IsLocallyOwned
    });
    
    // Publish ownership transfer to network
    cmd.PublishEvent(new OwnershipTransferEvent
    {
        Entity = vehicle,
        NewOwnerId = playerOwnership.OwnerId
    });
}
```

**Use for:** Vehicles, lootable items, transferable equipment

#### Pattern 3: Proximity-Based Ownership

```csharp
// Transfer ownership to nearest player
public void OnProximityCheck(Entity item)
{
    var nearestPlayer = FindNearestPlayer(item);
    
    if (nearestPlayer != Entity.Null)
    {
        var playerOwnership = view.GetComponentRO<NetworkOwnership>(nearestPlayer);
        
        // Item becomes owned by nearest player's node
        cmd.SetComponent(item, new NetworkOwnership
        {
            OwnerId = playerOwnership.OwnerId,
            IsLocallyOwned = playerOwnership.IsLocallyOwned
        });
    }
}
```

**Use for:** Area-of-interest management, load balancing

### Debugging Ownership Issues

**Common Problems:**

1. **Rubber-banding (Entity jerks around):**
   - **Cause:** Owner is updating locally, but also reading network updates for owned entity
   - **Fix:** Add ownership check in ingress - skip owned entities

2. **Duplicate Network Events:**
   - **Cause:** All nodes publishing same entity-sourced event
   - **Fix:** Add ownership check in egress - only owner publishes

3. **Entity State Divergence:**
   - **Cause:** Ownership transfer not synchronized
   - **Fix:** Use `OwnershipTransferEvent` to coordinate transfer

4. **Missing Events:**
   - **Cause:** Wrong node thinks it owns entity
   - **Fix:** Verify ownership component matches network ID mapping

**Diagnostic Code:**

```csharp
[UpdateInPhase(SystemPhase.PostSimulation)]
public class OwnershipDebugSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var query = World.Query()
            .With<NetworkOwnership>()
            .With<Position>()
            .Build();
        
        foreach (var entity in query)
        {
            var ownership = World.GetComponent<NetworkOwnership>(entity);
            var pos = World.GetComponent<Position>(entity);
            
            if (ownership.IsLocallyOwned)
            {
                Console.WriteLine($"[OWNED] Entity {entity.Id} at {pos.Value}");
            }
            else
            {
                Console.WriteLine($"[REMOTE] Entity {entity.Id} (Owner: {ownership.OwnerId}) at {pos.Value}");
            }
        }
    }
}
```

---

## Time Control & Synchronization

### The GlobalTime Descriptor

In distributed simulations, each node needs a consistent view of **simulation time**. This is separate from **wall clock time** (real world).

**GlobalTime Singleton:**

```csharp
public struct GlobalTime
{
    public double TotalTime;        // Elapsed simulation time (seconds)
    public float DeltaTime;         // Time since last frame (seconds)
    public float TimeScale;         // Speed multiplier (0.0 = paused, 1.0 = realtime, 2.0 = 2x speed)
    public bool IsPaused;           // Convenience flag (TimeScale == 0.0)
    public long FrameNumber;        // Current frame index
}
```

**Usage in Systems:**

```csharp
public class PhysicsSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Get global time from world
        var time = World.GetSingleton<GlobalTime>();
        
        // Use simulation time, not wall clock
        float dt = time.DeltaTime;
        
        // Physics updates with scaled time
        foreach (var entity in _query)
        {
            var vel = World.GetComponent<Velocity>(entity);
            var pos = World.GetComponent<Position>(entity);
            
            // This respects TimeScale automatically
            pos.Value += vel.Value * dt;
            
            World.SetComponent(entity, pos);
        }
    }
}
```

### Two Modes of Time Synchronization

Distributed simulations face a fundamental challenge: **How do multiple nodes stay synchronized?**

FDP/ModuleHost supports two modes:

#### Mode 1: Continuous (Real-Time / Scaled)

**Best-effort synchronization**. Time flows continuously. Nodes chase the master clock.

**When to use:**
- Training simulations (flight simulators, tactical trainers)
- Game servers (MMOs, multiplayer games)
- Live demonstrations
- Most simulation scenarios (90% of use-cases)

**Characteristics:**
- ✅ Low latency (~20ms variance)
- ✅ Smooth playback
- ✅ Can pause/resume/speed up
- ⚠️ Not perfectly deterministic (acceptable for most use-cases)

#### Mode 2: Deterministic (Lockstep / Stepped)

**Strict synchronization**. Frame N starts only when Frame N-1 is done everywhere.

**When to use:**
- Scientific simulations requiring exact reproducibility
- Regulatory compliance (aerospace, medical)
- Debugging distributed bugs (replay from logs)
- Network testing (controlled timing)

**Characteristics:**
- ✅ Perfectly deterministic
- ✅ Repeatable from logs
- ⚠️ High latency (limited by slowest node)
- ⚠️ No smooth playback if network lags

### The Clock Model

**Separation of Concerns:**
- **Wall Clock** - Real world time (UTC ticks)
- **Simulation Clock** - Virtual world time (can be paused, scaled, stepped)

**Master/Slave Architecture:**
- **Master Clock** - One node (Orchestrator) owns authoritative time
- **Slave Clocks** - All other nodes follow Master using Phase-Locked Loop (PLL)

**The Simulation Time Equation:**

$$T_{sim} = T_{base} + (T_{wall} - T_{start}) \times Scale$$

Where:
- $T_{sim}$ - Current simulation time
- $T_{base}$ - Simulation time when last speed change happened
- $T_{wall}$ - Current wall clock time (UTC)
- $T_{start}$ - Wall clock time when last speed change happened
- $Scale$ - Speed coefficient

**Example:**

```
Initial State:
  T_base = 0.0
  T_start = 12:00:00 UTC
  Scale = 1.0 (realtime)

At 12:00:10 UTC:
  T_sim = 0.0 + (12:00:10 - 12:00:00) × 1.0 = 10.0 seconds

Speed up to 2x at T_sim = 10.0:
  T_base = 10.0
  T_start = 12:00:10 UTC
  Scale = 2.0

At 12:00:20 UTC:
  T_sim = 10.0 + (12:00:20 - 12:00:10) × 2.0 = 30.0 seconds
  (10 wall seconds = 20 sim seconds due to 2x speed)

Pause at T_sim = 30.0:
  T_base = 30.0
  T_start = 12:00:20 UTC
  Scale = 0.0

At 12:00:40 UTC:
  T_sim = 30.0 + (12:00:40 - 12:00:20) × 0.0 = 30.0 seconds
  (Frozen at 30.0 despite 20 wall seconds passing)
```

### Continuous Mode Implementation

**Network Protocol:**

**Topic:** `Sys.TimePulse` (1Hz heartbeat + on-change)

**Payload:**
```csharp
public class TimePulseDescriptor
{
    public long MasterWallTime;      // Master's UTC ticks
    public double SimTimeSnapshot;   // Master's current T_sim
    public float TimeScale;          // Master's current Scale
    public bool IsPaused;            // Master's pause state
}
```

**Master Node Behavior:**

```csharp
public class MasterTimeController : ITimeController
{
    private Stopwatch _wallClock = Stopwatch.StartNew();
    private double _simTimeBase = 0.0;
    private long _scaleChangeWallTicks = 0;
    private float _timeScale = 1.0f;
    
    public void Update(out float dt, out double totalTime)
    {
        // Calculate wall delta
        long currentWallTicks = _wallClock.ElapsedTicks;
        double wallDelta = (currentWallTicks - _lastWallTicks) / (double)Stopwatch.Frequency;
        _lastWallTicks = currentWallTicks;
        
        // Calculate sim delta (respecting scale)
        dt = (float)(wallDelta * _timeScale);
        totalTime = _simTimeBase + (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
        
        // Publish to network (1Hz or on-change)
        if (ShouldPublishPulse())
        {
            _networkWriter.Write(new TimePulseDescriptor
            {
                MasterWallTime = DateTimeOffset.UtcNow.Ticks,
                SimTimeSnapshot = totalTime,
                TimeScale = _timeScale,
                IsPaused = _timeScale == 0.0f
            });
        }
    }
    
    public void SetTimeScale(float scale)
    {
        // Save current sim time as new base
        _simTimeBase = CalculateCurrentSimTime();
        _scaleChangeWallTicks = _wallClock.ElapsedTicks;
        _timeScale = scale;
        
        // Immediately publish to slaves
        PublishTimePulse();
    }
}
```

**Slave Node Behavior (with PLL):**

```csharp
public class SlaveTimeController : ITimeController
{
    private Stopwatch _wallClock = Stopwatch.StartNew();
    private double _simTimeBase = 0.0;
    private long _scaleChangeWallTicks = 0;
    private float _timeScale = 1.0f;
    
    // PLL state
    private double _timeError = 0.0;
    private const float _correctionFactor = 0.01f; // 1% adjustment per frame
    
    public void OnTimePulseReceived(TimePulseDescriptor pulse)
    {
        // Calculate what our sim time SHOULD be based on master's snapshot
        long currentWallTicks = DateTimeOffset.UtcNow.Ticks;
        double wallDeltaSincePulse = (currentWallTicks - pulse.MasterWallTime) / (double)TimeSpan.TicksPerSecond;
        double masterSimTime = pulse.SimTimeSnapshot + wallDeltaSincePulse * pulse.TimeScale;
        
        // Calculate our current sim time
        double localSimTime = CalculateCurrentSimTime();
        
        // Calculate error
        _timeError = masterSimTime - localSimTime;
        
        // Update scale
        _timeScale = pulse.TimeScale;
    }
    
    public void Update(out float dt, out double totalTime)
    {
        // Calculate wall delta
        long currentWallTicks = _wallClock.ElapsedTicks;
        double wallDelta = (currentWallTicks - _lastWallTicks) / (double)Stopwatch.Frequency;
        _lastWallTicks = currentWallTicks;
        
        // PLL Correction: Gently adjust dt to converge with master
        // If we're behind (error > 0), run slightly faster
        // If we're ahead (error < 0), run slightly slower
        float correction = (float)(_timeError * _correctionFactor);
        float adjustedScale = _timeScale + correction;
        
        // Calculate dt with adjusted scale
        dt = (float)(wallDelta * adjustedScale);
        totalTime = CalculateCurrentSimTime() + dt;
        
        // Reduce error by what we just corrected
        _timeError -= correction * wallDelta;
    }
}
```

**Why PLL (Phase-Locked Loop)?**

Without PLL:
```
Master says: T_sim = 10.0
Slave has: T_sim = 9.8

Bad approach: Snap to 10.0
Result: Time jumps! Entities teleport! Rubber-banding!

Good approach (PLL): Gradually increase dt by 1% for next few frames
Frame 0: dt = 0.01616 (instead of 0.016)
Frame 1: dt = 0.01616
Frame 2: dt = 0.01616
...
After 100 frames: Converged to 10.0 smoothly
```

### Deterministic Mode Implementation

**Network Protocol:**

**Topic:** `Sys.FrameOrder` (Master → All)
**Topic:** `Sys.FrameAck` (All → Master)

**Frame Order Descriptor:**
```csharp
public class FrameOrderDescriptor
{
    public long FrameID;        // Frame number to execute
    public float FixedDelta;    // Fixed dt for this frame (e.g., 0.016s)
}
```

**Frame Ack Descriptor:**
```csharp
public class FrameAckDescriptor
{
    public long FrameID;        // Frame just completed
    public int NodeID;          // Who completed it
}
```

**Lockstep Cycle:**

```
1. Master waits for all ACKs for Frame N-1

2. Master publishes FrameOrder { FrameID: N, FixedDelta: 0.016 }

3. Slave receives FrameOrder:
   - Runs simulation with dt = 0.016
   - Executes all systems
   - **BARRIER: Pauses at end of frame**
   - Publishes FrameAck { FrameID: N, NodeID: Me }

4. Repeat
```

**Master Implementation:**

```csharp
public class SteppedTimeController : ITimeController
{
    private long _currentFrame = 0;
    private float _fixedDelta = 0.016f;
    private HashSet<int> _pendingAcks = new();
    private bool _waitingForAcks = false;
    
    public void Update(out float dt, out double totalTime)
    {
        if (_waitingForAcks)
        {
            // Check if all ACKs received
            if (_pendingAcks.Count == 0)
            {
                // All nodes finished Frame N-1, advance to Frame N
                _currentFrame++;
                _waitingForAcks = false;
                
                // Publish order for next frame
                _networkWriter.Write(new FrameOrderDescriptor
                {
                    FrameID = _currentFrame,
                    FixedDelta = _fixedDelta
                });
                
                // Reset pending ACKs
                _pendingAcks = new HashSet<int>(_allNodeIds);
            }
            else
            {
                // Still waiting - don't advance simulation
                dt = 0.0f;
                totalTime = _currentFrame * _fixedDelta;
                return;
            }
        }
        
        // Execute this frame
        dt = _fixedDelta;
        totalTime = _currentFrame * _fixedDelta;
        
        // Mark waiting for ACKs
        _waitingForAcks = true;
    }
    
    public void OnFrameAckReceived(FrameAckDescriptor ack)
    {
        if (ack.FrameID == _currentFrame)
        {
            _pendingAcks.Remove(ack.NodeID);
        }
    }
}
```

**Slave Implementation:**

```csharp
public class SteppedSlaveController : ITimeController
{
    private long _currentFrame = 0;
    private float _fixedDelta = 0.016f;
    private bool _hasFrameOrder = false;
    
    public void OnFrameOrderReceived(FrameOrderDescriptor order)
    {
        _currentFrame = order.FrameID;
        _fixedDelta = order.FixedDelta;
        _hasFrameOrder = true;
    }
    
    public void Update(out float dt, out double totalTime)
    {
        if (!_hasFrameOrder)
        {
            // Waiting for master - don't advance
            dt = 0.0f;
            totalTime = _currentFrame * _fixedDelta;
            return;
        }
        
        // Execute frame
        dt = _fixedDelta;
        totalTime = _currentFrame * _fixedDelta;
        
        // After simulation completes (end of Update), send ACK
        _hasFrameOrder = false;
    }
    
    public void SendFrameAck()
    {
        _networkWriter.Write(new FrameAckDescriptor
        {
            FrameID = _currentFrame,
            NodeID = _localNodeId
        });
    }
}
```

### Time Control Usage Examples

#### Example 1: Pause Simulation

```csharp
public class SimulationController
{
    private MasterTimeController _timeController;
    
    public void OnPauseButtonClicked()
    {
        _timeController.SetTimeScale(0.0f);
        // All slave nodes will receive TimePulse and pause smoothly
    }
    
    public void OnResumeButtonClicked()
    {
        _timeController.SetTimeScale(1.0f);
        // Resume at normal speed
    }
}
```

#### Example 2: Variable Speed Playback

```csharp
public class TrainingControls
{
    public void SetPlaybackSpeed(float speed)
    {
        // 0.5x = Slow motion for analysis
        // 1.0x = Realtime
        // 2.0x = Fast forward
        _timeController.SetTimeScale(speed);
    }
}
```

#### Example 3: Deterministic Replay from Log

```csharp
public class ReplayController
{
    private SteppedTimeController _timeController;
    private List<FrameOrderDescriptor> _recordedFrames;
    
    public void ReplayFromLog()
    {
        // Switch to deterministic mode
        _timeController.SetMode(TimeMode.Stepped);
        
        // Replay each frame exactly as it was recorded
        foreach (var frameOrder in _recordedFrames)
        {
            _timeController.ExecuteFrame(frameOrder);
            // Exact dt, exact frame number - deterministic!
        }
    }
}
```

### Choosing the Right Mode

**Use Continuous Mode when:**
- ✅ You need smooth, responsive playback
- ✅ Network latency varies
- ✅ Nodes have different performance characteristics
- ✅ Users need pause/speed controls
- ✅ "Good enough" synchronization is acceptable (~20ms variance)

**Use Deterministic Mode when:**
- ✅ You need perfect reproducibility
- ✅ Debugging distributed bugs
- ✅ Regulatory compliance (audit trails)
- ✅ Scientific validation
- ⚠️ Can tolerate latency (slowest node bottleneck)

**Recommendation:** Start with **Continuous Mode**. It handles 90% of use-cases and provides a better user experience. Add Deterministic Mode later only if strict reproducibility is required.

---

## Anti-Patterns to Avoid

### ❌ DON'T: System-to-System References
```csharp
public class BadSystem : ComponentSystem
{
    private OtherSystem _other; // WRONG!
}
```

### ❌ DON'T: Mutable Managed Components in Snapshots
```csharp
public class AIState
{
    public List<Entity> Targets; // Shallow copy breaks replay!
}
```

### ❌ DON'T: Forget Repository.Tick()
```csharp
void Update()
{
    // Missing: _repository.Tick();
    // Result: Flight Recorder records empty deltas!
}
```

### ❌ DON'T: Access Live Repository from Background Module
```csharp
public void Tick(ISimulationView view, float dt)
{
    var repo = view as EntityRepository; // UNSAFE!
    repo.GetComponent(...); // Race condition if running on background thread!
}
```

### ❌ DON'T Create Queries in OnUpdate()
```csharp
protected override void OnUpdate()
{
    var query = World.Query().With<Position>().Build(); // SLOW! Allocates!
    // Move to OnCreate()
}
```

---

**Version 2.0 - January 2026**  
**For questions or clarifications, see: [Design Implementation Plan](DESIGN-IMPLEMENTATION-PLAN.md)**
