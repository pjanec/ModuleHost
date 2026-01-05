# Demo Application Design: Multiplayer Game Server Simulation

**Purpose:** Demonstrate all ModuleHost kernel features in a realistic, compelling scenario  
**Target:** Game server architecture (highly relatable, shows real-world benefits)  
**Complexity:** Medium (understandable but non-trivial)

---

## 🎮 Demo Concept: "BattleRoyale Server Simulator"

**Scenario:** A 100-player battle royale game server managing entities, players, AI bots, projectiles, and world state.

**Why This Demo:**
- ✅ **Relatable:** Everyone understands game servers
- ✅ **Shows value:** Fast tier = low latency, Slow tier = scalability
- ✅ **Natural separation:** Network vs AI vs Analytics
- ✅ **Multiple frequencies:** Network (60Hz), Physics (60Hz), AI (10Hz), Analytics (1Hz)
- ✅ **Events:** Player actions, damage, kills
- ✅ **Commands:** AI decisions, entity spawning
- ✅ **Component filtering:** AI only sees Position+Health, not NetworkState

---

## 📊 Architecture Overview

### Entity Types (100-500 entities)

| Entity Type | Count | Components | Update Frequency |
|-------------|-------|------------|------------------|
| **Players** | 100 | Position, Velocity, Health, Inventory, NetworkState | 60 FPS |
| **AI Bots** | 50 | Position, Velocity, Health, AIState | 10 Hz (AI) |
| **Projectiles** | 200 | Position, Velocity, Damage, Lifetime | 60 FPS |
| **Items** | 100 | Position, ItemType, Lootable | Static |
| **Safe Zone** | 1 | Position, Radius, Shrinking | 1 Hz |

**Total:** ~450 entities with 10 component types

### Modules (7 modules demonstrating different patterns)

| Module | Tier | Frequency | Purpose | Demonstrates |
|--------|------|-----------|---------|--------------|
| **NetworkSyncModule** | Fast | 60 Hz | Prepare state updates for clients | GDB, zero-copy, every frame |
| **FlightRecorderModule** | Fast | 60 Hz | Record game state for replay | GDB, persistent replica |
| **PhysicsModule** | Fast | 60 Hz | Collision detection, movement | GDB, read-only + commands |
| **AIModule** | Slow | 10 Hz | Bot decision-making | SoD, filtered components, commands |
| **PathfindingModule** | Slow | 5 Hz | Route planning for AI | SoD, shared snapshot (convoy) |
| **AnalyticsModule** | Slow | 1 Hz | Stats, heatmaps, metrics | SoD, minimal components |
| **WorldManagerModule** | Slow | 1 Hz | Safe zone shrinking, item spawning | SoD, commands |

---

## 🎯 Feature Demonstration Matrix

| Feature | Module(s) | How Demonstrated |
|---------|-----------|------------------|
| **Fast Tier (GDB)** | Network, Recorder, Physics | Runs every frame, zero-copy replica |
| **Slow Tier (SoD)** | AI, Pathfinding, Analytics, World | Runs at 1-10 Hz, pooled snapshots |
| **Different Frequencies** | All modules | 1 Hz, 5 Hz, 10 Hz, 60 Hz |
| **Component Filtering** | AI, Analytics | AI sees Pos+Health, Analytics sees Pos only |
| **Zero-Copy (GDB)** | Network, Recorder | Same replica every frame |
| **Pool Reuse (SoD)** | AI, World | Snapshots returned to pool |
| **Shared Snapshot (Convoy)** | AI + Pathfinding | Both use same snapshot (10 Hz) |
| **Event Accumulation** | AI | Sees 6 frames of DamageEvents |
| **Command Buffer** | All Slow modules | Queue entity spawns, damage, kills |
| **Read-only Safety** | All modules | Can't modify live world directly |
| **Concurrent Execution** | All modules | Modules run in parallel |
| **Phase-based Sync** | System | Clear phase separation shown |

---

## 💻 Implementation Structure

### Project: `Fdp.Examples.BattleRoyale`

