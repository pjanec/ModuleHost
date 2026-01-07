# BATCH 06: Entity Lifecycle Manager (ELM)

**Batch ID:** BATCH-06  
**Phase:** Advanced - Distributed Entity Lifecycle  
**Priority:** MEDIUM (P2)  
**Estimated Effort:** 1 week  
**Dependencies:** BATCH-05 (needs ExecutionPolicy)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 6 (Entity Lifecycle Manager)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 06 section
4. **SST Rules:** `../../docs/ModuleHost-TODO.md` - Search for "bdc-sst-rules" section
5. **Current Implementation:** Review FDP EntityLifecycle and Event system

---

## 🎯 Batch Objectives

### Primary Goal
Coordinate atomic entity creation/destruction across multiple distributed modules using an event-based ACK protocol.

### Success Criteria
- ✅ Dark Construction: entities staged until all modules initialize them
- ✅ Coordinated Teardown: cleanup phase before deletion
- ✅ Event protocol working (ConstructionOrder/ACK, DestructionOrder/ACK)
- ✅ Query filtering by lifecycle state
- ✅ Multi-module scenarios validated
- ✅ All tests passing

### Why This Matters
In distributed simulations, no single module knows everything about an entity. Physics sets up rigid bodies, AI creates behavior trees, Network registers for sync. ELM ensures an entity doesn't become "Active" until ALL interested modules have initialized it, preventing half-initialized state and pop-in artifacts.

---

## 📋 Tasks

### Task 6.1: Lifecycle Event Definitions ⭐

**Objective:** Define the event protocol for coordinated entity lifecycle.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 6, Section 6.2 - "Data Structures"

**What to Create:**

```csharp
// File: ModuleHost.Core/ELM/LifecycleEvents.cs (NEW)

using System;
using Fdp.Kernel;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Published when an entity begins construction.
    /// Modules should initialize their components and respond with ConstructionAck.
    /// </summary>
    [EventId(9001)]
    public struct ConstructionOrder
    {
        /// <summary>
        /// Entity being constructed.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Entity type ID (for modules to decide if they care).
        /// </summary>
        public int TypeId;
        
        /// <summary>
        /// Frame number when construction started.
        /// </summary>
        public uint FrameNumber;
        
        /// <summary>
        /// Optional: Initiating module ID (who spawned it).
        /// </summary>
        public int InitiatorModuleId;
    }
    
    /// <summary>
    /// Response from a module indicating it has initialized the entity.
    /// </summary>
    [EventId(9002)]
    public struct ConstructionAck
    {
        /// <summary>
        /// Entity that was initialized.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Module ID that completed initialization.
        /// </summary>
        public int ModuleId;
        
        /// <summary>
        /// Optional: Success flag (allows modules to report initialization failure).
        /// </summary>
        public bool Success;
        
        /// <summary>
        /// Optional: Error message if Success == false.
        /// </summary>
        public string? ErrorMessage;
    }
    
    /// <summary>
    /// Published when an entity begins teardown.
    /// Modules should cleanup their state and respond with DestructionAck.
    /// </summary>
    [EventId(9003)]
    public struct DestructionOrder
    {
        /// <summary>
        /// Entity being destroyed.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Frame number when destruction started.
        /// </summary>
        public uint FrameNumber;
        
        /// <summary>
        /// Optional: Reason for destruction (debug info).
        /// </summary>
        public string? Reason;
    }
    
    /// <summary>
    /// Response from a module indicating it has cleaned up the entity.
    /// </summary>
    [EventId(9004)]
    public struct DestructionAck
    {
        /// <summary>
        /// Entity that was cleaned up.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Module ID that completed cleanup.
        /// </summary>
        public int ModuleId;
        
        /// <summary>
        /// Optional: Success flag.
        /// </summary>
        public bool Success;
    }
}
```

**Acceptance Criteria:**
- [ ] Four event structs defined with [EventId] attributes
- [ ] Event IDs don't conflict (9001-9004 reserved for ELM)
- [ ] All fields documented
- [ ] Optional fields use nullable types
- [ ] Serialization-friendly (no complex types)

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/LifecycleEventsTests.cs

