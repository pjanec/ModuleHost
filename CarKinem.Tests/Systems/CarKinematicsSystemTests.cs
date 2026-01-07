using System;
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
            repo.RegisterComponent<SpatialGridData>();
            
            // Register GlobalTime for DeltaTime
            repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.016f, TimeScale = 1.0f });

            var roadNetwork = new RoadNetworkBuilder().Build(5f, 40, 40);
            var trajectoryPool = new TrajectoryPoolManager();
            
            var spatialSystem = new SpatialHashSystem();
            var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
            
            spatialSystem.Create(repo);
            kinematicsSystem.Create(repo);
            
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
                MaxSpeedFwd = 30f,
                MaxAccel = 3f,
                MaxDecel = 6f,
                MaxSteerAngle = 0.6f,
                LookaheadTimeMin = 2f,
                LookaheadTimeMax = 10f,
                AccelGain = 2.0f,
                AvoidanceRadius = 2.5f
            });
            
            repo.AddComponent(entity, new NavState
            {
                Mode = NavigationMode.None
            });
            
            Vector2 initialPos = repo.GetComponent<VehicleState>(entity).Position;
            
            // Update systems
            spatialSystem.Run();
            kinematicsSystem.Run();
            
            // Verify singleton exists
            Assert.True(repo.HasSingleton<SpatialGridData>());
            
            Vector2 finalPos = repo.GetComponent<VehicleState>(entity).Position;
            
            // Vehicle should have moved (speed = 10 m/s, dt = 0.016 -> 0.16m move)
            Assert.NotEqual(initialPos, finalPos);
            
            // Cleanup
            spatialSystem.Dispose();
            kinematicsSystem.Dispose();
            roadNetwork.Dispose();
            trajectoryPool.Dispose();
            repo.Dispose();
        }

        [Fact]
        public void System_AvoidanceMovesVehicle()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<SpatialGridData>();
            repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.1f, TimeScale = 1.0f }); // Large DT

            var roadNetwork = new RoadNetworkBuilder().Build(5f, 40, 40);
            var trajectoryPool = new TrajectoryPoolManager();
            var spatialSystem = new SpatialHashSystem();
            var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
            
            spatialSystem.Create(repo);
            kinematicsSystem.Create(repo);

            // Create Entity A moving East at (0,0)
            var entA = repo.CreateEntity();
            repo.AddComponent(entA, new VehicleState { Position = new Vector2(0, 0), Forward = new Vector2(1, 0), Speed = 5f });
            repo.AddComponent(entA, new NavState { Mode = NavigationMode.None }); // Move straight
            repo.AddComponent(entA, new VehicleParams { 
                WheelBase = 2.0f, MaxSpeedFwd=10f, MaxAccel=10f, MaxDecel=10f, MaxSteerAngle=1f, 
                LookaheadTimeMin=1f, LookaheadTimeMax=2f, AccelGain=1f, AvoidanceRadius=2.0f 
            });

            // Create Entity B at (2, 0) stationary (Blocking path)
            var entB = repo.CreateEntity();
            repo.AddComponent(entB, new VehicleState { Position = new Vector2(2, 0), Forward = new Vector2(1, 0), Speed = 0f });
            repo.AddComponent(entB, new NavState { Mode = NavigationMode.None });
            repo.AddComponent(entB, new VehicleParams { AvoidanceRadius=2.0f });

            // Run update
            spatialSystem.Run();
            kinematicsSystem.Run(); // A should steer or decelerate/avoid

            var posA = repo.GetComponent<VehicleState>(entA).Position;
            var fwdA = repo.GetComponent<VehicleState>(entA).Forward;

            // Simple check: Should not be exactly at (0.5, 0) [5*0.1]. 
            // Avoidance might push it sideways or slow it down.
            // RVO usually adjusts velocity.
            // If avoidance works, the vehicle should have a lateral component or change in heading differ from straight east?
            // Or just check that it updated position.
            
            // Simple check: Should not be exactly at (0.5, 0) if avoidance kicked in.
            // Expected position if no avoidance: (0 + 5*0.1, 0) = (0.5, 0)
            Vector2 expectedNoAvoidance = new Vector2(0.5f, 0f);
            Assert.True(Vector2.Distance(posA, expectedNoAvoidance) > 0.001f, 
                $"Vehicle did not react to obstacle. Pos: {posA}, Expected: {expectedNoAvoidance}");

            spatialSystem.Dispose();
            kinematicsSystem.Dispose();
            roadNetwork.Dispose();
            trajectoryPool.Dispose();
            repo.Dispose();
        }

        [Fact]
        public void System_FollowsTrajectory()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<SpatialGridData>();
            repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.1f, TimeScale = 1.0f });

            var roadNetwork = new RoadNetworkBuilder().Build(5f, 40, 40);
            var trajectoryPool = new TrajectoryPoolManager();
            // Create a simple trajectory: (0,0) to (100,0)
            int trajId = trajectoryPool.RegisterTrajectory(new[] { new Vector2(0,0), new Vector2(100,0) });

            var spatialSystem = new SpatialHashSystem();
            var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
            
            spatialSystem.Create(repo);
            kinematicsSystem.Create(repo);

            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new VehicleState { Position = new Vector2(0, 0), Forward = new Vector2(1, 0), Speed = 10f });
            repo.AddComponent(entity, new NavState { Mode = NavigationMode.CustomTrajectory, TrajectoryId = trajId, ProgressS = 0f });
            repo.AddComponent(entity, new VehicleParams { 
                WheelBase = 2.0f, MaxSpeedFwd=20f, MaxAccel=10f, MaxDecel=10f, MaxSteerAngle=1f, 
                LookaheadTimeMin=1f, LookaheadTimeMax=2f, AccelGain=1f, AvoidanceRadius=2.0f 
            });

            // Update
            spatialSystem.Run(); // Build grid
            kinematicsSystem.Run();

            // Check ProgressS increased
            var nav = repo.GetComponent<NavState>(entity);
            // Expected progress: 10m/s * 0.1s = 1.0m (approx, assuming constant speed)
            Assert.True(nav.ProgressS > 0.5f, "Progress should advance");

            // Cleanup
            spatialSystem.Dispose();
            kinematicsSystem.Dispose();
            roadNetwork.Dispose();
            trajectoryPool.Dispose();
            repo.Dispose();
        }
    }
}
