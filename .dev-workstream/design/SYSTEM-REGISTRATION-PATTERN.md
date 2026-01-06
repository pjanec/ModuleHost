# Module System Registration - Architectural Pattern

**Date:** January 6, 2026  
**Purpose:** Explain system registration pattern for modular ECS design

---

## Concept Overview

### Current Design: Module = Monolithic Logic

**Our current `IModule` interface:**
```csharp
public interface IModule
{
    string Name { get; }
    ModuleTier Tier { get; }
    int UpdateFrequency { get; }
    
    void Tick(ISimulationView view, float deltaTime);
}
```

**Characteristics:**
- Module **IS** the logic unit
- One `Tick()` method does everything
- Self-contained, simple

**Example:**
```csharp
public class AIModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // All AI logic here:
        FindTargets();
        MoveTowardsTarget();
        ShootProjectiles();
        UpdateBehaviorState();
    }
}
```

---

## System Registration Pattern

### Modules = System Containers

**Extended interface:**
```csharp
public interface IModule
{
    string Name { get; }
    ModuleTier Tier { get; }
    int UpdateFrequency { get; }
    
    // NEW: Register component systems
    void RegisterSystems(ISystemRegistry registry);
}

public interface ISystemRegistry
{
    void RegisterSystem<T>(T system) where T : IComponentSystem;
}

public interface IComponentSystem
{
    void Execute(ISimulationView view, float deltaTime);
}
```

**Characteristics:**
- Module **CONTAINS** multiple systems
- Systems are smaller, focused units
- Reusable across modules
- Explicit dependencies

---

## Why This Pattern?

### 1. Composition over Monoliths

**Problem with monolithic modules:**
```csharp
public class AIModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // 500 lines of code doing:
        // - Target selection
        // - Pathfinding
        // - Combat logic
        // - State machine
        // - Animation selection
        // All mixed together!
    }
}
```

**Hard to:**
- Test individual features
- Reuse logic (e.g., pathfinding in multiple modules)
- Understand dependencies
- Parallelize sub-tasks

---

**Solution with systems:**
```csharp
public class AIModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Register focused systems
        registry.RegisterSystem(new TargetSelectionSystem());
        registry.RegisterSystem(new PathfindingSystem());
        registry.RegisterSystem(new CombatSystem());
        registry.RegisterSystem(new AnimationSystem());
    }
}
```

**Benefits:**
- Each system has single responsibility
- Systems are testable in isolation
- Can reuse `PathfindingSystem` in other modules
- Clear execution order

---

### 2. Explicit Dependencies

**Without systems:**
```csharp
public void Tick(...)
{
    // Implicit dependency: Must pathfind before moving
    var path = CalculatePath();
    MoveAlongPath(path);
    
    // Implicit dependency: Must select target before shooting
    var target = SelectTarget();
    ShootAt(target);
}
```

**With systems:**
```csharp
public void RegisterSystems(ISystemRegistry registry)
{
    var pathfindingSystem = new PathfindingSystem();
    var movementSystem = new MovementSystem();
    
    // Explicit: Movement depends on pathfinding
    movementSystem.DependsOn(pathfindingSystem);
    
    registry.RegisterSystem(pathfindingSystem);
    registry.RegisterSystem(movementSystem);
}
```

---

### 3. Reusability Across Modules

**Example: Shared smoothing system**

```csharp
// Shared system (reusable)
public class NetworkSmoothingSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query()
            .With<Position>()
            .With<NetworkTargetPosition>()
            .Build();
        
        foreach (var e in query)
        {
            ref var pos = ref view.GetComponentRW<Position>(e);
            ref readonly var target = ref view.GetComponentRO<NetworkTargetPosition>(e);
            
            pos.X = Lerp(pos.X, target.X, deltaTime / 0.1f);
            pos.Y = Lerp(pos.Y, target.Y, deltaTime / 0.1f);
        }
    }
}

// Module A: Network ingress
public class NetworkModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new NetworkReceiveSystem());
        registry.RegisterSystem(new NetworkSmoothingSystem()); // Reused!
    }
}

// Module B: Local player prediction
public class PredictionModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new ClientPredictionSystem());
        registry.RegisterSystem(new NetworkSmoothingSystem()); // Reused!
    }
}
```

---

## Implementation Design

### System Registry

```csharp
public class SystemRegistry : ISystemRegistry
{
    private readonly List<IComponentSystem> _systems = new();
    private readonly Dictionary<Type, IComponentSystem> _systemsByType = new();
    
    public void RegisterSystem<T>(T system) where T : IComponentSystem
    {
        _systems.Add(system);
        _systemsByType[typeof(T)] = system;
    }
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Execute all registered systems in order
        foreach (var system in _systems)
        {
            system.Execute(view, deltaTime);
        }
    }
}
```

---

### Module with Systems

