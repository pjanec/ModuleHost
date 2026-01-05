using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Events;
using ModuleHost.Core.Abstractions;

namespace Fdp.Examples.BattleRoyale.Modules;

/// <summary>
/// FlightRecorderModule records game state every frame for replay and debugging.
/// Demonstrates Fast Tier (GDB) with persistent state tracking.
/// </summary>
public class FlightRecorderModule : IModule
{
    public string Name => "FlightRecorder";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1; // Every frame
    
    public class FrameSnapshot
    {
        public uint Tick;
        public float Time;
        public int EntityCount;
        public int DamageEventCount;
        public int KillEventCount;
        public int ItemPickupEventCount;
    }
    
    private readonly Queue<FrameSnapshot> _frames = new();
    private const int MaxFrames = 1000; // ~16 seconds at 60 FPS
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Count all entities
        var query = view.Query().Build();
        int entityCount = 0;
        foreach (var _ in query)
        {
            entityCount++;
        }
        
        // Consume all event types
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        var killEvents = view.ConsumeEvents<KillEvent>();
        var itemPickupEvents = view.ConsumeEvents<ItemPickupEvent>();
        
        // Create frame snapshot
        var frame = new FrameSnapshot
        {
            Tick = view.Tick,
            Time = view.Time,
            EntityCount = entityCount,
            DamageEventCount = damageEvents.Length,
            KillEventCount = killEvents.Length,
            ItemPickupEventCount = itemPickupEvents.Length
        };
        
        // Add to ring buffer
        _frames.Enqueue(frame);
        if (_frames.Count > MaxFrames)
        {
            _frames.Dequeue();
        }
        
        // Log stats every second
        if (view.Tick % 60 == 0)
        {
            Console.WriteLine($"[Recorder @ T={view.Time:F1}s] " +
                $"Recording: {_frames.Count} frames buffered, " +
                $"{entityCount} entities, " +
                $"Events this frame: {damageEvents.Length}D {killEvents.Length}K {itemPickupEvents.Length}I");
        }
    }
    
    /// <summary>
    /// Get recorded frames (for replay or analysis).
    /// </summary>
    public IReadOnlyCollection<FrameSnapshot> GetRecordedFrames() => _frames;
}
