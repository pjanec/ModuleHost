# Network Integration

## Overview

**Network Integration** in FDP-ModuleHost enables **distributed simulations** where multiple nodes collaborate to simulate a shared world. This section consolidates all network-related concepts and provides an integration guide.

**What Network Integration Provides:**
- **Entity Synchronization:** Share entities across network nodes via DDS
- **Partial Ownership:** Different nodes control different aspects of same entity
- **Lifecycle Coordination:** Dark construction and coordinated teardown across network
- **Geographic Transforms:** Network messages in geodetic coordinates, local sim in Cartesian
- **Fault Tolerance:** Node crashes handled gracefully with ownership recovery

**Advanced Features (NEW - BATCH-12 through BATCH-15):**
- **Ghost Entity Protocol:** Handle out-of-order packet arrival gracefully
- **Reliable Initialization:** Barrier synchronization ensures all nodes ready before activation
- **Multi-Instance Descriptors:** Entities with multiple turrets/weapons (granular ownership)
- **Dynamic Ownership Events:** Reactive systems notified of ownership transfers
- **Validated Performance:** Stress-tested up to 1000+ entities

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

## Quick Start: Network-ELM Integration

### Minimal Setup (2-Node Distributed Simulation)

**Step 1: Create EntityLifecycleModule**
```csharp
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Interfaces;

// ELM coordinates construction across modules
var elm = new EntityLifecycleModule(new[] { 10 });  // Module ID 10 for gateway
```

**Step 2: Create Network Topology**
```csharp
// Define cluster: Local node = 1, Peer = 2
var topology = new StaticNetworkTopology(
    localNodeId: 1,
    allNodes: new[] { 1, 2 }
);
```

**Step 3: Create NetworkGatewayModule**
```csharp
// Gateway participates in ELM for reliable init
var gateway = new NetworkGatewayModule(
    moduleId: 10,      // Must match ELM registration
    localNodeId: 1,
    topology: topology,
    elm: elm
);

gateway.Initialize(null);
kernel.RegisterModule(gateway);
```

**Step 4: Create Ownership Strategy**
```csharp
// Default: All descriptors owned by EntityMaster owner
var strategy = new DefaultOwnershipStrategy();

// OR Custom: Weapon server owns all weapons
var strategy = new WeaponServerStrategy(weaponServerNodeId: 2);
```

**Step 5: Create TKB Database**
```csharp
// Define entity templates
public class SimpleTkbDatabase : ITkbDatabase
{
    private Dictionary<int, TkbTemplate> _templates = new();
    
    public SimpleTkbDatabase()
    {
        // Tank template
        var tankTemplate = new TkbTemplate();
        tankTemplate.Add<Armor>(new Armor { Thickness = 100 });
        tankTemplate.Add<EngineStats>(new EngineStats { Horsepower = 1500 });
        
        _templates[1] = tankTemplate;  // DISEntityType.Kind = 1
    }
    
    public TkbTemplate? GetTemplateByEntityType(DISEntityType entityType)
    {
        return _templates.TryGetValue(entityType.Kind, out var template) ? template : null;
    }
}

var tkbDb = new SimpleTkbDatabase();
```

**Step 6: Create NetworkSpawnerSystem**
```csharp
// System bridges network translators and ELM
var spawner = new NetworkSpawnerSystem(
    tkbDatabase: tkbDb,
    elm: elm,
    ownershipStrategy: strategy,
    localNodeId: 1
);

kernel.RegisterSystem(spawner);
```

**Step 7: Create Translators**
```csharp
// Shared entity lookup map
var networkIdToEntity = new Dictionary<long, Entity>();

// EntityMaster translator
var masterTranslator = new EntityMasterTranslator(1, networkIdToEntity, tkbDb);
kernel.RegisterTranslator(masterTranslator);

// EntityState translator
var stateTranslator = new EntityStateTranslator(1, networkIdToEntity);
kernel.RegisterTranslator(stateTranslator);

// EntityLifecycleStatus translator (for reliable init)
var statusTranslator = new EntityLifecycleStatusTranslator(1, gateway, networkIdToEntity);
kernel.RegisterTranslator(statusTranslator);

// Ownership update translator (for dynamic ownership)
var ownershipTranslator = new OwnershipUpdateTranslator(1, networkIdToEntity);
kernel.RegisterTranslator(ownershipTranslator);

// Multi-instance weapon translator
var weaponTranslator = new WeaponStateTranslator(1, networkIdToEntity);
kernel.RegisterTranslator(weaponTranslator);
```

**Step 8: Create NetworkEgressSystem**
```csharp
// Handles periodic publishing and ForceNetworkPublish
var egressSystem = new NetworkEgressSystem(
    translators: new IDescriptorTranslator[] 
    { 
        masterTranslator, 
        stateTranslator, 
        statusTranslator,
        weaponTranslator 
    },
    writers: new IDataWriter[] 
    { 
        masterWriter, 
        stateWriter, 
        statusWriter,
        weaponWriter 
    }
);

kernel.RegisterSystem(egressSystem);
```

**Step 9: Run Simulation**
```csharp
void Update(float deltaTime)
{
    _repository.Tick();
    _kernel.Update(deltaTime);
    
    // Network-ELM handles:
    // - Ingress: Receive packets, create Ghosts
    // - Spawn: Promote Ghosts, apply templates
    // - ELM: Coordinate construction
    // - Egress: Publish owned descriptors
}
```

**That's it!** The system now handles:
- ✅ Out-of-order packet arrival (Ghost protocol)
- ✅ Distributed construction (ELM coordination)
- ✅ Optional reliable initialization
- ✅ Multi-instance descriptors (if configured)

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

## Complete Example: Multi-Turret Tank with Reliable Init

**Scenario:** 2-node simulation with advanced features
- **Node 1:** Driver station (controls movement + main gun)
- **Node 2:** Gunner station (controls secondary weapon)
- **Features:** Reliable initialization, multi-instance weapons, ownership events

### Node 1 Full Setup

