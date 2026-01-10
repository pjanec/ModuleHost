# BATCH-CK-11: Command System Completion

**Batch ID:** BATCH-CK-11  
**Phase:** Core Commands - Spawn & Formation Creation  
**Priority:** HIGH (P1) - Unblocks background AI module integration  
**Estimated Effort:** 1.5 days  
**Dependencies:** None (builds on existing command infrastructure)  
**Starting Point:** Current main branch  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading & Workflow

**IMPORTANT: Read these documents before starting:**

### Developer Workflow
- **Workflow Guide**: `d:\Work\ModuleHost\.dev-workstream\README.md`
  - How to work, report, ask questions
  - Definition of Done
  - Communication standards

### Design & Architecture
- **Design Document**: `d:\Work\ModuleHost\docs\car-kinem-implementation-design.md`
  - Full architecture specification
  - Command system design
  - Formation system details

### Source Code Locations
- **CarKinem Core**: `Fdp.Examples.CarKinem\CarKinem\`
  - Commands: `CarKinem\Commands\`
  - Systems: `CarKinem\Systems\`
  - Components: `CarKinem\Core\`, `CarKinem\Formation\`
- **Tests**: `Fdp.Examples.CarKinem\CarKinem.Tests\`
- **Demo Application**: `Fdp.Examples.CarKinem\`

### Reporting Requirements
**When complete, submit:**
- **Report**: `d:\Work\ModuleHost\.dev-workstream\reports\BATCH-CK-11-REPORT.md`
  - Use template: `.dev-workstream\templates\BATCH-REPORT-TEMPLATE.md`
  - Include: Test results, design decisions, blockers
- **Questions** (if needed): `.dev-workstream\reports\BATCH-CK-11-QUESTIONS.md`
- **Blockers** (if blocked): `.dev-workstream\reports\BATCH-CK-11-BLOCKERS.md`

---

## 📚 Context & Problem Statement

### The Issue

Background AI modules cannot spawn vehicles or create formations using the command buffer pattern. These operations are currently only available via direct method calls in `DemoSimulation`, which is not accessible from background modules (ModuleHost sandbox).

**Current Limitations:**
```csharp
// ❌ NOT POSSIBLE from background AI module:
public class VehicleAIModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Cannot spawn vehicles - no command exists
        // Cannot create formations - no command exists
    }
}
```

**Current Workaround:**
```csharp
// ✅ Only works in main thread simulation orchestration:
var sim = new DemoSimulation();
int vehicleId = sim.SpawnVehicle(position, heading);  // Direct call only
```

### Design Specification Gap

From `docs/car-kinem-implementation-design.md`:

```csharp
[EventId(5001)]
public struct CmdSpawnVehicle
{
    public int EntityId;        // Pre-allocated entity ID
    public Vector2 Position;
    public float Heading;
    public int VehicleTypeId;   // Index into VehicleParams table
}

[EventId(5002)]
public struct CmdCreateFormation
{
    public int LeaderEntityId;
    public FormationType Type;
    public FormationParams Params;
}
```

---

## 🎯 Goal

Implement command-based spawning and formation creation to enable background AI modules to:

1. **Spawn vehicles** via command buffer with proper entity pre-allocation
2. **Create formations** dynamically and register them with the formation system
3. **Join formations** (already works but needs formation creation pathway)

### Success Criteria

✅ **CmdSpawnVehicle works from background module:**
```csharp
view.GetCommandBuffer().PublishEvent(new CmdSpawnVehicle
{
    Entity = preAllocatedEntity,
    Position = new Vector2(100, 50),
    Heading = 0f,
    VehicleClass = VehicleClass.PersonalCar
});
```

✅ **CmdCreateFormation works:**
```csharp
view.GetCommandBuffer().PublishEvent(new CmdCreateFormation
{
    LeaderEntity = leaderEntity,
    Type = FormationType.Column,
    Params = defaultParams
});
```

✅ **Formation joins work end-to-end:**
```csharp
// Create formation
cmd.PublishEvent(new CmdCreateFormation { ... });
// Join it
cmd.PublishEvent(new CmdJoinFormation { ... });
```

---

## 📋 Implementation Tasks

### **Task 1: Add CmdSpawnVehicle Command** ⭐⭐

**Objective:** Define spawn command event.

**File to Modify:** `CarKinem/Commands/CommandEvents.cs`

**Add after existing commands:**
```csharp
[EventId(2108)]
public struct CmdSpawnVehicle
{
    public Entity Entity;           // Pre-allocated entity (see notes)
    public Vector2 Position;        // Initial position
    public Vector2 Heading;         // Initial heading vector (normalized)
    public VehicleClass Class;      // Vehicle class (PersonalCar, Truck, etc.)
}
```

**Design Note: Entity Pre-Allocation**

Background modules cannot call `CreateEntity()` directly (read-only view). Two approaches:

**Option A: Pre-allocated Entity (Recommended)**
```csharp
// In background module:
var entity = view.GetCommandBuffer().CreateEntity();  // Pre-allocates
cmd.PublishEvent(new CmdSpawnVehicle { Entity = entity, ... });
```

**Option B: Auto-allocate in Handler**
```csharp
// Command doesn't specify entity:
public struct CmdSpawnVehicle
{
    public Vector2 Position;
    // ... no Entity field
}

