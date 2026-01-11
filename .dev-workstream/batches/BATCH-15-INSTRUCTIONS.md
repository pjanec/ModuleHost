# BATCH-15: Multi-Instance Support + Performance & Stress Testing

**Batch Number:** BATCH-15  
**Phase:** 4 (Final) + Performance Validation  
**Estimated Effort:** 6-8 hours  
**Priority:** HIGH (Completes Network-ELM Integration)

---

## 📋 Onboarding & Workflow

### Context
This batch **completes** the Network-ELM integration by implementing Phase 4 (Multi-Instance Support) and validates the entire system through comprehensive performance and stress testing.

**Previous Batches:**
- BATCH-12: Foundation Layer (NetworkConstants, interfaces, components)
- BATCH-13: Core Integration (Ghost protocol, NetworkSpawnerSystem, ELM bridge)
- BATCH-14/14.1: Reliable Initialization (NetworkGatewayModule, barrier logic, 27 tests)

**Status:** Phases 1-3 COMPLETE. Phase 4 + Performance Validation PENDING.

### Required Reading
1. **Workflow Guide:** `.dev-workstream/README.md`
2. **Design Document:** `docs/ModuleHost-network-ELM-implementation-spec.md` (sections on Multi-Instance and Testing)
3. **Analysis Summary:** `docs/ModuleHost-network-ELM-analysis-summary.md` (Phase 4 section)
4. **Previous Reviews:** `.dev-workstream/reviews/BATCH-14.1-REVIEW.md` (understand current state)

### Source Locations
- **Network Core:** `ModuleHost.Core/Network/`
- **Translators:** `ModuleHost.Core/Network/Translators/`
- **Systems:** `ModuleHost.Core/Network/Systems/`
- **Tests:** `ModuleHost.Core.Tests/Network/`
- **Benchmarks:** `ModuleHost.Benchmarks/`

### Report Submission
**When done, submit your report to:**  
`.dev-workstream/reports/BATCH-15-REPORT.md`

---

## 🎯 Objectives

### Part A: Multi-Instance Support (Phase 4)

**Goal:** Enable entities to have multiple descriptors of the same type with different instance IDs (e.g., Tank with 2 turrets = 2 WeaponState descriptors).

**Current Limitation:** Composite key system supports instance IDs, but all translators hardcode `instanceId = 0`.

### Part B: Performance & Stress Testing

**Goal:** Validate system performance, scalability, and reliability under production conditions.

**Coverage:**
- High entity counts (1000+)
- Concurrent operations
- Network stress (packet loss, timeouts)
- Benchmark baselines for future regression detection

---

## ✅ Tasks

---

## PART A: Multi-Instance Support

### Task 1: Extend IDataSample with InstanceId

**File:** `ModuleHost.Core/Network/IDescriptorTranslator.cs` (UPDATE)

**Requirement:**  
Add `InstanceId` property to `IDataSample` interface and `DataSample` class to support multi-instance descriptors.

**Implementation:**

```csharp
public interface IDataSample
{
    object Data { get; }
    DdsInstanceState InstanceState { get; }
    long EntityId { get; }
    long InstanceId { get; } // NEW: Instance ID for multi-instance descriptors
}

public class DataSample : IDataSample
{
    public object Data { get; set; }
    public DdsInstanceState InstanceState { get; set; }
    public long EntityId { get; set; }
    public long InstanceId { get; set; } = 0; // NEW: Default to 0 for single-instance
}
```

**Default Behavior:** `InstanceId = 0` for backward compatibility with existing single-instance descriptors.

**Why This Matters:**  
The composite key system already supports `(TypeId, InstanceId)` packing, but samples currently don't carry instance information. This enables translators to handle multiple instances per entity.

---

### Task 2: Add Multi-Instance Descriptor Classes

**File:** `ModuleHost.Core/Network/Messages/WeaponStateDescriptor.cs` (NEW)

**Requirement:**  
Create a descriptor for weapon state that supports instance IDs.

```csharp
using Fdp.Kernel;

namespace ModuleHost.Core.Network.Messages
{
    /// <summary>
    /// Weapon state descriptor for entities with multiple weapon systems.
    /// Supports multiple instances (e.g., turret 0, turret 1).
    /// </summary>
    public class WeaponStateDescriptor
    {
        public long EntityId { get; set; }
        public long InstanceId { get; set; } // Turret index (0, 1, 2...)
        public float AzimuthAngle { get; set; } // Horizontal rotation
        public float ElevationAngle { get; set; } // Vertical rotation
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
}
```

**File:** `ModuleHost.Core/Network/NetworkComponents.cs` (UPDATE)

Add component to store weapon state:

