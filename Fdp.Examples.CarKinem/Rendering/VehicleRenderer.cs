using Raylib_cs;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ImGuiNET;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class VehicleRenderer
    {
        public void RenderVehicles(ISimulationView view, Camera2D camera, int? selectedEntityId)
        {
            // Use ImGui's background draw list. 
            // It draws behind the main UI windows but on top of the generic Raylib background/cleared screen.
            var drawList = ImGui.GetBackgroundDrawList();

            var query = view.Query().With<global::CarKinem.Core.VehicleState>().With<global::CarKinem.Core.VehicleParams>().Build();
            
            query.ForEach((entity) =>
            {
                if (!view.HasComponent<global::CarKinem.Core.VehicleState>(entity) || !view.HasComponent<global::CarKinem.Core.VehicleParams>(entity))
                    return;

                var state = view.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                var parameters = view.GetComponentRO<global::CarKinem.Core.VehicleParams>(entity);
                
                // Calculate rotation
                float rotation = MathF.Atan2(state.Forward.Y, state.Forward.X);
                
                // Get color
                var (r, g, b) = global::CarKinem.Core.VehiclePresets.GetColor(parameters.Class);
                uint colorU32 = ImGui.GetColorU32(new Vector4(r/255f, g/255f, b/255f, 1.0f));
                
                float lineThickness = 2.0f; // Screen pixels
                
                if (selectedEntityId.HasValue && entity.Index == selectedEntityId.Value)
                {
                    colorU32 = ImGui.GetColorU32(new Vector4(0f, 1f, 0f, 1.0f)); // Green for selected
                    lineThickness = 3.5f;
                }
                
                // Calculate corners in world space
                float halfLen = parameters.Length / 2;
                float halfWidth = parameters.Width / 2;
                
                Vector2[] localCorners = new Vector2[]
                {
                    new Vector2(-halfLen, -halfWidth),
                    new Vector2(halfLen, -halfWidth),
                    new Vector2(halfLen, halfWidth),
                    new Vector2(-halfLen, halfWidth) 
                };
                
                // Transform to Screen Space directly
                Vector2[] screenCorners = new Vector2[4];
                float cosR = MathF.Cos(rotation);
                float sinR = MathF.Sin(rotation);

                for (int i = 0; i < 4; i++)
                {
                    // World transform
                    Vector2 worldPos = new Vector2(
                        state.Position.X + localCorners[i].X * cosR - localCorners[i].Y * sinR,
                        state.Position.Y + localCorners[i].X * sinR + localCorners[i].Y * cosR
                    );
                    
                    // Project to Screen
                    screenCorners[i] = Raylib.GetWorldToScreen2D(worldPos, camera);
                }
                
                // Draw Box (Polyline)
                // Note: AddPolyline takes points, num_points, col, flags, thickness
                // We simplify by adding lines manually or using AddPolyline with 'closed' flag
                drawList.AddPolyline(ref screenCorners[0], 4, colorU32, ImDrawFlags.Closed, lineThickness);

                // --- Draw Front Indicator (Triangle) ---
                
                // World coordinates for triangle
                float triangleSize = Math.Min(parameters.Length, parameters.Width) * 0.3f;
                Vector2 tipLocal = new Vector2(halfLen * 0.8f, 0);
                Vector2 leftLocal = new Vector2(halfLen * 0.3f, -triangleSize * 0.5f);
                Vector2 rightLocal = new Vector2(halfLen * 0.3f, triangleSize * 0.5f);
                
                Vector2 TransformToScreen(Vector2 local)
                {
                    Vector2 world = new Vector2(
                        state.Position.X + local.X * cosR - local.Y * sinR,
                        state.Position.Y + local.X * sinR + local.Y * cosR
                    );
                    return Raylib.GetWorldToScreen2D(world, camera);
                }

                Vector2 screenTip = TransformToScreen(tipLocal);
                Vector2 screenLeft = TransformToScreen(leftLocal);
                Vector2 screenRight = TransformToScreen(rightLocal);
                
                drawList.AddTriangleFilled(screenLeft, screenTip, screenRight, colorU32);
            });
        }
    }
}
