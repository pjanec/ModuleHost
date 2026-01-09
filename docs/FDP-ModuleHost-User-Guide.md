# FDP Kernel & ModuleHost User Guide

**Version:** 2.0  
**Date:** 2026-01-09  
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
9. [Time Control & Synchronization](#time-control--synchronization)
10. [Transient Components & Snapshot Filtering](#transient-components--snapshot-filtering)
11. [Best Practices](#best-practices)
12. [Common Patterns](#common-patterns)
13. [Anti-Patterns to Avoid](#anti-patterns-to-avoid)

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

## Entity Templates & Spawning (TKB)

### Overview

The **Technical Knowledge Base (TKB)** provides a template system for pre-configuring entity archetypes. Instead of manually adding components every time you spawn an entity, define reusable blueprints.

**Why Use TKB:**
- **Performance:** Components configured once, applied via delegates (faster than reflection)
- **Consistency:** All "Tank" entities use the same blueprint
- **Maintainability:** Change tank stats in one place

### Creating Templates

```csharp
using Fdp.Kernel.Tkb;

// Create template database
var tkb = new TkbDatabase();

// Define a "Tank" template
var tankTemplate = new TkbTemplate("Tank");
tankTemplate.AddComponent(new Position { X = 0, Y = 0, Z = 0 });
tankTemplate.AddComponent(new Health { Current = 100, Maximum = 100 });
tankTemplate.AddComponent(new Velocity { X = 0, Y = 0, Z = 0 });
tankTemplate.AddComponent(new TankConfig 
{ 
    Speed = 10.0f,
    TurretRotationSpeed = 45.0f,
    AmmoCapacity = 40
});

// Register template
tkb.RegisterTemplate(tankTemplate);
```

### Spawning from Templates

```csharp
// Spawn a tank at runtime
Entity tankEntity = tkb.Spawn("Tank", world);

// Template applied all components automatically
// Now customize instance-specific data
var pos = world.GetComponent<Position>(tankEntity);
pos.X = 100;
pos.Z = 50;
world.SetComponent(tankEntity, pos);
```

### Advanced: Component Initialization Delegates

For complex initialization logic:

```csharp
var enemyTemplate = new TkbTemplate("Enemy");

// Use delegate for dynamic values
enemyTemplate.AddComponent(() => new Health 
{ 
    Current = Random.Shared.Next(50, 100),  // Random starting health
    Maximum = 100 
});

enemyTemplate.AddComponent(() => new AIState
{
    Behavior = AIBehaviorType.Patrol,
    PatrolPath = GenerateRandomPath()  // Dynamic path generation
});

tkb.RegisterTemplate(enemyTemplate);
```

### Template Variants

Create specialized variants from base templates:

```csharp
// Base tank
var baseTankTemplate = new TkbTemplate("BaseTank");
baseTankTemplate.AddComponent(new Health { Current = 100, Maximum = 100 });
baseTankTemplate.AddComponent(new Velocity());

// Heavy tank (inherits base, adds armor)
var heavyTankTemplate = new TkbTemplate("HeavyTank", baseTankTemplate);
heavyTankTemplate.AddComponent(new Armor { Value = 50 });
heavyTankTemplate.AddComponent(new TankConfig { Speed = 5.0f });  // Slower

// Light tank (inherits base, faster)
var lightTankTemplate = new TkbTemplate("LightTank", baseTankTemplate);
lightTankTemplate.AddComponent(new TankConfig { Speed = 20.0f });  // Faster
```

### Integration with ModuleHost

```csharp
public class GameModule : IModule
{
    private TkbDatabase _tkb;
    
    public void Initialize(EntityRepository world, IEventBus eventBus)
    {
        // Load templates from config/data files
        _tkb = LoadTemplatesFromDisk("templates.json");
        
        // Spawn initial entities
        for (int i = 0; i < 10; i++)
        {
            var enemy = _tkb.Spawn("Enemy", world);
            // Customize position
        }
    }
}
```

### Best Practices

1. **Define templates at startup:** Don't create templates in hot paths (e.g., inside `Tick()`)
2. **Use templates for archetypes:** Tanks, Soldiers, Buildings - not for one-off entities
3. **Combine with EntityCommandBuffer:** Spawn in systems via ECB for deferred creation
4. **Store in singleton:** Make `TkbDatabase` accessible as a singleton or module field

### Performance

- **Template application:** O(N) where N = number of components in template
- **Memory:** Templates are flyweight (shared across all spawned entities)
- **Garbage:** Zero allocations after template registration (delegates are cached)

---




## Systems & Scheduling

### Overview

**Systems** are the fundamental units of logic in FDP, encapsulating behavior that operates on entities with specific components. The **System Scheduler** orchestrates their execution, ensuring correct ordering, phase-based execution, and deterministic behavior.

**What Problems Do Systems Solve:**
- **Separation of Concerns:** Each system handles one specific responsibility
- **Deterministic Execution:** Predictable order guarantees reproducible behavior
- **Cache-Friendly Iteration:** Systems query and process components in tight loops
- **Parallel Safety:** Clear dependencies prevent race conditions

**When to Use Systems:**
- Any gameplay logic (physics, AI, animation)
- Input processing and network ingestion
- Post-processing and coordinate transforms
- Export operations (network sync, flight recorder)

---

### Core Concepts

#### The IModuleSystem Interface

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// A system that executes within a module's context.
    /// Can run on main thread or background thread depending on module policy.
    /// </summary>
    public interface IModuleSystem
    {
        /// <summary>
        /// Called every frame (or at module's configured frequency).
        /// </summary>
        /// <param name="view">Snapshot view of simulation state</param>
        /// <param name="deltaTime">Time since last execution (seconds)</param>
        void Execute(ISimulationView view, float deltaTime);
    }
}
```

**Key Points:**
- Systems are **stateless** - all state lives in components
- Systems **read and write components** via the `ISimulationView`
- Systems **never reference other systems** directly
- Systems **communicate via components, singletons, and events**

#### System Phases

Systems execute in **5 phases** every frame, strictly ordered:

```
┌─────────────────────────────────────────────────────────────┐
│ Frame N                                                     │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ 1️⃣ Input Phase          - Poll input devices, network     │
│    SystemPhase.Input                                        │
│                                                             │
│ 2️⃣ BeforeSync Phase     - Lifecycle, pre-simulation setup  │
│    SystemPhase.BeforeSync                                   │
│                                                             │
│ 3️⃣ Simulation Phase     - Physics, AI, game logic          │
│    SystemPhase.Simulation                                   │
│                                                             │
│ 4️⃣ PostSimulation Phase - Post-processing, transforms      │
│    SystemPhase.PostSimulation                               │
│                                                             │
│ 5️⃣ Export Phase         - Network sync, flight recorder    │
│    SystemPhase.Export                                       │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

**Phase Purposes:**

| Phase | Purpose | Examples |
|-------|---------|----------|
| **Input** | Ingest external data | `InputSystem`, `NetworkIngestSystem` |
| **BeforeSync** | Setup before simulation | `EntityLifecycleSystem` |
| **Simulation** | Core gameplay logic | `PhysicsSystem`, `AISystem`, `CollisionSystem` |
| **PostSimulation** | Post-processing | `CoordinateTransformSystem`, `AnimationBlendSystem` |
| **Export** | Publish to external | `NetworkSyncSystem`, `FlightRecorderSystem` |

#### Topological Sorting

Within each phase, systems are **topologically sorted** based on their dependencies.

**How It Works:**
1. Collect all systems for a phase
2. Build dependency graph from `[UpdateAfter]` and `[UpdateBefore]` attributes
3. Perform topological sort (Kahn's algorithm)
4. Detect circular dependencies - throw `CircularDependencyException` if found
5. Execute systems in sorted order

**Dependency Rules:**
- ✅ **Same-phase dependencies:** Respected and enforced
- ❌ **Cross-phase dependencies:** Ignored (phase order handles it)
- 🔄 **Circular dependencies:** Detected and rejected at startup

**Example Dependency Graph:**

```
[UpdateInPhase(SystemPhase.Simulation)]
class SystemA { }

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SystemA))]
class SystemB { }

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SystemB))]
class SystemC { }

Execution Order: A → B → C
```

---

### Usage Examples

#### Example 1: Basic Movement System

From `Fdp.Kernel` base class:

```csharp
using Fdp.Kernel;

/// <summary>
/// Applies velocity to position every frame.
/// Runs in Simulation phase (default for ComponentSystem).
/// </summary>
public class MovementSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        float dt = DeltaTime;
        
        // Query all entities with both Position and Velocity
        var query = World.Query()
            .With<Position>()
            .With<Velocity>()
            .Build();
        
        // Iterate and update
        foreach (var entity in query)
        {
            var pos = World.GetComponent<Position>(entity);
            var vel = World.GetComponent<Velocity>(entity);
            
            // Update position
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
            pos.Z += vel.Z * dt;
            
            // Write back
            World.SetComponent(entity, pos);
        }
    }
}
```

**Expected Output:**
- Every frame, all entities with Position+Velocity move according to their velocity
- Position changes are immediately visible to subsequent systems in same frame

---

#### Example 2: Ordered System Chain with Dependencies

From `SystemSchedulerTests.cs` lines 17-40:

```csharp
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Scheduling;

// System A runs first (no dependencies)
[UpdateInPhase(SystemPhase.Simulation)]
public class SpatialHashGridSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Build spatial hash grid from current positions
        var grid = BuildGrid(view);
        
        // Publish as singleton for other systems
        // (This pattern shown in Core Principles section)
        Console.WriteLine("A: Built spatial grid");
    }
}

// System B runs after A
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SpatialHashGridSystem))]
public class BroadPhaseCollisionSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Read spatial grid singleton built by System A
        Console.WriteLine("B: Broad phase using grid");
    }
}

// System C runs after B
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(BroadPhaseCollisionSystem))]
public class NarrowPhaseCollisionSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Process collision pairs from broad phase
        Console.WriteLine("C: Narrow phase collision resolution");
    }
}

// Usage:
var scheduler = new SystemScheduler();
scheduler.RegisterSystem(new SpatialHashGridSystem());
scheduler.RegisterSystem(new NarrowPhaseCollisionSystem()); // Registered out of order
scheduler.RegisterSystem(new BroadPhaseCollisionSystem());  // Order doesn't matter!

scheduler.BuildExecutionOrders(); // Topological sort happens here

scheduler.ExecutePhase(SystemPhase.Simulation, view, 0.016f);

// Output (always deterministic):
// A: Built spatial grid
// B: Broad phase using grid
// C: Narrow phase collision resolution
```

**Key Insights:**
- **Registration order doesn't matter** - topological sort determines execution
- **Dependencies are explicit** via attributes, not implicit via code order
- **Deterministic execution** guaranteed by scheduler

---

#### Example 3: System Groups for Nested Execution

From `SystemSchedulerTests.cs` lines 66-86:

```csharp
using ModuleHost.Core.Abstractions;

/// <summary>
/// A group contains multiple systems that execute together.
/// Useful for organizing related systems.
/// </summary>
[UpdateInPhase(SystemPhase.Simulation)]
public class PhysicsSystemGroup : ISystemGroup
{
    public string Name => "Physics";
    
    private readonly List<IModuleSystem> _systems = new()
    {
        new IntegrateVelocitySystem(),
        new CollisionDetectionSystem(),
        new CollisionResponseSystem()
    };
    
    public IReadOnlyList<IModuleSystem> GetSystems() => _systems;
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Scheduler will iterate and execute each child system
        // Profiling tracks each system individually
    }
}

// Usage:
var scheduler = new SystemScheduler();
scheduler.RegisterSystem(new PhysicsSystemGroup());
scheduler.BuildExecutionOrders();

scheduler.ExecutePhase(SystemPhase.Simulation, view, 0.016f);

// All 3 nested systems execute:
// - IntegrateVelocitySystem
// - CollisionDetectionSystem
// - CollisionResponseSystem

// Profiling data available per child system:
var child = group.GetSystems()[0];
var profile = scheduler.GetProfileData(child);
Assert.NotNull(profile);
Assert.Equal(1, profile.ExecutionCount);
```

**Benefits of System Groups:**
- **Organization:** Group related systems together
- **Granular Profiling:** Each system tracked individually
- **Nested Dependencies:** Child systems can have [UpdateAfter] attributes

---

#### Example 4: Cross-Phase Dependencies (Ignored)

From `SystemSchedulerTests.cs` lines 54-64:

```csharp
// Input phase system
[UpdateInPhase(SystemPhase.Input)]
public class KeyboardInputSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Poll keyboard, publish input events
        Console.WriteLine("Input: Reading keyboard");
    }
}

// Export phase system declares dependency on Input system
[UpdateInPhase(SystemPhase.Export)]
[UpdateAfter(typeof(KeyboardInputSystem))] // Cross-phase dependency!
public class NetworkExportSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Export entities to network
        Console.WriteLine("Export: Sending to network");
    }
}

// Test:
var scheduler = new SystemScheduler();
scheduler.RegisterSystem(new KeyboardInputSystem());
scheduler.RegisterSystem(new NetworkExportSystem());

// This does NOT throw - cross-phase dependencies are ignored
scheduler.BuildExecutionOrders();

// Execution order guaranteed by phase order:
// 1. Input phase: KeyboardInputSystem
// ...
// 5. Export phase: NetworkExportSystem
```

**Why Cross-Phase Dependencies Are Ignored:**
- **Phase order handles it:** Input always runs before Export
- **Prevents contradictions:** Can't have Export before Input
- **Simplifies dependency graph:** No need to validate cross-phase edges

---

### API Reference

#### Attributes

##### `[UpdateInPhase(SystemPhase)]`

Specifies which phase this system belongs to.

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class MySystem : IModuleSystem { ... }
```

**Parameters:**
- `SystemPhase phase` - One of: `Input`, `BeforeSync`, `Simulation`, `PostSimulation`, `Export`

**Default:** If not specified, defaults to `SystemPhase.Simulation`

---

##### `[UpdateAfter(typeof(OtherSystem))]`

This system runs **after** the specified system (within the same phase).

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(PhysicsSystem))]
public class AnimationSystem : IModuleSystem { ... }
```

**Parameters:**
- `Type systemType` - The system that must run before this one

**Rules:**
- Only affects systems in the **same phase**
- Can specify multiple `[UpdateAfter]` attributes
- Circular dependencies throw `CircularDependencyException`

---

##### `[UpdateBefore(typeof(OtherSystem))]`

This system runs **before** the specified system (within the same phase).

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateBefore(typeof(RenderSystem))]
public class CameraSystem : IModuleSystem { ... }
```

**Parameters:**
- `Type systemType` - The system that must run after this one

**Equivalent To:**
```csharp
// These are equivalent:
[UpdateBefore(typeof(SystemB))]
class SystemA { }

[UpdateAfter(typeof(SystemA))]
class SystemB { }
```

---

#### SystemScheduler Class

```csharp
public class SystemScheduler
{
    /// <summary>
    /// Register a system for execution.
    /// </summary>
    public void RegisterSystem(IModuleSystem system);
    
    /// <summary>
    /// Build execution orders via topological sort.
    /// Call after registering all systems, before executing.
    /// Throws CircularDependencyException if circular dependencies detected.
    /// </summary>
    public void BuildExecutionOrders();
    
    /// <summary>
    /// Execute all systems in a specific phase.
    /// </summary>
    public void ExecutePhase(SystemPhase phase, ISimulationView view, float deltaTime);
    
    /// <summary>
    /// Get profiling data for a system (execution count, time).
    /// </summary>
    public SystemProfileData? GetProfileData(IModuleSystem system);
}
```

---

#### SystemPhase Enum

```csharp
public enum SystemPhase
{
    Input = 0,
    BeforeSync = 1,
    Simulation = 2,
    PostSimulation = 3,
    Export = 4
}
```

---

### Best Practices

#### ✅ DO: Use Explicit Dependencies

```csharp
// ✅ GOOD: Explicit dependency
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(PhysicsSystem))]
public class AnimationSystem : IModuleSystem
{
    // Clear that animation needs physics results
}
```

**Why:** Makes execution order explicit and self-documenting.

---

#### ✅ DO: Keep Systems Stateless

```csharp
// ✅ GOOD: No state in system
public class MovementSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // All state in components
        var query = view.Query().With<Position>().With<Velocity>().Build();
        foreach (var entity in query)
        {
            var pos = view.GetComponentRO<Position>(entity);
            var vel = view.GetComponentRO<Velocity>(entity);
            // ... update logic
        }
    }
}

// ❌ BAD: State in system
public class BadMovementSystem : IModuleSystem
{
    private List<Entity> _cachedEntities; // DON'T DO THIS!
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Cached state breaks snapshot isolation!
    }
}
```

**Why:** 
- Systems can be reused across modules
- Snapshots remain isolated
- No threading issues

---

#### ✅ DO: Use Phases Correctly

```csharp
// ✅ GOOD: Input in Input phase
[UpdateInPhase(SystemPhase.Input)]
public class KeyboardInputSystem : IModuleSystem { }

// ✅ GOOD: Physics in Simulation phase
[UpdateInPhase(SystemPhase.Simulation)]
public class PhysicsSystem : IModuleSystem { }

// ✅ GOOD: Network export in Export phase
[UpdateInPhase(SystemPhase.Export)]
public class NetworkSyncSystem : IModuleSystem { }
```

**Why:** Phases enforce correct execution order (Input → Simulation → Export).

---

#### ⚠️ DON'T: Create Circular Dependencies

```csharp
// ❌ BAD: Circular dependency
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SystemB))]
public class SystemA : IModuleSystem { }

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(SystemA))]
public class SystemB : IModuleSystem { }

// Throws CircularDependencyException:
scheduler.BuildExecutionOrders(); // ❌ EXCEPTION!
```

**Solution:** Break the cycle by removing one dependency or introducing a third system.

---

#### ⚠️ DON'T: Reference Other Systems Directly

```csharp
// ❌ BAD: Direct system reference
public class AnimationSystem : IModuleSystem
{
    private PhysicsSystem _physics; // DON'T DO THIS!
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var results = _physics.GetCollisionResults(); // Tight coupling!
    }
}

// ✅ GOOD: Communicate via data
public class PhysicsSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Publish singleton or component
        var buffer = view.GetCommandBuffer();
        // ... physics logic
        // Results stored in components
    }
}

public class AnimationSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Read components written by PhysicsSystem
        var query = view.Query().With<CollisionResult>().Build();
        // ... animation logic
    }
}
```

**Why:** Data-oriented design, testability, reusability.

---

#### ⚠️ DON'T: Forget to Call BuildExecutionOrders()

```csharp
// ❌ BAD: Missing BuildExecutionOrders()
var scheduler = new SystemScheduler();
scheduler.RegisterSystem(new SystemA());
scheduler.RegisterSystem(new SystemB());
// Missing: scheduler.BuildExecutionOrders();
scheduler.ExecutePhase(SystemPhase.Simulation, view, 0.016f); // Undefined order!

// ✅ GOOD: Build execution orders
var scheduler = new SystemScheduler();
scheduler.RegisterSystem(new SystemA());
scheduler.RegisterSystem(new SystemB());
scheduler.BuildExecutionOrders(); // ✅ Topological sort
scheduler.ExecutePhase(SystemPhase.Simulation, view, 0.016f); // Deterministic!
```

---

### Troubleshooting

#### Problem: Systems Execute in Wrong Order

**Symptoms:** 
- Animation runs before physics
- Collision detection sees stale positions

**Cause:** Missing or incorrect `[UpdateAfter]` / `[UpdateBefore]` attributes

**Solution:**
```csharp
// Add explicit dependencies
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(PhysicsSystem))] // ✅ Add this
public class AnimationSystem : IModuleSystem { }
```

**Debug Technique:**
Log execution order:
```csharp
public void Execute(ISimulationView view, float deltaTime)
{
    Console.WriteLine($"[{GetType().Name}] Executing");
    // ... system logic
}
```

---

#### Problem: CircularDependencyException at Startup

**Symptoms:**
```
CircularDependencyException: Circular dependency detected: SystemA → SystemB → SystemA
```

**Cause:** Two or more systems depend on each other directly or indirectly

**Solution:**
1. **Break the cycle** - Remove one dependency
2. **Introduce intermediate system** - Split logic into 3 systems
3. **Use different phases** - Move one system to earlier/later phase

**Example Fix:**
```csharp
// ❌ BEFORE (circular):
[UpdateAfter(typeof(SystemB))]
class SystemA { }

