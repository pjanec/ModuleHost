# BATCH-CK-07: Car Kinematics System (Main Physics)

**Batch ID:** BATCH-CK-07  
**Phase:** Core Kinematics  
**Prerequisites:**
- BATCH-CK-02 (Controllers) COMPLETE
- BATCH-CK-03 (Trajectory Pool) COMPLETE
- BATCH-CK-05 (Road Navigator) COMPLETE
- BATCH-CK-06 (Spatial Hash) COMPLETE  
**Assigned:** TBD  

---

## 📋 Objectives

Implement the main vehicle physics system:
1. CarKinematicsSystem (ComponentSystem integration)
2. Navigation mode handling (RoadGraph, CustomTrajectory, Formation, None)
3. Parallel execution (Parallel.ForEach)
4. Controller integration (PurePursuit, SpeedController, BicycleModel, RVO)
5. Spatial hash building per frame
6. Performance validation (50k vehicles target)

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**System Implementation:** Lines 1056-1315 in design doc

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\CarKinem\
└── Systems\
    ├── CarKinematicsSystem.cs     ← NEW
    └── SpatialHashSystem.cs       ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Systems\
    └── CarKinematicsSystemTests.cs ← NEW
```

---

## 🎯 Tasks

### Task CK-07-01: Spatial Hash System

**File:** `CarKinem/Systems/SpatialHashSystem.cs`

```csharp
using System.Linq;
using CarKinem.Core;
using CarKinem.Spatial;
using Fdp.Kernel;
using Fdp.Kernel.Collections;

namespace CarKinem.Systems
{
    /// <summary>
    /// Builds spatial hash grid from vehicle positions each frame.
    /// Runs early (Phase.EarlyUpdate) before kinematics.
    /// </summary>
    [SystemAttributes(Phase = Phase.EarlyUpdate, UpdateFrequency = UpdateFrequency.EveryFrame)]
    public class SpatialHashSystem : ComponentSystem
    {
        private SpatialHashGrid _grid;
        
        public SpatialHashGrid Grid => _grid;
        
        protected override void OnCreate()
        {
            // Hardcoded: 200x200 meter world, 5m cells = 40x40 grid
            _grid = SpatialHashGrid.Create(40, 40, 5.0f, 100000, Allocator.Persistent);
        }
        
        protected override void OnUpdate()
        {
            _grid.Clear();
            
            // Query all vehicles
            var query = World.Query<VehicleState>();
            
            foreach (var entity in query)
            {
                var state = World.GetComponent<VehicleState>(entity);
                _grid.Add(entity.Id, state.Position);
            }
        }
        
