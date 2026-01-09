# ModuleHost Network-ELM Integration - Implementation Specification

**Version:** 1.0  
**Date:** 2026-01-09  
**Status:** Final Design - Ready for Implementation  
**Related Batches:** BATCH-07 (Network Gateway), BATCH-07.1 (Partial Ownership)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Architectural Overview](#architectural-overview)
3. [Design Decisions](#design-decisions)
4. [Component Specifications](#component-specifications)
5. [System Workflows](#system-workflows)
6. [Implementation Details](#implementation-details)
7. [Testing Strategy](#testing-strategy)
8. [Known Limitations](#known-limitations)

---

## Executive Summary

This document specifies the integration between the **Network Gateway** (DDS-based entity replication) and the **Entity Lifecycle Manager (ELM)** (distributed construction/destruction coordination) in the ModuleHost framework.

### Key Features

- **Ghost Entity Protocol**: Allows out-of-order packet arrival (EntityState before EntityMaster)
- **Distributed Construction**: Ensures all nodes complete entity initialization before simulation
- **Partial Ownership**: Supports different nodes owning different descriptors on the same entity
- **Reliable/Fast Modes**: Configurable per-entity-type initialization guarantees
- **Dynamic Ownership Transfer**: Runtime handoff of descriptor authority with event notification

### Architecture Principles

1. **ECS Integration**: Network state stored as FDP components, not external buffers
2. **Main Thread Safety**: Network ingress runs synchronously for immediate ID mapping
3. **Event-Driven Coordination**: ELM and ownership changes trigger FDP events
4. **Interface Abstraction**: Strategy patterns for topology and ownership distribution

---

## Architectural Overview

### System Phases

```
┌─────────────────┐
│  INPUT PHASE    │
├─────────────────┤
│ 1. NetworkIngress    ← Read DDS, create Ghosts
│ 2. NetworkSpawner    ← Promote Ghosts, apply TKB
└─────────────────┘
         ↓
┌─────────────────┐
│ BEFORESYNC      │
├─────────────────┤
│ 3. LifecycleSystem   ← Process ACKs, activate entities
└─────────────────┘
         ↓
┌─────────────────┐
│ SIMULATION      │
├─────────────────┤
│ 4. Physics/AI/etc    ← Active entities only
└─────────────────┘
         ↓
┌─────────────────┐
│ EXPORT PHASE    │
├─────────────────┤
│ 5. NetworkEgress     ← Publish owned descriptors
└─────────────────┘
```

### Entity Lifecycle State Machine

```
┌──────────┐
│  GHOST   │ ←─── EntityState arrives before Master
└─────┬────┘
      │ EntityMaster arrives
      ↓
┌──────────────┐
│ CONSTRUCTING │
└──────┬───────┘
       │ All modules ACK
       ↓
┌──────────┐
│  ACTIVE  │
└──────┬───┘
       │ Destruction triggered
       ↓
┌──────────┐
│ TEARDOWN │
└──────────┘
```

### Component Relationships

```
Entity (NetworkID=123)
├─ NetworkIdentity { Value: 123 }
├─ NetworkOwnership { PrimaryOwnerId: 1, LocalNodeId: 2 }
├─ DescriptorOwnership { Map: { [EntityState]: 1, [Weapon]: 2 } }
├─ Position, Velocity (from EntityState)
└─ WeaponAmmo (from TKB Template)
```

---

## Design Decisions

### Decision Matrix

| # | Question | Decision | Rationale |
|---|----------|----------|-----------|
| 1 | Entity Creation Protocol | **Ghost State** (Option B) | ECS-native buffering, simpler than packet queues |
| 2 | NetworkGateway Module ID | **Configurable** | Supports multi-gateway scenarios |
| 3 | Peer Discovery | **INetworkTopology Interface** | Decouples static config from dynamic discovery |
| 4 | Ownership Assignment | **IOwnershipDistributionStrategy** | Logic-based, deterministic role calculation |
| 5 | Reliable Init Granularity | **Per-Entity-Type** | Balances performance (VFX fast) vs reliability (Tanks) |
| 6 | ELM TypeId | **TKB Template Name Hash** | Allows module filtering via TypeId |
| 7 | Descriptor Instance ID | **Scoped per Descriptor Type** | Natural indexing (Turret 0, Turret 1) |
| 8 | Composite Key Format | **Packed long** (TypeId << 32 \| InstanceId) | Avoids GC, 32-bit space sufficient |
| 9 | TKB Template Application | **Preserve Existing Components** | Keeps network state (Position from Ghost) |
| 10 | Ghost Timeout | **300 frames (5 sec @ 60Hz)** | Prevents memory leaks from lost packets |
| 11 | Construction Failure | **Master Publishes Disposal** | Ensures cluster convergence |
| 12 | Ownership Update Timing | **Immediate** (all lifecycle states) | Correctness over latency |

### Critical Constraints

1. **Main Thread Requirement**: NetworkGateway **MUST** run synchronously (not async module)
   - Reason: Direct `EntityRepository` access needed for immediate entity ID retrieval
   - Impact: Network ingress blocks simulation (acceptable <1ms overhead)

2. **Command Buffer Mixing**: Translators use **direct repo** for Create/Lifecycle, **CMD** for components
   - Reason: `cmd.CreateEntity()` returns deferred ID, breaks immediate mapping
   - Safety: Main thread execution prevents race conditions

3. **Ghost Visibility**: Ghosts are **NOT visible to standard queries** (use `.IncludeAll()`)
   - Reason: Ghosts lack type information/TKB components, would confuse game logic
   - Exception: Network systems explicitly query Ghosts for timeout checks

---

## Component Specifications

### Core Components

#### `NetworkOwnership` (Unmanaged)

```csharp
public struct NetworkOwnership
{
    /// <summary>Owner of EntityMaster descriptor (entity lifecycle)</summary>
    public int PrimaryOwnerId;
    
    /// <summary>Local node ID for fast ownership checks</summary>
    public int LocalNodeId;
}
```

**Usage:**
- Added by Network Gateway during entity creation
- Updated by OwnershipUpdateTranslator for EntityMaster transfers
- Queryable (unmanaged struct)

#### `DescriptorOwnership` (Managed)

```csharp
public class DescriptorOwnership
{
    /// <summary>
    /// Maps (DescriptorTypeId, InstanceId) -> OwnerNodeId
    /// Key format: (TypeId << 32) | InstanceId
    /// </summary>
    public Dictionary<long, int> Map { get; set; }
}
```

**Key Packing:**
```csharp
long key = (descriptorTypeId << 32) | (uint)instanceId;
// TypeId range: 0 to 2^31 (2 billion)
// InstanceId range: 0 to 2^32 (4 billion)
```

**Fallback Logic:**
- If descriptor not in map → use `NetworkOwnership.PrimaryOwnerId`
- Only populate map for non-default ownership (memory efficiency)

#### `NetworkSpawnRequest` (Transient)

```csharp
public struct NetworkSpawnRequest
{
    public DISEntityType DisType;      // DIS entity type from EntityMaster
    public int PrimaryOwnerId;          // Master node
    public MasterFlags Flags;           // ReliableInit, etc.
}
```

**Lifecycle:**
1. Added by `EntityMasterTranslator.PollIngress()`
2. Consumed by `NetworkSpawnerSystem.Execute()` (same frame)
3. Removed after processing

#### `PendingNetworkAck` (Transient Tag)

```csharp
public struct PendingNetworkAck { }
```

**Lifecycle:**
1. Added by `NetworkSpawnerSystem` if entity has `MasterFlags.ReliableInit`
2. Removed by `NetworkEgressSystem` after publishing `EntityLifecycleStatusDescriptor`

### Network Events

#### `DescriptorAuthorityChanged`

```csharp
[EventId(9010)]
public struct DescriptorAuthorityChanged
{
    public Entity Entity;
    public long DescriptorTypeId;
    public bool IsNowOwner;      // true = acquired, false = lost
    public int NewOwnerId;       // new owner node ID
}
```

**Usage Example (Weapon System):**
```csharp
foreach (var change in view.ConsumeEvents<DescriptorAuthorityChanged>())
{
    if (change.DescriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
    {
        if (change.IsNowOwner)
        {
            // Take control: clear interpolation buffers, enable input
            _weaponController.EnableLocalControl(change.Entity);
        }
        else
        {
            // Lost control: switch to remote interpolation mode
            _weaponController.DisableLocalControl(change.Entity);
        }
    }
}
```

---

## System Workflows

### Workflow 1: Master-First Creation (Ideal Path)

```
Node A (Master)                  Node B (Replica)
─────────────────                ────────────────
1. TKB spawns Tank
2. ELM.BeginConstruction()
3. Modules ACK
4. Entity → Active
5. Publish EntityMaster ──────→ 6. Receive EntityMaster
                                 7. CreateEntity()
                                 8. Set Lifecycle = Constructing
                                 9. Apply TKB Template
                                 10. ELM.BeginConstruction()
                                 11. Modules ACK
6. Publish EntityState ───────→ 12. Entity → Active
                                 13. Receive EntityState
                                 14. Update Position/Velocity
```

**Frame Timing:**
- Node A: Activation frame 100
- Node B: Activation frame ~105 (5 frames latency)

### Workflow 2: State-First Creation (Ghost Path)

```
Node A (Master)                  Node B (Replica)
─────────────────                ────────────────
1. [Master packet delayed]
2. Publish EntityState ───────→ 3. Receive EntityState
                                 4. CreateEntity() 
                                 5. Set Lifecycle = GHOST ★
                                 6. Apply Position/Velocity
                                 
3. Publish EntityMaster ──────→ 7. Receive EntityMaster
                                 8. GHOST → CONSTRUCTING ★
                                 9. Apply TKB Template (preserve existing)
                                 10. ELM.BeginConstruction()
                                 11. Modules ACK
                                 12. Entity → Active
```

**Ghost Timeout:**
- If step 7 never arrives: Ghost destroyed after 300 frames (5 seconds)
- Prevents memory leaks from partial packet loss

### Workflow 3: Reliable Initialization

```
Node A (Master)                  Node B, C (Replicas)
─────────────────                ────────────────
1. CreateEntity()
2. Set DisType.Flags = ReliableInit
3. Publish EntityMaster ──────→ 4. Receive Master
                                 5. Local construction
                                 6. Modules ACK
4. Wait for network ACKs         7. Entity → Active
                                 8. Publish EntityLifecycleStatus ───→
5. Receive Status from B         
6. Receive Status from C         
7. All ACKs received ★
8. NetworkGateway sends ConstructionAck to local ELM
9. Entity → Active
```

**Barrier Logic:**
- Master node's `NetworkGatewayModule` participates in ELM as a regular module
- It withholds its `ConstructionAck` until network peers confirm
- Timeout: Uses ELM's standard timeout (300 frames)

### Workflow 4: Dynamic Ownership Transfer

```
Node 1 (Current Owner)           Node 2 (New Owner)         DDS Network
──────────────────────           ──────────────             ───────────
1. User action triggers transfer
2. Publish OwnershipUpdate ───────────────→ 3. Receive Update
   {                                           4. Update DescriptorOwnership.Map
     EntityId: 123,                            5. Fire DescriptorAuthorityChanged
     DescrTypeId: WEAPON,                         { IsNowOwner: true }
     NewOwner: 2                               6. WeaponSystem responds:
   }                                               - Clear interpolation
                                                   - Enable physics
                                 ←───────────  7. Publish WeaponState (confirm)
8. Receive WeaponState from Node 2
9. Stop publishing (ownership transferred)
```

**Confirmation Write:**
- SST Protocol requires "new owner writes to confirm"
- Implemented via `ForceNetworkPublish` component
- Egress system publishes immediately (even if data unchanged)

---

## Implementation Details

### File Structure

```
ModuleHost.Core/
├── Network/
│   ├── NetworkConstants.cs          (NEW)
│   ├── NetworkComponents.cs         (UPDATE)
│   ├── EntityMasterDescriptor.cs    (UPDATE - add Flags)
│   ├── Messages/
│   │   ├── EntityStateDescriptor.cs             (NEW)
│   │   ├── EntityLifecycleStatusDescriptor.cs   (NEW)
│   │   └── OwnershipUpdate.cs                   (EXISTS)
│   ├── Interfaces/
│   │   ├── INetworkTopology.cs                  (NEW)
│   │   ├── IOwnershipDistributionStrategy.cs    (NEW)
│   │   └── ITkbDatabase.cs                      (NEW - abstraction)
│   ├── Systems/
│   │   ├── NetworkIngressSystem.cs              (NEW)
│   │   ├── NetworkSpawnerSystem.cs              (NEW)
│   │   └── NetworkEgressSystem.cs               (NEW)
│   ├── Translators/
│   │   ├── EntityStateTranslator.cs             (REFACTOR)
│   │   ├── EntityMasterTranslator.cs            (REFACTOR)
│   │   └── OwnershipUpdateTranslator.cs         (UPDATE)
│   └── NetworkGatewayModule.cs                  (REFACTOR)
├── ELM/
│   ├── EntityLifecycleModule.cs     (EXISTS - no changes)
│   ├── LifecycleEvents.cs           (EXISTS)
│   └── LifecycleSystem.cs           (EXISTS)
└── ...

Fdp.Kernel/
├── EntityLifecycle.cs               (UPDATE - add Ghost)
└── Tkb/
    ├── TkbTemplate.cs               (UPDATE - add preserveExisting)
    └── ITkbDatabase.cs              (NEW - interface extraction)
```

### Key Implementation Notes

#### 1. Direct Repository Access Pattern

**Context:** Network translators need immediate entity IDs for mapping.

**Problem:** `IEntityCommandBuffer.CreateEntity()` returns placeholder IDs (negative values) that resolve next frame.

**Solution:** Use direct `EntityRepository` access in synchronous network ingress.

```csharp
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    // Cast required - safe because NetworkGateway is Synchronous
    var repo = view as EntityRepository 
        ?? throw new InvalidOperationException("Network requires EntityRepository access");
    
    // Direct mutation for immediate ID
    var entity = repo.CreateEntity();
    repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
    
    // Map immediately
    _networkIdToEntity[desc.EntityId] = entity;
    
    // Components can use CMD or direct repo (both work)
    repo.AddComponent(entity, new NetworkIdentity { Value = desc.EntityId });
}
```

**Safety Guarantee:** `NetworkGatewayModule.Policy = ExecutionPolicy.Synchronous()` ensures single-threaded execution.

#### 2. TKB Template Preservation

**Challenge:** When promoting Ghost → Constructing, TKB template may overwrite network state.

**Example:**
- Ghost has `Position { X=100, Y=200 }` from `EntityState`
- TKB Template defines `Position { X=0, Y=0 }` (spawn point)
- Applying template would teleport entity to origin

**Solution:** Extend `TkbTemplate.ApplyTo()` with `preserveExisting` flag:

```csharp
public void ApplyTo(EntityRepository repo, Entity entity, bool preserveExisting = false)
{
    foreach (var applicator in _applicators)
    {
        applicator(repo, entity, preserveExisting);
    }
}

// Applicator logic
_applicators.Add((repo, entity, preserve) =>
{
    if (preserve && repo.HasComponent<Position>(entity)) 
        return; // Skip - keep network data
    
    repo.AddComponent(entity, new Position { X=0, Y=0 });
});
```

#### 3. Composite Key Helper Usage

```csharp
// Packing (Storage)
var descOwnership = new DescriptorOwnership();
long key = OwnershipExtensions.PackKey(
    descriptorTypeId: NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
    instanceId: 0  // Main weapon (index 0)
);
descOwnership.Map[key] = nodeId;

// Unpacking (Diagnostics)
foreach (var kvp in descOwnership.Map)
{
    var (typeId, instanceId) = OwnershipExtensions.UnpackKey(kvp.Key);
    Console.WriteLine($"Descriptor {typeId} Instance {instanceId} owned by {kvp.Value}");
}

// Lookup (Extension uses packing internally)
bool owned = view.OwnsDescriptor(entity, typeId, instanceId);
```

---

## Testing Strategy

### Unit Tests (Required)

#### Test Suite 1: Ghost Entity Lifecycle

```csharp
[Test]
public void EntityState_BeforeMaster_CreatesGhost()
{
    // Arrange
    var translator = new EntityStateTranslator(localNodeId: 1, _networkMap);
    var stateDesc = new EntityStateDescriptor { EntityId = 123, Location = new Vector3(10, 0, 0) };
    
    // Act
    translator.PollIngress(_mockReader, _cmd, _repo);
    
    // Assert
    var entity = _networkMap[123];
    Assert.Equal(EntityLifecycle.Ghost, _repo.GetLifecycleState(entity));
    Assert.True(_repo.HasComponent<Position>(entity));
    Assert.Equal(10, _repo.GetComponentRO<Position>(entity).Value.X);
}

[Test]
public void EntityMaster_AfterState_PromotesGhost()
{
    // Arrange - Ghost already exists
    var ghostEntity = CreateGhostEntity(networkId: 123);
    var masterDesc = new EntityMasterDescriptor { EntityId = 123, Type = DisType.Tank };
    
    // Act
    _masterTranslator.PollIngress(_mockReader, _cmd, _repo);
    _spawnerSystem.Execute(_repo, 0.016f);
    
    // Assert
    Assert.Equal(EntityLifecycle.Constructing, _repo.GetLifecycleState(ghostEntity));
    Assert.True(_repo.HasComponent<TankTurret>(ghostEntity)); // From TKB
    Assert.Equal(10, _repo.GetComponentRO<Position>(ghostEntity).Value.X); // Preserved from Ghost
}
```

#### Test Suite 2: Ownership Management

```csharp
[Test]
public void OwnershipUpdate_FiresAuthorityChangedEvent()
{
    // Arrange
    var entity = CreateNetworkedEntity(ownerId: 1, localNodeId: 2);
    var update = new OwnershipUpdate 
    { 
        EntityId = GetNetworkId(entity),
        DescrTypeId = NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
        NewOwner = 2  // Transfer to us
    };
    
    // Act
    _ownershipTranslator.PollIngress(_mockReader, _cmd, _repo);
    var events = _repo.ConsumeEvents<DescriptorAuthorityChanged>();
    
    // Assert
    var evt = events.Single();
    Assert.Equal(entity, evt.Entity);
    Assert.True(evt.IsNowOwner);
    Assert.Equal(2, evt.NewOwnerId);
}

[Test]
public void PartialOwnership_UsesStrategyPattern()
{
    // Arrange
    var mockStrategy = new Mock<IOwnershipDistributionStrategy>();
    mockStrategy.Setup(s => s.GetInitialOwner(
        NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
        DisType.Tank,
        masterNodeId: 1,
        instanceId: 0
    )).Returns(2); // Node 2 owns weapons
    
    var spawner = new NetworkSpawnerSystem(_tkb, _elm, mockStrategy.Object, localNodeId: 2);
    
    // Act
    spawner.Execute(_repo, 0.016f);
    
    // Assert
    Assert.True(_repo.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID));
    Assert.False(_repo.OwnsDescriptor(entity, NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID));
}
```

#### Test Suite 3: Reliable Initialization

```csharp
[Test]
public void ReliableInit_WaitsForNetworkACKs()
{
    // Arrange
    var topology = new Mock<INetworkTopology>();
    topology.Setup(t => t.GetExpectedPeers(DisType.Tank)).Returns(new[] { 2, 3 });
    
    var masterDesc = new EntityMasterDescriptor 
    { 
        EntityId = 123, 
        Flags = MasterFlags.ReliableInit 
    };
    
    // Act - Create entity (Master node)
    ProcessMasterDescriptor(masterDesc);
    
    // Assert - Entity still Constructing (NetworkGateway hasn't ACKed)
    Assert.Equal(EntityLifecycle.Constructing, GetLifecycleState(entity));
    
    // Act - Receive ACKs from Node 2 and 3
    ProcessLifecycleStatus(new EntityLifecycleStatusDescriptor { EntityId = 123, NodeId = 2 });
    ProcessLifecycleStatus(new EntityLifecycleStatusDescriptor { EntityId = 123, NodeId = 3 });
    
    // Assert - Now Active
    Assert.Equal(EntityLifecycle.Active, GetLifecycleState(entity));
}
```

### Integration Tests (Must-Have)

1. **Multi-Node Simulation**: 2-node test with real DDS loopback
2. **Out-of-Order Stress**: Randomly shuffle EntityMaster/State arrival
3. **Ownership Handoff**: Verify smooth transfer without dropped updates
4. **Ghost Timeout**: Confirm cleanup after 300 frames

### System Tests (Nice-to-Have)

1. **Packet Loss Simulation**: Drop 10% of EntityMaster packets
2. **Node Disconnect**: Simulate network partition during construction
3. **Performance Benchmark**: 1000 entities with 100Hz update rate

---

## Known Limitations

### 1. Single-Threaded Network Ingress

**Limitation:** Network Gateway must run on main thread (synchronous policy).

**Impact:**
- Maximum ingress throughput: ~1000 entities/frame @ 60 Hz
- DDS reader latency blocks simulation

**Mitigation:**
- Use DDS QoS (Reliable, Keep Last 10) to limit buffer size
- Consider read throttling for large battles (process 100 entities/frame)

### 2. No Multi-Instance Descriptor Support (v1.0)

**Limitation:** While the composite key system supports instance IDs, no translators currently use `instanceId != 0`.

**Impact:** Cannot replicate entities with multiple turrets/weapons via separate descriptors.

**Workaround:** Use single descriptor with array of states (e.g., `TurretStates[0]`, `TurretStates[1]`).

**Future Work:** Implement `TurretDescriptorTranslator` with instance awareness.

### 3. No Cross-Network ELM Coordination (Unreliable Mode)

**Limitation:** In Fast mode, each node activates entities independently without network sync.

**Impact:** Node A may simulate entity while Node B is still loading resources (1-2 frame desync).

**Acceptable Because:** Fast mode is for non-critical entities (VFX, debris).

### 4. Ghost Timeout is Fixed

**Limitation:** 300-frame timeout is hardcoded in `NetworkSpawnerSystem`.

**Impact:** Cannot configure per-entity-type or network condition.

**Future Work:** Make timeout configurable via `NetworkPolicy`.

---

## Appendix A: Constants Reference

```csharp
// NetworkConstants.cs
public static class NetworkConstants
{
    // DDS Descriptor Type IDs
    public const long ENTITY_MASTER_DESCRIPTOR_ID = 0;
    public const long ENTITY_STATE_DESCRIPTOR_ID = 1;
    public const long WEAPON_STATE_DESCRIPTOR_ID = 2;
    
    // System message IDs
    public const long ENTITY_LIFECYCLE_STATUS_ID = 900;
    public const long OWNERSHIP_UPDATE_ID = 901;
}

// ELM Event IDs
[EventId(9001)] public struct ConstructionOrder
[EventId(9002)] public struct ConstructionAck
[EventId(9003)] public struct DestructionOrder
[EventId(9004)] public struct DestructionAck

// Network Event IDs
[EventId(9010)] public struct DescriptorAuthorityChanged
```

---

## Appendix B: Configuration Examples

### Example: Network Topology (Static Config)

```csharp
public class StaticNetworkTopology : INetworkTopology
{
    private readonly int _localNodeId;
    private readonly Dictionary<DISEntityType, int[]> _peerMap;
    
    public StaticNetworkTopology(int localNodeId, string configFile)
    {
        _localNodeId = localNodeId;
        _peerMap = LoadConfig(configFile);
        // Config format: Tank -> [1, 2, 3] (all combat nodes participate)
    }
    
    public IEnumerable<int> GetExpectedPeers(DISEntityType type)
    {
        if (_peerMap.TryGetValue(type, out var peers))
            return peers.Where(p => p != _localNodeId); // Exclude self
        
        return Enumerable.Empty<int>();
    }
    
    public int LocalNodeId => _localNodeId;
}
```

### Example: Ownership Strategy (Weapon Server)

```csharp
public class WeaponServerOwnershipStrategy : IOwnershipDistributionStrategy
{
    private readonly int _weaponServerNodeId;
    
    public WeaponServerOwnershipStrategy(int weaponServerNodeId)
    {
        _weaponServerNodeId = weaponServerNodeId;
    }
    
    public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType, 
                                 int masterNodeId, long instanceId)
    {
        // Weapon Server owns all weapon descriptors
        if (descriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
        {
            return _weaponServerNodeId;
        }
        
        // Everything else defaults to Master
        return null;
    }
}
```

### Example: Lifecycle Mode Configuration

```csharp
var lifecycleModes = new Dictionary<DISEntityType, MasterFlags>
{
    // Critical entities: Wait for all nodes
    [DisType.M1A2_Tank] = MasterFlags.ReliableInit,
    [DisType.Apache_Helicopter] = MasterFlags.ReliableInit,
    
    // VFX/debris: Fast creation
    [DisType.Explosion_VFX] = MasterFlags.None,
    [DisType.Debris_Fragment] = MasterFlags.None,
};
```

---

## Appendix C: Migration Guide

### From Old Network Code

**Step 1:** Update lifecycle enum
```csharp
// Old
public enum EntityLifecycle { Constructing = 0, Active = 1, TearDown = 2 }

// New
public enum EntityLifecycle { Constructing = 0, Active = 1, TearDown = 2, Ghost = 4 }
```

**Step 2:** Update queries to exclude Ghosts
```csharp
// Old (implicitly excluded Constructing)
var query = repo.Query().With<Position>().Build();

// New (explicitly exclude Ghost)
var query = repo.Query().With<Position>().Build(); // Still works - Ghost not Active
// If you need all: .IncludeAll()
```

**Step 3:** Replace NetworkGateway constructor
```csharp
// Old
var gateway = new NetworkGatewayModule(localNodeId, ddsProvider);

// New
var gateway = new NetworkGatewayModule(
    localNodeId, 
    ddsProvider, 
    tkbDatabase,
    entityLifecycleModule,
    new DefaultOwnershipStrategy()
);
```

---

## Approval & Sign-Off

| Role | Name | Date | Signature |
|------|------|------|-----------|
| Lead Architect | [TBD] | 2026-01-09 | _________ |
| Network Engineer | [TBD] | | _________ |
| QA Lead | [TBD] | | _________ |

---

**Document Control:**
- **Created:** 2026-01-09 by AI Assistant (Antigravity)
- **Last Updated:** 2026-01-09
- **Next Review:** Upon implementation completion
