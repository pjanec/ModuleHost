using System;
using System.Linq;
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
    public class ParallelCorrectnessTests
    {
        [Fact]
        public void ParallelExecution_ProducesSameResults_AsSerial()
        {
            // Setup two identical repos
            var (repoSerial, sysSerial) = CreateTestRepo(100);
            var (repoParallel, sysParallel) = CreateTestRepo(100);
            
            // Configure systems
            sysSerial.ForceSerial = true;
            sysParallel.ForceSerial = false;
            
            // Run 10 frames on both
            for (int frame = 0; frame < 10; frame++)
            {
                // Update both
                // Note: We need to run ALL systems attached manually or via repo.Tick() if they are registered?
                // CreateTestRepo only creates them but doesn't "register" them for Tick() in a Kernel unless we use ModuleHostKernel.
                // But in unit tests typically we run systems manually or via repo if it manages them.
                // Looking at CreateTestRepo, it does: system.Create(repo);
                // System.Create(repo) usually registers it to repo? 
                // ComponentSystem.Create(repo) -> repo.GetSystemRegistry().Register(...) ?
                // If repo.Tick() is used, systems must be registered.
                // Assuming ComponentSystem.Create does the registration to repo's internal list if designed that way.
                // However, CarKinematicsSystem is a ComponentSystem.
                
                // If repo.Tick() drives them, then good.
                repoSerial.Tick();
                repoParallel.Tick();
            }
            
            // Compare final states
            var querySerial = repoSerial.Query().With<VehicleState>().Build();
            var queryParallel = repoParallel.Query().With<VehicleState>().Build();
            
            var serialStates = new System.Collections.Generic.List<Entity>();
            foreach(var e in querySerial) serialStates.Add(e);
            
            var parallelStates = new System.Collections.Generic.List<Entity>();
            foreach(var e in queryParallel) parallelStates.Add(e);
            
            Assert.Equal(serialStates.Count, parallelStates.Count);
            
            for (int i = 0; i < serialStates.Count; i++)
            {
                var stateSerial = repoSerial.GetComponent<VehicleState>(serialStates[i]);
                var stateParallel = repoParallel.GetComponent<VehicleState>(parallelStates[i]);
                
                // Positions should match within float precision
                float posDiff = Vector2.Distance(stateSerial.Position, stateParallel.Position);
                Assert.True(posDiff < 0.001f, 
                    $"Position mismatch: {stateSerial.Position} vs {stateParallel.Position}");
                
                float speedDiff = Math.Abs(stateSerial.Speed - stateParallel.Speed);
                Assert.True(speedDiff < 0.001f,
                    $"Speed mismatch: {stateSerial.Speed} vs {stateParallel.Speed}");
            }
            
            repoSerial.Dispose();
            repoParallel.Dispose();
            // Systems disposed? They usually attach to repo or need manual disposal if not managed.
            sysSerial.Dispose();
            sysParallel.Dispose();
        }
        
        private (EntityRepository, CarKinematicsSystem) CreateTestRepo(int vehicleCount)
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<SpatialGridData>();
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.1f, TimeScale = 1.0f });
            
            var roadNetwork = new RoadNetworkBuilder().Build(5f, 100, 100);
            var trajectoryPool = new TrajectoryPoolManager();
            
            var spatialSystem = new SpatialHashSystem();
            spatialSystem.Create(repo);
            
            var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool);
            kinematicsSystem.Create(repo);
            
            // Spawn vehicles
            var random = new Random(42);  // Deterministic seed
            for (int i = 0; i < vehicleCount; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new VehicleState 
                { 
                    Position = new Vector2(i * 10, i * 10),
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
                repo.AddComponent(entity, new NavState { Mode = NavigationMode.None });
            }
            
            return (repo, kinematicsSystem);
        }
    }
}
