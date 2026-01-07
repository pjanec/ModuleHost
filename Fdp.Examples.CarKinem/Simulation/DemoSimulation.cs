using System;
using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.CarKinem.Simulation
{
    public class DemoSimulation : IDisposable
    {
        private EntityRepository _repository;
        private AsyncRecorder _recorder;
        private ModuleHostKernel _kernel;
        private EventAccumulator _eventAccumulator;
        
        // Waypoint queues for multiple entities
        private Dictionary<int, List<Vector2>> _waypointQueues = new Dictionary<int, List<Vector2>>();
        private HashSet<int> _roamingEntities = new HashSet<int>();
        private Random _rng = new Random();

        private SpatialHashSystem _spatialSystem;
        private FormationTargetSystem _formationTargetSystem;

        private VehicleCommandSystem _commandSystem;
        private CarKinematicsSystem _kinematicsSystem;
        
        public RoadNetworkBlob RoadNetwork { get; private set; }
        public TrajectoryPoolManager TrajectoryPool { get; private set; }
        public FormationTemplateManager FormationTemplates { get; private set; }
        public ISimulationView View => _repository;
        public EntityRepository Repository => _repository;
        
        public DemoSimulation()
        {
            _repository = new EntityRepository();
            _recorder = new AsyncRecorder("demo_recording.fdp");
            _eventAccumulator = new EventAccumulator();
            
            RegisterComponents();
            
            // Register GlobalTime singleton
            _repository.RegisterComponent<GlobalTime>();
            _repository.SetSingletonUnmanaged(new GlobalTime());
            
            // Load road network
            RoadNetwork = new RoadNetworkBlob(); // Placeholder or load
            try {
                 RoadNetwork = RoadNetworkLoader.LoadFromJson("Assets/sample_road.json");
            } catch (Exception ex) {
                Console.WriteLine($"Warning: Could not load sample_road.json: {ex.Message}");
                Console.WriteLine("Using empty network");
            }
            
            // Create managers
            TrajectoryPool = new TrajectoryPoolManager();
            FormationTemplates = new FormationTemplateManager();
            
            // Create systems
            _spatialSystem = new SpatialHashSystem();
            _formationTargetSystem = new FormationTargetSystem(FormationTemplates);
            _commandSystem = new VehicleCommandSystem();
            _kinematicsSystem = new CarKinematicsSystem(RoadNetwork, TrajectoryPool);
            
            // Initialize ModuleHost kernel
            _kernel = new ModuleHostKernel(_repository, _eventAccumulator);
            _kernel.Initialize(); 
            
            // Initialize systems manually
            _spatialSystem.Create(_repository);
            _formationTargetSystem.Create(_repository);
            _commandSystem.Create(_repository);
            _kinematicsSystem.Create(_repository);
        }
        
        private void RegisterComponents()
        {
            _repository.RegisterComponent<VehicleState>();
            _repository.RegisterComponent<VehicleParams>();
            _repository.RegisterComponent<NavState>();
            _repository.RegisterComponent<FormationMember>();
            _repository.RegisterComponent<FormationRoster>();
            _repository.RegisterComponent<FormationTarget>();
            
            _repository.RegisterEvent<CmdNavigateToPoint>();
            _repository.RegisterEvent<CmdFollowTrajectory>();
            _repository.RegisterEvent<CmdNavigateViaRoad>();
            _repository.RegisterEvent<CmdJoinFormation>();
            _repository.RegisterEvent<CmdLeaveFormation>();
            _repository.RegisterEvent<CmdStop>();
            _repository.RegisterEvent<CmdSetSpeed>();
        }
        
        public void Tick(float deltaTime)
        {
            // Swap events (Input -> Current)
            _repository.Bus.SwapBuffers();

            // Update Kernel first to process/swap events
            _kernel.Update(deltaTime);

            ref var time = ref _repository.GetSingletonUnmanaged<GlobalTime>();
            time.DeltaTime = deltaTime;
            time.TotalTime += deltaTime;
            time.FrameCount++;
            
            _spatialSystem.Run();
            _formationTargetSystem.Run();
            _commandSystem.Run();
            _kinematicsSystem.Run();
            
            UpdateWaypointQueues();
            UpdateRoamers();
        }

        private void UpdateRoamers()
        {
            foreach(var id in new List<int>(_roamingEntities))
            {
                var entity = new Entity(id, 1);
                if (!_repository.IsAlive(entity)) { _roamingEntities.Remove(id); continue; }
                
                var nav = _repository.GetComponent<NavState>(entity);
                if (nav.HasArrived == 1)
                {
                     // Pick new point
                     SetDestination(id, new Vector2(_rng.Next(0,500), _rng.Next(0,500)));
                }
            }
        }

        private void UpdateWaypointQueues()
        {
            // Prune visited waypoints
            // Create list of keys to allow modification of dictionary
            foreach (var entityIndex in new List<int>(_waypointQueues.Keys))
            {
                var queue = _waypointQueues[entityIndex];
                if (queue.Count == 0) continue;
                
                // Assume generation 1 for simplicity in this demo
                var entity = new Entity(entityIndex, 1); 
                if (!_repository.IsAlive(entity)) {
                     _waypointQueues.Remove(entityIndex);
                     continue;
                }

                var state = _repository.GetComponent<VehicleState>(entity);
                
                // Check distance to next target
                // If close enough, remove it from queue (it's been "visited")
                if (Vector2.Distance(state.Position, queue[0]) < 8.0f) 
                {
                     queue.RemoveAt(0);
                     // We don't need to rebuild trajectory here; the current trajectory 
                     // is valid until we add a NEW point.
                }
            }
        }
        
        public int SpawnVehicle(Vector2 position, Vector2 heading, global::CarKinem.Core.VehicleClass vehicleClass = global::CarKinem.Core.VehicleClass.PersonalCar)
        {
            var e = _repository.CreateEntity();
            
            _repository.AddComponent(e, new VehicleState { 
                Position = position, 
                Forward = heading,
                Speed = 0,
                SteerAngle = 0
            });
            
            // Use preset for the vehicle class
            var preset = global::CarKinem.Core.VehiclePresets.GetPreset(vehicleClass);
            preset.Class = vehicleClass; // Set the class field
            _repository.AddComponent(e, preset);
            
            _repository.AddComponent(e, new NavState {
                Mode = NavigationMode.None
            });
            
            return e.Index;
        }
        
        public void AddWaypoint(int entityIndex, Vector2 destination)
        {
             // 1. Get/Create Queue
             if (!_waypointQueues.ContainsKey(entityIndex))
             {
                 _waypointQueues[entityIndex] = new List<Vector2>();
             }
             
             // 2. Add to Queue
             _waypointQueues[entityIndex].Add(destination);
             
             // 3. Construct Trajectory from Current Position
             // Assume generation 1
             var entity = new Entity(entityIndex, 1);
             if (!_repository.IsAlive(entity)) return;

             var state = _repository.GetComponent<VehicleState>(entity);
             
             var path = new List<Vector2>();
             path.Add(state.Position);
             path.AddRange(_waypointQueues[entityIndex]);
             
             // 4. Create Speeds (Cruise=15, Stop=0 at end)
             var speeds = new float[path.Count];
             for(int i=0; i<speeds.Length; i++) speeds[i] = 15.0f;
             speeds[speeds.Length - 1] = 0.0f; // Stop at end
             
             // 5. Register new Trajectory
             int trajId = TrajectoryPool.RegisterTrajectory(path.ToArray(), speeds, false);
             
             // Cleanup old trajectory to prevent leaks
             // Only if we were strictly following a custom trajectory before
             var oldNav = _repository.GetComponent<NavState>(entity);
             if (oldNav.Mode == NavigationMode.CustomTrajectory && oldNav.TrajectoryId > 0)
             {
                 TrajectoryPool.RemoveTrajectory(oldNav.TrajectoryId);
             }
             
             // 6. Issue Command (Reset progress to 0)
             _repository.Bus.Publish(new CmdFollowTrajectory {
                Entity = entity,
                TrajectoryId = trajId
            });
        }
        
        public void SetDestination(int entityIndex, Vector2 destination)
        {
             if (_waypointQueues.ContainsKey(entityIndex))
             {
                 _waypointQueues[entityIndex].Clear();
             }
             AddWaypoint(entityIndex, destination);
        }

        // Deprecated compatibility wrapper
        public void IssueMoveToPointCommand(int entityIndex, Vector2 destination) => AddWaypoint(entityIndex, destination);
        public void IssueMoveToPointCommand(Entity entity, Vector2 destination) => AddWaypoint(entity.Index, destination);

        public void SpawnCollisionTest(global::CarKinem.Core.VehicleClass vClass)
        {
            // 5 pairs attacking each other
            for(int i=0; i<5; i++)
            {
                 Vector2 center = new Vector2(250 + i * 20, 250 + i * 20); 
                 Vector2 offset = new Vector2(40, 0);
                 
                 int idA = SpawnVehicle(center - offset, new Vector2(1, 0), vClass);
                 SetDestination(idA, center + offset);
                 
                 int idB = SpawnVehicle(center + offset, new Vector2(-1, 0), vClass);
                 SetDestination(idB, center - offset);
            }
        }

        public void SpawnRoadUsers(int count, global::CarKinem.Core.VehicleClass vClass)
        {
            if (!RoadNetwork.Nodes.IsCreated || RoadNetwork.Nodes.Length < 2) return;
            
            for(int i=0; i<count; i++)
            {
                int startNodeIdx = _rng.Next(0, RoadNetwork.Nodes.Length);
                var startNode = RoadNetwork.Nodes[startNodeIdx];
                int endNodeIdx = _rng.Next(0, RoadNetwork.Nodes.Length);
                var endNode = RoadNetwork.Nodes[endNodeIdx];
                
                int id = SpawnVehicle(startNode.Position, new Vector2(1,0), vClass);
                
                var entity = new Entity(id, 1);
                _repository.Bus.Publish(new CmdNavigateViaRoad {
                     Entity = entity,
                     Destination = endNode.Position,
                     ArrivalRadius = 5.0f
                });
            }
        }

        public void SpawnRoamers(int count, global::CarKinem.Core.VehicleClass vClass)
        {
            for(int i=0; i<count; i++)
            {
                 Vector2 pos = new Vector2(_rng.Next(0,500), _rng.Next(0,500));
                 int id = SpawnVehicle(pos, Vector2.Zero, vClass);
                 
                 _roamingEntities.Add(id);
                 SetDestination(id, new Vector2(_rng.Next(0,500), _rng.Next(0,500)));
            }
        }

        public NavState GetNavState(int entityIndex)
        {
            // Assuming generation 1 for demo purposes if we don't have the entity handle
            return GetNavState(new Entity(entityIndex, 1));
        }

        public NavState GetNavState(Entity entity)
        {
             if (View.IsAlive(entity))
             {
                 return View.GetComponentRO<NavState>(entity);
             }
             return new NavState();
        }
        
        public void Dispose()
        {
            _spatialSystem.Dispose();
            _kinematicsSystem.Dispose();
            RoadNetwork.Dispose();
            TrajectoryPool.Dispose();
            FormationTemplates.Dispose();
            _kernel.Dispose();
            _repository.Dispose();
        }
    }
}
