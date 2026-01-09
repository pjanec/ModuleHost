using System;
using Fdp.Examples.CarKinem.Simulation;
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;

namespace Fdp.Examples.CarKinem
{
    public static class HeadlessStepper
    {
        public static void Run()
        {
            Console.WriteLine("--- HEADLESS STEPPER TEST ---");
            using var sim = new DemoSimulation();
            
            // 1. LIVE STEPPING
            Console.WriteLine("\n[Test 1] Live Stepping");
            // Advance 10 frames
            for(int i=0; i<10; i++) sim.Tick(0.016f, 1.0f);
            
            var time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Initial Frame: {time.FrameNumber} (Expected 10)");
            
            sim.IsPaused = true;
            sim.Tick(0.016f, 1.0f);
            time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Paused Tick Frame: {time.FrameNumber} (Expected 10)");
            
            Console.WriteLine("Stepping...");
            sim.StepFrames = 1;
            sim.Tick(0.016f, 1.0f);
            
            time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Stepped Frame: {time.FrameNumber} (Expected 11)");
            
            if (time.FrameNumber == 11) Console.WriteLine("LIVE SUCCESS");
            else Console.WriteLine("LIVE FAILURE");
            
            // 2. REPLAY STEPPING
            // We need to record some frames first.
            // We already recorded 11 frames (0-10) in 'demo_recording.fdp' because sim starts recording.
            
            Console.WriteLine("\n[Test 2] Replay Stepping");
            sim.StartReplay(); // Should load demo_recording.fdp (contains 11 frames)
            
            // Init
            sim.Tick(0.016f, 1.0f); // Replay Tick (Start paused)
            
            // Check Replay Frame (from PlaybackController)
            // Note: PlaybackController is not public, but accessible if we check internals?
            // We use sim.Time indirectly? No, in Replay, sim.Tick DOES NOT update GlobalTime via SetSingleton logic.
            // It relies on StepForward restoring it.
            
            // Check GlobalTime
            time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Replay Initial Frame: {time.FrameNumber}"); // Should be 0 (Rewound)
            
            // Step 1
            Console.WriteLine("Replay Step 1...");
            sim.StepFrames = 1;
            sim.Tick(0.016f, 1.0f);
            time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Replay Frame A: {time.FrameNumber}");
            
            // Step 2
            Console.WriteLine("Replay Step 2...");
            sim.StepFrames = 1;
            sim.Tick(0.016f, 1.0f);
            time = sim.Repository.GetSingletonUnmanaged<GlobalTime>();
            Console.WriteLine($"Replay Frame B: {time.FrameNumber}");
            
            if (time.FrameNumber > 0) Console.WriteLine("REPLAY SUCCESS");
            else Console.WriteLine("REPLAY FAILURE");
        }
    }
}