```csharp
/// <summary>
/// Weapon state component storing multi-instance weapon data.
/// Multiple weapons stored as dictionary keyed by instance ID.
/// </summary>
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

---

### Task 3: Implement WeaponStateTranslator with Multi-Instance Support

**File:** `ModuleHost.Core/Network/Translators/WeaponStateTranslator.cs` (UPDATE)

**Current Problem:** Existing `WeaponStateTranslator` doesn't handle instance IDs.

**Requirements:**
1. Read `InstanceId` from `IDataSample`
2. Update `WeaponStates` dictionary with instance-specific data
3. Egress publishes all weapon instances owned by local node
4. Use composite key to check ownership: `view.OwnsDescriptor(entity, WEAPON_STATE_DESCRIPTOR_ID, instanceId)`

**Implementation:**

```csharp
using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    public class WeaponStateTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.WeaponState";
        
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        private readonly int _localNodeId;
        
        public WeaponStateTranslator(
            int localNodeId,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.Data is not WeaponStateDescriptor desc)
                    continue;
                
                if (!_networkIdToEntity.TryGetValue(desc.EntityId, out var entity))
                    continue; // Entity doesn't exist yet
                
                // Get or create WeaponStates component
                WeaponStates weaponStates;
                if (view.HasManagedComponent<WeaponStates>(entity))
                {
                    weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
                }
                else
                {
                    weaponStates = new WeaponStates();
                }
                
                // Update specific weapon instance
                weaponStates.Weapons[desc.InstanceId] = new WeaponState
                {
                    AzimuthAngle = desc.AzimuthAngle,
                    ElevationAngle = desc.ElevationAngle,
                    AmmoCount = desc.AmmoCount,
                    Status = desc.Status
                };
                
                cmd.AddManagedComponent(entity, weaponStates);
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query entities with weapon states
            var query = view.Query()
                .WithManaged<WeaponStates>()
                .WithLifecycle(EntityLifecycle.Active)
                .Build();
            
            foreach (var entity in query)
            {
                if (!view.HasComponent<NetworkIdentity>(entity))
                    continue;
                
                var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
                var weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
                
                // Publish each weapon instance we own
                foreach (var kvp in weaponStates.Weapons)
                {
                    long instanceId = kvp.Key;
                    
                    // Check if we own this weapon instance
                    if (!view.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, instanceId))
                        continue;
                    
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
    }
}
```

---

### Task 4: Update NetworkSpawnerSystem for Multi-Instance Ownership

**File:** `ModuleHost.Core/Network/Systems/NetworkSpawnerSystem.cs` (UPDATE)

**Current Problem:** `DetermineDescriptorOwnership` doesn't handle multiple weapon instances.

**Requirement:**  
Update ownership determination to call strategy for each weapon instance defined in entity configuration.

**Changes:**

```csharp
private void DetermineDescriptorOwnership(
    EntityRepository repo,
    Entity entity,
    NetworkSpawnRequest request)
{
    // ... existing code for EntityMaster and EntityState ...
    
    // NEW: Determine weapon instance ownership
    // Example: Tank with 2 turrets -> check ownership for weapon instances 0 and 1
    int weaponInstanceCount = GetWeaponInstanceCount(request.DisType);
    
    for (int i = 0; i < weaponInstanceCount; i++)
    {
        var ownerNodeId = _strategy.GetInitialOwner(
            NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
            request.DisType,
            request.MasterNodeId,
            instanceId: i
        );
        
        if (ownerNodeId.HasValue && ownerNodeId.Value != request.MasterNodeId)
        {
            long key = OwnershipExtensions.PackKey(
                NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
                i
            );
            descOwnership.Map[key] = ownerNodeId.Value;
        }
    }
    
    // ... rest of method ...
}

