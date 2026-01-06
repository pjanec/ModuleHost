# BATCH-CK-09: Command Processing & API

**Batch ID:** BATCH-CK-09  
**Phase:** Commands  
**Prerequisites:** All systems (CK-07, CK-08) COMPLETE  
**Assigned:** TBD  

---

## 📋 Objectives

Implement event-based command API:
1. All command event structs
2. VehicleCommandSystem (processes command events)
3. VehicleAPI facade (high-level API)
4. Integration tests

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Command API Section:** Lines 1315-1486 in design doc

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\CarKinem\
├── Commands\
│   ├── CommandEvents.cs          ← NEW (all event structs)
│   └── VehicleAPI.cs             ← NEW
└── Systems\
    └── VehicleCommandSystem.cs   ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Commands\
    └── VehicleCommandSystemTests.cs ← NEW
```

---

## 🎯 Tasks

### Task CK-09-01: Command Events

**File:** `CarKinem/Commands/CommandEvents.cs`

```csharp
using System.Numerics;
using CarKinem.Core;
using Fdp.Kernel;

namespace CarKinem.Commands
{
    [Event(EventId = 2001)]
    public struct CmdNavigateToPoint
    {
        public int EntityId;
        public Vector2 Destination;
        public float ArrivalRadius;
        public float Speed;
    }
    
    [Event(EventId = 2002)]
    public struct CmdFollowTrajectory
    {
        public int EntityId;
        public int TrajectoryId;
        public byte Looped;
    }
    
    [Event(EventId = 2003)]
    public struct CmdNavigateViaRoad
    {
        public int EntityId;
        public Vector2 Destination;
        public float ArrivalRadius;
    }
    
    [Event(EventId = 2004)]
    public struct CmdJoinFormation
    {
        public int EntityId;
        public int FormationId;
        public int SlotIndex;
    }
    
    [Event(EventId = 2005)]
    public struct CmdLeaveFormation
    {
        public int EntityId;
    }
    
    [Event(EventId = 2006)]
    public struct CmdStop
    {
        public int EntityId;
    }
    
