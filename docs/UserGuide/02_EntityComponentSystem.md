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

Managed components **MUST** be **immutable records** unless explicitly marked with a `[DataPolicy]`.
By default, mutable classes are treated as **Transient** (NoSnapshot) to prevent concurrency issues.

If you need to snapshot a mutable class, use `DataPolicy.SnapshotViaClone`.

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

**Option A: Attribute (Preferred)**
```csharp
[DataPolicy(DataPolicy.Transient)]
public class UIRenderCache
{
    public Dictionary<int, Texture> Cache = new(); // Safe: main-thread only
}
```

**Option B: Registration Override**
```csharp
repository.RegisterComponent<UIRenderCache>(DataPolicy.Transient);
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
// Register transient managed components (excluded from snapshots)
repository.RegisterComponent<UIRenderCache>(DataPolicy.Transient);
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