private int GetWeaponInstanceCount(DISEntityType type)
{
    // Simple heuristic: Could be configured via TKB template metadata
    // For now, hardcode based on entity kind
    switch (type.Kind)
    {
        case 1: // Platform/Tank
            return type.Category == 1 ? 2 : 1; // Main battle tank = 2 weapons, others = 1
        default:
            return 0; // No weapons
    }
}
```

**Alternative (Cleaner):** Store weapon count in TKB template metadata or entity config. For this batch, the heuristic approach is acceptable for testing.

---

### Task 5: Multi-Instance Integration Tests

**File:** `ModuleHost.Core.Tests/Network/MultiInstanceTests.cs` (NEW)

**Requirements:**  
Comprehensive tests for multi-instance descriptor handling.

**Minimum Tests (8 required):**

```csharp
using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class MultiInstanceTests
    {
        [Fact]
        public void DataSample_InstanceId_DefaultsToZero()
        {
            var sample = new DataSample();
            Assert.Equal(0, sample.InstanceId);
        }
        
        [Fact]
        public void WeaponStateTranslator_Ingress_MultipleInstances_StoresIndependently()
        {
            // Setup: Entity with 2 weapon instances
            // Act: Receive weapon state for instance 0 and instance 1
            // Assert: WeaponStates contains both instances with correct data
        }
        
        [Fact]
        public void WeaponStateTranslator_Egress_OnlyPublishesOwnedInstances()
        {
            // Setup: Entity with 3 weapons, local node owns instances 0 and 2
            // Act: ScanAndPublish
            // Assert: Only instances 0 and 2 published
        }
        
        [Fact]
        public void NetworkSpawner_MultiTurretTank_DeterminesInstanceOwnership()
        {
            // Setup: Tank entity with 2 turrets, strategy assigns turret 1 to node 2
            // Act: ProcessSpawnRequest
            // Assert: DescriptorOwnership map contains (WEAPON_STATE_ID, 1) -> Node 2
        }
        
        [Fact]
        public void OwnershipExtensions_PackUnpackKey_WithNonZeroInstance()
        {
            long packed = OwnershipExtensions.PackKey(999, 5);
            var (typeId, instanceId) = OwnershipExtensions.UnpackKey(packed);
            
            Assert.Equal(999, typeId);
            Assert.Equal(5, instanceId);
        }
        
        [Fact]
        public void OwnershipExtensions_OwnsDescriptor_ChecksCompositeKey()
        {
            // Setup: Entity with ownership map containing (WEAPON, 2) -> Node 1
            // Act: Check ownership for different instances
            // Assert: Node 1 owns instance 2, not instances 0 or 1
        }
        
        [Fact]
        public void WeaponStates_MultipleInstances_UpdateIndependently()
        {
            // Setup: Entity with weapon instances 0, 1, 2
            // Act: Update instance 1's ammo count
            // Assert: Only instance 1 changed, instances 0 and 2 unchanged
        }
        
        [Fact]
        public void MultiInstance_OwnershipTransfer_UpdatesSpecificInstance()
        {
            // Setup: Entity with 2 weapons, transfer ownership of instance 1
            // Act: OwnershipUpdate for (WEAPON_STATE, 1)
            // Assert: Instance 1 ownership changed, instance 0 unchanged
        }
    }
}
```

**Important:** Implement all 8 tests with full logic, not just stubs.

---

### Task 6: Multi-Instance Integration Scenario

**File:** `ModuleHost.Core.Tests/Network/MultiInstanceScenarios.cs` (NEW)

**Requirements:**  
End-to-end scenario demonstrating multi-turret tank replication.

**Scenario: Two-Node Multi-Turret Tank**

```csharp
[Fact]
public void Scenario_MultiTurretTank_ReplicatesAcrossNodes()
{
    // Setup: 2-node cluster
    // Node 1: Owns EntityMaster and primary weapon (instance 0)
    // Node 2: Owns secondary weapon (instance 1)
    
    using var repo1 = new EntityRepository();
    using var repo2 = new EntityRepository();
    
    RegisterComponents(repo1);
    RegisterComponents(repo2);
    
    // === Node 1: Create tank entity ===
    var tankEntity1 = repo1.CreateEntity();
    repo1.AddComponent(tankEntity1, new NetworkIdentity { Value = 100 });
    
    var weaponStates1 = new WeaponStates();
    weaponStates1.Weapons[0] = new WeaponState { AzimuthAngle = 45.0f, AmmoCount = 100 };
    weaponStates1.Weapons[1] = new WeaponState { AzimuthAngle = 90.0f, AmmoCount = 50 };
    repo1.AddManagedComponent(tankEntity1, weaponStates1);
    
    // Setup ownership: Node 1 owns weapon 0, Node 2 owns weapon 1
    var ownership1 = new DescriptorOwnership();
    ownership1.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1)] = 2;
    repo1.AddManagedComponent(tankEntity1, ownership1);
    
    // === Node 1: Egress (publishes weapon 0 only) ===
    var writer1 = new MockDataWriter();
    var translator1 = new WeaponStateTranslator(1, new Dictionary<long, Entity> { { 100, tankEntity1 } });
    translator1.ScanAndPublish(repo1, writer1);
    
    Assert.Single(writer1.WrittenSamples); // Only weapon 0
    var pub1 = (WeaponStateDescriptor)writer1.WrittenSamples[0];
    Assert.Equal(0, pub1.InstanceId);
    Assert.Equal(45.0f, pub1.AzimuthAngle);
    
    // === Node 2: Receives weapon 0, publishes weapon 1 ===
    var tankEntity2 = repo2.CreateEntity();
    repo2.AddComponent(tankEntity2, new NetworkIdentity { Value = 100 });
    
    // Simulate ownership: Node 2 owns weapon 1
    var ownership2 = new DescriptorOwnership();
    ownership2.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)] = 1;
    // Instance 1 implicitly owned by node 2 (not in map = use PrimaryOwnerId logic)
    repo2.AddManagedComponent(tankEntity2, ownership2);
    
    var weaponStates2 = new WeaponStates();
    weaponStates2.Weapons[1] = new WeaponState { AzimuthAngle = 120.0f, AmmoCount = 75 };
    repo2.AddManagedComponent(tankEntity2, weaponStates2);
    
    // Node 2 publishes
    var writer2 = new MockDataWriter();
    var translator2 = new WeaponStateTranslator(2, new Dictionary<long, Entity> { { 100, tankEntity2 } });
    translator2.ScanAndPublish(repo2, writer2);
    
    Assert.Single(writer2.WrittenSamples); // Only weapon 1
    var pub2 = (WeaponStateDescriptor)writer2.WrittenSamples[0];
    Assert.Equal(1, pub2.InstanceId);
    Assert.Equal(120.0f, pub2.AzimuthAngle);
    
    // === Node 1: Ingress weapon 1 from Node 2 ===
    var reader1 = new MockDataReader(new DataSample
    {
        Data = new WeaponStateDescriptor
        {
            EntityId = 100,
            InstanceId = 1,
            AzimuthAngle = 120.0f,
            AmmoCount = 75
        },
        InstanceState = DdsInstanceState.Alive,
        EntityId = 100,
        InstanceId = 1 // NEW: Carry instance ID in sample
    });
    
    var cmd1 = repo1.GetCommandBuffer();
    translator1.PollIngress(reader1, cmd1, repo1);
    cmd1.Playback();
    
    // Verify: Node 1 now has both weapon instances
    var finalWeapons1 = repo1.GetManagedComponentRO<WeaponStates>(tankEntity1);
    Assert.Equal(2, finalWeapons1.Weapons.Count);
    Assert.Equal(45.0f, finalWeapons1.Weapons[0].AzimuthAngle);  // Local weapon 0
    Assert.Equal(120.0f, finalWeapons1.Weapons[1].AzimuthAngle); // Remote weapon 1
}
```

---

## PART B: Performance & Stress Testing

### Task 7: Network Performance Benchmarks

**File:** `ModuleHost.Benchmarks/NetworkPerformanceBenchmarks.cs` (NEW)

**Requirements:**  
Benchmark critical network operations using BenchmarkDotNet.

**Benchmarks to Implement (5 minimum):**

```csharp
using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Tests.Mocks;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 3, targetCount: 10)]
    public class NetworkPerformanceBenchmarks
    {
        private EntityRepository _repo;
        private Dictionary<long, Entity> _networkIdToEntity;
        private EntityStateTranslator _stateTranslator;
        private WeaponStateTranslator _weaponTranslator;
        private NetworkEgressSystem _egressSystem;
        private MockDataWriter _writer;
        
        [Params(100, 500, 1000)]
        public int EntityCount;
        
        [GlobalSetup]
        public void Setup()
        {
            _repo = new EntityRepository();
            RegisterComponents(_repo);
            
            _networkIdToEntity = new Dictionary<long, Entity>();
            
            // Create entities with network components
            for (int i = 0; i < EntityCount; i++)
            {
                var entity = _repo.CreateEntity();
                _repo.AddComponent(entity, new NetworkIdentity { Value = i });
                _repo.AddComponent(entity, new Position { X = i, Y = i, Z = 0 });
                _repo.AddComponent(entity, new Velocity { X = 1, Y = 1, Z = 0 });
                _repo.SetLifecycleState(entity, EntityLifecycle.Active);
                
                _networkIdToEntity[i] = entity;
                
                // Add weapons to half the entities
                if (i % 2 == 0)
                {
                    var weaponStates = new WeaponStates();
                    weaponStates.Weapons[0] = new WeaponState { AzimuthAngle = 45, AmmoCount = 100 };
                    _repo.AddManagedComponent(entity, weaponStates);
                }
            }
            
            _stateTranslator = new EntityStateTranslator(1, _networkIdToEntity);
            _weaponTranslator = new WeaponStateTranslator(1, _networkIdToEntity);
            _writer = new MockDataWriter();
            
            _egressSystem = new NetworkEgressSystem(
                new IDescriptorTranslator[] { _stateTranslator, _weaponTranslator },
                new IDataWriter[] { _writer, _writer }
            );
        }
        
        [GlobalCleanup]
        public void Cleanup()
        {
            _repo.Dispose();
        }
        
        [Benchmark]
        public void Egress_PublishAllEntities()
        {
            _writer.WrittenSamples.Clear();
            _egressSystem.Execute(_repo, 0.016f);
        }
        
        [Benchmark]
        public void Ingress_EntityState_BatchUpdate()
        {
            // Simulate receiving updates for 10% of entities
            int updateCount = EntityCount / 10;
            var samples = new List<IDataSample>();
            
            for (int i = 0; i < updateCount; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityStateDescriptor
                    {
                        EntityId = i,
                        Position = new Position { X = i + 1, Y = i + 1, Z = 0 },
                        Velocity = new Velocity { X = 2, Y = 2, Z = 0 }
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = _repo.GetCommandBuffer();
            _stateTranslator.PollIngress(reader, cmd, _repo);
            cmd.Playback();
        }
        
        [Benchmark]
        public void OwnershipLookup_CompositeKey()
        {
            // Benchmark composite key packing/unpacking performance
            long sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                long key = OwnershipExtensions.PackKey(i, i * 2);
                var (typeId, instanceId) = OwnershipExtensions.UnpackKey(key);
                sum += typeId + instanceId;
            }
        }
        
        [Benchmark]
        public void GhostPromotion_BatchProcess()
        {
            // Create 10 Ghost entities
            var ghosts = new List<Entity>();
            for (int i = 0; i < 10; i++)
            {
                var entity = _repo.CreateEntity();
                _repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
                _repo.AddComponent(entity, new NetworkSpawnRequest 
                { 
                    DisType = new DISEntityType { Kind = 1 },
                    MasterNodeId = 1
                });
                ghosts.Add(entity);
            }
            
            // Benchmark NetworkSpawnerSystem processing
            // (Simplified - in real scenario would need full setup)
            foreach (var ghost in ghosts)
            {
                _repo.SetLifecycleState(ghost, EntityLifecycle.Constructing);
            }
        }
        
        [Benchmark]
        public void NetworkGateway_TimeoutCheck()
        {
            // Benchmark timeout checking logic (simplified)
            uint currentFrame = 1000;
            var pendingFrames = new Dictionary<Entity, uint>();
            
            for (int i = 0; i < 100; i++)
            {
                var entity = new Entity(i, 1);
                pendingFrames[entity] = (uint)(currentFrame - i * 3);
            }
            
            var timedOut = new List<Entity>();
            foreach (var kvp in pendingFrames)
            {
                if (currentFrame - kvp.Value > 300)
                {
                    timedOut.Add(kvp.Key);
                }
            }
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
        }
    }
}
```

**Expected Output:** Baseline performance metrics for each operation. Document results in report.

---

### Task 8: Stress Test - 1000-Entity Concurrent Operations

**File:** `ModuleHost.Core.Tests/Network/NetworkStressTests.cs` (NEW)

**Requirements:**  
Validate system stability under high load.

**Tests to Implement (4 minimum):**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkStressTests
    {
        [Fact]
        public void Stress_1000Entities_MasterFirstCreation()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityMasterTranslator(1, networkIdToEntity, null!);
            
            // Create 1000 entities via EntityMaster messages
            var samples = new List<IDataSample>();
            for (int i = 0; i < 1000; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityMasterDescriptor
                    {
                        EntityId = i,
                        OwnerId = 1,
                        Type = new DISEntityType { Kind = 1 },
                        Name = $"Entity_{i}"
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = repo.GetCommandBuffer();
            
            var startTime = DateTime.UtcNow;
            translator.PollIngress(reader, cmd, repo);
            cmd.Playback();
            var duration = DateTime.UtcNow - startTime;
            
            // Verify all created
            Assert.Equal(1000, networkIdToEntity.Count);
            Assert.True(duration.TotalMilliseconds < 500, $"Creation took {duration.TotalMilliseconds}ms (expected <500ms)");
        }
        
        [Fact]
        public void Stress_1000Entities_GhostPromotion()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            // Create 1000 Ghost entities
            var entities = new List<Entity>();
            for (int i = 0; i < 1000; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkIdentity { Value = i });
                repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
                entities.Add(entity);
            }
            
            // Promote all to Constructing
            var startTime = DateTime.UtcNow;
            foreach (var entity in entities)
            {
                repo.SetLifecycleState(entity, EntityLifecycle.Constructing);
            }
            var duration = DateTime.UtcNow - startTime;
            
            // Verify all promoted
            foreach (var entity in entities)
            {
                Assert.Equal(EntityLifecycle.Constructing, repo.GetLifecycleState(entity));
            }
            
            Assert.True(duration.TotalMilliseconds < 100, $"Promotion took {duration.TotalMilliseconds}ms (expected <100ms)");
        }
        
        [Fact]
        public void Stress_ConcurrentOwnershipUpdates_1000Entities()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            
            // Create 1000 entities
            for (int i = 0; i < 1000; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkIdentity { Value = i });
                repo.SetLifecycleState(entity, EntityLifecycle.Active);
                
                var ownership = new DescriptorOwnership();
                ownership.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)] = 1;
                repo.AddManagedComponent(entity, ownership);
                
                networkIdToEntity[i] = entity;
            }
            
            // Transfer ownership for all entities
            var translator = new OwnershipUpdateTranslator(2, networkIdToEntity);
            var samples = new List<IDataSample>();
            
            for (int i = 0; i < 1000; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new OwnershipUpdate
                    {
                        EntityId = i,
                        DescrTypeId = NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
                        InstanceId = 0,
                        NewOwner = 2,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = repo.GetCommandBuffer();
            
            var startTime = DateTime.UtcNow;
            translator.ProcessOwnershipUpdate(reader, cmd, repo);
            cmd.Playback();
            var duration = DateTime.UtcNow - startTime;
            
            // Verify all updated
            int updatedCount = 0;
            foreach (var kvp in networkIdToEntity)
            {
                var entity = kvp.Value;
                var ownership = repo.GetManagedComponentRO<DescriptorOwnership>(entity);
                long key = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0);
                
                if (ownership.Map[key] == 2)
                    updatedCount++;
            }
            
            Assert.Equal(1000, updatedCount);
            Assert.True(duration.TotalMilliseconds < 1000, $"Updates took {duration.TotalMilliseconds}ms (expected <1000ms)");
        }
        
        [Fact]
        public void Stress_ReliableInit_100EntitiesWithTimeout()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var topo = new StaticNetworkTopology(1, new[] { 1, 2, 3 }); // 3-node cluster
            var elm = new EntityLifecycleModule(new[] { 10 });
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            gateway.Initialize(null!);
            
            // Create 100 entities in reliable mode
            var entities = new List<Entity>();
            for (int i = 0; i < 100; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType { Kind = 1 } });
                repo.AddComponent(entity, new PendingNetworkAck());
                entities.Add(entity);
                
                var cmd = repo.GetCommandBuffer();
                elm.BeginConstruction(entity, 1, repo.GlobalVersion, cmd);
                cmd.Playback();
            }
            
            // Gateway processes - all should be pending
            gateway.Execute(repo, 0);
            
            // Advance time past timeout
            for (int i = 0; i < 305; i++)
            {
                repo.Tick();
            }
            
            // Gateway check timeout - all should ACK due to timeout
            var startTime = DateTime.UtcNow;
            gateway.Execute(repo, 0);
            var duration = DateTime.UtcNow - startTime;
            
            // Verify reasonable performance even with 100 timeouts
            Assert.True(duration.TotalMilliseconds < 500, $"Timeout check took {duration.TotalMilliseconds}ms (expected <500ms)");
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
        }
    }
}
```