```csharp
using ModuleHost.Core;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Network.Interfaces;
using Fdp.Kernel;

public class Node1Setup
{
    private EntityRepository _repo;
    private ModuleHostKernel _kernel;
    private EntityLifecycleModule _elm;
    private NetworkGatewayModule _gateway;
    private Dictionary<long, Entity> _networkIdToEntity;
    
    public void Initialize()
    {
        _repo = new EntityRepository();
        _kernel = new ModuleHostKernel(_repo);
        _networkIdToEntity = new Dictionary<long, Entity>();
        
        // === 1. Entity Lifecycle Module ===
        _elm = new EntityLifecycleModule(
            participatingModuleIds: new[] { 10 },  // Gateway module
            timeoutFrames: 300
        );
        _kernel.RegisterModule(_elm);
        
        // === 2. Network Topology ===
        var topology = new StaticNetworkTopology(
            localNodeId: 1,
            allNodes: new[] { 1, 2 }  // 2-node cluster
        );
        
        // === 3. NetworkGatewayModule ===
        _gateway = new NetworkGatewayModule(
            moduleId: 10,
            localNodeId: 1,
            topology: topology,
            elm: _elm
        );
        _gateway.Initialize(null);
        _kernel.RegisterModule(_gateway);
        
        // === 4. Ownership Strategy ===
        // Node 1 owns main gun (instance 0)
        // Node 2 owns secondary gun (instance 1)
        var strategy = new MultiTurretStrategy(
            localNodeId: 1,
            secondaryWeaponOwner: 2
        );
        
        // === 5. TKB Database ===
        var tkbDb = new TankTkbDatabase();
        
        // === 6. NetworkSpawnerSystem ===
        var spawner = new NetworkSpawnerSystem(
            tkbDatabase: tkbDb,
            elm: _elm,
            ownershipStrategy: strategy,
            localNodeId: 1
        );
        _kernel.RegisterSystem(spawner);
        
        // === 7. Translators ===
        var masterTranslator = new EntityMasterTranslator(1, _networkIdToEntity, tkbDb);
        var stateTranslator = new EntityStateTranslator(1, _networkIdToEntity);
        var statusTranslator = new EntityLifecycleStatusTranslator(1, _gateway, _networkIdToEntity);
        var weaponTranslator = new WeaponStateTranslator(1, _networkIdToEntity);
        var ownershipTranslator = new OwnershipUpdateTranslator(1, _networkIdToEntity);
        
        _kernel.RegisterTranslator(masterTranslator);
        _kernel.RegisterTranslator(stateTranslator);
        _kernel.RegisterTranslator(statusTranslator);
        _kernel.RegisterTranslator(weaponTranslator);
        _kernel.RegisterTranslator(ownershipTranslator);
        
        // === 8. Egress System ===
        var egressSystem = new NetworkEgressSystem(
            translators: new[] { masterTranslator, stateTranslator, statusTranslator, weaponTranslator },
            writers: new[] { masterWriter, stateWriter, statusWriter, weaponWriter }
        );
        _kernel.RegisterSystem(egressSystem);
        
        // === 9. Register Components ===
        _repo.RegisterComponent<NetworkIdentity>();
        _repo.RegisterComponent<NetworkOwnership>();
        _repo.RegisterComponent<NetworkSpawnRequest>();
        _repo.RegisterComponent<PendingNetworkAck>();
        _repo.RegisterComponent<ForceNetworkPublish>();
        _repo.RegisterComponent<Position>();
        _repo.RegisterComponent<Velocity>();
        _repo.RegisterComponent<Armor>();
        _repo.RegisterComponent<EngineStats>();
    }
    
    public void CreateTank()
    {
        // Publish EntityMaster to network (will be received by both nodes)
        var descriptor = new EntityMasterDescriptor
        {
            EntityId = 100,
            OwnerId = 1,  // Node 1 is primary owner
            Type = new DISEntityType { Kind = 1, Category = 1 },  // Main Battle Tank
            Name = "M1_Abrams_01",
            Flags = MasterFlags.ReliableInit  // Enable reliable initialization
        };
        
        _masterWriter.Write(descriptor);
        
        // System will automatically:
        // 1. EntityMasterTranslator creates entity + NetworkSpawnRequest
        // 2. NetworkSpawnerSystem applies TKB template, determines ownership
        // 3. NetworkGatewayModule waits for Node 2 to confirm
        // 4. Entity becomes Active when both nodes ready
    }
    
    public void Update(float deltaTime)
    {
        _repo.Tick();
        _kernel.Update(deltaTime);
        
        // System handles:
        // - Receiving EntityState from network (Ghost or update)
        // - Receiving WeaponState instance 1 from Node 2
        // - Publishing EntityState (movement - we own it)
        // - Publishing WeaponState instance 0 (main gun - we own it)
    }
}

// === Custom Ownership Strategy ===
public class MultiTurretStrategy : IOwnershipDistributionStrategy
{
    private readonly int _localNodeId;
    private readonly int _secondaryWeaponOwner;
    
    public MultiTurretStrategy(int localNodeId, int secondaryWeaponOwner)
    {
        _localNodeId = localNodeId;
        _secondaryWeaponOwner = secondaryWeaponOwner;
    }
    
    public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType,
                                 int masterNodeId, long instanceId)
    {
        // Weapon instance 1 (secondary) owned by Node 2
        if (descriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID && instanceId == 1)
        {
            return _secondaryWeaponOwner;
        }
        
        // Everything else: default to master owner
        return null;
    }
}

// === TKB Database ===
public class TankTkbDatabase : ITkbDatabase
{
    private TkbTemplate _tankTemplate;
    
    public TankTkbDatabase()
    {
        _tankTemplate = new TkbTemplate();
        _tankTemplate.Add<Armor>(new Armor { Thickness = 100, Type = ArmorType.Composite });
        _tankTemplate.Add<EngineStats>(new EngineStats { Horsepower = 1500, MaxSpeed = 70 });
        _tankTemplate.Add<TurretRotation>(new TurretRotation { MaxSpeed = 45 });
    }
    
    public TkbTemplate? GetTemplateByEntityType(DISEntityType entityType)
    {
        if (entityType.Kind == 1 && entityType.Category == 1)
            return _tankTemplate;
        
        return null;
    }
    
    public TkbTemplate? GetTemplateByName(string templateName)
    {
        return templateName == "Tank" ? _tankTemplate : null;
    }
}
```

### Node 2 Setup (Similar)

Node 2 uses identical setup code with `localNodeId: 2`.

**Key Differences:**
- `localNodeId: 2` in all constructors
- Same `allNodes: [1, 2]` in topology
- Same ownership strategy (strategy determines per-instance)

### Weapon Control System (Reactive to Ownership)

```csharp
using ModuleHost.Core.Network;

[UpdateInPhase(SystemPhase.Simulation)]
public class WeaponControlSystem : IModuleSystem
{
    private readonly int _localNodeId;
    
    public WeaponControlSystem(int localNodeId)
    {
        _localNodeId = localNodeId;
    }
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // === React to ownership changes ===
        foreach (var evt in view.ConsumeEvents<DescriptorAuthorityChanged>())
        {
            if (evt.DescriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
            {
                if (evt.IsNowOwner)
                {
                    Console.WriteLine($"[Node {_localNodeId}] Acquired weapon {evt.InstanceId} control");
                    // Enable player input for this weapon
                }
                else
                {
                    Console.WriteLine($"[Node {_localNodeId}] Lost weapon {evt.InstanceId} control");
                    // Disable player input
                }
            }
        }
        
        // === Update owned weapons ===
        var query = view.Query()
            .WithManaged<WeaponStates>()
            .With<NetworkIdentity>()
            .WithLifecycle(EntityLifecycle.Active)
            .Build();
        
        foreach (var entity in query)
        {
            var weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
            
            // Update each weapon instance we own
            foreach (var kvp in weaponStates.Weapons)
            {
                long instanceId = kvp.Key;
                
                // Check if we own this weapon instance
                if (!view.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, instanceId))
                    continue;
                
                // Update weapon (player input, AI, etc.)
                var weapon = kvp.Value;
                weapon.AzimuthAngle += GetPlayerInput() * deltaTime * 45f;  // 45°/sec
                weaponStates.Weapons[instanceId] = weapon;
            }
            
            // WeaponStateTranslator will publish owned instances during egress
        }
    }
    
    private float GetPlayerInput()
    {
        // Simulate player input
        return UnityEngine.Input.GetAxis("Horizontal");
    }
}
```

### Output (Console Logs)

**Node 1:**
```
[NetworkGatewayModule] Entity 0: Waiting for 1 peer ACK
[Node 1] Entity 100 ready: Waiting for Node 2...
[NetworkGatewayModule] Entity 0: Received ACK from node 2
[NetworkGatewayModule] Entity 0: All peer ACKs received, sending local ACK to ELM
[ELM] Entity 100: All modules ACKed, transitioning to Active
[Node 1] Acquired weapon 0 control
[WeaponControlSystem] Publishing weapon 0: Azimuth=45.0°, Ammo=100
```

**Node 2:**
```
[NetworkGatewayModule] Entity 0: Fast mode (no PendingNetworkAck), ACKing immediately
[ELM] Entity 100: All modules ACKed, transitioning to Active
[EntityLifecycleStatusTranslator] Published Active status for entity 100
[Node 2] Acquired weapon 1 control
[WeaponControlSystem] Publishing weapon 1: Azimuth=120.0°, Ammo=75
```

**Result:** Tank spawned with reliable synchronization. Node 1 controls movement + main gun, Node 2 controls secondary weapon. Both nodes see complete tank state.

---

## Complete Example: Distributed Tank Simulation (Legacy)

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

## Network-ELM Integration (Advanced)

**Implemented in:** BATCH-12 through BATCH-15 ⭐

The Network-ELM Integration provides advanced distributed entity lifecycle features for production-grade networked simulations, including ghost entity handling, reliable initialization, and multi-instance descriptor support.

### Overview

The Network-ELM system solves critical challenges in distributed simulations:

**1. Out-of-Order Packet Arrival (Ghost Protocol)**
- Network packets can arrive in any order
- `EntityState` may arrive before `EntityMaster`
- System handles gracefully with "Ghost" entities

**2. Distributed Construction Coordination**
- Multiple nodes must initialize entities simultaneously
- Reliable mode ensures all nodes complete before activation
- Fast mode prioritizes latency over synchronization

**3. Multi-Instance Descriptors**
- Entities can have multiple instances of same descriptor type
- Example: Tank with 2 turrets (2 `WeaponState` instances)
- Each instance can be owned by different nodes

**4. Dynamic Ownership Transfer**
- Ownership can change at runtime
- Event notifications for reactive systems
- Automatic confirmation protocol

---

### Ghost Entity Protocol

**Problem:** In distributed systems, packets arrive out of order. What happens if `EntityState` (movement data) arrives before `EntityMaster` (entity metadata)?

**Solution:** Ghost entities - placeholder entities that await complete information.

#### Entity Lifecycle States (Extended)

```csharp
public enum EntityLifecycle : byte
{
    Constructing = 1,  // Being initialized (invisible to queries)
    Active = 2,        // Fully initialized and active
    TearDown = 3,      // Being destroyed
    Ghost = 4          // NEW: Awaiting EntityMaster (out-of-order arrival)
}
```

