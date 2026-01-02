# Module Implementation Examples - Hybrid GDB+SoD Architecture

**Purpose:** Reference implementations showing how to write modules using the new `ISimulationView` API

---

## Example 1: Simple AI Module (Strategy Agnostic)

**File:** `Examples/SimpleAiModule.cs`

```csharp
using ModuleHost.Framework;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Commands;

namespace ModuleHost.Examples
{
    /// <summary>
    /// Example AI module that decides when units should retreat.
    /// This module is AGNOSTIC to whether it receives a GDB replica or SoD snapshot.
    /// </summary>
    public class SimpleAiModule : IModule
    {
        private BitMask256 _componentMask;
        private EventTypeMask _event Mask;
        
        public ModuleDefinition GetDefinition() => new()
        {
            Id = "SimpleAI",
            Version = "1.0",
            IsSynchronous = false,  // Background module
            TargetFrequencyHz = 10,  // Run at 10Hz
            MaxExpectedRuntimeMs = 50,  // AI takes ~50ms
            
            // Event-driven: wake immediately on Explosion events
            WatchEvents = new[] { typeof(ExplosionEvent) },
            WatchComponents = new[] { typeof(Health) }
        };
        
        public ComponentMask GetSnapshotRequirements()
        {
            // Declare what data we need
            _componentMask = new BitMask256();
            _componentMask.Set(typeof(Position));
            _componentMask.Set(typeof(Health));
            _componentMask.Set(typeof(Team));
            return new ComponentMask(_componentMask);
        }
        
        public EventTypeMask GetEventRequirements()
        {
            _eventMask = new EventTypeMask();
            _eventMask.Set(typeof(ExplosionEvent));
            return _eventMask;
        }
        
        public void Initialize(IModuleContext context) { }
        public void Start() { }
        public void Stop() { }
        public void RegisterSystems(ISystemRegistry registry) { }  // No sync systems
        
        /// <summary>
        /// Main AI tick - receives ISimulationView which could be GDB replica or SoD snapshot.
        /// Module doesn't care which!
        /// </summary>
        public JobHandle Tick(
            FrameTime time,
            ISimulationView view,  // ← Could be World B (GDB) or World C (SoD)!
            ICommandBuffer commands)
        {
            // 1. Check for explosions (event-driven logic)
            var explosions = view.ConsumeEvents<ExplosionEvent>();
            if (explosions.Length > 0)
            {
                HandleExplosions(view, commands, explosions);
            }
            
            // 2. Regular AI logic - find damaged units
            var query = view.Query()
                .With<Position>()
                .With<Health>()
                .With<Team>()
                .Build();
            
            query.ForEach(entity =>
            {
                var health = view.GetComponentRO<Health>(entity);
                
                // If health low, issue retreat command
                if (health.Value < 30)
                {
                    var position = view.GetComponentRO<Position>(entity);
                    var safePos = FindSafeLocation(position);
                    
                    commands.SetComponent(entity, new Orders
                    {
                        Type = OrderType.Retreat,
                        Destination = safePos
                    });
                }
            });
            
            return default;
        }
        
        private void HandleExplosions(
            ISimulationView view,
            ICommandBuffer commands,
            ReadOnlySpan<ExplosionEvent> explosions)
        {
            foreach (var explosion in explosions)
            {
                // Find nearby units and make them scatter
                // ... AI logic ...
            }
        }
        
        private Vector3 FindSafeLocation(Position currentPos)
        {
            // Pathfinding logic...
            return new Vector3();
        }
        
        public void DrawDiagnostics() { }
    }
}
```

---

## Example 2: Network Module (GDB-Optimized)

**File:** `Examples/NetworkModule.cs`

```csharp
using ModuleHost.Framework;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Examples
{
    /// <summary>
    /// Network module that serializes state to DDS.
    /// Runs at 60Hz on World B (GDB).
    /// </summary>
    public class NetworkModule : IModule
    {
        public ModuleDefinition GetDefinition() => new()
        {
            Id = "Network",
            Version = "1.0",
            IsSynchronous = false,
            TargetFrequencyHz = 60,  // High frequency
            MaxExpectedRuntimeMs = 5  // Must be fast
        };
        
        public ComponentMask GetSnapshotRequirements()
        {
            // Network needs ~50% of components (not internal physics data)
            var mask = new BitMask256();
            mask.Set(typeof(Position));
            mask.Set(typeof(Velocity));
            mask.Set(typeof(Identity));  // Tier 2
            mask.Set(typeof(Team));      // Tier 2
            // NOT needed: PhysicsInternal, CollisionHistory, etc.
            return new ComponentMask(mask);
        }
        
        public EventTypeMask GetEventRequirements()
        {
            // Network forwards events too
            var mask = new EventTypeMask();
            mask.Set(typeof(DamageEvent));
            mask.Set(typeof(DestroyedEvent));
            return mask;
        }
        
        public JobHandle Tick(
            FrameTime time,
            ISimulationView view,  // World B (GDB replica)
            ICommandBuffer commands)
        {
            // Serialize state to DDS
            var entities = view.Query().With<Position>().Build();
            
            foreach (var entity in entities)
            {
                if (!view.IsAlive(entity)) continue;
                
                var pos = view.GetComponentRO<Position>(entity);
                var identity = view.GetManagedComponentRO<Identity>(entity);
                
                // Serialize to DDS...
                PublishEntityUpdate(entity, pos, identity);
            }
            
            // Forward events
            var damageEvents = view.ConsumeEvents<DamageEvent>();
            foreach (var evt in damageEvents)
            {
                PublishDamageEvent(evt);
            }
            
            return default;
        }
        
        private void PublishEntityUpdate(Entity entity, Position pos, Identity identity) { }
        private void PublishDamageEvent(DamageEvent evt) { }
        
        public void Initialize(IModuleContext context) { }
        public void Start() { }
        public void Stop() { }
        public void RegisterSystems(ISystemRegistry registry) { }
        public void DrawDiagnostics() { }
    }
}
```

