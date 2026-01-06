# Car Kinematics Demo Application - Design Document

**Project:** Fdp.Examples.CarKinem  
**Purpose:** Interactive visual demonstration and debugging tool for Car Kinematics module  
**Architecture:** Raylib (rendering) + ImGui (UI) + FDP/ModuleHost (simulation)  
**Target:** Real-time visualization of 1,000+ vehicles with full interaction  
**Date:** 2026-01-07  

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Rendering System](#rendering-system)
4. [Interaction System](#interaction-system)
5. [UI System (ImGui)](#ui-system-imgui)
6. [Simulation Integration](#simulation-integration)
7. [Data Structures](#data-structures)
8. [Implementation Plan](#implementation-plan)

---

## Overview

### Goals

The demo application serves multiple purposes:
1. **Visual Validation** - Verify car kinematics behavior is correct
2. **Interactive Testing** - Manually test navigation modes, formations, etc.
3. **Performance Profiling** - Measure frame times, bottlenecks
4. **Demonstration** - Showcase capabilities to stakeholders
5. **Debugging Tool** - Inspect individual vehicle state, replay recordings

### Core Features

**Rendering:**
- Road network visualization (Hermite curves)
- Vehicle rendering (sized boxes with front indicator)
- Custom trajectory visualization
- Formation structure visualization
- Status labels (per-vehicle debug info)

**Interaction:**
- Vehicle selection (left-click)
- Click-to-move command (left-click empty space)
- Custom path editing (right-click mode)
- Camera pan/zoom
- Frame-by-frame stepping

**UI Controls:**
- Batch vehicle spawning (slider-controlled)
- Formation creation (type, size, count)
- Simulation controls (play, pause, step, rewind, speed)
- Performance metrics display
- Selected vehicle inspector

---

## Architecture

### Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                   Demo Application Loop                      │
├─────────────────────────────────────────────────────────────┤
│  1. Input Handling (Raylib)                                  │
│     ├─ Mouse input (click, drag, wheel)                     │
│     └─ Keyboard input (hotkeys)                             │
├─────────────────────────────────────────────────────────────┤
│  2. ImGui UI Update                                          │
│     ├─ Spawn controls                                        │
│     ├─ Formation controls                                    │
│     ├─ Simulation controls (Flight Recorder)                │
│     └─ Inspector panel                                       │
├─────────────────────────────────────────────────────────────┤
│  3. Simulation Tick (FDP/ModuleHost)                         │
│     ├─ ModuleHostKernel.Tick()                              │
│     ├─ CarKinematicsModule.Tick()                           │
│     └─ FlightRecorder.RecordFrame()                         │
├─────────────────────────────────────────────────────────────┤
│  4. Rendering (Raylib)                                       │
│     ├─ Camera transform setup                                │
│     ├─ Road network rendering                                │
│     ├─ Vehicle rendering                                     │
│     ├─ Trajectory/formation visualization                    │
│     └─ Status labels                                         │
├─────────────────────────────────────────────────────────────┤
│  5. ImGui Rendering                                          │
└─────────────────────────────────────────────────────────────┘
```

### Technology Stack

- **Rendering:** Raylib (C# bindings: `Raylib-cs`)
- **UI:** Dear ImGui (C# bindings: `ImGui.NET`)
- **Simulation:** FDP/ModuleHost + CarKinem
- **Math:** System.Numerics

### Project Structure

```
Fdp.Examples.CarKinem/
├── Fdp.Examples.CarKinem.csproj
├── Program.cs                          (Entry point, main loop)
├── Rendering/
│   ├── RoadRenderer.cs                 (Hermite curve rendering)
│   ├── VehicleRenderer.cs              (Box + front indicator)
│   ├── TrajectoryRenderer.cs           (Path visualization)
│   ├── FormationRenderer.cs            (Formation structure)
│   └── DebugLabelRenderer.cs           (Per-vehicle status)
├── Input/
│   ├── InputManager.cs                 (Mouse/keyboard handling)
│   ├── SelectionManager.cs             (Vehicle selection)
│   └── PathEditingMode.cs              (Custom path editor)
├── UI/
│   ├── MainUI.cs                       (ImGui layout orchestration)
│   ├── SpawnControlsPanel.cs           (Batch spawning UI)
│   ├── FormationControlsPanel.cs       (Formation creation UI)
│   ├── SimulationControlsPanel.cs      (Play/pause/step/rewind)
│   ├── InspectorPanel.cs               (Selected vehicle details)
│   └── PerformancePanel.cs             (FPS, frame time, etc.)
├── Simulation/
│   ├── DemoSimulation.cs               (FDP setup, module registration)
│   ├── DemoModule.cs                   (Custom module for demo logic)
│   └── CommandIssuer.cs                (Issue commands to vehicles)
├── Assets/
│   └── sample_road.json                (Default road network)
└── README.md
```

---

## Rendering System

### Camera System

**Camera State:**
```csharp
public class Camera2D
{
    public Vector2 Position;    // World position camera is looking at
    public float Zoom;          // Zoom level (1.0 = 1 pixel = 1 meter)
    public float Rotation;      // Camera rotation (unused in 2D top-down)
}
```

**Controls:**
- **Pan:** Middle mouse drag or WASD keys
- **Zoom:** Mouse wheel (centered on cursor position)
- **Reset:** 'R' key resets to default view

**Transform:**
```csharp
Matrix3x2 GetCameraMatrix()
{
    // 1. Scale (zoom)
    // 2. Translate to center of screen
    // 3. Translate by -camera position
    return Matrix3x2.CreateScale(Zoom) 
         * Matrix3x2.CreateTranslation(ScreenWidth/2, ScreenHeight/2)
         * Matrix3x2.CreateTranslation(-Position);
}

Vector2 ScreenToWorld(Vector2 screenPos)
{
    Matrix3x2.Invert(GetCameraMatrix(), out var inverse);
    return Vector2.Transform(screenPos, inverse);
}
```

### Road Network Rendering

**RoadRenderer.cs:**

```csharp
public class RoadRenderer
{
    public void RenderRoadNetwork(RoadNetworkBlob network, Camera2D camera)
    {
        // Render all segments
        for (int i = 0; i < network.Segments.Length; i++)
        {
            ref readonly var segment = ref network.Segments[i];
            RenderHermiteSegment(segment, camera);
            RenderLaneMarkers(segment, camera);
        }
        
        // Render nodes (junctions)
        for (int i = 0; i < network.Nodes.Length; i++)
        {
            ref readonly var node = ref network.Nodes[i];
            RenderNode(node, camera);
        }
    }
    
    private void RenderHermiteSegment(RoadSegment segment, Camera2D camera)
    {
        const int CURVE_SAMPLES = 32;
        Vector2 prev = segment.P0;
        
        for (int i = 1; i <= CURVE_SAMPLES; i++)
        {
            float t = i / (float)CURVE_SAMPLES;
            Vector2 point = RoadGraphNavigator.EvaluateHermite(t, 
                segment.P0, segment.T0, segment.P1, segment.T1);
            
            Vector2 screenPrev = WorldToScreen(prev, camera);
            Vector2 screenPoint = WorldToScreen(point, camera);
            
            Raylib.DrawLineV(screenPrev, screenPoint, Color.DARKGRAY);
            prev = point;
        }
    }
    
    private void RenderLaneMarkers(RoadSegment segment, Camera2D camera)
    {
        // Draw dashed center line if multi-lane
        if (segment.LaneCount > 1)
        {
            // Sample curve and draw perpendicular lane markers
            // ... (similar to segment rendering but with perpendicular offset)
        }
    }
    
    private void RenderNode(RoadNode node, Camera2D camera)
    {
        Vector2 screenPos = WorldToScreen(node.Position, camera);
        float radius = 3f * camera.Zoom;
        Raylib.DrawCircleV(screenPos, radius, Color.GRAY);
    }
}
```

### Vehicle Rendering

**VehicleRenderer.cs:**

```csharp
public class VehicleRenderer
{
    public void RenderVehicles(ISimulationView sim, Camera2D camera, int? selectedEntityId)
    {
        var query = sim.Query<VehicleState, VehicleParams>();
        
        foreach (var entity in query)
        {
            var state = sim.GetComponent<VehicleState>(entity);
            var @params = sim.GetComponent<VehicleParams>(entity);
            
            bool isSelected = entity.Id == selectedEntityId;
            RenderVehicle(entity, state, @params, camera, isSelected);
        }
    }
    
    private void RenderVehicle(Entity entity, VehicleState state, VehicleParams @params,
        Camera2D camera, bool isSelected)
    {
        // Calculate vehicle corners (oriented box)
        float halfLength = @params.Length / 2;
        float halfWidth = @params.Width / 2;
        
        Vector2 right = VectorMath.Right(state.Forward);
        
        Vector2[] corners = new Vector2[4]
        {
            state.Position + state.Forward * halfLength + right * halfWidth,   // Front-right
            state.Position + state.Forward * halfLength - right * halfWidth,   // Front-left
            state.Position - state.Forward * halfLength - right * halfWidth,   // Rear-left
            state.Position - state.Forward * halfLength + right * halfWidth    // Rear-right
        };
        
        // Transform to screen space
        Vector2[] screenCorners = new Vector2[4];
        for (int i = 0; i < 4; i++)
            screenCorners[i] = WorldToScreen(corners[i], camera);
        
        // Draw vehicle box
        Color vehicleColor = isSelected ? Color.YELLOW : Color.BLUE;
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            Raylib.DrawLineV(screenCorners[i], screenCorners[next], vehicleColor);
        }
        
        // Draw front indicator (thicker line at front)
        Raylib.DrawLineEx(screenCorners[0], screenCorners[1], 3f, Color.RED);
        
        // Draw selection rectangle if selected
        if (isSelected)
        {
            float margin = 0.5f;
            Vector2[] selectionCorners = new Vector2[4]
            {
                state.Position + state.Forward * (halfLength + margin) + right * (halfWidth + margin),
                state.Position + state.Forward * (halfLength + margin) - right * (halfWidth + margin),
                state.Position - state.Forward * (halfLength + margin) - right * (halfWidth + margin),
                state.Position - state.Forward * (halfLength + margin) + right * (halfWidth + margin)
            };
            
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                Vector2 p1 = WorldToScreen(selectionCorners[i], camera);
                Vector2 p2 = WorldToScreen(selectionCorners[next], camera);
                Raylib.DrawLineEx(p1, p2, 2f, Color.YELLOW);
            }
        }
    }
}
```

### Status Label Rendering

**DebugLabelRenderer.cs:**

```csharp
public class DebugLabelRenderer
{
    public void RenderVehicleLabels(ISimulationView sim, Camera2D camera)
    {
        var query = sim.Query<VehicleState, NavState>();
        
        foreach (var entity in query)
        {
            var state = sim.GetComponent<VehicleState>(entity);
            var nav = sim.GetComponent<NavState>(entity);
            
            string label = BuildLabelText(entity, state, nav, sim);
            
            // Position label above vehicle
            Vector2 labelWorldPos = state.Position + new Vector2(0, 3); // 3m above
            Vector2 labelScreenPos = WorldToScreen(labelWorldPos, camera);
            
            Raylib.DrawText(label, (int)labelScreenPos.X, (int)labelScreenPos.Y, 
                12, Color.WHITE);
        }
    }
    
    private string BuildLabelText(Entity entity, VehicleState state, NavState nav, 
        ISimulationView sim)
    {
        switch (nav.Mode)
        {
            case NavigationMode.Formation:
                var member = sim.GetComponent<FormationMember>(entity);
                return $"F:{member.FormationId} Slot:{member.SlotIndex}";
                
            case NavigationMode.CustomTrajectory:
                return $"Traj:{nav.TrajectoryId} @{nav.ProgressS:F1}m";
                
            case NavigationMode.RoadGraph:
                return $"Road:{nav.CurrentSegmentId} {nav.RoadPhase}";
                
            default:
                return $"Idle {state.Speed:F1}m/s";
        }
    }
}
```

### Trajectory Rendering

**TrajectoryRenderer.cs:**

```csharp
public class TrajectoryRenderer
{
    private TrajectoryPoolManager _trajectoryPool;
    
    public void RenderTrajectory(int trajectoryId, Camera2D camera, Color color)
    {
        if (!_trajectoryPool.TryGetTrajectory(trajectoryId, out var traj))
            return;
        
        // Draw waypoints
        for (int i = 0; i < traj.Waypoints.Length; i++)
        {
            Vector2 screenPos = WorldToScreen(traj.Waypoints[i].Position, camera);
            Raylib.DrawCircleV(screenPos, 5f, color);
            
            // Draw line to next waypoint
            if (i < traj.Waypoints.Length - 1)
            {
                Vector2 screenNext = WorldToScreen(traj.Waypoints[i + 1].Position, camera);
                Raylib.DrawLineV(screenPos, screenNext, color);
            }
            else if (traj.IsLooped == 1)
            {
                // Connect last to first
                Vector2 screenFirst = WorldToScreen(traj.Waypoints[0].Position, camera);
                Raylib.DrawLineV(screenPos, screenFirst, color);
            }
        }
    }
}
```

---

## Interaction System

### Selection System

**SelectionManager.cs:**

```csharp
public class SelectionManager
{
    private int? _selectedEntityId;
    
    public int? SelectedEntityId => _selectedEntityId;
    
    public void HandleMouseClick(Vector2 screenPos, Camera2D camera, ISimulationView sim)
    {
        Vector2 worldPos = ScreenToWorld(screenPos, camera);
        
        // Find closest vehicle to click position
        int? closestEntity = null;
        float minDist = float.MaxValue;
        
        var query = sim.Query<VehicleState, VehicleParams>();
        foreach (var entity in query)
        {
            var state = sim.GetComponent<VehicleState>(entity);
            var @params = sim.GetComponent<VehicleParams>(entity);
            
            // Check if click is inside vehicle bounds
            float dist = Vector2.Distance(worldPos, state.Position);
            float maxRadius = MathF.Max(@params.Length, @params.Width) / 2;
            
            if (dist < maxRadius && dist < minDist)
            {
                minDist = dist;
                closestEntity = entity.Id;
            }
        }
        
        _selectedEntityId = closestEntity;
    }
    
    public void ClearSelection()
    {
        _selectedEntityId = null;
    }
}
```

### Path Editing Mode

**PathEditingMode.cs:**

```csharp
public class PathEditingMode
{
    private List<Vector2> _editingPath = new();
    private bool _isActive = false;
    
    public bool IsActive => _isActive;
    public IReadOnlyList<Vector2> CurrentPath => _editingPath;
    
    public void StartEditing()
    {
        _isActive = true;
        _editingPath.Clear();
    }
    
    public void AddWaypoint(Vector2 worldPos)
    {
        if (!_isActive)
            return;
        
        _editingPath.Add(worldPos);
    }
    
    public void FinishEditing(int? selectedEntityId, DemoSimulation sim)
    {
        if (!_isActive || _editingPath.Count < 2)
        {
            _isActive = false;
            _editingPath.Clear();
            return;
        }
        
        if (selectedEntityId.HasValue)
        {
            // Register trajectory and issue command
            int trajId = sim.TrajectoryPool.RegisterTrajectory(_editingPath.ToArray());
            sim.IssueFollowTrajectoryCommand(selectedEntityId.Value, trajId);
        }
        
        _isActive = false;
        _editingPath.Clear();
    }
    
    public void CancelEditing()
    {
        _isActive = false;
        _editingPath.Clear();
    }
    
    public void Render(Camera2D camera)
    {
        if (!_isActive || _editingPath.Count == 0)
            return;
        
        // Draw waypoints and connecting lines
        for (int i = 0; i < _editingPath.Count; i++)
        {
            Vector2 screenPos = WorldToScreen(_editingPath[i], camera);
            Raylib.DrawCircleV(screenPos, 6f, Color.GREEN);
            
            if (i > 0)
            {
                Vector2 screenPrev = WorldToScreen(_editingPath[i - 1], camera);
                Raylib.DrawLineDashed(screenPrev, screenPos, Color.GREEN);
            }
        }
    }
}
```

### Input Manager

**InputManager.cs:**

```csharp
public class InputManager
{
    public void HandleInput(SelectionManager selection, PathEditingMode pathEditor,
        Camera2D camera, DemoSimulation sim)
    {
        // Right mouse button - toggle path editing mode
        if (Raylib.IsMouseButtonPressed(MouseButton.MOUSE_BUTTON_RIGHT))
        {
            if (pathEditor.IsActive)
                pathEditor.FinishEditing(selection.SelectedEntityId, sim);
            else if (selection.SelectedEntityId.HasValue)
                pathEditor.StartEditing();
        }
        
        // Left mouse button - selection or path waypoint or move command
        if (Raylib.IsMouseButtonPressed(MouseButton.MOUSE_BUTTON_LEFT))
        {
            Vector2 mousePos = Raylib.GetMousePosition();
            Vector2 worldPos = ScreenToWorld(mousePos, camera);
            
            if (pathEditor.IsActive)
            {
                // Add waypoint to path being edited
                pathEditor.AddWaypoint(worldPos);
            }
            else
            {
                // Try to select vehicle
                int? prevSelection = selection.SelectedEntityId;
                selection.HandleMouseClick(mousePos, camera, sim.View);
                
                // If clicked empty space with vehicle selected, issue move command
                if (selection.SelectedEntityId == null && prevSelection.HasValue)
                {
                    sim.IssueMoveToPointCommand(prevSelection.Value, worldPos);
                    selection.SelectedEntityId = prevSelection; // Re-select
                }
            }
        }
        
        // ESC - cancel path editing or clear selection
        if (Raylib.IsKeyPressed(KeyboardKey.KEY_ESCAPE))
        {
            if (pathEditor.IsActive)
                pathEditor.CancelEditing();
            else
                selection.ClearSelection();
        }
        
        // Camera controls
        HandleCameraInput(camera);
    }
    
    private void HandleCameraInput(Camera2D camera)
    {
        // Pan with middle mouse drag
        if (Raylib.IsMouseButtonDown(MouseButton.MOUSE_BUTTON_MIDDLE))
        {
            Vector2 delta = Raylib.GetMouseDelta();
            camera.Position -= delta / camera.Zoom;
        }
        
        // Pan with WASD
        float panSpeed = 10f / camera.Zoom;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_W)) camera.Position.Y -= panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_S)) camera.Position.Y += panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_A)) camera.Position.X -= panSpeed;
        if (Raylib.IsKeyDown(KeyboardKey.KEY_D)) camera.Position.X += panSpeed;
        
        // Zoom with mouse wheel
        float wheel = Raylib.GetMouseWheelMove();
        if (MathF.Abs(wheel) > 0.01f)
        {
            Vector2 mousePos = Raylib.GetMousePosition();
            Vector2 worldPosBefore = ScreenToWorld(mousePos, camera);
            
            camera.Zoom *= 1.0f + wheel * 0.1f;
            camera.Zoom = Math.Clamp(camera.Zoom, 0.1f, 10f);
            
            Vector2 worldPosAfter = ScreenToWorld(mousePos, camera);
            camera.Position += worldPosBefore - worldPosAfter;
        }
        
        // Reset camera with 'R'
        if (Raylib.IsKeyPressed(KeyboardKey.KEY_R))
        {
            camera.Position = new Vector2(50, 50);
            camera.Zoom = 1.0f;
        }
    }
}
```

---

## UI System (ImGui)

### Main UI Layout

**MainUI.cs:**

```csharp
public class MainUI
{
    private SpawnControlsPanel _spawnControls = new();
    private FormationControlsPanel _formationControls = new();
    private SimulationControlsPanel _simControls = new();
    private InspectorPanel _inspector = new();
    private PerformancePanel _performance = new();
    
