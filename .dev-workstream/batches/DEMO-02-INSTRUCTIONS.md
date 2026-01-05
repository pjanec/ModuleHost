# DEMO-02 Developer Assignment - Fast Tier Modules

**Date:** January 5, 2026  
**Project:** BattleRoyale Demo - Fast Tier Modules  
**Estimated Time:** 1 day  
**Story Points:** 8

---

## ⚠️ CRITICAL: File Locations

**Project Directory (where you add code):**
```
D:\WORK\ModuleHost\Examples\Fdp.Examples.BattleRoyale\
```

**Report File (when done):**
```
D:\WORK\ModuleHost\.dev-workstream\reports\DEMO-02-REPORT.md
```

**Instructions File (this document):**
```
D:\WORK\ModuleHost\.dev-workstream\batches\DEMO-02-INSTRUCTIONS.md
```

---

## 📚 Required Reading

1. **Demo Design:** `D:\WORK\ModuleHost\docs\DEMO-APPLICATION-DESIGN.md` (lines 113-283)
2. **Module Examples:** `D:\WORK\ModuleHost\docs\MODULE-IMPLEMENTATION-EXAMPLES.md`
3. **Workflow:** `D:\WORK\ModuleHost\.dev-workstream\README.md`

---

## 🎯 Tasks

### TASK-005: NetworkSyncModule (3 SP)

**File to create:** `D:\WORK\ModuleHost\Examples\Fdp.Examples.BattleRoyale\Modules\NetworkSyncModule.cs`

**Purpose:** Simulate network state preparation for 100 players (60 Hz)

**Requirements:**
- Implements `IModule`
- `Tier = ModuleTier.Fast`
- `UpdateFrequency = 1` (every frame)
- Query players with Position + NetworkState
- Read-only access to components
- Track delta compression (position changes)
- Consume DamageEvent and KillEvent

**Template:**
```csharp
namespace Fdp.Examples.BattleRoyale.Modules;

public class NetworkSyncModule : IModule
{
    public string Name => "NetworkSync";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    private Dictionary<Entity, Position> _lastPositions = new();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var query = view.Query().With<Position>().With<NetworkState>().Build();
        
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
                // Would send network packet here
            }
        }
        
        // Consume events for this frame
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        var killEvents = view.ConsumeEvents<KillEvent>();
        
        // Log stats
        if (view.Tick % 60 == 0)
            Console.WriteLine($"[NetworkSync] Updated {updated} players, " +
                $"Events: {damageEvents.Length} damage, {killEvents.Length} kills");
    }
}
```

---

### TASK-006: FlightRecorderModule (2 SP)

**File to create:** `D:\WORK\ModuleHost\Examples\Fdp.Examples.BattleRoyale\Modules\FlightRecorderModule.cs`

**Purpose:** Record game state for replay/debugging

**Requirements:**
- Fast tier (records every frame)
- Capture entity counts
- Accumulate events
- Ring buffer (last 1000 frames)

**Template:**
```csharp
namespace Fdp.Examples.BattleRoyale.Modules;

public class FlightRecorderModule : IModule
{
    public string Name => "FlightRecorder";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    private class FrameSnapshot
    {
        public uint Tick;
        public float Time;
        public int EntityCount;
        public int DamageEventCount;
        public int KillEventCount;
    }
    
    private readonly Queue<FrameSnapshot> _frames = new();
    private const int MaxFrames = 1000; // ~16 seconds at 60 FPS
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var query = view.Query().Build(); // All entities
        int count = 0;
        foreach (var _ in query) count++;
        
        var frame = new FrameSnapshot
        {
            Tick = view.Tick,
            Time = view.Time,
            EntityCount = count,
            DamageEventCount = view.ConsumeEvents<DamageEvent>().Length,
            KillEventCount = view.ConsumeEvents<KillEvent>().Length
        };
        
        _frames.Enqueue(frame);
        if (_frames.Count > MaxFrames)
            _frames.Dequeue();
            
        if (view.Tick % 60 == 0)
            Console.WriteLine($"[Recorder] Recording: {_frames.Count} frames, " +
                $"{count} entities");
    }
}
```

---

### TASK-007: PhysicsModule (3 SP)

**File to create:** `D:\WORK\ModuleHost\Examples\Fdp.Examples.BattleRoyale\Modules\PhysicsModule.cs`

**Purpose:** Collision detection and damage application