[UpdateAfter(typeof(SystemA))]
class SystemB { }

// ✅ AFTER (fixed):
class SystemA { } // No dependency

[UpdateAfter(typeof(SystemA))]
class SystemB { } // B depends on A only
```

---

#### Problem: Cross-Phase Dependency Not Respected

**Symptoms:**
- `[UpdateAfter(typeof(InputSystem))]` on Export system seems ignored

**Cause:** Cross-phase dependencies are **intentionally ignored**

**Solution:** This is **expected behavior**. Phase order handles cross-phase ordering:
- Input (Phase 0) always runs before Export (Phase 4)
- No need for explicit cross-phase dependencies

**Verification:**
```csharp
[UpdateInPhase(SystemPhase.Input)]
public class InputSystem : IModuleSystem { }

[UpdateInPhase(SystemPhase.Export)]
[UpdateAfter(typeof(InputSystem))] // Ignored, but Input still runs first!
public class ExportSystem : IModuleSystem { }

// Execution order guaranteed by phase:
// 1. Input: InputSystem
// 2. Simulation: (other systems)
// 3. Export: ExportSystem ✅
```

---

### Performance Tips

#### Minimize System Count

**Problem:** 100 tiny systems = overhead
**Solution:** Combine related logic into fewer systems

```csharp
// ❌ TOO GRANULAR (overhead):
class UpdateXPositionSystem { }
class UpdateYPositionSystem { }
class UpdateZPositionSystem { }

// ✅ BETTER (combined):
class UpdatePositionSystem 
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Update X, Y, Z in one pass
    }
}
```

**Guideline:** Aim for 10-50 systems per phase, not 100+.

---

#### Profile System Execution Time

```csharp
var scheduler = new SystemScheduler();
// ... register systems, build orders

scheduler.ExecutePhase(SystemPhase.Simulation, view, 0.016f);

// Check which systems are slow
foreach (var system in allSystems)
{
    var profile = scheduler.GetProfileData(system);
    if (profile.AverageTimeMs > 2.0)
    {
        Console.WriteLine($"SLOW: {system.GetType().Name} = {profile.AverageTimeMs}ms");
    }
}
```

**Threshold:** Systems should complete in <1ms on average (60 FPS = 16ms budget).

---

#### Use System Groups for Organization, Not Performance

**Note:** System groups do **not** improve performance - they're for organization only.

```csharp
// System groups just organize systems; scheduler flattens them during execution
[UpdateInPhase(SystemPhase.Simulation)]
public class PhysicsGroup : ISystemGroup
{
    public IReadOnlyList<IModuleSystem> GetSystems() => new[]
    {
        new VelocitySystem(),
        new CollisionSystem()
    };
}

// Execution is identical to:
scheduler.RegisterSystem(new VelocitySystem());
scheduler.RegisterSystem(new CollisionSystem());
```

---

### Thread Safety Considerations

#### Systems on Background Threads

When modules run in `FrameSynced` or `Asynchronous` mode, their systems execute on **background threads**.

**Implications:**
- ✅ **Read components:** Safe (immutable snapshot)
- ✅ **Write via command buffer:** Safe (deferred, serialized)
- ❌ **Direct SetComponent():** Not available on snapshots
- ❌ **Shared mutable state:** Avoid caches, static fields

**Example:**
```csharp
// Module with background execution
public class AIModule : IModule
{
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10); // Async
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new BehaviorTreeSystem()); // Runs on background thread!
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
public class BehaviorTreeSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Running on BACKGROUND THREAD
        
        // ✅ SAFE: Read components
        var agent = view.GetComponentRO<AIAgent>(entity);
        
        // ✅ SAFE: Write via command buffer
        var buffer = view.GetCommandBuffer();
        buffer.SetComponent(entity, newState);
        
        // ❌ UNSAFE: Direct write (not available on snapshot anyway)
        // view.SetComponent(entity, newState); // Does not exist on ISimulationView!
    }
}
```

**Key Takeaway:** Systems within async modules use `ISimulationView` (read-only snapshot) + command buffers (deferred writes).

---

### Cross-References

**Related Sections:**
- [Modules & ModuleHost](#modules--modulehost) - How systems are registered and executed within modules
- [Event Bus](#event-bus) - Systems consume events from the bus
- [Simulation Views & Execution Modes](#simulation-views--execution-modes) - ISimulationView interface used by systems
- [Entity Component System (ECS)](#entity-component-system-ecs) - Components that systems operate on

**API Reference:**
- See [API Reference - Systems & Scheduling](API-REFERENCE.md#systems--scheduling)

**Example Code:**
- `ModuleHost.Core.Tests/SystemSchedulerTests.cs` - Comprehensive system tests
- `FDP/Fdp.Tests/*SystemTests.cs` - Real-world system examples

**Related Batches:**
- BATCH-08 - Geographic Transform Services (uses PostSimulation phase)
- BATCH-07 - Network Gateway (uses Input and Export phases)

---

### ⚠️ Zombie Tasks

If a module exceeds `MaxExpectedRuntimeMs`, it is **abandoned** but **not killed**.

```csharp
public class BadModule : IModule
{
    public void Tick(float dt)
    {
        while (true) { }  // ← Infinite loop!
    }
}

// Result:
// 1. ModuleHost times out after MaxExpectedRuntimeMs
// 2. Circuit breaker trips (module skipped in future frames)
// 3. BUT: The while loop continues running on a thread pool thread (zombie)
```

**Impact:**
- **Thread Leak:** Zombie thread runs forever until app exit
- **CPU Waste:** Zombie consumes 100% of one core
- **Memory Leak:** Any allocations in zombie are never freed

**Mitigation:**
- Test modules with `CancellationToken` internally
- Monitor thread pool usage
- Use separate `AppDomain` or `Process` for untrusted modules (advanced)

---






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

---

### Resilience & Safety

ModuleHost includes built-in mechanisms to protect the simulation from faulty modules.

#### Circuit Breaker Pattern

Each module is protected by a **Circuit Breaker** that monitors its health.
- **Failures**: Exceptions or Timeouts count as failures.
- **Threshold**: If failures exceed `FailureThreshold` (default: 3), the circuit **Opens**.
- **Open State**: The module is disabled (not executed) for `CircuitResetTimeoutMs` (default: 5000ms).
- **Half-Open**: After the timeout, the module runs ONCE on probation. Success resets the circuit; Failure keeps it open.

#### Handling Timeouts & Zombie Tasks

If a module exceeds `MaxExpectedRuntimeMs`, the ModuleHost considers it "timed out".
**IMPORTANT**: The .NET runtime does not allow safely "killing" a thread.

1.  **Zombie Task**: The timed-out task continues running in the background ("zombie").
2.  **Abandonment**: The ModuleHost moves on to the next frame/task immediately.
3.  **Circuit Trip**: Repeated timeouts will open the circuit, preventing *new* tasks from spawning. This effectively limits the number of active zombie tasks to the `FailureThreshold`.
4.  **Resource Leak**: The zombie task consumes memory/CPU until it finishes naturally.

**Best Practice:**
Ensure your module code (especially loops) terminates correctly. While the Host protects the simulation frame rate, it cannot free resources held by a hung thread.

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


### Snapshot Management & Convoy Pattern

**Implemented in:** BATCH-03 ✅

For "Slow" modules (Asynchronous/FrameSynced at < 60Hz), creating individual snapshots (SoD) for each module can be expensive in memory and CPU (memcpy cost).

**The Convoy Pattern** optimizes this by grouping modules that share the same **Update Frequency** and **Execution Mode**. These modules share a **single, immutable snapshot** of the world state.

#### How it Works
1.  **Grouping:** The Kernel automatically groups modules with identical `TargetFrequencyHz` and `Mode`.
2.  **Shared Provider:** A single `SharedSnapshotProvider` is created for the group.
3.  **Lazy Sync:** The snapshot is synced from the live world only when the *first* module in the convoy executes.
4.  **Reference Counting:** The snapshot remains valid until the *last* module finishes its task.
5.  **Pooling:** Once released, the underlying `EntityRepository` returns to a global `SnapshotPool` for reuse (zero GC).

#### Benefits
-   **Memory:** Reduced from `N * SnapshotSize` to `1 * SnapshotSize` per frequency group.
-   **CPU:** `SyncFrom` (memcpy) happens once per group, not once per module.
-   **Consistency:** All modules in the convoy see the exact same frame state.

#### Enabling the Convoy
Simply configure multiple modules with the **exact same** frequency.

```csharp
// Module A
Policy = ModuleExecutionPolicy.FixedInterval(100, ModuleMode.Async); // 10Hz

// Module B
Policy = ModuleExecutionPolicy.FixedInterval(100, ModuleMode.Async); // 10Hz

// Result: Both share ONE snapshot updated every 100ms.
```

#### Snapshot Pooling
To further reduce GC pressure, ModuleHost uses a `SnapshotPool`.
-   **Warmup:** Repositories are pre-allocated at startup.
-   **Recycling:** Used repositories are cleared and returned to the pool.
-   **Configuration:** Pool size defaults to reasonable values but can be tuned via `FdpConfig`.

**Note:** Pooled snapshots retain their buffer capacities, stabilizing memory usage after a warmup period.

#### Optimizing Convoy Performance

**Implemented in:** BATCH-05.1 ✅

By default, convoy snapshots sync **ALL** components (up to 256 component tables), even if modules only need a few. This can waste significant CPU time and memory bandwidth.

**The Solution:** Declare your module's required components explicitly.

```csharp
public class AIModule : IModule
{
    public string Name => "AIModule";
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10); // 10Hz
    
    // Declare which components this module actually uses
    public IEnumerable<Type> GetRequiredComponents()
    {
        yield return typeof(VehicleState);
        yield return typeof(AIBehavior);
        yield return typeof(Position);
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Module only queries the components declared above
        var query = view.Query()
            .With<VehicleState>()
            .With<AIBehavior>()
            .With<Position>()
            .Build();
        
        query.ForEach(entity =>
        {
            var state = view.GetComponentRO<VehicleState>(entity);
            var behavior = view.GetComponentRW<AIBehavior>(entity);
            var pos = view.GetComponentRO<Position>(entity);
            
            // AI logic here...
        });
    }
}
```

**Performance Impact:**
-   **Before:** Convoy syncs 256 component tables (all components)
-   **After:** Convoy syncs 3 component tables (only VehicleState, AIBehavior, Position)
-   **Result:** 50-95% reduction in convoy sync time for focused modules

**Default Behavior (Safe):**
If you **don't** override `GetRequiredComponents()`, the convoy syncs **all** components. This is:
-   ✅ **Safe:** Your module gets all data it might need
-   ⚠️ **Inefficient:** Wastes CPU/memory copying unused components

**Best Practice:**
-   Always declare `GetRequiredComponents()` for performance-critical modules
-   List **all** component types your module reads (from queries AND direct access)
-   Keep the list in sync with your `Tick()` implementation

**Convoy Union Mask:**
When multiple modules share a convoy, their component requirements are combined (union):

```csharp
// Module A needs: VehicleState, AIBehavior
// Module B needs: VehicleState, NetworkState
// 
// Convoy union mask: VehicleState | AIBehavior | NetworkState (3 components total)
```

This ensures each module gets the components it needs while still minimizing data copying.

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

## Entity Lifecycle Management

**Implemented in:** BATCH-06 ⭐

The Entity Lifecycle Manager (ELM) provides cooperative coordination for entity creation and destruction across distributed modules, ensuring entities are fully initialized before becoming active in simulation.

### The Problem

In a distributed module architecture, entities need initialization from multiple systems:
- **Physics** module sets up collision bounds
- **AI** module initializes behavior trees
- **Network** module registers for replication

Without coordination, entities become visible to queries before all modules complete setup, causing:
- ❌ Physics queries see entities without collision data
- ❌ AI tries to pathfind with uninitialized navigation
- ❌ Network replicates incomplete state

### Entity Lifecycle States

Entities progress through three states:

```csharp
public enum EntityLifecycle
{
    Constructing,  // Entity being initialized (not visible to normal queries)
    Active,        // Fully initialized and active in simulation
    TearDown       // Being destroyed (cleanup in progress)
}
```

**State Transitions:**
```
CreateStagedEntity()
    ↓
[Constructing] ──► All modules ACK ──► [Active] ──► BeginDestruction() ──►  [TearDown] ──► All modules ACK ──► Destroyed
    │                                                        │
    └─► NACK/Timeout ──► Destroyed                         └─► Timeout ──► Force Destroyed
```

### Construction Flow

#### 1. Create Staged Entity

```csharp
// Spawner system
var entity = repo.CreateStagedEntity(); // Starts in 'Constructing' state
repo.AddComponent(entity, new VehicleState { ... });

// Register with ELM
var cmd = view.GetCommandBuffer();
elm.BeginConstruction(entity, vehicleTypeId, currentFrame, cmd);
```

#### 2. ELM Publishes Order

```csharp
// ELM automatically publishes
public struct ConstructionOrder
{
    public Entity Entity;
    public int TypeId;  // e.g., VEHICLE_TYPE_ID
}
```

#### 3. Modules Initialize

Modules react to the order and perform their setup:

```csharp
// Physics Module
[UpdateInPhase(SystemPhase.BeforeSync)]
public class PhysicsInitSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        foreach (var order in view.ConsumeEvents<ConstructionOrder>())
        {
            if (order.TypeId == VEHICLE_TYPE_ID)
            {
                // Setup collision
                var bounds = new CollisionBounds { ... };
                cmd.AddComponent(order.Entity, bounds);
                
                // ACK success
                cmd.PublishEvent(new ConstructionAck
                {
                    Entity = order.Entity,
                    ModuleId = PHYSICS_MODULE_ID,
                    Success = true
                });
            }
        }
    }
}
```

#### 4. ELM Activates Entity

When **ALL** registered modules send `ConstructionAck`:
- ELM sets entity state to `Active`
- Entity becomes visible to normal queries
- Simulation continues

**On Failure:**
- Any module sends `Success = false` → Entity immediately destroyed
- Timeout (default 5s) → Entity abandoned and destroyed

### Destruction Flow

#### 1. Begin Destruction

```csharp
// Damage system detects death
if (health.Current <= 0)
{
    elm.BeginDestruction(entity, currentFrame, "Health depleted", cmd);
}
```

#### 2. ELM Publishes Order

```csharp
public struct DestructionOrder
{
    public Entity Entity;
    public FixedString64 Reason;
}
```

#### 3. Modules Cleanup

```csharp
// Network Module
foreach (var order in view.ConsumeEvents<DestructionOrder>())
{
    // Unregister from replication
    networkTable.Unregister(order.Entity);
    
    // Send final state to clients
    SendDestroyMessage(order.Entity);
    
    // ACK cleanup complete
    cmd.PublishEvent(new DestructionAck
    {
        Entity = order.Entity,
        ModuleId = NETWORK_MODULE_ID,
        Success = true
    });
}
```

#### 4. ELM Destroys Entity

When ALL modules ACK:
- Entity destroyed
- Resources freed
- No memory leaks

### Query Filtering

By default, queries only return `Active` entities:

```csharp
// Default: Only active entities
var query = repo.Query()
    .With<VehicleState>()
    .Build();

query.ForEach(entity =>
{
    // Only sees fully constructed, active entities
});
```

**Include Constructing Entities:**
```csharp
// Debug/editor tools
var allQuery = repo.Query()
    .With<VehicleState>()
    .IncludeAll()  // Include Constructing + Active + TearDown
    .Build();
```

**Explicit Filtering:**
```csharp
// Only entities being set up
var constructingQuery = repo.Query()
    .WithLifecycle(EntityLifecycle.Constructing)
    .Build();

// Only entities being destroyed
var teardownQuery = repo.Query()
    .WithLifecycle(EntityLifecycle.TearDown)
    .Build();
```

### ELM Setup

#### 1. Register Participating Modules

```csharp
// ModuleHost initialization
var elm = new EntityLifecycleModule(new[]
{
    PHYSICS_MODULE_ID,  // 1
    AI_MODULE_ID,       // 2
    NETWORK_MODULE_ID   // 3
});

kernel.RegisterModule(elm);
kernel.RegisterModule(physicsModule);  // Must have Id = 1
kernel.RegisterModule(aiModule);       // Must have Id = 2
kernel.RegisterModule(networkModule);  // Must have Id = 3
```

#### 2. Configure Timeouts

```csharp
var elm = new EntityLifecycleModule(
    participatingModules: new[] { 1, 2, 3 },
    timeoutFrames: 300  // 5 seconds at 60 FPS (default)
);
```

### Best Practices

#### ✅ DO:
- Use ELM for multi-system entities (vehicles, characters, buildings)
- Set reasonable timeouts based on module complexity
- Handle `ConstructionOrder` in `BeforeSync` phase for determinism
- Log NACK reasons for debugging

#### ❌ DON'T:
- Use ELM for simple entities (particles, projectiles)
- Block in construction handlers (defeats async purpose)
- Forget to ACK (causes timeout and entity destruction)

### Performance

- **Lifecycle filtering:** O(1) bitwise check in query hot loop
- **ACK tracking:** O(1) dictionary lookup per entity
- **Events:** Unmanaged structs (zero GC)
- **Overhead:** ~50ns per entity query (negligible)

### Statistics

Monitor ELM health:

```csharp
var stats = elm.GetStatistics();

Console.WriteLine($"Pending: {stats.pending}");        // Entities waiting for ACKs
Console.WriteLine($"Constructed: {stats.constructed}");  // Successfully activated
Console.WriteLine($"Destroyed: {stats.destroyed}");      // Successfully cleaned up
Console.WriteLine($"Timeouts: {stats.timeouts}");        // Failed due to timeout
```

---

## Distributed Ownership & Network Integration

**Implemented in:** BATCH-07 + BATCH-07.1 ⭐

ModuleHost integrates with external DDS-based networks (SST protocol) for distributed simulation, allowing multiple nodes to collaboratively control different aspects of the same entity.

### The Challenge: Partial Ownership

In distributed simulations, entities are often **partially owned** by different nodes:

**Example: Tank Entity**
- **Node 1 (Driver Station)** controls movement (`Position`, `Velocity`)
- **Node 2 (Weapon Station)** controls weapon (`WeaponAmmo`, `WeaponHeat`)
- **Both** nodes need to see the complete entity state

**Without Partial Ownership:**
- ❌ Only one node can update the tank
- ❌ Other nodes are read-only spectators
- ❌ No collaborative control

**With Partial Ownership:**
- ✅ Node 1 updates movement descriptors
- ✅ Node 2 updates weapon descriptors
- ✅ Both nodes see synchronized entity
- ✅ True distributed simulation

### Entity Ownership Model

#### Per-Entity Ownership (Simple)

**Use for:** Entities fully controlled by one node.

```csharp
// Entity owned by Node 1
var tank = repo.CreateEntity();
repo.AddComponent(tank, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1,  // Node 1 owns everything
    PartialOwners = new Dictionary<long, int>()  // Empty = no split
});
```

**Behavior:**
- Node 1 publishes **all** descriptors
- Node 2 receives but doesn't publish
- Simple, traditional replication

#### Per-Descriptor Ownership (Advanced)

**Use for:** Collaborative entity control.

```csharp
// Tank with split ownership
repo.AddComponent(tank, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1,  // Node 1 is EntityMaster owner
    PartialOwners = new Dictionary<long, int>
    {
        { 1, 1 },  // EntityState (movement) → Node 1
        { 2, 2 }   // WeaponState → Node 2
    }
});
```

**Behavior:**
- Node 1 publishes `EntityState` (movement)
- Node 2 publishes `WeaponState` (weapon)
- Both nodes receive full synchronized state
- Collaborative control achieved

### Ownership Transfer

Ownership can be transferred dynamically during simulation.

#### Initiating Transfer

```csharp
// Node 3 requests WeaponState ownership
var ownershipUpdate = new OwnershipUpdate
{
    EntityId = tank.NetworkId,
    DescrTypeId = 2,  // WeaponState
    NewOwner = 3       // Transfer to Node 3
};

networkGateway.SendOwnershipUpdate(ownershipUpdate);
```

#### Transfer Protocol

1. **Initiator** sends `OwnershipUpdate` message
2. **Current owner (Node 2)**:
   - Receives message
   - Stops publishing WeaponState
   - Updates local ownership map
3. **New owner (Node 3)**:
   - Receives message
   - Updates local ownership map
   - Publishes WeaponState to "confirm"
   - FDP component metadata updated

**Result:** Ownership transferred smoothly without entity disruption.

### EntityMaster Descriptor

**The EntityMaster descriptor is special** - it controls entity lifecycle.

#### Rules

1. **EntityMaster owner is the "primary" owner**
   - Default owner for all descriptors
   - Stored in `NetworkOwnership.PrimaryOwnerId`

2. **EntityMaster disposal deletes entity**
   - If EntityMaster is disposed on network
   - Local entity is destroyed
   - No orphaned descriptors

3. **Partial owner disposal returns ownership**
   - If Node 2 crashes (owns WeaponState)
   - Ownership returns to EntityMaster owner (Node 1)
   - Simulation continues gracefully

#### Example: Node Crashes

**Scenario:**
- Node 1 owns EntityMaster + EntityState
- Node 2 owns WeaponState
- Node 2 crashes

**Without Disposal Handling:**
- ❌ WeaponState ownership stuck with Node 2 (dead)
- ❌ No updates to weapon ever again
- ❌ Entity broken

**With Disposal Handling (BATCH-07.1):**
```csharp
// DDS publishes NOT_ALIVE_DISPOSED for WeaponState
// Network Gateway detects disposal
HandleDescriptorDisposal(WeaponState)
{
    // Check: Was Node 2 a partial owner?
    if (currentOwner != PrimaryOwnerId)
    {
        // Yes! Return ownership to Node 1
        PartialOwners.Remove(WeaponStateTypeId);
       
        // Falls back to PrimaryOwnerId
        // Node 1 resumes weapon control
    }
}
```

**Result:**
- ✅ Ownership automatically returns to Node 1
- ✅ Node 1 publishes weapon updates
- ✅ Simulation continues
- ✅ Fault tolerance achieved

### FDP Component Metadata Integration

ModuleHost bridges network ownership with FDP's per-component metadata.

#### Component Ownership Sync

```csharp
// When ownership changes for WeaponState descriptor...
ownership.SetDescriptorOwner(WeaponStateTypeId, newOwnerId: 3);

// Automatically sync FDP component metadata
var weaponAmmoTable = repo.GetComponentTable<WeaponAmmo>();
weaponAmmoTable.Metadata.OwnerId = 3;  // Synced!

var weaponHeatTable = repo.GetComponentTable<WeaponHeat>();
weaponHeatTable.Metadata.OwnerId = 3;  // Synced!
```

**Benefits:**
- FDP systems can check ownership natively
- No dependency on network layer
- Consistent ownership model

#### Checking Ownership in Systems

```csharp
// Option 1: Via NetworkOwnership component
var ownership = view.GetComponentRO<NetworkOwnership>(entity);
if (ownership.OwnsDescriptor(WeaponStateTypeId))
{
    // We own this, perform update
}

// Option 2: Via FDP component metadata (cleaner)
var weaponTable = view.GetComponentTable<WeaponAmmo>();
if (weaponTable.Metadata.OwnerId == _localNodeId)
{
    // We own this, perform update
}
```

### Descriptor-Component Mapping

Network descriptors (rich, denormalized) map to FDP components (atomic, normalized).

#### Registration

```csharp
// During NetworkGateway initialization
var ownershipMap = new DescriptorOwnershipMap();

// Map descriptor types to components
ownershipMap.RegisterMapping(
    descriptorTypeId: 1,  // SST.EntityState
    typeof(Position),
    typeof(Velocity),
    typeof(Orientation)
);

ownershipMap.RegisterMapping(
    descriptorTypeId: 2,  // SST.WeaponState
    typeof(WeaponAmmo),
    typeof(WeaponHeat),
    typeof(WeaponType)
);
```

**Why Mapping?**
- Network uses **rich descriptors** (1 message = multiple fields)
- FDP uses **atomic components** (normalized, ECS-friendly)
- Mapping bridges the two models
- Ownership applied to correct components

### Best Practices

#### ✅ DO:

- **Use EntityMaster for primary ownership**
  - Designate one node as EntityMaster owner
  - Other nodes are partial owners

- **Map descriptors to components explicitly**
  - Clear ownership boundaries
  - Easier debugging

- **Handle ownership transfers gracefully**
  - Stop publishing before transfer completes
  - Confirm ownership with DDS write

- **Monitor disposal events**
  - Node crashes are normal in distributed systems
  - Automatic ownership return prevents stalls

#### ❌ DON'T:

- **Publish descriptors you don't own**
  - Violates SST protocol
  - Causes undefined behavior
  - Use ownership checks in egress

- **Forget EntityMaster owner**
  - Always set `PrimaryOwnerId`
  - Fallback for unassigned descriptors

- **Assume ownership is static**
  - Ownership can transfer at runtime
  - Always check before publishing

### Performance

- **Ownership check:** O(1) dictionary lookup
- **Descriptor disposal:** O(1) cleanup
- **Component metadata sync:** O(k) where k = components per descriptor (typically 2-4)
- **Overhead:** ~100ns per descriptor per frame (negligible)

### Error Handling

#### Scenario: Ownership Conflict

**Problem:** Two nodes think they own WeaponState

**Detection:**
```csharp
// NetworkGateway detects duplicate writes
if (lastWriter != ownership.GetOwner(WeaponStateTypeId))
{
    Console.Warn($"Ownership conflict: Expected {expectedOwner}, got {lastWriter}");
}
```

**Resolution:**
- Last writer wins (DDS behavior)
- Log conflict for diagnosis
- Consider forcing ownership transfer

#### Scenario: Missing ACK

**Problem:** Ownership transfer message lost

**Detection:**
- Current owner stops publishing
- New owner never receives message
- Descriptor stalls

**Resolution:**
-Timeout-based retry (application-level)
- Monitor statistics for stalled transfers

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

### Advanced Playback with PlaybackController

The `RecordingReader` provides sequential playback. For interactive replay tools (UI scrubbers, debuggers), use `PlaybackController`:

#### Features

- **Seeking:** Jump to any frame instantly
- **Stepping:** Frame-by-frame navigation (forward/backward)
- **Fast Forward:** Skip ahead at high speed
- **Frame Index:** Built automatically for O(1) random access

#### Setup

```csharp
using Fdp.Kernel.FlightRecorder;

// Load recording
var reader = new RecordingReader("simulation.fdr");
var controller = new PlaybackController(reader, world);

// Build frame index (one-time cost)
controller.BuildIndex();  // Scans recording, creates jump table
```

#### Seeking

```csharp
// Jump to frame 1000
controller.SeekToFrame(1000);

// Jump to specific tick
controller.SeekToTick(1234567890UL);

// Result: World state reflects Frame 1000
```

#### Stepping (Frame-by-Frame Debug)

```csharp
// Step forward one frame
controller.StepForward();

// Step backward one frame
controller.StepBackward();  // Rewinds to last keyframe, replays to target

// Current frame number
int currentFrame = controller.CurrentFrame;
```

#### Fast Forward

```csharp
// Fast forward 100 frames
controller.FastForward(100);

// Fast forward to end
controller.FastForward(controller.TotalFrames - controller.CurrentFrame);
```

#### UI Integration Example

```csharp
public class ReplayDebugger
{
    private PlaybackController _playback;
    private bool _isPaused = true;
    private int _playbackSpeed = 1;  // 1x, 2x, 4x, etc.
    
    public void Update(float deltaTime)
    {
        if (_isPaused)
            return;
        
        // Advance at playback speed
        for (int i = 0; i < _playbackSpeed; i++)
        {
            if (_playback.CurrentFrame < _playback.TotalFrames - 1)
                _playback.StepForward();
        }
    }
    
    public void OnSeekBar(int targetFrame)
    {
        _playback.SeekToFrame(targetFrame);
    }
    
    public void OnStepForward()
    {
        _playback.StepForward();
    }
    
    public void OnStepBackward()
    {
        _playback.StepBackward();
    }
    
    public void OnTogglePause()
    {
        _isPaused = !_isPaused;
    }
    
    public void OnSetSpeed(int speed)
    {
        _playbackSpeed = speed;  // 1x, 2x, 4x
    }
}
```

#### Performance Considerations

- **Seeking Forward:** O(N keyframes) - fast if keyframes are frequent
- **Seeking Backward:** O(N keyframes + M frames) - rewinds to last keyframe, replays forward
- **Frame Index Build:** O(Recording Size) - one-time cost at load
- **Recommendation:** Use keyframes every 60-300 frames for interactive scrubbing

#### Keyframe Strategy

```csharp
var recorder = new RecordingWriter("replay.fdr");

// Configure keyframe frequency
recorder.KeyframeInterval = 120;  // Keyframe every 120 frames

// Trade-off:
// - More keyframes = Faster seeking, Larger file
// - Fewer keyframes = Slower seeking, Smaller file
// Recommended: 60-300 frames (1-5 seconds at 60 FPS)
```

---

### Polymorphic Serialization

When managed components contain **interfaces** or **abstract classes**, the serializer needs type information to deserialize correctly.

#### Problem

```csharp
// Component with interface
public record AIComponent(IAIStrategy Strategy);  // ← Interface!

// Implementation
public class PatrolStrategy : IAIStrategy { ... }
public class AttackStrategy : IAIStrategy { ... }

// Runtime
var entity = world.CreateEntity();
world.AddComponent(entity, new AIComponent(new PatrolStrategy()));

// Serialize → Deserialize
// ❌ ERROR: Serializer doesn't know which concrete type to create!
```

#### Solution: `[FdpPolymorphicType]` Attribute

Tag all concrete implementations with unique IDs:

```csharp
// Define interface
public interface IAIStrategy
{
    void Execute(Entity entity, EntityRepository world);
}

// Tag implementations
[FdpPolymorphicType(1)]  // ← Unique ID
public class PatrolStrategy : IAIStrategy
{
    public Vector3[] WaypointPath { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Patrol logic
    }
}

[FdpPolymorphicType(2)]  // ← Different ID
public class AttackStrategy : IAIStrategy
{
    public Entity Target { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Attack logic
    }
}

[FdpPolymorphicType(3)]
public class FleeStrategy : IAIStrategy
{
    public float FleeDistance { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Flee logic
    }
}
```

#### Registration (Required)

Before serialization, register all polymorphic types:

```csharp
using Fdp.Kernel.FlightRecorder;

var serializer = new FdpPolymorphicSerializer();

// Register interface + implementations
serializer.RegisterPolymorphicType<IAIStrategy, PatrolStrategy>(1);
serializer.RegisterPolymorphicType<IAIStrategy, AttackStrategy>(2);
serializer.RegisterPolymorphicType<IAIStrategy, FleeStrategy>(3);

// Now serialization works
var recorder = new RecordingWriter("replay.fdr", serializer);
```

#### Abstract Classes

Works the same way:

```csharp
[FdpPolymorphicType(10)]
public abstract class Weapon
{
    public abstract void Fire();
}

[FdpPolymorphicType(11)]
public class Rifle : Weapon
{
    public override void Fire() { /* Rifle logic */ }
}

