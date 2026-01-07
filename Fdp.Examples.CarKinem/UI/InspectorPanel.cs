using System;
using Fdp.Kernel;
using ImGuiNET;
using Fdp.Examples.CarKinem.Input;

namespace Fdp.Examples.CarKinem.UI
{
    public class InspectorPanel
    {
        private EntityInspector _entityInspector = new();
        private EventInspector _eventInspector = new();

        public void Render(EntityRepository repository, SelectionManager selectionManager)
        {
            if (!ImGui.Begin("Inspector"))
            {
                ImGui.End();
                return;
            }

            if (ImGui.BeginTabBar("InspectorTabs"))
            {
                if (ImGui.BeginTabItem("Components"))
                {
                    _entityInspector.SetContext(repository, selectionManager);
                    _entityInspector.Update();
                    _entityInspector.DrawImGui();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Global Events"))
                {
                    _eventInspector.SetEventBus(repository.Bus);
                    _eventInspector.Update(); 
                    _eventInspector.DrawImGui();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
        }
    }
}
