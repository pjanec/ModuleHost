using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;

namespace Fdp.Examples.BattleRoyale.Systems;

public static class EntityFactory
{
    /// <summary>
    /// Registers all component types with the repository.
    /// Must be called before spawning entities.
    /// </summary>
    public static void RegisterAllComponents(EntityRepository world)
    {
        // Unmanaged components
        world.RegisterComponent<Position>();
        world.RegisterComponent<Velocity>();
        world.RegisterComponent<Health>();
        world.RegisterComponent<AIState>();
        world.RegisterComponent<Inventory>();
        world.RegisterComponent<NetworkState>();
        world.RegisterComponent<ItemType>();
        world.RegisterComponent<Damage>();
        world.RegisterComponent<SafeZone>();
        
        // Managed component
        world.RegisterComponent<PlayerInfo>();
    }
    
    /// <summary>
    /// Spawn players at random positions.
    /// </summary>
    public static void SpawnPlayers(EntityRepository world, int count = 100)
    {
        for (int i = 0; i < count; i++)
        {
            var entity = world.CreateEntity();
            
            // Position
            world.AddComponent(entity, new Position
            {
                X = Random.Shared.NextSingle() * 1000f,
                Y = Random.Shared.NextSingle() * 1000f
            });
            
            // Velocity (initially at rest)
            world.AddComponent(entity, new Velocity
            {
                X = 0f,
                Y = 0f
            });
            
            // Health
            world.AddComponent(entity, new Health
            {
                Current = 100f,
                Max = 100f
            });
            
            // Inventory (starting equipment)
            world.AddComponent(entity, new Inventory
            {
                Weapon = 1,
                Ammo = 30,
                HealthKits = 2
            });
            
            // Network state
            world.AddComponent(entity, new NetworkState
            {
                LastUpdateTick = 0,
                DirtyFlags = 0xFF  // All dirty initially
            });
            
            // Player info (managed)
            world.AddComponent(entity, new PlayerInfo
            {
                Name = $"Player_{i + 1}",
                PlayerId = Guid.NewGuid()
            });
        }
    }
    
    /// <summary>
    /// Spawn AI bots at random positions.
    /// </summary>
    public static void SpawnBots(EntityRepository world, int count = 50)
    {
        for (int i = 0; i < count; i++)
        {
            var entity = world.CreateEntity();
            
            // Position
            world.AddComponent(entity, new Position
            {
                X = Random.Shared.NextSingle() * 1000f,
                Y = Random.Shared.NextSingle() * 1000f
            });
            
            // Velocity (initially at rest)
            world.AddComponent(entity, new Velocity
            {
                X = 0f,
                Y = 0f
            });
            
            // Health
            world.AddComponent(entity, new Health
            {
                Current = 80f,  // Bots have slightly less health
                Max = 80f
            });
            
            // AI State
            world.AddComponent(entity, new AIState
            {
                TargetEntity = Entity.Null,
                AggressionLevel = Random.Shared.NextSingle()  // Random aggression 0-1
            });
        }
    }
    
    /// <summary>
    /// Spawn items (health kits, weapons, ammo) at random positions.
    /// </summary>
    public static void SpawnItems(EntityRepository world, int count = 100)
    {
        for (int i = 0; i < count; i++)
        {
            var entity = world.CreateEntity();
            
            // Position
            world.AddComponent(entity, new Position
            {
                X = Random.Shared.NextSingle() * 1000f,
                Y = Random.Shared.NextSingle() * 1000f
            });
            
            // Random item type
            var itemType = (ItemTypeEnum)(i % 3);  // Distribute evenly
            world.AddComponent(entity, new ItemType
            {
                Type = itemType
            });
        }
    }
    
    /// <summary>
    /// Create the safe zone entity.
    /// </summary>
    public static Entity CreateSafeZone(EntityRepository world)
    {
        var entity = world.CreateEntity();
        
        // Center of the map
        world.AddComponent(entity, new Position
        {
            X = 500f,
            Y = 500f
        });
        
        // Initial safe zone radius
        world.AddComponent(entity, new SafeZone
        {
            Radius = 800f
        });
        
        return entity;
    }
    
    /// <summary>
    /// Create a projectile fired from a position in a direction.
    /// </summary>
    public static Entity CreateProjectile(
        EntityRepository world,
        Position pos,
        Velocity vel,
        float damage)
    {
        var entity = world.CreateEntity();
        
        world.AddComponent(entity, pos);
        world.AddComponent(entity, vel);
        world.AddComponent(entity, new Damage
        {
            Amount = damage
        });
        
        return entity;
    }
}
