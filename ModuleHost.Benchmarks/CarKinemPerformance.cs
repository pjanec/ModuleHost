using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CarKinem.Core;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Kernel;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class CarKinemPerformance
    {
        private EntityRepository _repo;
        private SpatialHashSystem _spatialSystem;
        private CarKinematicsSystem _kinematicsSystem;
        private RoadNetworkBlob _roadNetwork;
        private TrajectoryPoolManager _trajectoryPool;
        
        [Params(1000, 10000, 50000)]
        public int VehicleCount;
        
        [GlobalSetup]
        public void Setup()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<VehicleState>();
            _repo.RegisterComponent<VehicleParams>();
            _repo.RegisterComponent<NavState>();
            _repo.RegisterComponent<SpatialGridData>();
            
            // Register GlobalTime
            _repo.RegisterComponent<GlobalTime>();
            _repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.016f, TimeScale = 1.0f });
            
            // Create minimal road network
            _roadNetwork = new RoadNetworkBuilder().Build(5f, 100, 100);
            _trajectoryPool = new TrajectoryPoolManager();
            
            // Create systems
            _spatialSystem = new SpatialHashSystem();
            _kinematicsSystem = new CarKinematicsSystem(_roadNetwork, _trajectoryPool);
            
            _spatialSystem.Create(_repo);
            _kinematicsSystem.Create(_repo);
            
            // Spawn vehicles in grid pattern
            var random = new Random(42);
            for (int i = 0; i < VehicleCount; i++)
            {
                var entity = _repo.CreateEntity();
                
                // Grid distribution
                int gridSize = (int)Math.Ceiling(Math.Sqrt(VehicleCount));
                int x = (i % gridSize) * 20;
                int y = (i / gridSize) * 20;
                
                _repo.AddComponent(entity, new VehicleState
                {
                    Position = new Vector2(x, y),
                    Forward = new Vector2(1, 0),
                    Speed = (float)(random.NextDouble() * 20 + 5)  // 5-25 m/s
                });
                
                _repo.AddComponent(entity, new VehicleParams
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
                
                _repo.AddComponent(entity, new NavState
                {
                    Mode = NavigationMode.None
                });
            }
        }
        
        [Benchmark]
        public void UpdateKinematics()
        {
            _spatialSystem.Run();
            _kinematicsSystem.Run();
        }
        
        [GlobalCleanup]
        public void Cleanup()
        {
            _spatialSystem.Dispose();
            _kinematicsSystem.Dispose();
            _roadNetwork.Dispose();
            _trajectoryPool.Dispose();
            _repo.Dispose();
        }
    }
}