[FdpPolymorphicType(12)]
public class Shotgun : Weapon
{
    public override void Fire() { /* Shotgun logic */ }
}

// Registration
serializer.RegisterPolymorphicType<Weapon, Rifle>(11);
serializer.RegisterPolymorphicType<Weapon, Shotgun>(12);
```

#### Type ID Rules

1. **Unique per type:** IDs must be unique within the same interface/abstract class
2. **Stable:** Don't change IDs after shipping (breaks old recordings)
3. **Avoid 0:** Reserve 0 for "null" polymorphic references
4. **Range:** 1-65535 (ushort)

#### Error Handling

```csharp
// ❌ Missing [FdpPolymorphicType]
public class NewStrategy : IAIStrategy { }

// Runtime error during serialization:
// InvalidOperationException: Type 'NewStrategy' is not registered as polymorphic
```

**Solution:** Always tag concrete types.

#### Best Practices

1. **Centralize registration:** Register all polymorphic types at startup
2. **Document IDs:** Keep a master list of type IDs in comments
3. **Avoid complex hierarchies:** Deep inheritance + polymorphism = serialization complexity
4. **Prefer composition:** Use strategy pattern with interfaces over deep class hierarchies

---

### Concurrent Collections Support

The `FdpAutoSerializer` has explicit support for thread-safe collections:

**Supported:**
- `List<T>`, `Dictionary<K,V>`
- `ConcurrentDictionary<K,V>` ✅
- `Queue<T>`, `ConcurrentQueue<T>` ✅
- `Stack<T>`, `ConcurrentStack<T>` ✅
- `ConcurrentBag<T>` ✅

**Example:**
```csharp
public record ThreadSafeCache(
    ConcurrentDictionary<int, string> Data
);

// Serialization works automatically
world.AddComponent(entity, new ThreadSafeCache(new ConcurrentDictionary<int, string>()));
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

---

## Reactive Scheduling & Component Dirty Tracking

### Overview

**Reactive Scheduling** allows modules to run **only when needed** based on data changes or events, instead of polling every frame. This dramatically reduces CPU usage for event-driven logic.

**Component Dirty Tracking** enables efficient detection of modifications to component tables without per-write overhead.

### Core Interfaces

#### IComponentTable.HasChanges

```csharp
public interface IComponentTable
{
    /// <summary>
    /// Efficiently checks if this table has been modified since the specified version.
    /// Uses lazy scan of chunk versions (O(chunks), typically ~100 chunks for 100k entities).
    /// PERFORMANCE: 10-50ns scan time, L1-cache friendly, no write contention.
    /// 
    /// Uses STRICT INEQUALITY (version > sinceVersion).
    /// Example:
    ///   Version 5: Component written
    ///   HasChanges(4) -> TRUE  (5 > 4)
    ///   HasChanges(5) -> FALSE (5 > 5 is false)
    /// 
    /// Usage: Store lastRunVersion = GlobalVersion BEFORE update,
    ///        Check HasChanges(lastRunVersion) NEXT frame.
    /// </summary>
    bool HasChanges(uint sinceVersion);
    
    // ... other members ...
}
```

#### EntityRepository.HasComponentChanged

```csharp
public class EntityRepository
{
    /// <summary>
    /// Checks if a component table has been modified since the specified tick.
    /// Delegates to the underlying IComponentTable.HasChanges().
    /// </summary>
    /// <param name="componentType">Type of component to check</param>
    /// <param name="sinceTick">Version to compare against</param>
    /// <returns>True if table was modified after sinceTick</returns>
    public bool HasComponentChanged(Type componentType, uint sinceTick)
    {
        if (_componentTables.TryGetValue(componentType, out var table))
        {
            return table.HasChanges(sinceTick);
        }
        return false;
    }
}
```

#### FdpEventBus.HasEvent

```csharp
public class FdpEventBus
{
    /// <summary>
    /// Checks if an unmanaged event of type T exists in the current frame.
    /// O(1) lookup - uses HashSet populated during SwapBuffers().
    /// </summary>
    public bool HasEvent<T>() where T : unmanaged
    {
        return _activeEventIds.Contains(EventType<T>.Id);
    }
    
    /// <summary>
    /// Checks if a managed event of type T exists in the current frame.
    /// </summary>
    public bool HasManagedEvent<T>() where T : class
    {
        return _activeEventIds.Contains(GetManagedTypeId<T>());
    }
    
    /// <summary>
    /// Checks if an event of the specified type exists in the current frame.
    /// Uses reflection-based caching for value types.
    /// </summary>
    public bool HasEvent(Type type)
    {
        if (type.IsValueType)
        {
            // Cached reflection lookup for EventType<T>.Id
            if (!_unmanagedEventIdCache.TryGetValue(type, out int id))
            {
                // ... reflection to get ID, then cache ...
            }
            return _activeEventIds.Contains(id);
        }
        else
        {
            return _activeEventIds.Contains(type.FullName!.GetHashCode() & 0x7FFFFFFF);
        }
    }
}
```

### ModuleExecutionPolicy

```csharp
public struct ModuleExecutionPolicy
{
    public ModuleMode Mode { get; set; }        // FrameSynced or Async
    public TriggerType Trigger { get; set; }    // When to run
    public int IntervalMs { get; set; }         // For Interval trigger
    public Type TriggerArg { get; set; }        // Event/Component type
    
    // Factory methods for easy configuration
    public static ModuleExecutionPolicy DefaultFast { get; }   // FrameSynced, Always
    public static ModuleExecutionPolicy DefaultSlow { get; }   //  Async, Always
    
    public static ModuleExecutionPolicy OnEvent<T>(ModuleMode mode = ModuleMode.FrameSynced);
    public static ModuleExecutionPolicy OnComponentChange<T>(ModuleMode mode = ModuleMode.FrameSynced);
    public static ModuleExecutionPolicy FixedInterval(int ms, ModuleMode mode = ModuleMode.Async);
}

public enum TriggerType
{
    Always,              // Run every frame
    Interval,            // Run every N milliseconds
    OnEvent,             // Run only when specific event published
    OnComponentChange    // Run only when specific component modified
}
```

### Usage Examples

#### Example 1: Event-Driven AI Module

```csharp
public class CombatAIModule : IModule
{
    public string Name => "CombatAI";
    
    // Run ONLY when WeaponFireEvent occurs
    public ModuleExecutionPolicy Policy => 
        ModuleExecutionPolicy.OnEvent<WeaponFireEvent>(ModuleMode.Async);
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Only runs when weapons fire - saves CPU!
        var events = view.GetEvents<WeaponFireEvent>();
        
        foreach (var evt in events)
        {
            // React to gunfire: take cover, return fire, etc.
            ReactToCombat(view, evt);
        }
    }
}
```

**Performance Impact:**
- **Without reactive:** Ticks 60 times/sec = 3,600 ticks/min
- **With reactive:** Ticks only on gunfire = ~10-50 ticks/min
- **CPU savings:** 98%+ for event-driven logic

#### Example 2: Analytics on Damage

```csharp
public class DamageAnalyticsModule : IModule
{
    public string Name => "DamageAnalytics";
    
    // Run ONLY when Health component changes
    public ModuleExecutionPolicy Policy => 
        ModuleExecutionPolicy.OnComponentChange<Health>(ModuleMode.Async);
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Only runs when health changes - efficient!
        var query = view.Query().With<Health>().Build();
        
        foreach (var entity in query)
        {
            var health = view.GetComponentRO<Health>(entity);
            if (health.Current < health.Max * 0.25f)
            {
                LogLowHealthEvent(entity, health);
            }
        }
    }
}
```

#### Example 3: Interval-Based Network Sync

```csharp
public class NetworkSyncModule : IModule
{
    public string Name => "NetworkSync";
    
    // Run every 100ms (10Hz) instead of 60Hz
    public ModuleExecutionPolicy Policy => 
        ModuleExecutionPolicy.FixedInterval(100, ModuleMode.FrameSynced);
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Runs at 10Hz - network-friendly rate
        PublishStateUpdates(view);
    }
}
```

#### Example 4: Combining Multiple Conditions (Manual)

```csharp
public class SmartModule : IModule
{
    public string Name => "SmartModule";
    
    // Default to Always, but check manually in Tick
    public ModuleExecutionPolicy Policy => ModuleExecutionPolicy.DefaultFast;
    
    private uint _lastHealthCheck = 0;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Manual check: Run ONLY if Health changed OR DamageEvent occurred
        bool healthChanged = view.Repository.HasComponentChanged(typeof(Health), _lastHealthCheck);
        bool damageOccurred = view.Bus.HasEvent<DamageEvent>();
        
        if (!healthChanged && !damageOccurred)
            return; // Skip this frame
        
        _lastHealthCheck = view.Repository.GlobalVersion;
        
        // Process logic...
    }
}
```

### Performance Characteristics

#### Component Dirty Tracking

