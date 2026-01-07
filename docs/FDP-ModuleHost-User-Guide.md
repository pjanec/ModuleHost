# FDP Kernel & ModuleHost User Guide

**Version:** 1.0  
**Date:** 2026-01-07  
**Audience:** Developers building high-performance simulations  

---

## Table of Contents

1. [Core Principles](#core-principles)
2. [Entity Component System (ECS)](#entity-component-system-ecs)
3. [Systems & Scheduling](#systems--scheduling)
4. [Modules & ModuleHost](#modules--modulehost)
5. [Event Bus](#event-bus)
6. [Simulation Views](#simulation-views)
7. [Best Practices](#best-practices)
8. [Common Patterns](#common-patterns)
9. [Anti-Patterns to Avoid](#anti-patterns-to-avoid)

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

**Component Types:**

1. **Unmanaged Components** (preferred for performance)
   - Must be `unmanaged` types (no managed references)
   - Stored in NativeArrays (off-heap, cache-friendly)
   - Can be safely accessed in parallel

   ```csharp
   public struct Position
   {
       public float X;
       public float Y;
       public float Z;
   }
   ```

2. **Managed Components** (use sparingly)
   - Can contain managed references (strings, objects)
   - Stored in managed arrays (GC pressure)
   - Slower access

   ```csharp
   public class AIBehaviorTree
   {
       public BehaviorNode RootNode; // Managed reference
   }
   ```

3. **Singleton Components** (global state)
   - Only one instance per world
   - Used for cross-system communication
   - Accessed via `GetSingleton<T>()`

   ```csharp
   public struct SpatialGridData
   {
       public SpatialHashGrid Grid;
   }
   ```

### Component Registration

**Before use, register all components:**

```csharp
var repository = new EntityRepository();

// Register unmanaged components
repository.RegisterComponent<Position>();
repository.RegisterComponent<Velocity>();
repository.RegisterComponent<Health>();

// Register managed components
repository.RegisterManagedComponent<AIBehaviorTree>();

// Register singletons
repository.RegisterComponent<SpatialGridData>();
```

### Entity Lifecycle

**Creating Entities:**

```csharp
// Create entity
var entity = repository.CreateEntity();

// Add components
repository.AddComponent(entity, new Position { X = 0, Y = 0, Z = 0 });
repository.AddComponent(entity, new Velocity { X = 1, Y = 0, Z = 0 });
```

**Accessing Components:**

```csharp
// Read component
var pos = repository.GetComponent<Position>(entity);

// Modify and write back
pos.X += 10;
repository.SetComponent(entity, pos); // Use SetComponent for updates

// Check if component exists
bool hasVelocity = repository.HasComponent<Velocity>(entity);

// Remove component
repository.RemoveComponent<Health>(entity);
```

**Destroying Entities:**

```csharp
repository.DestroyEntity(entity);
// Entity is marked for deletion, will be cleaned up at end of frame
```

**Important API Note:**
- `AddComponent<T>()` - Upsert behavior (add or update)
- `SetComponent<T>()` - Alias for AddComponent (semantic clarity for updates)
- Use `SetComponent` when you know the component exists (update scenario)
- Use `AddComponent` when you may be creating it for the first time

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
        
        // Query entities with Position and Velocity
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

### System Lifecycle

```csharp
public class MySystem : ComponentSystem
{
    // Called once when system is registered
    protected override void OnCreate()
    {
        // Initialize resources
        _myBuffer = new NativeArray<float>(1000, Allocator.Persistent);

        // initialize queries; Caching queries in OnCreate is a major perf win vs building them in OnUpdate
        _query = World.Query().With<Pos>().Build();
    }
    
    // Called every frame (or based on schedule)
    protected override void OnUpdate()
    {
        // System logic here
    }
    
    // Called when system is destroyed
    protected override void OnDestroy()
    {
        // Cleanup resources
        _myBuffer.Dispose();
    }
}
```

### Querying Entities

**Basic Query:**

```csharp
var query = World.Query()
    .With<Position>()    // Entities must have Position
    .With<Velocity>()    // AND Velocity
    .Build();

foreach (var entity in query)
{
    // Process entity
}
```

**Query with Exclusions:**

```csharp
var query = World.Query()
    .With<Position>()
    .Without<Dead>()     // Exclude dead entities
    .Build();
```

**Parallel Iteration (Zero GC):**

```csharp
// FDP's optimized parallel iteration (uses pooled batches)
query.ForEachParallel((entity, index) =>
{
    var pos = World.GetComponent<Position>(entity);
    var vel = World.GetComponent<Velocity>(entity);
    
    // ... logic (thread-safe if each entity modifies only itself)
    
    World.SetComponent(entity, pos);
});
```

**⚠️ ANTI-PATTERN: Manual Collection**
```csharp
// ❌ DON'T DO THIS (allocates every frame):
var list = new List<Entity>();
foreach (var e in query) list.Add(e);
Parallel.ForEach(list, entity => { ... });

// ✅ DO THIS instead:
query.ForEachParallel((entity, index) => { ... });
```

### System Scheduling with Attributes

**FDP Kernel uses declarative attributes for system ordering:**

```csharp
using Fdp.Kernel;

// Update in the SimulationSystemGroup
[UpdateInGroup(typeof(SimulationSystemGroup))]
public class PhysicsSystem : ComponentSystem
{
    // ...
}

// Run before another system
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(RenderSystem))]
public class CameraSystem : ComponentSystem
{
    // ...
}

// Run after another system
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InputSystem))]
public class PlayerControllerSystem : ComponentSystem
{
    // ...
}

// Multiple constraints
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
[UpdateBefore(typeof(CollisionSystem))]
public class MovementSystem : ComponentSystem
{
    // ...
}
```

**System Groups (Execution Phases):**

```
InitializationSystemGroup  (early setup)
    ↓
SimulationSystemGroup      (main logic)
    ↓
PresentationSystemGroup    (rendering, output)
```

**Example - Complete Scheduling:**

```csharp
// Early setup
[UpdateInGroup(typeof(InitializationSystemGroup))]
public class SpawnSystem : ComponentSystem { }

// Main logic phase
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(PhysicsSystem))]
public class InputSystem : ComponentSystem { }

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(InputSystem))]
public class PhysicsSystem : ComponentSystem { }

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PhysicsSystem))]
public class AnimationSystem : ComponentSystem { }

// Rendering phase
[UpdateInGroup(typeof(PresentationSystemGroup))]
public class RenderSystem : ComponentSystem { }
```

---

## Modules & ModuleHost

### What is a Module?

A **Module** is a collection of related systems that operate on a **snapshot** of the simulation state.

**Key Differences: System vs Module:**

| Aspect | ComponentSystem | Module |
|--------|----------------|--------|
| Execution | Main thread | Can be separate thread |
| State Access | Direct (EntityRepository) | Snapshot (ISimulationView) |
| Update Frequency | Every frame | Configurable (e.g., 10Hz) |
| Use Case | Tight loops, physics | AI, pathfinding, analytics |

### Module Interface

```csharp
using ModuleHost.Core;

public interface IModule
{
    string Name { get; }
    ModuleTier Tier { get; }
    
    void Initialize(ISimulationView view);
    void Tick(float deltaTime);
    void Shutdown();
}
```

**ModuleTier:**
- `ModuleTier.Gameplay` - Game logic, runs frequently
- `ModuleTier.AI` - Decision making, can run slower
- `ModuleTier.Analytics` - Logging, debugging, very slow

### Example Module

```csharp
public class AIModule : IModule
{
    public string Name => "AI Decision Making";
    public ModuleTier Tier => ModuleTier.AI;
    
    private ISimulationView _view;
    
    public void Initialize(ISimulationView view)
    {
        _view = view;
    }
    
    public void Tick(float deltaTime)
    {
        // Query snapshot
        var query = _view.Query<Position, AIState>();
        
        foreach (var entity in query)
        {
            var pos = _view.GetComponent<Position>(entity);
            var ai = _view.GetComponent<AIState>(entity);
            
            // Make decisions...
            
            // Publish commands via events
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new MoveToCommand 
            {
                EntityId = entity.Id,
                Target = CalculateTarget(pos, ai)
            });
        }
    }
    
    public void Shutdown()
    {
        // Cleanup
    }
}
```

### Module Scheduling Attributes

```csharp
using ModuleHost.Core;

// Run in specific phase with update frequency
[UpdateInPhase(Phase.Gameplay)]
[UpdateFrequency(10)] // 10 Hz (every 0.1s)
public class PathfindingModule : IModule
{
    // ...
}

[UpdateInPhase(Phase.AI)]
[UpdateFrequency(5)] // 5 Hz (every 0.2s)
public class DecisionModule : IModule
{
    // ...
}

[UpdateInPhase(Phase.Analytics)]
[UpdateFrequency(1)] // 1 Hz (every 1s)
public class MetricsModule : IModule
{
    // ...
}
```

### ModuleHost Integration

```csharp
// Setup
var repository = new EntityRepository();
var recorder = new FlightRecorder(repository);
var moduleHost = new ModuleHostKernel(repository, recorder);

// Register modules
moduleHost.RegisterModule(new AIModule(), ModuleTier.AI);
moduleHost.RegisterModule(new PathfindingModule(), ModuleTier.Gameplay);

// Main loop
while (running)
{
    float deltaTime = GetDeltaTime();
    
    // Tick modules (manages snapshots, frequencies, etc.)
    moduleHost.Tick(deltaTime);
    
    // Render, etc.
}
```

---

## Event Bus

### Events for Communication

**Events** are the primary way to send commands or notifications in FDP.

**Event Definition:**

```csharp
using Fdp.Kernel;

[Event(EventId = 1001)]
public struct MoveToCommand
{
    public int EntityId;
    public Vector2 Target;
    public float Speed;
}

[Event(EventId = 1002)]
public struct DamageEvent
{
    public int VictimId;
    public int AttackerId;
    public float Amount;
}
```

**Publishing Events:**

```csharp
// In a system or module
var cmd = World.GetCommandBuffer();

cmd.PublishEvent(new MoveToCommand
{
    EntityId = entity.Id,
    Target = new Vector2(100, 100),
    Speed = 10f
});
```

**Consuming Events:**

```csharp
public class CommandProcessorSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Consume all MoveToCommand events this frame
        var events = World.View.ConsumeEvents<MoveToCommand>();
        
        foreach (var cmd in events)
        {
            var entity = new Entity(cmd.EntityId, 0);
            
            if (!World.IsAlive(entity))
                continue;
            
            // Process command
            var nav = World.GetComponent<NavState>(entity);
            nav.Target = cmd.Target;
            nav.TargetSpeed = cmd.Speed;
            World.SetComponent(entity, nav);
        }
    }
}
```

**Event Flow:**

```
Producer (System/Module)
    ↓ PublishEvent
Event Bus
    ↓ ConsumeEvents
Consumer (System)
    ↓ Process
Entity Components Updated
```

**Best Practices:**
- Use events for commands (intent to change state)
- Use events for notifications (something happened)
- Events are consumed once (single reader pattern)
- Events are frame-local (cleared each frame)

---

## Simulation Views

### What is an ISimulationView?

An **ISimulationView** provides **read-only snapshot access** to the simulation state.

**Purpose:**
- Modules operate on snapshots (not live data)
- Prevents race conditions
- Allows parallel module execution

**View Types:**

1. **Live View** (`EntityRepository.GetView()`)
   - Direct access to current frame
   - Used by ComponentSystems
   - Not thread-safe across systems

2. **Snapshot View** (ModuleHost provides this)
   - Copy of state at specific frame
   - Used by Modules
   - Thread-safe (read-only copy)

### Using ISimulationView

```csharp
public void ProcessAI(ISimulationView view)
{
    // Query entities
    var query = view.Query<Position, AIState>();
    
    foreach (var entity in query)
    {
        // Read components (SNAPSHOT - may be stale)
        var pos = view.GetComponent<Position>(entity);
        var ai = view.GetComponent<AIState>(entity);
        
        // Check component existence
        if (!view.HasComponent<Health>(entity))
            continue;
        
        // Make decisions based on snapshot...
        
        // Publish commands (deferred application)
        var cmd = view.GetCommandBuffer();
        cmd.PublishEvent(new AttackCommand { ... });
    }
}
```

**Key Methods:**

```csharp
public interface ISimulationView
{
    // Queries
    EntityQuery Query<T1>();
    EntityQuery Query<T1, T2>();
    // ... etc
    
    // Component access
    T GetComponent<T>(Entity entity);
    bool HasComponent<T>(Entity entity);
    
    // Command buffer (for deferred writes)
    ICommandBuffer GetCommandBuffer();
    
    // Event consumption
    IEnumerable<T> ConsumeEvents<T>();
}
```

**⚠️ Important:**
- `ISimulationView` is **read-only**
- Cannot `SetComponent` directly
- Use `CommandBuffer` to queue changes
- Changes apply at end of frame

---

## Best Practices

### System Organization

**Recommended Project Structure:**

```
MyGame/
├── Components/
│   ├── Vehicle/
│   │   ├── VehicleState.cs
│   │   ├── VehicleParams.cs
│   │   └── NavState.cs
│   ├── Combat/
│   │   ├── Health.cs
│   │   ├── Damage.cs
│   │   └── Armor.cs
│   └── Core/
│       ├── Position.cs
│       └── Rotation.cs
├── Systems/
│   ├── Physics/
│   │   ├── MovementSystem.cs
│   │   ├── CollisionSystem.cs
│   │   └── SpatialHashSystem.cs
│   ├── Combat/
│   │   ├── DamageSystem.cs
│   │   └── HealthRegenSystem.cs
│   └── Rendering/
│       └── DebugDrawSystem.cs
├── Modules/
│   ├── AIModule.cs
│   ├── PathfindingModule.cs
│   └── AnalyticsModule.cs
└── Events/
    ├── Commands/
    │   ├── MoveToCommand.cs
    │   └── AttackCommand.cs
    └── Notifications/
        ├── EntityDiedEvent.cs
        └── CollisionEvent.cs
```

### Performance Guidelines

**1. Minimize Allocations:**

```csharp
// ❌ BAD: Allocates every frame
void OnUpdate()
{
    var list = new List<Entity>(); // GC allocation!
    foreach (var e in query) list.Add(e);
}

// ✅ GOOD: Zero allocations
void OnUpdate()
{
    query.ForEachParallel((entity, index) => { ... });
}
```

**2. Use Span for Temporary Arrays:**

```csharp
// ❌ BAD: Heap allocation
var neighbors = new (int, Vector2)[32];

// ✅ GOOD: Stack allocation
Span<(int, Vector2)> neighbors = stackalloc (int, Vector2)[32];
```

**3. Batch Component Access:**

```csharp
// ❌ BAD: Repeated lookups
for (int i = 0; i < 1000; i++)
{
    var entity = entities[i];
    var pos = World.GetComponent<Position>(entity); // Lookup
    var vel = World.GetComponent<Velocity>(entity); // Lookup
    // ...
}

// ✅ GOOD: Use queries or table access
var query = World.Query<Position, Velocity>().Build();
query.ForEachParallel((entity, index) =>
{
    var pos = World.GetComponent<Position>(entity);
    var vel = World.GetComponent<Velocity>(entity);
    // ...
});
```

**4. Prefer Unmanaged Components:**

```csharp
// ❌ SLOWER: Managed component
public class AIData
{
    public string BehaviorTreePath; // Managed string
}

// ✅ FASTER: Unmanaged component
public struct AIData
{
    public int BehaviorTreeId; // Index into tree array
}
```

### Thread Safety

**Safe Patterns:**

```csharp
// ✅ SAFE: Each entity modifies only itself
query.ForEachParallel((entity, index) =>
{
    var pos = World.GetComponent<Position>(entity);
    pos.X += deltaTime;
    World.SetComponent(entity, pos); // Safe - only modifying this entity
});
```

**Unsafe Patterns:**

```csharp
// ❌ UNSAFE: Race condition!
int globalCounter = 0; // Shared state

query.ForEachParallel((entity, index) =>
{
    globalCounter++; // Multiple threads modifying!
});
```

**Solution for Shared State:**

```csharp
// Use Interlocked operations
private int _counter;

query.ForEachParallel((entity, index) =>
{
    Interlocked.Increment(ref _counter); // Thread-safe
});
```

---

## Common Patterns

### Pattern 1: Data Producer/Consumer (Singleton)

**Use Case:** System A produces data, System B consumes it.

```csharp
// Producer
[UpdateInGroup(typeof(SimulationSystemGroup))]
public class SpatialHashSystem : ComponentSystem
{
    private SpatialHashGrid _grid;
    
    protected override void OnUpdate()
    {
        _grid.Clear();
        // ... build grid ...
        
        World.SetSingleton(new SpatialGridData { Grid = _grid });
    }
}

// Consumer
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public class CollisionSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var gridData = World.GetSingleton<SpatialGridData>();
        var grid = gridData.Grid;
        
        // Use grid...
    }
}
```

### Pattern 2: Command Processing

**Use Case:** Module requests action, system processes it.

```csharp
// Module publishes command
public class AIModule : IModule
{
    public void Tick(float deltaTime)
    {
        var cmd = _view.GetCommandBuffer();
        cmd.PublishEvent(new MoveToCommand { EntityId = 42, Target = dest });
    }
}

// System processes command
[UpdateInGroup(typeof(SimulationSystemGroup))]
public class CommandProcessorSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var events = World.View.ConsumeEvents<MoveToCommand>();
        
        foreach (var cmd in events)
        {
            // Apply command to entity
        }
    }
}
```

### Pattern 3: Reactive System

**Use Case:** System reacts to events.

```csharp
[Event(EventId = 2001)]
public struct EntityDiedEvent
{
    public int EntityId;
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
public class LootDropSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var deaths = World.View.ConsumeEvents<EntityDiedEvent>();
        
        foreach (var death in deaths)
        {
            // Spawn loot at death location
            var entity = new Entity(death.EntityId, 0);
            if (!World.IsAlive(entity)) continue;
            
            var pos = World.GetComponent<Position>(entity);
            SpawnLoot(pos);
        }
    }
    
    void SpawnLoot(Position pos) { /* ... */ }
}
```

### Pattern 4: Multi-Phase Processing

**Use Case:** System needs multiple passes.

```csharp
// Phase 1: Gather
[UpdateInGroup(typeof(SimulationSystemGroup))]
public class CollisionDetectionSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Find collisions, publish events
        var cmd = World.GetCommandBuffer();
        cmd.PublishEvent(new CollisionEvent { ... });
    }
}

// Phase 2: Respond
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(CollisionDetectionSystem))]
public class CollisionResponseSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Consume collision events, apply forces
        var collisions = World.View.ConsumeEvents<CollisionEvent>();
        foreach (var collision in collisions)
        {
            ApplyImpulse(collision);
        }
    }
}
```

---

## Anti-Patterns to Avoid

### ❌ Anti-Pattern 1: System-to-System References

```csharp
// ❌ WRONG
public class MySystem : ComponentSystem
{
    private OtherSystem _otherSystem; // DON'T!
    
    protected override void OnCreate()
    {
        _otherSystem = World.GetSystem<OtherSystem>(); // NO!
    }
}

// ✅ CORRECT - Use Singletons
public class MySystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var data = World.GetSingleton<SharedData>();
    }
}
```

### ❌ Anti-Pattern 2: Storing Entity IDs Without Generation

```csharp
// ❌ WRONG
public struct FormationRoster
{
    public fixed int MemberIds[16]; // Stale reference risk!
}

// ✅ CORRECT - Store full Entity
public struct FormationRoster
{
    public fixed long MemberEntities[16]; // Entity (ID + Gen)
}
```

### ❌ Anti-Pattern 3: Logic in Components

```csharp
// ❌ WRONG
public struct Vehicle
{
    public Vector2 Position;
    public Vector2 Velocity;
    
    public void Move(float dt) // Logic in component!
    {
        Position += Velocity * dt;
    }
}

// ✅ CORRECT - Logic in Systems
public class MovementSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        var query = World.Query<Vehicle>().Build();
        foreach (var entity in query)
        {
            var vehicle = World.GetComponent<Vehicle>(entity);
            vehicle.Position += vehicle.Velocity * DeltaTime;
            World.SetComponent(entity, vehicle);
        }
    }
}
```

### ❌ Anti-Pattern 4: Allocating in Hot Paths

```csharp
// ❌ WRONG
protected override void OnUpdate()
{
    var temp = new List<Entity>(); // Allocates!
    var array = query.ToArray();   // Allocates!
}

// ✅ CORRECT
protected override void OnUpdate()
{
    query.ForEachParallel((entity, index) => { ... }); // Zero GC
}
```

### ❌ Anti-Pattern 5: Overusing Managed Components

```csharp
// ❌ WRONG - Managed unnecessarily
public class Transform
{
    public Vector3 Position; // Could be struct!
}

// ✅ CORRECT - Unmanaged for performance
public struct Transform
{
    public Vector3 Position;
}
```

---

### 1. The "Ghost Entity" Pattern (Network Interpolation)
*   **Context:** In networking or smoothing, you often have a "Target State" (from network) and a "Present State" (visual).
*   **Best Practice:** Do not snap position directly. Use a separate component for the target.
*   **Example:**
    ```csharp
    public struct NetworkTarget { public Vector3 Pos; }
    public struct Position { public Vector3 Value; } // Rendered
    
    // System:
    pos.Value = Vector3.Lerp(pos.Value, target.Pos, dt * 10);
    ```

### 2. Input Handling Pattern (Polled vs Event)
*   **Context:** How to get keyboard/mouse input into the ECS?
*   **Best Practice:**
    *   *Option A (Singleton):* `World.SetSingleton(new InputState { IsJumpPressed = true })`. Good for continuous state.
    *   *Option B (Events):* `cmd.PublishEvent(new JumpCommand())`. Good for one-shot actions.
    *   **Anti-Pattern:** Reading `Input.GetKeyDown` inside a deep simulation system (breaks determinism/replay).

### 3. Tag Components (Flags)
*   **Context:** Boolean flags on components waste memory and bandwidth if they are sparse.
*   **Best Practice:** Use "Tag Components" (empty structs) to mark state.
*   **Example:**
    ```csharp
    public struct IsBurning : IComponentData {} // Size: 1 byte (or 0 logic size)
    
    // Query:
    var burningQuery = World.Query().With<Position>().With<IsBurning>().Build();
    ```
    *   *Why:* Faster iteration (smaller archetype), cleaner logic (`HasComponent<IsBurning>`).

### 4. Prefabs / Blueprints (TKB)
*   **Context:** Creating complex entities with 10 components manually is error-prone.
*   **Best Practice:** Use the TKB (Transient Knowledge Base) or a Factory pattern.
*   **Example:**
    ```csharp
    // Instead of manual AddComponent calls:
    TkbDatabase.Spawn("Tank_T72", World, position);
    ```

### 5. "Read-Write-Write" Dependency Hazards
*   **Context:** Parallel execution rules.
*   **Best Practice:** If you read from Component A and write to Component B, ensure no other system reads B in parallel.
*   **Advice:** "Read-Only is free. Write requires exclusivity." (Though FDP handles this via `ForEachParallel` entity isolation, the architectural mindset helps).

### 6. Debugging Tips
*   **Context:** ECS debugging is hard because data is scattered.
*   **Best Practice:**
    *   Use the **Flight Recorder** to catch glitches in act.
    *   Name your Entities (debug build only) or use `ManagedComponent<DebugName>` if needed.
    *   Use `[SystemAttributes]` to force serial execution (`MaxDegreeOfParallelism = 1`) when debugging race conditions.



## Quick Reference

### System Lifecycle

```
OnCreate()     → Called once when registered
   ↓
OnUpdate()     → Called every frame (or per schedule)
   ↓
OnDestroy()    → Called when system is removed
```

### Module Lifecycle

```
Initialize(view)  → Called once
   ↓
Tick(deltaTime)   → Called per frequency (e.g., 10Hz)
   ↓
Shutdown()        → Called on cleanup
```

### Common Attributes

```csharp
// FDP Kernel (ComponentSystem)
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(OtherSystem))]
[UpdateAfter(typeof(AnotherSystem))]

// ModuleHost (IModule)
[UpdateInPhase(Phase.Gameplay)]
[UpdateFrequency(10)] // Hz
```

### Performance Checklist

- [ ] Use `ForEachParallel` instead of manual iteration
- [ ] Use `Span<T>` for temporary arrays
- [ ] Prefer unmanaged components
- [ ] Batch component access
- [ ] Zero allocations in `OnUpdate`
- [ ] Store full `Entity` handles, not just IDs
- [ ] Systems communicate via data, not references

---

## Further Reading

- **FDP Architecture:** `docs/reference-archive/FDP-features-and-architecture.pdf`
- **ModuleHost Overview:** `docs/reference-archive/ModuleHost-Overview.pdf`
- **Car Kinematics Design:** `docs/car-kinem-implementation-design.md`
- **Design Addendum:** `docs/car-kinem-design-addendum.md`

---

**Document Version:** 1.0  
**Last Updated:** 2026-01-07  
**Maintainer:** Antigravity Team
