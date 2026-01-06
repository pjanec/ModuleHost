# BATCH-CK-10: Integration & Demo Application

**Batch ID:** BATCH-CK-10  
**Phase:** Integration  
**Prerequisites:** ALL previous batches (CK-01 through CK-09) COMPLETE  
**Assigned:** TBD  

---

## 📋 Objectives

Create complete demo application and validate entire system:
1. Fdp.Examples.CarKinem project (Raylib + ImGui)
2. All rendering systems (road, vehicles, trajectories, formations)
3. All interaction systems (selection, path editing, camera)
4. All UI panels (spawn, formation, simulation, inspector)
5. Performance validation (1,000+ vehicles @ 60 FPS)
6. Documentation and sample scenarios

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-demo-design.md`  
**Full Demo Design:** Complete specification in this document

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\Fdp.Examples.CarKinem\
├── Fdp.Examples.CarKinem.csproj
├── Program.cs
├── Rendering/
│   ├── RoadRenderer.cs
│   ├── VehicleRenderer.cs
│   ├── TrajectoryRenderer.cs
│   ├── FormationRenderer.cs
│   └── DebugLabelRenderer.cs
├── Input/
│   ├── InputManager.cs
│   ├── SelectionManager.cs
│   └── PathEditingMode.cs
├── UI/
│   ├── MainUI.cs
│   ├── SpawnControlsPanel.cs
│   ├── FormationControlsPanel.cs
│   ├── SimulationControlsPanel.cs
│   ├── InspectorPanel.cs
│   └── PerformancePanel.cs
├── Simulation/
│   ├── DemoSimulation.cs
│   └── DemoModule.cs
└── Assets/
    ├── sample_road.json
    └── README.md
```

---

## 🎯 Implementation Phases

### Phase 1: Project Setup & Basic Rendering (3-4 hours)

**Project Setup:**
```bash
dotnet new console -n Fdp.Examples.CarKinem
cd Fdp.Examples.CarKinem
dotnet add package Raylib-cs
dotnet add package ImGui.NET
dotnet add package ImGuiNET.Raylib-cs
dotnet add reference ../../CarKinem/CarKinem.csproj
dotnet add reference ../../FDP/Fdp.Kernel/Fdp.Kernel.csproj
dotnet add reference ../../ModuleHost.Core/ModuleHost.Core.csproj
```

**Tasks:**
- [ ] **CK-10-01**: Program.cs main loop
  - Raylib window initialization (1280x720)
  - ImGui setup and integration
  - Main render loop (60 FPS target)
  - Camera system implementation

- [ ] **CK-10-02**: RoadRenderer
  - Hermite curve rendering (32 samples per segment)
  - Lane marker rendering
  - Node (junction) rendering
  - Spatial grid visualization (debug mode)

- [ ] **CK-10-03**: VehicleRenderer
  - Oriented box rendering (4 corners)
  - Front indicator (red line)
  - Selection rectangle
  - Vehicle color coding by state

**Acceptance:**
- Window opens, road network visible
- Vehicles render as oriented boxes
- Camera pan/zoom works

---

### Phase 2: Interaction (2-3 hours)

**Tasks:**
- [ ] **CK-10-04**: Selection system
  - Left-click to select vehicle
  - Selection highlight rendering
  - Click detection (point-in-box test)

- [ ] **CK-10-05**: Path editing mode
  - Right-click activates editing
  - Left-click adds waypoints
  - Right-click finishes and issues command
  - Real-time path preview rendering

- [ ] **CK-10-06**: Click-to-move
  - Left-click empty space with selection
  - Issues NavigateToPoint command
  - Visual feedback (target marker)

**Acceptance:**
- Can select vehicles
- Can draw custom paths
- Can command vehicles to move

---

### Phase 3: UI Controls (3-4 hours)

**Tasks:**
- [ ] **CK-10-07**: Spawn controls panel
  - Slider for spawn count (1-1000)
  - Checkbox for random movement
  - Spawn button
  - Clear all button
  - Vehicle count display

- [ ] **CK-10-08**: Formation controls panel
  - Formation type dropdown (Column, Wedge, Line)
  - Formation size slider (2-16)
  - Formation count slider (1-10)
  - Create formations button

- [ ] **CK-10-09**: Simulation controls panel
  - Play/Pause button
  - Step forward/backward buttons
  - Simulation speed slider (0.1x - 5x)
  - Recording controls (start, stop, save, load)
  - Frame timeline slider

- [ ] **CK-10-10**: Inspector panel
  - VehicleState display (position, speed, heading)
  - NavState display (mode, progress, destination)
  - FormationMember display (if applicable)
  - Real-time updates