```
Fdp.Examples.BattleRoyale/
├── Components/
│   ├── Position.cs          // Unmanaged
│   ├── Velocity.cs          // Unmanaged
│   ├── Health.cs            // Unmanaged
│   ├── AIState.cs           // Unmanaged
│   ├── Inventory.cs         // Unmanaged
│   ├── NetworkState.cs      // Unmanaged
│   ├── ItemType.cs          // Unmanaged
│   └── PlayerInfo.cs        // Managed (string Name, Guid PlayerId)
├── Events/
│   ├── DamageEvent.cs       // Unmanaged
│   ├── KillEvent.cs         // Unmanaged
│   └── ItemPickupEvent.cs   // Unmanaged
├── Modules/
│   ├── NetworkSyncModule.cs      // Fast tier
│   ├── FlightRecorderModule.cs   // Fast tier
│   ├── PhysicsModule.cs          // Fast tier
│   ├── AIModule.cs               // Slow tier
│   ├── PathfindingModule.cs      // Slow tier
│   ├── AnalyticsModule.cs        // Slow tier
│   └── WorldManagerModule.cs     // Slow tier
├── Systems/
│   ├── GameSimulation.cs    // Main simulation logic (Phase 1)
│   ├── EntityFactory.cs     // Create players, bots, items
│   └── CollisionDetection.cs
├── Visualization/
│   ├── ConsoleRenderer.cs   // Real-time console output
│   ├── StatsDisplay.cs      // Module performance stats
│   └── EventLog.cs          // Event stream visualization
└── Program.cs               // Main entry point
```

---

## 🔧 Detailed Module Design

### 1. NetworkSyncModule (Fast Tier - GDB)

**Purpose:** Prepare network updates for 100 players (60 Hz)

**Why Fast:**
- Players expect <50ms latency
- State must update every frame (16ms at 60 FPS)
- Zero-copy replica critical for performance

**Implementation:**
```csharp
public class NetworkSyncModule : IModule
{
    public string Name => "NetworkSync";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1; // Every frame
    
    private Dictionary<Entity, NetworkSnapshot> _lastSnapshots = new();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Read all players (zero-copy via GDB)
        var query = view.Query().With<Position>().With<NetworkState>().Build();
        
        foreach (var entity in query)
        {
            var pos = view.GetComponentRO<Position>(entity);
            var netState = view.GetComponentRO<NetworkState>(entity);
            
            // Check if state changed (delta compression)
            if (HasChanged(entity, pos, netState))
            {
                // Prepare network packet (would send to client)
                PrepareStateUpdate(entity, pos, netState);
            }
        }
        
        // Consume damage/kill events for this frame
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        var killEvents = view.ConsumeEvents<KillEvent>();
        
        // Would send events to relevant clients
    }
}
```

**Demonstrates:**
- ✅ Fast tier (every frame)
- ✅ Zero-copy GDB replica
- ✅ Event consumption
- ✅ Read-only access

---

### 2. FlightRecorderModule (Fast Tier - GDB)

**Purpose:** Record full game state for replay/debugging

**Why Fast:**
- Must capture every frame for accurate replay
- Persistent replica allows efficient state capture

**Implementation:**
```csharp
public class FlightRecorderModule : IModule
{
    public string Name => "FlightRecorder";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    private readonly List<FrameSnapshot> _recording = new();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Capture snapshot (full state, every frame)
        var frame = new FrameSnapshot
        {
            Tick = view.Tick,
            Time = view.Time,
            EntityCount = CountEntities(view),
            Events = new()
            {
                Damage = view.ConsumeEvents<DamageEvent>().ToArray(),
                Kills = view.ConsumeEvents<KillEvent>().ToArray()
            }
        };
        
        _recording.Add(frame);
        
        // Limit recording to last 1000 frames (~16 seconds at 60 FPS)
        if (_recording.Count > 1000)
            _recording.RemoveAt(0);
    }
    
    public void SaveReplay(string path)
    {
        // Serialize _recording to file
    }
}
```

**Demonstrates:**
- ✅ Fast tier
- ✅ Every-frame capture
- ✅ Persistent GDB replica (efficiently iterable)
- ✅ Event history access

---

### 3. PhysicsModule (Fast Tier - GDB with Commands)

**Purpose:** Collision detection and resolution

**Why Fast:**
- Physics must run at 60 Hz for smooth gameplay
- Needs to apply damage immediately