**Implementation:** Lazy scan of chunk versions (not per-entity flags)

```csharp
// Inside NativeChunkTable<T>
public bool HasChanges(uint sinceVersion)
{
    // O(chunks) where chunks << entities
    // For 100k entities @ 16k/chunk = ~6 chunk checks
    for (int i = 0; i < _totalChunks; i++)
    {
        if (_chunkVersions[i] > sinceVersion)
            return true;
    }
    return false;
}
```

**Performance:**
- **Scan time:** 10-50ns (measured in tests)
- **Write cost:** Zero (no dirty flags set)
- **Cache:** L1-friendly (contiguous uint[] array)
- **Thread-safe:** Read-only scan during single-threaded phases

**Why This Design:**

❌ **Per-Entity Dirty Flag Approach:**
```csharp
// BAD: Cache thrashing on every write
public void SetComponent(Entity e, Position pos)
{
    _data[e.Index] = pos;
    _dirtyFlags[e.Index] = true; // ← Cache line contention!
}
```

✅ **Lazy Scan Approach:**
```csharp
// GOOD: Zero write overhead
public void SetComponent(Entity e, Position pos)
{
    _data[e.Index] = pos;
    // Chunk version already updated by existing logic
}
```

#### Event Bus Active Tracking

**Implementation:** HashSet rebuilt during SwapBuffers()

```csharp
public void SwapBuffers()
{
    _activeEventIds.Clear();
    
    // O(streams) not O(events)
    foreach (var stream in _nativeStreams.Values)
    {
        stream.Swap();
        if (stream.GetRawBytes().Length > 0)
        {
            _activeEventIds.Add(stream.EventTypeId); // O(1)
        }
    }
    
    // Same for managed streams...
}
```

**Performance:**
- **Lookup:** O(1) during module dispatch
- **Rebuild:** O(streams) once per frame (~50 stream types max)
- **Memory:** Minimal (HashSet<int> with ~20 active IDs)

### Integration with ModuleHostKernel

The kernel checks trigger conditions in `ShouldRunThisFrame`:

```csharp
private bool ShouldRunThisFrame(ModuleEntry entry)
{
    switch (entry.Policy.Trigger)
    {
        case TriggerType.Always:
            return true;
        
        case TriggerType.Interval:
            // Check accumulated time
            return entry.AccumulatedDeltaTime >= entry.Policy.IntervalMs / 1000.0f;
        
        case TriggerType.OnEvent:
            // Check event bus
            return _liveWorld.Bus.HasEvent(entry.Policy.TriggerArg);
        
        case TriggerType.OnComponentChange:
            // Check dirty tracking
            return _liveWorld.HasComponentChanged(entry.Policy.TriggerArg, entry.LastRunTick);
        
        default:
            return true;
    }
}
```

**Version Tracking for Async Modules:**

```csharp
private void DispatchModules(float deltaTime)
{
    foreach (var entry in _modules)
    {
        if (ShouldRunThisFrame(entry))
        {
            // IMPORTANT: Capture version BEFORE dispatch
            entry.LastRunTick = _liveWorld.GlobalVersion;
            
            entry.CurrentTask = Task.Run(() => entry.Module.Tick(view, accumulated));
        }
    }
}
```

**Why This Matters:** Async modules may span multiple frames. Capturing `GlobalVersion` at dispatch ensures `HasComponentChanged` detects ALL modifications that occurred while the module was running.

### Best Practices

**1. Use Reactive Triggers for Event-Driven Logic**

```csharp
// ✅ GOOD: Only runs when needed
Policy = ModuleExecutionPolicy.OnEvent<CollisionEvent>();

// ❌ BAD: Wastes CPU polling
Policy = ModuleExecutionPolicy.DefaultFast;
void Tick(...)
{
    if (view.GetEvents<CollisionEvent>().Length == 0)
        return; // Still ran for nothing!
}
```

**2. Choose Appropriate Granularity**

```csharp
// ✅ GOOD: Specific component
Policy = ModuleExecutionPolicy.OnComponentChange<Health>();

// ⚠️ ACCEPTABLE: Broader component if needed
Policy = ModuleExecutionPolicy.OnComponentChange<Position>();
// May trigger more often, but still better than Always
```

**3. Combine with Interval for Rate Limiting**

```csharp
// Run on event, but max 10Hz
public void Tick(ISimulationView view, float deltaTime)
{
    _accumulatedTime += deltaTime;
    
    if (_accumulatedTime < 0.1f) // 100ms = 10Hz
        return;
    
    _accumulatedTime = 0;
    
    // Process events at controlled rate...
}
```

**4. Document Trigger Rationale**

```csharp
public class ExplosionVFXModule : IModule
{
    // Runs ONLY on DetonationEvent
    // Rationale: VFX spawning is expensive, event-driven is 99% cheaper
    public ModuleExecutionPolicy Policy => 
        ModuleExecutionPolicy.OnEvent<DetonationEvent>(ModuleMode.Async);
}
```

### Common Pitfalls

**❌ PITFALL 1: Forgetting Version Semantics**

```csharp
// WRONG: Checks same version
uint tick = repo.GlobalVersion;
// ... modify component ...
repo.HasComponentChanged(typeof(Pos), tick); // FALSE!

// CORRECT: Check previous version
uint tick = repo.GlobalVersion;
// ... modify component ...
repo.Tick(); // Increment version
repo.HasComponentChanged(typeof(Pos), tick); // TRUE!
```

**❌ PITFALL 2: Async Module Missing Changes**

```csharp
// WRONG: Capture version AFTER module completes
Task task = Task.Run(() => module.Tick(...));
await task;
entry.LastRunTick = repo.GlobalVersion; // Too late!

// CORRECT: Capture version BEFORE dispatch
entry.LastRunTick = repo.GlobalVersion;
Task task = Task.Run(() => module.Tick(...));
```

**❌ PITFALL 3: Event Check Before SwapBuffers**

```csharp
// WRONG: Events not active yet
repo.Bus.Publish(new FireEvent());
bool hasEvent = repo.Bus.HasEvent<FireEvent>(); // FALSE!

// CORRECT: Check after swap
repo.Bus.Publish(new FireEvent());
repo.Bus.SwapBuffers();
bool hasEvent = repo.Bus.HasEvent<FireEvent>(); // TRUE!
```


---

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



### Deterministic Mode (Lockstep)

For **frame-perfect synchronization** across distributed peers, use **lockstep mode**. The master waits for all slaves to finish each frame before advancing.

#### When to Use

| Mode | Use Case | Sync Variance | Latency Sensitivity |
|------|----------|---------------|---------------------|
| **Continuous** | Real-time simulation | ~10ms | Low (PLL smooths) |
| **Deterministic** | Frame-perfect replay, anti-cheat | 0ms | High (stalls on slow peer) |

**Use Deterministic when:**
- Server must verify client state (anti-cheat)
- Debugging requires exact frame matching
- Replay must be bit-identical to live run

#### Architecture

```
Master                Slave 1              Slave 2
  |                      |                    |
  |---FrameOrder 0------>|                    |
  |---FrameOrder 0-------------------->|
  |                      |                    |
  |                   [Execute               |
  |                    Frame 0]              |
  |                      |                    |
  |<--FrameAck 0---------|                    |
  |                                       [Execute
  |                                        Frame 0]
  |                                           |
  |<--FrameAck 0-----------------------------|
  |                                           |
[All ACKs received]                          |
  |                                           |
  |---FrameOrder 1------>|                    |
  |---FrameOrder 1-------------------->|
  |                   [Execute               |
  |                    Frame 1]          [Execute
  |                      |                Frame 1]
```

#### Setup

**Master:**
```csharp
using ModuleHost.Core.Time;

var nodeIds = new HashSet<int> { 1, 2, 3 };  // IDs of all slave peers

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Master,
    Mode = TimeMode.Deterministic,
    AllNodeIds = nodeIds,  // Required for lockstep
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f  // 60 FPS
    }
};

var controller = TimeControllerFactory.Create(eventBus, timeConfig);
```

**Slave:**
```csharp
var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Slave,
    Mode = TimeMode.Deterministic,
    LocalNodeId = 1,  // This slave's ID
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f  // Must match master
    }
};

var controller = TimeControllerFactory.Create(eventBus, timeConfig);
```

#### Network Messages

Lockstep uses two event types:

```csharp
// Master → Slaves: "Execute Frame N"
[EventId(2001)]
public struct FrameOrderDescriptor
{
    public long FrameID;         // Frame to execute
    public float FixedDelta;     // Timestep (usually constant)
    public long SequenceID;      // For reliability checking
}

// Slaves → Master: "Frame N complete"
[EventId(2002)]
public struct FrameAckDescriptor
{
    public long FrameID;         // Completed frame
    public int NodeID;           // Slave ID
    public double TotalTime;     // For verification
}
```

#### Execution Flow

1. **Master publishes FrameOrder**
2. **Slaves consume FrameOrder, execute frame**
3. **Slaves publish FrameAck**
4. **Master waits for all ACKs**
5. **When all ACKs received** → Master advances to next frame
6. **Repeat**

#### Stalling Behavior

If one slave is slow, **the entire cluster waits**:

```
Frame 50:
Master: Waiting for ACKs from [1, 2, 3]
Slave 1: ACK sent (10ms)
Slave 2: ACK sent (12ms)
Slave 3: Still processing... (500ms)  ← Bottleneck

Master: Stalled (500ms total)
Result: Frame 50 took 500ms for entire cluster
```

**Mitigation:**
- Use **equal hardware** for all slaves
- Set **timeout warnings** in `TimeConfig.SnapThresholdMs`
- Monitor `Console.WriteLine` output: `"[Lockstep] Frame 50 took 500ms"`

#### Debugging

```csharp
// Enable diagnostic logging
var config = new TimeConfig
{
    SnapThresholdMs = 100.0  // Warn if frame > 100ms
};

// Console output:
// [Lockstep] Frame 50 took 523.4ms (threshold: 100.0ms)
// [Lockstep] Late ACK from Node 3: Frame 48 (current: 50)
```

#### Comparison: Continuous vs Deterministic

```csharp
// Continuous Mode (PLL)
var time = controller.Update();
// Returns immediately with best-effort sync
// dt may vary slightly (16.5ms, 16.8ms) due to PLL correction

// Deterministic Mode (Lockstep)
var time = controller.Update();
// May return immediately OR stall waiting for ACKs
// dt is ALWAYS exactly FixedDeltaSeconds (16.667ms)
```

#### Best Practices

1. **Use for verification, not primary gameplay:** Lockstep adds latency
2. **Monitor ACK times:** Identify slow peers proactively
3. **Match hardware:** Heterogeneous clusters will stall
4. **Test with network simulation:** Add artificial latency to catch edge cases

---






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

## Modules & ModuleHost

### Overview

**Modules** are self-contained units of simulation logic in the ModuleHost architecture. They encapsulate related functionality and can execute either through registered systems or directly.

**Key Concepts:**
- `IModule` interface defines the contract
- Two execution patterns: System-based (recommended) or Direct execution
- Modules can run synchronously, at reduced frequency, or asynchronously
- Reactive scheduling based on component changes or events

---

### The IModule Interface

```csharp
public interface IModule
{
    // Identity
    string Name { get; }
    
    // How it runs
    ExecutionPolicy Policy { get; }
    
    // Registration (optional - for system-based modules)
    void RegisterSystems(ISystemRegistry registry);
    
    // Execution (required)
    void Tick(ISimulationView view, float deltaTime);
    
    // Reactive scheduling (optional)
    IReadOnlyList<Type>? WatchComponents { get; }
    IReadOnly List<Type>? WatchEvents { get; }
    
    // Snapshot optimization (optional)
    IEnumerable<Type>? GetRequiredComponents();
}
```

---

### Two Execution Patterns

#### Pattern 1: System-Based Modules (Recommended)

**When to use:**
- Multiple subsystems with different execution phases
- Need phase-specific logic (Input, Simulation, PostSimulation)
- Want better testability and separation of concerns
- Using reactive scheduling

**How it works:**
1. Implement `RegisterSystems()` to register `IModuleSystem` instances
2. Systems tagged with `[UpdateInPhase(phase)]` execute in phase order
3. Leave `Tick()` empty (or use for post-system coordination)
4. Kernel automatically executes systems

**Example:**
```csharp
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Geographic;

public class GeographicTransformModule : IModule
{
    public string Name => "GeographicTransform";
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    private readonly IGeographicTransform _transform;
    private readonly List<IModuleSystem> _systems = new();
    
    public GeographicTransformModule(double lat, double lon, double alt)
    {
        _transform = new WGS84Transform();
        _transform.SetOrigin(lat, lon, alt);
    }
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Input phase: Smooth remote entities
        registry.RegisterSystem(new NetworkSmoothingSystem(_transform));
        
        // PostSimulation phase: Sync owned entities
        registry.RegisterSystem(new CoordinateTransformSystem(_transform));
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Empty - kernel executes systems in phase order
        
        // OR: Optional coordination logic after systems complete
        // _stats.TotalEntities = CountEntities(view);
    }
}
```

**Execution Flow:**
```
Frame Start
  ↓
[Input Phase]
  → NetworkSmoothingSystem.Execute()  // Registered system
  ↓
[Simulation Phase]
  → (Physics, game logic, etc.)
  ↓
[PostSimulation Phase]
  → CoordinateTransformSystem.Execute()  // Registered system
  ↓
→ GeographicTransformModule.Tick()  // Module coordination (empty or stats)
  ↓
Frame End
```

**Benefits:**
- ✅ Systems execute in correct phase order
- ✅ Better testability (mock systems independently)
- ✅ Cleaner separation of concerns
- ✅ Supports reactive scheduling on systems
- ✅ Framework handles execution

---

#### Pattern 2: Direct Execution Modules

**When to use:**
- Simple, self-contained module
- Single responsibility, no subsystems
- Don't need phase-specific execution
- Legacy code or quick prototypes

**How it works:**
1. Implement all logic directly in `Tick()`
2. `RegisterSystems()` remains empty (default implementation)
3. Kernel calls `Tick()` after all system phases complete
4. No phase control

**Example:**
```csharp
public class SimpleStatsModule : IModule
{
    public string Name => "Statistics";
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    private EntityQuery _allEntities;
    private int _frameCount;
    
    public SimpleStatsModule()
    {
        // No systems to register
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // All logic here - runs when kernel calls module.Tick()
        
        _frameCount++;
        
        if (_frameCount % 60 == 0)  // Every second
        {
            var entityCount = view.Query().Build().Count();
            Console.WriteLine($"[Stats] Frame {_frameCount}, Entities: {entityCount}");
        }
    }
}
```

**Execution Flow:**
```
Frame Start
  ↓
[All System Phases Execute]
  → Input, Simulation, PostSimulation systems
  ↓
→ SimpleStatsModule.Tick()  // All module logic here
  ↓
Frame End
```

**Tradeoffs:**
- ✅ Simpler for self-contained modules
- ✅ Full control over execution
- ❌ No phase-specific execution
- ❌ Harder to test (monolithic Tick())
- ❌ Can't benefit from reactive scheduling on subsystems

---

### Execution Policies

Modules specify *how* and *when* they run via `ExecutionPolicy`:

#### Synchronous (Main Thread, Every Frame)
```csharp
public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
```

**Use for:**
- Critical systems (input, physics, rendering prep)
- Fast operations (<1ms)
- Systems that interact with main thread state

**Characteristics:**
- Runs on main thread
- Every frame
- Blocks simulation until complete

---

#### Frame-Synced (Main Thread, Reduced Rate)
```csharp
public ExecutionPolicy Policy => ExecutionPolicy.FrameSynced(targetHz: 30);
```

**Use for:**
- Non-critical updates at reduced rate
- Still needs main thread access
- Example: UI updates, less frequent queries

**Characteristics:**
- Runs on main thread
- At specified frequency (10Hz, 30Hz, etc.)
- Synced to frame timing

**Parameters:**
- `targetHz`: Desired frequency (e.g., 30 for 30Hz)

---

#### Fast Replica (Convoy + Live Data)
```csharp
public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
```

**Use for:**
- Modules needing live + snapshot data
- Background processing with current state visibility
- Complex queries over historical + current data

**Characteristics:**
- Runs on background thread
- Receives snapshots (convoy) + live data access
- Can run concurrently with simulation

