using ImGuiNET;
using Fdp.Examples.CarKinem.Simulation;
using System.Numerics;
using System;

namespace Fdp.Examples.CarKinem.UI
{
    public class SimulationControlsPanel
    {
        public bool IsPaused { get; private set; }
        public float TimeScale = 1.0f;

        public void Render(DemoSimulation sim)
        {
            // Sync local state for external readers (MainUI)
            IsPaused = sim.IsPaused;
            
            // Play/Pause
            if (ImGui.Button(sim.IsPaused ? "Play" : "Pause"))
            {
                sim.IsPaused = !sim.IsPaused;
            }
            
            ImGui.SameLine();
            
            // Step
            if (ImGui.Button("Step"))
            {
                sim.StepForward();
            }
            
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.SliderFloat("Speed", ref TimeScale, 0.1f, 5.0f);
            
            ImGui.Separator();
            
            var time = sim.Repository.GetSingletonUnmanaged<global::Fdp.Kernel.GlobalTime>();
            ImGui.Text($"Time: {time.TotalTime:F2}s | Frame: {time.FrameNumber}");
            
            ImGui.Separator();
            
            // Replay / Recording
            if (sim.IsReplaying) 
            {
                ImGui.TextColored(new Vector4(1, 1, 0, 1), $"REPLAY MODE [{sim.PlaybackController!.CurrentFrame}/{sim.PlaybackController!.TotalFrames}]");
                
                if (ImGui.Button("Stop Replay"))
                {
                    sim.StopReplay();
                    return;
                }
                
                int currentFrame = sim.PlaybackController!.CurrentFrame;
                int maxFrame = Math.Max(0, sim.PlaybackController!.TotalFrames - 1);
                
                if (ImGui.SliderInt("Timeline", ref currentFrame, 0, maxFrame))
                {
                    sim.IsPaused = true;
                    sim.PlaybackController!.SeekToFrame(sim.Repository, currentFrame);
                }
            }
            else
            {
                if (sim.IsRecording)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), "RECORDING");
                    ImGui.SameLine();
                    ImGui.Text($"{sim.Recorder?.RecordedFrames ?? 0} frames");
                }
                else
                {
                    ImGui.TextDisabled("Recording Stopped");
                }
                
                // Allow entering replay if we have data
                if (ImGui.Button("Enter Replay Mode"))
                {
                    sim.StartReplay();
                }
            }
        }
    }
}
