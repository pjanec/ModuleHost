using System;
using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Examples.CarKinem.Components;
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Time;

namespace Fdp.Examples.CarKinem.Simulation
{
    public class DemoSimulation : IDisposable
    {
        private EntityRepository _repository;
        public AsyncRecorder Recorder { get; private set; }
        public PlaybackController? PlaybackController { get; private set; }
        
        public bool IsRecording { get; set; } = true;
        public bool IsReplaying { get; private set; } = false;
        
        private bool _isPaused = false;
        public bool IsPaused 
        { 
            get => _isPaused; 
            set 
            {
                if (_isPaused != value)
                {
                    _isPaused = value;
                    OnPauseChanged(value);
                }
            } 
        }
        
        public bool SingleStep { get; set; } = false;
        
        private int _totalRecordedFrames = 0;
        private ulong _lastRecordedVersion = 0;
        
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
        
        // Distributed components
        private readonly TimeControllerConfig _timeConfig;
        private DistributedTimeCoordinator? _timeCoordinator;
        private SlaveTimeModeListener? _slaveListener;
        public DistributedTimeCoordinator? TimeCoordinator => _timeCoordinator; // Expose for testing/control
        
        public DemoSimulation(TimeControllerConfig? timeConfig = null)
        {
            _timeConfig = timeConfig ?? new TimeControllerConfig { Role = TimeRole.Standalone };
            
            _repository = new EntityRepository();
            Recorder = new AsyncRecorder("demo_recording.fdp");
            // PlaybackController initialized only on Replay start
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
            _formationTargetSystem = new FormationTargetSystem(FormationTemplates, TrajectoryPool);
            _commandSystem = new VehicleCommandSystem();
            _kinematicsSystem = new CarKinematicsSystem(RoadNetwork, TrajectoryPool);
            
            // Initialize ModuleHost kernel
            _kernel = new ModuleHostKernel(_repository, _eventAccumulator);
            
            // Configure Time
            _kernel.ConfigureTime(_timeConfig);
            
            _kernel.Initialize(); 
            
            // Initialize Synchronization Logic
            if (_timeConfig.Role == TimeRole.Master || _timeConfig.Role == TimeRole.Standalone)
            {
                // Standalone acts as Master with no slaves by default, but ready for logic
                var slaveIds = _timeConfig.AllNodeIds ?? new HashSet<int>();
                _timeCoordinator = new DistributedTimeCoordinator(_repository.Bus, _kernel, _timeConfig, slaveIds);
            }
            else
            {
                _slaveListener = new SlaveTimeModeListener(_repository.Bus, _kernel, _timeConfig);
            }
            
            // Initialize systems manually
            _spatialSystem.Create(_repository);
            _formationTargetSystem.Create(_repository);
            _commandSystem.Create(_repository);
            _kinematicsSystem.Create(_repository);
            
            _systems.AddRange(new ComponentSystem[] { 
                _spatialSystem, 
                _formationTargetSystem, 
                _commandSystem, 
                _kinematicsSystem 
            });
            
            // Initial spawn
            SpawnFastOne();
        }
        
        private List<ComponentSystem> _systems = new List<ComponentSystem>();
        public IReadOnlyList<ComponentSystem> Systems => _systems;
        
        private void RegisterComponents()
        {
            _repository.RegisterComponent<VehicleState>();
            _repository.RegisterComponent<VehicleParams>();
            _repository.RegisterComponent<NavState>();
            _repository.RegisterComponent<FormationMember>();
            _repository.RegisterComponent<FormationRoster>();
            _repository.RegisterComponent<FormationTarget>();
            _repository.RegisterComponent<VehicleColor>(); // Register Color Component
            
            _repository.RegisterEvent<CmdSpawnVehicle>();
            _repository.RegisterEvent<CmdCreateFormation>();
            _repository.RegisterEvent<CmdNavigateToPoint>();
            _repository.RegisterEvent<CmdFollowTrajectory>();
            _repository.RegisterEvent<CmdNavigateViaRoad>();
            _repository.RegisterEvent<CmdJoinFormation>();
            _repository.RegisterEvent<CmdLeaveFormation>();
            _repository.RegisterEvent<CmdStop>();
            _repository.RegisterEvent<CmdSetSpeed>();
        }
        
        public int StepFrames { get; set; }