#### Ghost Creation Flow

**Scenario:** `EntityState` arrives before `EntityMaster`

```
Step 1: EntityState arrives (no entity exists)
   ↓
EntityStateTranslator creates GHOST entity
   ├─ Sets lifecycle: Ghost
   ├─ Adds Position, Velocity (from packet)
   ├─ Adds NetworkIdentity
   └─ Entity NOT visible to normal queries

Step 2: EntityMaster arrives (entity exists as Ghost)
   ↓
EntityMasterTranslator finds existing Ghost
   ├─ Adds NetworkSpawnRequest component
   └─ Leaves lifecycle as Ghost

Step 3: NetworkSpawnerSystem processes
   ↓
   ├─ Applies TKB template (preserveExisting=true)
   ├─ Determines ownership
   ├─ Promotes: Ghost → Constructing
   └─ Calls ELM.BeginConstruction()

Step 4: All modules ACK
   ↓
ELM transitions: Constructing → Active
   └─ Entity now visible to queries
```

#### Ghost Timeout Protection

```csharp
// If EntityMaster never arrives...
public const int GHOST_TIMEOUT_FRAMES = 300;  // 5 seconds @ 60Hz

// Ghost entities older than 300 frames are automatically deleted
// Prevents memory leaks from lost packets
```

**Implementation:**
```csharp
// Creating a Ghost entity
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    var repo = (EntityRepository)view;
    
    foreach (var sample in reader.TakeSamples())
    {
        var desc = (EntityStateDescriptor)sample.Data;
        
        // Entity doesn't exist - create as Ghost
        var entity = repo.CreateEntity();
        repo.AddComponent(entity, new NetworkIdentity { Value = desc.EntityId });
        repo.AddComponent(entity, new Position { Value = desc.Location });
        repo.AddComponent(entity, new Velocity { Value = desc.Velocity });
        
        // Mark as Ghost (awaiting EntityMaster)
        repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
        
        _networkIdToEntity[desc.EntityId] = entity;
    }
}
```

#### Querying Ghost Entities

**Default (normal queries):**
```csharp
// Only returns Active entities (Ghosts excluded)
var query = repo.Query()
    .With<Position>()
    .Build();

foreach (var entity in query)
{
    // Never sees Ghost entities
}
```

**Including Ghosts (for debugging):**
```csharp
// Include ALL lifecycle states
var query = repo.Query()
    .With<Position>()
    .IncludeAll()  // Includes Ghost, Constructing, Active, TearDown
    .Build();
```

**Explicit Ghost query:**
```csharp
// Only Ghost entities
var query = repo.Query()
    .With<NetworkIdentity>()
    .WithLifecycle(EntityLifecycle.Ghost)
    .Build();

foreach (var entity in query)
{
    Console.WriteLine($"Ghost entity awaiting EntityMaster: {entity.Index}");
}
```

---

### NetworkSpawnerSystem (Bridge Between Network and ELM)

The `NetworkSpawnerSystem` is the integration point between the network gateway and Entity Lifecycle Management. It processes `NetworkSpawnRequest` components created by translators.

#### Responsibilities

1. **TKB Template Application** - Apply predefined component sets
2. **Ghost Promotion** - Upgrade Ghost entities to Constructing
3. **Ownership Determination** - Assign descriptor ownership (including multi-instance)
4. **ELM Integration** - Call `BeginConstruction()` to start coordination

#### Configuration

```csharp
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.ELM;

// Create dependencies
var tkbDatabase = new MyTkbDatabase();  // Implements ITkbDatabase
var elm = new EntityLifecycleModule(new[] { PHYSICS_ID, AI_ID, NETWORK_ID });
var ownershipStrategy = new MyOwnershipStrategy();  // Implements IOwnershipDistributionStrategy

// Create system
var spawner = new NetworkSpawnerSystem(
    tkbDatabase: tkbDatabase,
    elm: elm,
    ownershipStrategy: ownershipStrategy,
    localNodeId: 1
);

// Register system (runs in Input phase, after translators)
kernel.RegisterSystem(spawner);
```

#### TKB Templates (Type-Kit-Bag)

**Purpose:** Define standard component sets for entity types.

```csharp
public interface ITkbDatabase
{
    TkbTemplate? GetTemplateByEntityType(DISEntityType entityType);
    TkbTemplate? GetTemplateByName(string templateName);
}

// Example implementation
public class TankTkbDatabase : ITkbDatabase
{
    public TkbTemplate? GetTemplateByEntityType(DISEntityType entityType)
    {
        if (entityType.Kind == 1 && entityType.Category == 1)  // Main Battle Tank
        {
            return _tankTemplate;
        }
        return null;
    }
}

// Creating a template
var tankTemplate = new TkbTemplate();
tankTemplate.Add<Armor>(new Armor { Thickness = 100 });
tankTemplate.Add<TurretRotation>(new TurretRotation { MaxSpeed = 45 });
tankTemplate.Add<EngineStats>(new EngineStats { Horsepower = 1500 });
```

#### PreserveExisting Flag

When promoting a Ghost entity, existing components (from `EntityState`) must be preserved:

```csharp
// Ghost has Position and Velocity from EntityState packet
var entity = FindGhostEntity(networkId);

// Apply template (add Armor, TurretRotation, EngineStats)
// preserveExisting=true means Position and Velocity NOT overwritten
tkbTemplate.ApplyTo(repo, entity, preserveExisting: true);

// Result: Entity has all components (Position, Velocity, Armor, TurretRotation, EngineStats)
```

#### Ownership Strategy

**Purpose:** Determine which node owns which descriptors for partial ownership scenarios.

```csharp
public interface IOwnershipDistributionStrategy
{
    int? GetInitialOwner(
        long descriptorTypeId,
        DISEntityType entityType,
        int masterNodeId,
        long instanceId
    );
}

// Example: Weapon Server Strategy
public class WeaponServerStrategy : IOwnershipDistributionStrategy
{
    private readonly int _weaponServerNodeId;
    
    public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType, 
                                 int masterNodeId, long instanceId)
    {
        // Weapon descriptors go to dedicated weapon server
        if (descriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
        {
            return _weaponServerNodeId;
        }
        
        // Everything else: default to master owner
        return null;
    }
}
```

**Default Strategy:**
```csharp
public class DefaultOwnershipStrategy : IOwnershipDistributionStrategy
{
    public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType,
                                 int masterNodeId, long instanceId)
    {
        return null;  // null = use master owner for everything
    }
}
```

#### NetworkSpawnRequest Component

**Created by:** EntityMasterTranslator when `EntityMaster` arrives

```csharp
public struct NetworkSpawnRequest
{
    public long NetworkId;
    public DISEntityType DisType;
    public int MasterNodeId;
    public int PrimaryOwnerId;
    public MasterFlags Flags;
    public string TemplateName;
}
```

**Processed by:** NetworkSpawnerSystem (automatically removed after processing)

---

### Reliable Initialization Protocol

**Problem:** In distributed simulations, entities must be fully initialized on ALL nodes before any node begins simulation. Without coordination, nodes may interact with partially-initialized entities.

**Solution:** Reliable initialization mode with barrier synchronization.

#### Initialization Modes

**Fast Mode (Default):**
```
EntityMaster arrives → Create entity → Initialize locally → ACTIVE
                                                (50-100ms)
```
- ✅ Low latency (~1 frame)
- ✅ No network coordination
- ⚠️ Other nodes may not be ready
- **Use for:** Non-critical entities (VFX, props)

**Reliable Mode (Opt-in):**
```
EntityMaster arrives → Create entity → Initialize locally
                                      ↓
                                   Wait for peers
                                      ↓
                    All nodes ACK → ACTIVE
                                 (300ms typical)
```
- ✅ Guaranteed cross-node synchronization
- ✅ No race conditions
- ⚠️ Higher latency (network RTT + timeout)
- **Use for:** Critical entities (vehicles, players)

#### Enabling Reliable Mode

**Set flag in EntityMasterDescriptor:**
```csharp
[Flags]
public enum MasterFlags
{
    None = 0,
    ReliableInit = 1 << 0
}

// When creating entity for network
var descriptor = new EntityMasterDescriptor
{
    EntityId = 100,
    OwnerId = 1,
    Type = new DISEntityType { Kind = 1, Category = 1 },  // Tank
    Flags = MasterFlags.ReliableInit  // Enable reliable mode
};

_ddsWriter.Write(descriptor);
```

**On receiving node:**
```csharp
// EntityMasterTranslator adds PendingNetworkAck for reliable entities
if ((desc.Flags & MasterFlags.ReliableInit) != 0)
{
    cmd.SetComponent(entity, new PendingNetworkAck());
}
```

