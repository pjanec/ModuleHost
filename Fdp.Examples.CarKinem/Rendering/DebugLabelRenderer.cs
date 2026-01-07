using Raylib_cs;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class DebugLabelRenderer
    {
        public void RenderVehicleLabels(ISimulationView view, Camera2D camera)
        {
            var query = view.Query().With<global::CarKinem.Core.VehicleState>().Build();
            
            query.ForEach((entity) =>
            {
                if (!view.HasComponent<global::CarKinem.Core.VehicleState>(entity)) return;
                
                var state = view.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                
                string text = $"ID: {entity.Index}";
                Raylib.DrawText(text, (int)state.Position.X, (int)state.Position.Y - 20, 10, Color.White);
            });
        }
    }
}