    public void Render(DemoSimulation sim, SelectionManager selection)
    {
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(350, 600), ImGuiCond.FirstUseEver);
        
        if (ImGui.Begin("Car Kinematics Demo"))
        {
            if (ImGui.CollapsingHeader("Spawn Controls", ImGuiTreeNodeFlags.DefaultOpen))
            {
                _spawnControls.Render(sim);
            }
            
            if (ImGui.CollapsingHeader("Formation Controls", ImGuiTreeNodeFlags.DefaultOpen))
            {
                _formationControls.Render(sim);
            }
            
            ImGui.Separator();
            
            if (ImGui.CollapsingHeader("Simulation Controls", ImGuiTreeNodeFlags.DefaultOpen))
            {
                _simControls.Render(sim);
            }
            
            ImGui.Separator();
            
            if (ImGui.CollapsingHeader("Performance", ImGuiTreeNodeFlags.DefaultOpen))
            {
                _performance.Render(sim);
            }
            
            ImGui.End();
        }
        
        // Inspector window (separate)
        if (selection.SelectedEntityId.HasValue)
        {
            _inspector.Render(sim, selection.SelectedEntityId.Value);
        }
    }
}
```

### Spawn Controls Panel

**SpawnControlsPanel.cs:**

```csharp
public class SpawnControlsPanel
{
    private int _spawnCount = 10;
    private bool _randomMovement = true;
    