using Xunit;
using ModuleHost.Core.ELM;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests
{
    public class LifecycleEventsTests
    {
        [Fact]
        public void ConstructionOrder_EventIdIsUnique()
        {
            // Verify event ID is registered
            var id = EventType<ConstructionOrder>.Id;
            Assert.Equal(9001, id);
        }
        
        [Fact]
        public void ConstructionAck_EventIdIsUnique()
        {
            var id = EventType<ConstructionAck>.Id;
            Assert.Equal(9002, id);
        }
        
        [Fact]
        public void DestructionOrder_EventIdIsUnique()
        {
            var id = EventType<DestructionOrder>.Id;
            Assert.Equal(9003, id);
        }
        
        [Fact]
        public void DestructionAck_EventIdIsUnique()
        {
            var id = EventType<DestructionAck>.Id;
            Assert.Equal(9004, id);
        }
        
        [Fact]
        public void ConstructionOrder_CanBePublished()
        {
            var bus = new FdpEventBus();
            var entity = new Entity(123);
            
            bus.Publish(new ConstructionOrder
            {
                Entity = entity,
                TypeId = 1,
                FrameNumber = 100
            });
            
            // Verify event is in current frame buffer
            var events = bus.GetReadBuffer<ConstructionOrder>();
            Assert.Contains(events, e => e.Entity.Id == 123);
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/ELM/LifecycleEvents.cs`
- [ ] New test file: `ModuleHost.Core.Tests/LifecycleEventsTests.cs`
- [ ] 5+ unit tests passing

---

### Task 6.2: EntityLifecycleModule Core ⭐⭐⭐

**Objective:** Implement the central coordinator module that tracks pending entities and manages ACK protocol.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 6, Section 6.2 - "ELM Module"

**What to Create:**

```csharp
// File: ModuleHost.Core/ELM/EntityLifecycleModule.cs (NEW)

using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Coordinates entity lifecycle across distributed modules.
    /// Ensures entities are fully initialized before becoming Active,
    /// and properly cleaned up before destruction.
    /// </summary>
    public class EntityLifecycleModule : IModule
    {
        public string Name => "EntityLifecycleManager";
        
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        // Reactive: listen for ACK events
        public IReadOnlyList<Type>? WatchEvents => new[]
        {
            typeof(ConstructionAck),
            typeof(DestructionAck)
        };
        
        public IReadOnlyList<Type>? WatchComponents => null;
        
        // === Configuration ===
        
        /// <summary>
        /// IDs of modules that participate in lifecycle coordination.
        /// Entity becomes Active when all modules ACK.
        /// </summary>
        private readonly HashSet<int> _participatingModuleIds;
        
        /// <summary>
        /// Timeout in frames before giving up on pending entity.
        /// </summary>
        private readonly int _timeoutFrames;
        
        // === State Tracking ===
        
        private readonly Dictionary<Entity, PendingConstruction> _pendingConstruction = new();
        private readonly Dictionary<Entity, PendingDestruction> _pendingDestruction = new();
        
        // === Statistics ===
        
        private int _totalConstructed;
        private int _totalDestructed;
        private int _timeouts;
        
        public EntityLifecycleModule(
            IEnumerable<int> participatingModuleIds,
            int timeoutFrames = 300) // 5 seconds at 60Hz
        {
            _participatingModuleIds = new HashSet<int>(participatingModuleIds);
            _timeoutFrames = timeoutFrames;
            
            if (_participatingModuleIds.Count == 0)
            {
                throw new ArgumentException(
                    "At least one participating module required", 
                    nameof(participatingModuleIds));
            }
        }
        
        public void RegisterSystems(ISystemRegistry registry)
        {
            registry.RegisterSystem(new LifecycleSystem(this));
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            // Main logic in LifecycleSystem
        }
        
        // === Public API ===
        
        /// <summary>
        /// Begins construction of a new entity.
        /// Publishes ConstructionOrder and tracks pending ACKs.
        /// </summary>
        public void BeginConstruction(Entity entity, int typeId, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (_pendingConstruction.ContainsKey(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity.Id} already in construction");
            }
            
            // Track pending state
            _pendingConstruction[entity] = new PendingConstruction
            {
                Entity = entity,
                TypeId = typeId,
                StartFrame = currentFrame,
                RemainingAcks = new HashSet<int>(_participatingModuleIds)
            };
            
            // Publish order event
            cmd.PublishEvent(new ConstructionOrder
            {
                Entity = entity,
                TypeId = typeId,
                FrameNumber = currentFrame
            });
        }
        
        /// <summary>
        /// Begins teardown of an entity.
        /// Publishes DestructionOrder and tracks pending ACKs.
        /// </summary>
        public void BeginDestruction(Entity entity, uint currentFrame, string? reason, IEntityCommandBuffer cmd)
        {
            if (_pendingDestruction.ContainsKey(entity))
            {
                return; // Already in teardown
            }
            
            _pendingDestruction[entity] = new PendingDestruction
            {
                Entity = entity,
                StartFrame = currentFrame,
                RemainingAcks = new HashSet<int>(_participatingModuleIds),
                Reason = reason
            };
            
            cmd.PublishEvent(new DestructionOrder
            {
                Entity = entity,
                FrameNumber = currentFrame,
                Reason = reason
            });
        }
        
        // === Internal Logic (called by LifecycleSystem) ===
        
        internal void ProcessConstructionAck(ConstructionAck ack, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (!_pendingConstruction.TryGetValue(ack.Entity, out var pending))
            {
                // ACK for non-pending entity (duplicate or late ACK)
                return;
            }
            
            if (!ack.Success)
            {
                // Module failed to initialize - abort construction
                Console.Error.WriteLine(
                    $"[ELM] Construction failed for {ack.Entity.Id}: {ack.ErrorMessage}");
                
                _pendingConstruction.Remove(ack.Entity);
                cmd.DestroyEntity(ack.Entity);
                return;
            }
            
            // Record ACK
            pending.RemainingAcks.Remove(ack.ModuleId);
            
            if (pending.RemainingAcks.Count == 0)
            {
                // All ACKs received - activate entity
                cmd.SetLifecycleState(ack.Entity, EntityLifecycle.Active);
                _pendingConstruction.Remove(ack.Entity);
                _totalConstructed++;
                
                Console.WriteLine(
                    $"[ELM] Entity {ack.Entity.Id} activated after {currentFrame - pending.StartFrame} frames");
            }
        }
        
        internal void ProcessDestructionAck(DestructionAck ack, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (!_pendingDestruction.TryGetValue(ack.Entity, out var pending))
            {
                return;
            }
            
            pending.RemainingAcks.Remove(ack.ModuleId);
            
            if (pending.RemainingAcks.Count == 0)
            {
                // All ACKs received - destroy entity
                cmd.DestroyEntity(ack.Entity);
                _pendingDestruction.Remove(ack.Entity);
                _totalDestructed++;
                
                Console.WriteLine(
                    $"[ELM] Entity {ack.Entity.Id} destroyed after {currentFrame - pending.StartFrame} frames");
            }
        }
        
        internal void CheckTimeouts(uint currentFrame, IEntityCommandBuffer cmd)
        {
            // Check construction timeouts
            var timedOutConstruction = new List<Entity>();
            foreach (var kvp in _pendingConstruction)
            {
                if (currentFrame - kvp.Value.StartFrame > _timeoutFrames)
                {
                    timedOutConstruction.Add(kvp.Key);
                }
            }
            
            foreach (var entity in timedOutConstruction)
            {
                var pending = _pendingConstruction[entity];
                Console.Error.WriteLine(
                    $"[ELM] Construction timeout for {entity.Id}. Missing ACKs from modules: {string.Join(", ", pending.RemainingAcks)}");
                
                _pendingConstruction.Remove(entity);
                cmd.DestroyEntity(entity);
                _timeouts++;
            }
            
            // Check destruction timeouts (similar logic)
            var timedOutDestruction = new List<Entity>();
            foreach (var kvp in _pendingDestruction)
            {
                if (currentFrame - kvp.Value.StartFrame > _timeoutFrames)
                {
                    timedOutDestruction.Add(kvp.Key);
                }
            }
            
            foreach (var entity in timedOutDestruction)
            {
                Console.Error.WriteLine(
                    $"[ELM] Destruction timeout for {entity.Id}. Forcing deletion.");
                
                _pendingDestruction.Remove(entity);
                cmd.DestroyEntity(entity);
                _timeouts++;
            }
        }
        
        // === Diagnostics ===
        
        public (int constructed, int destructed, int timeouts, int pending) GetStatistics()
        {
            return (_totalConstructed, _totalDestructed, _timeouts, 
                    _pendingConstruction.Count + _pendingDestruction.Count);
        }
    }
    
    // === Helper Classes ===
    
    internal class PendingConstruction
    {
        public Entity Entity;
        public int TypeId;
        public uint StartFrame;
        public HashSet<int> RemainingAcks = new();
    }
    
    internal class PendingDestruction
    {
        public Entity Entity;
        public uint StartFrame;
        public HashSet<int> RemainingAcks = new();
        public string? Reason;
    }
}
```

**Acceptance Criteria:**
- [ ] Module tracks pending construction/destruction
- [ ] ACK bitmask management working
- [ ] Timeout detection implemented
- [ ] Public API for beginning lifecycle transitions
- [ ] Statistics exposed for monitoring
- [ ] Error handling for failed initialization

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/EntityLifecycleModuleTests.cs

[Fact]
public void ELM_BeginConstruction_PublishesOrder()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2, 3 });
    var cmd = CreateCommandBuffer();
    var entity = new Entity(100);
    
    elm.BeginConstruction(entity, typeId: 1, currentFrame: 10, cmd);
    
    var events = cmd.GetPublishedEvents<ConstructionOrder>();
    Assert.Contains(events, e => e.Entity.Id == 100);
}

[Fact]
public void ELM_AllAcksReceived_ActivatesEntity()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2 });
    var cmd = CreateCommandBuffer();
    var entity = new Entity(100);
    
    elm.BeginConstruction(entity, 1, 10, cmd);
    
    // Module 1 ACKs
    elm.ProcessConstructionAck(new ConstructionAck
    {
        Entity = entity,
        ModuleId = 1,
        Success = true
    }, 11, cmd);
    
    // Not activated yet (module 2 pending)
    Assert.DoesNotContain(cmd.GetLifecycleStateChanges(), 
        c => c.Entity.Id == 100);
    
    // Module 2 ACKs
    elm.ProcessConstructionAck(new ConstructionAck
    {
        Entity = entity,
        ModuleId = 2,
        Success = true
    }, 12, cmd);
    
    // Now activated
    Assert.Contains(cmd.GetLifecycleStateChanges(),
        c => c.Entity.Id == 100 && c.NewState == EntityLifecycle.Active);
}

[Fact]
public void ELM_FailedAck_AbortsConstruction()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2 });
    var cmd = CreateCommandBuffer();
    var entity = new Entity(100);
    
    elm.BeginConstruction(entity, 1, 10, cmd);
    
    elm.ProcessConstructionAck(new ConstructionAck
    {
        Entity = entity,
        ModuleId = 1,
        Success = false,
        ErrorMessage = "Physics setup failed"
    }, 11, cmd);
    
    // Entity should be destroyed
    Assert.Contains(cmd.GetDestroyedEntities(), e => e.Id == 100);
}

[Fact]
public void ELM_Timeout_AbandonsConstruction()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2 }, timeoutFrames: 10);
    var cmd = CreateCommandBuffer();
    var entity = new Entity(100);
    
    elm.BeginConstruction(entity, 1, currentFrame: 0, cmd);
    
    // Module 1 ACKs, module 2 never responds
    elm.ProcessConstructionAck(new ConstructionAck
    {
        Entity = entity,
        ModuleId = 1,
        Success = true
    }, 5, cmd);
    
    // Run timeout check at frame 15
    elm.CheckTimeouts(currentFrame: 15, cmd);
    
    // Entity should be destroyed due to timeout
    Assert.Contains(cmd.GetDestroyedEntities(), e => e.Id == 100);
    
    var stats = elm.GetStatistics();
    Assert.Equal(1, stats.timeouts);
}

[Fact]
public void ELM_BeginDestruction_PublishesOrder()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2 });
    var cmd = CreateCommandBuffer();
    var entity = new Entity(100);
    
    elm.BeginDestruction(entity, currentFrame: 20, reason: "Test", cmd);
    
    var events = cmd.GetPublishedEvents<DestructionOrder>();
    Assert.Contains(events, e => e.Entity.Id == 100);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/ELM/EntityLifecycleModule.cs`
- [ ] New test file: `ModuleHost.Core.Tests/EntityLifecycleModuleTests.cs`
- [ ] 5+ unit tests passing

---

### Task 6.3: LifecycleSystem Implementation ⭐⭐

**Objective:** Create the system that processes lifecycle events each frame.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 6, Section 6.2 - "LifecycleSystem"

**What to Create:**

```csharp
// File: ModuleHost.Core/ELM/LifecycleSystem.cs (NEW)

using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Processes lifecycle events (ACKs) and manages entity state transitions.
    /// Runs in BeforeSync phase to ensure changes are visible to all modules.
    /// </summary>
    [UpdateInPhase(SystemPhase.BeforeSync)]
    public class LifecycleSystem : IModuleSystem
    {
        private readonly EntityLifecycleModule _manager;
        
        public LifecycleSystem(EntityLifecycleModule manager)
        {
            _manager = manager;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            var cmd = view.GetCommandBuffer();
            uint currentFrame = view.GlobalVersion;
            
            // Process construction ACKs
            foreach (var ack in view.GetEvents<ConstructionAck>())
            {
                _manager.ProcessConstructionAck(ack, currentFrame, cmd);
            }
            
            // Process destruction ACKs
            foreach (var ack in view.GetEvents<DestructionAck>())
            {
                _manager.ProcessDestructionAck(ack, currentFrame, cmd);
            }
            
            // Check for timeouts
            _manager.CheckTimeouts(currentFrame, cmd);
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Runs in BeforeSync phase
- [ ] Processes both construction and destruction ACKs
- [ ] Checks timeouts every frame
- [ ] Uses command buffer for all mutations

**Unit Tests:**

```csharp
[Fact]
public void LifecycleSystem_ProcessesAcks correctly()
{
    var elm = new EntityLifecycleModule(new[] { 1 });
    var system = new LifecycleSystem(elm);
    var view = CreateSimulationView();
    var entity = new Entity(100);
    
    // Begin construction
    elm.BeginConstruction(entity, 1, 10, view.GetCommandBuffer());
    
    // Publish ACK event
    view.PublishEvent(new ConstructionAck
    {
        Entity = entity,
        ModuleId = 1,
        Success = true
    });
    
    // Execute system
    system.Execute(view, 0.016f);
    
    // Verify entity activated
    Assert.Contains(view.GetCommandBuffer().GetLifecycleStateChanges(),
        c => c.Entity.Id == 100 && c.NewState == EntityLifecycle.Active);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/ELM/LifecycleSystem.cs`
- [ ] Tests in existing test file
- [ ] 1+ integration test passing

---

### Task 6.4: Query Lifecycle Filtering ⭐

**Objective:** Update EntityQuery to filter entities by lifecycle state.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 6, Section 6.2 - "Integration"

**Files to Modify:**

```csharp
// File: FDP/Fdp.Kernel/EntityQuery.cs

public class EntityQuery
{
    // Existing fields...
    
    private EntityLifecycle _lifecycleFilter = EntityLifecycle.Active; // DEFAULT
    
    /// <summary>
    /// Filter query to specific lifecycle state.
    /// Default: Active (excludes Constructing and TearDown).
    /// </summary>
    public EntityQuery WithLifecycle(EntityLifecycle state)
    {
        _lifecycleFilter = state;
        return this;
    }
    
    /// <summary>
    /// Include entities currently being constructed.
    /// Use when module needs to setup staging entities.
    /// </summary>
    public EntityQuery IncludeConstructing()
    {
        _lifecycleFilter = EntityLifecycle.Constructing;
        return this;
    }
    
    /// <summary>
    /// Include entities in teardown phase.
    /// Use when module needs to cleanup before destruction.
    /// </summary>
    public EntityQuery IncludeTearDown()
    {
        _lifecycleFilter = EntityLifecycle.TearDown;
        return this;
    }
    
    /// <summary>
    /// Include all entities regardless of lifecycle state.
    /// </summary>
    public EntityQuery IncludeAll()
    {
        _lifecycleFilter = EntityLifecycle.All; // Special value
        return this;
    }
    
    // Modify Build() to apply lifecycle filter
    public IEnumerable<Entity> Build()
    {
        // ... existing filtering ...
        
        foreach (var entity in candidates)
        {
            if (_lifecycleFilter != EntityLifecycle.All)
            {
                var currentState = GetEntityLifecycleState(entity);
                if (currentState != _lifecycleFilter)
                    continue;
            }
            
            yield return entity;
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Default queries only return Active entities
- [ ] `.WithLifecycle()` method added
- [ ] `.IncludeConstructing()` helper added
- [ ] `.IncludeTearDown()` helper added
- [ ] `.IncludeAll()` bypass added
- [ ] Backward compatible (existing queries unchanged)

**Unit Tests:**

```csharp
// File: FDP/Fdp.Tests/EntityQueryLifecycleTests.cs

[Fact]
public void EntityQuery_DefaultFilter_OnlyActive()
{
    var repo = CreateRepository();
    
    var entity1 = repo.CreateEntity(); // Active
    var entity2 = repo.CreateEntity();
    repo.SetLifecycleState(entity2, EntityLifecycle.Constructing);
    
    var query = repo.Query().Build();
    
    Assert.Contains(entity1, query);
    Assert.DoesNotContain(entity2, query); // Filtered out
}

[Fact]
public void EntityQuery_IncludeConstructing_ReturnsStaging()
{
    var repo = CreateRepository();
    
    var entity1 = repo.CreateEntity();
    repo.SetLifecycleState(entity1, EntityLifecycle.Constructing);
    
    var query = repo.Query().IncludeConstructing().Build();
    
    Assert.Contains(entity1, query);
}

[Fact]
public void EntityQuery_IncludeAll_ReturnsEverything()
{
    var repo = CreateRepository();
    
    var active = repo.CreateEntity();
    var constructing = repo.CreateEntity();
    var tearDown = repo.CreateEntity();
    
    repo.SetLifecycleState(constructing, EntityLifecycle.Constructing);
    repo.SetLifecycleState(tearDown, EntityLifecycle.TearDown);
    
    var query = repo.Query().IncludeAll().Build().ToList();
    
    Assert.Equal(3, query.Count);
}
```

**Deliverables:**
- [ ] Modified: `FDP/Fdp.Kernel/EntityQuery.cs`
- [ ] New test file: `FDP/Fdp.Tests/EntityQueryLifecycleTests.cs`
- [ ] 3+ unit tests passing

---

### Task 6.5: ELM Integration Testing ⭐⭐

**Objective:** End-to-end validation of lifecycle coordination scenarios.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 6, entire chapter

**Test Scenarios:**

```csharp
// File: ModuleHost.Tests/EntityLifecycleIntegrationTests.cs

[Fact]
public async Task ELM_3Module_CoordinatedSpawn()
{
    // Setup: 3 modules (Physics, AI, Network) all participate
    var physics = new PhysicsModule { Id = 1 };
    var ai = new AIModule { Id = 2 };
    var network = new NetworkModule { Id = 3 };
    
    var elm = new EntityLifecycleModule(new[] { 1, 2, 3 });
    
    var kernel = CreateKernel();
    kernel.RegisterModule(elm);
    kernel.RegisterModule(physics);
    kernel.RegisterModule(ai);
    kernel.RegisterModule(network);
    kernel.Initialize();
    
    // Spawn entity
    var entity = kernel.LiveWorld.CreateEntity();
    kernel.LiveWorld.SetLifecycleState(entity, EntityLifecycle.Constructing);
    
    elm.BeginConstruction(entity, typeId: 1, 
        kernel.LiveWorld.GlobalVersion, 
        kernel.LiveWorld.GetCommandBuffer());
    
    // Run frames until all modules ACK
    for (int frame = 0; frame < 10; frame++)
    {
        kernel.Update(0.016f);
        await Task.Delay(10);
        
        // Check if activated
        if (kernel.LiveWorld.GetLifecycleState(entity) == EntityLifecycle.Active)
        {
            break;
        }
    }
    
    // Verify activated
    Assert.Equal(EntityLifecycle.Active, 
        kernel.LiveWorld.GetLifecycleState(entity));
    
    // Verify all modules initialized
    Assert.True(physics.InitializedEntities.Contains(entity));
    Assert.True(ai.InitializedEntities.Contains(entity));
    Assert.True(network.InitializedEntities.Contains(entity));
}

[Fact]
public async Task ELM_3Module_CoordinatedDestroy()
{
    // Similar test for destruction
    // Verify all modules clean up before entity deleted
}

[Fact]
public async Task ELM_Module_Crash_During_Construction()
{
    // Module fails to ACK (crashed)
    // Verify timeout causes cleanup
    // Verify other modules not blocked
}

[Fact]
public async Task ELM_Lifecycle_State_Consistency()
{
    // Multi-module scenario
    // Verify no module ever sees half-initialized entity
    //Verify query filtering works correctly
}

[Fact]
public async Task ELM_PartialACK_ThenTimeout()
{
    var elm = new EntityLifecycleModule(new[] { 1, 2, 3 }, timeoutFrames: 5);
    // Module 1 and 2 ACK, module 3 never responds
    // Verify timeout cleanup
}
```

**Deliverables:**
- [ ] New test file: `ModuleHost.Tests/EntityLifecycleIntegrationTests.cs`
- [ ] 5+ integration tests passing
- [ ] Multi-module scenarios validated

---

## ✅ Definition of Done

- [ ] All 5 tasks completed
- [ ] Event protocol defined
- [ ] ELM module implemented
- [ ] Lifecycle system working
- [ ] Query filtering functional
- [ ] All unit tests passing (14+ tests)
- [ ] All integration tests passing (5+ tests)
- [ ] Multi-module coordination validated
- [ ] Timeout handling working
- [ ] No compiler warnings
- [ ] Changes committed
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| ACK latency | <1 frame | <3 frames |
| Coordination accuracy | 100% | 100% |
| Timeout detection | ±1 frame | ±5 frames |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| All tests | Passing |
| Entity consistency | 100% |

---

## 🚧 Potential Challenges

### Challenge 1: Module ID Management
**Issue:** How to assign unique IDs to modules  
**Solution:** Use module index in kernel or explicit ID property  
**Ask if:** ID assignment strategy unclear

### Challenge 2: Event ID Conflicts
**Issue:** Event IDs 9001-9004 must not conflict  
**Solution:** Reserve range, document in event registry  
**Ask if:** Conflicts detected

### Challenge 3: Lifecycle State in FDP
**Issue:** EntityLifecycle enum might not exist in FDP  
**Solution:** Add if missing, or use component tag  
**Ask if:** FDP API unclear

### Challenge 4: Command Buffer Queuing
**Issue:** Lifecycle changes must be queued correctly  
**Solution:** Use existing command buffer pattern  
**Ask if:** Command buffer API confusing

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-06-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-06-QUESTIONS.md`

---

## 🔗 References

**Primary Design:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 6  
**SST Rules:** `../../docs/ModuleHost-TODO.md` - bdc-sst-rules section  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 06  

**Code to Review:**
- FDP Entity and Event system
- Command buffer pattern
- Existing module examples

---

## 💡 Implementation Tips

1. **Start with events** - simplest, most independent
2. **Test ACK protocol thoroughly** - this is the core mechanism
3. **Think about error cases** - failed init, timeouts, crashes
4. **Use realistic scenarios** - physics + AI + network
5. **Log everything** - debugging distributed coordination is hard
6. **Consider partial ACKs** - what if some modules optional?

**This enables distributed simulation - very powerful!**

Good luck! 🚀
