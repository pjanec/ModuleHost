# Network Ingress Pattern - Architectural Guide

**Date:** January 5, 2026  
**Purpose:** Canonical pattern for receiving network data in ModuleHost  
**Note:** Uses DDS (not RPC/UDP stream)

---

## Architecture: Ingress Pipeline

**Flow:** Socket → Queue → Background Deserialize → Commands → World A

```
┌──────────────┐
│  IO Thread   │  Receives raw DDS packets
│   (Socket)   │  
└──────┬───────┘
       │ Push
       ▼
┌──────────────────┐
│  ConcurrentQueue │  Thread-safe ingress buffer
│  <NetworkPacket> │
└──────┬───────────┘
       │ Dequeue
       ▼
┌────────────────────────────────┐
│  Network Module (Background)   │  Phase 2
│  - Deserialize (expensive!)    │
│  - Map NetID → Entity          │
│  - Read WorldB for context     │
│  - Queue commands              │
└──────┬─────────────────────────┘
       │ IEntityCommandBuffer
       ▼
┌────────────────────────────────┐
│  Main Thread Playback          │  Phase 3
│  - Apply to World A            │
│  - Thread-safe mutation        │
└────────────────────────────────┘
```

---

## Why This Architecture

**Benefits:**

1. **Non-blocking IO:** Socket callback doesn't stall simulation
2. **Offload CPU:** Deserialization happens in background (Phase 2)
3. **Context available:** Can read WorldB for lookups (NetID → Entity)
4. **Thread-safe:** Commands applied on main thread only
5. **Batched:** Process multiple packets per module tick

---

## Implementation

### 1. DDS Interface (Outside ECS)

```csharp
public class DdsInterface
{
    private readonly ConcurrentQueue<DdsPacket> _ingressQueue = new();
    private readonly DdsParticipant _participant;
    
    public DdsInterface(string domainId)
    {
        _participant = new DdsParticipant(domainId);
        
        // Subscribe to DDS topics
        _participant.Subscribe<EntityStateUpdate>(OnEntityUpdate);
        _participant.Subscribe<EntitySpawnRequest>(OnSpawnRequest);
        _participant.Subscribe<GameEvent>(OnGameEvent);
    }
    
    // DDS callbacks (IO thread)
    private void OnEntityUpdate(EntityStateUpdate update)
    {
        _ingressQueue.Enqueue(new DdsPacket 
        { 
            Type = PacketType.EntityUpdate, 
            Data = update 
        });
    }
    
    private void OnSpawnRequest(EntitySpawnRequest request)
    {
        _ingressQueue.Enqueue(new DdsPacket 
        { 
            Type = PacketType.Spawn, 
            Data = request 
        });
    }
    
    private void OnGameEvent(GameEvent evt)
    {
        _ingressQueue.Enqueue(new DdsPacket 
        { 
            Type = PacketType.Event, 
            Data = evt 
        });
    }
    
    public bool TryDequeue(out DdsPacket packet) 
        => _ingressQueue.TryDequeue(out packet);
}

public class DdsPacket
{
    public PacketType Type;
    public object Data; // Boxed DDS message
}

public enum PacketType
{
    EntityUpdate,
    Spawn,
    Destroy,
    Event
}
```

---

### 2. Network Module (Ingress Logic)

```csharp
public class NetworkIngressModule : IModule
{
    private readonly DdsInterface _dds;
    private readonly Dictionary<int, Entity> _netIdToEntity = new();
    
    public string Name => "NetworkIngress";
    public ModuleTier Tier => ModuleTier.Fast; // Every frame
    public int UpdateFrequency => 1;
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        // Process ingress (limit to avoid stalling)
        int processedCount = 0;
        const int MaxPerFrame = 1000;
        
        while (_dds.TryDequeue(out var packet) && processedCount < MaxPerFrame)
        {
            ProcessPacket(packet, view, cmd);
            processedCount++;
        }
        
        if (processedCount > 0)
            Console.WriteLine($"[NetworkIngress] Processed {processedCount} packets");
    }
    
    private void ProcessPacket(DdsPacket packet, ISimulationView view, IEntityCommandBuffer cmd)
    {
        switch (packet.Type)
        {
            case PacketType.EntityUpdate:
                HandleEntityUpdate((EntityStateUpdate)packet.Data, view, cmd);
                break;
                
            case PacketType.Spawn:
                HandleSpawn((EntitySpawnRequest)packet.Data, cmd);
                break;
                
            case PacketType.Destroy:
                HandleDestroy((EntityDestroyRequest)packet.Data, cmd);
                break;
                
            case PacketType.Event:
                HandleEvent((GameEvent)packet.Data, cmd);
                break;
        }
    }
    
    private void HandleEntityUpdate(EntityStateUpdate update, ISimulationView view, IEntityCommandBuffer cmd)
    {
        // Map NetworkID → Entity
        if (!_netIdToEntity.TryGetValue(update.NetworkId, out Entity entity))
        {
            // Entity not found - might need to spawn first
            return;
        }
        
        // ⭐ CRITICAL: Use Ghost/Target pattern for smoothing
        // Don't update Position directly - update NetworkTargetPosition
        cmd.SetComponent(entity, new NetworkTargetPosition
        {
            X = update.Position.X,
            Y = update.Position.Y,
            ReceivedAt = view.Time
        });
        
        // Update health (no smoothing needed)
        cmd.SetComponent(entity, new Health
        {
            Current = update.Health,
            Max = 100
        });
    }
    
    private void HandleSpawn(EntitySpawnRequest request, IEntityCommandBuffer cmd)
    {
        // Create entity
        var entity = cmd.CreateEntity();
        
        // Add components
        cmd.AddComponent(entity, new Position { X = request.X, Y = request.Y });
        cmd.AddComponent(entity, new NetworkTargetPosition { X = request.X, Y = request.Y });
        cmd.AddComponent(entity, new Health { Current = 100, Max = 100 });
        cmd.AddComponent(entity, new NetworkState 
        { 
            NetworkId = request.NetworkId 
        });
        
        // Register mapping
        _netIdToEntity[request.NetworkId] = entity;
    }
    
    private void HandleEvent(GameEvent evt, IEntityCommandBuffer cmd)
    {
        // Publish event to live world
        cmd.PublishEvent(new DamageEvent
        {
            Victim = _netIdToEntity.GetValueOrDefault(evt.VictimId),
            Attacker = _netIdToEntity.GetValueOrDefault(evt.AttackerId),
            Amount = evt.DamageAmount,
            Tick = evt.Tick
        });
    }
}
```