    public void Render(DemoSimulation sim)
    {
        ImGui.SliderInt("Spawn Count", ref _spawnCount, 1, 1000);
        ImGui.Checkbox("Random Movement", ref _randomMovement);
        
        if (ImGui.Button("Spawn Vehicles"))
        {
            SpawnVehicles(sim, _spawnCount, _randomMovement);
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Clear All"))
        {
            sim.ClearAllVehicles();
        }
        
        ImGui.Text($"Current Vehicles: {sim.GetVehicleCount()}");
    }
    
    private void SpawnVehicles(DemoSimulation sim, int count, bool randomMovement)
    {
        Random rng = new Random();
        
        for (int i = 0; i < count; i++)
        {
            Vector2 pos = new Vector2(
                rng.Next(0, 200),
                rng.Next(0, 200)
            );
            
            Vector2 heading = new Vector2(
                (float)rng.NextDouble() * 2 - 1,
                (float)rng.NextDouble() * 2 - 1
            );
            heading = Vector2.Normalize(heading);
            
            int entityId = sim.SpawnVehicle(pos, heading);
            
            if (randomMovement)
            {
                Vector2 destination = new Vector2(
                    rng.Next(0, 200),
                    rng.Next(0, 200)
                );
                sim.IssueMoveToPointCommand(entityId, destination);
            }
        }
    }
}
```

### Formation Controls Panel

**FormationControlsPanel.cs:**

```csharp
public class FormationControlsPanel
{
    private FormationType _formationType = FormationType.Column;
    private int _formationSize = 5;
    private int _formationCount = 1;
    