**Implementation:**
```csharp
public class PhysicsModule : IModule
{
    public string Name => "Physics";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Check projectile collisions
        var projectiles = view.Query().With<Position>().With<Damage>().Build();
        var players = view.Query().With<Position>().With<Health>().Build();
        
        foreach (var proj in projectiles)
        {
            var projPos = view.GetComponentRO<Position>(proj);
            
            foreach (var player in players)
            {
                var playerPos = view.GetComponentRO<Position>(player);
                
                if (Distance(projPos, playerPos) < 1.0f) // Hit!
                {
                    var damage = view.GetComponentRO<Damage>(proj);
                    var health = view.GetComponentRO<Health>(player);
                    
                    // Queue damage command
                    cmd.SetComponent(player, new Health 
                    { 
                        Current = health.Current - damage.Amount 
                    });
                    
                    // Queue destroy projectile
                    cmd.DestroyEntity(proj);
                    
                    // Publish damage event
                    PublishDamageEvent(player, damage.Amount);
                }
            }
        }
    }
}
```

**Demonstrates:**
- ✅ Fast tier with GDB
- ✅ Command buffer usage
- ✅ Read-only view (changes via commands)
- ✅ Read multiple component types

---

### 4. AIModule (Slow Tier - SoD with Filtering)

**Purpose:** Bot decision-making (10 Hz sufficient)

**Why Slow:**
- AI doesn't need 60 Hz updates (overkill)
- 10 Hz = 100ms decision latency (acceptable)
- Component filtering: AI only needs Position + Health

**Implementation:**
```csharp
public class AIModule : IModule
{
    public string Name => "AI";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6; // 10 Hz at 60 FPS
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // See accumulated events since last run (6 frames)
        var damageEvents = view.ConsumeEvents<DamageEvent>();
        
        // AI bots only see Position + Health (filtered SoD snapshot)
        var bots = view.Query().With<Position>().With<AIState>().Build();
        
        foreach (var bot in bots)
        {
            var pos = view.GetComponentRO<Position>(bot);
            var aiState = view.GetComponentRO<AIState>(bot);
            
            // Simple AI: Find nearest player
            var target = FindNearestPlayer(view, pos);
            
            if (target != Entity.Null)
            {
                // Move toward target
                var targetPos = view.GetComponentRO<Position>(target);
                var direction = Normalize(targetPos - pos);
                
                cmd.SetComponent(bot, new Velocity 
                { 
                    X = direction.X * 5.0f,
                    Y = direction.Y * 5.0f 
                });
                
                // If close enough, shoot
                if (Distance(pos, targetPos) < 10.0f)
                {
                    // Spawn projectile
                    var projectile = cmd.CreateEntity();
                    cmd.AddComponent(projectile, new Position { X = pos.X, Y = pos.Y });
                    cmd.AddComponent(projectile, new Velocity { X = direction.X * 20, Y = direction.Y * 20 });
                    cmd.AddComponent(projectile, new Damage { Amount = 10 });
                }
            }
        }
    }
}
```

**Demonstrates:**
- ✅ Slow tier (10 Hz)
- ✅ SoD filtered snapshot (Position + Health only)
- ✅ Event accumulation (6 frames of damage events)
- ✅ Command buffer (move, spawn entities)
- ✅ Reduced CPU (runs 1/6 as often)

---

### 5. PathfindingModule (Slow Tier - Shared Snapshot Convoy)

**Purpose:** Route planning for AI (5 Hz)

**Why Slow:**
- Pathfinding is expensive
- 5 Hz (200ms) is acceptable for route updates

**Implementation:**
```csharp
public class PathfindingModule : IModule
{
    public string Name => "Pathfinding";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 12; // 5 Hz at 60 FPS
    
    private Dictionary<Entity, List<Vector2>> _paths = new();
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Shares same snapshot as AIModule if using SharedSnapshotProvider
        
        var bots = view.Query().With<Position>().With<AIState>().Build();
        
        foreach (var bot in bots)
        {
            var pos = view.GetComponentRO<Position>(bot);
            var aiState = view.GetComponentRO<AIState>(bot);
            
            if (aiState.TargetEntity != Entity.Null)
            {
                var targetPos = view.GetComponentRO<Position>(aiState.TargetEntity);
                
                // Expensive pathfinding (A* algorithm)
                var path = CalculatePath(pos, targetPos);
                _paths[bot] = path;
            }
        }
    }
}
```

**Demonstrates:**
- ✅ Slow tier (5 Hz)
- ✅ Shared snapshot with AIModule (convoy pattern)
- ✅ Expensive computation justified by lower frequency

