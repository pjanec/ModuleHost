namespace Fdp.Examples.BattleRoyale.Modules;

using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;
using Fdp.Examples.BattleRoyale.Events;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

public class AnalyticsModule : IModule
{
    public string Name => "Analytics";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 60; // 1 Hz
    
    private readonly Dictionary<Vector2Int, int> _killHeatmap = new();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Count entities by type (position-based classification)
        var allEntities = view.Query().With<Position>().Build();
        
        int playerCount = 0;
        int botCount = 0;
        int itemCount = 0;
        int projectileCount = 0;
        int alphaTeamCount = 0;
        int bravoTeamCount = 0;
        
        foreach (var entity in allEntities)
        {
            // Classify by other components
            if (view.HasComponent<NetworkState>(entity))
            {
                playerCount++;
                
                // ⭐ Demonstrate managed component querying
                if (view.HasManagedComponent<Team>(entity))
                {
                    var team = view.GetManagedComponentRO<Team>(entity);
                    if (team.TeamName == "Alpha")
                        alphaTeamCount++;
                    else if (team.TeamName == "Bravo")
                        bravoTeamCount++;
                }
            }
            else if (view.HasComponent<AIState>(entity))
                botCount++;
            else if (view.HasComponent<ItemType>(entity))
                itemCount++;
            else if (view.HasComponent<Damage>(entity))
                projectileCount++;
        }
        
        // Process kill events (accumulated over 60 frames)
        var killEvents = view.ConsumeEvents<KillEvent>();
        
        foreach (var kill in killEvents)
        {
            var gridPos = new Vector2Int(
                (int)(kill.Position.X / 100),
                (int)(kill.Position.Y / 100)
            );
            _killHeatmap[gridPos] = _killHeatmap.GetValueOrDefault(gridPos) + 1;
        }
        
        Console.WriteLine($"[Analytics @ T={view.Time:F1}s] " +
            $"Players:{playerCount} (Alpha:{alphaTeamCount} Bravo:{bravoTeamCount}) " +
            $"Bots:{botCount} Items:{itemCount} Projectiles:{projectileCount} " +
            $"Kills this second:{killEvents.Length}");
    }
}

record struct Vector2Int(int X, int Y);
