using System.Numerics;
using Raylib_cs;
using Fdp.Examples.CarKinem.Simulation;

namespace Fdp.Examples.CarKinem.Input
{
    public class InputManager
    {
        public void HandleInput(SelectionManager selection, PathEditingMode pathEditor, ref Camera2D camera, DemoSimulation simulation)
        {
            float dt = Raylib.GetFrameTime();
            
            // Camera Pan
            float panSpeed = 300.0f / camera.Zoom;
            if (Raylib.IsKeyDown(KeyboardKey.W)) camera.Target.Y -= panSpeed * dt;
            if (Raylib.IsKeyDown(KeyboardKey.S)) camera.Target.Y += panSpeed * dt;
            if (Raylib.IsKeyDown(KeyboardKey.A)) camera.Target.X -= panSpeed * dt;
            if (Raylib.IsKeyDown(KeyboardKey.D)) camera.Target.X += panSpeed * dt;
            
            // Camera Zoom
            float zoomSpeed = 0.5f;
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                camera.Zoom = Math.Clamp(camera.Zoom + wheel * zoomSpeed, 0.1f, 5.0f);
            }
            
            // Mouse handling
            Vector2 mouseWorld = Raylib.GetScreenToWorld2D(Raylib.GetMousePosition(), camera);
            
            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                // Simple selection logic (iterate all entities?)
                // For demo, we might need a spatial query, but we can brute force if entity count is small.
                // Or use SpatialHashSystem if exposed appropriately.
                // Currently SpatialHashSystem is internal logic.
                // We'll leave selection for later or implement simple radius check if needed.
                // Default: spawn vehicle on click if ctrl held
                if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
                {
                    simulation.SpawnVehicle(mouseWorld, new Vector2(1, 0));
                }
                else
                {
                    // Basic selection logic
                    bool found = false;
                    // Note: This brute forces the query which is fine for small N demo
                    var query = simulation.View.Query().With<global::CarKinem.Core.VehicleState>().Build();
                    query.ForEach((entity) => {
                         if (found) return;
                         var state = simulation.View.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                         if (Vector2.Distance(state.Position, mouseWorld) < 3.0f) // Check radius
                         {
                             selection.SelectedEntityId = entity.Index;
                             found = true;
                         }
                    });
                    
                    if (!found) selection.SelectedEntityId = null;
                }
            }
            
            if (Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                // Move command if entity selected
                if (selection.SelectedEntityId.HasValue)
                {
                    simulation.IssueMoveToPointCommand(selection.SelectedEntityId.Value, mouseWorld);
                }
            }
        }
    }
}