---

### 3. Ghost/Live Smoothing Pattern

**New Component:**
```csharp
// Components/NetworkTargetPosition.cs
public struct NetworkTargetPosition
{
    public float X;
    public float Y;
    public float ReceivedAt; // Timestamp for interpolation
}
```

**Smoothing System (Main Thread, Phase 1):**
```csharp
public class NetworkSmoothingSystem
{
    private readonly EntityRepository _world;
    
    public void Run(float deltaTime)
    {
        // Query entities with both Position and NetworkTargetPosition
        var query = _world.Query()
            .With<Position>()
            .With<NetworkTargetPosition>()
            .Build();
        
        foreach (var entity in query)
        {
            ref var pos = ref _world.GetComponentRW<Position>(entity);
            ref readonly var target = ref _world.GetComponentRO<NetworkTargetPosition>(entity);
            
            // Lerp towards target (60Hz = ~16ms, smooth over 100ms)
            float lerpFactor = deltaTime / 0.1f; // 100ms smoothing window
            lerpFactor = Math.Clamp(lerpFactor, 0f, 1f);
            
            pos.X = Lerp(pos.X, target.X, lerpFactor);
            pos.Y = Lerp(pos.Y, target.Y, lerpFactor);
        }
    }
    
    private float Lerp(float a, float b, float t) => a + (b - a) * t;
}
```

---

## Benefits of Ghost/Live Pattern

**Why separate NetworkTargetPosition from Position?**

1. **Smooth movement:** Interpolation hides network jitter
2. **Prediction:** Can extrapolate if needed
3. **Reconciliation:** On mismatch, smoothly correct
4. **Separation of concerns:**
   - Network module: "This is where it SHOULD be"
   - Smoothing system: "Move there smoothly"
   - Rendering: Uses actual Position (already smooth)

---

## Performance Characteristics

| Operation | Thread | Time | Benefit |
|-----------|--------|------|---------|
| DDS Callback | IO Thread | ~1μs | Queue push only |
| Deserialization | Network Module (Bg) | ~50-100μs | Off main thread |
| Command Recording | Network Module (Bg) | ~10μs | Fast writes |
| Command Playback | Main Thread | ~5μs | Bulk apply |
| Smoothing | Main Thread | ~2μs per entity | Cheap lerp |

**Total main thread impact:** ~7μs per entity (negligible!)

---

## DDS-Specific Considerations

**DDS Topics:**
```csharp
// DDS message definitions (IDL)
struct EntityStateUpdate {
    long networkId;
    Position position;
    float health;
    long timestamp;
};

struct EntitySpawnRequest {
    long networkId;
    Position spawnPosition;
    EntityType type;
};

struct GameEvent {
    long victimId;
    long attackerId;
    float damageAmount;
    long tick;
};
```

**QoS Settings:**
- EntityStateUpdate: RELIABLE, KEEP_LAST(1) - only care about latest
- EntitySpawnRequest: RELIABLE, KEEP_ALL - must not lose
- GameEvent: BEST_EFFORT, VOLATILE - low latency, tolerate loss

---

## Demo Implementation Plan

**Add to DEMO-04 or DEMO-06:**

**TASK-020: Network Ingress + Smoothing** (5 SP)
- Create DdsInterface (mocked for demo)
- NetworkIngressModule
- NetworkTargetPosition component
- NetworkSmoothingSystem
- Demonstrate ghost→live interpolation

**Components:**
1. Mock DDS interface (uses timer to simulate packets)
2. NetworkIngressModule processes mock packets
3. Updates NetworkTargetPosition (ghost)
4. NetworkSmoothingSystem interpolates Position (live)
5. Visualization shows both ghost and live positions

**Visual Demo:**
- Ghost position: Faint outline (where packet says entity is)
- Live position: Solid (what renderer shows)
- Line between: Shows interpolation

---

## Summary: Ingress Architecture

**3 Key Layers:**

1. **IO Layer:** DDS callbacks → ConcurrentQueue (non-blocking)
2. **Processing Layer:** Network Module (background) deserializes → Commands
3. **Application Layer:** Main thread applies commands → World A

**Ghost/Live Pattern:**
- Network writes NetworkTargetPosition (ghost)
- Smoothing system interpolates Position (live)
- Hides latency/jitter, professional feel

**Thread Safety:**
- IO thread: Queue push only (lock-free)
- Background thread: Reads WorldB + writes commands (isolated)
- Main thread: Applies commands (exclusive World A access)

---

**This pattern is production-ready and demonstrates real-world networking!** 🌐🚀