        private void OnPauseChanged(bool paused)
        {
            if (IsReplaying) return; // Replay handles pause locally via PlaybackController

            if (paused)
            {
                // PAUSE: Switch to Deterministic
                if (_timeCoordinator != null)
                {
                    // Get slave IDs from config
                    var slaveIds = _timeConfig.AllNodeIds ?? new HashSet<int>();
                    _timeCoordinator.SwitchToDeterministic(slaveIds);
                }
            }
            else
            {
                // UNPAUSE: Switch to Continuous
                if (_timeCoordinator != null)
                {
                    _timeCoordinator.SwitchToContinuous();
                }
            }
        }

        public void Tick(float deltaTime, float timeScale)
        {
            // Replay Mode
            if (IsReplaying && PlaybackController != null)
            {
                if (!IsPaused)
                {
                    if (!PlaybackController.StepForward(_repository))
                    {
                        IsPaused = true; // End of recording
                        Console.WriteLine("Replay Finished");
                    }
                    else
                    {
                        // In replay, events are injected into the read buffer.
                        // We must NOT swap buffers.
                    }
                }
                return;
            }

            // Live / Recording Mode
            // Update distributed coordinators
            _timeCoordinator?.Update();
            _slaveListener?.Update();
            
            if (IsPaused && StepFrames > 0)
            {
                 // Handle Stepping
                 // Only step if we are actually in Deterministic mode (pause completed)
                 if (_kernel.GetTimeController().GetMode() == TimeMode.Deterministic)
                 {
                     const float FIXED_STEP_DT = 1.0f / 60.0f;
                     _kernel.StepFrame(FIXED_STEP_DT);
                     StepFrames--;
                 }
                 else
                 {
                     // Still coasting to barrier -> Normal Update
                     _kernel.Update();
                 }
            }
            else
            {
                // Normal Update
                // If Continuous: Advances time
                // If Deterministic (Paused): Returns frozen time (delta 0)
                _kernel.Update();
            }
            
            // Required for Versioning to work with AsyncRecorder
            // _repository.Tick(); // Handled by _kernel.Update
            
            // Swap events (Input -> Current)
            // _repository.Bus.SwapBuffers(); // Handled by _kernel.Update

            // Update Kernel (Handling Tick, Input, Swap, Capture, Dispatch)
            // Note: Since we use Standalone Master Controller, we set the scale, and it measures 
            // wall clock internally. 'deltaTime' passed from Raylib is ignored by TimeController 
            // in Continuous mode, but we assume they match reasonably well.
            // If we wanted to FORCE deltaTime, we'd need Stepped mode or Manual update.
            // For now, we trust the controller (Stopwatch) to be accurate.
            
            if (_timeConfig.Role != TimeRole.Slave)
            {
                _kernel.SetTimeScale(timeScale);
            }
            
            // _timeCoordinator?.Update(); // Already called above
            // _slaveListener?.Update();   // Already called above
            
            // _kernel.Update(); // Already called above

            // Manual System Updates (if not registered as Modules)
            // Ideally these should be modules, but legacy structure is fine.
            // Note: Kernel.Update() already advanced time and singleton.
            
            // Note: kernel update increments GlobalVersion.
            // Systems below run AFTER kernel update.

            
            _spatialSystem.Run();
            _formationTargetSystem.Run();
            _commandSystem.Run();
            _kinematicsSystem.Run();
            
            UpdateWaypointQueues();
            UpdateRoamers();
            
            // Record Frame
            // Record Frame
            if (IsRecording && Recorder != null)
            {
                bool isKeyframe = (_totalRecordedFrames % 60 == 0);
                
                if (isKeyframe)
                {
                     Recorder.CaptureKeyframe(_repository, false, _repository.Bus);
                }
                else
                {
                     Recorder.CaptureFrame(_repository, (uint)_lastRecordedVersion, false, _repository.Bus);
                }
                
                _totalRecordedFrames++;
                _lastRecordedVersion = _repository.GlobalVersion;
            }
        }
        