#### NetworkGatewayModule (Barrier Coordination)

The `NetworkGatewayModule` participates in ELM to implement the barrier.

**Setup:**
```csharp
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.ELM;

// Network topology (who are the peers?)
var topology = new StaticNetworkTopology(
    localNodeId: 1,
    allNodes: new[] { 1, 2, 3 }  // 3-node cluster
);

// Create gateway module
var gateway = new NetworkGatewayModule(
    moduleId: 10,  // Unique module ID for ELM
    localNodeId: 1,
    topology: topology,
    elm: elm  // Reference to EntityLifecycleModule
);

gateway.Initialize(null);  // Registers with ELM
kernel.RegisterModule(gateway);
```

#### Reliable Init Flow

```
Node 1: EntityMaster + PendingNetworkAck
   ↓
ELM publishes ConstructionOrder
   ↓
NetworkGatewayModule receives ConstructionOrder
   ├─ Check: Entity has PendingNetworkAck? YES
   ├─ Determine expected peers: [2, 3]
   ├─ Wait for EntityLifecycleStatus from Node 2
   ├─ Wait for EntityLifecycleStatus from Node 3
   └─ ACK withheld

Node 2 & Node 3: Receive EntityMaster, initialize
   ↓
Become Active (ELM completes locally)
   ↓
Publish EntityLifecycleStatus (Active) to network

Node 1: Receives status from Node 2
   ├─ Peer 2 ready ✓
   └─ Still waiting for Node 3...

Node 1: Receives status from Node 3
   ├─ Peer 3 ready ✓
   ├─ All peers confirmed!
   └─ Send ConstructionAck to ELM

Node 1: ELM receives ACK
   └─ Entity transitions to Active
```

#### Timeout Protection

```csharp
public const int RELIABLE_INIT_TIMEOUT_FRAMES = 300;  // 5 seconds @ 60Hz

// If peer ACKs don't arrive within 300 frames:
// - NetworkGatewayModule sends ACK anyway
// - Entity becomes Active (prevents infinite blocking)
// - Warning logged
```

**Implementation:**
```csharp
private void CheckPendingAckTimeouts(IEntityCommandBuffer cmd, uint currentFrame)
{
    var timedOut = new List<Entity>();
    
    foreach (var kvp in _pendingStartFrame)
    {
        if (currentFrame - kvp.Value > NetworkConstants.RELIABLE_INIT_TIMEOUT_FRAMES)
        {
            Console.Error.WriteLine($"[NetworkGatewayModule] Entity {kvp.Key.Index}: Timeout waiting for peer ACKs");
            timedOut.Add(kvp.Key);
        }
    }
    
    foreach (var entity in timedOut)
    {
        // Timeout - ACK anyway to prevent blocking forever
        _elm.AcknowledgeConstruction(entity, ModuleId, currentFrame, cmd);
        cmd.RemoveComponent<PendingNetworkAck>(entity);
        
        _pendingPeerAcks.Remove(entity);
        _pendingStartFrame.Remove(entity);
    }
}
```

#### INetworkTopology Interface

**Purpose:** Abstract peer discovery for different network configurations.

```csharp
public interface INetworkTopology
{
    int LocalNodeId { get; }
    IEnumerable<int> GetExpectedPeers(DISEntityType entityType);
}
```

**Static Topology (Simple):**
```csharp
public class StaticNetworkTopology : INetworkTopology
{
    private readonly int _localNodeId;
    private readonly int[] _allNodes;
    
    public int LocalNodeId => _localNodeId;
    
    public StaticNetworkTopology(int localNodeId, int[] allNodes)
    {
        _localNodeId = localNodeId;
        _allNodes = allNodes;
    }
    
    public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
    {
        // Return all nodes except self
        return _allNodes.Where(id => id != _localNodeId);
    }
}
```

**Dynamic Topology (Advanced):**
```csharp
public class DynamicNetworkTopology : INetworkTopology
{
    public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
    {
        // Query service discovery for active nodes
        var activePeers = _serviceDiscovery.GetActivePeers();
        
        // Filter by entity type (e.g., only tank simulators for tank entities)
        return activePeers.Where(p => p.Capabilities.Contains(entityType));
    }
}
```

#### EntityLifecycleStatusDescriptor

**Message for peer synchronization:**

```csharp
public class EntityLifecycleStatusDescriptor
{
    public long EntityId { get; set; }
    public int NodeId { get; set; }
    public EntityLifecycle State { get; set; }
    public long Timestamp { get; set; }
}
```

**Published by:** EntityLifecycleStatusTranslator when entities become Active

```csharp
public void ScanAndPublish(ISimulationView view, IDataWriter writer)
{
    // Query: Active entities with PendingNetworkAck (were in reliable mode)
    var query = view.Query()
        .With<NetworkIdentity>()
        .With<PendingNetworkAck>()
        .WithLifecycle(EntityLifecycle.Active)
        .Build();
    
    foreach (var entity in query)
    {
        var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
        
        var status = new EntityLifecycleStatusDescriptor
        {
            EntityId = networkId,
            NodeId = _localNodeId,
            State = EntityLifecycle.Active,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        writer.Write(status);
    }
}
```

**Received by:** EntityLifecycleStatusTranslator, forwarded to NetworkGatewayModule

```csharp
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    foreach (var sample in reader.TakeSamples())
    {
        var status = (EntityLifecycleStatusDescriptor)sample.Data;
        
        // Ignore our own messages
        if (status.NodeId == _localNodeId)
            continue;
        
        // Find entity
        if (!_networkIdToEntity.TryGetValue(status.EntityId, out var entity))
            continue;
        
        // Forward to NetworkGatewayModule
        _gateway.ReceiveLifecycleStatus(entity, status.NodeId, status.State, cmd, currentFrame);
    }
}
```

---

### Multi-Instance Descriptor Support

**Problem:** Some entities have multiple components of the same type. Example: Tank with 2 turrets needs 2 `WeaponState` descriptors.

**Solution:** Instance IDs - each descriptor instance identified by `(TypeId, InstanceId)` composite key.

#### IDataSample Enhancement

```csharp
public interface IDataSample
{
    object Data { get; }
    DdsInstanceState InstanceState { get; }
    long EntityId { get; }
    long InstanceId { get; }  // NEW: Instance ID for multi-instance descriptors
}

public class DataSample : IDataSample
{
    public object Data { get; set; }
    public DdsInstanceState InstanceState { get; set; }
    public long EntityId { get; set; }
    public long InstanceId { get; set; } = 0;  // Default: single instance
}
```

**Backward Compatibility:** Existing single-instance descriptors use `InstanceId = 0` (default).

#### Multi-Instance Descriptor Example

**WeaponStateDescriptor:**
```csharp
public class WeaponStateDescriptor
{
    public long EntityId { get; set; }
    public long InstanceId { get; set; }  // 0=primary turret, 1=secondary, etc.
    public float AzimuthAngle { get; set; }
    public float ElevationAngle { get; set; }
    public int AmmoCount { get; set; }
    public WeaponStatus Status { get; set; }
}

public enum WeaponStatus : byte
{
    Ready = 0,
    Firing = 1,
    Reloading = 2,
    Jammed = 3,
    Disabled = 4
}
```

#### Multi-Instance Component Storage

```csharp
// Component stores multiple weapon instances
public class WeaponStates
{
    /// <summary>
    /// Maps weapon instance ID -> weapon state.
    /// Instance 0 = primary weapon, Instance 1+ = secondary weapons.
    /// </summary>
    public Dictionary<long, WeaponState> Weapons { get; set; } = new();
}

public struct WeaponState
{
    public float AzimuthAngle;
    public float ElevationAngle;
    public int AmmoCount;
    public WeaponStatus Status;
}
```

#### Multi-Instance Translator

**Ingress (receiving weapon updates):**
```csharp
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    foreach (var sample in reader.TakeSamples())
    {
        var desc = (WeaponStateDescriptor)sample.Data;
        
        if (!_networkIdToEntity.TryGetValue(desc.EntityId, out var entity))
            continue;
        
        // Get or create WeaponStates component
        WeaponStates weaponStates;
        if (view.HasManagedComponent<WeaponStates>(entity))
        {
            weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
        }
        else
        {
            weaponStates = new WeaponStates();
            cmd.AddManagedComponent(entity, weaponStates);
        }
        
        // Update SPECIFIC weapon instance (not all weapons!)
        weaponStates.Weapons[desc.InstanceId] = new WeaponState
        {
            AzimuthAngle = desc.AzimuthAngle,
            ElevationAngle = desc.ElevationAngle,
            AmmoCount = desc.AmmoCount,
            Status = desc.Status
        };
    }
}
```