---

### 6. AnalyticsModule (Slow Tier - Minimal Filtering)

**Purpose:** Track statistics, heatmaps (1 Hz)

**Why Slow:**
- Analytics don't need real-time updates
- 1 Hz sufficient for metrics

**Implementation:**
```csharp
public class AnalyticsModule : IModule
{
    public string Name => "Analytics";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 60; // 1 Hz at 60 FPS
    
    private Dictionary<Vector2, int> _heatmap = new(); // Death heatmap
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Only sees Position (filtered SoD)
        
        // Count players in different zones
        var zones = new int[4]; // Divide map into 4 quadrants
        
        var players = view.Query().With<Position>().Build();
        foreach (var player in players)
        {
            var pos = view.GetComponentRO<Position>(player);
            int zone = GetZone(pos);
            zones[zone]++;
        }
        
        // Track kills (accumulated over 60 frames)
        var killEvents = view.ConsumeEvents<KillEvent>();
        foreach (var kill in killEvents)
        {
            // Add to heatmap
            _heatmap[kill.Position] = _heatmap.GetValueOrDefault(kill.Position) + 1;
        }
        
        // Log stats
        Console.WriteLine($"[Analytics] Tick {view.Tick}: " +
            $"Players: {zones.Sum()}, " +
            $"Kills this second: {killEvents.Length}");
    }
}
```

**Demonstrates:**
- ✅ Slow tier (1 Hz)
- ✅ Minimal component filtering (Position only)
- ✅ Event accumulation (60 frames)
- ✅ Low CPU usage for non-critical system

---

### 7. WorldManagerModule (Slow Tier - Commands)

**Purpose:** Safe zone shrinking, item spawning (1 Hz)

**Implementation:**
```csharp
public class WorldManagerModule : IModule
{
    public string Name => "WorldManager";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 60; // 1 Hz
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Shrink safe zone
        var safeZone = FindSafeZone(view);
        if (safeZone != Entity.Null)
        {
            var radius = view.GetComponentRO<SafeZone>(safeZone);
            cmd.SetComponent(safeZone, new SafeZone 
            { 
                Radius = radius.Radius * 0.99f // Shrink 1% per second
            });
        }
        
        // Spawn random items
        if (Random.Shared.NextDouble() < 0.3) // 30% chance per second
        {
            var item = cmd.CreateEntity();
            cmd.AddComponent(item, new Position 
            { 
                X = Random.Shared.Next(0, 1000),
                Y = Random.Shared.Next(0, 1000)
            });
            cmd.AddComponent(item, new ItemType { Type = ItemTypeEnum.HealthKit });
        }
    }
}
```

**Demonstrates:**
- ✅ Slow tier (1 Hz)
- ✅ Command buffer (entity creation, modification)
- ✅ Minimal frequency for world management

---

## 🖥️ Main Simulation Loop (Program.cs)

```csharp
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== BattleRoyale Server Simulator ===");
        Console.WriteLine("Demonstrating ModuleHost Hybrid GDB+SoD Architecture\n");
        
        // Setup
        using var liveWorld = new EntityRepository();
        RegisterComponents(liveWorld);
        
        var accumulator = new EventAccumulator(maxHistoryFrames: 60);
        using var moduleHost = new ModuleHostKernel(liveWorld, accumulator);
        
        // Register modules
        Console.WriteLine("Registering modules:");
        RegisterModules(moduleHost, liveWorld, accumulator);
        
        // Spawn initial entities
        Console.WriteLine("\nSpawning entities:");
        SpawnInitialEntities(liveWorld);
        
        // Run simulation
        Console.WriteLine("\nRunning simulation (press ESC to stop)...\n");
        
        const float deltaTime = 1.0f / 60.0f; // 60 FPS
        var frameCount = 0;
        var stopwatch = Stopwatch.StartNew();
        
        while (!Console.KeyAvailable || Console.ReadKey(true).Key != ConsoleKey.Escape)
        {
            // Phase 1: Game Simulation (Main Thread)
            UpdateGameLogic(liveWorld, deltaTime);
            
            // Phase 2: ModuleHost Update
            //  - Captures events
            //  - Syncs providers
            //  - Dispatches modules (async)
            //  - Plays back commands
            moduleHost.Update(deltaTime);
            
            // Phase 3: Visualization
            if (frameCount % 60 == 0) // 1 Hz
            {
                DisplayStats(liveWorld, moduleHost, frameCount, stopwatch.Elapsed);
            }
            
            frameCount++;
            liveWorld.Tick();
            
            // Simulate frame timing
            Thread.Sleep(16); // ~60 FPS
        }
        
        Console.WriteLine("\nSimulation stopped.");
        DisplayFinalStats(moduleHost, frameCount, stopwatch.Elapsed);
    }
}
```