---

## Example 3: Flight Recorder Module (GDB 100%)

**File:** `Examples/FlightRecorderModule.cs`

```csharp
using ModuleHost.Framework;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Examples
{
    /// <summary>
    /// Flight recorder that captures 100% of simulation state.
    /// Runs at 60Hz on World B (GDB).
    /// </summary>
    public class FlightRecorderModule : IModule
    {
        public ModuleDefinition GetDefinition() => new()
        {
            Id = "FlightRecorder",
            Version = "1.0",
            IsSynchronous = false,
            TargetFrequencyHz = 60,
            MaxExpectedRuntimeMs = 10
        };
        
        public ComponentMask GetSnapshotRequirements()
        {
            // Recorder needs EVERYTHING
            return ComponentMask.All;
        }
        
        public EventTypeMask GetEventRequirements()
        {
            // Recorder captures ALL events
            return EventTypeMask.All;
        }
        
        public JobHandle Tick(
            FrameTime time,
            ISimulationView view,  // World B (GDB - full replica)
            ICommandBuffer commands)
        {
            // Recorder doesn't write commands, only reads
            
            // Compress and save state
            var frame = new RecorderFrame
            {
                Tick = view.Tick,
                Time = view.Time
            };
            
            // Serialize all entities
            var allEntities = view.Query().Build();
            foreach (var entity in allEntities)
            {
                if (!view.IsAlive(entity)) continue;
                
                // Access ALL components (100% of data)
                var position = view.GetComponentRO<Position>(entity);
                var velocity = view.GetComponentRO<Velocity>(entity);
                // ... all other components ...
                
                frame.AddEntity(entity, position, velocity, /* ... */);
            }
            
            // Serialize events
            SerializeAllEvents(frame, view);
            
            // Async compress and save to disk
            CompressAndSaveAsync(frame);
            
            return default;
        }
        
        private void SerializeAllEvents(RecorderFrame frame, ISimulationView view)
        {
            // Get all event types
            var explosions = view.ConsumeEvents<ExplosionEvent>();
            var damage = view.ConsumeEvents<DamageEvent>();
            // ... etc for all registered event types
            
            frame.AddEvents(explosions);
            frame.AddEvents(damage);
        }
        
        private void CompressAndSaveAsync(RecorderFrame frame) { }
        
        public void Initialize(IModuleContext context) { }
        public void Start() { }
        public void Stop() { }
        public void RegisterSystems(ISystemRegistry registry) { }
        public void DrawDiagnostics() { }
    }
}
```

---

## Example 4: Module Configuration (Host Setup)

**File:** `Examples/HostConfiguration.cs`

```csharp
using ModuleHost.Core;
using ModuleHost.Core.Providers;

namespace ModuleHost.Examples
{
    public class HostConfiguration
    {
        public static void ConfigureModuleHost(ModuleHostKernel host)
        {
            // Create strategies
            var fastGdb = new DoubleBufferProvider(host.LiveWorld);
            var slowSod = new OnDemandProvider(host.LiveWorld);
            
            // Option 1: Explicit strategy assignment
            host.RegisterModule(new FlightRecorderModule(), fastGdb);  // GDB
            host.RegisterModule(new NetworkModule(), fastGdb);         // GDB (shares World B)
            host.RegisterModule(new SimpleAiModule(), slowSod);        // SoD
            host.RegisterModule(new AnalyticsModule(), slowSod);       // SoD (shares World C)
            
            // Option 2: Auto-assign based on frequency
            host.UseAutoStrategy(
                fastThreshold: 30,  // >= 30Hz → GDB
                slowThreshold: 30   // <  30Hz → SoD
            );
            
            host.RegisterModule(new FlightRecorderModule());  // Auto → GDB (60Hz)
            host.RegisterModule(new NetworkModule());         // Auto → GDB (60Hz)
            host.RegisterModule(new SimpleAiModule());        // Auto → SoD (10Hz)
            
            // Option 3: Shared slow replay (Convoy pattern)
            var sharedSlow = new SharedSnapshotProvider(host.LiveWorld);
            host.RegisterModule(new SimpleAiModule(), sharedSlow);
            host.RegisterModule(new AnalyticsModule(), sharedSlow);
            // Both modules share World C, slowest defines pace
        }
    }
}
```