---

### Task 9: Reliability Test - Packet Loss & Network Partitions

**File:** `ModuleHost.Core.Tests/Network/NetworkReliabilityTests.cs` (NEW)

**Requirements:**  
Test system behavior under adverse network conditions.

**Tests to Implement (3 minimum):**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkReliabilityTests
    {
        [Fact]
        public void Reliability_PacketLoss_10Percent_EntityEventuallyComplete()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityStateTranslator(1, networkIdToEntity);
            
            // Simulate 100 packets, 10% loss
            var random = new Random(42); // Seeded for reproducibility
            var packets = new List<IDataSample>();
            
            for (int i = 0; i < 100; i++)
            {
                if (random.NextDouble() > 0.10) // 90% delivery rate
                {
                    packets.Add(new DataSample
                    {
                        Data = new EntityStateDescriptor
                        {
                            EntityId = i,
                            Position = new Position { X = i, Y = i, Z = 0 },
                            Velocity = new Velocity { X = 1, Y = 1, Z = 0 }
                        },
                        InstanceState = DdsInstanceState.Alive,
                        EntityId = i
                    });
                }
            }
            
            var reader = new MockDataReader(packets.ToArray());
            var cmd = repo.GetCommandBuffer();
            translator.PollIngress(reader, cmd, repo);
            cmd.Playback();
            
            // Verify ~90 entities created (with some variance)
            Assert.InRange(networkIdToEntity.Count, 85, 95);
        }
        
        [Fact]
        public void Reliability_DuplicatePackets_Idempotency()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityStateTranslator(1, networkIdToEntity);
            
            // Send same EntityState packet 5 times (duplicate due to retransmission)
            var samples = new List<IDataSample>();
            for (int i = 0; i < 5; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityStateDescriptor
                    {
                        EntityId = 100,
                        Position = new Position { X = 10, Y = 10, Z = 0 },
                        Velocity = new Velocity { X = 1, Y = 1, Z = 0 }
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = 100
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = repo.GetCommandBuffer();
            translator.PollIngress(reader, cmd, repo);
            cmd.Playback();
            
            // Verify only 1 entity created, not 5
            Assert.Single(networkIdToEntity);
            
            var entity = networkIdToEntity[100];
            var pos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(10, pos.X);
        }
        
        [Fact]
        public void Reliability_OutOfOrderPackets_EventualConsistency()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var stateTranslator = new EntityStateTranslator(1, networkIdToEntity);
            var masterTranslator = new EntityMasterTranslator(1, networkIdToEntity, null!);
            
            // Scenario: EntityState arrives BEFORE EntityMaster (out of order)
            
            // Step 1: EntityState arrives (should create Ghost)
            var stateReader = new MockDataReader(new DataSample
            {
                Data = new EntityStateDescriptor
                {
                    EntityId = 200,
                    Position = new Position { X = 50, Y = 50, Z = 0 },
                    Velocity = new Velocity { X = 2, Y = 2, Z = 0 }
                },
                InstanceState = DdsInstanceState.Alive,
                EntityId = 200
            });
            
            var cmd = repo.GetCommandBuffer();
            stateTranslator.PollIngress(stateReader, cmd, repo);
            cmd.Playback();
            
            Assert.Single(networkIdToEntity);
            var entity = networkIdToEntity[200];
            Assert.Equal(EntityLifecycle.Ghost, repo.GetLifecycleState(entity));
            
            var ghostPos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(50, ghostPos.X);
            
            // Step 2: EntityMaster arrives late (should promote Ghost)
            var masterReader = new MockDataReader(new DataSample
            {
                Data = new EntityMasterDescriptor
                {
                    EntityId = 200,
                    OwnerId = 1,
                    Type = new DISEntityType { Kind = 1 },
                    Name = "LateArrival"
                },
                InstanceState = DdsInstanceState.Alive,
                EntityId = 200
            });
            
            cmd = repo.GetCommandBuffer();
            masterTranslator.PollIngress(masterReader, cmd, repo);
            cmd.Playback();
            
            // Verify: Same entity, Ghost position preserved, now has NetworkSpawnRequest
            Assert.Single(networkIdToEntity);
            Assert.True(repo.HasComponent<NetworkSpawnRequest>(entity));
            
            var finalPos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(50, finalPos.X); // Position from Ghost preserved
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
        }
    }
}
```

---

## 🧪 Testing Requirements

### Minimum Test Coverage

**Part A - Multi-Instance:**
- 8 unit tests (MultiInstanceTests.cs)
- 1 integration scenario (MultiInstanceScenarios.cs)

**Part B - Performance:**
- 5 benchmarks (NetworkPerformanceBenchmarks.cs)
- 4 stress tests (NetworkStressTests.cs)
- 3 reliability tests (NetworkReliabilityTests.cs)

**Total Minimum:** 21 new tests

### Test Quality Standards

**Each test must:**
1. Have clear Arrange/Act/Assert structure
2. Validate specific behavior, not just "doesn't crash"
3. Use meaningful assertions (not just `Assert.True(true)`)
4. Include performance expectations where applicable (e.g., "< 500ms")

**Benchmarks must:**
1. Document baseline results in report
2. Use [MemoryDiagnoser] to track allocations
3. Test realistic scenarios (not toy data)

---

## 📝 Report Questions

Answer these questions in your report (`.dev-workstream/reports/BATCH-15-REPORT.md`):

### Part A: Multi-Instance

1. **Design Decision:** How does `WeaponStateTranslator` handle updates to only one weapon instance without affecting others? Explain the data structure and update logic.

2. **Ownership Complexity:** In a 3-node cluster with a tank having 3 turrets, each owned by a different node, trace the full ownership determination flow. What data structures are involved?

3. **Backward Compatibility:** How does setting `InstanceId = 0` by default maintain backward compatibility with existing single-instance descriptors? What happens to old code that doesn't set this field?

4. **Edge Case:** What happens if a node receives a `WeaponStateDescriptor` for instance 5, but the entity only has 2 weapons configured locally? How is this handled?

### Part B: Performance

5. **Benchmark Results:** What were the baseline performance metrics for the 5 benchmarks? Include mean time, allocations, and p95 latency for each.

6. **Scalability:** Based on stress tests, what is the practical limit for entity count before performance degrades significantly? What's the bottleneck?

7. **Reliability Trade-offs:** Packet loss tests show X% delivery rate. What reliability mechanisms in the system (timeouts, retries, etc.) compensate for this? What's the worst-case scenario?

8. **Performance Regression:** If these benchmarks are run in CI/CD, what threshold would you recommend for failing the build? (e.g., "fail if egress takes >2x baseline")

### General

9. **Integration Complexity:** This batch integrates multi-instance support across 4 components (IDataSample, descriptors, translators, spawner). What was the most challenging integration point?

10. **Production Readiness:** Based on stress and reliability testing, is the Network-ELM system ready for production use? What limitations remain?

---

## 🎯 Success Criteria

This batch is **DONE** when:

1. ✅ All 6 tasks in Part A implemented (Multi-Instance Support)
2. ✅ All 3 tasks in Part B implemented (Performance Testing)
3. ✅ Minimum 21 tests implemented and passing
4. ✅ All benchmarks run successfully (results documented)
5. ✅ All 10 report questions answered thoroughly
6. ✅ Code compiles without errors or warnings
7. ✅ No test-only code in production files
8. ✅ Multi-instance integration scenario passes end-to-end

---

## 💡 Implementation Hints

### Hint 1: IDataSample InstanceId

The `InstanceId` property on `IDataSample` should flow from DDS message → Sample → Translator. Most messages will have `InstanceId = 0` (default). Only multi-instance messages (like `WeaponStateDescriptor`) use non-zero values.

### Hint 2: Backward Compatibility

Existing code doesn't set `InstanceId` on samples. Make sure your implementation handles this gracefully:
- Default to 0 if not set
- Existing translators (EntityMaster, EntityState) continue using instance 0
- Only `WeaponStateTranslator` needs multi-instance logic

### Hint 3: Benchmark Interpretation

BenchmarkDotNet output includes:
- **Mean:** Average execution time (primary metric)
- **Error/StdDev:** Variability (lower is better)
- **Gen 0/Gen 1:** GC allocations (should be 0 for hot paths)
- **Allocated:** Total memory allocated (minimize this)

Document the "Mean" and "Allocated" columns in your report.

### Hint 4: Stress Test Performance

Stress tests should complete within reasonable time:
- 1000 entities: < 1 second for creation/update
- 100 reliable init timeouts: < 500ms to process

If tests run slower, investigate bottlenecks (likely dictionary lookups or query performance).

### Hint 5: MockDataReader Enhancement

You may need to enhance `MockDataReader` to support `InstanceId` on samples:

```csharp
public class MockDataReader : IDataReader
{
    private readonly IDataSample[] _samples;
    
