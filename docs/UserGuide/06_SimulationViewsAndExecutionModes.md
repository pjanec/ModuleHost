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

