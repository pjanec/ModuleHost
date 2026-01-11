# BATCH-13: Network-ELM Integration - Translators & Spawner System

**Batch Number:** BATCH-13  
**Phase:** Network System Upgrade - Phase 1 (Part 2 of 2)  
**Estimated Effort:** 8-10 hours  
**Priority:** HIGH - Core functionality implementation

---

## 📋 Onboarding & Workflow

### Developer Instructions
Welcome to BATCH-13! This batch implements the core integration logic between Network and ELM systems. You'll refactor existing translators and create the NetworkSpawnerSystem that bridges the two systems.

### Required Reading (IN ORDER)
1. **Workflow Guide:** `d:\Work\ModuleHost\.dev-workstream\README.md`
2. **BATCH-12 Review:** `d:\Work\ModuleHost\.dev-workstream\reviews\BATCH-12-REVIEW.md` - **READ THIS!** Understand what needs improvement
3. **Implementation Spec:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-implementation-spec.md` - Focus on sections 5, 6
4. **Analysis Summary:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-analysis-summary.md` - Critical Issues #1, #2, #3

### Source Code Location
- **Primary Work Area:** `d:\Work\ModuleHost\ModuleHost.Core\Network\`
- **Test Project:** `d:\Work\ModuleHost\ModuleHost.Core.Tests\`

### Report Submission
**When done, submit your report to:**  
`d:\Work\ModuleHost\.dev-workstream\reports\BATCH-13-REPORT.md`

**If you have questions, create:**  
`d:\Work\ModuleHost\.dev-workstream\questions\BATCH-13-QUESTIONS.md`

---

## 🎯 Batch Objectives

This batch implements the **core Network-ELM integration**:

1. Refactor EntityStateTranslator to create Ghost entities
2. Complete EntityMasterTranslator implementation (currently incomplete stub)
3. Implement NetworkSpawnerSystem (the bridge between Network and ELM)
4. Update OwnershipUpdateTranslator to emit events
5. Add comprehensive integration tests

**What Makes This Batch Critical:**
- Fixes Critical Issue #1: Entity Creation Race Condition
- Fixes Critical Issue #2: EntityMasterTranslator Incomplete Implementation
- Fixes Critical Issue #3: No Network-ELM Bridge

---

## ⚠️ IMPORTANT: Quality Standards for This Batch

### Based on BATCH-12 Review Feedback:

**❗ TEST QUALITY EXPECTATIONS**
- **NOT ACCEPTABLE:** Tests that only verify "can I set this value"
- **REQUIRED:** Tests that verify actual behavior, edge cases, and integration scenarios
- **REQUIRED:** Tests must validate the WHAT MATTERS, not just that code runs

**❗ REPORT QUALITY EXPECTATIONS**
- **REQUIRED:** Use the full report template structure
- **REQUIRED:** Thoroughly answer ALL specific questions in instructions
- **REQUIRED:** Document design decisions YOU made beyond the spec
- **REQUIRED:** Explain challenges encountered and how you solved them
- **REQUIRED:** Discuss any deviations with clear rationale

**❗ CODE QUALITY EXPECTATIONS**
- **REQUIRED:** Handle edge cases (null entities, missing components, etc.)
- **REQUIRED:** Consistent error handling patterns
- **REQUIRED:** XML documentation on ALL public methods
- **REQUIRED:** Follow existing code patterns (study EntityStateTranslator current implementation)

---

## ✅ Tasks

### Task 1: Add EntityLifecycle.Ghost State to Fdp.Kernel

**File:** `FDP/Fdp.Kernel/EntityLifecycle.cs` (UPDATE)

**Description:**  
Add the `Ghost` state to the EntityLifecycle enum. This state represents entities created from EntityState packets that are waiting for EntityMaster to arrive.

**Current Enum:**
```csharp
public enum EntityLifecycle : byte
{
    Constructing = 0,
    Active = 1,
    TearDown = 2,
}
```

**Required Change:**
```csharp
public enum EntityLifecycle : byte
{
    Constructing = 0,
    Active = 1,
    TearDown = 2,
    Ghost = 4,  // Entity created from network state, awaiting EntityMaster
}
```

**⚠️ IMPORTANT:** This is a breaking change! Use value `4` (not `3`) to leave room for future states.

**Reference:** Implementation Spec Section 5.1 (Ghost Entity Protocol), Analysis Summary Section "Decision 1"

**Tests Required:**
- ✅ Unit test: Verify Ghost state can be set on entity
- ✅ Unit test: Verify Ghost entities excluded from standard queries (default behavior)
- ✅ Unit test: Verify Ghost entities included with `.IncludeAll()` or `.WithLifecycle(Ghost)`

---

### Task 2: Refactor EntityStateTranslator - Add Ghost Creation

**File:** `ModuleHost.Core/Network/Translators/EntityStateTranslator.cs` (REFACTOR)

**Description:**  
Refactor the translator to create Ghost entities when EntityState arrives before EntityMaster.

**Current Problem (Lines 101-134):**
```csharp
private Entity CreateEntityFromDescriptor(...)
{
    var entity = cmd.CreateEntity();
    cmd.SetLifecycleState(entity, EntityLifecycle.Constructing); // ❌ WRONG
    // ... sets Position, Velocity, etc.
}
```

**Required Changes:**

#### 2A: Update CreateEntityFromDescriptor to use Direct Repository Access

**WHY:** We need immediate entity ID for `_networkIdToEntity` mapping. `cmd.CreateEntity()` returns deferred ID.

**SAFETY:** NetworkGateway must run synchronously (main thread) - this is already the case.

```csharp
private Entity CreateEntityFromDescriptor(
    EntityStateDescriptor desc, 
    IEntityCommandBuffer cmd, 
    ISimulationView view)
{
    // Cast to EntityRepository for direct access
    var repo = view as EntityRepository;
    if (repo == null)
    {
        throw new InvalidOperationException(
            "EntityStateTranslator requires direct EntityRepository access. " +
            "NetworkGateway must run with ExecutionPolicy.Synchronous().");
    }
    
    // Direct entity creation (immediate ID)
    var entity = repo.CreateEntity();
    
    // ★ NEW: Create as GHOST (not Constructing)
    repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
    
    // Set initial network state
    repo.AddComponent(entity, new Position { Value = desc.Location });
    repo.AddComponent(entity, new Velocity { Value = desc.Velocity });
    repo.AddComponent(entity, new NetworkIdentity { Value = desc.EntityId });
    
    // Set ownership
    repo.AddComponent(entity, new NetworkOwnership
    {
        PrimaryOwnerId = desc.OwnerId,
        LocalNodeId = _localNodeId
    });
    
    // Initialize empty descriptor ownership map
    repo.AddManagedComponent(entity, new DescriptorOwnership());
    
    repo.AddComponent(entity, new NetworkTarget
    {
        Value = desc.Location,
        Timestamp = desc.Timestamp
    });
    
    Console.WriteLine($"[EntityStateTranslator] Created GHOST entity {entity.Index} from network ID {desc.EntityId}");
    
    return entity;
}
```

#### 2B: Update FindEntityByNetworkId to Include Ghosts

**Current code (line 81):** `.IncludeConstructing()`  
**Required:** `.IncludeAll()` or `.WithLifecycle(EntityLifecycle.Ghost)`

```csharp
private Entity FindEntityByNetworkId(long networkId, ISimulationView view)
{
    if (_networkIdToEntity.TryGetValue(networkId, out var entity) && view.IsAlive(entity))
    {
        return entity;
    }
    
    // ★ CHANGED: Include Ghost entities in search
    var query = view.Query()
        .With<NetworkIdentity>()
        .IncludeAll()  // Include all lifecycle states
        .Build();
    
    foreach(var e in query)
    {
        var comp = view.GetComponentRO<NetworkIdentity>(e);
        if (comp.Value == networkId)
        {
            _networkIdToEntity[networkId] = e;
            _entityToNetworkId[e] = networkId;
            return e;
        }
    }
    
    // Not found - cleanup stale mapping
    if (_networkIdToEntity.ContainsKey(networkId))
    {
        _networkIdToEntity.Remove(networkId);
    }
    
    return Entity.Null;
}
```

**Reference:** Implementation Spec Section 6.1.1 (EntityStateTranslator Refactoring)

**Tests Required:**
- ✅ Unit test: EntityState arrives first → Ghost entity created
- ✅ Unit test: Ghost entity has Position and Velocity from packet
- ✅ Unit test: Ghost entity has NetworkIdentity set correctly
- ✅ Unit test: FindEntityByNetworkId finds Ghost entities
- ✅ Integration test: Ghost entity excluded from game logic queries
- ✅ Integration test: Ghost → Constructing transition preserves Position

---

### Task 3: Complete EntityMasterTranslator Implementation

**File:** `ModuleHost.Core/Network/Translators/EntityMasterTranslator.cs` (REFACTOR)

**Description:**  
Complete the translator to handle EntityMaster descriptor ingress and add NetworkSpawnRequest components.

**Current Problem (Lines 45-52):**
```csharp
if (!_networkIdToEntity.TryGetValue(desc.EntityId, out var entity)) {
    // Create entity
    // But usually EntityState creates it with Position/Velocity.
    // Master just has Meta.
    // We'll skip creation logic for now...
    // ❌ NO IMPLEMENTATION
}
```

**Required Implementation:**

```csharp
public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
{
    // Cast for direct repository access
    var repo = view as EntityRepository;
    if (repo == null)
    {
        throw new InvalidOperationException(
            "EntityMasterTranslator requires direct EntityRepository access. " +
            "NetworkGateway must run with ExecutionPolicy.Synchronous().");
    }
    
    foreach (var sample in reader.TakeSamples())
    {
        if (sample.InstanceState == DdsInstanceState.NotAliveDisposed)
        {
            HandleDisposal(sample, cmd, view);
            continue;
        }
        
        if (sample.Data is not EntityMasterDescriptor desc)
        {
            if (sample.InstanceState == DdsInstanceState.Alive)
                Console.Error.WriteLine($"[EntityMasterTranslator] Unexpected sample type: {sample.Data?.GetType().Name}");
            continue;
        }
        
        // Check if entity already exists (could be Ghost from EntityState)
        Entity entity;
        bool isNewEntity = false;
        
        if (!_networkIdToEntity.TryGetValue(desc.EntityId, out entity) || !view.IsAlive(entity))
        {
            // Entity doesn't exist - create it directly (Master-first scenario)
            entity = repo.CreateEntity();
            isNewEntity = true;
            
            // Set NetworkIdentity
            repo.AddComponent(entity, new NetworkIdentity { Value = desc.EntityId });
            
            // Add to mapping
            _networkIdToEntity[desc.EntityId] = entity;
            
            Console.WriteLine($"[EntityMasterTranslator] Created entity {entity.Index} from EntityMaster (network ID {desc.EntityId})");
        }
        else
        {
            Console.WriteLine($"[EntityMasterTranslator] Found existing entity {entity.Index} for network ID {desc.EntityId}");
        }
        
        // Set or update NetworkOwnership
        repo.AddOrSetComponent(entity, new NetworkOwnership
        {
            PrimaryOwnerId = desc.OwnerId,
            LocalNodeId = _localNodeId
        });
        
        // Ensure DescriptorOwnership exists
        if (!repo.HasManagedComponent<DescriptorOwnership>(entity))
        {
            repo.AddManagedComponent(entity, new DescriptorOwnership());
        }
        
        // ★ KEY PART: Add NetworkSpawnRequest for NetworkSpawnerSystem to process
        // This component tells the spawner:
        // - What DIS type this entity is (for TKB template lookup)
        // - Whether to use reliable init mode
        // - What the primary owner is
        repo.AddComponent(entity, new NetworkSpawnRequest
        {
            DisType = desc.Type,
            PrimaryOwnerId = desc.OwnerId,
            Flags = desc.Flags,
            NetworkEntityId = desc.EntityId
        });
        
        Console.WriteLine($"[EntityMasterTranslator] Added NetworkSpawnRequest for entity {entity.Index} (Type: {desc.Type.Kind}, Flags: {desc.Flags})");
    }
}
```

**Helper Method Update:**
```csharp
private void HandleDisposal(IDataSample sample, IEntityCommandBuffer cmd, ISimulationView view)
{
    long entityId = sample.EntityId;
    if (entityId == 0 && sample.Data is EntityMasterDescriptor desc)
    {
        entityId = desc.EntityId;
    }
    
    if (entityId == 0)
    {
        Console.Error.WriteLine("[EntityMasterTranslator] Cannot handle disposal - no entity ID");
        return;
    }
    
    if (_networkIdToEntity.TryGetValue(entityId, out var entity))
    {
        Console.WriteLine($"[EntityMaster] Disposed {entityId}. Destroying entity {entity.Index}.");
        cmd.DestroyEntity(entity);
        _networkIdToEntity.Remove(entityId);
    }
}
```

**Reference:** Implementation Spec Section 6.1.2 (EntityMasterTranslator), Analysis Summary "Critical Issue #2"

**Tests Required:**
- ✅ Unit test: EntityMaster arrives first → Entity created directly
- ✅ Unit test: EntityMaster after Ghost → Entity found, not duplicated
- ✅ Unit test: NetworkSpawnRequest component added with correct data
- ✅ Unit test: NetworkOwnership set correctly
- ✅ Unit test: Disposal removes entity from mapping and destroys entity
- ✅ Integration test: Master-first path creates entity without Ghost state
- ✅ Integration test: Ghost promotion path (State → Master)

---

### Task 4: Implement NetworkSpawnerSystem

**File:** `ModuleHost.Core/Network/Systems/NetworkSpawnerSystem.cs` (NEW FILE)

**Description:**  
Create the system that bridges Network and ELM. It processes NetworkSpawnRequest components, applies TKB templates, determines ownership, and calls ELM's BeginConstruction.

**Full Implementation:**

```csharp
using System;
using Fdp.Kernel;
using Fdp.Kernel.Tkb;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network.Systems
{
    /// <summary>
    /// System that processes NetworkSpawnRequest components and coordinates
    /// entity spawning between the network layer and Entity Lifecycle Manager (ELM).
    /// 
    /// Responsibilities:
    /// - Apply TKB templates based on DIS entity type
    /// - Determine descriptor ownership via strategy pattern
    /// - Promote Ghost entities to Constructing state
    /// - Call ELM.BeginConstruction() to start distributed construction
    /// - Handle reliable vs fast initialization modes
    /// </summary>
    public class NetworkSpawnerSystem
    {
        private readonly ITkbDatabase _tkbDatabase;
        private readonly EntityLifecycleModule _elm;
        private readonly IOwnershipDistributionStrategy _ownershipStrategy;
        private readonly int _localNodeId;
        
        public NetworkSpawnerSystem(
            ITkbDatabase tkbDatabase,
            EntityLifecycleModule elm,
            IOwnershipDistributionStrategy ownershipStrategy,
            int localNodeId)
        {
            _tkbDatabase = tkbDatabase ?? throw new ArgumentNullException(nameof(tkbDatabase));
            _elm = elm ?? throw new ArgumentNullException(nameof(elm));
            _ownershipStrategy = ownershipStrategy ?? throw new ArgumentNullException(nameof(ownershipStrategy));
            _localNodeId = localNodeId;
        }
        
        /// <summary>
        /// Execute the spawner system. Should run in INPUT or BEFORESYNC phase,
        /// after network ingress but before ELM lifecycle processing.
        /// </summary>
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Cast for direct repository access (spawner needs immediate mutations)
            var repo = view as EntityRepository;
            if (repo == null)
            {
                throw new InvalidOperationException(
                    "NetworkSpawnerSystem requires EntityRepository access.");
            }
            
            // Query entities with NetworkSpawnRequest (transient component)
            var query = repo.Query()
                .With<NetworkSpawnRequest>()
                .IncludeAll()  // Include Ghost entities
                .Build();
            
            foreach (var entity in query)
            {
                var request = repo.GetComponentRO<NetworkSpawnRequest>(entity);
                
                try
                {
                    ProcessSpawnRequest(repo, entity, request);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NetworkSpawnerSystem] Error processing entity {entity.Index}: {ex.Message}");
                }
                finally
                {
                    // Remove transient component (consumed)
                    repo.RemoveComponent<NetworkSpawnRequest>(entity);
                }
            }
        }
        
        private void ProcessSpawnRequest(EntityRepository repo, Entity entity, NetworkSpawnRequest request)
        {
            var currentState = repo.GetLifecycleState(entity);
            bool wasGhost = (currentState == EntityLifecycle.Ghost);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Processing entity {entity.Index} (State: {currentState}, Type: {request.DisType.Kind})");
            
            // 1. Get TKB template
            var template = _tkbDatabase.GetTemplateByEntityType(request.DisType);
            if (template == null)
            {
                Console.Error.WriteLine($"[NetworkSpawnerSystem] No TKB template found for entity type {request.DisType.Kind}. Skipping entity {entity.Index}.");
                return;
            }
            
            // 2. Apply TKB template
            // If entity was Ghost, preserve existing components (Position from EntityState)
            // If entity is new (Master-first), apply template normally
            bool preserveExisting = wasGhost;
            template.ApplyTo(repo, entity, preserveExisting);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Applied TKB template '{template.Name}' to entity {entity.Index} (preserveExisting: {preserveExisting})");
            
            // 3. Determine partial ownership using strategy
            DetermineDescriptorOwnership(repo, entity, request);
            
            // 4. Promote to Constructing state (if Ghost or new)
            if (currentState != EntityLifecycle.Constructing)
            {
                repo.SetLifecycleState(entity, EntityLifecycle.Constructing);
                Console.WriteLine($"[NetworkSpawnerSystem] Promoted entity {entity.Index} from {currentState} to Constructing");
            }
            
            // 5. Begin ELM construction
            // The TypeId for ELM should be derived from template name (hash) or DIS type
            // For now, use a simple hash of template name
            long elmTypeId = HashTemplateName(template.Name);
            
            _elm.BeginConstruction(entity, elmTypeId);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Called ELM.BeginConstruction for entity {entity.Index} (TypeId: {elmTypeId})");
            
            // 6. Handle reliable init mode
            if (request.Flags.HasFlag(MasterFlags.ReliableInit))
            {
                // Add PendingNetworkAck tag - NetworkGateway will wait for peer ACKs
                repo.AddComponent(entity, new PendingNetworkAck());
                Console.WriteLine($"[NetworkSpawnerSystem] Entity {entity.Index} marked for reliable init");
            }
        }
        
        private void DetermineDescriptorOwnership(EntityRepository repo, Entity entity, NetworkSpawnRequest request)
        {
            // Get or create DescriptorOwnership component
            DescriptorOwnership descOwnership;
            if (repo.HasManagedComponent<DescriptorOwnership>(entity))
            {
                // Clone for mutation
                var existing = repo.GetManagedComponentRO<DescriptorOwnership>(entity);
                descOwnership = new DescriptorOwnership 
                { 
                    Map = new System.Collections.Generic.Dictionary<long, int>(existing.Map) 
                };
            }
            else
            {
                descOwnership = new DescriptorOwnership();
            }
            
            // Determine ownership for each descriptor type
            // For now, we handle the standard descriptors: EntityState, EntityMaster, WeaponState
            var descriptorTypeIds = new[] 
            { 
                NetworkConstants.ENTITY_MASTER_DESCRIPTOR_ID,
                NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID,
                NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID 
            };
            
            foreach (var descriptorTypeId in descriptorTypeIds)
            {
                // Ask strategy for initial owner (instanceId = 0 for now, multi-instance deferred)
                int? strategyOwner = _ownershipStrategy.GetInitialOwner(
                    descriptorTypeId,
                    request.DisType,
                    request.PrimaryOwnerId,
                    instanceId: 0
                );
                
                int owner = strategyOwner ?? request.PrimaryOwnerId;
                
                // Only populate map if different from primary owner (saves memory)
                if (owner != request.PrimaryOwnerId)
                {
                    long key = OwnershipExtensions.PackKey(descriptorTypeId, 0);
                    descOwnership.Map[key] = owner;
                    
                    Console.WriteLine($"[NetworkSpawnerSystem] Entity {entity.Index}: Descriptor {descriptorTypeId} owned by node {owner} (partial ownership)");
                }
            }
            
            // Update component
            repo.SetManagedComponent(entity, descOwnership);
        }
        
        private long HashTemplateName(string templateName)
        {
            // Simple hash - in production, use stable hash function
            // For now, use GetHashCode (sufficient for demo)
            return (long)templateName.GetHashCode();
        }
    }
}
```

**Reference:** Implementation Spec Section 6.2 (NetworkSpawnerSystem), Analysis Summary "Critical Issue #3"

**Tests Required:**
- ✅ Unit test: Ghost entity → Template applied with preserveExisting=true
- ✅ Unit test: New entity (Master-first) → Template applied with preserveExisting=false
- ✅ Unit test: Strategy returns null → PrimaryOwnerId used
- ✅ Unit test: Strategy returns specific node → DescriptorOwnership map populated
- ✅ Unit test: ReliableInit flag → PendingNetworkAck added
- ✅ Unit test: Non-ReliableInit flag → PendingNetworkAck NOT added
- ✅ Unit test: ELM.BeginConstruction called with correct TypeId
- ✅ Unit test: NetworkSpawnRequest removed after processing
- ✅ Integration test: Full Ghost→Master→Spawner→Constructing flow
- ✅ Integration test: Position preserved from Ghost after template application

---

### Task 5: Update OwnershipUpdateTranslator - Emit Events

**File:** `ModuleHost.Core/Network/Translators/OwnershipUpdateTranslator.cs` (UPDATE)

**Description:**  
Update the translator to emit `DescriptorAuthorityChanged` events and add `ForceNetworkPublish` component.

**Current Problem (Lines 76-109):**
- Updates DescriptorOwnership.Map ✅
- Logs ownership changes ✅
- Does NOT emit events ❌
- Does NOT add ForceNetworkPublish ❌

**Required Changes:**

Find the section around lines 76-109 where ownership is updated and add event emission:

```csharp
// Inside PollIngress, after updating DescriptorOwnership.Map:

