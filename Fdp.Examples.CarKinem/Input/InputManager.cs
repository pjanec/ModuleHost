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
        private KeyInputManager _keyManager;
        private DemoSimulation _boundSimulation;

        public InputManager()
        {
            _keyManager = new KeyInputManager();
            // We can't register keys here because we don't have the simulation instance yet.
            // We'll delay registration until HandleInput, or make it check if registered.
        }

        private void EnsureKeysRegistered(DemoSimulation simulation)
        {
            if (_boundSimulation == simulation) return;
            
            _boundSimulation = simulation;
            _keyManager.Clear();
            
            _keyManager.RegisterKey(KeyboardKey.Right, (count) => 
            {
                int step = 1;
                if (Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift)) step = 10;
                if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)) step = 100;
                
                for(int i=0; i<count; i++) simulation.StepForward(step);
            });
            
            _keyManager.RegisterKey(KeyboardKey.Left, (count) => 
            {
                int step = 1;
                if (Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift)) step = 10;
                if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl)) step = 100;

                for(int i=0; i<count; i++) simulation.StepBackward(step);
            });

            _keyManager.RegisterKey(KeyboardKey.Space, () => 
            {
                simulation.IsPaused = !simulation.IsPaused;
            });
        }

        public void HandleInput(SelectionManager selection, PathEditingMode pathEditor, ref Camera2D camera, DemoSimulation simulation, global::Fdp.Examples.CarKinem.UI.UIState uiState)
        {
            EnsureKeysRegistered(simulation);

            float dt = Raylib.GetFrameTime();
            var io = ImGuiNET.ImGui.GetIO();
            bool mouseCaptured = io.WantCaptureMouse;
            bool kbdCaptured = io.WantCaptureKeyboard;
            
            // Mouse Interaction (Zoom, Pan, Select)
            if (!mouseCaptured)
            {
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
                        float clickTolerance = 8.0f / camera.Zoom;
                        int? clickedEntity = null;
                        float minDistance = float.MaxValue;
                        
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
                            simulation.AddWaypoint(selection.SelectedEntityId.Value, mouseWorld, uiState.InterpolationMode);
                        }
                        else
                        {
                            // Right: Clear queue and move immediately
                            simulation.SetDestination(selection.SelectedEntityId.Value, mouseWorld, uiState.InterpolationMode);
                        }
                    }
                }
            }

            // Keyboard Shortcuts
            if (!kbdCaptured)
            {
                // Update and process auto-repeat keys
                _keyManager.Update(dt);
                _keyManager.ProcessActions();
            }
        }
    }
}
