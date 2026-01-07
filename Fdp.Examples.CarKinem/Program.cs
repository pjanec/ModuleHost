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
            if (args.Contains("--headless"))
            {
                RunHeadless();
                return;
            }

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
                if (!mainUI.IsPaused)
                {
                     simulation.Tick(dt * mainUI.TimeScale);
                }
                
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

        static void RunHeadless()
        {
            Console.WriteLine("--- HEADLESS MODE START ---");
            using var sim = new DemoSimulation();
            
            // Spawn
            var eid = sim.SpawnVehicle(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(1, 0));
            Console.WriteLine($"Spawned Entity {eid} at (0,0)");
            
            // Issue Command
            sim.IssueMoveToPointCommand(eid, new System.Numerics.Vector2(100, 0));
            Console.WriteLine("Issued Move To (100,0)");
            
            // Tick loop
            for (int i = 0; i < 60; i++)
            {
                sim.Tick(0.1f);
                var nav = sim.GetNavState(eid);
                
                if (i % 10 == 0)
                {
                    // Access detailed state for debugging
                    // (Assuming we can't easily get Position without digging into View here, but NavState helps)
                    Console.WriteLine($"Tick {i}: NavMode={nav.Mode} TargetSpeed={nav.TargetSpeed:F2} Arrived={nav.HasArrived}");
                }
            }
            Console.WriteLine("--- HEADLESS MODE END ---");
        }
    }
}