**Egress (publishing owned weapons):**
```csharp
public void ScanAndPublish(ISimulationView view, IDataWriter writer)
{
    var query = view.Query()
        .WithManaged<WeaponStates>()
        .WithLifecycle(EntityLifecycle.Active)
        .Build();
    
    foreach (var entity in query)
    {
        var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
        var weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
        
        // Publish each weapon instance we own
        foreach (var kvp in weaponStates.Weapons)
        {
            long instanceId = kvp.Key;
            
            // Check ownership for THIS instance
            if (!view.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, instanceId))
                continue;  // Don't publish weapons we don't own
            
            var weaponState = kvp.Value;
            
            var desc = new WeaponStateDescriptor
            {
                EntityId = networkId,
                InstanceId = instanceId,
                AzimuthAngle = weaponState.AzimuthAngle,
                ElevationAngle = weaponState.ElevationAngle,
                AmmoCount = weaponState.AmmoCount,
                Status = weaponState.Status
            };
            
            writer.Write(desc);
        }
    }
}
```

#### Composite Key Ownership

**Packing:**
```csharp
public static class OwnershipExtensions
{
    // Pack (TypeId, InstanceId) into single long key
    public static long PackKey(long descriptorTypeId, long instanceId)
    {
        return (descriptorTypeId << 32) | (uint)instanceId;
    }
    
    // Unpack key back to (TypeId, InstanceId)
    public static (long typeId, long instanceId) UnpackKey(long packedKey)
    {
        long typeId = packedKey >> 32;
        long instanceId = (uint)packedKey;  // Mask lower 32 bits
        return (typeId, instanceId);
    }
    
    // Check ownership for specific instance
    public static bool OwnsDescriptor(this ISimulationView view, Entity entity,
                                       long descriptorTypeId, long instanceId)
    {
        var ownership = view.GetManagedComponentRO<DescriptorOwnership>(entity);
        long key = PackKey(descriptorTypeId, instanceId);
        
        if (ownership.Map.TryGetValue(key, out int owner))
        {
            return owner == /* localNodeId */;
        }
        
        // Not in map = use primary owner
        var networkOwnership = view.GetComponentRO<NetworkOwnership>(entity);
        return networkOwnership.PrimaryOwnerId == networkOwnership.LocalNodeId;
    }
}
```

**Benefits:**
- Dictionary lookup instead of tuple allocation
- No GC pressure
- 32 bits per field (4 billion IDs each)

#### Multi-Turret Tank Example

**Scenario:** Tank with 2 turrets, split ownership

**Node 1 (Driver):**
- Owns EntityMaster
- Owns EntityState (movement)
- Owns Weapon Instance 0 (main gun)

**Node 2 (Gunner):**
- Owns Weapon Instance 1 (coaxial mg)

**Setup:**
```csharp
// NetworkSpawnerSystem determines ownership
private void DetermineDescriptorOwnership(EntityRepository repo, Entity entity, NetworkSpawnRequest request)
{
    var descOwnership = new DescriptorOwnership();
    
    // EntityMaster (instance 0)
    AssignDescriptorOwnership(descOwnership, NetworkConstants.ENTITY_MASTER_DESCRIPTOR_ID, request, 0);
    
    // EntityState (instance 0)
    AssignDescriptorOwnership(descOwnership, NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID, request, 0);
    
    // WeaponState (multi-instance)
    int weaponInstanceCount = GetWeaponInstanceCount(request.DisType);  // Returns 2 for tank
    for (int i = 0; i < weaponInstanceCount; i++)
    {
        AssignDescriptorOwnership(descOwnership, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, request, i);
    }
    
    repo.SetManagedComponent(entity, descOwnership);
}

private void AssignDescriptorOwnership(DescriptorOwnership descOwnership, 
                                        long descriptorTypeId, 
                                        NetworkSpawnRequest request, 
                                        int instanceId)
{
    var owner = _strategy.GetInitialOwner(descriptorTypeId, request.DisType, 
                                           request.MasterNodeId, instanceId);
    
    // Only populate map if different from primary owner
    if (owner != request.PrimaryOwnerId)
    {
        long key = OwnershipExtensions.PackKey(descriptorTypeId, instanceId);
        descOwnership.Map[key] = owner;
    }
}
```

**Result:**
- Node 1 publishes Weapon Instance 0 updates
- Node 2 publishes Weapon Instance 1 updates
- Both nodes receive both weapon states
- Full tank simulation with distributed control

---

### Dynamic Ownership Transfer with Events

**Implemented in:** BATCH-13 ⭐

When ownership changes, systems need to react (stop publishing, start subscribing, etc.).

#### DescriptorAuthorityChanged Event

```csharp
[EventId(9010)]
public struct DescriptorAuthorityChanged
{
    public Entity Entity;
    public long DescriptorTypeId;
    public long InstanceId;
    public int PreviousOwner;
    public int NewOwner;
    public bool IsNowOwner;  // True if local node acquired ownership
}
```

#### Automatic Event Emission

**OwnershipUpdateTranslator** emits events when ownership transitions:

```csharp
public void ProcessOwnershipUpdate(IDataReader reader, IEntityCommandBuffer cmd, EntityRepository repo)
{
    foreach (var sample in reader.TakeSamples())
    {
        var update = (OwnershipUpdate)sample.Data;
        
        // ... update ownership map ...
        
        // Emit event if ownership changed
        bool isNowOwner = (update.NewOwner == _localNodeId);
        bool wasOwner = (previousOwner == _localNodeId);
        
        if (isNowOwner != wasOwner)
        {
            var evt = new DescriptorAuthorityChanged
            {
                Entity = entity,
                DescriptorTypeId = update.DescrTypeId,
                InstanceId = update.InstanceId,
                PreviousOwner = previousOwner,
                NewOwner = update.NewOwner,
                IsNowOwner = isNowOwner
            };
            
            cmd.PublishEvent(evt);
        }
        
        // If we became owner, force immediate egress
        if (isNowOwner)
        {
            cmd.SetComponent(entity, new ForceNetworkPublish());
        }
    }
}
```

#### Reacting to Ownership Changes

**Example: Weapon System**
```csharp
public class WeaponControlSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // React to ownership changes
        foreach (var evt in view.ConsumeEvents<DescriptorAuthorityChanged>())
        {
            if (evt.DescriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
            {
                if (evt.IsNowOwner)
                {
                    // We acquired weapon control
                    Console.WriteLine($"Acquired control of weapon {evt.InstanceId} on entity {evt.Entity.Index}");
                    
                    // Enable player input for this weapon
                    cmd.AddComponent(evt.Entity, new PlayerControlled { WeaponInstance = evt.InstanceId });
                }
                else
                {
                    // We lost weapon control
                    Console.WriteLine($"Lost control of weapon {evt.InstanceId} on entity {evt.Entity.Index}");
                    
                    // Disable player input
                    cmd.RemoveComponent<PlayerControlled>(evt.Entity);
                }
            }
        }
    }
}
```

#### ForceNetworkPublish Component

**Purpose:** Force immediate egress (bypass normal frequency throttling).

```csharp
public struct ForceNetworkPublish { }  // Tag component
```

**Added when:**
- Ownership acquired (send confirmation write)
- Critical state change (death, spawn)

**Processed by NetworkEgressSystem:**
```csharp
public void Execute(ISimulationView view, float deltaTime)
{
    var cmd = view.GetCommandBuffer();
    
    // Process force-publish requests
    var query = view.Query()
        .With<ForceNetworkPublish>()
        .Build();
    
    foreach (var entity in query)
    {
        // Remove component (one-time trigger)
        cmd.RemoveComponent<ForceNetworkPublish>(entity);
        
        // Translators will publish this entity immediately
    }
    
    // Normal periodic egress...
    for (int i = 0; i < _translators.Length; i++)
    {
        _translators[i].ScanAndPublish(view, _writers[i]);
    }
}
```

---

### Performance Characteristics

**Validated in:** BATCH-15 (Stress & Reliability Testing) ⭐

#### Scalability

| Entity Count | Egress Time | Ingress Time (10% updates) | Notes |
|--------------|-------------|----------------------------|-------|
| 100 | ~15ms | ~5ms | Baseline |
| 500 | ~70ms | ~20ms | Linear scaling |
| 1000 | ~150ms | ~40ms | Acceptable for 60Hz |
| 5000+ | >200ms | >100ms | Requires optimization (Interest Management) |

**Bottleneck:** Egress (querying all entities, checking ownership per descriptor)

**Recommendation:** For 5,000+ networked entities:
- Implement spatial interest management
- Reduce egress frequency (30Hz for distant entities)
- Use area-of-responsibility clustering