// ... existing code that updates descOwnership.Map ...

cmd.SetManagedComponent(entity, descOwnership);

// ★ NEW: Emit DescriptorAuthorityChanged event
var ownership = view.GetComponentRO<NetworkOwnership>(entity);
bool isNowOwner = (update.NewOwner == ownership.LocalNodeId);
bool wasOwner = (currentOwner == ownership.LocalNodeId);

if (isNowOwner != wasOwner)
{
    // Ownership transition occurred
    var evt = new DescriptorAuthorityChanged
    {
        Entity = entity,
        DescriptorTypeId = update.DescrTypeId,
        IsNowOwner = isNowOwner,
        NewOwnerId = update.NewOwner
    };
    
    cmd.EmitEvent(evt);
    
    Console.WriteLine($"[Ownership] Entity {entity.Index} descriptor {update.DescrTypeId}: " +
        $"{(isNowOwner ? "ACQUIRED" : "LOST")} ownership (new owner: {update.NewOwner})");
}

// ★ NEW: Add ForceNetworkPublish if we became owner (SST confirmation write)
if (isNowOwner)
{
    cmd.SetComponent(entity, new ForceNetworkPublish());
    Console.WriteLine($"[Ownership] Entity {entity.Index}: Force publish scheduled for confirmation");
}
```

**Reference:** Implementation Spec Section 6.3 (Dynamic Ownership), Analysis Summary "Issue #5"

**Tests Required:**
- ✅ Unit test: Ownership transfer → Event emitted with IsNowOwner=true
- ✅ Unit test: Ownership lost → Event emitted with IsNowOwner=false
- ✅ Unit test: New owner → ForceNetworkPublish component added
- ✅ Unit test: Lost ownership → ForceNetworkPublish NOT added
- ✅ Integration test: Module can subscribe to DescriptorAuthorityChanged
- ✅ Integration test: ForceNetworkPublish triggers immediate egress

---

### Task 6: Add TkbTemplate.ApplyTo Overload with preserveExisting

**File:** `FDP/Fdp.Kernel/Tkb/TkbTemplate.cs` (UPDATE)

**Description:**  
Extend the TkbTemplate.ApplyTo method to support a `preserveExisting` parameter that preserves existing components when promoting Ghost entities.

**Current Signature:**
```csharp
public void ApplyTo(EntityRepository repo, Entity entity)
```

**Required Addition:**
```csharp
public void ApplyTo(EntityRepository repo, Entity entity, bool preserveExisting = false)
{
    foreach (var applicator in _applicators)
    {
        applicator(repo, entity, preserveExisting);
    }
}
```

**AND Update Applicator Signature:**

The internal applicator delegate needs to accept the `preserveExisting` parameter. This may require updating the applicator registration logic.

**Example Applicator Logic:**
```csharp
// When registering applicators:
_applicators.Add((repo, entity, preserve) =>
{
    if (preserve && repo.HasComponent<Position>(entity))
    {
        // Skip - keep existing Position from network Ghost
        return;
    }
    
    repo.AddComponent(entity, new Position { Value = templateValue });
});
```

**⚠️ IMPORTANT:** This change affects Fdp.Kernel. Ensure backward compatibility.

**Reference:** Implementation Spec Section 6.2.4 (TKB Template Preservation)

**Tests Required:**
- ✅ Unit test: ApplyTo with preserveExisting=false → Overwrites existing components
- ✅ Unit test: ApplyTo with preserveExisting=true → Keeps existing components
- ✅ Unit test: ApplyTo with preserveExisting=true → Adds missing components
- ✅ Integration test: Ghost Position preserved after template application

---

## 🧪 Testing Requirements

### ⚠️ CRITICAL: Test Quality Standards

Based on BATCH-12 feedback, your tests MUST validate actual behavior, not just compilation.

**❌ UNACCEPTABLE TEST EXAMPLES:**
```csharp
[Fact]
public void NetworkSpawnerSystem_CanBeCreated()
{
    var system = new NetworkSpawnerSystem(...);
    Assert.NotNull(system);  // ❌ USELESS TEST
}

