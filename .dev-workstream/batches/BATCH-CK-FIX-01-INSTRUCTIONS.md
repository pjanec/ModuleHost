# BATCH-CK-FIX-01: Architectural Corrections

**Batch ID:** BATCH-CK-FIX-01  
**Phase:** Corrective  
**Prerequisites:** BATCH-CK-01 through BATCH-CK-08 COMPLETE  
**Priority:** HIGH - Fixes architectural violations  
**Assigned:** TBD  

---

## 📋 Objectives

Apply architectural corrections from design addendum:
1. Add `SetComponent<T>()` alias to EntityRepository
2. Refactor SpatialHashSystem to use Singleton pattern
3. Update CarKinematicsSystem to consume Singleton
4. Fix FormationRoster to store full Entity handles
5. Add proper FDP Kernel attributes to all systems
6. Replace manual entity collection with `ForEachParallel`

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-design-addendum.md`

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\FDP\Fdp.Kernel\
└── EntityRepository.cs              ← UPDATE (add SetComponent)

D:\WORK\ModuleHost\CarKinem\
├── Formation\
│   └── FormationRoster.cs           ← UPDATE (Entity storage)
│       FormationRosterExtensions.cs ← NEW (helper methods)
├── Spatial\
│   └── SpatialGridData.cs           ← NEW (singleton component)
└── Systems\
    ├── SpatialHashSystem.cs         ← UPDATE (write singleton)
    ├── CarKinematicsSystem.cs       ← UPDATE (read singleton, ForEachParallel)
    └── FormationTargetSystem.cs     ← UPDATE (use Entity helpers)

D:\WORK\ModuleHost\CarKinem.Tests\
├── Systems\
│   └── CarKinematicsSystemTests.cs  ← UPDATE (singleton access)
└── Formation\
    └── FormationRosterTests.cs      ← UPDATE (Entity helpers)
```

---

## 🎯 Tasks

### Task FIX-01: Add `SetComponent<T>()` to EntityRepository

**File:** `Fdp.Kernel/EntityRepository.cs`

Add method after `AddComponent`:

```csharp
/// <summary>
/// Set component value (alias for AddComponent - upsert behavior).
/// Use this when semantically updating an existing component.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void SetComponent<T>(Entity entity, T component) where T : struct
{
    // In FDP, AddComponent is already upsert (update-or-insert)
    AddComponent<T>(entity, component);
}
```

**Test:** Verify SetComponent and AddComponent produce identical results.

---

### Task FIX-02: Create SpatialGridData Singleton Component

**File:** `CarKinem/Spatial/SpatialGridData.cs` (NEW)

```csharp
using CarKinem.Spatial;

namespace CarKinem.Spatial
{
    /// <summary>
    /// Singleton component containing spatial hash grid.
    /// Produced by SpatialHashSystem, consumed by CarKinematicsSystem.
    /// </summary>
    public struct SpatialGridData
    {
        public SpatialHashGrid Grid;
    }
}
```

---

### Task FIX-03: Update SpatialHashSystem to Write Singleton

**File:** `CarKinem/Systems/SpatialHashSystem.cs`

```csharp
using System;
using CarKinem.Core;
using CarKinem.Spatial;
using Fdp.Kernel;
using Fdp.Kernel.Collections;

namespace CarKinem.Systems
{
    /// <summary>
    /// Builds spatial hash grid from vehicle positions each frame.
    /// Publishes grid as singleton component.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public class SpatialHashSystem : ComponentSystem
    {
        private SpatialHashGrid _grid;
        
        protected override void OnCreate()
        {
            // Hardcoded: 200x200 meter world, 5m cells = 40x40 grid
            _grid = SpatialHashGrid.Create(40, 40, 5.0f, 100000, Allocator.Persistent);
        }
        
        protected override void OnUpdate()
        {
            _grid.Clear();
            
            // Query all vehicles
            var query = World.Query().With<VehicleState>().Build();
            
            foreach (var entity in query)
            {
                var state = World.GetComponent<VehicleState>(entity);
                _grid.Add(entity.Id, state.Position);
            }
            
            // Publish as singleton (Data-Oriented pattern)
            World.SetSingleton(new SpatialGridData { Grid = _grid });
        }
        
        protected override void OnDestroy()
        {
            _grid.Dispose();
        }
    }
}
```

**Changes:**
- Removed public `Grid` property
- Added `World.SetSingleton()` call
- Added `[UpdateInGroup]` attribute

---

### Task FIX-04: Update CarKinematicsSystem to Read Singleton

