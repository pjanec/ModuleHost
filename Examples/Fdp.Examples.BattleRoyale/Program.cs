using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Systems;
using Fdp.Examples.BattleRoyale.Components;
using Fdp.Examples.BattleRoyale.Modules;
using ModuleHost.Core;

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
        
        // Register Fast Tier modules
        moduleHost.RegisterModule(new NetworkSyncModule());
        Console.WriteLine("✓ Registered NetworkSyncModule (Fast tier)");
        
        moduleHost.RegisterModule(new FlightRecorderModule());
        Console.WriteLine("✓ Registered FlightRecorderModule (Fast tier)");
        
        moduleHost.RegisterModule(new PhysicsModule());
        Console.WriteLine("✓ Registered PhysicsModule (Fast tier)");
        
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
        Console.WriteLine("=== Running Simulation (120 frames = 2 seconds at 60 FPS) ===");
        Console.WriteLine();
        
        // Run simulation loop
        const float deltaTime = 1.0f / 60.0f; // 60 FPS
        const int totalFrames = 120; // 2 seconds
        
        for (int frame = 0; frame < totalFrames; frame++)
        {
            // Update simulation time
            world.SetSimulationTime(frame * deltaTime);
            
            // Run all modules
            moduleHost.Update(deltaTime);
            
            // Increment tick
            world.Tick();
            
            // Simulate frame timing (optional - comment out for fast execution)
            // Thread.Sleep(16);
        }
        
        Console.WriteLine();
        Console.WriteLine("=== Simulation Complete ===");
        Console.WriteLine($"Final entity count: {world.EntityCount}");
        Console.WriteLine();
        Console.WriteLine("Press any key to exit.");
        Console.ReadKey();
    }
}