#### Operation Costs

| Operation | Cost | Notes |
|-----------|------|-------|
| Ownership check (composite key) | ~10ns | Bitwise pack + dictionary lookup |
| Ghost creation | ~500ns | Entity creation + 3 components |
| Ghost promotion | ~50ms | TKB template application (10 entities) |
| Reliable init barrier | 300ms typical | Network RTT + peer processing |
| Timeout check (100 entities) | <1ms | Simple arithmetic per entity |

#### Memory Overhead

**Per networked entity:**
- NetworkIdentity: 8 bytes
- NetworkOwnership: 16 bytes
- DescriptorOwnership: 40 bytes + (16 bytes × partial owners)
- WeaponStates (2 instances): 88 bytes

**Total:** ~160 bytes per entity (excluding game components)

**Network bandwidth (typical):**
- EntityState: ~100 bytes
- WeaponState: ~50 bytes
- EntityLifecycleStatus: ~30 bytes
- 1000 entities @ 60Hz: ~9 MB/s

---

### Network-ELM Best Practices

#### ✅ DO: Use Reliable Mode for Critical Entities

```csharp
// Critical entity (player tank)
var descriptor = new EntityMasterDescriptor
{
    EntityId = 100,
    Flags = MasterFlags.ReliableInit,  // Ensure all nodes ready
    // ...
};
```

**Why:** Prevents race conditions where nodes interact before full initialization.

#### ✅ DO: Use Fast Mode for Transient Entities

```csharp
// VFX, particles, projectiles
var descriptor = new EntityMasterDescriptor
{
    EntityId = 200,
    Flags = MasterFlags.None,  // Fast mode
    // ...
};
```

**Why:** Reduces latency for entities where synchronization isn't critical.

#### ✅ DO: Query Ghosts for Debugging

```csharp
// Monitor ghost entities
var ghostQuery = repo.Query()
    .With<NetworkIdentity>()
    .WithLifecycle(EntityLifecycle.Ghost)
    .Build();

if (ghostQuery.Count() > 10)
{
    Console.WriteLine($"Warning: {ghostQuery.Count()} ghost entities awaiting EntityMaster");
}
```

**Why:** High ghost count indicates packet loss or EntityMaster publication issues.

#### ✅ DO: Use Multi-Instance for Complex Entities

```csharp
// Tank with multiple turrets
var weaponStates = new WeaponStates();
weaponStates.Weapons[0] = new WeaponState { /* main gun */ };
weaponStates.Weapons[1] = new WeaponState { /* coaxial MG */ };
weaponStates.Weapons[2] = new WeaponState { /* commander MG */ };

repo.AddManagedComponent(tank, weaponStates);
```

**Why:** Enables granular ownership (different nodes control different weapons).

#### ⚠️ DON'T: Assume EntityMaster Arrives First

```csharp
// ❌ WRONG: Assuming master-first
if (!_networkIdToEntity.ContainsKey(entityId))
{
    throw new Exception("EntityMaster should arrive first!");
}

// ✅ CORRECT: Handle either order
if (!_networkIdToEntity.TryGetValue(entityId, out var entity))
{
    // Create as Ghost - EntityMaster will promote later
    entity = CreateGhostEntity(entityId, entityState);
}
```

**Why:** Network packets arrive out of order. Ghost protocol handles this.

#### ⚠️ DON'T: Forget Timeout Configuration

```csharp
// ❌ WRONG: Infinite wait (if timeout not configured)
var gateway = new NetworkGatewayModule(moduleId, localNodeId, topology, elm);
// Default timeout is 300 frames, but verify it's reasonable for your network

// ✅ CORRECT: Explicit timeout based on network conditions
// If high latency network (>500ms RTT), increase timeout
// Timeout in frames, not ms (account for frame rate)
```

**Why:** Without timeout, dead nodes cause entities to block forever.

#### ⚠️ DON'T: Mix Instance IDs Across Descriptor Types

```csharp
// ❌ WRONG: Reusing instance ID 0 for different purposes
ownership.Map[PackKey(WEAPON_STATE, 0)] = 1;
ownership.Map[PackKey(SENSOR_STATE, 0)] = 2;  // OK - different descriptor type

// ✅ CORRECT: Instance IDs are scoped per descriptor type
// Instance 0 of WeaponState != Instance 0 of SensorState
```

**Why:** Instance IDs are descriptor-type-specific, not entity-global.

---

### Network-ELM Troubleshooting

#### Problem: Entities Stuck as Ghost

**Symptoms:**
- Ghost count increases over time
- Entities never become Active

**Cause:** EntityMaster packets not arriving or not being processed.

**Solution:**
```csharp
// 1. Check EntityMasterTranslator is registered and running
kernel.RegisterTranslator(new EntityMasterTranslator(localNodeId, networkIdToEntity, tkbDb));

// 2. Verify DDS topic subscription
Assert.NotNull(_ddsReader.FindTopic("SST.EntityMaster"));

// 3. Check NetworkSpawnerSystem is registered
kernel.RegisterSystem(new NetworkSpawnerSystem(tkbDb, elm, strategy, localNodeId));

// 4. Monitor ghost timeout
var ghostQuery = repo.Query().WithLifecycle(EntityLifecycle.Ghost).Build();
Console.WriteLine($"Ghosts: {ghostQuery.Count()}");
```

#### Problem: Reliable Init Never Completes

**Symptoms:**
- Entities stuck in Constructing state
- No transition to Active

**Cause:** Peer ACKs not arriving or NetworkGatewayModule not configured.

**Solution:**
```csharp
// 1. Verify NetworkGatewayModule registered with correct module ID
var gateway = new NetworkGatewayModule(moduleId: 10, ...);
elm.RegisterModule(moduleId: 10);  // Must match!

// 2. Check topology configuration
var topology = new StaticNetworkTopology(1, new[] { 1, 2, 3 });
var peers = topology.GetExpectedPeers(new DISEntityType());
Assert.Equal(2, peers.Count());  // Should exclude self

// 3. Verify EntityLifecycleStatusTranslator registered
kernel.RegisterTranslator(new EntityLifecycleStatusTranslator(localNodeId, gateway, networkIdToEntity));

// 4. Monitor timeout (should auto-complete after 300 frames)
```

#### Problem: Wrong Node Publishing Multi-Instance Descriptor

**Symptoms:**
- Ownership conflict warnings
- Multiple nodes publishing same weapon instance

**Cause:** Ownership strategy not correctly configured.

**Solution:**
```csharp
// 1. Verify strategy returns correct owner for each instance
var strategy = new MyOwnershipStrategy();
var owner0 = strategy.GetInitialOwner(WEAPON_STATE, tankType, masterNode: 1, instanceId: 0);
var owner1 = strategy.GetInitialOwner(WEAPON_STATE, tankType, masterNode: 1, instanceId: 1);

Assert.Equal(1, owner0);  // Node 1 owns instance 0
Assert.Equal(2, owner1);  // Node 2 owns instance 1

// 2. Verify translator checks ownership per instance
public void ScanAndPublish(ISimulationView view, IDataWriter writer)
{
    foreach (var kvp in weaponStates.Weapons)
    {
        long instanceId = kvp.Key;
        
        // MUST check ownership for THIS instance
        if (!view.OwnsDescriptor(entity, WEAPON_STATE_DESCRIPTOR_ID, instanceId))
            continue;
        
        // ...
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

## Network-ELM API Reference

### Core Components

#### NetworkIdentity (struct)

```csharp
public struct NetworkIdentity
{
    public long Value;  // Globally unique network entity ID
}
```

**Usage:** Required on all networked entities. Maps local Entity to network ID.

---

#### NetworkOwnership (struct)

```csharp
public struct NetworkOwnership
{
    public int LocalNodeId;     // This node's ID
    public int PrimaryOwnerId;  // Who owns EntityMaster (default owner)
}
```

**Usage:** Determines ownership for simple (single-owner) scenarios.

---

#### DescriptorOwnership (class)

```csharp
public class DescriptorOwnership
{
    /// <summary>
    /// Maps (DescriptorTypeId, InstanceId) -> OwnerNodeId
    /// Key format: (TypeId << 32) | InstanceId
    /// </summary>
    public Dictionary<long, int> Map { get; set; } = new();
}
```

**Usage:** Stores partial ownership (descriptor-specific and instance-specific).

**Example:**
```csharp
var ownership = new DescriptorOwnership();

// Node 1 owns weapon instance 0
long key0 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0);
ownership.Map[key0] = 1;

