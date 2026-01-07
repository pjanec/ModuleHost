using Raylib_cs;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ImGuiNET;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class DebugLabelRenderer
    {
        public void RenderVehicleLabels(ISimulationView view, Camera2D camera)
        {
            // Get ImGui's foreground draw list (renders on top of everything)
            var drawList = ImGui.GetForegroundDrawList();
            
            var query = view.Query().With<global::CarKinem.Core.VehicleState>().Build();
            
            query.ForEach((entity) =>
            {
                if (!view.HasComponent<global::CarKinem.Core.VehicleState>(entity)) return;
                
                var state = view.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                
                string text = $"ID: {entity.Index}";
                
                // Position in world space (above the vehicle)
                Vector2 worldPos = new Vector2(state.Position.X, state.Position.Y - 2.0f);
                
                // Convert world position to screen position
                Vector2 screenPos = Raylib.GetWorldToScreen2D(worldPos, camera);
                
                // Measure text to center it horizontally
                Vector2 textSize = ImGui.CalcTextSize(text);
                screenPos.X -= textSize.X / 2.0f;
                
                // Draw text using ImGui (vector-based, always crisp!)
                // Add a black shadow for better readability
                drawList.AddText(
                    new Vector2(screenPos.X + 1, screenPos.Y + 1),
                    ImGui.GetColorU32(new Vector4(0, 0, 0, 0.8f)),
                    text
                );
                
                // Draw white text on top
                drawList.AddText(
                    screenPos,
                    ImGui.GetColorU32(new Vector4(1, 1, 1, 1)),
                    text
                );
            });
        }
    }
}