        public void StartReplay()
        {
            if (IsReplaying) return;
            
            // flush recorder
            Recorder?.Dispose();
            
            IsRecording = false;
            IsReplaying = true;
            IsPaused = true; // Start paused
            
            // Reset repository logic? 
            // In Showcase, it reuses the repo but clears it? Or PlaybackController handles it?
            // PlaybackController.StartEncodedPlayback clears the repo usually.
            
            // Start Replay
            try 
            {
                PlaybackController = new PlaybackController("demo_recording.fdp");
                PlaybackController.EventBus = _repository.Bus;
                PlaybackController.Rewind(_repository);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start replay: {ex.Message}");
                IsReplaying = false;
                IsRecording = true;
                Recorder = new AsyncRecorder("demo_recording.fdp");
            }
        }
        
        public void StopReplay()
        {
            if (!IsReplaying) return;
            
            PlaybackController?.Dispose();
            PlaybackController = null;
            
            IsReplaying = false;
            // Restore live mode (might be broken state if we don't reload completely)
            // Ideally we restart simulation. For now, just flag it.
            // Re-enable recording
            // Re-enable recording
            Recorder = new AsyncRecorder("demo_recording.fdp");
            IsRecording = true;
            _totalRecordedFrames = 0;
            _lastRecordedVersion = _repository.GlobalVersion;
        }
        
        public void StepForward(int frames = 1)
        {
            IsPaused = true;
            if (IsReplaying && PlaybackController != null)
            {
                // Seek to next frame
                // CurrentFrame tracks the current frame index.
                // So seeking to CurrentFrame + frames advances steps.
                int target = Math.Min(PlaybackController.CurrentFrame + frames, PlaybackController.TotalFrames - 1);
                PlaybackController.SeekToFrame(_repository, target);
            }
            else
            {
                StepFrames = frames;
            }
        }

        public void StepBackward(int frames = 1)
        {
            if (!IsReplaying || PlaybackController == null) return;
            
            IsPaused = true;
            // Seek to previous frame (clamped to 0)
            int target = Math.Max(0, PlaybackController.CurrentFrame - frames);
            PlaybackController.SeekToFrame(_repository, target);
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
                     
                     // Use linear for new segments by default to avoid Hermite looping issues with random points
                     // Or check existing?
                     // Let's default to Linear for random roamers to be safe, unless we track it
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
            
            // Set Default Color (White/Gray) or Class Color?
            // Let's default to standard class color, but wrapped in component
            // We'll use GreenYellow as "Standard Spawned" default per previous request
            _repository.AddComponent(e, VehicleColor.GreenYellow);
            
            _repository.AddComponent(e, new NavState {
                Mode = NavigationMode.None
            });
            
            return e.Index;
        }
        
        public void AddWaypoint(int entityIndex, Vector2 destination, TrajectoryInterpolation interpolation = TrajectoryInterpolation.Linear)
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
             int trajId = TrajectoryPool.RegisterTrajectory(path.ToArray(), speeds, false, interpolation);
             
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
        
        public void SetDestination(int entityIndex, Vector2 destination, TrajectoryInterpolation interpolation = TrajectoryInterpolation.Linear)
        {
             if (_waypointQueues.ContainsKey(entityIndex))
             {
                 _waypointQueues[entityIndex].Clear();
             }
             AddWaypoint(entityIndex, destination, interpolation);
        }

        // Deprecated compatibility wrapper
        public void IssueMoveToPointCommand(int entityIndex, Vector2 destination) => AddWaypoint(entityIndex, destination);
        public void IssueMoveToPointCommand(Entity entity, Vector2 destination) => AddWaypoint(entity.Index, destination);
        public void IssueMoveToPointCommand(int entityIndex, Vector2 destination, TrajectoryInterpolation interpolation) => AddWaypoint(entityIndex, destination, interpolation);

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