- [ ] **CK-10-11**: Performance panel
  - FPS counter
  - Frame time graph (120 samples)
  - Vehicle count
  - System time breakdown (spatial hash, kinematics, rendering)

**Acceptance:**
- All UI panels functional
- Can spawn vehicles in batches
- Can create formations
- Can pause/step/rewind simulation
- Inspector shows correct data

---

### Phase 4: Advanced Rendering (2-3 hours)

**Tasks:**
- [ ] **CK-10-12**: Status labels
  - Label positioning above vehicles
  - Formation info display
  - Trajectory info display
  - Road segment info display
  - Text rendering with background

- [ ] **CK-10-13**: Trajectory visualization
  - Waypoint rendering (circles)
  - Path line rendering
  - Loop indicator
  - Current progress marker
  - Only for selected vehicle

- [ ] **CK-10-14**: Formation visualization
  - Leader-member connections (lines)
  - Slot positions (ghost boxes)
  - Formation type indicator
  - Member state color coding

**Acceptance:**
- Labels visible and readable
- Selected vehicle's trajectory shown
- Formation structure visible

---

### Phase 5: Demo Scenarios & Documentation (2-3 hours)

**Tasks:**
- [ ] **CK-10-15**: Sample road network
  - Create comprehensive sample_road.json
  - Multiple intersections
  - Various road types (straight, curved)
  - Cover 200x200m world

- [ ] **CK-10-16**: Demo scenarios
  - Scenario 1: 100 random vehicles
  - Scenario 2: Traffic on road network
  - Scenario 3: Multiple formations
  - Scenario 4: Custom path following
  - Scenario 5: Stress test (1000+ vehicles)

- [ ] **CK-10-17**: Performance validation
  - Measure frame time at 100, 500, 1000, 2000 vehicles
  - Profile bottlenecks
  - Optimize if needed
  - Document performance characteristics

- [ ] **CK-10-18**: Documentation
  - README.md with controls
  - Demo walkthrough
  - Performance guidelines
  - Known limitations

**Acceptance:**
- All scenarios run smoothly
- 1000+ vehicles @ 60 FPS on modern hardware
- Documentation complete

---

## Code Templates

### Program.cs Template

```csharp
using Raylib_cs;
using ImGuiNET;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Examples.CarKinem.Rendering;
using Fdp.Examples.CarKinem.Input;
using Fdp.Examples.CarKinem.UI;

namespace Fdp.Examples.CarKinem
{
    class Program
    {
        static void Main(string[] args)
        {
            // Initialize Raylib
            Raylib.InitWindow(1280, 720, "Car Kinematics Demo");
            Raylib.SetTargetFPS(60);
            
            // Initialize ImGui
            ImGuiController.Initialize();
            
            // Create simulation
            var simulation = new DemoSimulation();
            
            // Create managers
            var camera = new Camera2D { Position = new Vector2(100, 100), Zoom = 1.0f };
            var selection = new SelectionManager();
            var pathEditor = new PathEditingMode();
            var inputManager = new InputManager();
            
            // Create renderers
            var roadRenderer = new RoadRenderer();
            var vehicleRenderer = new VehicleRenderer();
            var trajRenderer = new TrajectoryRenderer(simulation.TrajectoryPool);
            var labelRenderer = new DebugLabelRenderer();
            
            // Create UI
            var mainUI = new MainUI();
            
            // Main loop
            while (!Raylib.WindowShouldClose())
            {
                float dt = Raylib.GetFrameTime();
                
                // Input
                inputManager.HandleInput(selection, pathEditor, camera, simulation);
                
                // Simulation
                simulation.Tick(dt);
                
                // Render
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.DARKGRAY);
                
                // World rendering
                roadRenderer.RenderRoadNetwork(simulation.RoadNetwork, camera);
                vehicleRenderer.RenderVehicles(simulation.View, camera, selection.SelectedEntityId);
                
                if (selection.SelectedEntityId.HasValue)
                {
                    var nav = simulation.GetNavState(selection.SelectedEntityId.Value);
                    if (nav.Mode == NavigationMode.CustomTrajectory)
                    {
                        trajRenderer.RenderTrajectory(nav.TrajectoryId, camera, Color.GREEN);
                    }
                }
                
                labelRenderer.RenderVehicleLabels(simulation.View, camera);
                pathEditor.Render(camera);
                
                // UI
                ImGuiController.NewFrame();
                mainUI.Render(simulation, selection);
                ImGuiController.Render();
                
                Raylib.EndDrawing();
            }
            
            // Cleanup
            simulation.Dispose();
            ImGuiController.Shutdown();
            Raylib.CloseWindow();
        }
    }
}
```

