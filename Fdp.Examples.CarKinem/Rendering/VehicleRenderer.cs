using Raylib_cs;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class VehicleRenderer
    {
        public void RenderVehicles(ISimulationView view, Camera2D camera, int? selectedEntityId)
        {
            var query = view.Query().With<global::CarKinem.Core.VehicleState>().With<global::CarKinem.Core.VehicleParams>().Build();
            
            query.ForEach((entity) =>
            {
                if (!view.HasComponent<global::CarKinem.Core.VehicleState>(entity) || !view.HasComponent<global::CarKinem.Core.VehicleParams>(entity))
                    return;

                var state = view.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                var parameters = view.GetComponentRO<global::CarKinem.Core.VehicleParams>(entity);
                
                // Draw vehicle
                // Calculate rotation from Forward vector
                float rotation = MathF.Atan2(state.Forward.Y, state.Forward.X) * (180.0f / MathF.PI);
                
                Rectangle rect = new Rectangle(state.Position.X, state.Position.Y, parameters.Length, parameters.Width);
                Vector2 origin = new Vector2(parameters.Length / 2, parameters.Width / 2);
                
                Color color = Color.Red; // Default
                if (selectedEntityId.HasValue && entity.Index == selectedEntityId.Value)
                {
                    color = Color.Green; // Selected
                }
                
                Raylib.DrawRectanglePro(rect, origin, rotation, color);
                
                // Draw wheels (simplified) - removed for brevity/safety
                /*
                float halfLen = parameters.Length * 0.4f;
                float halfWidth = parameters.Width * 0.4f;
                
                DrawWheel(state.Position, state.Forward, halfLen, halfWidth, state.SteerAngle, rotation); 
                */
            });
        }
        
        private void DrawWheel(Vector2 carPos, Vector2 carFwd, float offsetX, float offsetY, float steerAngle, float carRotationDeg)
        {
             // Calculate world position of wheel relative to car
             // This is complex with just rotation degree.
             // Let's keep it simple: just draw vehicle body for now to avoid mess.
        }
    }
}