```csharp
public class AIModule : IModule
{
    private readonly SystemRegistry _registry = new();
    
    public string Name => "AI";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6;
    
    public AIModule()
    {
        // Register at construction time
        RegisterSystems(_registry);
    }
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new TargetSelectionSystem());
        registry.RegisterSystem(new PathfindingSystem());
        registry.RegisterSystem(new CombatDecisionSystem());
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Delegate to systems
        _registry.Execute(view, deltaTime);
    }
}
```

---

### Example Systems

```csharp
public class TargetSelectionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        var bots = view.Query().With<AIState>().With<Position>().Build();
        var players = view.Query().With<Health>().With<Position>().Build();
        
        foreach (var bot in bots)
        {
            // Find nearest player
            Entity target = FindNearestPlayer(bot, players, view);
            
            // Update AI state with target
            cmd.SetComponent(bot, new AIState { Target = target });
        }
    }
}

public class CombatDecisionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        var bots = view.Query().With<AIState>().With<Position>().Build();
        
        foreach (var bot in bots)
        {
            ref readonly var aiState = ref view.GetComponentRO<AIState>(bot);
            
            if (aiState.Target != Entity.Null && InRange(bot, aiState.Target, view))
            {
                // Spawn projectile
                SpawnProjectile(cmd, bot, aiState.Target, view);
            }
        }
    }
}
```

---

## When to Use System Registration

### ✅ Use When:

1. **Complex modules** with distinct responsibilities
   - Example: AI with targeting, pathfinding, combat, animation
   
2. **Reusable logic** across modules
   - Example: NetworkSmoothingSystem used by multiple modules
   
3. **Testability** is critical
   - Each system can be unit tested independently
   
4. **Team development**
   - Different developers work on different systems
   
5. **Dynamic composition**
   - Want to swap systems at runtime (e.g., easy/hard AI)

---

### ❌ NOT Needed When:

1. **Simple modules** with single responsibility
   - Example: WorldManagerModule (just shrinks safe zone)
   
2. **One-off logic**
   - Not reused anywhere else
   
3. **Rapid prototyping**
   - Extra abstraction slows iteration

---

## Hybrid Approach (Recommended)

**Keep both patterns:**

```csharp
// Simple module: No systems, direct Tick()
public class WorldManagerModule : IModule
{
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Simple logic, no need for systems
        ShrinkSafeZone(view);
    }
}

// Complex module: Uses systems
public class AIModule : IModule
{
    private readonly SystemRegistry _registry = new();
    
    public AIModule()
    {
        _registry.RegisterSystem(new TargetSelectionSystem());
        _registry.RegisterSystem(new PathfindingSystem());
        _registry.RegisterSystem(new CombatSystem());
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        _registry.Execute(view, deltaTime);
    }
}
```

---

## Comparison to Other Frameworks

### Unity DOTS
```csharp
// Unity: Systems are registered globally
public class MySystem : SystemBase
{
    protected override void OnUpdate()
    {
        Entities.ForEach((ref Position pos, in Velocity vel) => {
            pos.Value += vel.Value * deltaTime;
        }).Run();
    }
}
```

**Difference:**
- Unity: Systems are global, scheduled by framework
- ModuleHost: Systems are local to modules, executed by module

---

### Bevy (Rust)
```rust
// Bevy: Systems registered in app builder
app.add_system(movement_system)
   .add_system(collision_system)
   .add_system(render_system);
```

**Difference:**
- Bevy: Systems are functions, scheduled globally
- ModuleHost: Systems are classes, owned by modules

---

### Our Design (ModuleHost)
```csharp
// ModuleHost: Systems are optional, module-scoped
public class AIModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new TargetingSystem());
        registry.RegisterSystem(new CombatSystem());
    }
}
```

**Advantages:**
- ✅ Modules can be simple (no systems) or complex (with systems)
- ✅ Systems are scoped to modules (no global scheduling)
- ✅ Compatible with ModuleTier system
- ✅ Gradual adoption (add systems only when needed)

---

## Recommendation for BattleRoyale Demo

**Current implementation (DEMO-03):** ✅ Good as-is
- Modules are simple enough
- No reuse of logic yet
- Clear and understandable

**Future (DEMO-06?):** Consider adding systems when:
- AI gets more complex (targeting, pathfinding, behavior trees)
- Need to reuse logic (network smoothing in multiple places)
- Want to demonstrate advanced patterns

**Don't add systems just for abstraction's sake!**

---

## Summary

**System Registration Pattern:**
- Modules contain multiple smaller systems
- Systems are focused, reusable, testable
- Explicit dependencies and composition

**When useful:**
- Complex modules
- Reusable logic
- Team development
- Testing requirements

**Current ModuleHost design:**
- Simple modules use `Tick()` directly
- Complex modules can adopt system registry
- Hybrid approach: both patterns coexist

**For BattleRoyale demo:**
- Current design (DEMO-03) is appropriate
- Add systems later if complexity warrants it

---

**The pattern is valuable for complex scenarios but not mandatory for our current demo!** 🎯