### DemoSimulation.cs Template

```csharp
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
using ModuleHost.Core;

namespace Fdp.Examples.CarKinem.Simulation
{
    public class DemoSimulation : IDisposable
    {
        private EntityRepository _repository;
        private FlightRecorder _recorder;
        private ModuleHostKernel _kernel;
        
        private SpatialHashSystem _spatialSystem;
        private FormationTargetSystem _formationTargetSystem;
        private RoadGraphNavigator _roadNavigator;
        private VehicleCommandSystem _commandSystem;
        private CarKinematicsSystem _kinematicsSystem;
        
        public RoadNetworkBlob RoadNetwork { get; private set; }
        public TrajectoryPoolManager TrajectoryPool { get; private set; }
        public FormationTemplateManager FormationTemplates { get; private set; }
        public ISimulationView View => _repository.GetView();
        
        public DemoSimulation()
        {
            _repository = new EntityRepository();
            _recorder = new FlightRecorder(_repository);
            
            RegisterComponents();
            
            // Load road network
            RoadNetwork = RoadNetworkLoader.LoadFromJson("Assets/sample_road.json");
            
            // Create managers
            TrajectoryPool = new TrajectoryPoolManager();
            FormationTemplates = new FormationTemplateManager();
            
            // Create systems
            _spatialSystem = new SpatialHashSystem();
            _formationTargetSystem = new FormationTargetSystem(FormationTemplates);
            _commandSystem = new VehicleCommandSystem();
            _kinematicsSystem = new CarKinematicsSystem(RoadNetwork, TrajectoryPool);
            
            // Initialize ModuleHost kernel
            _kernel = new ModuleHostKernel(_repository, _recorder);
            
            // Register systems
            _repository.GetSystemRegistry().RegisterSystem(_spatialSystem);
            _repository.GetSystemRegistry().RegisterSystem(_formationTargetSystem);
            _ repository.GetSystemRegistry().RegisterSystem(_commandSystem);
            _repository.GetSystemRegistry().RegisterSystem(_kinematicsSystem);
            
            // Set World for all systems
            _spatialSystem.World = _repository;
            _formationTargetSystem.World = _repository;
            _commandSystem.World = _repository;
            _kinematicsSystem.World = _repository;
            
            // OnCreate for all systems
            _spatialSystem.OnCreate();
            _formationTargetSystem.OnCreate();
            _commandSystem.OnCreate();
            _kinematicsSystem.OnCreate();
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
            _kernel.Tick(deltaTime);
        }
        
        public int SpawnVehicle(Vector2 position, Vector2 heading)
        {
            // ... implementation from design doc
        }
        
        public void IssueMoveToPointCommand(int entityId, Vector2 destination)
        {
            var api = new VehicleAPI(View);
            api.NavigateToPoint(entityId, destination);
        }
        
        public void Dispose()
        {
            _spatialSystem.OnDestroy();
            _kinematicsSystem.OnDestroy();
            RoadNetwork.Dispose();
            TrajectoryPool.Dispose();
            FormationTemplates.Dispose();
        }
    }
}
```

---

## ✅ Acceptance Criteria

### Functionality
- [ ] Window opens, demo runs at 60 FPS
- [ ] Road network renders correctly
- [ ] Vehicles render as oriented boxes with front indicators
- [ ] Can select vehicles (left-click)
- [ ] Can command vehicles to move (left-click empty space)
- [ ] Can draw custom paths (right-click mode)
- [ ] Can spawn batches of vehicles (slider + button)
- [ ] Can create formations (type/size/count)
- [ ] Simulation controls work (play/pause/step/rewind)
- [ ] Inspector shows correct vehicle data
- [ ] Status labels visible above vehicles
- [ ] Performance panel shows FPS and frame time

### Performance
- [ ] 1000 vehicles @ 60 FPS (modern hardware)
- [ ] 500 vehicles @ 60 FPS (minimum target)
- [ ] Frame time graph shows no major spikes
- [ ] Smooth camera pan/zoom at all zoom levels

### Code Quality
- [ ] Zero warnings on build
- [ ] Clean separation of concerns
- [ ] Documentation complete
- [ ] Sample scenarios included

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-10-REPORT.md`

Include:
- Screenshots of demo running
- Performance metrics (FPS at different vehicle counts)
- Video recording of demo (optional)
- Known issues/limitations

**Total Time Estimate:** 12-17 hours

---

**This is the capstone batch that demonstrates all features!**
