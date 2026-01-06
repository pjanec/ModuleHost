using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Systems;
using Fdp.Examples.BattleRoyale.Components;
using Fdp.Examples.BattleRoyale.Modules;
using Fdp.Examples.BattleRoyale.Visualization;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.BattleRoyale;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== BattleRoyale Demo - Fast Tier Modules Test ===");
        Console.WriteLine();
        
        // Create entity repository
        var world = new EntityRepository();
        
        // Register all component types
        EntityFactory.RegisterAllComponents(world);
        Console.WriteLine("✓ Registered 10 component types");
        
        // Create event accumulator for module system
        var eventAccumulator = new EventAccumulator(maxHistoryFrames: 60);
        
        // Create ModuleHost kernel
        using var moduleHost = new ModuleHostKernel(world, eventAccumulator);
        moduleHost.SetSchemaSetup(EntityFactory.RegisterAllComponents);
        
        // Register Fast Tier modules
        moduleHost.RegisterModule(new NetworkSyncModule());
        Console.WriteLine("✓ Registered NetworkSyncModule (Fast tier)");
        
        moduleHost.RegisterModule(new FlightRecorderModule());
        Console.WriteLine("✓ Registered FlightRecorderModule (Fast tier)");
        
        moduleHost.RegisterModule(new PhysicsModule());
        Console.WriteLine("✓ Registered PhysicsModule (Fast tier)");

        // Register Slow Tier modules
        moduleHost.RegisterModule(new AIModule());
        Console.WriteLine("✓ Registered AIModule (Slow tier, 10 Hz)");

        moduleHost.RegisterModule(new AnalyticsModule());
        Console.WriteLine("✓ Registered AnalyticsModule (Slow tier, 1 Hz)");

        moduleHost.RegisterModule(new WorldManagerModule());
        Console.WriteLine("✓ Registered WorldManagerModule (Slow tier, 1 Hz)");
        
        // Register a global test system to verify scheduler
        moduleHost.RegisterGlobalSystem(new TestGlobalSystem());
        Console.WriteLine("✓ Registered TestGlobalSystem");
        
        // Initialize systems
        moduleHost.Initialize();
        Console.WriteLine("✓ Initialized ModuleHost (Systems Registered & Sorted)");
        
        // Print schedule debug info
        Console.WriteLine();
        Console.WriteLine("=== System Execution Schedule ===");
        Console.WriteLine(moduleHost.SystemScheduler.ToDebugString());
        Console.WriteLine("================================");
        
        Console.WriteLine();
        
        // Spawn entities
        EntityFactory.SpawnPlayers(world, 100);
        Console.WriteLine("✓ Spawned 100 players");
        
        EntityFactory.SpawnBots(world, 50);
        Console.WriteLine("✓ Spawned 50 AI bots");
        
        EntityFactory.SpawnItems(world, 100);
        Console.WriteLine("✓ Spawned 100 items");
        
        var safeZone = EntityFactory.CreateSafeZone(world);
        Console.WriteLine("✓ Created safe zone");
        
        // Create some test projectiles to demonstrate physics
        for (int i = 0; i < 10; i++)
        {
            EntityFactory.CreateProjectile(
                world,
                new Position { X = 100f + i * 10, Y = 100f + i * 10 },
                new Velocity { X = 5f, Y = 5f },
                25f
            );
        }
        Console.WriteLine("✓ Created 10 test projectiles");
        
        Console.WriteLine();
        Console.WriteLine($"Total entities: {world.EntityCount}");
        Console.WriteLine();
        Console.WriteLine("=== Running Simulation (300 frames = 5 seconds at 60 FPS) ===");
        Console.WriteLine();
        
        // Setup renderer
        var renderer = new ConsoleRenderer();

        // Run simulation loop
        const float deltaTime = 1.0f / 60.0f; // 60 FPS
        const int totalFrames = 300; // 5 seconds
        
        for (int frame = 0; frame < totalFrames; frame++)
        {
            try
            {
                // Update simulation time
                world.SetSimulationTime(frame * deltaTime);
                
                // Run all modules
                moduleHost.Update(deltaTime);
                
                // Increment tick
                world.Tick();
                
                // Render visualization (1 Hz)
                if (frame % 60 == 0)
                {
                    renderer.Render(frame, frame * deltaTime, world.EntityCount, 
                        moduleHost.GetExecutionStats());
                }
                
                // Simulate frame timing
                Thread.Sleep(16);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nCRASH at frame {frame}: {ex}");
                break;
            }
        }
        
        Console.WriteLine();
        Console.WriteLine("=== Simulation Complete ===");
        Console.WriteLine($"Final entity count: {world.EntityCount}");
        Console.WriteLine();
        // Console.ReadKey(); // Removing ReadKey to allow automated exit if needed, or keeping it? 
                              // Instructions don't say to remove it, but running tests often prefers no blocking.
                              // I'll leave ReadKey if it was there, but maybe comment it out if I need to capture output automatically. 
                              // The user instruction doesn't say "don't block". But for "dotnet test" runs it doesn't matter (main isn't called).
                              // If I run it via command line, I want it to finish so I can see output.
                              // I'll comment out ReadKey or rely on user running it. 
                              // Actually, if I run `dotnet run`, it will block. 
                              // The instructions say "Runtime output (5 second simulation)". 
                              // I will remove ReadKey to allow it to finish and return to terminal.
        // Console.ReadKey(); 
    }
}

[UpdateInPhaseAttribute(SystemPhase.Simulation)]
public class TestGlobalSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime) { }
}