// Node 2 owns weapon instance 1
long key1 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1);
ownership.Map[key1] = 2;
```

---

#### NetworkSpawnRequest (struct)

```csharp
public struct NetworkSpawnRequest
{
    public long NetworkId;
    public DISEntityType DisType;
    public int MasterNodeId;
    public int PrimaryOwnerId;
    public MasterFlags Flags;
    public string TemplateName;
}
```

**Usage:** Transient component added by EntityMasterTranslator, consumed by NetworkSpawnerSystem.

**Lifecycle:** Added → Processed → Removed (same frame)

---

#### PendingNetworkAck (struct)

```csharp
public struct PendingNetworkAck { }  // Tag component
```

**Usage:** Marks entities requiring reliable initialization (waiting for peer ACKs).

**Added by:** NetworkSpawnerSystem (if MasterFlags.ReliableInit set)  
**Removed by:** NetworkGatewayModule (after peers confirm or timeout)

---

#### ForceNetworkPublish (struct)

```csharp
public struct ForceNetworkPublish { }  // Tag component
```

**Usage:** Forces immediate egress (bypass periodic publishing).

**Use cases:**
- Ownership acquired (send confirmation)
- Critical state change (death, spawn)
- Manual trigger for important updates

**Lifecycle:** Added → Removed (same frame), triggers immediate publish

---

#### WeaponStates (class)

```csharp
public class WeaponStates
{
    public Dictionary<long, WeaponState> Weapons { get; set; } = new();
}

public struct WeaponState
{
    public float AzimuthAngle;
    public float ElevationAngle;
    public int AmmoCount;
    public WeaponStatus Status;
}
```

**Usage:** Stores multiple weapon instances per entity.

**Example:**
```csharp
var weaponStates = new WeaponStates();
weaponStates.Weapons[0] = new WeaponState { AmmoCount = 100, Status = WeaponStatus.Ready };
weaponStates.Weapons[1] = new WeaponState { AmmoCount = 50, Status = WeaponStatus.Reloading };

repo.AddManagedComponent(tank, weaponStates);
```

---

### Events

#### DescriptorAuthorityChanged (struct)

```csharp
[EventId(9010)]
public struct DescriptorAuthorityChanged
{
    public Entity Entity;
    public long DescriptorTypeId;
    public long InstanceId;
    public int PreviousOwner;
    public int NewOwner;
    public bool IsNowOwner;
}
```

**Published by:** OwnershipUpdateTranslator when ownership transfers

**Usage:**
```csharp
foreach (var evt in view.ConsumeEvents<DescriptorAuthorityChanged>())
{
    if (evt.IsNowOwner && evt.DescriptorTypeId == WEAPON_STATE_DESCRIPTOR_ID)
    {
        Console.WriteLine($"We now control weapon {evt.InstanceId}");
    }
}
```

---

### Network Messages (DDS Descriptors)

#### EntityMasterDescriptor

```csharp
public class EntityMasterDescriptor
{
    public long EntityId { get; set; }
    public int OwnerId { get; set; }
    public DISEntityType Type { get; set; }
    public string Name { get; set; }
    public MasterFlags Flags { get; set; } = MasterFlags.None;
}

[Flags]
public enum MasterFlags
{
    None = 0,
    ReliableInit = 1 << 0
}
```

**Topic:** `SST.EntityMaster`

**Usage:** Creates or identifies entities on the network. Primary owner publishes, all nodes receive.

---

#### EntityLifecycleStatusDescriptor

```csharp
public class EntityLifecycleStatusDescriptor
{
    public long EntityId { get; set; }
    public int NodeId { get; set; }
    public EntityLifecycle State { get; set; }
    public long Timestamp { get; set; }
}
```

**Topic:** `SST.EntityLifecycleStatus`

**Usage:** Peer acknowledgment protocol for reliable initialization.

---

#### WeaponStateDescriptor

```csharp
public class WeaponStateDescriptor
{
    public long EntityId { get; set; }
    public long InstanceId { get; set; }  // Turret index (0, 1, 2...)
    public float AzimuthAngle { get; set; }
    public float ElevationAngle { get; set; }
    public int AmmoCount { get; set; }
    public WeaponStatus Status { get; set; }
}
```

**Topic:** `SST.WeaponState`

**Usage:** Multi-instance weapon state. Each instance published independently.

---

### Systems

#### NetworkSpawnerSystem

**Phase:** Input (after translators)

**Purpose:** Bridge network translators and ELM.

**Constructor:**
```csharp
public NetworkSpawnerSystem(
    ITkbDatabase tkbDatabase,
    EntityLifecycleModule elm,
    IOwnershipDistributionStrategy ownershipStrategy,
    int localNodeId
)
```

**Responsibilities:**
- Process NetworkSpawnRequest components
- Apply TKB templates (preserveExisting for Ghosts)
- Determine descriptor ownership (including multi-instance)
- Call ELM.BeginConstruction()

---

#### NetworkEgressSystem

**Phase:** Export (after simulation)

**Purpose:** Publish owned descriptors to network.

**Constructor:**
```csharp
public NetworkEgressSystem(
    IDescriptorTranslator[] translators,
    IDataWriter[] writers
)
```

**Responsibilities:**
- Process ForceNetworkPublish components
- Call ScanAndPublish on all translators
- Publish owned descriptors to DDS

---

### Modules

#### NetworkGatewayModule

**Purpose:** Participate in ELM for reliable initialization barrier.

**Constructor:**
```csharp
public NetworkGatewayModule(
    int moduleId,              // Module ID for ELM registration
    int localNodeId,           // This node's ID
    INetworkTopology topology, // Peer discovery
    EntityLifecycleModule elm  // ELM reference
)
```

**Responsibilities:**
- Intercept ConstructionOrder events
- For entities with PendingNetworkAck: wait for peer ACKs
- Receive EntityLifecycleStatus messages from peers
- Send ConstructionAck when all peers confirm or timeout
- Handle DestructionOrder cleanup

**Module ID:** Must be registered with ELM and unique.

---

### Interfaces

#### ITkbDatabase

```csharp
public interface ITkbDatabase
{
    TkbTemplate? GetTemplateByEntityType(DISEntityType entityType);
    TkbTemplate? GetTemplateByName(string templateName);
}
```

**Purpose:** Provide TKB templates for entity types.

**Implementation:** Application-specific (config files, hardcoded, database)

---

#### IOwnershipDistributionStrategy

```csharp
public interface IOwnershipDistributionStrategy
{
    int? GetInitialOwner(
        long descriptorTypeId,
        DISEntityType entityType,
        int masterNodeId,
        long instanceId
    );
}
```

**Purpose:** Determine descriptor ownership for partial ownership scenarios.

**Return Value:**
- `int` = Specific node owns this descriptor/instance
- `null` = Use master owner (default)

---

#### INetworkTopology

```csharp
public interface INetworkTopology
{
    int LocalNodeId { get; }
    IEnumerable<int> GetExpectedPeers(DISEntityType entityType);
}
```

**Purpose:** Abstract peer discovery.

**Implementations:**
- `StaticNetworkTopology` - Hardcoded peer list
- `DynamicNetworkTopology` - Service discovery based

---

### Utility Extensions

#### OwnershipExtensions

```csharp
public static class OwnershipExtensions
{
    // Pack composite key
    public static long PackKey(long descriptorTypeId, long instanceId);
    
    // Unpack composite key
    public static (long typeId, long instanceId) UnpackKey(long packedKey);
    
    // Check ownership (single instance)
    public static bool OwnsDescriptor(this ISimulationView view, Entity entity, long descriptorTypeId);
    
    // Check ownership (specific instance)
    public static bool OwnsDescriptor(this ISimulationView view, Entity entity, 
                                       long descriptorTypeId, long instanceId);
}
```

**Usage:**
```csharp
// Check if we own weapon instance 1
if (view.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, instanceId: 1))
{
    // Publish weapon 1 updates
}
```

---

### Constants

#### NetworkConstants

```csharp
public static class NetworkConstants
{
    public const long ENTITY_MASTER_DESCRIPTOR_ID = 0;
    public const long ENTITY_STATE_DESCRIPTOR_ID = 1;
    public const long WEAPON_STATE_DESCRIPTOR_ID = 2;
    public const long ENTITY_LIFECYCLE_STATUS_ID = 900;
    public const long OWNERSHIP_UPDATE_ID = 901;
    