    [Event(EventId = 2007)]
    public struct CmdSetSpeed
    {
        public int EntityId;
        public float Speed;
    }
}
```

---

### Task CK-09-02: Vehicle Command System

**File:** `CarKinem/Systems/VehicleCommandSystem.cs`

```csharp
using CarKinem.Commands;
using CarKinem.Core;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Processes vehicle command events.
    /// Runs early to update NavState before physics.
    /// </summary>
    [SystemAttributes(Phase = Phase.EarlyUpdate, UpdateFrequency = UpdateFrequency.EveryFrame)]
    public class VehicleCommandSystem : ComponentSystem
    {
        protected override void OnUpdate()
        {
            ProcessNavigateToPointCommands();
            ProcessFollowTrajectoryCommands();
            ProcessNavigateViaRoadCommands();
            ProcessJoinFormationCommands();
            ProcessLeaveFormationCommands();
            ProcessStopCommands();
            ProcessSetSpeedCommands();
        }
        
        private void ProcessNavigateToPointCommands()
        {
            var events = World.View.ConsumeEvents<CmdNavigateToPoint>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.None; // Direct navigation (no special mode)
                nav.FinalDestination = cmd.Destination;
                nav.ArrivalRadius = cmd.ArrivalRadius;
                nav.TargetSpeed = cmd.Speed;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessFollowTrajectoryCommands()
        {
            var events = World.View.ConsumeEvents<CmdFollowTrajectory>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.CustomTrajectory;
                nav.TrajectoryId = cmd.TrajectoryId;
                nav.ProgressS = 0f;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessNavigateViaRoadCommands()
        {
            var events = World.View.ConsumeEvents<CmdNavigateViaRoad>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.RoadGraph;
                nav.RoadPhase = RoadGraphPhase.Approaching;
                nav.FinalDestination = cmd.Destination;
                nav.ArrivalRadius = cmd.ArrivalRadius;
                nav.CurrentSegmentId = -1;
                nav.ProgressS = 0f;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessJoinFormationCommands()
        {
            var events = World.View.ConsumeEvents<CmdJoinFormation>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                // Add FormationMember component if not exists
                if (!World.HasComponent<FormationMember>(entity))
                {
                    World.AddComponent(entity, new FormationMember());
                }
                
                ref var member = ref World.GetComponentRef<FormationMember>(entity);
                member.FormationId = cmd.FormationId;
                member.SlotIndex = cmd.SlotIndex;
                member.State = FormationMemberState.Rejoining;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.Formation;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, member);
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessLeaveFormationCommands()
        {
            var events = World.View.ConsumeEvents<CmdLeaveFormation>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.None;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessStopCommands()
        {
            var events = World.View.ConsumeEvents<CmdStop>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.Mode = NavigationMode.None;
                nav.TargetSpeed = 0f;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessSetSpeedCommands()
        {
            var events = World.View.ConsumeEvents<CmdSetSpeed>();
            
            foreach (var cmd in events)
            {
                var entity = new Entity(cmd.EntityId, 0);
                
                if (!World.IsAlive(entity))
                    continue;
                
                ref var nav = ref World.GetComponentRef<NavState>(entity);
                nav.TargetSpeed = cmd.Speed;
                
                World.SetComponent(entity, nav);
            }
        }
    }
}
```

---

### Task CK-09-03: Vehicle API Facade

**File:** `CarKinem/Commands/VehicleAPI.cs`

```csharp
using System.Numerics;
using CarKinem.Core;
using Fdp.Kernel;

namespace CarKinem.Commands
{
    /// <summary>
    /// High-level API for vehicle commands.
    /// Facade over event system.
    /// </summary>
    public class VehicleAPI
    {
        private readonly ISimulationView _view;
        
        public VehicleAPI(ISimulationView view)
        {
            _view = view;
        }
        
        /// <summary>
        /// Command vehicle to navigate to a point and stop.
        /// </summary>
        public void NavigateToPoint(int entityId, Vector2 destination, 
            float arrivalRadius = 2.0f, float speed = 10.0f)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdNavigateToPoint
            {
                EntityId = entityId,
                Destination = destination,
                ArrivalRadius = arrivalRadius,
                Speed = speed
            });
        }
        
        /// <summary>
        /// Command vehicle to follow a custom trajectory.
        /// </summary>
        public void FollowTrajectory(int entityId, int trajectoryId, bool looped = false)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdFollowTrajectory
            {
                EntityId = entityId,
                TrajectoryId = trajectoryId,
                Looped = (byte)(looped ? 1 : 0)
            });
        }
        
        /// <summary>
        /// Command vehicle to navigate using road network.
        /// </summary>
        public void NavigateViaRoad(int entityId, Vector2 destination, 
            float arrivalRadius = 2.0f)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdNavigateViaRoad
            {
                EntityId = entityId,
                Destination = destination,
                ArrivalRadius = arrivalRadius
            });
        }
        
        /// <summary>
        /// Command vehicle to join a formation.
        /// </summary>
        public void JoinFormation(int entityId, int formationId, int slotIndex)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdJoinFormation
            {
                EntityId = entityId,
                FormationId = formationId,
                SlotIndex = slotIndex
            });
        }
        
        /// <summary>
        /// Command vehicle to leave its formation.
        /// </summary>
        public void LeaveFormation(int entityId)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdLeaveFormation
            {
                EntityId = entityId
            });
        }
        
        /// <summary>
        /// Command vehicle to stop.
        /// </summary>
        public void Stop(int entityId)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdStop
            {
                EntityId = entityId
            });
        }
        
        /// <summary>
        /// Set vehicle target speed.
        /// </summary>
        public void SetSpeed(int entityId, float speed)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdSetSpeed
            {
                EntityId = entityId,
                Speed = speed
            });
        }
    }
}
```

---

### Task CK-09-04: Tests

**File:** `CarKinem.Tests/Commands/VehicleCommandSystemTests.cs`

```csharp
using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class VehicleCommandSystemTests
    {
        [Fact]
        public void NavigateToPoint_SetsNavState()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdNavigateToPoint>();
            
            var system = new VehicleCommandSystem();
            repo.GetSystemRegistry().RegisterSystem(system);
            system.World = repo;
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new VehicleState());
            repo.AddComponent(entity, new NavState { Mode = NavigationMode.None });
            
            // Issue command
            var api = new VehicleAPI(repo.GetView());
            api.NavigateToPoint(entity.Id, new Vector2(100, 100), 2.0f, 15.0f);
            
            // Process commands
            system.OnUpdate();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(new Vector2(100, 100), nav.FinalDestination);
            Assert.Equal(2.0f, nav.ArrivalRadius);
            Assert.Equal(15.0f, nav.TargetSpeed);
        }
        
        [Fact]
        public void FollowTrajectory_SetsTrajectoryMode()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdFollowTrajectory>();
            
            var system = new VehicleCommandSystem();
            system.World = repo;
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState());
            
            var api = new VehicleAPI(repo.GetView());
            api.FollowTrajectory(entity.Id, trajectoryId: 42, looped: true);
            
            system.OnUpdate();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(NavigationMode.CustomTrajectory, nav.Mode);
            Assert.Equal(42, nav.TrajectoryId);
        }
    }
}
```

---

## ✅ Acceptance Criteria

- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 7 integration tests (one per command)
- [ ] All command events defined with Event attributes
- [ ] VehicleAPI facade provides clean high-level API
- [ ] Command processing happens in EarlyUpdate phase
- [ ] Entity validity checked before processing commands

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-09-REPORT.md`

**Time Estimate:** 3-4 hours
