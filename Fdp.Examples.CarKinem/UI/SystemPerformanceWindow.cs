using ImGuiNET;
using Fdp.Kernel;
using System.Collections.Generic;

namespace Fdp.Examples.CarKinem.UI
{
    public class SystemPerformanceWindow
    {
        public bool IsOpen = true;

        public void Render(IEnumerable<ComponentSystem> systems)
        {
            if (!IsOpen) return;

            if (ImGui.Begin("System Performance", ref IsOpen))
            {
                ImGui.Columns(2, "perf_cols");
                ImGui.Separator();
                ImGui.Text("System Name"); ImGui.NextColumn();
                ImGui.Text("Time (ms)"); ImGui.NextColumn();
                ImGui.Separator();
                
                double total = 0;
                
                foreach (var system in systems)
                {
                    ImGui.Text(system.GetType().Name);
                    ImGui.NextColumn();
                    ImGui.Text($"{system.LastUpdateDuration:F4}");
                    ImGui.NextColumn();
                    
                    total += system.LastUpdateDuration;
                }
                
                ImGui.Separator();
                ImGui.Text("Total (Systems Only)");
                ImGui.NextColumn();
                ImGui.Text($"{total:F4}");
                ImGui.Columns(1);
            }
            ImGui.End();
        }
    }
}
