using Raylib_cs;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ImGuiNET;
using CarKinem.Formation;
using Fdp.Examples.CarKinem.Components;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class VehicleRenderer
    {
        public void RenderVehicles(ISimulationView view, Camera2D camera, int? selectedEntityId)
        {
            // Use Raylib's 2D mode for vector graphics (high performance)
            // Assumes Camera2D mode is ALREADY ACTIVE (caller responsibility)

            var query = view.Query().With<global::CarKinem.Core.VehicleState>().With<global::CarKinem.Core.VehicleParams>().Build();
            
            query.ForEach((entity) =>
            {
                if (!view.HasComponent<global::CarKinem.Core.VehicleState>(entity) || !view.HasComponent<global::CarKinem.Core.VehicleParams>(entity))
                    return;

                var state = view.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                var parameters = view.GetComponentRO<global::CarKinem.Core.VehicleParams>(entity);
                
                // Calculate rotation (in degrees for Raylib)
                float rotationRad = MathF.Atan2(state.Forward.Y, state.Forward.X);
                // float rotationDeg = rotationRad * (180.0f / MathF.PI);
                
                // Get color based on behavior/role
                Color vehicleColor;

                // Priority 1: Component Override
                if (view.HasComponent<VehicleColor>(entity))
                {
                    var c = view.GetComponentRO<VehicleColor>(entity);
                    vehicleColor = new Color(c.R, c.G, c.B, c.A);
                }
                // Priority 2: Inferred Role (Fallback for backward compat or complex logic if needed)
                else if (view.HasComponent<FormationMember>(entity))
                {
                    vehicleColor = new Color(0, 200, 255, 255); // Cyan
                }
                else if (view.HasComponent<FormationRoster>(entity))
                {
                    vehicleColor = new Color(255, 0, 255, 255); // Magenta
                }
                else if (view.HasComponent<global::CarKinem.Core.NavState>(entity))
                {
                    var nav = view.GetComponentRO<global::CarKinem.Core.NavState>(entity);
                    if (nav.Mode == global::CarKinem.Core.NavigationMode.RoadGraph)
                        vehicleColor = new Color(50, 100, 255, 255); // Blue
                    else if (nav.Mode == global::CarKinem.Core.NavigationMode.CustomTrajectory)
                         vehicleColor = new Color(173, 255, 47, 255); // GreenYellow (default trajectory)
                    else
                        vehicleColor = new Color(200, 200, 200, 255); // Gray
                }
                else
                {
                    var (r, g, b) = global::CarKinem.Core.VehiclePresets.GetColor(parameters.Class);
                    vehicleColor = new Color((byte)r, (byte)g, (byte)b, (byte)255);
                }
                
                float thickness = 0.15f; // World units
                
                if (selectedEntityId.HasValue && entity.Index == selectedEntityId.Value)
                {
                    vehicleColor = Color.Green;
                    thickness = 0.3f;
                    
                    // --- Draw Nav Trajectory (Thin Line) for Selected ---
                    if (view.HasComponent<global::CarKinem.Core.NavState>(entity))
                    {
                        var nav = view.GetComponentRO<global::CarKinem.Core.NavState>(entity);
                        if (nav.Mode == global::CarKinem.Core.NavigationMode.CustomTrajectory || 
                            nav.Mode == global::CarKinem.Core.NavigationMode.RoadGraph ||
                            (nav.Mode == global::CarKinem.Core.NavigationMode.None && !nav.HasArrived.Equals(1)) ||
                            nav.Mode == global::CarKinem.Core.NavigationMode.Formation)
                        {
                             if (nav.Mode == global::CarKinem.Core.NavigationMode.None)
                             {
                                 Raylib.DrawLineEx(state.Position, nav.FinalDestination, 0.1f, new Color(0, 255, 255, 100));
                                 Raylib.DrawCircleV(nav.FinalDestination, 0.5f, new Color(0, 255, 255, 100));
                             }
                             else if (nav.Mode == global::CarKinem.Core.NavigationMode.CustomTrajectory)
                             {
                                 // Render trajectory path in gray
                                 // Assuming trajectory rendering is available via separate call or we do simple preview here
                                 // Since TrajectoryRenderer handles the full path, we might just draw a line to the next immediate target point if possible?
                                 // But TrajectoryRenderer is separate. Here we can just draw a connector to current target.
                                 // Let's defer full path rendering to TrajectoryRenderer in Program.cs, but draw a simple line to current target here.
                                 
                                 // Actually, user wants "spline path if following a spline path using thin gray line"
                                 // This is best handled by the TrajectoryRenderer, but maybe we can trigger it here?
                                 // No, VehicleRenderer doesn't have access to TrajectoryPool.
                                 // So we rely on Program.cs to render the trajectory for the selected entity.
                                 
                                 // Program.cs ALREADY calls trajRenderer.RenderTrajectory for the selected entity.
                                 // We just need to ensure it uses the right color (Thin Gray as requested).
                             }
                             else if (nav.Mode == global::CarKinem.Core.NavigationMode.Formation)
                             {
                                 // Draw line to formation target slot
                                 if (view.HasComponent<FormationTarget>(entity))
                                 {
                                     var target = view.GetComponentRO<FormationTarget>(entity);
                                     Raylib.DrawLineEx(state.Position, target.TargetPosition, 0.1f, new Color(200, 200, 200, 100));
                                     Raylib.DrawCircleV(target.TargetPosition, 0.3f, new Color(200, 200, 200, 100));
                                 }
                             }
                        }
                    }
                }
                
                // Calculate corners manually for rotating rectangle
                float halfLen = parameters.Length / 2;
                float halfWidth = parameters.Width / 2;
                float cosR = MathF.Cos(rotationRad);
                float sinR = MathF.Sin(rotationRad);
                
                Vector2 Transform(float x, float y)
                {
                     return new Vector2(
                        state.Position.X + x * cosR - y * sinR,
                        state.Position.Y + x * sinR + y * cosR
                    );
                }
                
                Vector2 p1 = Transform(-halfLen, -halfWidth);
                Vector2 p2 = Transform(halfLen, -halfWidth);
                Vector2 p3 = Transform(halfLen, halfWidth);
                Vector2 p4 = Transform(-halfLen, halfWidth);
                
                // Draw Box
                Raylib.DrawLineEx(p1, p2, thickness, vehicleColor);
                Raylib.DrawLineEx(p2, p3, thickness, vehicleColor);
                Raylib.DrawLineEx(p3, p4, thickness, vehicleColor);
                Raylib.DrawLineEx(p4, p1, thickness, vehicleColor);
                
                // Draw Front Triangle Indicator
                float triangleSize = Math.Min(parameters.Length, parameters.Width) * 0.3f;
                Vector2 t1 = Transform(halfLen * 0.8f, 0);
                Vector2 t2 = Transform(halfLen * 0.3f, -triangleSize * 0.5f);
                Vector2 t3 = Transform(halfLen * 0.3f, triangleSize * 0.5f);
                
                Raylib.DrawTriangle(t1, t3, t2, vehicleColor); // Note vertex order for culling

                // Render ID label using Raylib (Screen Space) to avoid ImGui vertex limit
                // We do this here inside the loop while we have the entity context
                // Note: We need to temporarily end Mode2D to draw screen space text, then begin again?
                // No, that's expensive per entity.
                // Alternative: Draw text in World Space with small scale?
                // Raylib.DrawTextEx is World Space if inside Mode2D? Yes, but it scales with zoom (gets blurry or huge).
                
                // Better approach: Collect labels and draw them in a second pass after EndMode2D?
                // Or just use DrawTextPro in World Space if we accept scaling.
                
                // Let's stick to world space geometry for now, and maybe avoid text for every single entity if count is high.
                // Only draw label for selected entity or if zoomed in closely?
                
                if (camera.Zoom > 0.5f || (selectedEntityId.HasValue && entity.Index == selectedEntityId.Value))
                {
                    // Simple world space text? Raylib default font is bitmap, scales poorly.
                    // Let's skip text for all entities to save performance/vertices, unless critical.
                }

                // --- Formation Visualization ---
                if (view.HasComponent<FormationRoster>(entity))
                {
                    var roster = view.GetComponentRO<FormationRoster>(entity);
                    if (roster.Count > 0)
                    {
                        // Draw leader marker
                         Raylib.DrawCircleV(state.Position, halfWidth * 0.8f, new Color(255, 0, 255, 100));
                         // Extra ring to signify leader clearly
                         // Use DrawRing for better visibility and float coordinates
                         Raylib.DrawRing(state.Position, halfWidth * 1.3f, halfWidth * 1.6f, 0, 360, 32, Color.Magenta);

                        // Draw lines to followers
                        for (int i = 1; i < roster.Count; i++)
                        {
                            var follower = roster.GetMember(i);
                            if (view.IsAlive(follower) && view.HasComponent<global::CarKinem.Core.VehicleState>(follower))
                            {
                                var fState = view.GetComponentRO<global::CarKinem.Core.VehicleState>(follower);
                                Raylib.DrawLineEx(state.Position, fState.Position, 0.1f, new Color(255, 0, 255, 128));
                            }
                        }
                    }
                }
            });
        }
    }
}