**File:** `CarKinem/Systems/CarKinematicsSystem.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Avoidance;
using CarKinem.Controllers;
using CarKinem.Core;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Trajectory;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Main vehicle physics system.
    /// Runs in parallel for all vehicles.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SpatialHashSystem))]
    [UpdateAfter(typeof(FormationTargetSystem))]
    public class CarKinematicsSystem : ComponentSystem
    {
        private readonly RoadNetworkBlob _roadNetwork;
        private readonly TrajectoryPoolManager _trajectoryPool;
        
        public CarKinematicsSystem(RoadNetworkBlob roadNetwork, TrajectoryPoolManager trajectoryPool)
        {
            _roadNetwork = roadNetwork;
            _trajectoryPool = trajectoryPool;
        }
        
        protected override void OnUpdate()
        {
            float dt = DeltaTime;
            
            // Read spatial grid from singleton (Data-Oriented dependency)
            var gridData = World.GetSingleton<SpatialGridData>();
            var spatialGrid = gridData.Grid;
            
            // Get all vehicles
            var query = World.Query()
                .With<VehicleState>()
                .With<VehicleParams>()
                .With<NavState>()
                .Build();
            
            // Parallel update using FDP's zero-GC ForEachParallel
            query.ForEachParallel((entity, index) =>
            {
                UpdateVehicle(entity, dt, spatialGrid);
            });
        }
        
        private void UpdateVehicle(Entity entity, float dt, SpatialHashGrid spatialGrid)
        {
            var state = World.GetComponent<VehicleState>(entity);
            var @params = World.GetComponent<VehicleParams>(entity);
            var nav = World.GetComponent<NavState>(entity);
            
            // ... (rest of update logic unchanged)
            
            // Write back state using SetComponent (semantic clarity)
            World.SetComponent(entity, state);
            World.SetComponent(entity, nav);
        }
        
        // ... (rest of methods unchanged)
    }
}
```

**Changes:**
- Removed `_spatialHashSystem` field
- Removed `SpatialSystemOverride` property
- Removed `OnCreate()` dependency injection
- Read `SpatialGridData` singleton in `OnUpdate()`
- Replaced manual entity collection with `ForEachParallel`
- Changed `AddComponent` to `SetComponent` for updates
- Added `[UpdateInGroup]` and `[UpdateAfter]` attributes

---

### Task FIX-05: Update FormationRoster to Store Full Entity Handles

**File:** `CarKinem/Formation/FormationRoster.cs`

```csharp
using System.Runtime.InteropServices;
using Fdp.Kernel;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation roster (attached to leader entity or formation manager).
    /// Fixed capacity of 16 members.
    /// Stores full Entity handles (ID + Generation) for safety.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FormationRoster
    {
        public int Count;                 // Number of active members (0-16)
        public int TemplateId;            // Index into formation template blob
        public FormationType Type;        // Formation type
        public FormationParams Params;    // Formation parameters
        
        // Fixed-capacity arrays
        public fixed long MemberEntities[16];   // Full Entity (8 bytes: ID + Generation)
        public fixed ushort SlotIndices[16];    // Slot index for each member
    }
}
```

**File:** `CarKinem/Formation/FormationRosterExtensions.cs` (NEW)

```csharp
using System;
using Fdp.Kernel;

namespace CarKinem.Formation
{
    /// <summary>
    /// Helper methods for FormationRoster entity access.
    /// </summary>
    public static class FormationRosterExtensions
    {
        /// <summary>
        /// Set member entity at index.
        /// </summary>
        public static unsafe void SetMember(this ref FormationRoster roster, int index, Entity entity)
        {
            if (index < 0 || index >= 16)
                throw new IndexOutOfRangeException($"Member index {index} out of range [0, 16)");
            
            roster.MemberEntities[index] = *(long*)&entity; // Reinterpret Entity as long
        }
        
        /// <summary>
        /// Get member entity at index.
        /// </summary>
        public static unsafe Entity GetMember(this ref FormationRoster roster, int index)
        {
            if (index < 0 || index >= 16)
                return Entity.Null;
            
            long value = roster.MemberEntities[index];
            return *(Entity*)&value; // Reinterpret long as Entity
        }
    }
}
```

**Changes:**
- Changed `fixed int MemberEntityIds[16]` to `fixed long MemberEntities[16]`
- Added extension methods for safe access
- Entity is 8 bytes (4B ID + 4B Generation), stored as long

---

### Task FIX-06: Update FormationTargetSystem

**File:** `CarKinem/Systems/FormationTargetSystem.cs`

