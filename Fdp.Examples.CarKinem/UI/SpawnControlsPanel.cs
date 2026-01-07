using ImGuiNET;
using System.Numerics;
using Fdp.Examples.CarKinem.Simulation;

namespace Fdp.Examples.CarKinem.UI
{
    public class SpawnControlsPanel
    {
        private int _spawnCount = 10;
        private bool _randomMovement = true;
        
        public void Render(DemoSimulation sim)
        {
            ImGui.SliderInt("Spawn Count", ref _spawnCount, 1, 100);
            ImGui.Checkbox("Random Movement", ref _randomMovement);
            
            if (ImGui.Button("Spawn Vehicles"))
            {
                SpawnVehicles(sim, _spawnCount, _randomMovement);
            }
            
            ImGui.SameLine();
            
            if (ImGui.Button("Clear All"))
            {
                // sim.ClearAllVehicles(); // TODO: Implement Clear in Simulation
            }
            
            // ImGui.Text($"Current Vehicles: {sim.GetVehicleCount()}"); // TODO: Implement Count
        }
        
        private void SpawnVehicles(DemoSimulation sim, int count, bool randomMovement)
        {
            var rng = new Random();
            
            for (int i = 0; i < count; i++)
            {
                Vector2 pos = new Vector2(
                    rng.Next(0, 500),
                    rng.Next(0, 500)
                );
                
                Vector2 heading = new Vector2(
                    (float)rng.NextDouble() * 2 - 1,
                    (float)rng.NextDouble() * 2 - 1
                );
                heading = Vector2.Normalize(heading);
                
                int entityIndex = sim.SpawnVehicle(pos, heading);
                
                if (randomMovement)
                {
                    Vector2 destination = new Vector2(
                        rng.Next(0, 500),
                        rng.Next(0, 500)
                    );
                    sim.IssueMoveToPointCommand(entityIndex, destination);
                }
            }
        }
    }
}