**Advanced:** See [Simulation Views & Execution Modes](#simulation-views--execution-modes)

---

#### Slow Background (Async, Reduced Rate)
```csharp
public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(targetHz: 10);
```

**Use for:**
- Heavy computation (pathfinding, AI)
- Infrequent updates
- Non-blocking operations

**Characteristics:**
- Runs on background thread
- At specified frequency
- Works on snapshot data (eventually consistent)

**Parameters:**
- `targetHz`: Update frequency

---

### Reactive Scheduling

Modules can opt into **reactive scheduling** to only execute when relevant changes occur.

#### Watch Components
```csharp
public class UIModule : IModule
{
    // Only run when these components change
    public IReadOnlyList<Type>? WatchComponents => new[]
    {
        typeof(Health),
        typeof(DisplayName),
        typeof(Position)
    };
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // This only executes if Health, DisplayName, or Position changed this frame
        UpdateUI(view);
    }
}
```

**Benefits:**
- ❌ No wasted CPU on unchanged data
- ✅ Event-driven updates
- ✅ Automatically batches changes

---

#### Watch Events
```csharp
public class DamageModule : IModule
{
    // Only run when these events fire
    public IReadOnlyList<Type>? WatchEvents => new[]
    {
        typeof(CollisionEvent),
        typeof(ExplosionEvent)
    };
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Only executes if CollisionEvent or ExplosionEvent fired this frame
        ProcessDamage(view);
    }
}
```

**Use Cases:**
- Responding to game events
- Entity lifecycle coordination (ConstructionAck, DestructionAck)
- Trigger-based logic

---

### Snapshot Optimization (GetRequiredComponents)

Background modules (FastReplica,SlowBackground) receive **snapshots** of simulation state. Specify required components to reduce snapshot size:

```csharp
public class PathfindingModule : IModule
{
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10);
    
    //  Only need 3 components (not all 50+)
    public IEnumerable<Type>? GetRequiredComponents() => new[]
    {
        typeof(Position),
        typeof(NavTarget),
        typeof(NavAgent)
    };
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Snapshot only contains Position, NavTarget, NavAgent
        // 95% smaller than full snapshot!
        ComputePaths(view);
    }
}
```

**Performance Impact:**
- 100 component types, module needs 5: **95% reduction**
- Faster serialization
- Less memory
- Reduced convoy contention

**Return Values:**
- `null` or empty: Include ALL components (safe but large)
- Specific types: Include only these (optimized)

---

### Module Lifecycle

```
1. Registration (Once)
   kernel.RegisterModule(myModule)
     ↓
   myModule.RegisterSystems(registry)  // Collect systems
         ↓
   Systems stored in kernel registry

2. Every Frame
   kernel.Tick(deltaTime)
     ↓
   For each phase (Input, Simulation, PostSim, etc.):
     ↓
     Execute all [UpdateInPhase(phase)] systems
     ↓
   After all phases:
     ↓
     Call myModule.Tick(view, deltaTime)
```

---

### Complete Example: Entity Lifecycle Module

```csharp
using System;
using System.Collections.Generic;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;

/// <summary>
/// Coordinates entity construction/destruction across distributed modules.
/// Pattern: System-based + reactive scheduling
/// </summary>
public class EntityLifecycleModule : IModule
{
    public string Name => "EntityLifecycleManager";
    
    // Synchronous - critical for entity coordination
    public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
    
    // Reactive: Only run when ACK events fire
    public IReadOnlyList<Type>? WatchEvents => new[]
    {
        typeof(ConstructionAck),
        typeof(DestructionAck)
    };
    
    private readonly HashSet<int> _participatingModuleIds;
    private readonly Dictionary<Entity, PendingConstruction> _pending = new();
    
    public EntityLifecycleModule(IEnumerable<int> moduleIds)
    {
        _participatingModuleIds = new HashSet<int>(moduleIds);
    }
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Register system to process ACKs
        registry.RegisterSystem(new LifecycleSystem(this));
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Coordination logic after system executes
        // Could gather statistics, check timeouts, etc.
    }
    
    // Public API for creating entities
    public void BeginConstruction(Entity entity, int typeId, uint frame, IEntityCommandBuffer cmd)
    {
        _pending[entity] = new PendingConstruction
        {
            Entity = entity,
            StartFrame = frame,
            RemainingAcks = new HashSet<int>(_participatingModuleIds)
        };
        
        cmd.PublishEvent(new ConstructionOrder { Entity = entity, TypeId = typeId });
    }
}

[UpdateInPhase(SystemPhase.PostSimulation)]
public class LifecycleSystem : IModuleSystem
{
    private readonly EntityLifecycleModule _module;
    
    public LifecycleSystem(EntityLifecycleModule module)
    {
        _module = module;
    }
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Process ACKs accumulated this frame
        var acks = view.GetEventsOf<ConstructionAck>();
        foreach (var ack in acks)
        {
            _module.ProcessConstructionAck(ack);
        }
    }
}
```

**Why System-Based:**
- LifecycleSystem runs in PostSimulation phase (after entities created)
- Reactive scheduling: Only runs when ACK events fire
- Module provides public API, system handles execution
- Testable: Can mock LifecycleSystem independently

---

### Best Practices

#### ✅ DO: Use System-Based for Complex Modules
```csharp
// Complex module with multiple phases
public class NetworkModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new NetworkIngressSystem());   // Input phase
        registry.RegisterSystem(new NetworkEgressSystem());    // PostSim phase
    }
}
```

#### ✅ DO: Use Reactive Scheduling
```csharp
// Avoid running every frame unnecessarily
public IReadOnlyList<Type>? WatchComponents => new[] { typeof(Health) };
```

#### ✅ DO: Optimize Snapshots
```csharp
// Background modules: Specify required components
public IEnumerable<Type>? GetRequiredComponents() => new[] 
{ 
    typeof(Position), 
    typeof(Velocity) 
};
```

#### ✅ DO: Choose Appropriate Policy
```csharp
// Critical: Synchronous
public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();

// Heavy computation: Background
public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10);
```

#### ⚠️ DON'T: Mix Patterns Unnecessarily
```csharp
// ❌ BAD: Register systems AND implement logic in Tick()
public void RegisterSystems(ISystemRegistry registry)
{
    registry.RegisterSystem(new MySystem());
}

public void Tick(ISimulationView view, float deltaTime)
{
    // Also doing work here - confusing!
    DoSomethingElse();
}

// ✅ GOOD: Choose one pattern
public void Tick(ISimulationView view, float deltaTime)
{
    // Empty -systems handle everything
}
```

#### ⚠️ DON'T: Use Direct Execution for Phase-Sensitive Logic
```csharp
// ❌ BAD: Tick() runs AFTER all phases
public void Tick(ISimulationView view, float deltaTime)
{
    // This runs LATE - after PostSimulation phase!
    // If you need Input phase logic, use a system
}

// ✅ GOOD: Use system with phase attribute
[UpdateInPhase(SystemPhase.Input)]
public class MySystem : IModuleSystem { /* ... */ }
```

---

### Troubleshooting

#### Problem: Systems not executing

**Check:**
1. Did you call `registry.RegisterSystem()`?
2. Does kernel support auto-executing registered systems?
3. If not, execute manually in `Tick()`:

```csharp
public void Tick(ISimulationView view, float deltaTime)
{
    foreach (var system in _systems)
    {
        system.Execute(view, deltaTime);
    }
}
```

---

#### Problem: Module running too often

**Solution:** Use reactive scheduling:
```csharp
public IReadOnlyList<Type>? WatchComponents => new[] { typeof(Health) };
```

Or reduce frequency:
```csharp
public ExecutionPolicy Policy => ExecutionPolicy.FrameSynced(30);  // 30Hz instead of 60Hz
```

---

#### Problem: Background module seeing stale data

**Expected:** Background modules work on snapshots (eventually consistent)

**Solutions:**
1. Use `FastReplica` if you need current data:
   ```csharp
   public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
   ```

2. Or accept staleness for heavy computation:
   ```csharp
   public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10);  // May be 1-2 frames behind
   ```

---

### API Reference

For detailed API documentation, see:
- `IModule` interface (ModuleHost.Core.Abstractions)
- `IModuleSystem` interface
- `ExecutionPolicy` class
- `ISystemRegistry` interface

**Full API:** [API Reference - Module Framework](API-REFERENCE.md#module-framework)

---

## Geographic Transform Services


### Overview

The Geographic Transform Services bridge FDP's local Cartesian coordinate system with global WGS84 geodetic coordinates (latitude/longitude/altitude). This enables:

- **Network Interoperability:** Exchange entity positions in standardized geodetic format
- **Global Positioning:** Place simulations anywhere on Earth
- **Smooth Network Updates:** Interpolate remote entity positions for rendering

**Module:** `Ge GraphicTransformModule`  
**Namespace:** `ModuleHost.Core.Geographic`

---

### Core Concepts

#### Local vs Geodetic Coordinates

**Local (Cartesian):**
- Physics simulation coordinate system
- Origin at chosen geographic point
- X = East, Y = North, Z = Up (ENU tangent plane)
- Units: meters
- Fast for physics calculations

**Geodetic (WGS84):**
- Global coordinate system
- Latitude/Longitude in degrees, Altitude in meters
- Used for network messages and global positioning
- Standardized across distributed nodes

#### Automatic Synchronization

The system automatically keeps local and geodetic coordinates synchronized based on **ownership**:

```
Owned Entities (Physics Authority):
  Position (XYZ) → PositionGeodetic (Lat/Lon/Alt)
  "I control physics, update network state"

Remote Entities (Network Authority):
  PositionGeodetic (Lat/Lon/Alt) → Position (XYZ)
  "Network updates me, interpolate smoothly"
```

---

### Setup

#### 1. Create Module

```csharp
using ModuleHost.Core.Geographic;

// Place simulation origin (San Francisco coords used as example)
var geoModule = new GeographicTransformModule(
    latitudeDeg: 37.7749,
    longitudeDeg: -122.4194,
    altitudeMeters: 0
);

kernel.RegisterModule(geoModule);
```

**Important:** Choose an origin near your simulation area. Accuracy degrades beyond ~100km.

#### 2. Add Components to Entities

```csharp
using ModuleHost.Core.Geographic;
using ModuleHost.Core.Network;

// For networked entities:
var entity = repo.CreateEntity();

// Physics position (local Cartesian)
repo.AddComponent(entity, new Position { Value = new Vector3(100, 0, 50) });

// Geodetic position (for network)
repo.AddComponent(entity, new PositionGeodetic
{
    Latitude = 37.7749,
    Longitude = -122.4194,
    Altitude = 50
});

// Ownership (determines sync direction)
repo.AddComponent(entity, new NetworkOwnership
{
    LocalNodeId = 1,    // This node's ID
    PrimaryOwnerId = 1  // Who owns this entity (1 = us, 2 = remote)
});
```

---

### How It Works

The module runs two systems in sequence:

#### 1. NetworkSmoothingSystem (Input Phase)

**Purpose:** Smooths remote entity positions for rendering

**When:** Input phase (before physics)

**Logic:**
```
For each REMOTE entity:
  1. Read PositionGeodetic (from network update)
  2. Convert to local Cartesian
  3. Lerp current Position toward target (dead reckoning)
  4. Update Position component
```

**Code Flow:**
```csharp
// Remote entity (PrimaryOwnerId != LocalNodeId)
var geoPos = GetManagedComponentRO<PositionGeodetic>(entity);
var targetCartesian = transform.ToCartesian(
    geoPos.Latitude,
    geoPos.Longitude,
    geoPos.Altitude
);

float t = Math.Clamp(deltaTime * 10.0f, 0f, 1f);  // Smoothing factor
Position.Value = Vector3.Lerp(Position.Value, targetCartesian, t);
```

**Smoothing:** Converges to target over ~0.1 seconds (configurable via smoothing factor)

#### 2. CoordinateTransformSystem (PostSimulation Phase)

**Purpose:** Updates geodetic coordinates from physics

**When:** Post-simulation phase (after physics updates Position)

**Logic:**
```
For each OWNED entity:
  1. Read Position (from physics simulation)
  2. Convert to geodetic coordinates
  3. Update PositionGeodetic (for network egress)
```

**Code Flow:**
```csharp
// Owned entity (PrimaryOwnerId == LocalNodeId)
var (lat, lon, alt) = transform.ToGeodetic(Position.Value);

// Only update if changed significantly (epsilon threshold)
if (Math.Abs(geoPos.Latitude - lat) > 1e-6 || ...)
{
    PositionGeodetic = new PositionGeodetic
    {
        Latitude = lat,
        Longitude = lon,
        Altitude = alt
    };
}
```

**Optimization:** Skips update if change < 1e-6 degrees (~10cm) or < 0.1m altitude

---

### Components

#### Position (struct)
Local Cartesian position for physics.

```csharp
public struct Position
{
    public Vector3 Value;  // X=East, Y=North, Z=Up (meters)
}
```

**Used By:** Physics systems, rendering

#### PositionGeodetic (class)
Global geodetic position for networking.

```csharp
public class PositionGeodetic
{
    public double Latitude;   // Degrees (-90 to 90)
    public double Longitude;  // Degrees (-180 to 180)
    public double Altitude;   // Meters above WGS84 ellipsoid
}
```

**Used By:** Network translators, external systems

**Note:** Managed component (class) because doubles + precision requirements.

#### NetworkOwnership (struct)
Determines which node controls entity physics.

```csharp
public struct NetworkOwnership
{
    public int LocalNodeId;     // This node's ID
    public int PrimaryOwnerId;  // Who owns this entity
}
```

**Authority Check:**
```csharp
bool isOwned = ownership.PrimaryOwnerId == ownership.LocalNodeId;
```

---

### Usage Examples

#### Example 1: Positioning an Aircraft

```csharp
// Spawn F-16 over San Francisco Bay
var f16 = repo.CreateEntity();

// Start at specific location
repo.AddComponent(f16, new PositionGeodetic
{
    Latitude = 37.8,          // Over the bay
    Longitude = -122.42,
    Altitude = 1000           // 1km altitude
});

// Ownership: We control this aircraft
repo.AddComponent(f16, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1  // We own it
});

// Physics: Convert geodetic to local on first tick
// (NetworkSmoothingSystem will initialize Position if PrimaryOwner)
// OR manually initialize:
var transform = new WGS84Transform();
transform.SetOrigin(37.7749, -122.4194, 0);
var localPos = transform.ToCartesian(37.8, -122.42, 1000);

repo.AddComponent(f16, new Position { Value = localPos });
```

#### Example 2: Receiving Remote Entity

```csharp
// Network ingress received EntityStateDescriptor for entity ID 100
var remoteEntity = repo.CreateEntity();

// Network data
repo.AddComponent(remoteEntity, new PositionGeodetic
{
    Latitude = 37.75,
    Longitude = -122.45,
    Altitude = 500
});

// Ownership: Remote node controls it
repo.AddComponent(remoteEntity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 2  // Owned by node 2
});

// NetworkSmoothingSystem will automatically:
// - Convert geodetic → local
// - Update Position component
// - Smooth movement each frame
```

#### Example 3: Check if Position Changed

```csharp
// In your custom system
foreach (var entity in _query)
{
    var geo = view.GetManagedComponentRO<PositionGeodetic>(entity);
    
    Console.WriteLine($"Entity at: {geo.Latitude:F6}, {geo.Longitude:F6}, {geo.Altitude:F1}m");
}
```

---

### Coordinate System Details

#### WGS84 Ellipsoid

**Constants:**
- Semi-major axis (a): 6,378,137 meters
- Flattening (f): 1 / 298.257223563
- Eccentricity² (e²): 0.00669437999...

**Transform Method:**
1. Geodetic → ECEF (Earth-Centered, Earth-Fixed)
2. ECEF → Local ENU (East-North-Up tangent plane)
3. Rotation based on origin latitude/longitude

**Precision:**
- **Horizontal:** Sub-centimeter within 10km
- **Horizontal:** ~10cm within 100km
- **Vertical:** ~1m (altitude less critical for most simulations)

#### Coordinate Frame (ENU)

```
      Z (Up)
      |
      |
      |_____Y (North)
     /
    /
   X (East)
```

**Alignment:**
- +X: East
- +Y: North
- +Z: Up (perpendicular to ellipsoid at origin)

**Matches:** Aviation/simulation conventions (not Unity which is Y-up)

---

### Performance

#### Execution Order

```
Frame Start
  ↓
[Input Phase] NetworkSmoothingSystem
  - Inbound: Geodetic → Local (for remote entities)
  ↓
[Simulation Phase] Physics/Game Logic
  - Updates Position for owned entities
  ↓
[PostSimulation Phase] CoordinateTransformSystem
  - Outbound: Local → Geodetic (for owned entities)
  ↓
Frame End
```

#### Costs

**NetworkSmoothingSystem:**
- Per remote entity: ~200 cycles (trig functions)
- 100 remote entities: ~20,000 cycles (~0.007ms @ 3GHz)

**CoordinateTransformSystem:**
- Per owned entity: ~500 cycles (iterative ECEF conversion)
- 100 owned entities: ~50,000 cycles (~0.017ms @ 3GHz)
- **Optimization:** Add dirty checking (future)

**Total Overhead:** <0.03ms for 200 networked entities

---

### Best Practices

#### ✅ DO: Choose Origin Wisely

```csharp
// Place origin near simulation center
var geoModule = new GeographicTransformModule(
    latitudeDeg: 37.7749,   // San Francisco
    longitudeDeg: -122.4194,
    altitudeMeters: 0
);
```

**Why:** Accuracy degrades with distance. Keep entities within 100km of origin.

#### ✅ DO: Use Ownership Correctly

```csharp
// Owned entity: Physics drives geodetic
repo.AddComponent(entity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1  // We own it
});

// Remote entity: Geodetic drives physics
repo.AddComponent(entity, new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 2  // They own it
});
```

#### ✅ DO: Let Systems Handle Sync

Don't manually sync coordinates - the systems do it automatically:

```csharp
// ❌ DON'T DO THIS:
var pos = repo.GetComponentRO<Position>(entity);
var geo = transform.ToGeodetic(pos.Value);
repo.SetManagedComponent(entity, new PositionGeodetic { ... });

// ✅ DO THIS (systems handle it):
// Just update Position for owned entities
repo.SetComponent(entity, new Position { Value = newPos });
// CoordinateTransformSystem will update PositionGeodetic
```

#### ⚠️ DON'T: Fight Ownership

```csharp
// ❌ WRONG: Updating physics for remote entity
if (ownership.PrimaryOwnerId != ownership.LocalNodeId)
{
    // Don't update Position here!
    // Let NetworkSmoothingSystem do it
}
```

#### ⚠️ DON'T: Exceed Range Limit

```csharp
// ❌ WRONG: Entity 200km from origin
var entity = repo.CreateEntity();
repo.AddComponent(entity, new Position { Value = new Vector3(200_000, 0, 0) });
// Geodetic conversion accuracy degraded!

// ✅ CORRECT: Move simulation origin if needed
if (maxDistance > 100_000)
{
    // Re-origin simulation to new center
    geoModule.SetOrigin(newLat, newLon, newAlt);
}
```

---

### Troubleshooting

#### Problem: Remote entities "snap" instead of smooth movement

**Cause:** NetworkTarget component missing or smoothing factor too high

**Solution:**
```csharp
// Ensure entity has all required components
repo.AddComponent(entity, new Position { ... });
repo.AddComponent(entity, new PositionGeodetic { ... });
repo.AddComponent(entity, new NetworkOwnership { ... });
// NetworkTarget not required in BATCH-08.1 (simple lerp)
```

**Note:** BATCH-08.1 uses simple lerp. Future: true dead reckoning with velocity prediction.

#### Problem: Owned entity geodetic not updating

**Cause:** Entity not marked as owned

**Solution:**
```csharp
var ownership = repo.GetComponentRO<NetworkOwnership>(entity);
Debug.Assert(ownership.PrimaryOwnerId == ownership.LocalNodeId);
```

#### Problem: Coordinates inaccurate

**Cause:** Entity too far from origin

**Solution:**
```csharp
// Check distance
var pos = repo.GetComponentRO<Position>(entity);
float distance = pos.Value.Length();

if (distance > 100_000)  // 100km
{
    Console.WriteLine($"Warning: Entity {distance}m from origin. Consider re-origin.");
}
```

---

### Integration with Network Gateway

**Typical Flow (BATCH-07 + BATCH-08):**

```
Owned Entity:
  1. Physics updates Position (local XYZ)
  2. CoordinateTransformSystem → PositionGeodetic (network message)
  3. EntityStateTranslator publishes PositionGeodetic to DDS
  4. Network sends to remote nodes

Remote Entity:
  1. DDS receives PositionGeodetic from network
  2. EntityStateTranslator updates component
  3. NetworkSmoothingSystem → Position (smooth interpolation)
  4. Rendering uses Position
```

**See Also:** [Distributed Ownership & Network Integration](#distributed-ownership--network-integration)

---

### API Reference

For detailed API documentation, see:
- `IGeographicTransform` interface
- `WGS84Transform` implementation
- `CoordinateTransformSystem`
- `NetworkSmoothingSystem`
- `GeographicTransformModule`

**Full API:** [API Reference - Geographic Transform Services](API-REFERENCE.md#geographic-transform-services)


---

## Simulation Views & Execution Modes

### Overview

The **ISimulationView** interface is the abstraction layer between systems/modules and simulation state. It provides **readonly access** to entity data with different execution guarantees depending on the **module's execution mode**.

**What Problems Does ISimulationView Solve:**
- **Thread Safety:** Background modules can't accidentally mutate live state
- **Snapshot Isolation:** Async modules see consistent state even across multiple frames
- **Uniform API:** Same interface works for sync, frame-synced, and async execution
- **Command Buffer Abstraction:** Deferred mutations via command buffers

**When to Use ISimulationView:**
- Always! Systems and modules use `ISimulationView`, never `EntityRepository` directly
- Query entities: `view.Query().With<T>().Build()`
- Read components: `view.GetComponentRO<T>(entity)`
- Consume events: `view.ConsumeEvents<T>()`
- Defer writes: `view.GetCommandBuffer().SetComponent(...)`

---

### Core Concepts

#### The ISimulationView Interface

From `ISimulationViewTests.cs` verification:

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Read-only view of simulation state.
    /// Implemented by EntityRepository (live) and snapshots (replicas).
    /// </summary>
    public interface ISimulationView
    {
        /// <summary>
        /// Current simulation tick (frame number).
        /// </summary>
        uint Tick { get; }
        
        /// <summary>
        /// Simulation time in seconds.
        /// </summary>
        float Time { get; }
        
        /// <summary>
        /// Get command buffer for deferred writes.
        /// </summary>
        IEntityCommandBuffer GetCommandBuffer();
        
        /// <summary>
        /// Read unmanaged component (zero-copy).
        /// </summary>
        ref readonly T GetComponentRO<T>(Entity entity) where T : unmanaged;
        
        /// <summary>
        /// Read managed component (immutable).
        /// </summary>
        T GetManagedComponentRO<T>(Entity entity) where T : class;
        
        /// <summary>
        /// Check if entity is alive.
        /// </summary>
        bool IsAlive(Entity entity);
        
        /// <summary>
        /// Check if entity has component.
        /// </summary>
        bool HasComponent<T>(Entity entity) where T : unmanaged;
        
        /// <summary>
        /// Check if entity has managed component.
        /// </summary>
        bool HasManagedComponent<T>(Entity entity) where T : class;
        
        /// <summary>
        /// Consume unmanaged events (zero-copy span).
        /// </summary>
        ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
        
        /// <summary>
        /// Consume managed events (list).
        /// </summary>
        IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class;
        
        /// <summary>
        /// Build entity query.
        /// </summary>
        QueryBuilder Query();
    }
}
```

**Key Design Points:**
- ✅ **No `IDisposable`:** Views are NOT disposable (managed by providers)
- ✅ **Read-only:** No `SetComponent()` or `CreateEntity()` methods
- ✅ **Deferred Writes:** Use `GetCommandBuffer()` for mutations
- ✅ **Zero-Copy:** `GetComponentRO<T>()` returns `ref readonly` (no allocation)

---

#### Execution Modes & Data Access

**Three execution modes determine what kind of view systems receive:**

| Execution Mode | View Type | Data Source | Thread | Access | Latency |
|----------------|-----------|-------------|--------|--------|---------|
| **Synchronous** | EntityRepository | Live World A | Main | Full R/W | 0 frames |
| **FrameSynced** | Snapshot (GDB) | World B Replica | Background | Read-only | 0-1 frames |
| **Asynchronous** | Snapshot (SoD) | World C Pooled | Background | Read-only | 1-N frames |

---

### Usage Examples

#### Example 1: Query and Read Components

From typical system usage:

```csharp
using ModuleHost.Core.Abstractions;

[UpdateInPhase(SystemPhase.Simulation)]
public class DamageSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Consume damage events
        var damages = view.ConsumeEvents<DamageEvent>();
        
        foreach (var dmg in damages)
        {
            // Check if target is alive
            if (!view.IsAlive(dmg.Target))
                continue;
            
            // Read current health (read-only)
            if (!view.HasComponent<Health>(dmg.Target))
                continue;
                
            ref readonly var health = ref view.GetComponentRO<Health>(dmg.Target);
            
            // Calculate new health (deferred write via command buffer)
            var newHealth = health.Value - dmg.Amount;
            
            var cmd = view.GetCommandBuffer();
            cmd.SetComponent(dmg.Target, new Health { Value = newHealth });
            
            // Publish death event if health <= 0
            if (newHealth <= 0)
            {
                cmd.PublishEvent(new DeathEvent { Entity = dmg.Target });
            }
        }
    }
}
```

**Expected Behavior:**
- Reads `Health` component without copying (ref readonly)
- Writes deferred via command buffer
- Events published to PENDING buffer
- All changes applied after system completes

---

#### Example 2: Entity Query Building

```csharp
public class MovementSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Build query for entities with Position + Velocity
        var query = view.Query()
            .With<Position>()
            .With<Velocity>()
            .Build();
        
        foreach (var entity in query)
        {
            ref readonly var pos = ref view.GetComponentRO<Position>(entity);
            ref readonly var vel = ref view.GetComponentRO<Velocity>(entity);
            
            // Calculate new position
            var newPos = new Position
            {
                X = pos.X + vel.X * deltaTime,
                Y = pos.Y + vel.Y * deltaTime,
                Z = pos.Z + vel.Z * deltaTime
            };
            
            // Deferred write
            view.GetCommandBuffer().SetComponent(entity, newPos);
        }
    }
}
```

**Performance:** Query builds once, iteration is cache-friendly (SOA layout).

---

#### Example 3: Managed Component Access

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class BehaviorTreeSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query()
            .With<AIAgent>()
            .Build();
        
        foreach (var entity in query)
        {
            // Check if entity has managed component
            if (!view.HasManagedComponent<BehaviorTree>(entity))
                continue;
            
            // Read managed component (immutable reference)
            var tree = view.GetManagedComponentRO<BehaviorTree>(entity);
            
            // Execute AI logic (tree is immutable record)
            var nextState = tree.Update(deltaTime);
            
            // Update via command buffer
            view.GetCommandBuffer().SetManagedComponent(entity, nextState);
        }
    }
}
```

