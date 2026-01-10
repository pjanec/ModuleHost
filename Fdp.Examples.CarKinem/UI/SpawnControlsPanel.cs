using ImGuiNET;
using System.Numerics;
using Fdp.Examples.CarKinem.Simulation;
using CarKinem.Core;
using CarKinem.Trajectory;
using CarKinem.Formation;

namespace Fdp.Examples.CarKinem.UI
{
    public class SpawnControlsPanel
    {
        private int _spawnCount = 10;
        private bool _randomMovement = true;
        public void Render(DemoSimulation sim, UIState uiState)
        {
            ImGui.SliderInt("Spawn Count", ref _spawnCount, 1, 100);
            ImGui.Checkbox("Random Movement", ref _randomMovement);
            
            // Trajectory Interpolation Toggle
            int mode = (int)uiState.InterpolationMode;
            ImGui.Text("Trajectory Interpolation:");
            if (ImGui.RadioButton("Linear", ref mode, 0)) uiState.InterpolationMode = TrajectoryInterpolation.Linear;
            ImGui.SameLine();
            if (ImGui.RadioButton("Catmull-Rom (Smooth)", ref mode, 1)) uiState.InterpolationMode = TrajectoryInterpolation.CatmullRom;
            
            // Vehicle class combo box
            string[] classNames = new string[]
            {
                "Personal Car",
                "Truck",
                "Bus",
                "Tank",
                "Pedestrian"
            };
            
            int selectedIndex = (int)uiState.SelectedVehicleClass;
            if (ImGui.Combo("Vehicle Type", ref selectedIndex, classNames, classNames.Length))
            {
                uiState.SelectedVehicleClass = (VehicleClass)selectedIndex;
            }
            
            if (ImGui.Button("Spawn Vehicles"))
            {
                SpawnVehicles(sim, _spawnCount, _randomMovement, uiState.SelectedVehicleClass, uiState.InterpolationMode);
            }
            
            ImGui.SameLine();
            
            if (ImGui.Button("Spawn Collision Test"))
            {
                sim.SpawnCollisionTest(uiState.SelectedVehicleClass);
            }
            ImGui.SameLine();
            if (ImGui.Button("Spawn Road Users"))
            {
               sim.SpawnRoadUsers(_spawnCount, uiState.SelectedVehicleClass);
            }
            ImGui.SameLine();
            if (ImGui.Button("Spawn Roamers"))
            {
               sim.SpawnRoamers(_spawnCount, uiState.SelectedVehicleClass, uiState.InterpolationMode);
            }
            ImGui.SameLine();
            
            ImGui.SameLine();
            
            if (ImGui.Button("Clear All"))
            {
                // sim.ClearAllVehicles(); // TODO: Implement Clear in Simulation
            }
            
            ImGui.Separator();
            ImGui.Text("Formation Controls");
            // Formation Type
            int fType = (int)uiState.SelectedFormationType;
            ImGui.Text("Type:"); ImGui.SameLine();
            if (ImGui.RadioButton("Column", ref fType, 0)) uiState.SelectedFormationType = FormationType.Column;
            ImGui.SameLine();
            if (ImGui.RadioButton("Wedge", ref fType, 1)) uiState.SelectedFormationType = FormationType.Wedge;
            ImGui.SameLine();
            if (ImGui.RadioButton("Line", ref fType, 2)) uiState.SelectedFormationType = FormationType.Line;
            
            if (ImGui.Button("Spawn Formation"))
            {
                sim.SpawnFormation(uiState.SelectedVehicleClass, uiState.SelectedFormationType, _spawnCount, uiState.InterpolationMode);
            }
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Hint: Select the Leader to move the entire formation.");
            ImGui.TextDisabled("(Leader marked with 'Leader' label)");
            
            // Show vehicle class info
            var preset = VehiclePresets.GetPreset(uiState.SelectedVehicleClass);
            ImGui.Separator();
            ImGui.Text($"Size: {preset.Length:F1}m x {preset.Width:F1}m");
            ImGui.Text($"Max Speed: {preset.MaxSpeedFwd:F1} m/s");
            ImGui.Text($"Max Turn: {(preset.MaxSteerAngle * 180 / MathF.PI):F0}°");
        }
        
        private void SpawnVehicles(DemoSimulation sim, int count, bool randomMovement, VehicleClass vehicleClass, TrajectoryInterpolation interpolation)
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
                
                int entityIndex = sim.SpawnVehicle(pos, heading, vehicleClass);
                
                if (randomMovement)
                {
                    Vector2 destination = new Vector2(
                        rng.Next(0, 500),
                        rng.Next(0, 500)
                    );
                    sim.IssueMoveToPointCommand(entityIndex, destination, interpolation);
                }
            }
        }
    }
}