        public void SpawnFastOne()
        {
            if (!RoadNetwork.Nodes.IsCreated || RoadNetwork.Nodes.Length < 2) return;
            
            // Pick two random nodes
            int startIdx = _rng.Next(0, RoadNetwork.Nodes.Length);
            int endIdx = _rng.Next(0, RoadNetwork.Nodes.Length);
            while (startIdx == endIdx) endIdx = _rng.Next(0, RoadNetwork.Nodes.Length);
            
            var startNode = RoadNetwork.Nodes[startIdx];
            var endNode = RoadNetwork.Nodes[endIdx];
            
            int id = SpawnVehicle(startNode.Position, new Vector2(1,0), global::CarKinem.Core.VehicleClass.PersonalCar);
            
            Entity entity = new Entity(id, 1);
            
            // Boost speed
            var vParams = _repository.GetComponent<VehicleParams>(entity);
            vParams.MaxSpeedFwd = 50.0f; // Very fast
            vParams.MaxAccel = 10.0f;     // High acceleration
            vParams.MaxLatAccel = 15.0f;  // Grip
            // Initialization: Direct modification is safe here (Single thread setup).
            // For runtime updates, prefer Command Buffers or Events.
            // _repository.SetComponent(entity, vParams); // Already ref returned? No, struct copy.
             _repository.SetComponent(entity, vParams);
            
            // Set Color (Visual hack? Or does renderer use class color?)
            // We can't change color easily as it's static in VehiclePresets/Class.
            // But speed is enough.
            
            _repository.Bus.Publish(new CmdNavigateViaRoad {
                 Entity = entity,
                 Destination = endNode.Position,
                 ArrivalRadius = 5.0f
            });
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
                
                // Assign Road User Color (Blue)
                var entity = new Entity(id, 1);
                _repository.SetComponent(entity, VehicleColor.Blue);
                
                _repository.Bus.Publish(new CmdNavigateViaRoad {
                     Entity = entity,
                     Destination = endNode.Position,
                     ArrivalRadius = 5.0f
                });
            }
        }

        public void SpawnRoamers(int count, global::CarKinem.Core.VehicleClass vClass, TrajectoryInterpolation interpolation = TrajectoryInterpolation.Linear)
        {
            for(int i=0; i<count; i++)
            {
                 Vector2 pos = new Vector2(_rng.Next(0,500), _rng.Next(0,500));
                 // Fix: Ensure non-zero heading to prevent static position bug
                 Vector2 heading = new Vector2((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f);
                 if (heading == Vector2.Zero) heading = new Vector2(1, 0);
                 else heading = Vector2.Normalize(heading);

                 int id = SpawnVehicle(pos, heading, vClass);
                 // Assign Roamer Color (Orange)
                 _repository.SetComponent(new Entity(id, 1), VehicleColor.Orange);
                 
                 _roamingEntities.Add(id);
                 SetDestination(id, new Vector2(_rng.Next(0,500), _rng.Next(0,500)), interpolation);
            }
        }

        public void SpawnFormation(global::CarKinem.Core.VehicleClass vClass, FormationType type, int count, TrajectoryInterpolation interpolation)
        {
             // 1. Pick Start Position
             Vector2 startPos = new Vector2(_rng.Next(100, 400), _rng.Next(100, 400));
             Vector2 heading = new Vector2(1, 0); // East facing default
             
             // 2. Spawn Leader
             int leaderId = SpawnVehicle(startPos, heading, vClass);
             var leaderEntity = new Entity(leaderId, 1);
             
             // 3. Create Formation Command
             _repository.Bus.Publish(new CmdCreateFormation
             {
                 LeaderEntity = leaderEntity,
                 Type = type,
                 Params = new FormationParams 
                 {
                     Spacing = 12.0f,
                     WedgeAngleRad = 0.5f,
                     MaxCatchUpFactor = 1.25f,
                     BreakDistance = 50.0f,
                     ArrivalThreshold = 2.0f,
                     SpeedFilterTau = 1.0f
                 }
             });
             
             // 4. Spawn Followers
             // Retrieve the template to spawn them in correct positions immediately
             var template = FormationTemplates.GetTemplate(type);
             
             for (int i = 0; i < count - 1; i++) // count includes leader
             {
                 // Calculate slot position based on template
                 // Note: slot index i corresponds to the i-th follower
                 Vector2 followerPos = template.GetSlotPosition(i, startPos, heading);
                 
                 int followerId = SpawnVehicle(followerPos, heading, vClass);
                 var followerEntity = new Entity(followerId, 1);
                 // Assign Follower Color (Cyan)
                 _repository.SetComponent(followerEntity, VehicleColor.Cyan);
                 
                 _repository.Bus.Publish(new CmdJoinFormation
                 {
                     Entity = followerEntity,
                     LeaderEntity = leaderEntity,
                     SlotIndex = i // 0-indexed slots for followers
                 });
             }
             
             // 5. Move Leader
             // Give it a destination so they start moving
             Vector2 dest = startPos + new Vector2(200, 0);
             SetDestination(leaderId, dest, interpolation);
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
            Recorder?.Dispose();
            PlaybackController?.Dispose();
        }
    }
}
