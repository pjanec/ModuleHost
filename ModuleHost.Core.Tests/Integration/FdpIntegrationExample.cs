// File: ModuleHost.Core.Tests/Integration/FdpIntegrationExample.cs
using System;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using Xunit;

namespace ModuleHost.Core.Tests.Integration
{
    /// <summary>
    /// Example integration of ModuleHostKernel with FDP simulation loop.
    /// NOT production code - demonstrates integration pattern.
    /// </summary>
    public class FdpIntegrationExample
    {
        [Fact]
        public void ExampleSimulationLoop()
        {
            // ============================================
            // SETUP PHASE (Once at startup)
            // ============================================
            
            // Create live world (FDP)
            using var liveWorld = new EntityRepository();
            
            // Register components
            liveWorld.RegisterComponent<Position>();
            liveWorld.RegisterComponent<Velocity>();
            
            // Create entities
            for (int i = 0; i < 100; i++)
            {
                var e = liveWorld.CreateEntity();
                liveWorld.AddComponent(e, new Position { X = i, Y = 0 });
                liveWorld.AddComponent(e, new Velocity { X = 1, Y = 0 });
            }
            
            // Create event accumulator
            var eventAccumulator = new EventAccumulator(maxHistoryFrames: 10);
            
            // Create ModuleHost
            using var moduleHost = new ModuleHostKernel(liveWorld, eventAccumulator);
            
            // Register modules
            var networkModule = new MockModule("Network", ModuleTier.Fast, 1);
            var aiModule = new MockModule("AI", ModuleTier.Slow, 6);
            
            moduleHost.RegisterModule(networkModule);
            moduleHost.RegisterModule(aiModule);
            
            moduleHost.Initialize(); // REQUIRED
            
            // ============================================
            // SIMULATION LOOP (Every frame)
            // ============================================
            
            const float deltaTime = 1.0f / 60.0f; // 60 FPS
            
            for (int frame = 0; frame < 20; frame++)
            {
                // Phase 1: Simulation (main thread)
                // - Physics, gameplay logic, etc.
                // - Modifies liveWorld
                SimulatePhysics(liveWorld, deltaTime);
                
                // Phase 2: ModuleHost Update (main thread)
                // - Captures events
                // - Syncs providers
                // - Dispatches modules (async)
                moduleHost.Update(deltaTime);
                
                // Allow async modules to run
                System.Threading.Thread.Sleep(10);
                
                // Phase 3: Command Processing (main thread)
                // - Process commands from modules
                // - Apply to liveWorld
                // (Not implemented in this example)
            }
            
            // Verify modules ran correct number of times
            Assert.Equal(20, networkModule.TickCount); // Every frame
            // Logic for AI module (UpdateFrequency = 6):
            // Frames 0-5 (updates 1-6): Run at end of update 6? No, logic is (FramesSince+1 >= Freq).
            // Updates: 1, 2, 3, 4, 5 (run), 6... wait.
            // Let's re-verify logic:
            // Frame 0: FramesSince=0. +1 < 6. No run. FS -> 1.
            // Frame 1: FS=1. +1 < 6. No run. FS -> 2.
            // ...
            // Frame 4: FS=4. +1 < 6. No run. FS -> 5.
            // Frame 5: FS=5. +1 >= 6. RUN. FS -> 0.
            
            // So Runs indices: 5, 11, 17. (3 times in 20 frames).
            // Indices (0-based):
            // 0,1,2,3,4,5 (RUN)
            // 6,7,8,9,10,11 (RUN)
            // 12,13,14,15,16,17 (RUN)
            // 18,19 (Wait)
            
            Assert.Equal(3, aiModule.TickCount); 
        }
        
        private void SimulatePhysics(EntityRepository world, float deltaTime)
        {
            // Example: Move all entities
            var query = world.Query()
                .With<Position>()
                .With<Velocity>()
                .Build();
            
            world.Query()
                .With<Position>()
                .With<Velocity>()
                .Build()
                .ForEach(entity => 
                {
                    ref var pos = ref world.GetComponentRW<Position>(entity);
                    ref readonly var vel = ref world.GetComponentRO<Velocity>(entity);
                    
                    pos.X += (int)(vel.X * deltaTime); // Cast to int since struct uses int
                    pos.Y += (int)(vel.Y * deltaTime);
                });
        }
    }
    
    // Mock module for testing
    public class MockModule : IModule
    {
        public string Name { get; }
        public ModuleTier Tier { get; }
        public int UpdateFrequency { get; }
        public int TickCount { get; private set; }
        
        public MockModule(string name, ModuleTier tier, int frequency)
        {
            Name = name;
            Tier = tier;
            UpdateFrequency = frequency;
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            TickCount++;
            
            // Example: Count entities
            int count = 0;
            view.Query().Build().ForEach(e => count++);
            
            // Module work would go here
            // (e.g., AI decisions, network packets, analytics)
        }
    }
    
    struct Position { public int X, Y; }
    struct Velocity { public int X, Y; }
}