---

## 📈 Console Output (Real-time Visualization)

```
=== BattleRoyale Server Simulator ===
Demonstrating ModuleHost Hybrid GDB+SoD Architecture

Registering modules:
  [FAST ] NetworkSync      (60 Hz) - Provider: DoubleBuffer (GDB)
  [FAST ] FlightRecorder   (60 Hz) - Provider: DoubleBuffer (GDB)
  [FAST ] Physics          (60 Hz) - Provider: DoubleBuffer (GDB)
  [SLOW ] AI               (10 Hz) - Provider: OnDemand (SoD, filtered)
  [SLOW ] Pathfinding      (5 Hz)  - Provider: Shared (convoy with AI)
  [SLOW ] Analytics        (1 Hz)  - Provider: OnDemand (SoD, filtered)
  [SLOW ] WorldManager     (1 Hz)  - Provider: OnDemand (SoD)

Spawning entities:
  Created 100 players
  Created 50 AI bots
  Created 100 items
  Created 1 safe zone

Running simulation (press ESC to stop)...

Frame 0060 (1.0s) | Entities: 251 | FPS: 60.0
  Module           | Runs | Avg Time | Provider      | Allocations
  ---------------- | ---- | -------- | ------------- | -----------
  NetworkSync      | 60   | 0.2ms    | GDB (0 copy)  | 0 bytes
  FlightRecorder   | 60   | 0.1ms    | GDB (0 copy)  | 0 bytes
  Physics          | 60   | 0.8ms    | GDB (0 copy)  | 0 bytes
  AI               | 10   | 2.3ms    | SoD (pooled)  | 0 bytes
  Pathfinding      | 5    | 5.1ms    | Shared        | 0 bytes
  Analytics        | 1    | 0.3ms    | SoD (pooled)  | 0 bytes
  WorldManager     | 1    | 0.1ms    | SoD (pooled)  | 0 bytes
  
  Events: Damage=45, Kills=3, Items Picked=7
  Safe Zone Radius: 995.0 units
  
  [NetworkSync] Prepared 100 state updates
  [AI] 50 bots made decisions, spawned 12 projectiles
  [Analytics] Zone distribution: [32, 28, 25, 15]

Frame 0120 (2.0s) | Entities: 268 | FPS: 60.0
  ...
```

---

## 🎯 Demo Value Propositions

### For Each Audience

**Game Developers:**
- "See how hybrid architecture scales to 100+ players"
- "Fast modules (network) get <1ms latency, slow modules (AI) don't waste CPU"
- "Zero allocations after warmup = stable frame times"

**System Architects:**
- "Phase-based execution eliminates need for complex locking"
- "Provider strategy pattern allows flexible replication"
- "Command buffer provides clean separation of concerns"

**Performance Engineers:**
- "GDB: Zero-copy for critical path (network, physics)"
- "SoD: Component filtering reduces bandwidth 80%"
- "Dirty tracking: Only 30% of chunks synced on average"

---

## 📁 Deliverable

Create project: `Fdp.Examples.BattleRoyale`

**Files to implement (~600-800 lines total):**
1. Components (10 files, ~100 lines)
2. Modules (7 files, ~300 lines)
3. Systems (3 files, ~200 lines)
4. Visualization (3 files, ~200 lines)
5. Program.cs (~100 lines)

**Estimated implementation time:** 1-2 days

---

## 🚀 Next Steps

**Would you like me to:**
1. ✅ Implement the full demo application?
2. ✅ Create a simplified version (fewer modules)?
3. ✅ Design an alternative demo scenario?
4. ✅ Generate the project structure and skeleton code?

**This demo would be PERFECT for:**
- Documentation examples
- Conference presentations
- Marketing materials
- Performance benchmarks
- Tutorial walkthrough

---

**What do you think? Should I proceed with implementation?** 🎮