**Critical:** Managed components MUST be immutable (records) for snapshot safety.

---

### EntityRepository as ISimulationView

**EntityRepository implements ISimulationView** for synchronous modules:

```csharp
// ModuleHost internally does this:
public void ExecuteSynchronousModule(SyncModule module)
{
    // Pass live repository as ISimulationView
    ISimulationView view = _liveRepository;
    
    module.Tick(view, deltaTime);
    
    // Module sees live state, but uses same API
}
```

**Benefits:**
- Uniform API across all modules
- Can switch module from Sync → Async without changing code
- Testing is easier (can mock ISimulationView)

---

### Snapshot Views (Background Modules)

For **FrameSynced** and **Asynchronous** modules, views are **snapshots**:

#### FrameSynced (GDB - Generalized Double Buffer)

```csharp
// ModuleHost creates persistent replica
private DoubleBufferProvider _fastReplicaProvider;

public void Initialize()
{
    _fastReplicaProvider = new DoubleBufferProvider(_liveRepository);
}

public void Update()
{
    // Every frame: Sync replica from live world
    _fastReplicaProvider.Update(); // SyncFrom diffs
    
    // Dispatch module to background thread
    var view = _fastReplicaProvider.AcquireView();
    
    Task.Run(() =>
    {
        frameSyncedModule.Tick(view, deltaTime);
        // Main thread WAITS for this to complete
    }).Wait();
}
```

**Characteristics:**
- **Persistent:** Same replica instance reused every frame
- **Fast Sync:** Only diffs copied (~0.5-1ms for 100k entities)
- **Low Latency:** 0-1 frame delay
- **Thread-Safe:** Module reads immutable snapshot

---

#### Asynchronous (SoD - Snapshot on Demand)

```csharp
// ModuleHost uses snapshot pool
private OnDemandProvider _slowProvider;

public void Initialize()
{
    // Create provider with component mask
    var mask = GetComponentMask(asyncModule);
    _slowProvider = new OnDemandProvider(_pool, mask);
}

public void ScheduleAsyncModule()
{
    // Acquire pooled snapshot
    var view = _slowProvider.AcquireView(); // Syncs from live world
    
    // Dispatch to background (fire-and-forget)
    Task.Run(() =>
    {
        asyncModule.Tick(view, deltaTime);
        
        // When done, commands harvested and view released
        HarvestCommands(view);
        _slowProvider.ReleaseView(view);
    });
    
    // Main thread continues immediately
}
```

**Characteristics:**
- **Pooled:** Snapshots reused (zero GC in steady state)
- **Selective:** Only syncs components module needs
- **Variable Latency:** Module sees state from when it started
- **No Blocking:** Main thread never waits

---

### Best Practices

#### ✅ DO: Always Use ISimulationView, Never EntityRepository Directly

```csharp
// ✅ GOOD: Accepts abstraction
public class MySystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Works in any execution mode
        var query = view.Query().With<Position>().Build();
    }
}

// ❌ BAD: Depends on concrete type
public class BadSystem : IModuleSystem
{
    private EntityRepository _repo; // Tight coupling!
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var repo = (EntityRepository)view; // Cast breaks with snapshots!
    }
}
```

**Why:** Abstraction allows module to run sync or async without code changes.

---

#### ✅ DO: Use Command Buffers for All Writes

```csharp
// ✅ GOOD: Deferred writes
public void Execute(ISimulationView view, float deltaTime)
{
    var cmd = view.GetCommandBuffer();
    
    foreach (var entity in query)
    {
        cmd.SetComponent(entity, newComponent);
        cmd.PublishEvent(new MyEvent { });
    }
    // Commands applied when system completes
}

// ❌ BAD: Direct writes (not available on snapshots anyway)
public void Execute(ISimulationView view, float deltaTime)
{
    var repo = (EntityRepository)view; // Breaks with snapshots!
    repo.SetComponent(entity, newComponent); // Not thread-safe!
}
```

**Why:** Command buffers are thread-safe and work uniformly across all execution modes.

---

#### ✅ DO: Check Entity Liveness Before Access

```csharp
// ✅ GOOD: Check before accessing
foreach (var entity in damageEvents.Select(e => e.Target))
{
    if (!view.IsAlive(entity))
        continue; // Entity was destroyed
    
    ref readonly var health = ref view.GetComponentRO<Health>(entity);
    // ... process
}

// ❌ BAD: No check
foreach (var entity in targets)
{
    ref readonly var health = ref view.GetComponentRO<Health>(entity);
    // Throws if entity dead!
}
```

**Why:** Entities can be destroyed between frames (especially in async modules).

---

#### ⚠️ DON'T: Assume Snapshot is Up-to-Date

```csharp
// ❌ WRONG ASSUMPTION (Async module):
public void Execute(ISimulationView view, float deltaTime)
{
    // Module started at Frame 100
    // Now it's Frame 105 (main thread advanced)
    // View still sees Frame 100 state!
    
    ref readonly var pos = ref view.GetComponentRO<Position>(entity);
    // Position is STALE (5 frames old)
}
```

