using Raylib_cs;
using ImGuiNET;
using rlImGui_cs;
using System.Numerics;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Examples.CarKinem.Rendering;
using Fdp.Examples.CarKinem.Input;
using Fdp.Examples.CarKinem.UI;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Spatial;
using CarKinem.Systems;

namespace Fdp.Examples.CarKinem
{
    class Program
    {
        static void Main(string[] args)
        {
            // Initialize Raylib
            // Try to set flags before init to maybe help compatibility or logging
            Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);
            Raylib.InitWindow(1280, 720, "Car Kinematics Demo");
            Raylib.SetTargetFPS(60);
            
            // Initialize ImGui
            rlImGui.Setup(true);
            
            // Create simulation
            var simulation = new DemoSimulation();
            
            // Create managers
            var camera = new Camera2D { Offset = new Vector2(1280/2, 720/2), Target = new Vector2(0, 0), Zoom = 1.0f, Rotation = 0 };
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
                inputManager.HandleInput(selection, pathEditor, ref camera, simulation);
                
                // Simulation
                simulation.Tick(dt);
                
                // Render
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.DarkGray);
                
                Raylib.BeginMode2D(camera);
                
                // World rendering
                roadRenderer.RenderRoadNetwork(simulation.RoadNetwork, camera);
                vehicleRenderer.RenderVehicles(simulation.View, camera, selection.SelectedEntityId);
                
                if (selection.SelectedEntityId.HasValue)
                {
                    // Assuming valid entity for now or need to fix selection to store Entity
                    // We need to fetch generation or store Entity
                    // For now, let's just skip if we can't find generation
                }
                
                labelRenderer.RenderVehicleLabels(simulation.View, camera);
                pathEditor.Render(camera);
                
                Raylib.EndMode2D();
                
                // UI
                rlImGui.Begin();
                mainUI.Render(simulation, selection);
                rlImGui.End();
                
                Raylib.EndDrawing();
            }
            
            // Cleanup
            simulation.Dispose();
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }
    }
}
