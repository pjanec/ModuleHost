using ImGuiNET;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Kernel;

namespace Fdp.Examples.CarKinem.UI
{
    public class InspectorPanel
    {
        public void Render(DemoSimulation sim, int entityId)
        {
            ImGui.Begin("Inspector");
            ImGui.Text($"Entity ID: {entityId}");
            ImGui.Separator();

            // We construct an Entity handle. Brittle but works for demo context if alive.
            var entity = new Entity(entityId, 1); // Generation 1 assumption
            // Realistically we should look it up or pass the actual Entity struct from selection
            
            if (sim.View.IsAlive(entity))
            {
                 if (sim.View.HasComponent<global::CarKinem.Core.VehicleState>(entity))
                 {
                     var state = sim.View.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                     if (ImGui.TreeNode("Vehicle State"))
                     {
                         ImGui.Text($"Pos: {state.Position:F2}");
                         ImGui.Text($"Speed: {state.Speed:F2}");
                         ImGui.Text($"Steer: {state.SteerAngle:F2}");
                         ImGui.TreePop();
                     }
                 }
                 
                 if (sim.View.HasComponent<global::CarKinem.Core.NavState>(entity))
                 {
                     var nav = sim.View.GetComponentRO<global::CarKinem.Core.NavState>(entity);
                     if (ImGui.TreeNode("Navigation"))
                     {
                         ImGui.Text($"Mode: {nav.Mode}");
                         ImGui.Text($"Target Speed: {nav.TargetSpeed:F2}");
                         ImGui.TreePop();
                     }
                 }
            }
            else
            {
                ImGui.TextColored(new System.Numerics.Vector4(1,0,0,1), "Entity Destroyed or Invalid Handle");
            }
            
            ImGui.End();
        }
    }
}
