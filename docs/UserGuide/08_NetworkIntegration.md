# Network Integration

## Overview

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

## Core Integration Points

### 1. Entity Lifecycle Management (ELM)

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

### 2. Distributed Ownership

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

### 3. Geographic Transform Services

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

## Integration Workflow

### Setting Up Network Integration

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

## Complete Example: Distributed Tank Simulation

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

## Network Data Flow

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
foreach (var entity in ownedEntities)
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

## Best Practices

### ✅ DO: Use EntityLifecycleModule for Coordinated Creation

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

### ✅ DO: Assign Primary Owner for Every Entity

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

### ✅ DO: Use Geographic Transforms for Global Simulations

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

### ⚠️ DON'T: Publish Descriptors You Don't Own

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

### ⚠️ DON'T: Assume Ownership is Static

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

## Troubleshooting

### Problem: Entity Appears Partially Initialized

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

### Problem: Ownership Conflict Detected

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

### Problem: Geographic Coordinates Incorrect

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

## Performance Characteristics

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

### ELM Best Practices

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

Console.WriteLine($"Pending: {stats.Pending}");        // Entities waiting for ACKs
Console.WriteLine($"Constructed: {stats.Constructed}");  // Successfully activated
Console.WriteLine($"Destroyed: {stats.Destroyed}");      // Successfully cleaned up
Console.WriteLine($"Timeouts: {stats.Timeouts}");        // Failed due to timeout
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

### Ownership Best Practices

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

### Ownership Performance

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
- Timeout-based retry (application-level)
- Monitor statistics for stalled transfers

---

## Geographic Transform Services

### Overview

The Geographic Transform Services bridge FDP's local Cartesian coordinate system with global WGS84 geodetic coordinates (latitude/longitude/altitude). This enables:

- **Network Interoperability:** Exchange entity positions in standardized geodetic format
- **Global Positioning:** Place simulations anywhere on Earth
- **Smooth Network Updates:** Interpolate remote entity positions for rendering

**Module:** `GeographicTransformModule`  
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

### Geographic Transform Performance

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

### Geographic Transform Best Practices

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

### Geographic Troubleshooting

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

---
