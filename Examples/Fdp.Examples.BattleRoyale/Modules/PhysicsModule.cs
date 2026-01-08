using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;
using Fdp.Examples.BattleRoyale.Events;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.BattleRoyale.Modules;

/// <summary>
/// PhysicsModule handles collision detection and damage application at 60 Hz.
/// Demonstrates Fast Tier (GDB) with command buffer usage for mutations.
/// </summary>
public class PhysicsModule : IModule
{
    public string Name => "Physics";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1; // Every frame
    
    public IEnumerable<Type> GetRequiredComponents()
    {
        yield return typeof(Position);
        yield return typeof(Damage);
        yield return typeof(Health);
    }
    
    private const float CollisionRadius = 1.0f;
    private const float CollisionRadiusSq = CollisionRadius * CollisionRadius;
    
    private int _totalCollisions = 0;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Get all projectiles
        var projectiles = view.Query()
            .With<Position>()
            .With<Damage>()
            .Build();
        
        // Get all players (targets)
        var players = view.Query()
            .With<Position>()
            .With<Health>()
            .Build();
        
        int collisionsThisFrame = 0;
        var projectilesHit = new HashSet<Entity>();
        
        foreach (var proj in projectiles)
        {
            // Skip if already hit
            if (projectilesHit.Contains(proj))
                continue;
                
            ref readonly var projPos = ref view.GetComponentRO<Position>(proj);
            ref readonly var damage = ref view.GetComponentRO<Damage>(proj);
            
            foreach (var player in players)
            {
                ref readonly var playerPos = ref view.GetComponentRO<Position>(player);
                
                // Distance check (squared for performance)
                float dx = projPos.X - playerPos.X;
                float dy = projPos.Y - playerPos.Y;
                float distSq = dx * dx + dy * dy;
                
                if (distSq < CollisionRadiusSq)
                {
                    ref readonly var health = ref view.GetComponentRO<Health>(player);
                    
                    // Apply damage via command buffer
                    float newHealth = Math.Max(0, health.Current - damage.Amount);
                    cmd.SetComponent(player, new Health 
                    { 
                        Current = newHealth, 
                        Max = health.Max 
                    });
                    
                    // NOTE: Event publishing would require access to world.Bus
                    // In a production system, command buffer would support PublishEvent
                    // For now, events are consumed but not produced by modules
                    
                    // Destroy projectile (via command buffer)
                    cmd.DestroyEntity(proj);
                    projectilesHit.Add(proj);
                    
                    collisionsThisFrame++;
                    _totalCollisions++;
                    
                    break; // Projectile can only hit one target
                }
            }
        }
        
        // Log collision activity
        if (collisionsThisFrame > 0)
        {
            Console.WriteLine($"[Physics @ T={view.Time:F2}s] " +
                $"{collisionsThisFrame} collisions detected (total: {_totalCollisions})");
        }
        
        // Log periodic stats
        if (view.Tick % 60 == 0)
        {
            int projCount = 0;
            foreach (var _ in projectiles) projCount++;
            
            int playerCount = 0;
            foreach (var _ in players) playerCount++;
            
            Console.WriteLine($"[Physics @ T={view.Time:F1}s] " +
                $"Tracking {projCount} projectiles, {playerCount} players");
        }
    }
}
