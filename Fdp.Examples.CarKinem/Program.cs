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
            
            // Try to restore window size from previous session
            int windowWidth = 1280;
            int windowHeight = 720;
            if (File.Exists("window_config.txt"))
            {
                try
                {
                    var lines = File.ReadAllLines("window_config.txt");
                    if (lines.Length >= 2)
                    {
                        windowWidth = int.Parse(lines[0]);
                        windowHeight = int.Parse(lines[1]);
                    }
                }
                catch { /* Use defaults on error */ }
            }
            
            Raylib.InitWindow(windowWidth, windowHeight, "Car Kinematics Demo");
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
                inputManager.HandleInput(selection, pathEditor, ref camera, simulation, mainUI.UIState);
                
                // Simulation
                simulation.Tick(dt, mainUI.TimeScale);
                
                // Render
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.DarkGray);
                
                Raylib.BeginMode2D(camera);
                
                // World rendering
                roadRenderer.RenderRoadNetwork(simulation.RoadNetwork, camera);
                
                // Vehicles (Now using Raylib vector graphics)
                vehicleRenderer.RenderVehicles(simulation.View, camera, selection.SelectedEntityId);
                
                if (selection.SelectedEntityId.HasValue)
                {
                    // Render Active Trajectory for selected entity
                    var nav = simulation.GetNavState(selection.SelectedEntityId.Value);
                    if (nav.Mode == global::CarKinem.Core.NavigationMode.CustomTrajectory)
                    {
                        // Draw with Raylib (Background layer)
                        trajRenderer.RenderTrajectory(nav.TrajectoryId, nav.ProgressS, camera, new Color(180, 180, 180, 128)); // Thin Gray
                    }
                    else if (nav.Mode == global::CarKinem.Core.NavigationMode.Formation)
                    {
                        // If leader has trajectory, render it too
                        // Need to find leader... 
                        // FormationTarget component has leader info indirectly via position, but not ID?
                        // Actually FormationMember has LeaderEntityId.
                        var entity = new Fdp.Kernel.Entity(selection.SelectedEntityId.Value, 1); // Generation assumption risk
                        // Safe way: query component
                        if (simulation.View.HasComponent<FormationMember>(entity))
                        {
                            var member = simulation.View.GetComponentRO<FormationMember>(entity);
                            // We need to access the leader entity to get ITS nav state
                            // We don't have easy direct entity access by index without generation in View...
                            // But we can try to guess or use the member's target as a hint.
                            
                            // Let's iterate all entities to find the leader? No, slow.
                            // If we just want to render the leader's trajectory, we need the leader's ID.
                            // member.LeaderEntityId is the index.
                            // Let's try to get nav state for leader index.
                            var leaderNav = simulation.GetNavState(member.LeaderEntityId);
                            if (leaderNav.Mode == global::CarKinem.Core.NavigationMode.CustomTrajectory)
                            {
                                trajRenderer.RenderTrajectory(leaderNav.TrajectoryId, leaderNav.ProgressS, camera, new Color(180, 180, 180, 128));
                            }
                        }
                    }
                    
                    // Selection highlight handled in VehicleRenderer
                }
                
                pathEditor.Render(camera);
                
                Raylib.EndMode2D();
                
                // UI
                rlImGui.Begin();
                
                // Render Labels (Text needs to be screen space or ImGui)
                // WARNING: labelRenderer still uses ImGui.GetForegroundDrawList() which contributes to vertex count
                // If vertex limit is critical, we must limit label count or use Raylib text.
                // We'll limit it to only selected entity or when count is low.
                if (simulation.View.Query().With<VehicleState>().Build().Count() < 50)
                {
                    labelRenderer.RenderVehicleLabels(simulation.View, camera);
                }
                else if (selection.SelectedEntityId.HasValue)
                {
                    // Only render label for selected entity to save vertices
                    // We need a specific method for single label or just modify the renderer to filter?
                    // For now, let's just skip mass labeling.
                }
                
                mainUI.Render(simulation, selection);
                rlImGui.End();
                
                Raylib.EndDrawing();
            }
            
            // Save window size before cleanup
            try
            {
                File.WriteAllText("window_config.txt", $"{Raylib.GetScreenWidth()}\n{Raylib.GetScreenHeight()}");
            }
            catch { /* Ignore save errors */ }
            
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
                sim.Tick(0.1f, 1.0f);
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
