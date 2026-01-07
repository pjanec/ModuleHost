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

        public void HandleInput(SelectionManager selection, PathEditingMode pathEditor, ref Camera2D camera, DemoSimulation simulation, global::Fdp.Examples.CarKinem.UI.UIState uiState)
        {
            float dt = Raylib.GetFrameTime();
            
            // Camera Zoom
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                Vector2 mouseScreenPos = Raylib.GetMousePosition();
                Vector2 worldPosBeforeZoom = Raylib.GetScreenToWorld2D(mouseScreenPos, camera);

                camera.Zoom = Math.Clamp(camera.Zoom + wheel * 0.125f * camera.Zoom, 0.01f, 100.0f);
                
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
                    // Instant Spawn - use selected vehicle class from UI
                    simulation.SpawnVehicle(mouseWorld, new Vector2(1, 0), uiState.SelectedVehicleClass);
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
                    // Check for entity with larger tolerance
                    int? clickedEntity = null;
                    float minDistance = float.MaxValue;
                    
                    // Base tolerance of 8 world units, scaled by zoom
                    // At zoom 1.0, tolerance is 8.0
                    // At zoom 2.0, tolerance is 4.0 (entities appear larger, need less tolerance)
                    // At zoom 0.5, tolerance is 16.0 (entities appear smaller, need more tolerance)
                    float clickTolerance = 8.0f / camera.Zoom;
                    
                    var query = simulation.View.Query().With<global::CarKinem.Core.VehicleState>().Build();
                    query.ForEach((entity) => {
                         var state = simulation.View.GetComponentRO<global::CarKinem.Core.VehicleState>(entity);
                         float dist = Vector2.Distance(state.Position, mouseWorld);
                         if (dist < clickTolerance && dist < minDistance)
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
                    if (Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift))
                    {
                        // Shift+Right: Append to queue
                        simulation.AddWaypoint(selection.SelectedEntityId.Value, mouseWorld);
                    }
                    else
                    {
                        // Right: Clear queue and move immediately
                        simulation.SetDestination(selection.SelectedEntityId.Value, mouseWorld);
                    }
                }
            }
        }
    }
}