    public MockDataReader(params IDataSample[] samples)
    {
        _samples = samples;
    }
    
    // ... existing implementation ...
}
```

---

## 🚨 Common Pitfalls to Avoid

1. **Don't hardcode instance counts** - Use entity configuration or heuristics, not magic numbers like "always 2 weapons"
2. **Don't skip benchmark documentation** - Raw BenchmarkDotNet output is not enough; interpret and explain results
3. **Don't write trivial stress tests** - "Create 1000 entities" is not useful without performance assertions
4. **Don't ignore memory allocations** - Use `[MemoryDiagnoser]` and report Gen0/Gen1/Allocated
5. **Don't test only happy paths** - Reliability tests MUST include failures (packet loss, timeouts, out-of-order)

---

## 📚 Reference Materials

- **Multi-Instance Design:** `docs/ModuleHost-network-ELM-implementation-spec.md` (Known Limitations section)
- **Composite Key Packing:** `ModuleHost.Core/Network/NetworkComponents.cs` (OwnershipExtensions)
- **Benchmark Examples:** `ModuleHost.Benchmarks/ConvoyPerformance.cs`
- **Previous Integration Tests:** `ModuleHost.Core.Tests/Network/ReliableInitializationScenarios.cs`

---

## 📊 Deliverable Checklist

Copy this into your report:

```markdown
## Implementation Checklist