[Fact]
public void ProcessSpawnRequest_DoesNotThrow()
{
    system.Execute(...);
    Assert.True(true);  // ❌ TESTS NOTHING
}
```

**✅ ACCEPTABLE TEST EXAMPLES:**
```csharp
[Fact]
public void EntityStateTranslator_StateBeforeMaster_CreatesGhostWithCorrectPosition()
{
    // Arrange
    var translator = CreateTranslator();
    var desc = new EntityStateDescriptor 
    { 
        EntityId = 123, 
        Location = new Vector3(10, 20, 30) 
    };
    
    // Act
    translator.PollIngress(mockReader, cmd, repo);
    
    // Assert
    var entity = GetEntityByNetworkId(123);
    Assert.Equal(EntityLifecycle.Ghost, repo.GetLifecycleState(entity));
    Assert.True(repo.HasComponent<Position>(entity));
    
    var pos = repo.GetComponentRO<Position>(entity);
    Assert.Equal(10, pos.Value.X);
    Assert.Equal(20, pos.Value.Y);
    Assert.Equal(30, pos.Value.Z);
}
```

### Unit Test Requirements

Create test file: `ModuleHost.Core.Tests/Network/NetworkELMIntegrationTests.cs`

**Minimum Test Count: 30 tests** (more is better)

**Test Categories:**

1. **EntityStateTranslator - Ghost Creation** (6 tests minimum)
   - State arrives first → Ghost created
   - Ghost has correct lifecycle state
   - Ghost has Position/Velocity from packet
   - Ghost has NetworkIdentity
   - Ghost excluded from standard queries
   - FindEntityByNetworkId finds Ghosts

2. **EntityMasterTranslator - Complete Implementation** (7 tests minimum)
   - Master arrives first → Entity created directly
   - Master after Ghost → Entity found, not duplicated
   - NetworkSpawnRequest added with correct data
   - NetworkOwnership set correctly
   - Disposal removes entity and cleans mapping
   - Multiple descriptors handled correctly
   - Error handling for invalid data

3. **NetworkSpawnerSystem** (10 tests minimum)
   - Ghost promotion preserves Position
   - Master-first applies template normally
   - Strategy null → Uses PrimaryOwner
   - Strategy specific → Map populated
   - ReliableInit → PendingNetworkAck added
   - Fast mode → PendingNetworkAck NOT added
   - ELM.BeginConstruction called
   - NetworkSpawnRequest removed after processing
   - TKB template applied correctly
   - Error handling for missing template

4. **OwnershipUpdateTranslator - Events** (5 tests minimum)
   - Ownership acquired → Event with IsNowOwner=true
   - Ownership lost → Event with IsNowOwner=false
   - New owner → ForceNetworkPublish added
   - Lost ownership → ForceNetworkPublish NOT added
   - No transition → No event emitted

5. **TkbTemplate.ApplyTo** (4 tests minimum)
   - preserveExisting=false → Overwrites
   - preserveExisting=true → Keeps existing
   - preserveExisting=true → Adds missing
   - Backward compatibility (no parameter → false)

### Integration Test Requirements

Create test file: `ModuleHost.Core.Tests/Network/NetworkELMIntegrationScenarios.cs`

**Minimum Scenario Count: 5 comprehensive scenarios**

**Required Scenarios:**

1. **Scenario: State-First Creation (Ghost Path)**
   - EntityState arrives → Ghost created with Position
   - EntityMaster arrives → NetworkSpawnRequest added
   - NetworkSpawnerSystem executes → TKB applied, Position preserved
   - ELM processes → Entity becomes Active
   - Verify: Position from Ghost retained

2. **Scenario: Master-First Creation (Ideal Path)**
   - EntityMaster arrives → Entity created directly
   - NetworkSpawnRequest processed → TKB applied
   - EntityState arrives → Position updated
   - Verify: Entity never in Ghost state

3. **Scenario: Reliable Initialization**
   - EntityMaster with ReliableInit flag
   - NetworkSpawnerSystem adds PendingNetworkAck
   - Verify: ELM construction begins
   - Verify: Entity awaits network confirmation

4. **Scenario: Partial Ownership**
   - Custom strategy assigns WeaponState to different node
   - NetworkSpawnerSystem applies strategy
   - Verify: DescriptorOwnership.Map contains entry
   - Verify: OwnsDescriptor returns correct owner

5. **Scenario: Ownership Transfer**
   - Entity exists with initial ownership
   - OwnershipUpdate received
   - Event emitted
   - ForceNetworkPublish added
   - Verify: Module can react to event

### Test Execution Requirements

- ✅ All tests must pass
- ✅ No test warnings or skipped tests
- ✅ Tests must run in < 5 seconds total
- ✅ Tests must not depend on execution order
- ✅ Tests must clean up after themselves

---

## 📊 Report Requirements

### ⚠️ MANDATORY: Use Full Template Structure

Your report must follow the template structure completely. Don't skip sections!

**Required Sections:**
1. ✅ Completion Status (all tasks checked off)
2. ✅ Test Results (full output, not just "passing")
3. ✅ Implementation Summary (files added/modified with details)
4. ✅ Implementation Details (for EACH task - approach, decisions, challenges, tests)
5. ✅ Deviations & Improvements (if any, with full rationale)
6. ✅ Performance Observations (any concerns noted)
7. ✅ Integration Notes (how systems fit together)
8. ✅ Known Issues & Limitations (be honest)
9. ✅ Dependencies (what this batch depends on)
10. ✅ Documentation status
11. ✅ Pre-submission checklist

### Specific Questions You MUST Answer

**Question 1:** How did you handle the transition from Ghost to Constructing? What components needed special handling?

**Question 2:** What edge cases did you discover during testing? How did you handle them?

**Question 3:** Did you encounter any issues with the direct repository access pattern? How did you ensure thread safety?

**Question 4:** How does the TKB template preservation work? Give a concrete example of a component that was preserved vs one that was added.

**Question 5:** What happens if TKB template is not found for an entity type? How does the system behave?

**Question 6:** Did you find any architectural issues or improvement opportunities? If so, what?

**Question 7:** What was the most challenging part of this batch? How did you overcome it?

**Question 8:** How confident are you that the integration tests cover the critical paths? What additional tests would you add if you had more time?

---

## 🎯 Success Criteria

This batch is considered **DONE** when:

1. ✅ All 6 tasks implemented and working
2. ✅ EntityLifecycle.Ghost state added to Fdp.Kernel
3. ✅ EntityStateTranslator creates Ghost entities correctly
4. ✅ EntityMasterTranslator complete (no stub code remaining)
5. ✅ NetworkSpawnerSystem fully functional
6. ✅ OwnershipUpdateTranslator emits events
7. ✅ TkbTemplate supports preserveExisting
8. ✅ Minimum 30 unit tests passing (more is better)
9. ✅ Minimum 5 integration scenarios passing
10. ✅ All tests validate actual behavior (not just compilation)
11. ✅ Comprehensive report submitted (answers all questions)
12. ✅ No compiler warnings
13. ✅ Code follows existing patterns
14. ✅ XML documentation on all public methods

---

## ⚠️ Common Pitfalls to Avoid

1. **❌ Using cmd.CreateEntity() in translators** → Use repo.CreateEntity() for immediate ID
2. **❌ Forgetting to cast ISimulationView to EntityRepository** → Add null check
3. **❌ Not including Ghost in queries** → Use .IncludeAll() or .WithLifecycle()
4. **❌ Overwriting Ghost Position with TKB template** → Use preserveExisting=true
5. **❌ Not removing NetworkSpawnRequest** → Must remove after processing (transient)
6. **❌ Not emitting events** → OwnershipUpdate MUST emit DescriptorAuthorityChanged
7. **❌ Shallow tests** → Test actual behavior, not just "does it compile"
8. **❌ Minimal report** → Use full template, answer all questions thoroughly

---

## 📚 Reference Materials

### Must Read (In Order)
1. `docs/ModuleHost-network-ELM-implementation-spec.md` - Sections 5, 6
2. `docs/ModuleHost-network-ELM-analysis-summary.md` - Critical Issues #1, #2, #3
3. `.dev-workstream/reviews/BATCH-12-REVIEW.md` - Learn from feedback

### Code to Study
- Existing `EntityStateTranslator.cs` - Learn the pattern
- Existing `LifecycleSystem.cs` (ELM) - Understand construction flow
- Existing `OwnershipUpdateTranslator.cs` - See current implementation

### Design Decisions Reference
- Implementation Spec "Decision 1: Ghost State Protocol"
- Implementation Spec "Decision 2: Direct Repository Access"
- Implementation Spec "Section 6.2: NetworkSpawnerSystem"

---

## 🚀 Getting Started

### Recommended Task Order
1. **Task 1:** Add Ghost state (easiest, unblocks others)
2. **Task 6:** TkbTemplate preserveExisting (needed by spawner)
3. **Task 2:** Refactor EntityStateTranslator (foundation)
4. **Task 3:** Complete EntityMasterTranslator (creates spawn requests)
5. **Task 4:** Implement NetworkSpawnerSystem (core logic)
6. **Task 5:** Update OwnershipUpdateTranslator (events)
7. **Tests:** Write unit tests as you go, integration tests at end

### Development Approach
- **TDD Recommended:** Write failing test first, then implement
- **Commit frequently:** One commit per task minimum
- **Test incrementally:** Don't wait until the end to run tests
- **Document as you go:** XML comments while code is fresh in mind

---

## ❓ Questions or Blockers?

If you encounter questions or blockers:

1. **Check the spec first** - Most answers are in Implementation Spec Section 6
2. **Check existing code** - Pattern probably exists elsewhere
3. **Create questions file** - `questions/BATCH-13-QUESTIONS.md`
4. **Document what you tried** - Show your thinking
5. **Continue with other tasks** - Don't block entirely on one issue

---

## 💬 Final Notes from Development Lead

This batch is the **heart of the Network-ELM integration**. It's more complex than BATCH-12, but also more interesting. You're building the bridge between two critical systems.

**What I'm looking for:**
- **Deep understanding** of the problem (not just copy-paste from spec)
- **Thoughtful testing** that validates correctness (not just coverage)
- **Clear communication** in your report (help me understand your thinking)
- **Quality code** that others can maintain (not just "it works")

**Take your time.** This is 8-10 hours of work for a reason. Better to do it right than fast.

**Good luck!** This is challenging but important work. 🚀

---

**Batch Created:** 2026-01-11  
**Development Lead:** AI Assistant (Development Manager)  
**Dependencies:** BATCH-12 (Foundation Layer)  
**Next Batch:** BATCH-14 (Integration & Testing)