**Solution:** Accept that async modules work with stale data. Design accordingly:
- Use for **decision-making** (doesn't need latest state)
- Avoid for **rendering** or **tight feedback loops**

---

#### ⚠️ DON'T: Cache View References Across Frames

```csharp
// ❌ BAD: Caching view
public class BadModule : IModule
{
    private ISimulationView _cachedView; // DON'T!
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        _cachedView = view; // Dangerous!
    }
}

// ✅ GOOD: Use view only within Tick()
public class GoodModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Use view here, don't store it
        var query = view.Query()...;
    }
    // View reference discarded
}
```

**Why:** View lifetime managed by provider (may be pooled/reused).

---

### Troubleshooting

#### Problem: InvalidOperationException - Entity Not Found

**Symptoms:**
```
InvalidOperationException: Entity {Index=42, Gen=3} not found in repository
```

**Cause:** Accessing dead entity without checking `IsAlive()`.

**Solution:**
```csharp
// Always check before accessing
if (!view.IsAlive(entity))
{
    Console.WriteLine($"Entity {entity} is dead, skipping");
    continue;
}

ref readonly var component = ref view.GetComponentRO<T>(entity);
```

---

#### Problem: Stale Data in Async Module

**Symptoms:**
- AI makes decisions based on old positions
- Pathfinding uses outdated obstacles

**Cause:** Async module sees snapshot from when it started (could be multiple frames old).

**Solution:** This is **by design**. Async modules work with stale data.

**Mitigation:**
1. **Accept staleness:** Design AI to tolerate old data
2. **Increase frequency:** Run at 30Hz instead of 10Hz
3. **Use FrameSynced:** If staleness is critical

---

#### Problem: Command Buffer Changes Not Visible

**Symptoms:**
- Call `cmd.SetComponent(entity, newValue)`
- Next system sees old value

**Cause:** Command buffers are **deferred** - applied after module completes.

**Solution:** Understand command buffer semantics:

```csharp
// Frame N:
public void Execute(ISimulationView view, float dt)
{
    var cmd = view.GetCommandBuffer();
    cmd.SetComponent(entity, new Health { Value = 50 });
    
    // Changes NOT visible yet (still in buffer)
    ref readonly var health = ref view.GetComponentRO<Health>(entity);
    Assert.NotEqual(50, health.Value); // Still old value!
}

// After module completes:
// - Commands harvested
// - Applied to live world
// - Visible next frame
```

---

### Performance Characteristics

#### View Access Overhead

| Operation | Cost | Notes |
|-----------|------|-------|
| `view.GetComponentRO<T>()` | **~5ns** | Zero-copy, ref return |
| `view.IsAlive(entity)` | **~3ns** | Bit check |
| `view.Query().Build()` | **~50ns** | Query caching amortizes cost |
| `view.ConsumeEvents<T>()` | **~10ns** | ReadOnlySpan (no allocation) |
| `view.GetCommandBuffer()` | **~2ns** | Cached reference |

**Compared to Direct EntityRepository Access:** ISimulationView adds negligible overhead (~1-2ns per call).

---

#### Snapshot Creation Cost

**GDB (Generalized Double Buffer):**
- Initial allocation: 100k entities × 20 components = ~50MB (~10ms one-time)
- Per-frame sync: Only diffs copied (~0.5-1ms for 1000 changed entities)
- Memory: 2× live world (double buffer)

**SoD (Snapshot on Demand):**
- Acquire from pool: ~0.01ms
- Sync selective components: 0.1-0.5ms (depends on component mask)
- Memory: 1× snapshot (pooled)
- Release to pool: ~0.005ms

---

### Cross-References

**Related Sections:**
- [Entity Component System (ECS)](#entity-component-system-ecs) - Components accessed via ISimulationView
- [Systems & Scheduling](#systems--scheduling) - Systems receive ISimulationView
- [Modules & ModuleHost](#modules--modulehost) - Execution modes determine view type
- [Event Bus](#event-bus) - Events consumed via `view.ConsumeEvents<T>()`

**API Reference:**
- See [API Reference - Simulation Views](API-REFERENCE.md#simulation-views)

**Example Code:**
- `FDP/Fdp.Tests/ISimulationViewTests.cs` - Interface verification
- `FDP/Fdp.Tests/EntityRepositoryAsViewTests.cs` - EntityRepository as view
- `ModuleHost.Core.Tests/ISimulationViewTests.cs` - ModuleHost integration

**Related Batches:**
- BATCH-03 - Snapshot on Demand implementation
- BATCH-04 - GDB (Generalized Double Buffer) implementation

---

---

## Flight Recorder & Deterministic Replay

### Overview

The **Flight Recorder** system captures simulation state changes to disk for **exact replay** and **deterministic validation**. It enables debugging, testing, and proof-of-correctness for complex distributed simulations.

**What Problems Does Flight Recorder Solve:**
- **Debugging:** Replay exact scenario that caused a bug
- **Determinism Validation:** Verify simulation is deterministic (same inputs → same outputs)
- **Audit Trail:** Compliance and proof-of-execution for critical systems
- **Testing:** Capture production scenarios for regression tests

**When to Use Flight Recorder:**
- Debugging non-deterministic bugs
- Validating distributed synchronization
- Compliance requirements (aerospace, medical, financial)
- Automated testing with real scenarios

---

### Core Concepts

#### Recording Modes

**Two recording strategies:**

1. **Keyframe + Deltas:**
   - Frame 0: Full snapshot (keyframe)
   - Frame 1-99: Only changed components (delta)
   - Frame 100: Full snapshot (new keyframe)
   - Repeat

2. **Keyframe-Only:**
   - Every frame: Full snapshot
   - Simpler but larger files

**Default:** Keyframe every 100 frames + deltas (optimal for most use cases).

---

#### File Format

**Binary format (.fdp file):**

```
┌───────────────────────────────────────┐
│ Header                                │
│ - Magic:   "FDPREC" (6 bytes)        │
│ - Version: uint32 (format version)   │
│ - Timestamp: int64 (recording time)  │
└───────────────────────────────────────┘
┌───────────────────────────────────────┐
│ Frame 0 (Keyframe)                    │
│ - FrameType: byte (0 = keyframe)     │
│ - Tick:     uint32                    │
│ - EntityCount: int                    │
│ - Component Data (all entities)       │
│ - Destruction Log                     │
└───────────────────────────────────────┘
┌───────────────────────────────────────┐
│ Frame 1 (Delta)                       │
│ - FrameType: byte (1 = delta)        │
│ - Tick: uint32                        │
│ - ChangedEntityCount: int             │
│ - Component Data (changed only)       │
│ - Destruction Log                     │
└───────────────────────────────────────┘
...
```

---

#### Change Detection

**How the recorder knows what changed:**

```csharp
// When you call:
repository.Tick(); // GlobalVersion++ (e.g., 42 → 43)

// And modify a component:
repository.SetComponent(entity, new Position { X = 10 });
// Component stamped with version 43

// Recorder captures delta:
recorder.CaptureFrame(repository, sinceTick: 42);
// Internally queries: "Components with version > 42"
// Result: Only changed components written
```

**Critical:** Must call `repository.Tick()` every frame for change detection to work!

---

### Usage Examples

#### Example 1: Record and Replay Single Entity

From `FlightRecorderTests.cs` lines 34-75:

```csharp
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;

[Fact]
public void RecordAndReplay_SingleEntity_RestoresCorrectly()
{
    string filePath = "test_recording.fdp";
    
    // ===== RECORDING =====
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var entity = recordRepo.CreateEntity();
    recordRepo.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    
    // Record to file
    using (var recorder = new AsyncRecorder(filePath))
    {
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
    }
    
    // ===== REPLAY =====
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>(); // Must match recording!
    
    using (var reader = new RecordingReader(filePath))
    {
        bool hasFrame = reader.ReadNextFrame(replayRepo);
        Assert.True(hasFrame); // Frame read successfully
    }
    
    // Verify restored state
    Assert.Equal(1, replayRepo.EntityCount);
    
    var query = replayRepo.Query().With<Position>().Build();
    foreach (var e in query)
    {
        ref readonly var pos = ref replayRepo.GetComponentRO<Position>(e);
        Assert.Equal(10f, pos.X);
        Assert.Equal(20f, pos.Y);
        Assert.Equal(30f, pos.Z);
    }
}
```

**Expected Output:**
- Entity created with Position component
- State captured to file
- File replayed into new repository
- Position values match exactly

---

#### Example 2: Record Deltas (Only Changed Entities)

From `FlightRecorderTests.cs` lines 111-159:

```csharp
[Fact]
public void RecordDelta_OnlyChangedEntities_RecordsCorrectly()
{
    string filePath = "test_delta.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var e1 = recordRepo.CreateEntity();
    var e2 = recordRepo.CreateEntity();
    recordRepo.AddComponent(e1, new Position { X = 1, Y = 1, Z = 1 });
    recordRepo.AddComponent(e2, new Position { X = 2, Y = 2, Z = 2 });
    
    using (var recorder = new AsyncRecorder(filePath))
    {
        // Frame 0: Keyframe (both entities)
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
        
        // Frame 1: Modify ONLY e1
        recordRepo.Tick();
        ref var pos = ref recordRepo.GetComponentRW<Position>(e1);
        pos.X = 100; // Only e1 changed!
        
        // Capture delta (only e1 written to file)
        recorder.CaptureFrame(recordRepo, recordRepo.GlobalVersion - 1, blocking: true);
    }
    
    // Replay
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo); // Keyframe: both entities
        reader.ReadNextFrame(replayRepo); // Delta: e1 updated
    }
    
    // Verify: e1 has updated position, e2 unchanged
    var query = replayRepo.Query().With<Position>().Build();
    foreach (var e in query)
    {
        ref readonly var pos = ref replayRepo.GetComponentRO<Position>(e);
        if (e.Index == e1.Index)
        {
            Assert.Equal(100f, pos.X); // Updated!
        }
        else if (e.Index == e2.Index)
        {
            Assert.Equal(2f, pos.X); // Unchanged
        }
    }
}
```

**Performance Benefit:** Delta recording reduces file size by ~90% for typical simulations.

---

#### Example 3: Record Entity Destruction

From `FlightRecorderTests.cs` lines 191-229:

```csharp
[Fact]
public void RecordAndReplay_EntityDestruction_RemovesEntity()
{
    string filePath = "test_destruction.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var e1 = recordRepo.CreateEntity();
    var e2 = recordRepo.CreateEntity();
    recordRepo.AddComponent(e1, new Position { X = 1, Y = 1, Z = 1 });
    recordRepo.AddComponent(e2, new Position { X = 2, Y = 2, Z = 2 });
    
    using (var recorder = new AsyncRecorder(filePath))
    {
        // Frame 0: Keyframe (2 entities)
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
        
        // Frame 1: Destroy e1
        recordRepo.Tick();
        recordRepo.DestroyEntity(e1);
        
        recorder.CaptureFrame(recordRepo, recordRepo.GlobalVersion - 1, blocking: true);
    }
    
    // Replay
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo); // Keyframe: 2 entities
        reader.ReadNextFrame(replayRepo); // Delta: e1 destroyed
    }
    
    // Verify: e1 destroyed, e2 alive
    Assert.Equal(1, replayRepo.EntityCount);
    Assert.False(replayRepo.IsAlive(e1)); // Destroyed!
    Assert.True(replayRepo.IsAlive(e2));  // Still alive
}
```

**Key Insight:** Destruction log is part of frame data - replays are **exact**, including entity lifecycles.

---

#### Example 4: Large-Scale Recording (Performance Test)

From `FlightRecorderTests.cs` lines 727-761:

```csharp
[Fact]
public void RecordKeyframe_LargeEntityCount_CompletesSuccessfully()
{
    string filePath = "test_large.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    recordRepo.RegisterComponent<Velocity>();
    
    const int entityCount = 1000;
    
    // Create 1000 entities
    for (int i = 0; i < entityCount; i++)
    {
        var e = recordRepo.CreateEntity();
        recordRepo.AddComponent(e, new Position { X = i, Y = i, Z = i });
        recordRepo.AddComponent(e, new Velocity { X = 1, Y = 1, Z = 1 });
    }
    
    // Record
    using (var recorder = new AsyncRecorder(filePath))
    {
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
    }
    
    // Replay and verify
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    replayRepo.RegisterComponent<Velocity>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo);
    }
    
    Assert.Equal(entityCount, replayRepo.EntityCount);
}
```

**Performance:** 1000 entities with 2 components each records in <10ms.

---

### API Reference

#### AsyncRecorder Class

```csharp
public class AsyncRecorder : IDisposable
{
    /// <summary>
    /// Create recorder writing to file.
    /// </summary>
    public AsyncRecorder(string filePath);
    
    /// <summary>
    /// Capture full keyframe (all entities).
    /// </summary>
    public void CaptureKeyframe(EntityRepository repository);
    
    /// <summary>
    /// Capture delta frame (only changed entities since sinceTick).
    /// </summary>
    /// <param name="blocking">If true, waits for write before returning</param>
    public void CaptureFrame(EntityRepository repository, uint sinceTick, bool blocking = false);
    
    /// <summary>
    /// Number of successfully recorded frames.
    /// </summary>
    public int RecordedFrames { get; }
    
    /// <summary>
    /// Number of dropped frames (async buffer full).
    /// </summary>
    public int DroppedFrames { get; }
    
    /// <summary>
    /// Flush pending writes and close file.
    /// </summary>
    public void Dispose();
}
```

---

#### RecordingReader Class

```csharp
public class RecordingReader : IDisposable
{
    /// <summary>
    /// Open recording file for replay.
    /// Throws InvalidDataException if file corrupt.
    /// </summary>
    public RecordingReader(string filePath);
    
    /// <summary>
    /// File format version.
    /// </summary>
    public uint FormatVersion { get; }
    
    /// <summary>
    /// Recording timestamp (Unix epoch).
    /// </summary>
    public long RecordingTimestamp { get; }
    
    /// <summary>
    /// Read next frame into repository.
    /// Returns false if end of file reached.
    /// </summary>
    public bool ReadNextFrame(EntityRepository repository);
    
    /// <summary>
    /// Close file.
    /// </summary>
    public void Dispose();
}
```

---

### Best Practices

#### ✅ DO: Call Tick() Every Frame

```csharp
// ✅ GOOD: Tick called before modifications
void Update()
{
    _repository.Tick(); // GlobalVersion++
    
    // Modify components
    SetComponent(entity, new Position { X = 10 });
    // Component stamped with current version
    
    // Record delta
    _recorder.CaptureFrame(_repository, _repository.GlobalVersion - 1);
}

// ❌ BAD: Forgot Tick()
void Update()
{
    // Missing: _repository.Tick();
    
    SetComponent(entity, new Position { X = 10 });
    // Component stamped with STALE version
    
    _recorder.CaptureFrame(...); // Records nothing (no changes detected)!
}
```

---

#### ✅ DO: Use Keyframe + Delta for Production

```csharp
// ✅ GOOD: Hybrid recording
for (int frame = 0; frame < 1000; frame++)
{
    _repository.Tick();
    
    if (frame % 100 == 0)
    {
        _recorder.CaptureKeyframe(_repository); // Every 100 frames
    }
    else
    {
        _recorder.CaptureFrame(_repository, sinceTick: frame - 1);
    }
}

// Result: 90% smaller file, fast seeking (jump to nearest keyframe)
```

---

#### ✅ DO: Sanitize Dead Entities Before Recording

The Flight Recorder automatically **sanitizes** dead entities (zeros their memory) to prevent leaking deleted data into recordings.

**Why This Matters:**
```csharp
// Frame 1: Create entity with secret data
var entity = repo.CreateEntity();
repo.AddComponent(entity, new SecretData { Password = "hunter2" });

// Frame 2: Destroy entity
repo.DestroyEntity(entity);

// Without sanitization:
// - Entity data still in memory
// - Recorded to file
// - Replay shows deleted secrets!

// With sanitization (automatic):
// - Destroy marks entity dead
// - Recorder zeros the memory slot
// - Recording contains only zeros
// - Replays are clean
```

This is handled automatically by the recorder.

---

#### ⚠️ DON'T: Forget to Register Components Before Replay

```csharp
// ❌ BAD: Component not registered
using var replayRepo = new EntityRepository();
// Missing: replayRepo.RegisterComponent<Position>();

using var reader = new RecordingReader("recording.fdp");
reader.ReadNext Frame(replayRepo); // EXCEPTION - component type unknown!

// ✅ GOOD: Register all components
using var replayRepo = new EntityRepository();
replayRepo.RegisterComponent<Position>();
replayRepo.RegisterComponent<Velocity>();

using var reader = new RecordingReader("recording.fdp");
reader.ReadNextFrame(replayRepo); // Works!
```

---

#### ⚠️ DON'T: Use Managed Components with Mutable State

```csharp
// ❌ BAD: Mutable managed component
public class BadAIState
{
    public List<Vector3> Waypoints; // Mutable!
}

// Recording:
var state = new BadAIState { Waypoints = new() { vec1, vec2 } };
repo.AddManagedComponent(entity, state);
// Shallow copy to snapshot
// Later, code modifies state.Waypoints
// Replay is corrupted!

// ✅ GOOD: Immutable record
public record GoodAIState
{
    public required ImmutableList<Vector3> Waypoints { get; init; }
}

// Recording:
var state = new GoodAIState { Waypoints = ImmutableList.Create(vec1, vec2) };
repo.AddManagedComponent(entity, state);
// Shallow copy is safe (immutable)
// Replay is exact!
```

---

### Troubleshooting

#### Problem: InvalidDataException on ReadNextFrame

**Symptoms:**
```
InvalidDataException: Invalid magic bytes. Expected 'FDPREC', got '...'
```

**Cause:** File corrupted or not a valid FDP recording.

**Solution:**
```csharp
// Validate file before reading
try
{
    using var reader = new RecordingReader(filePath);
    Console.WriteLine($"Format version: {reader.FormatVersion}");
    Console.WriteLine($"Recorded: {DateTimeOffset.FromUnixTimeSeconds(reader.RecordingTimestamp)}");
}
catch (InvalidDataException ex)
{
    Console.Error($"Invalid recording file: {ex.Message}");
}
```

---

#### Problem: Replay Diverges from Original

**Symptoms:**
- Replay starts identically but diverges after N frames
- Position values differ by small amounts

**Causes:**
1. **Non-determinism:** Random number generator, DateTime.Now, etc.
2. **Missing Tick():** Change detection broken
3. **Floating-point precision:** Different CPU architectures

**Solutions:**

**1. Use Deterministic Random:**
```csharp
// ❌ BAD: Non-deterministic
float angle = Random.Shared.NextSingle(); // Different every replay!

// ✅ GOOD: Seeded random
var rng = new Random(seed: 42);
float angle = rng.NextSingle(); // Same every replay
```

**2. Verify Tick() Called:**
```csharp
void Update()
{
    _repository.Tick(); // MUST call!
    // ... simulation ...
}
```

**3. Accept Floating-Point Variance:**
```csharp
// ✅ GOOD: Epsilon comparison
const float epsilon = 0.0001f;
bool positionsMatch = Math.Abs(recorded.X - replayed.X) < epsilon;
```

---

#### Problem: RecordedFrames vs DroppedFrames Mismatch

**Symptoms:**
```
Warning: Recorder reported 95 frames recorded, 5 frames dropped
```

**Cause:** Async recorder buffer full (recording thread slower than game thread).

**Solutions:**

**1. Use Blocking Mode:**
```csharp
// Frame will wait for write to complete
_recorder.CaptureFrame(_repository, sinceTick, blocking: true);
```

**2. Reduce Frame Rate:**
```csharp
// Record every 2nd frame instead of every frame
if (_frameCount % 2 == 0)
{
    _recorder.CaptureFrame(...);
}
```

**3. Use Faster Storage (SSD vs HDD):**
- Async mode can drop frames on slow HDDs
- SSDs typically have no drops

---

### Performance Characteristics

#### Recording Overhead

| Operation | Time (1000 entities) | File Size |
|-----------|----------------------|-----------|
| **Keyframe** | 5-10ms | ~500 KB |
| **Delta (10% changed)** | 0.5-1ms | ~50 KB |
| **Delta (50% changed)** | 2-3ms | ~250 KB |

**Optimizations:**
- Binary format (no JSON overhead)
- Delta compression (only changes)
- Async writes (doesn't block game thread)
- Sparse entity support (skips empty chunks)

---

#### File Size Examples

**1000 frames, 100 entities, 2 components:**
- Keyframe-only: ~50 MB
- Keyframe + Delta (every 100 frames): ~5 MB (10× smaller!)

**1000 frames, 10,000 entities, 5 components:**
- Keyframe-only: ~5 GB
- Keyframe + Delta: ~500 MB (10× smaller!)

---

### Cross-References

**Related Sections:**
- [Entity Component System (ECS)](#entity-component-system-ecs) - Components recorded by Flight Recorder
- [Event Bus](#event-bus) - Events can be recorded for replay
- [Modules & ModuleHost](#modules--modulehost) - ModuleHost can integrate recorder
- [Simulation Views](#simulation-views--execution-modes) - Snapshots used for async recording

**API Reference:**
- See [API Reference - Flight Recorder](API-REFERENCE.md#flight-recorder)

**Example Code:**
- `FDP/Fdp.Tests/FlightRecorderTests.cs` - Comprehensive recorder tests (764 lines)
- `FDP/Fdp.Tests/FlightRecorderIntegrationTests.cs` - End-to-end scenarios
- `FDP/Fdp.Tests/EventBusFlightRecorderIntegrationTests.cs` - Event recording

**Related Batches:**
- None (core FDP feature)

---


---

## Network Integration

### Overview

**Network Integration** in FDP-ModuleHost enables **distributed simulations** where multiple nodes collaborate to simulate a shared world. This section consolidates all network-related concepts and provides an integration guide.

**What Network Integration Provides:**
- **Entity Synchronization:** Share entities across network nodes via DDS
- **Partial Ownership:** Different nodes control different aspects of same entity
- **Lifecycle Coordination:** Dark construction and coordinated teardown across network
- **Geographic Transforms:** Network messages in geodetic coordinates, local sim in Cartesian
- **Fault Tolerance:** Node crashes handled gracefully with ownership recovery

**Architecture:**

```
┌──────────────────────────────────────────────────────────────┐
│ Local Node (FDP Simulation)                                 │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  EntityRepository (Live World)                               │
│  ├─ Entity: Tank #42                                         │
│  │  ├─ LocalPosition {X,Y,Z}              (Local)           │
│  │  ├─ PositionGeodetic {Lat,Lon,Alt}     (Network)         │
│  │  ├─ EntityState {Velocity, Heading}    (Owned, Published)│
│  │  └─ WeaponState {Aim, Ammo}            (Remote, Received)│
│  │                                                           │
│  └─ Modules:                                                 │
│     ├─ EntityLifecycleModule  (Coordinates construction)     │
│     ├─ GeographicTransformModule (Local ↔ Geodetic)          │
│     └─ NetworkGatewayModule   (DDS Pub/Sub)                 │
│                                                              │
└──────────────────────────────────────────────────────────────┘
                          ↕ DDS Network
┌──────────────────────────────────────────────────────────────┐
│ Remote Node (FDP Simulation)                                │
│  ├─ Tank #42 (Replica)                                       │
│  │  ├─ EntityState (Received, Remote-owned)                  │
│  │  └─ WeaponState (Owned locally, Published)                │
└──────────────────────────────────────────────────────────────┘
```

**Key Concepts:**
- **Descriptors:** Network data structures (SST format, rich schemas)
- **Components:** ECS data (FDP format, atomic, cache-friendly)
- **Ownership:** Who publishes which descriptors
- **Translators:** Bridge between descriptors and components
- **Lifecycle:** Coordinated entity creation/destruction

---

### Core Integration Points

#### 1. Entity Lifecycle Management (ELM)

**Purpose:** Coordinate entity creation and destruction across distributed nodes.

**Dark Construction Pattern:**
```csharp
// Node 1: Create new tank entity
var tank = _repository.CreateEntity();

// Set lifecycle: Constructing (invisible to other systems)
_lifecycleModule.SetLifecycleState(tank, EntityLifecycle.Constructing);

// Initialize components
_repository.AddComponent(tank, new Position { ... });
_repository.AddComponent(tank, new EntityState { ... });

// Publish ConstructionRequest to network
_bus.Publish(new ConstructionRequest 
{ 
    EntityId = tank.NetworkId,
    Modules = new[] { PHYSICS_MODULE_ID, AI_MODULE_ID, NETWORK_MODULE_ID }
});

// Wait for all modules to ACK...
// (Entity remains invisible until fully initialized)

// Once all ACKs received → SetLifecycleState(Active)
```

**See:** [Entity Lifecycle Management](#entity-lifecycle-management) for full details.

---

#### 2. Distributed Ownership

**Purpose:** Allow multiple nodes to control different aspects of the same entity.

**Partial Ownership Example:**
```csharp
// Tank entity with split ownership
var ownership = new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1,  // Node 1 owns tank
    PartialOwners = new Dictionary<long, int>
    {
        { ENTITY_STATE_TYPE_ID, 1 },  // Node 1 publishes movement
        { WEAPON_STATE_TYPE_ID, 2 }   // Node 2 publishes weapon
    }
};

// Node 1: Publish EntityState (movement, heading)
if (ownership.OwnsDescriptor(ENTITY_STATE_TYPE_ID))
{
    _ddsWriter.WriteEntityState(tank.EntityState);
}

// Node 2: Publish WeaponState (aim, ammo)
if (ownership.OwnsDescriptor(WEAPON_STATE_TYPE_ID))
{
    _ddsWriter.WriteWeaponState(tank.WeaponState);
}

// Both nodes receive both descriptors → full tank state
```

**See:** [Distributed Ownership & Network Integration](#distributed-ownership--network-integration) for full details.

---

#### 3. Geographic Transform Services

**Purpose:** Convert between local Cartesian (simulation) and global Geodetic (network) coordinates.

**Typical Flow:**
```
Local Simulation:
  1. Physics updates Position {X=1000, Y=2000, Z=100} (meters, local)
  2. CoordinateTransformSystem →
     PositionGeodetic {Lat=37.xxx, Lon=-122.xxx, Alt=100} (WGS84)
  3. Publish PositionGeodetic to network

Remote Node:
  1. Receive PositionGeodetic from network
  2. NetworkSmoothingSystem →
     Position {X=..., Y=..., Z=...} (local to remote's origin)
  3. Rendering uses local Position
```

**Benefits:**
- Network messages independent of local coordinate frame
- Each node chooses its own origin
- No coordinate frame synchronization needed
- Global interoperability (GPS, mapping)

**See:** [Geographic Transform Services](#geographic-transform-services) for full details.

---

### Integration Workflow

#### Setting Up Network Integration

**1. Register Modules:**
```csharp
var kernel = new ModuleHostKernel(repository);

// Entity Lifecycle (coordinates construction/destruction)
var elm = new EntityLifecycleModule(new[] 
{ 
    PHYSICS_MODULE_ID,
    AI_MODULE_ID,
    NETWORK_MODULE_ID
});
kernel.RegisterModule(elm);

// Geographic Transform (local ↔ geodetic)
var geoTransform = new WGS84Transform(
    originLat: 37.7749,  // San Francisco
    originLon: -122.4194,
    originAlt: 0.0
);
var geoModule = new GeographicTransformModule(geoTransform);
kernel.RegisterModule(geoModule);

// Network Gateway (DDS pub/sub)
var networkModule = new NetworkGatewayModule(
    localNodeId: 1,
    ddsParticipant: participant
);
kernel.RegisterModule(networkModule);
```

---

**2. Configure Ownership:**
```csharp
// Define which node owns which descriptors
var ownershipConfig = new Dictionary<ulong, NetworkOwnership>
{
    [tank.NetworkId] = new NetworkOwnership
    {
        LocalNodeId = 1,
        PrimaryOwnerId = 1,
        PartialOwners = new()
        {
            { ENTITY_MASTER_TYPE_ID, 1 },   // Node 1 (primary)
            { ENTITY_STATE_TYPE_ID, 1 },     // Node 1 (movement)
            { WEAPON_STATE_TYPE_ID, 2 }      // Node 2 (weapon)
        }
    }
};

networkModule.ApplyOwnership(ownershipConfig);
```

---

**3. Register Descriptor-Component Mappings:**
```csharp
var ownershipMap = new DescriptorOwnershipMap();

// Map EntityState descriptor to Position + Velocity components
ownershipMap.RegisterMapping(
    descriptorTypeId: ENTITY_STATE_TYPE_ID,
    componentTypes: new[] { typeof(Position), typeof(Velocity) }
);

// Map WeaponState descriptor to WeaponState component
ownershipMap.RegisterMapping(
    descriptorTypeId: WEAPON_STATE_TYPE_ID,
    componentTypes: new[] { typeof(WeaponState) }
);

networkModule.SetOwnershipMap(ownershipMap);
```

---

**4. Run Simulation:**
```csharp
void Update(float deltaTime)
{
    // 1. Tick repository (increment frame/version)
    _repository.Tick();
    
    // 2. Execute all modules
    _kernel.Update(deltaTime);
    
    // Internally, modules execute in this order:
    // - Input Phase: NetworkGatewayModule (ingest DDS samples)
    // - Simulation Phase: Physics, AI
    // - PostSimulation Phase: GeographicTransformModule (local → geodetic)
    // - Export Phase: NetworkGatewayModule (publish owned descriptors)
}
```

---

### Complete Example: Distributed Tank Simulation

**Scenario:** Two nodes simulating a shared tank.
- **Node 1:** Controls movement (driver)
- **Node 2:** Controls weapon (gunner)

**Node 1 Setup:**
```csharp
// Create tank entity
var tank = _repository.CreateEntity();

// Add components
_repository.AddComponent(tank, new Position { X = 0, Y = 0, Z = 0 });
_repository.AddComponent(tank, new Velocity { X = 10, Y = 0, Z = 0 });
_repository.AddComponent(tank, new WeaponState { Aim = 0, Ammo = 100 });

// Configure ownership (Node 1 owns movement, Node 2 owns weapon)
var ownership = new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 1,
    PartialOwners = new()
    {
        { ENTITY_MASTER_TYPE_ID, 1 },
        { ENTITY_STATE_TYPE_ID, 1 },    // Node 1 publishes
        { WEAPON_STATE_TYPE_ID, 2 }     // Node 2 publishes
    }
};

_networkModule.SetOwnership(tank.NetworkId, ownership);

// Start simulation
```

**Node 1 Every Frame:**
```csharp
void Update(float deltaTime)
{
    _repository.Tick();
    
    // 1. Receive weapon updates from Node 2 (via NetworkGatewayModule)
    //    → WeaponState component updated
    
    // 2. Physics updates Position based on Velocity (local)
    
    // 3. CoordinateTransformSystem: Position → PositionGeodetic
    
    // 4. Publish EntityState (contains PositionGeodetic + Velocity) to network
    //    (NetworkGatewayModule checks ownership.OwnsDescriptor(ENTITY_STATE_TYPE_ID))
}
```

**Node 2 Setup:**
```csharp
// Receive tank entity from network
// (NetworkGatewayModule creates replica entity)

var tank = FindEntity(networkId: 42);

// Configure ownership (same as Node 1, but LocalNodeId = 2)
var ownership = new NetworkOwnership
{
    LocalNodeId = 2,
    PrimaryOwnerId = 1,
    PartialOwners = new()
    {
        { ENTITY_MASTER_TYPE_ID, 1 },
        { ENTITY_STATE_TYPE_ID, 1 },
        { WEAPON_STATE_TYPE_ID, 2 }     // Node 2 publishes
    }
};

_networkModule.SetOwnership(tank.NetworkId, ownership);
```

**Node 2 Every Frame:**
```csharp
void Update(float deltaTime)
{
    _repository.Tick();
    
    // 1. Receive EntityState from Node 1 (via NetworkGatewayModule)
    //    → Position + Velocity updated
    
    // 2. Player input updates WeaponState (aim, firing)
    
    // 3. Publish WeaponState to network
    //    (NetworkGatewayModule checks ownership.OwnsDescriptor(WEAPON_STATE_TYPE_ID))
}
```

**Result:** Both nodes see a fully functional tank, with movement controlled by Node 1 and weapon by Node 2.

---

### Network Data Flow

**Publishing (Egress):**

```
Local FDP Component → Translator → SST Descriptor → DDS → Network
       ↓                  ↓              ↓
   Position {X,Y,Z}    Transform    EntityState
   Velocity {Vx,Vy}    Bundle →     {Lat, Lon, Alt,
                                     Vx, Vy, Vz}
```

**Receiving (Ingress):**

```
Network → DDS → SST Descriptor → Translator → Local FDP Component
                      ↓              ↓              ↓
                  EntityState    Transform    Position {X,Y,Z}
                  {Lat, Lon,...} Unpack →     Velocity {Vx,Vy}
```

**Ownership Check (Egress):**
```csharp
foreach (var entity in owned Entities)
{
    if (ownership.OwnsDescriptor(ENTITY_STATE_TYPE_ID))
    {
        // Translate components → descriptor
        var descriptor = TranslateToEntityState(entity);
        
        // Publish to network
        _ddsWriter.Write(descriptor);
    }
}
```

**Ownership Application (Ingress):**
```csharp
foreach (var sample in _ddsReader.TakeSamples())
{
    var entity = FindOrCreateEntity(sample.NetworkId);
    
    // Translate descriptor → components
    TranslateToComponents(sample.Data, entity);
    
    // Sync ownership metadata to FDP components
    var ownerId = ownership.GetOwner(sample.DescriptorTypeId);
    _repository.SetComponentMetadata(entity, typeof(Position), new() { OwnerId = ownerId });
}
```

---

### Best Practices

#### ✅ DO: Use EntityLifecycleModule for Coordinated Creation

```csharp
// ✅ GOOD: Dark construction with lifecycle
var entity = _repository.CreateEntity();
_elm.SetLifecycleState(entity, EntityLifecycle.Constructing);

// Initialize components...

_bus.Publish(new ConstructionRequest { EntityId = entity.NetworkId, ... });

// Wait for ACKs, then:
_elm.SetLifecycleState(entity, EntityLifecycle.Active);
```

**Why:** Prevents race conditions where some nodes see partially-initialized entities.

---

#### ✅ DO: Assign Primary Owner for Every Entity

```csharp
// ✅ GOOD: Explicit primary owner
var ownership = new NetworkOwnership
{
    LocalNodeId = 1,
    PrimaryOwnerId = 2,  // Node 2 is primary
    PartialOwners = new() { ... }
};
```

**Why:** Primary owner handles entity deletion and ownership fallback.

---

#### ✅ DO: Use Geographic Transforms for Global Simulations

```csharp
// ✅ GOOD: Geodetic coordinates on network
var transform = new WGS84Transform(originLat, originLon, originAlt);
var geoModule = new GeographicTransformModule(transform);
kernel.RegisterModule(geoModule);

// Local: Position {X, Y, Z} (meters from origin)
// Network: PositionGeodetic {Lat, Lon, Alt} (WGS84)
```

**Why:** Nodes can have different local origins, enables GPS integration.

---

#### ⚠️ DON'T: Publish Descriptors You Don't Own

```csharp
// ❌ BAD: Publishing without ownership check
_ddsWriter.WriteWeaponState(tank.Weapon); // Violates SST protocol!

// ✅ GOOD: Check ownership first
if (_ownership.OwnsDescriptor(WEAPON_STATE_TYPE_ID))
{
    _ddsWriter.WriteWeaponState(tank.Weapon);
}
```

**Why:** Violates Single Source of Truth (SST), causes conflicts and undefined behavior.

---

#### ⚠️ DON'T: Assume Ownership is Static

```csharp
// ❌ BAD: Caching ownership decision
private bool _ownsWeapon = _ownership.OwnsDescriptor(WEAPON_STATE_TYPE_ID);

void Update()
{
    if (_ownsWeapon) // Stale!
    {
        PublishWeapon();
    }
}

// ✅ GOOD: Check ownership every frame
void Update()
{
    if (_ownership.OwnsDescriptor(WEAPON_STATE_TYPE_ID))
    {
        PublishWeapon();
    }
}
```

**Why:** Ownership can transfer at runtime (hand-off scenarios).

---

### Troubleshooting

#### Problem: Entity Appears Partially Initialized

**Symptoms:**
- Some components present, others missing
- Systems crash accessing non-existent components

**Cause:** Entity became Active before all modules ACKed construction.

**Solution:**
```csharp
// Ensure lifecycle coordination
_elm.RegisterModules(new[] { PHYSICS_ID, AI_ID, NET_ID });

// Publish construction request
_bus.Publish(new ConstructionRequest { ... });

// WAIT for ALL ACKs before activating
// (EntityLifecycleModule handles this automatically)
```

---

#### Problem: Ownership Conflict Detected

**Symptoms:**
```
Warning: Multiple nodes publishing WeaponState (Node 1, Node 2)
```

**Cause:** Ownership configuration mismatch between nodes.

**Solution:**
1. **Verify primary owner:**
   ```csharp
   Assert.Equal(expectedOwner, _ownership.PrimaryOwnerId);
   ```

2. **Check partial owners match:**
   ```csharp
   Assert.Equal(node2, _ownership.GetOwner(WEAPON_STATE_TYPE_ID));
   ```

3. **Use ownership transfer protocol** if intentional:
   ```csharp
   _networkModule.TransferOwnership(WEAPON_STATE_TYPE_ID, newOwner: 3);
   ```

---

#### Problem: Geographic Coordinates Incorrect

**Symptoms:**
- Entity appears at wrong location
- Large coordinate differences between nodes

**Cause:** Wrong origin or transform not applied.

**Solution:**
```csharp
// 1. Verify origin matches intended location
var transform = new WGS84Transform(
    originLat: 37.7749,  // San Francisco
    originLon: -122.4194,
    originAlt: 0.0
);

// 2. Verify module registered
kernel.RegisterModule(new GeographicTransformModule(transform));

// 3. Verify components synced
var localPos = entity.GetComponent<Position>();
var geoPos = entity.GetComponent<PositionGeodetic>();
Console.WriteLine($"Local: {localPos.X}, {localPos.Y}");
Console.WriteLine($"Geo: {geoPos.Latitude}, {geoPos.Longitude}");
```

---

### Performance Characteristics

| Operation | Cost | Notes |
|-----------|------|-------|
| **Ownership Check** | ~10ns | Dictionary lookup |
| **Descriptor Translation** | ~50-200ns | Depends on complexity |
| **DDS Publish** | ~1-5μs | Network I/O |
| **DDS Subscribe** | ~1-5μs | Network I/O |
| **Geographic Transform** | ~100ns | Matrix multiplication |
| **Lifecycle ACK Processing** | ~50ns | Per ACK |

**Throughput:**
- **100 entities** @ 60Hz → ~6,000 updates/sec
- **1,000 entities** @ 60Hz → ~60,000 updates/sec
- **10,000 entities** @ 10Hz → ~100,000 updates/sec

**Network Bandwidth (typical):**
- EntityState descriptor: ~100 bytes
- WeaponState descriptor: ~50 bytes
- 100 entities @ 60Hz: ~900 KB/s

---

## Time Control & Synchronization

### Overview

The Time Control system provides synchronized time management across distributed simulation peers. This is essential for networked simulations where multiple peers must maintain temporal coherence.

**Key Components:**
- **GlobalTime:** Unified time struct tracking simulation time, wall time, and frame count
- **Master Time Controller:** Authoritative timekeeper publishing periodic time pulses
- **Slave Time Controller:** Adaptive follower using PLL to converge with master

---

### GlobalTime Structure

The `GlobalTime` struct is the single source of truth for time in FDP simulations (`Fdp.Kernel/GlobalTime.cs`):

```csharp
public struct GlobalTime
{
    public long FrameNumber;              // Current frame (signed for delta calculations)
    public long StartWallTicks;           // Simulation start time (Stopwatch ticks)
    public long UnscaledDeltaTime;        // Frame delta in ticks
    public long UnscaledTotalTime;        // Total elapsed ticks since start
    public float DeltaTime;               // Frame delta in seconds (scaled)
    public float TotalTime;               // Total elapsed seconds (scaled)
    public float TimeScale;               // Time multiplier (1.0 = real-time)
    public bool IsPaused;                 // Simulation pause state
}
```

---

### Master Time Controller

The **Master** is the authoritative timekeeper, publishing `TimePulse` messages at 1Hz:

```csharp
using ModuleHost.Core.Time;

var master = new MasterTimeController(eventBus);
master.SetTimeScale(1.0f);

while (running)
{
    GlobalTime time = master.Update();
    repository.SetSingleton(time);
    systemScheduler.ExecuteAll(repository, time.DeltaTime);
}
```

---

### Slave Time Controller

The **Slave** follows the master using a Phase-Locked Loop (PLL):

```csharp
var slave = new SlaveTimeController(
    eventBus,
    new TimeConfig 
    { 
        NetworkLatencyMs = 50,      // Expected one-way latency
        PLLGain = 0.1,              // Convergence speed
        HardSnapThresholdMs = 500   // Force-sync threshold
    }
);

while (running)
{
    GlobalTime time = slave.Update();
    repository.SetSingleton(time);
    systemScheduler.ExecuteAll(repository, time.DeltaTime);
}
```

**PLL Algorithm:** Slave gradually speeds up/slows down to match master, applying proportional correction to delta time based on sync error.

---

## Transient Components & Snapshot Filtering

### Overview

**Transient Components** are excluded from all snapshot operations (GDB, DoubleBuffer, Flight Recorder). This ensures:
- **Thread Safety:** Mutable managed components can't cause race conditions
- **Performance:** Heavy caches don't bloat snapshots
- **Memory:** Reduced snapshot size

---

### Component Classification

| Component Type | Snapshotable | Rationale |
|----------------|--------------|-----------|
| **Struct** (unmanaged) | ✅ Yes | Value copy, thread-safe |
| **Record** (class) | ✅ Yes | Immutable, compiler-enforced |
| **Class** + `[TransientComponent]` | ❌ No | Mutable, main-thread only |
| **Class** (no attribute) | ❌ **ERROR** | Must declare intent! |

---

### Marking Components

**Option 1: Attribute** (for mutable classes):
```csharp
[TransientComponent]
public class UIRenderCache
{
    public Dictionary<int, Texture> TextureCache = new();
}
```

**Option 2: Record** (for immutable data):
```csharp
// Auto-snapshotable (no attribute needed)
public record PlayerStats(int Health, int Score, string Name);
```

---

### Registration

```csharp
// Struct - snapshotable by default
repository.RegisterComponent<Position>();

// Record - auto-detected as immutable ✅
repository.RegisterComponent<PlayerStats>();

// Class with attribute - auto-detected as transient ❌
repository.RegisterComponent<UIRenderCache>();

// Class without attribute - ERROR!
repository.RegisterComponent<GameState>();
// Throws: "Must mark with [TransientComponent] or convert to record"
```

---

### Snapshot Filtering

**Default:** Excludes transient components automatically
```csharp
snapshot.SyncFrom(liveWorld);
// Result:
// ✅ Position, Velocity (snapshotable) → Copied
// ❌ UIRenderCache (transient) → NOT copied
```

**Debug Override:** Force include transient
```csharp
debugSnapshot.SyncFrom(liveWorld, includeTransient: true);
// Result:
// ✅ Position, Velocity → Copied
// ✅ UIRenderCache (transient) → Copied for debugging
```

**Optimization Override:** Exclude specific snapshotable types
```csharp
// Network sync - exclude large data
networkSnapshot.SyncFrom(liveWorld, excludeTypes: new[] 
{ 
    typeof(NavigationMesh),    // Too large for network
    typeof(TerrainHeightMap)   // Static, doesn't change
});
// Result:
// ✅ Position, Velocity → Copied
// ❌ NavigationMesh, TerrainHeightMap → Excluded (optimization)
// ❌ UIRenderCache → Excluded (transient)
```

**Explicit Mask Override:** Full manual control
```csharp
// Build custom mask (only Position and Velocity)
var customMask = new BitMask256();
customMask.SetBit(ComponentType<Position>.ID);
customMask.SetBit(ComponentType<Velocity>.ID);

snapshot.SyncFrom(liveWorld, mask: customMask);
// Result:
// ✅ Position, Velocity → Copied
// ❌ Everything else → Excluded (explicit control)
// Note: Explicit mask STILL filters out transient by default!

// To include transient WITH explicit mask:
snapshot.SyncFrom(liveWorld, mask: customMask, includeTransient: true);
```

**Priority Rules:**
1. **Explicit mask** (if provided) → Intersects with snapshotable mask
2. **includeTransient** (if true) → Overrides default transient exclusion
3. **excludeTypes** (if provided) → Removes specific types from mask
4. **Default** (no parameters) → Auto-builds snapshotable-only mask

---

### Flight Recorder Integration

Flight Recorder automatically excludes transient components:

```csharp
recorder.CaptureKeyframe();  // Excludes transient
recorder.CaptureKeyframe(includeTransient: true);  // Debug mode
```

---

### Best Practices

#### ⭐ **#1 Rule: Use Structs for Plain Old Data (POD)**

**Performance First: Structs for Game Data**

```csharp
// ✅ RECOMMENDED: Unmanaged struct for POD (best performance)
public struct Position
{
    public float X;
    public float Y;
    public float Z;
}

public struct Velocity
{
    public float X;
    public float Y;
    public float Z;
}

public struct Health
{
    public int Current;
    public int Maximum;
}

// Benefits:
// 1. Zero GC overhead (stack/inline allocated)
// 2. Cache-friendly (contiguous memory)
// 3. Always snapshotable (value copy)
// 4. Best performance for ECS
```

**When You Need Managed Data:**

```csharp
// ✅ RECOMMENDED: Record for immutable managed data
public record PlayerInfo(
    string Name,           // ← String requires managed component
    int Level,
    int ExperiencePoints
);

// ✅ REQUIRED: Class + attribute for mutable managed data
[TransientComponent]
public class UIRenderCache
{
    public Dictionary<int, Texture> TextureCache = new();
    public List<Mesh> MeshCache = new();
}

// Benefits of Records:
// 1. Compiler-enforced immutability (init-only)
// 2. Auto-snapshotable (no attribute needed)
// 3. Thread-safe (immutable = safe shallow copy)
// 4. Clean syntax
```

**❌ Common Mistake: Using Class for POD:**

```csharp
// ❌ WRONG: Class for simple data (slow, GC overhead)
public class Position
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

// ❌ WRONG: Record for simple data (still heap-allocated)
public record Position(float X, float Y, float Z);

// ✅ CORRECT: Struct for simple data (fast, cache-friendly)
public struct Position
{
    public float X, Y, Z;
}
```

---

#### ✅ **DO:**

- **Use `struct` for all POD components** (positions, velocities, stats)
  - Example: `struct Position { float X, Y, Z; }`
  - Example: `struct Velocity { float X, Y, Z; }`
  - Example: `struct Health { int Current, Maximum; }`
  - **Why:** Best performance, zero GC, cache-friendly

- **Use `record` for immutable managed data** (names, collections)
  - Example: `record PlayerInfo(string Name, int Level)` ← String requires managed
  - Example: `record TeamData(string TeamName, IReadOnlyList<int> MemberIDs)`
  - **Why:** Need managed references (string, collections) but want immutability

- **Use `class` + `[TransientComponent]` for mutable managed state**
  - Example: `[TransientComponent] class UICache { Dictionary<> ... }`
  - Example: `[TransientComponent] class AIWorkspace { List<> ... }`
  - **Why:** Need mutability, main-thread only

- **Keep transient components on main thread only**
  - Never access from background modules
  - Use `IModule.GetRequiredComponents()` to exclude them

- **Use `includeTransient: true` only for debugging**
  - Inspector views
  - Debug snapshots

---

#### ❌ **DON'T:**

- **Don't use `class` or `record` for POD** (use `struct` instead!)
  - ❌ `class Position` → ✅ `struct Position`
  - ❌ `record Velocity(...)` → ✅ `struct Velocity`
  - **Why:** Heap allocation, GC overhead, cache misses

- **Don't use `class` for immutable managed data** (use `record`)
  - ❌ `class PlayerInfo` → ✅ `record PlayerInfo(...)`
  - **Why:** Records enforce immutability at compile-time

- **Don't forget `[TransientComponent]` on mutable managed classes**
  - System will throw helpful error if you forget
  - But prefer immutable `record` when possible

- **Don't access transient components from background modules**
  - Will cause race conditions

- **Don't use `snapshotable: true` override on mutable classes**
  - ⚠️ **Dangerous!** Thread-safety violations

---

#### 🎯 **Component Type Decision Chart:**

| Data Type | Solution | Example | Snapshotable |
|-----------|----------|---------|--------------|
| **Simple POD** (no references) | `struct` | `struct Position { float X, Y, Z; }` | ✅ Always |
| **Immutable + Managed refs** | `record` | `record PlayerInfo(string Name)` | ✅ Auto |
| **Mutable + Managed refs** | `class` + `[TransientComponent]` | `class UICache { Dictionary<> }` | ❌ Transient |

**Decision Flow:**
1. **Does it contain strings/collections?**
   - **No** → Use `struct` (POD, best performance)
   - **Yes** → Continue to step 2

2. **Is it mutable (needs to change after creation)?**
   - **No** → Use `record` (immutable managed data)
   - **Yes** → Use `class` + `[TransientComponent]` (mutable, transient)

**Rule of Thumb:** 
- **90% of components = `struct`** (Position, Velocity, Health, etc.)
- **5% of components = `record`** (PlayerName, TeamInfo with strings)
- **5% of components = `class` + `[TransientComponent]`** (UI caches, AI workspace)

---

### ⚠️ BitMask256 Alignment (INSERT IN "Entity Component System")

`BitMask256` requires **32-byte alignment** for AVX2 SIMD operations. This is handled automatically in `EntityHeader`.

**Warning:** If you embed `BitMask256` in custom structs:

```csharp
// ❌ DANGER: May break alignment
public struct MyCustomData
{
    public int SomeField;
    public BitMask256 MyMask;  // ← Alignment lost!
}

// ✅ SAFE: Use EntityHeader as-is
var header = world.GetEntityHeader(entity);
var mask = header.ComponentMask;
```

**Symptoms of misalignment:**
- Crashes in `BitMask256.Or()` or `And()` with AVX2 intrinsics
- Only occurs on CPUs with AVX2 support
- Works on older CPUs (slower fallback path)

**Recommendation:** Don't embed `BitMask256` outside of `EntityHeader`.

---


### Cross-References

**Related Sections:**
- [Entity Lifecycle Management](#entity-lifecycle-management) - Dark construction, coordinated teardown
- [Distributed Ownership & Network Integration](#distributed-ownership--network-integration) - Partial ownership, ownership transfer
- [Geographic Transform Services](#geographic-transform-services) - Coordinate transforms
- [Modules & ModuleHost](#modules--modulehost) - NetworkGatewayModule, GeographicTransformModule
- [Event Bus](#event-bus) - ConstructionRequest, ConstructionAck events

**API Reference:**
- See [API Reference - Network Integration](API-REFERENCE.md#network-integration)

**Example Code:**
- `ModuleHost.Core/Network/NetworkGatewayModule.cs` - Network integration module
- `ModuleHost.Core/Geographic/GeographicTransformModule.cs` - Coordinate transform module
- `ModuleHost.Core/Lifecycle/EntityLifecycleModule.cs` - Lifecycle coordination
- `ModuleHost.Core.Tests/NetworkIntegrationTests.cs` - Integration tests

**Related Batches:**
- BATCH-06 - Entity Lifecycle Management
- BATCH-07 - Network Integration (Distributed Ownership)
- BATCH-07.1 - Partial Descriptor Ownership
- BATCH-08 - Geographic Transform Services

---

---

**Version 2.0 - January 2026 (Updated: 2026-01-09)**  
**For questions or clarifications, see: [Design Implementation Plan](DESIGN-IMPLEMENTATION-PLAN.md)**