---

## Example 5: Component Types (Tier 1 & 2)

**File:** `Examples/ComponentDefinitions.cs`

```csharp
using System.Collections.Immutable;

namespace ModuleHost.Examples
{
    // Tier 1: Unmanaged (blittable structs)
    public struct Position
    {
        public float X, Y, Z;
    }
    
    public struct Velocity
    {
        public float X, Y, Z;
    }
    
    public struct Health
    {
        public int Value;
        public int Max;
    }
    
    // Tier 2: Managed (MUST be immutable records)
    public record Identity
    {
        public required string Callsign { get; init; }
        public required string UnitType { get; init; }
        public ImmutableList<string> Aliases { get; init; } = ImmutableList<string>.Empty;
    }
    
    public record Team
    {
        public required int TeamId { get; init; }
        public required string TeamName { get; init; }
    }
    
    public record Orders
    {
        public required OrderType Type { get; init; }
        public required Vector3 Destination { get; init; }
        public ImmutableList<Vector3> Waypoints { get; init; } = ImmutableList<Vector3>.Empty;
    }
    
    public enum OrderType
    {
        None,
        Move,
        Attack,
        Retreat
    }
}
```

---

## Example 6: Event Definitions

**File:** `Examples/EventDefinitions.cs`

```csharp
namespace ModuleHost.Examples
{
    // Tier 1: Unmanaged events (struct)
    public struct ExplosionEvent
    {
        public float X, Y, Z;
        public float Radius;
        public float Damage;
        public uint SourceEntity;
    }
    
    public struct DamageEvent
    {
        public uint TargetEntity;
        public int DamageAmount;
        public uint SourceEntity;
    }
    
    public struct DestroyedEvent
    {
        public uint Entity;
        public uint KilledBy;
    }
    
    // Tier 2: Managed events (class - must be serializable)
    public class ChatMessageEvent
    {
        public required string PlayerName { get; init; }
        public required string Message { get; init; }
        public required int TeamId { get; init; }
    }
}
```

---

## Module Implementation Checklist

When writing a new module:

```
Module Definition:
[ ] Set IsSynchronous (false for background)
[ ] Set TargetFrequencyHz
[ ] Set MaxExpectedRuntimeMs
[ ] Declare WatchEvents (event-driven triggers)
[ ] Declare WatchComponents (reactive scheduling)

Data Requirements:
[ ] Implement GetSnapshotRequirements() (declare needed components)
[ ] Implement GetEventRequirements() (declare needed events)

Tick Implementation:
[ ] Use ISimulationView (NOT EntityRepository directly)
[ ] Use GetComponentRO<T>() for Tier 1 (read-only ref)
[ ] Use GetManagedComponentRO<T>() for Tier 2
[ ] Use ConsumeEvents<T>() for event history
[ ] Use ICommandBuffer for all writes (never mutate view)

Testing:
[ ] Test with GDB provider
[ ] Test with SoD provider
[ ] Test event accumulation (slow frequency)
[ ] Verify no mutations to view
[ ] Verify command validation
```

---

## Common Patterns

### Pattern 1: Query + Read + Command

```csharp
var query = view.Query()
    .With<Position>()
    .With<Health>()
    .Build();

query.ForEach(entity => {
    // Read
    var health = view.GetComponentRO<Health>(entity);
    
    // Decide
    if (health.Value < 10)
    {
        // Command
        commands.DestroyEntity(entity);
    }
});
```

### Pattern 2: Event-Driven Logic

```csharp
var explosions = view.ConsumeEvents<ExplosionEvent>();
if (explosions.Length == 0) return;  // No events, early exit

foreach (var explosion in explosions)
{
    // React to explosion
    FindNearbyUnits(view, explosion.Position)
        .ForEach(unit => commands.SetComponent(unit, new Orders { Type = OrderType.Scatter }));
}
```

### Pattern 3: Optimistic Concurrency

```csharp
var enemy = FindTarget(view);
if (!view.IsAlive(enemy)) return;  // Already dead

var enemyGen = enemy.Generation;
var enemyPos = view.GetComponentRO<Position>(enemy);

// Async thinking... (50ms passes, world advances)

// Validate before commanding
commands.EnqueueValidated(new AttackCommand
{
    Target = enemy,
    ExpectedGeneration = enemyGen,  // ← Validation
    ExpectedPosition = enemyPos     // ← Validation
});
```

---

**Status:** ✅ Complete module examples with all patterns

**See Also:**
- [HYBRID-ARCHITECTURE-QUICK-REFERENCE.md](HYBRID-ARCHITECTURE-QUICK-REFERENCE.md)
- [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md)
- [IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md)

---

*Last Updated: January 4, 2026*