    public const int GHOST_TIMEOUT_FRAMES = 300;        // 5 seconds @ 60Hz
    public const int RELIABLE_INIT_TIMEOUT_FRAMES = 300; // 5 seconds @ 60Hz
}
```

**Usage:** Standard descriptor type IDs and timeout values.

---

## Migration Guide: Adding Network-ELM to Existing Project

### Step 1: Update Fdp.Kernel (if using submodule)

```bash
cd FDP
git pull origin main
# Ensure EntityLifecycle.Ghost (value 4) is present
cd ..
```

### Step 2: Add New Components

```csharp
// In your component registration
repo.RegisterComponent<NetworkIdentity>();
repo.RegisterComponent<NetworkOwnership>();
repo.RegisterComponent<NetworkSpawnRequest>();
repo.RegisterComponent<PendingNetworkAck>();
repo.RegisterComponent<ForceNetworkPublish>();
```

### Step 3: Replace Old Network Module

**Before (Legacy):**
```csharp
var networkModule = new OldNetworkGateway(localNodeId);
kernel.RegisterModule(networkModule);
```

**After (Network-ELM):**
```csharp
// 1. Create ELM
var elm = new EntityLifecycleModule(new[] { 10 });
kernel.RegisterModule(elm);

// 2. Create topology
var topology = new StaticNetworkTopology(localNodeId, allNodes);

// 3. Create gateway
var gateway = new NetworkGatewayModule(10, localNodeId, topology, elm);
gateway.Initialize(null);
kernel.RegisterModule(gateway);

// 4. Create spawner
var spawner = new NetworkSpawnerSystem(tkbDb, elm, strategy, localNodeId);
kernel.RegisterSystem(spawner);

// 5. Update translators (add networkIdToEntity tracking)
var stateTranslator = new EntityStateTranslator(localNodeId, networkIdToEntity);
// ... other translators
```

### Step 4: Update Queries (Exclude Ghosts)

**Before:**
```csharp
var query = repo.Query().With<Position>().Build();
// Might accidentally include Ghost entities
```

**After:**
```csharp
// Queries now automatically exclude Ghost entities (default behavior)
var query = repo.Query().With<Position>().Build();
// Only Active entities returned ✅

// If you need Ghosts (debugging):
var allQuery = repo.Query().With<Position>().IncludeAll().Build();
```

### Step 5: Enable Features Incrementally

**Phase 1:** Basic Network-ELM (required)
- EntityLifecycleModule
- NetworkSpawnerSystem
- EntityMasterTranslator updates

**Phase 2:** Reliable Init (optional)
- NetworkGatewayModule
- EntityLifecycleStatusTranslator
- Set MasterFlags.ReliableInit on critical entities

**Phase 3:** Multi-Instance (optional)
- WeaponStateTranslator with instance support
- WeaponStates component
- Multi-instance ownership strategy

### Step 6: Test Migration

**Test 1: Ghost Creation**
```csharp
[Fact]
public void Migration_EntityStateBeforeMaster_CreatesGhost()
{
    // Send EntityState (no EntityMaster)
    var stateDesc = new EntityStateDescriptor { EntityId = 100, ... };
    stateTranslator.PollIngress(reader, cmd, repo);
    cmd.Playback();
    
    // Verify Ghost created
    var entity = networkIdToEntity[100];
    Assert.Equal(EntityLifecycle.Ghost, repo.GetLifecycleState(entity));
    
    // Send EntityMaster
    var masterDesc = new EntityMasterDescriptor { EntityId = 100, ... };
    masterTranslator.PollIngress(reader, cmd, repo);
    cmd.Playback();
    
    // Verify promotion to Constructing
    Assert.Equal(EntityLifecycle.Constructing, repo.GetLifecycleState(entity));
}
```

**Test 2: Reliable Init**
```csharp
[Fact]
public void Migration_ReliableInit_WaitsForPeers()
{
    // Create entity with ReliableInit flag
    var masterDesc = new EntityMasterDescriptor
    {
        EntityId = 200,
        Flags = MasterFlags.ReliableInit
    };
    
    // Process ingress
    masterTranslator.PollIngress(reader, cmd, repo);
    spawner.Execute(repo, 0);
    cmd.Playback();
    
    // Verify: PendingNetworkAck present
    var entity = networkIdToEntity[200];
    Assert.True(repo.HasComponent<PendingNetworkAck>(entity));
    
    // Gateway should wait for peers
    gateway.Execute(repo, 0);
    
    // Verify: Still Constructing (not Active yet)
    Assert.Equal(EntityLifecycle.Constructing, repo.GetLifecycleState(entity));
}
```

---

## Performance Tuning Guide

### Optimizing Egress Performance

**Problem:** Egress scanning all entities is O(n), becomes bottleneck at 5,000+ entities.

**Solution 1: Spatial Interest Management**
```csharp
// Only publish entities near subscribers
public class SpatialEgressSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Query entities in area-of-interest
        var nearbyQuery = view.Query()
            .With<Position>()
            .WithLifecycle(EntityLifecycle.Active)
            .Build()
            .Where(e => IsNearSubscribers(e));  // Custom filter
        
        foreach (var entity in nearbyQuery)
        {
            // Publish only nearby entities
            _stateTranslator.PublishEntity(entity, view, _writer);
        }
    }
}
```

**Solution 2: Frequency Throttling**
```csharp
// Reduce update rate for distant entities
public class DistanceBasedEgressSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        foreach (var entity in _query)
        {
            float distance = GetDistanceToSubscriber(entity);
            int frequency = DetermineUpdateFrequency(distance);
            
            // Skip if not time to update
            if (frameCount % frequency != 0)
                continue;
            
            _stateTranslator.PublishEntity(entity, view, _writer);
        }
    }
    
    private int DetermineUpdateFrequency(float distance)
    {
        if (distance < 100) return 1;   // 60Hz (every frame)
        if (distance < 500) return 2;   // 30Hz
        if (distance < 1000) return 4;  // 15Hz
        return 10;                       // 6Hz for very distant
    }
}
```

### Optimizing Ingress Performance

**Batch Processing:** Already implemented in WeaponStateTranslator.

**Additional Optimization:**
```csharp
// Process ingress in chunks (prevent frame spikes)
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    const int MAX_PER_FRAME = 100;
    int processed = 0;
    
    foreach (var sample in reader.TakeSamples())
    {
        if (++processed > MAX_PER_FRAME)
        {
            // Defer remaining samples to next frame
            _pendingSamples.Add(sample);
            break;
        }
        
        // Process sample...
    }
}
```

### Monitoring Network Health

```csharp
// Track network statistics
public class NetworkMonitorSystem
{
    private int _frameCount = 0;
    private int _ghostCount = 0;
    private int _activeCount = 0;
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        if (++_frameCount % 60 == 0)  // Every second @ 60Hz
        {
            _ghostCount = view.Query().WithLifecycle(EntityLifecycle.Ghost).Build().Count();
            _activeCount = view.Query().WithLifecycle(EntityLifecycle.Active).Build().Count();
            
            Console.WriteLine($"[Network] Active: {_activeCount}, Ghosts: {_ghostCount}");
            
            // Alert if ghost count is high
            if (_ghostCount > 50)
            {
                Console.WriteLine($"WARNING: High ghost count ({_ghostCount}). Check EntityMaster publication.");
            }
        }
    }
}
```

---

## Summary: Network-ELM Feature Matrix

| Feature | Batch | Status | Use Case |
|---------|-------|--------|----------|
| **Ghost Entity Protocol** | 12-13 | ✅ Production | Out-of-order packet handling |
| **Distributed Construction** | 12-13 | ✅ Production | Cross-node entity initialization |
| **Reliable Initialization** | 14-14.1 | ✅ Production | Critical entities (players, vehicles) |
| **Fast Initialization** | 14-14.1 | ✅ Production | Transient entities (VFX, projectiles) |
| **Dynamic Ownership Events** | 13 | ✅ Production | Reactive systems (ownership handoff) |
| **Multi-Instance Descriptors** | 15 | ✅ Production | Multi-turret tanks, multi-sensor platforms |
| **Performance Validated** | 15 | ✅ Tested | 1000+ entities, packet loss, stress tests |

### Production Readiness Checklist

Before deploying Network-ELM to production:

- [ ] All nodes running same code version
- [ ] Network topology configured correctly
- [ ] TKB templates defined for all entity types
- [ ] Ownership strategy tested
- [ ] Reliable init enabled for critical entities only
- [ ] Ghost timeout appropriate for network latency
- [ ] Monitoring system in place (ghost count, ACK timeouts)
- [ ] Performance validated with expected entity count
- [ ] Failover tested (node crash scenarios)

### Further Reading

- **Implementation Spec:** `docs/ModuleHost-network-ELM-implementation-spec.md` - Detailed architecture
- **Analysis Summary:** `docs/ModuleHost-network-ELM-analysis-summary.md` - Design decisions
- **Test Examples:** `ModuleHost.Core.Tests/Network/` - Unit and integration tests
- **Benchmarks:** `ModuleHost.Benchmarks/NetworkPerformanceBenchmarks.cs` - Performance baselines

---
