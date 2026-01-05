using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;
using Fdp.Examples.BattleRoyale.Events;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.BattleRoyale.Modules;

/// <summary>
/// NetworkSyncModule simulates network state preparation for 100 players at 60 Hz.
/// Demonstrates Fast Tier (GDB) with delta compression and event consumption.
/// </summary>
public class NetworkSyncModule : IModule
{
    public string Name => "NetworkSync";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1; // Every frame
    
    private readonly Dictionary<Entity, Position> _lastPositions = new();
    private int _totalUpdates = 0;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Query all entities with Position + NetworkState
        var query = view.Query()
            .With<Position>()
            .With<NetworkState>()
            .Build();
        
        int updated = 0;
        
        foreach (var entity in query)
        {
            ref readonly var pos = ref view.GetComponentRO<Position>(entity);
            ref readonly var netState = ref view.GetComponentRO<NetworkState>(entity);
            
            // Check if position changed (delta compression)
            if (!_lastPositions.TryGetValue(entity, out var lastPos) ||
                Math.Abs(pos.X - lastPos.X) > 0.1f ||
                Math.Abs(pos.Y - lastPos.Y) > 0.1f)
            {
                _lastPositions[entity] = pos;
                updated++;
                _totalUpdates++;
                
                // In a real implementation, would send network packet here
                // e.g., SendStateUpdate(entity, pos, netState);
            }
        }
        
        // Consume events for this frame (critical for event-driven modules)
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        var killEvents = view.ConsumeEvents<KillEvent>();
        
        // Log stats every second (60 frames at 60 FPS)
        if (view.Tick % 60 == 0)
        {
            Console.WriteLine($"[NetworkSync @ T={view.Time:F1}s] " +
                $"Updated {updated} players, " +
                $"Events: {damageEvents.Length} damage, {killEvents.Length} kills, " +
                $"Total updates: {_totalUpdates}");
        }
    }
}