    public void Render(DemoSimulation sim)
    {
        // Formation type dropdown
        string[] typeNames = Enum.GetNames<FormationType>();
        int selectedType = (int)_formationType;
        if (ImGui.Combo("Formation Type", ref selectedType, typeNames, typeNames.Length))
        {
            _formationType = (FormationType)selectedType;
        }
        
        ImGui.SliderInt("Formation Size", ref _formationSize, 2, 16);
        ImGui.SliderInt("Formation Count", ref _formationCount, 1, 10);
        
        if (ImGui.Button("Create Formations"))
        {
            CreateFormations(sim, _formationType, _formationSize, _formationCount);
        }
    }
    
    private void CreateFormations(DemoSimulation sim, FormationType type, 
        int size, int count)
    {
        Random rng = new Random();
        
        for (int f = 0; f < count; f++)
        {
            // Spawn formation at random location
            Vector2 leaderPos = new Vector2(
                rng.Next(20, 180),
                rng.Next(20, 180)
            );
            
            int formationId = sim.CreateFormation(type, size, leaderPos);
        }
    }
}
```

### Simulation Controls Panel

**SimulationControlsPanel.cs:**

```csharp
public class SimulationControlsPanel
{
    private bool _isPaused = false;
    private float _simSpeed = 1.0f;
    