**Requirements:**
- Fast tier (physics at 60 Hz)
- Check projectile-player collisions
- Use command buffer for damage
- Destroy projectiles on hit
- Simple distance-based collision (radius 1.0f)

**Template:**
```csharp
namespace Fdp.Examples.BattleRoyale.Modules;

public class PhysicsModule : IModule
{
    public string Name => "Physics";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Get all projectiles and players
        var projectiles = view.Query().With<Position>().With<Damage>().Build();
        var players = view.Query().With<Position>().With<Health>().Build();
        
        int collisions = 0;
        
        foreach (var proj in projectiles)
        {
            ref readonly var projPos = ref view.GetComponentRO<Position>(proj);
            ref readonly var damage = ref view.GetComponentRO<Damage>(proj);
            
            foreach (var player in players)
            {
                ref readonly var playerPos = ref view.GetComponentRO<Position>(player);
                
                // Distance check
                float dx = projPos.X - playerPos.X;
                float dy = projPos.Y - playerPos.Y;
                float distSq = dx * dx + dy * dy;
                
                if (distSq < 1.0f) // Hit radius
                {
                    ref readonly var health = ref view.GetComponentRO<Health>(player);
                    
                    // Apply damage
                    float newHealth = Math.Max(0, health.Current - damage.Amount);
                    cmd.SetComponent(player, new Health 
                    { 
                        Current = newHealth, 
                        Max = health.Max 
                    });
                    
                    // Destroy projectile
                    cmd.DestroyEntity(proj);
                    
                    collisions++;
                    
                    // Check for kill
                    if (newHealth <= 0)
                    {
                        // Would publish KillEvent here
                    }
                    
                    break; // Projectile destroyed
                }
            }
        }
        
        if (collisions > 0)
            Console.WriteLine($"[Physics] {collisions} collisions detected");
    }
}
```

---

## ✅ Acceptance Criteria

- [ ] All 3 modules compile without warnings
- [ ] Each module implements `IModule` correctly
- [ ] All use correct tier (Fast)
- [ ] All use correct frequency (1 = every frame)
- [ ] NetworkSync tracks delta compression
- [ ] Recorder maintains ring buffer
- [ ] Physics detects collisions and applies damage
- [ ] Console output shows module activity

---

## 🧪 Testing

**Update Program.cs** to register modules:

```csharp
// File: D:\WORK\ModuleHost\Examples\Fdp.Examples.BattleRoyale\Program.cs

using var world = new EntityRepository();
EntityFactory.RegisterAllComponents(world);

var accumulator = new EventAccumulator(maxHistoryFrames: 60);
using var moduleHost = new ModuleHostKernel(world, accumulator);

// Register fast tier modules
moduleHost.RegisterModule(new NetworkSyncModule());
moduleHost.RegisterModule(new FlightRecorderModule());
moduleHost.RegisterModule(new PhysicsModule());

// Spawn entities
EntityFactory.SpawnPlayers(world, 100);
EntityFactory.SpawnBots(world, 50);
EntityFactory.SpawnItems(world, 100);

// Run simulation
for (int frame = 0; frame < 120; frame++) // 2 seconds at 60 FPS
{
    moduleHost.Update(1.0f / 60.0f);
    world.Tick();
    Thread.Sleep(16);
}
```

---

## 📝 Deliverables

**Report File:** `D:\WORK\ModuleHost\.dev-workstream\reports\DEMO-02-REPORT.md`

**Must include:**
- Task completion status (3/3)
- Build output (zero warnings)
- Runtime output showing module activity
- File listing (3 new module files)
- Any issues encountered

---

## 💡 Key Implementation Notes

**Module Registration:**
- ModuleHost automatically assigns providers based on Tier
- Fast tier → DoubleBufferProvider (GDB, zero-copy)
- Modules run concurrently on thread pool

**Command Buffer:**
- All mutations go through `view.GetCommandBuffer()`
- Commands played back on main thread after modules finish
- Never modify components directly (read-only view)

**Event Consumption:**
- `ConsumeEvents<T>()` returns events since last run
- Fast tier (every frame): sees 1 frame of events
- Events automatically cleared after consumption

---

**Questions?** `D:\WORK\ModuleHost\.dev-workstream\reports\DEMO-02-QUESTIONS.md`  
**Done?** `D:\WORK\ModuleHost\.dev-workstream\reports\DEMO-02-REPORT.md`

**Let's build the fast tier!** ⚡🚀
