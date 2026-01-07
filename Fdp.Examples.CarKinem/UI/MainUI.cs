using ImGuiNET;
using System.Numerics;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Examples.CarKinem.Input;

namespace Fdp.Examples.CarKinem.UI
{
    public class MainUI
    {
        private SpawnControlsPanel _spawnControls = new();
        private SimulationControlsPanel _simControls = new();
        private EntityInspector _entityInspector = new();
        private EventInspector _eventInspector = new();
        private PerformancePanel _perfPanel = new();
        private SystemPerformanceWindow _sysPerfWindow = new();
        
        public UIState UIState { get; } = new();
        public bool IsPaused => _simControls.IsPaused;
        public float TimeScale => _simControls.TimeScale;

        public void Render(DemoSimulation simulation, SelectionManager selection)
        {
            ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(300, 500), ImGuiCond.FirstUseEver);
            
            if (ImGui.Begin("Simulation Control"))
            {
                ImGui.Text($"FPS: {Raylib_cs.Raylib.GetFPS()}");
                ImGui.Separator();
                
                if (ImGui.CollapsingHeader("Simulation", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    _simControls.Render(simulation);
                }
                
                if (ImGui.CollapsingHeader("Spawning", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    _spawnControls.Render(simulation, UIState);
                }
                
                if (ImGui.CollapsingHeader("Performance", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    _perfPanel.Render(simulation);
                    ImGui.Checkbox("Show System Profiler", ref _sysPerfWindow.IsOpen);
                }
                
                ImGui.End();
            }
            
            // Entity Inspector - separate window
            _entityInspector.SetContext((Fdp.Kernel.EntityRepository)simulation.View, selection);
            _entityInspector.Update();
            _entityInspector.DrawImGui();
            
            // Event Inspector - separate window
            _eventInspector.SetEventBus(simulation.Repository.Bus);
            _eventInspector.Update();
            _eventInspector.DrawImGui();
            
            // System Performance Profiler - separate window
            _sysPerfWindow.Render(simulation.Systems);
        }
    }
}