    public void Render(DemoSimulation sim)
    {
        // Play/Pause
        if (ImGui.Button(_isPaused ? "Play" : "Pause"))
        {
            _isPaused = !_isPaused;
            sim.SetPaused(_isPaused);
        }
        
        ImGui.SameLine();
        
        // Step forward
        if (ImGui.Button("Step Forward"))
        {
            sim.StepFrame(1);
        }
        
        ImGui.SameLine();
        
        // Step backward
        if (ImGui.Button("Step Backward"))
        {
            sim.StepFrame(-1);
        }
        
        // Simulation speed
        ImGui.SliderFloat("Sim Speed", ref _simSpeed, 0.1f, 5.0f);
        sim.SetTimeScale(_simSpeed);
        
        ImGui.Separator();
        
        // Recording controls
        ImGui.Text("Recording:");
        
        if (ImGui.Button("Start Recording"))
        {
            sim.StartRecording();
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Stop Recording"))
        {
            sim.StopRecording();
        }
        
        if (ImGui.Button("Save Recording"))
        {
            sim.SaveRecording("recording.fdr");
        }
        
        ImGui.SameLine();
        
        if (ImGui.Button("Load Recording"))
        {
            sim.LoadRecording("recording.fdr");
        }
        
        // Playback timeline
        int currentFrame = sim.GetCurrentFrame();
        int totalFrames = sim.GetTotalRecordedFrames();
        
        if (totalFrames > 0)
        {
            if (ImGui.SliderInt("Frame", ref currentFrame, 0, totalFrames - 1))
            {
                sim.SeekToFrame(currentFrame);
            }
        }
    }
}
```

### Inspector Panel

**InspectorPanel.cs:**

```csharp
public class InspectorPanel
{
    public void Render(DemoSimulation sim, int entityId)
    {
        ImGui.SetNextWindowPos(new Vector2(Raylib.GetScreenWidth() - 360, 10), 
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(350, 500), ImGuiCond.FirstUseEver);
        
        if (ImGui.Begin($"Inspector - Entity {entityId}"))
        {
            var entity = new Entity(entityId, 0); // Generation unknown
            
            // Vehicle State
            if (sim.View.HasComponent<VehicleState>(entity))
            {
                var state = sim.View.GetComponent<VehicleState>(entity);
                
                if (ImGui.TreeNodeEx("VehicleState", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Position: ({state.Position.X:F2}, {state.Position.Y:F2})");
                    ImGui.Text($"Forward: ({state.Forward.X:F2}, {state.Forward.Y:F2})");
                    ImGui.Text($"Speed: {state.Speed:F2} m/s");
                    ImGui.Text($"Steer: {state.SteerAngle:F2} rad");
                    ImGui.Text($"Accel: {state.Accel:F2} m/s²");
                    ImGui.TreePop();
                }
            }
            
            // Nav State
            if (sim.View.HasComponent<NavState>(entity))
            {
                var nav = sim.View.GetComponent<NavState>(entity);
                
                if (ImGui.TreeNodeEx("NavState", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Mode: {nav.Mode}");
                    ImGui.Text($"Progress: {nav.ProgressS:F2} m");
                    ImGui.Text($"Target Speed: {nav.TargetSpeed:F2} m/s");
                    
                    if (nav.Mode == NavigationMode.RoadGraph)
                    {
                        ImGui.Text($"Road Phase: {nav.RoadPhase}");
                        ImGui.Text($"Segment ID: {nav.CurrentSegmentId}");
                    }
                    else if (nav.Mode == NavigationMode.CustomTrajectory)
                    {
                        ImGui.Text($"Trajectory ID: {nav.TrajectoryId}");
                    }
                    
                    ImGui.Text($"Destination: ({nav.FinalDestination.X:F2}, " +
                        ${nav.FinalDestination.Y:F2})");
                    ImGui.Text($"Arrived: {nav.HasArrived == 1}");
                    
                    ImGui.TreePop();
                }
            }
            
            // Formation Member
            if (sim.View.HasComponent<FormationMember>(entity))
            {
                var member = sim.View.GetComponent<FormationMember>(entity);
                
                if (ImGui.TreeNodeEx("FormationMember", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Text($"Formation ID: {member.FormationId}");
                    ImGui.Text($"Slot Index: {member.SlotIndex}");
                    ImGui.Text($"State: {member.State}");
                    ImGui.TreePop();
                }
            }
            
            ImGui.End();
        }
    }
}
```

### Performance Panel

**PerformancePanel.cs:**

```csharp
public class PerformancePanel
{
    private Queue<float> _frameTimes = new(120);
    
    public void Render(DemoSimulation sim)
    {
        ImGui.Text($"FPS: {Raylib.GetFPS()}");
        ImGui.Text($"Frame Time: {Raylib.GetFrameTime() * 1000:F2} ms");
        ImGui.Text($"Vehicle Count: {sim.GetVehicleCount()}");
        
        // Frame time graph
        float currentFrameTime = Raylib.GetFrameTime() * 1000;
        _frameTimes.Enqueue(currentFrameTime);
        if (_frameTimes.Count > 120)
            _frameTimes.Dequeue();
        
        float[] frameTimeArray = _frameTimes.ToArray();
        ImGui.PlotLines("Frame Time (ms)", ref frameTimeArray[0], 
            frameTimeArray.Length, 0, null, 0f, 33.3f, new Vector2(300, 80));
    }
}
```

---

## Simulation Integration

### Demo Simulation Setup

**DemoSimulation.cs:**

```csharp
public class DemoSimulation
{
    private ModuleHostKernel _kernel;
    private EntityRepository _repository;
    private FlightRecorder _recorder;
    private CarKinematicsModule _carModule;
    
    public ISimulationView View => _repository.GetView();
    public TrajectoryPoolManager TrajectoryPool { get; private set; }
    public RoadNetworkBlob RoadNetwork { get; private set; }
    
    public DemoSimulation()
    {
        // Initialize FDP
        _repository = new EntityRepository();
        _recorder = new FlightRecorder(_repository);
        
        // Register all component types
        RegisterComponents();
        
        // Load road network
        RoadNetwork = RoadNetworkLoader.LoadFromJson("Assets/sample_road.json");
        
        // Create trajectory pool
        TrajectoryPool = new TrajectoryPoolManager();
        
        // Create car kinematics module
        _carModule = new CarKinematicsModule(RoadNetwork, TrajectoryPool);
        
        // Initialize ModuleHost kernel
        _kernel = new ModuleHostKernel(_repository, _recorder);
        _kernel.RegisterModule(_carModule, ModuleTier.Gameplay);
    }
    
    private void RegisterComponents()
    {
        _repository.RegisterComponent<VehicleState>();
        _repository.RegisterComponent<VehicleParams>();
        _repository.RegisterComponent<NavState>();
        _repository.RegisterComponent<FormationMember>();
        _repository.RegisterComponent<FormationRoster>();
        _repository.RegisterComponent<FormationTarget>();
        // ... register all component types
    }
    
    public void Tick(float deltaTime)
    {
        _kernel.Tick(deltaTime);
    }
    
    public int SpawnVehicle(Vector2 position, Vector2 heading)
    {
        var cmd = _repository.GetCommandBuffer();
        var entity = cmd.CreateEntity();
        
        cmd.AddComponent(entity, new VehicleState
        {
            Position = position,
            Forward = heading,
            Speed = 0f
        });
        
        cmd.AddComponent(entity, new VehicleParams
        {
            Length = 4.5f,
            Width = 2.0f,
            WheelBase = 2.7f,
            MaxSpeed = 30f,
            MaxAccel = 3.0f,
            MaxDecel = 6.0f,
            MaxSteerAngle = 0.6f
        });
        
        cmd.AddComponent(entity, new NavState
        {
            Mode = NavigationMode.None
        });
        
        return entity.Id;
    }
    
    public void IssueMoveToPointCommand(int entityId, Vector2 destination)
    {
        var cmd = _repository.GetCommandBuffer();
        cmd.PublishEvent(new CmdNavigateToPoint
        {
            EntityId = entityId,
            Destination = destination,
            ArrivalRadius = 2.0f,
            Speed = 10.0f
        });
    }
    
    public void IssueFollowTrajectoryCommand(int entityId, int trajectoryId)
    {
        var cmd = _repository.GetCommandBuffer();
        cmd.PublishEvent(new CmdFollowTrajectory
        {
            EntityId = entityId,
            TrajectoryId = trajectoryId,
            Looped = false
        });
    }
    
    public int CreateFormation(FormationType type, int size, Vector2 leaderPos)
    {
        // Create leader
        int leaderId = SpawnVehicle(leaderPos, new Vector2(1, 0));
        
        // Create formation roster
        var cmd = _repository.GetCommandBuffer();
        var formationEntity = cmd.CreateEntity();
        
        cmd.AddComponent(formationEntity, new FormationRoster
        {
            Type = type,
            Count = size,
            // ... populate members
        });
        
        // Spawn member vehicles and assign to formation
        // ... (implementation details)
        
        return formationEntity.Id;
    }
    
    public void SetPaused(bool paused) { /* ... */ }
    public void SetTimeScale(float scale) { /* ... */ }
    public void StepFrame(int count) { _recorder.StepFrames(count); }
    public void StartRecording() { _recorder.StartRecording(); }
    public void StopRecording() { _recorder.StopRecording(); }
    public void SaveRecording(string path) { _recorder.SaveToFile(path); }
    public void LoadRecording(string path) { _recorder.LoadFromFile(path); }
    public int GetCurrentFrame() => _recorder.CurrentFrame;
    public int GetTotalRecordedFrames() => _recorder.TotalFrames;
    public void SeekToFrame(int frame) { _recorder.SeekToFrame(frame); }
    public int GetVehicleCount() { /* query vehicle count */ }
    public void ClearAllVehicles() { /* destroy all vehicle entities */ }
}
```

---

## Data Structures

### Command Events

```csharp
[Event(EventId = 1001)]
public struct CmdNavigateToPoint
{
    public int EntityId;
    public Vector2 Destination;
    public float ArrivalRadius;
    public float Speed;
}

[Event(EventId = 1002)]
public struct CmdFollowTrajectory
{
    public int EntityId;
    public int TrajectoryId;
    public bool Looped;
}

[Event(EventId = 1003)]
public struct CmdNavigateViaRoad
{
    public int EntityId;
    public Vector2 Destination;
    public float ArrivalRadius;
}

[Event(EventId = 1004)]
public struct CmdJoinFormation
{
    public int EntityId;
    public int FormationId;
    public int SlotIndex;
}
```

---

## Implementation Plan

### Phase 1: Basic Rendering (2-3 hours)
1. Project setup (Raylib + ImGui references)
2. Camera system implementation
3. Road network rendering (Hermite curves)
4. Vehicle rendering (boxes with front indicator)
5. Basic main loop

### Phase 2: Interaction (2-3 hours)
1. Selection system (click to select)
2. Camera controls (pan/zoom)
3. Click-to-move command
4. Path editing mode (right-click, waypoint placement)

### Phase 3: UI Controls (3-4 hours)
1. Spawn controls panel
2. Formation controls panel
3. Simulation controls panel (FlightRecorder integration)
4. Inspector panel
5. Performance panel

### Phase 4: Advanced Rendering (2-3 hours)
1. Status labels above vehicles
2. Trajectory visualization
3. Formation structure visualization
4. Selection highlighting

### Phase 5: Polish & Testing (2-3 hours)
1. Performance profiling
2. Bug fixes
3. Documentation
4. Sample scenarios

**Total Estimated Time:** 11-16 hours

---

## Dependencies

- **Raylib-cs:** `dotnet add package Raylib-cs`
- **ImGui.NET:** `dotnet add package ImGui.NET`
- **Raylib-cs.Extras:** For ImGui integration with Raylib
- **CarKinem library:** Reference to CarKinem project
- **FDP/ModuleHost:** Reference to Fdp.Kernel, ModuleHost.Core

---

## Testing Strategy

### Manual Testing Scenarios

1. **Basic Movement:**
   - Spawn 10 vehicles
   - Click-to-move each vehicle
   - Verify they reach destinations

2. **Custom Paths:**
   - Select vehicle
   - Right-click to start path editing
   - Click waypoints
   - Right-click to finish
   - Verify vehicle follows path

3. **Formations:**
   - Create column formation (5 vehicles)
   - Move leader, verify members follow
   - Create wedge formation, verify correct positioning

4. **Road Navigation:**
   - Command vehicle to use road network
   - Verify Approaching → Following → Leaving → Arrived phases
   - Inspect segment IDs in inspector panel

5. **Flight Recorder:**
   - Record 100 frames
   - Pause, step backward/forward
   - Seek to specific frame
   - Save recording, reload, verify replay

6. **Performance:**
   - Spawn 1000 vehicles
   - Verify FPS stays above 30
   - Check frame time graph for spikes

---

## Future Enhancements

- **3D Visualization:** Elevation support, 3D camera
- **Traffic Signals:** Red/yellow/green lights at intersections
- **Pedestrians:** Additional entity type
- **Weather/Time-of-Day:** Visual effects
- **Export to Video:** Record MP4 of simulation
- **Multiplayer Simulation:** Multiple demo instances viewing same simulation

---

**Document Version:** 1.0  
**Last Updated:** 2026-01-07  
**Status:** Ready for Implementation (BATCH-CK-10)