// Handler creates it:
var entity = World.CreateEntity();
```

**Recommendation:** Use Option A (pre-allocation) for consistency with FDP command buffer pattern.

**Deliverables:**
- [ ] Add `CmdSpawnVehicle` struct with `[EventId(2108)]`
- [ ] Document entity pre-allocation requirement in XML comments

---

### **Task 2: Add CmdCreateFormation Command** ⭐⭐

**Objective:** Define formation creation command event.

**File to Modify:** `CarKinem/Commands/CommandEvents.cs`

**Add after CmdSpawnVehicle:**
```csharp
[EventId(2109)]
public struct CmdCreateFormation
{
    public Entity LeaderEntity;     // Entity to become formation leader
    public FormationType Type;      // Column, Wedge, Line, Custom
    public FormationParams Params;  // Formation parameters
}
```

**Deliverables:**
- [ ] Add `CmdCreateFormation` struct with `[EventId(2109)]`
- [ ] Add XML comments explaining leader role

---

### **Task 3: Implement Spawn Handler** ⭐⭐⭐⭐

**Objective:** Process spawn commands in VehicleCommandSystem.

**File to Modify:** `CarKinem/Systems/VehicleCommandSystem.cs`

**Add to OnUpdate():**
```csharp
protected override void OnUpdate()
{
    ProcessSpawnCommands();  // NEW
    ProcessNavigateToPointCommands();
    // ... existing commands
}
```

**Implement handler:**
```csharp
private void ProcessSpawnCommands()
{
    var events = World.Bus.Consume<CmdSpawnVehicle>();
    
    foreach (var cmd in events)
    {
        var entity = cmd.Entity;
        
        // Verify entity was pre-allocated and is alive
        if (!World.IsAlive(entity))
        {
            Console.WriteLine($"WARNING: CmdSpawnVehicle references dead entity {entity}");
            continue;
        }
        
        // Add VehicleState component
        World.AddComponent(entity, new VehicleState
        {
            Position = cmd.Position,
            Forward = Vector2.Normalize(cmd.Heading),
            Speed = 0f,
            SteerAngle = 0f,
            Accel = 0f,
            Pitch = 0f,
            Roll = 0f,
            CurrentLaneIndex = -1
        });
        
        // Add VehicleParams component (use preset)
        var preset = VehiclePresets.GetPreset(cmd.Class);
        preset.Class = cmd.Class;  // Set class field
        World.AddComponent(entity, preset);
        
        // Add NavState component (idle)
        World.AddComponent(entity, new NavState
        {
            Mode = NavigationMode.None,
            RoadPhase = RoadGraphPhase.Approaching,
            TrajectoryId = -1,
            CurrentSegmentId = -1,
            ProgressS = 0f,
            TargetSpeed = 0f,
            FinalDestination = cmd.Position,
            ArrivalRadius = 2.0f,
            SpeedErrorInt = 0f,
            LastSteerCmd = 0f,
            ReverseAllowed = 0,
            HasArrived = 0,
            IsBlocked = 0
        });
    }
}
```

**Deliverables:**
- [ ] Implement `ProcessSpawnCommands()` method
- [ ] Add entity validation
- [ ] Add all 3 required components (VehicleState, VehicleParams, NavState)
- [ ] Use `VehiclePresets.GetPreset()` for params

---

### **Task 4: Implement Formation Creation Handler** ⭐⭐⭐⭐

**Objective:** Process formation creation commands and add FormationRoster.

**File to Modify:** `CarKinem/Systems/VehicleCommandSystem.cs`

**Add to OnUpdate():**
```csharp
protected override void OnUpdate()
{
    ProcessSpawnCommands();
    ProcessCreateFormationCommands();  // NEW
    ProcessNavigateToPointCommands();
    // ... existing commands
}
```

**Implement handler:**
```csharp
private void ProcessCreateFormationCommands()
{
    var events = World.Bus.Consume<CmdCreateFormation>();
    
    foreach (var cmd in events)
    {
        var leaderEntity = cmd.LeaderEntity;
        
        if (!World.IsAlive(leaderEntity))
        {
            Console.WriteLine($"WARNING: CmdCreateFormation references dead leader {leaderEntity}");
            continue;
        }
        
        // Create/update FormationRoster component on leader
        FormationRoster roster;
        
        if (World.HasComponent<FormationRoster>(leaderEntity))
        {
            // Update existing roster
            roster = World.GetComponent<FormationRoster>(leaderEntity);
        }
        else
        {
            // Create new roster
            roster = new FormationRoster();
            World.AddComponent(leaderEntity, roster);
        }
        
        // Configure roster
        roster.Type = cmd.Type;
        roster.Params = cmd.Params;
        roster.Count = 1;  // Leader only initially
        roster.SetMember(0, leaderEntity);  // Leader is always slot 0
        roster.SetSlotIndex(0, 0);
        
        World.SetComponent(leaderEntity, roster);
    }
}
```

**Deliverables:**
- [ ] Implement `ProcessCreateFormationCommands()` method
- [ ] Handle both new and existing rosters
- [ ] Initialize leader as slot 0
- [ ] Set formation type and params

---

### **Task 5: Fix CmdJoinFormation to Use Leader Entity** ⭐⭐⭐

**Objective:** Update join command to reference leader entity instead of `FormationId`.

**File to Modify:** `CarKinem/Commands/CommandEvents.cs`

**Current (INCORRECT):**
```csharp
[EventId(2104)]
public struct CmdJoinFormation
{
    public Entity Entity;
    public int FormationId;  // ❌ No way to get formation ID
    public int SlotIndex;
}
```

**New (CORRECT):**
```csharp
[EventId(2104)]
public struct CmdJoinFormation
{
    public Entity Entity;        // Follower entity
    public Entity LeaderEntity;  // Formation leader entity (has FormationRoster)
    public int SlotIndex;        // Desired slot (0-15)
}
```

**File to Modify:** `CarKinem/Systems/VehicleCommandSystem.cs`

**Update handler:**
```csharp
private void ProcessJoinFormationCommands()
{
    var events = World.Bus.Consume<CmdJoinFormation>();
    
    foreach (var cmd in events)
    {
        var followerEntity = cmd.Entity;
        var leaderEntity = cmd.LeaderEntity;
        
        if (!World.IsAlive(followerEntity) || !World.IsAlive(leaderEntity))
            continue;
        
        // Verify leader has a formation
        if (!World.HasComponent<FormationRoster>(leaderEntity))
        {
            Console.WriteLine($"WARNING: CmdJoinFormation: Leader {leaderEntity} has no FormationRoster");
            continue;
        }
        
        // Add FormationMember component if not exists
        if (!World.HasComponent<FormationMember>(followerEntity))
        {
            World.AddComponent(followerEntity, new FormationMember());
        }
        
        var member = World.GetComponent<FormationMember>(followerEntity);
        member.LeaderEntityId = leaderEntity.Index;  // Store leader index
        member.SlotIndex = (ushort)cmd.SlotIndex;
        member.State = FormationMemberState.Rejoining;
        member.IsInFormation = 1;
        World.SetComponent(followerEntity, member);
        
        // Add follower to leader's roster
        var roster = World.GetComponent<FormationRoster>(leaderEntity);
        if (roster.Count < 16)  // Max 16 members
        {
            roster.SetMember(roster.Count, followerEntity);
            roster.SetSlotIndex(roster.Count, (ushort)cmd.SlotIndex);
            roster.Count++;
            World.SetComponent(leaderEntity, roster);
        }
        
        // Set follower navigation mode to Formation
        var nav = World.GetComponent<NavState>(followerEntity);
        nav.Mode = NavigationMode.Formation;
        nav.HasArrived = 0;
        World.SetComponent(followerEntity, nav);
    }
}
```

**Deliverables:**
- [ ] Update `CmdJoinFormation` struct to use `LeaderEntity`
- [ ] Update `ProcessJoinFormationCommands()` handler
- [ ] Add follower to roster
- [ ] Validate leader has FormationRoster

---

### **Task 6: Update VehicleAPI Facade** ⭐⭐

**Objective:** Update VehicleAPI with new commands.

**File to Modify:** `CarKinem/Commands/VehicleAPI.cs`

**Add new methods:**
```csharp
/// <summary>
/// Spawn a new vehicle at the specified position.
/// Note: Entity must be pre-allocated via command buffer.
/// </summary>
public void SpawnVehicle(Entity entity, Vector2 position, Vector2 heading, 
    VehicleClass vehicleClass = VehicleClass.PersonalCar)
{
    var cmd = _view.GetCommandBuffer();
    cmd.PublishEvent(new CmdSpawnVehicle
    {
        Entity = entity,
        Position = position,
        Heading = heading,
        Class = vehicleClass
    });
}

