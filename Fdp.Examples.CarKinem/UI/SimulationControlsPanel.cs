using ImGuiNET;
using Fdp.Examples.CarKinem.Simulation;

namespace Fdp.Examples.CarKinem.UI
{
    public class SimulationControlsPanel
    {
        private bool _isPaused = false;
        private float _timeScale = 1.0f;
        
        public bool IsPaused => _isPaused;
        public float TimeScale => _timeScale;

        public void Render(DemoSimulation sim)
        {
            if (_isPaused)
            {
                if (ImGui.Button("Resume")) _isPaused = false;
                ImGui.SameLine();
                if (ImGui.Button("Step")) 
                {
                    // Single step logic handled in Program.cs typically, or we expose a step flag
                    // For now, let's just use pause flag.
                }
            }
            else
            {
                if (ImGui.Button("Pause")) _isPaused = true;
            }
            
            ImGui.SliderFloat("Time Scale", ref _timeScale, 0.1f, 5.0f);
            
            ImGui.Separator();
            var time = sim.Repository.GetSingletonUnmanaged<global::Fdp.Kernel.GlobalTime>();
            ImGui.Text($"Total Time: {time.TotalTime:F2}s");
            ImGui.Text($"Frame: {time.FrameCount}");
        }
    }
}