        protected override void OnDestroy()
        {
            _grid.Dispose();
        }
    }
}
```

---

### Task CK-07-02: Car Kinematics System

**File:** `CarKinem/Systems/CarKinematicsSystem.cs`

```csharp
using System;
using System.Numerics;
using System.Threading.Tasks;
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
    [SystemAttributes(Phase = Phase.Update, UpdateFrequency = UpdateFrequency.EveryFrame)]
    public class CarKinematicsSystem : ComponentSystem
    {
        private readonly RoadNetworkBlob _roadNetwork;
        private readonly TrajectoryPoolManager _trajectoryPool;
        private SpatialHashSystem _spatialHashSystem;
        
        public CarKinematicsSystem(RoadNetworkBlob roadNetwork, TrajectoryPoolManager trajectoryPool)
        {
            _roadNetwork = roadNetwork;
            _trajectoryPool = trajectoryPool;
        }
        
        protected override void OnCreate()
        {
            // Get spatial hash system
            _spatialHashSystem = World.GetSystem<SpatialHashSystem>();
        }
        
        protected override void OnUpdate()
        {
            float dt = DeltaTime;
            var spatialGrid = _spatialHashSystem.Grid;
            
            // Get all vehicles
            var query = World.Query<VehicleState, VehicleParams, NavState>();
            var entities = query.ToArray();
            
            // Parallel update
            Parallel.ForEach(entities, entity =>
            {
                UpdateVehicle(entity, dt, spatialGrid);
            });
        }
        
        private void UpdateVehicle(Entity entity, float dt, SpatialHashGrid spatialGrid)
        {
            var state = World.GetComponent<VehicleState>(entity);
            var @params = World.GetComponent<VehicleParams>(entity);
            ref var nav = ref World.GetComponentRef<NavState>(entity);
            
            // Determine target (position, heading, speed) based on navigation mode
            Vector2 targetPos;
            Vector2 targetHeading;
            float targetSpeed;
            
            switch (nav.Mode)
            {
                case NavigationMode.RoadGraph:
                    (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                        ref nav, state.Position, _roadNetwork);
                    break;
                    
                case NavigationMode.CustomTrajectory:
                    (targetPos, targetHeading, targetSpeed) = SampleCustomTrajectory(ref nav);
                    break;
                    
                case NavigationMode.Formation:
                    (targetPos, targetHeading, targetSpeed) = GetFormationTarget(entity);
                    break;
                    
                case NavigationMode.None:
                default:
                    // No movement
                    targetPos = state.Position;
                    targetHeading = state.Forward;
                    targetSpeed = 0f;
                    break;
            }
            
            // Calculate desired velocity
            Vector2 desiredVelocity = targetHeading * targetSpeed;
            
            // Apply collision avoidance
            Vector2 avoidanceVelocity = ApplyCollisionAvoidance(
                desiredVelocity, state.Position, state.Forward * state.Speed, 
                spatialGrid, @params);
            
            // Pure Pursuit steering
            float steerAngle = PurePursuitController.CalculateSteering(
                state.Position,
                state.Forward,
                avoidanceVelocity,
                state.Speed,
                @params.WheelBase,
                @params.LookaheadMin,
                @params.LookaheadMax,
                @params.MaxSteerAngle);
            
            // Speed control
            float targetSpeedAfterAvoidance = avoidanceVelocity.Length();
            float accel = SpeedController.CalculateAcceleration(
                state.Speed,
                targetSpeedAfterAvoidance,
                @params.SpeedGain,
                @params.MaxAccel,
                @params.MaxDecel);
            
            // Integrate bicycle model
            BicycleModel.Integrate(ref state, steerAngle, accel, dt, @params.WheelBase);
            
            // Update progress (for trajectory/road modes)
            if (nav.Mode == NavigationMode.CustomTrajectory || nav.Mode == NavigationMode.RoadGraph)
            {
                nav.ProgressS += state.Speed * dt;
            }
            
            // Write back state
            World.SetComponent(entity, state);
            World.SetComponent(entity, nav);
        }
        
        private (Vector2 pos, Vector2 heading, float speed) SampleCustomTrajectory(ref NavState nav)
        {
            var (pos, tangent, speed) = _trajectoryPool.SampleTrajectory(nav.TrajectoryId, nav.ProgressS);
            return (pos, tangent, speed);
        }
        
        private (Vector2 pos, Vector2 heading, float speed) GetFormationTarget(Entity entity)
        {
            // TODO: Formation system (BATCH-CK-08)
            // For now, return stationary
            var state = World.GetComponent<VehicleState>(entity);
            return (state.Position, state.Forward, 0f);
        }
        
        private Vector2 ApplyCollisionAvoidance(Vector2 preferredVel, Vector2 selfPos, 
            Vector2 selfVel, SpatialHashGrid spatialGrid, VehicleParams @params)
        {
            // Query neighbors within avoidance radius
            Span<(int, Vector2)> neighbors = stackalloc (int, Vector2)[32];
            int count = spatialGrid.QueryNeighbors(selfPos, @params.AvoidanceRadius * 2.5f, neighbors);
            
            if (count == 0)
                return preferredVel;
            
            // Convert to (pos, vel) format for RVO
            Span<(Vector2 pos, Vector2 vel)> neighborData = stackalloc (Vector2, Vector2)[count];
            for (int i = 0; i < count; i++)
            {
                var (entityId, pos) = neighbors[i];
                // TODO: Get neighbor velocity (for now, assume stationary)
                neighborData[i] = (pos, Vector2.Zero);
            }
            
            return RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel, neighborData,
                @params.AvoidanceRadius, @params.MaxSpeed);
        }
    }
}
```

---

### Task CK-07-03: Tests

**File:** `CarKinem.Tests/Systems/CarKinematicsSystemTests.cs`

```csharp
using System.Numerics;
using CarKinem.Core;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Systems
{
    public class CarKinematicsSystemTests
    {
        [Fact]
        public void System_UpdatesVehiclePosition()
        {
            // Setup
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            
            var roadNetwork = new RoadNetworkBuilder().Build(5f, 40, 40);
            var trajectoryPool = new TrajectoryPoolManager();
            
            var spatialSystem = new SpatialHashSystem();
            var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
            
            repo.GetSystemRegistry().RegisterSystem(spatialSystem);
            repo.GetSystemRegistry().RegisterSystem(kinematicsSystem);
            
            spatialSystem.World = repo;
            kinematicsSystem.World = repo;
            
            spatialSystem.OnCreate();
            kinematicsSystem.OnCreate();
            
            // Create vehicle
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new VehicleState
            {
                Position = Vector2.Zero,
                Forward = new Vector2(1, 0),
                Speed = 10f
            });
            
            repo.AddComponent(entity, new VehicleParams
            {
                WheelBase = 2.7f,
                MaxSpeed = 30f,
                MaxAccel = 3f,
                MaxDecel = 6f,
                MaxSteerAngle = 0.6f,
                LookaheadMin = 2f,
                LookaheadMax = 10f,
                SpeedGain = 2.0f,
                AvoidanceRadius = 2.5f
            });
            
            repo.AddComponent(entity, new NavState
            {
                Mode = NavigationMode.None
            });
            
            Vector2 initialPos = repo.GetComponent<VehicleState>(entity).Position;
            
            // Update systems
            spatialSystem.OnUpdate();
            kinematicsSystem.OnUpdate();
            
            Vector2 finalPos = repo.GetComponent<VehicleState>(entity).Position;
            
            // Vehicle should have moved (speed = 10 m/s, even small dt should move it)
            Assert.NotEqual(initialPos, finalPos);
            
            // Cleanup
            spatialSystem.OnDestroy();
            kinematicsSystem.OnDestroy();
            roadNetwork.Dispose();
            trajectoryPool.Dispose();
        }
    }
}
```

---

## ✅ Acceptance Criteria

- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 3 integration tests
- [ ] Parallel.ForEach used for vehicle updates
- [ ] All navigation modes handled (RoadGraph, CustomTrajectory, Formation, None)
- [ ] Spatial hash rebuilt each frame
- [ ] Controllers integrated correctly
- [ ] No allocations in UpdateVehicle hot path

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-07-REPORT.md`

**Time Estimate:** 6-8 hours (complex integration)

---

**Focus:** Integration correctness, parallel safety, performance.
