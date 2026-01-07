using System.Numerics;
using Raylib_cs;
using Fdp.Examples.CarKinem.Simulation;

namespace Fdp.Examples.CarKinem.Input
{
    public class InputManager
    {
        private Vector2 _dragStartPos;
        private bool _isDragging;
        private bool _possibleClick;

        public void HandleInput(SelectionManager selection, PathEditingMode pathEditor, ref Camera2D camera, DemoSimulation simulation)
        {
            float dt = Raylib.GetFrameTime();
            
            // Camera Zoom
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                Vector2 mouseScreenPos = Raylib.GetMousePosition();
                Vector2 worldPosBeforeZoom = Raylib.GetScreenToWorld2D(mouseScreenPos, camera);

                camera.Zoom = Math.Clamp(camera.Zoom + wheel * 0.125f * camera.Zoom, 0.1f, 5.0f);
                
                Vector2 worldPosAfterZoom = Raylib.GetScreenToWorld2D(mouseScreenPos, camera);
                camera.Target += (worldPosBeforeZoom - worldPosAfterZoom);
            }

            if (ImGuiNET.ImGui.GetIO().WantCaptureMouse) return;

            Vector2 mouseWorld = Raylib.GetScreenToWorld2D(Raylib.GetMousePosition(), camera);

            // Left Mouse Interaction (Select / Pan / Spawn)
            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                _dragStartPos = Raylib.GetMousePosition();
                _isDragging = false;
                _possibleClick = true;

                if (Raylib.IsKeyDown(KeyboardKey.LeftControl))
                {
                    // Instant Spawn
                    simulation.SpawnVehicle(mouseWorld, new Vector2(1, 0));
                    _possibleClick = false; // Don't process as selection
                }
            }

            if (Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                var currentPos = Raylib.GetMousePosition();
                if (Vector2.DistanceSquared(currentPos, _dragStartPos) > 4.0f) // Threshold
                {
                    _isDragging = true;
                    _possibleClick = false; // It's a drag, not a click
                }

                if (_isDragging && !Raylib.IsKeyDown(KeyboardKey.LeftControl))
                {
                    // Pan Logic
                    var delta = Raylib.GetMouseDelta();
                    delta = delta * (-1.0f / camera.Zoom);
                    camera.Target += delta;
                }
            }

            if (Raylib.IsMouseButtonReleased(MouseButton.Left))
            {
                if (_possibleClick && !Raylib.IsKeyDown(KeyboardKey.LeftControl))
                {
                    // It was a click (not a drag)
                    // Check for entity
                    int? clickedEntity = null;
                    float minDistance = float.MaxValue;
                    
                    var query = simulation.View.Query().With<global::CarKinem.Core.VehicleState>().Build();
                    query.ForEach((entity) => {
                         var state = simulation.View.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                         float dist = Vector2.Distance(state.Position, mouseWorld);
                         if (dist < 3.0f && dist < minDistance)
                         {
                             minDistance = dist;
                             clickedEntity = entity.Index;
                         }
                    });

                    // Update selection
                    selection.SelectedEntityId = clickedEntity;
                }
                
                _isDragging = false;
                _possibleClick = false;
            }

            if (Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                if (selection.SelectedEntityId.HasValue)
                {
                    simulation.IssueMoveToPointCommand(selection.SelectedEntityId.Value, mouseWorld);
                }
            }
        }
    }
}