```csharp
using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Calculates formation slot targets for members.
    /// Runs before CarKinematicsSystem.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(CarKinematicsSystem))]
    public class FormationTargetSystem : ComponentSystem
    {
        private readonly FormationTemplateManager _templateManager;
        
        public FormationTargetSystem(FormationTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }
        
        protected override void OnUpdate()
        {
            var formationQuery = World.Query().With<FormationRoster>().Build();
            
            foreach (var formationEntity in formationQuery)
            {
                var roster = World.GetComponent<FormationRoster>(formationEntity);
                UpdateFormation(ref roster);
            }
        }
        
        private void UpdateFormation(ref FormationRoster roster)
        {
            if (roster.Count == 0)
                return;
            
            // Get leader entity (now safe - has generation!)
            Entity leaderEntity = roster.GetMember(0);
            
            if (!World.IsAlive(leaderEntity))
                return;
            
            var leaderState = World.GetComponent<VehicleState>(leaderEntity);
            var template = _templateManager.GetTemplate(roster.Type);
            
            // Update each member's target
            for (int i = 1; i < roster.Count; i++)
            {
                Entity memberEntity = roster.GetMember(i);
                
                if (!World.IsAlive(memberEntity))
                    continue;
                
                int slotIndex = roster.GetSlotIndex(i); // Extension method
                
                // Calculate slot position
                Vector2 slotPos = template.GetSlotPosition(slotIndex, 
                    leaderState.Position, leaderState.Forward);
                
                // Create/update FormationTarget
                if (!World.HasComponent<FormationTarget>(memberEntity))
                {
                    World.AddComponent(memberEntity, new FormationTarget());
                }
                
                var target = World.GetComponent<FormationTarget>(memberEntity);
                target.TargetPosition = slotPos;
                target.TargetHeading = leaderState.Forward;
                target.TargetSpeed = leaderState.Speed;
                World.SetComponent(memberEntity, target); // Use SetComponent
                
                // Update member state
                if (World.HasComponent<FormationMember>(memberEntity))
                {
                    var member = World.GetComponent<FormationMember>(memberEntity);
                    var memberState = World.GetComponent<VehicleState>(memberEntity);
                    
                    float distToSlot = Vector2.Distance(memberState.Position, slotPos);
                    
                    if (distToSlot < roster.Params.ArrivalThreshold)
                        member.State = FormationMemberState.InSlot;
                    else if (distToSlot < roster.Params.BreakDistance * 0.5f)
                        member.State = FormationMemberState.CatchingUp;
                    else if (distToSlot < roster.Params.BreakDistance)
                        member.State = FormationMemberState.Rejoining;
                    else
                        member.State = FormationMemberState.Broken;
                    
                    World.SetComponent(memberEntity, member);
                }
            }
        }
    }
}
```

**Changes:**
- Removed `GetHeader` workaround
- Used `roster.GetMember()` extension method
- `IsAlive` now works correctly (has generation)
- Changed `AddComponent` to `SetComponent` for updates
- Added `[UpdateInGroup]` and `[UpdateBefore]` attributes

---

### Task FIX-07: Add `GetSlotIndex()` Extension

**File:** `CarKinem/Formation/FormationRosterExtensions.cs`

Add method:

```csharp
/// <summary>
/// Get slot index for member at index.
/// </summary>
public static unsafe ushort GetSlotIndex(this ref FormationRoster roster, int index)
{
    if (index < 0 || index >= 16)
        return 0;
    
    return roster.SlotIndices[index];
}

/// <summary>
/// Set slot index for member at index.
/// </summary>
public static unsafe void SetSlotIndex(this ref FormationRoster roster, int index, ushort slotIndex)
{
    if (index < 0 || index >= 16)
        throw new IndexOutOfRangeException($"Slot index {index} out of range [0, 16)");
    
    roster.SlotIndices[index] = slotIndex;
}
```

---

### Task FIX-08: Update Tests

**File:** `CarKinem.Tests/Systems/CarKinematicsSystemTests.cs`

Update test setup:

```csharp
[Fact]
public void System_UpdatesVehiclePosition()
{
    // Setup
    var repo = new EntityRepository();
    repo.RegisterComponent<VehicleState>();
    repo.RegisterComponent<VehicleParams>();
    repo.RegisterComponent<NavState>();
    repo.RegisterComponent<SpatialGridData>(); // Register singleton
    
    var roadNetwork = new RoadNetworkBuilder().Build(5f, 40, 40);
    var trajectoryPool = new TrajectoryPoolManager();
    
    var spatialSystem = new SpatialHashSystem();
    var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
    
    spatialSystem.World = repo;
    kinematicsSystem.World = repo;
    
    spatialSystem.OnCreate();
    // No OnCreate for kinematics - no dependencies
    
    // ... create vehicle ...
    
    // Update systems (spatial writes singleton, kinematics reads it)
    spatialSystem.OnUpdate();
    kinematicsSystem.OnUpdate();
    
    // ... assertions ...
    
    spatialSystem.OnDestroy();
    roadNetwork.Dispose();
    trajectoryPool.Dispose();
}
```

**Changes:**
- Register `SpatialGridData` component
- Remove spatial system injection
- Spatial system now writes singleton automatically

---

## ✅ Acceptance Criteria

- [ ] `SetComponent<T>()` added to EntityRepository
- [ ] `SpatialGridData` singleton component created
- [ ] `SpatialHashSystem` writes singleton (no exposed Grid property)
- [ ] `CarKinematicsSystem` reads singleton (no system dependency)
- [ ] `CarKinematicsSystem` uses `ForEachParallel` (no manual collection)
- [ ] `FormationRoster` stores `fixed long MemberEntities[16]`
- [ ] Extension methods `GetMember()`, `SetMember()`, `GetSlotIndex()` implemented
- [ ] `FormationTargetSystem` uses Entity helpers (no `GetHeader` workaround)
- [ ] All systems have proper `[UpdateInGroup]` and `[UpdateBefore/After]` attributes
- [ ] All Update calls use `SetComponent` instead of `AddComponent`
- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL 87 tests pass**

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-FIX-01-REPORT.md`

**Time Estimate:** 2-3 hours

---

**CRITICAL:** This batch fixes architectural violations. Must be completed before BATCH-CK-09 and BATCH-CK-10.
