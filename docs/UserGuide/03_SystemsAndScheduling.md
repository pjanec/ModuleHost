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
    OnComponentChange    //  Run only when specific component modified
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

**Implementation:**  Lazy scan of chunk versions (not per-entity flags)

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

