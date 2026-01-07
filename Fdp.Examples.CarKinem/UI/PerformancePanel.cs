using ImGuiNET;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Kernel;

namespace Fdp.Examples.CarKinem.UI
{
    public class PerformancePanel
    {
        private float[] _frameTimeHistory = new float[60];
        private int _historyIndex = 0;
        
        public void Render(DemoSimulation sim)
        {
            float dt = Raylib_cs.Raylib.GetFrameTime() * 1000.0f; // ms
            
            _frameTimeHistory[_historyIndex] = dt;
            _historyIndex = (_historyIndex + 1) % _frameTimeHistory.Length;
            
            ImGui.Text($"FPS: {Raylib_cs.Raylib.GetFPS()}");
            ImGui.Text($"Frame Time: {dt:F2} ms");
            
            ImGui.PlotLines("Frame Time", ref _frameTimeHistory[0], _frameTimeHistory.Length, 0, "", 0, 33.0f, new System.Numerics.Vector2(0, 50));
            
            ImGui.Separator();
            // In a real ModuleHost scenario we would query the Kernel for system timings.
            // Since we are running systems manually in DemoSimulation, we don't have automatic metrics.
            // Placeholder for now.
             ImGui.TextDisabled("Detailed system profiling requires");
             ImGui.TextDisabled("kernel integration updates.");
        }
    }
}