/// <summary>
/// Create a formation with the specified leader.
/// </summary>
public void CreateFormation(Entity leaderEntity, FormationType type, 
    FormationParams? parameters = null)
{
    var cmd = _view.GetCommandBuffer();
    
    // Use default params if not specified
    var params_ = parameters ?? new FormationParams
    {
        Spacing = 5.0f,
        WedgeAngleRad = 0.52f,  // ~30 degrees
        MaxCatchUpFactor = 1.2f,
        BreakDistance = 50.0f,
        ArrivalThreshold = 2.0f,
        SpeedFilterTau = 0.5f
    };
    
    cmd.PublishEvent(new CmdCreateFormation
    {
        LeaderEntity = leaderEntity,
        Type = type,
        Params = params_
    });
}

/// <summary>
/// Command vehicle to join a formation.
/// Updated signature to use leader entity.
/// </summary>
public void JoinFormation(Entity followerEntity, Entity leaderEntity, int slotIndex = -1)
{
    var cmd = _view.GetCommandBuffer();
    
    // Auto-assign slot if not specified
    if (slotIndex < 0)
    {
        // TODO: Query roster to find next available slot
        slotIndex = 1;  // Default to slot 1 (leader is 0)
    }
    
    cmd.PublishEvent(new CmdJoinFormation
    {
        Entity = followerEntity,
        LeaderEntity = leaderEntity,
        SlotIndex = slotIndex
    });
}
```

**Deliverables:**
- [ ] Add `SpawnVehicle()` method to VehicleAPI
- [ ] Add `CreateFormation()` method with default params
- [ ] Update `JoinFormation()` signature to use `LeaderEntity`

---

### **Task 7: Register New Command Events** ⭐

**Objective:** Register command events in DemoSimulation.

**File to Modify:** `Fdp.Examples.CarKinem/Simulation/DemoSimulation.cs`

**Update RegisterComponents():**
```csharp
private void RegisterComponents()
{
    // ... existing registrations
    
    _repository.RegisterEvent<CmdSpawnVehicle>();       // NEW
    _repository.RegisterEvent<CmdCreateFormation>();    // NEW
    _repository.RegisterEvent<CmdNavigateToPoint>();
    // ... rest
}
```

**Deliverables:**
- [ ] Register `CmdSpawnVehicle` event
- [ ] Register `CmdCreateFormation` event

---

## 🧪 Testing Strategy

### **Task 8: Unit Tests - Spawn Command** ⭐⭐⭐

**File to Create:** `CarKinem.Tests/Commands/SpawnCommandTests.cs`

```csharp
using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class SpawnCommandTests
    {
        [Fact]
        public void SpawnCommand_CreatesVehicleWithComponents()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdSpawnVehicle>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            // Pre-allocate entity
            var entity = repo.CreateEntity();
            
            // Issue spawn command
            repo.Bus.Publish(new CmdSpawnVehicle
            {
                Entity = entity,
                Position = new Vector2(100, 50),
                Heading = new Vector2(1, 0),
                Class = VehicleClass.PersonalCar
            });
            
            // Process command
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify components
            Assert.True(repo.HasComponent<VehicleState>(entity));
            Assert.True(repo.HasComponent<VehicleParams>(entity));
            Assert.True(repo.HasComponent<NavState>(entity));
            
            var state = repo.GetComponent<VehicleState>(entity);
            Assert.Equal(new Vector2(100, 50), state.Position);
            Assert.Equal(new Vector2(1, 0), state.Forward);
            
            repo.Dispose();
        }
        
        [Fact]
        public void SpawnCommand_IgnoresDeadEntity()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterEvent<CmdSpawnVehicle>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            // Create and destroy entity
            var entity = repo.CreateEntity();
            repo.DestroyEntity(entity);
            
            // Try to spawn on dead entity
            repo.Bus.Publish(new CmdSpawnVehicle
            {
                Entity = entity,
                Position = Vector2.Zero,
                Heading = new Vector2(1, 0),
                Class = VehicleClass.PersonalCar
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Should not crash, command ignored
            Assert.False(repo.IsAlive(entity));
            
            repo.Dispose();
        }
    }
}
```

**Deliverables:**
- [ ] Create `SpawnCommandTests.cs` with 2+ tests
- [ ] Test successful spawn
- [ ] Test dead entity handling

---

### **Task 9: Unit Tests - Formation Creation** ⭐⭐⭐

**File to Create:** `CarKinem.Tests/Commands/FormationCreationTests.cs`

```csharp
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class FormationCreationTests
    {
        [Fact]
        public void CreateFormation_AddsRosterToLeader()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterEvent<CmdCreateFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var leaderEntity = repo.CreateEntity();
            
            // Create formation
            repo.Bus.Publish(new CmdCreateFormation
            {
                LeaderEntity = leaderEntity,
                Type = FormationType.Column,
                Params = new FormationParams
                {
                    Spacing = 5.0f,
                    MaxCatchUpFactor = 1.2f,
                    BreakDistance = 50.0f,
                    ArrivalThreshold = 2.0f
                }
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify roster
            Assert.True(repo.HasComponent<FormationRoster>(leaderEntity));
            var roster = repo.GetComponent<FormationRoster>(leaderEntity);
            Assert.Equal(FormationType.Column, roster.Type);
            Assert.Equal(1, roster.Count);  // Leader only
            Assert.Equal(5.0f, roster.Params.Spacing);
            
            repo.Dispose();
        }
        
        [Fact]
        public void JoinFormation_AddsFollowerToRoster()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<FormationMember>();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterEvent<CmdCreateFormation>();
            repo.RegisterEvent<CmdJoinFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var leaderEntity = repo.CreateEntity();
            repo.AddComponent(leaderEntity, new VehicleState());
            repo.AddComponent(leaderEntity, new NavState());
            
            var followerEntity = repo.CreateEntity();
            repo.AddComponent(followerEntity, new VehicleState());
            repo.AddComponent(followerEntity, new NavState());
            
            // Create formation
            repo.Bus.Publish(new CmdCreateFormation
            {
                LeaderEntity = leaderEntity,
                Type = FormationType.Column,
                Params = new FormationParams { Spacing = 5.0f }
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Join formation
            repo.Bus.Publish(new CmdJoinFormation
            {
                Entity = followerEntity,
                LeaderEntity = leaderEntity,
                SlotIndex = 1
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify follower
            Assert.True(repo.HasComponent<FormationMember>(followerEntity));
            var member = repo.GetComponent<FormationMember>(followerEntity);
            Assert.Equal(leaderEntity.Index, member.LeaderEntityId);
            Assert.Equal(1, member.SlotIndex);
            
            // Verify roster
            var roster = repo.GetComponent<FormationRoster>(leaderEntity);
            Assert.Equal(2, roster.Count);  // Leader + follower
            
            repo.Dispose();
        }
    }
}
```

**Deliverables:**
- [ ] Create `FormationCreationTests.cs` with 2+ tests
- [ ] Test formation creation
- [ ] Test follower join

---

## ✅ Validation Criteria

### Build Verification
```powershell
dotnet build CarKinem/CarKinem.csproj --nologo
# Expected: Build succeeded. 0 Warning(s)
```

### Test Verification
```powershell
dotnet test CarKinem.Tests/CarKinem.Tests.csproj --filter "SpawnCommand|FormationCreation" --nologo
# Expected: All new tests passed
```

### Integration Test (Manual)
```csharp
// In Fdp.Examples.CarKinem or new test:
var repo = new EntityRepository();
// ... register components ...

// Spawn vehicle via command
var entity = repo.CreateEntity();
repo.Bus.Publish(new CmdSpawnVehicle { Entity = entity, ... });

// Create formation via command
repo.Bus.Publish(new CmdCreateFormation { LeaderEntity = entity, ... });

// Join formation via command
var follower = repo.CreateEntity();
repo.Bus.Publish(new CmdSpawnVehicle { Entity = follower, ... });
repo.Bus.Publish(new CmdJoinFormation { 
    Entity = follower, 
    LeaderEntity = entity, 
    SlotIndex = 1 
});

// Verify in UI or console
```

---

## 🎓 Developer Notes

### Design Decisions Summary

1. **Entity Pre-Allocation**: Use command buffer's `CreateEntity()` for background module compatibility
2. **Leader-Based Formations**: Use `LeaderEntity` reference instead of global formation IDs (simpler, more ECS-like)
3. **Default Formation Params**: Provide sensible defaults in `VehicleAPI.CreateFormation()`

### Entity Reference Pattern

**Problem:** Background modules have read-only view, cannot create entities directly.

**Solution:** Command buffer provides entity pre-allocation:
```csharp
// In background module:
var cmdBuffer = view.GetCommandBuffer();
var entity = cmdBuffer.CreateEntity();  // Pre-allocates, reserves index
cmdBuffer.PublishEvent(new CmdSpawnVehicle { Entity = entity, ... });
// Handler in main thread adds components
```

### Formation Roster Management

**Leader as Slot 0:** Leader always occupies slot 0 in roster (simplifies logic)

**Max 16 Members:** Hard limit per design spec (fixed array in FormationRoster)

**No Remove Command Yet:** `CmdLeaveFormation` exists but doesn't update roster (enhancement for later)

---

## 🚀 Completion Checklist

### Implementation
- [ ] Task 1: Add CmdSpawnVehicle command
- [ ] Task 2: Add CmdCreateFormation command
- [ ] Task 3: Implement spawn handler
- [ ] Task 4: Implement formation creation handler
- [ ] Task 5: Fix CmdJoinFormation
- [ ] Task 6: Update VehicleAPI facade
- [ ] Task 7: Register new events

### Testing
- [ ] Task 8: Spawn command tests (2+ tests)
- [ ] Task 9: Formation creation tests (2+ tests)

### Final Validation
- [ ] Build clean (0 warnings)
- [ ] All tests pass (97+ tests total)
- [ ] Manual integration test passes
- [ ] Commands work from background module

---

## 📊 Success Metrics

**Before BATCH-CK-11:**
```csharp
// ❌ Cannot spawn from background module
var entity = view.CreateEntity();  // ERROR: Read-only view
```

**After BATCH-CK-11:**
```csharp
// ✅ Works from background module
var cmd = view.GetCommandBuffer();
var entity = cmd.CreateEntity();
cmd.PublishEvent(new CmdSpawnVehicle { Entity = entity, ... });
```

**End-to-End Flow:**
```csharp
// Create formation leader
var leader = cmd.CreateEntity();
cmd.PublishEvent(new CmdSpawnVehicle { Entity = leader, ... });
cmd.PublishEvent(new CmdCreateFormation { LeaderEntity = leader, ... });

// Add followers
for (int i = 0; i < 5; i++)
{
    var follower = cmd.CreateEntity();
    cmd.PublishEvent(new CmdSpawnVehicle { Entity = follower, ... });
    cmd.PublishEvent(new CmdJoinFormation { 
        Entity = follower, 
        LeaderEntity = leader, 
        SlotIndex = i + 1 
    });
}
```

---

**END OF BATCH-CK-11 INSTRUCTIONS**