### Part A: Multi-Instance Support
- [ ] Task 1: IDataSample.InstanceId added
- [ ] Task 2: WeaponStateDescriptor created
- [ ] Task 3: WeaponStateTranslator implemented
- [ ] Task 4: NetworkSpawnerSystem updated
- [ ] Task 5: MultiInstanceTests (8 tests)
- [ ] Task 6: MultiInstanceScenarios (1 scenario)

### Part B: Performance & Stress Testing
- [ ] Task 7: NetworkPerformanceBenchmarks (5 benchmarks)
- [ ] Task 8: NetworkStressTests (4 tests)
- [ ] Task 9: NetworkReliabilityTests (3 tests)

### Test Summary
- [ ] Total tests: 21+ (8 + 1 + 5 + 4 + 3)
- [ ] All tests passing
- [ ] Benchmarks run and documented

### Report Quality
- [ ] All 10 questions answered
- [ ] Benchmark results included
- [ ] Performance analysis provided
- [ ] Production readiness assessment
```

---

**Batch Created:** 2026-01-11  
**Development Lead:** AI Assistant  
**Estimated Completion:** 6-8 hours  
**Priority:** HIGH - Completes Network-ELM Integration

This is the **final implementation batch** for the Network-ELM system. After BATCH-15, the system will be feature-complete and production-ready! 🚀
