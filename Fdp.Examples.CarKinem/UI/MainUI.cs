using ImGuiNET;
using System.Numerics;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Examples.CarKinem.Input;

namespace Fdp.Examples.CarKinem.UI
{
    public class MainUI
    {
        public void Render(DemoSimulation simulation, SelectionManager selection)
        {
            ImGui.Begin("Simulation Control");
            
            ImGui.Text($"FPS: {Raylib_cs.Raylib.GetFPS()}");
            
            // Spawn button
            if (ImGui.Button("Spawn Vehicle"))
            {
                 simulation.SpawnVehicle(new Vector2(100, 100), new Vector2(1, 0));
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Reset"))
            {
                // Reset logic here if implemented
            }
            
            ImGui.End();
            
            if (selection.SelectedEntityId.HasValue)
            {
                ImGui.Begin("Inspector");
                ImGui.Text($"Selected Entity: {selection.SelectedEntityId.Value}");
                
                var navParams = simulation.GetNavState(selection.SelectedEntityId.Value);
                ImGui.Text($"Mode: {navParams.Mode}");
                ImGui.Text($"Target Speed: {navParams.TargetSpeed:F2}");
                
                ImGui.End();
            }
        }
    }
}
