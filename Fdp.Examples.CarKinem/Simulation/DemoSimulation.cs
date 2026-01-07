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
        
        public void IssueMoveToPointCommand(int entityIndex, Vector2 destination)
        {
             // For demo, we are restricted by public API not having Entity.
             // We will try to construct Entity assuming generation 1.
             // This is BRITTLE but allows compilation for now.
             // In a real app we'd pass Entity instances around.
             IssueMoveToPointCommand(new Entity(entityIndex, 1), destination);
        }

        public void IssueMoveToPointCommand(Entity entity, Vector2 destination)
        {
             // Direct access is safe here because we are on the Main Thread
             // and we want the event to be picked up in the CURRENT frame's execution logic if possible.
             _repository.Bus.Publish(new CmdNavigateToPoint {
                Entity = entity,
                Destination = destination,
                Speed = 10.0f,
                ArrivalRadius = 2.0f
            });
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
