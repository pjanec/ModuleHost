# ModuleHost System Scheduling - Production Design

**Date:** January 6, 2026  
**Version:** 2.0 (Architect-Approved)  
**Status:** 🎯 PRODUCTION READY

---

## Overview

ModuleHost uses a **phase-based execution model** with **attribute-driven dependency resolution** to schedule system execution. Systems are small, focused units of logic that execute in a deterministic order determined by topological sorting of their declared dependencies.

**Architect Verdict:** ✅ Approved - *"Solid, professional-grade design that aligns perfectly with FDP hybrid philosophy."*

---

## Core Concepts

### 1. Systems

**A system is a focused unit of logic that operates on components.**

```csharp
public interface IComponentSystem
{
    void Execute(ISimulationView view, float deltaTime);
}
```

**Characteristics:**
- Single responsibility
- Operates on specific component sets
- Reusable across modules
- Declares phase and dependencies via attributes

---

### 2. Phases

**Phases organize system execution into logical buckets within the frame.**

```csharp
public enum SystemPhase
{
    Input = 1,              // Hardware input, early processing (Main Thread)
    BeforeSync = 2,         // Pre-sync preparation (Main Thread)
    // [SYNC A → B] - Kernel Operation
    Simulation = 10,        // Main logic - modules (Background Threads)
    // [PLAYBACK COMMANDS] - Kernel Operation
    PostSimulation = 20,    // Transform sync, interpolation (Main Thread)
    Export = 40             // Network send, recording (Main Thread)
}
```

**Important:** Structural changes (entity create/destroy via command buffer playback) are **kernel operations**, not user-system phases. They occur automatically between Simulation and PostSimulation phases.

**Main Thread Phases:** Input, BeforeSync, PostSimulation, Export  
**Background Thread Phase:** Simulation (modules)

---

### 3. Dependency Attributes

**Systems declare their execution requirements using attributes.**

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
[UpdateBefore(typeof(AnimationSystem))]
public class CombatDecisionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Combat logic
    }
}
```

**Available Attributes:**
- `[UpdateInPhase(phase)]` - Which phase the system runs in
- `[UpdateAfter(typeof(X))]` - Must run after system X
- `[UpdateBefore(typeof(Y))]` - Must run before system Y

**Cross-Phase Dependencies:** Dependencies across phases (e.g., Export system depending on Input system) are implicitly satisfied by kernel's phase execution order. Topological sort only applies **within each phase**.

---

## System Attributes

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class UpdateInPhaseAttribute : Attribute
{
    public SystemPhase Phase { get; }
    
    public UpdateInPhaseAttribute(SystemPhase phase)
    {
        Phase = phase;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class UpdateAfterAttribute : Attribute
{
    public Type SystemType { get; }
    
    public UpdateAfterAttribute(Type systemType)
    {
        SystemType = systemType;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class UpdateBeforeAttribute : Attribute
{
    public Type SystemType { get; }
    
    public UpdateBeforeAttribute(Type systemType)
    {
        SystemType = systemType;
    }
}
```

---

## System Scheduler

**The scheduler builds dependency graphs and performs topological sorting.**

```csharp
public class SystemScheduler
{
    private readonly Dictionary<SystemPhase, List<IComponentSystem>> _systemsByPhase = new();
    private readonly Dictionary<SystemPhase, List<IComponentSystem>> _sortedSystems = new();
    
    // Register a system (called during module initialization)
    public void RegisterSystem<T>(T system) where T : IComponentSystem
    {
        var phase = GetPhaseAttribute(system);
        
        if (!_systemsByPhase.ContainsKey(phase))
            _systemsByPhase[phase] = new List<IComponentSystem>();
        
        _systemsByPhase[phase].Add(system);
    }
    
    // Build execution orders (called at startup after all registrations)
    public void BuildExecutionOrders()
    {
        foreach (var (phase, systems) in _systemsByPhase)
        {
            var graph = BuildDependencyGraph(systems);
            var sorted = TopologicalSort(graph);
            
            if (sorted == null)
                throw new CircularDependencyException($"Circular dependency in {phase}");
            
            _sortedSystems[phase] = sorted;
        }
    }
    
    // Execute all systems in a phase
    public void ExecutePhase(SystemPhase phase, ISimulationView view, float deltaTime)
    {
        if (_sortedSystems.TryGetValue(phase, out var systems))
        {
            foreach (var system in systems)
            {
                system.Execute(view, deltaTime);
            }
        }
    }
    
    private DependencyGraph BuildDependencyGraph(List<IComponentSystem> systems)
    {
        var graph = new DependencyGraph();
        
        // CRITICAL: Create lookup for systems in THIS phase only
        var systemTypesInPhase = new HashSet<Type>(systems.Select(s => s.GetType()));
        
        foreach (var system in systems)
        {
            graph.AddNode(system);
            
            // Extract [UpdateAfter] attributes
            var afterAttrs = system.GetType()
                .GetCustomAttributes(typeof(UpdateAfterAttribute), true)
                .Cast<UpdateAfterAttribute>();
            
            foreach (var attr in afterAttrs)
            {
                // CRITICAL FIX: Only add edge if dependency is in CURRENT phase
                if (systemTypesInPhase.Contains(attr.SystemType))
                {
                    var dependency = systems.First(s => s.GetType() == attr.SystemType);
                    graph.AddEdge(dependency, system); // dependency → system
                }
                // Else: Dependency in another phase (implicitly handled) or missing (ignore)
            }
            
            // Extract [UpdateBefore] attributes
            var beforeAttrs = system.GetType()
                .GetCustomAttributes(typeof(UpdateBeforeAttribute), true)
                .Cast<UpdateBeforeAttribute>();
            
            foreach (var attr in beforeAttrs)
            {
                if (systemTypesInPhase.Contains(attr.SystemType))
                {
                    var dependent = systems.First(s => s.GetType() == attr.SystemType);
                    graph.AddEdge(system, dependent); // system → dependent
                }
            }
        }
        
        return graph;
    }
    
    private List<IComponentSystem> TopologicalSort(DependencyGraph graph)
    {
        // Kahn's algorithm
        var sorted = new List<IComponentSystem>();
        var inDegree = new Dictionary<IComponentSystem, int>();
        var queue = new Queue<IComponentSystem>();
        
        // Calculate in-degrees
        foreach (var node in graph.Nodes)
        {
            inDegree[node] = graph.GetIncomingEdges(node).Count;
            if (inDegree[node] == 0)
                queue.Enqueue(node);
        }
        
        // Process nodes
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(node);
            
            foreach (var neighbor in graph.GetOutgoingEdges(node))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }
        
        // Cycle detection
        return sorted.Count == graph.Nodes.Count ? sorted : null;
    }
    
    // Debug visualization
    public string ToDebugString()
    {
        var sb = new StringBuilder();
        
        foreach (var (phase, systems) in _sortedSystems)
        {
            sb.AppendLine($"PHASE: {phase}");
            
            for (int i = 0; i < systems.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {systems[i].GetType().Name}");
            }
            
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}
```

---

## Module System Registration

**Modules declare their systems during initialization.**

```csharp
public class AIModule : IModule
{
    private readonly SystemScheduler _scheduler = new();
    
    public string Name => "AI";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6; // 10 Hz
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Systems declare their own phases/dependencies via attributes
        registry.RegisterSystem(new TargetSelectionSystem());
        registry.RegisterSystem(new PathfindingSystem());
        registry.RegisterSystem(new CombatDecisionSystem());
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Module orchestrates its Simulation phase systems
        _scheduler.ExecutePhase(SystemPhase.Simulation, view, deltaTime);
    }
}
```

**System examples:**

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class TargetSelectionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Find nearest targets for bots
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class PathfindingSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Calculate paths to targets
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class CombatDecisionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Decide to attack target
    }
}
```

**Scheduler automatically determines execution order:**
1. TargetSelectionSystem
2. PathfindingSystem (or CombatDecisionSystem if no dependency between them)
3. CombatDecisionSystem (or PathfindingSystem)

---

## ModuleHost Kernel Integration

```csharp
public class ModuleHostKernel
{
    private readonly SystemScheduler _globalScheduler = new();
    private readonly List<ModuleDefinition> _modules = new();
    private readonly Dictionary<IModule, float> _accumulatedTime = new();
    
    // Register global systems (main thread phases)
    public void RegisterGlobalSystem<T>(T system) where T : IComponentSystem
    {
        _globalScheduler.RegisterSystem(system);
    }
    
    // Build execution orders at startup
    public void Initialize()
    {
        // Modules register their systems
        foreach (var module in _modules)
        {
            module.RegisterSystems(_globalScheduler);
        }
        
        // Build dependency graphs and sort
        _globalScheduler.BuildExecutionOrders();
        
        // Throws CircularDependencyException if cycles detected
    }
    
    public void Update(float deltaTime)
    {
        // ═══════════ PHASE: Input ═══════════
        _globalScheduler.ExecutePhase(SystemPhase.Input, _liveWorldView, deltaTime);
        
        // ═══════════ PHASE: BeforeSync ═══════════
        _globalScheduler.ExecutePhase(SystemPhase.BeforeSync, _liveWorldView, deltaTime);
        
        // ═══════════ [SYNC A → B] ═══════════
        _doubleBufferProvider.Sync();
        _accumulator.InjectIntoCurrent(_replicaEventBus);
        
        // ═══════════ PHASE: Simulation (Background) ═══════════
        var tasks = new List<Task>();
        
        foreach (var moduleDef in _modules)
        {
            // Accumulate time
            _accumulatedTime[moduleDef.Module] += deltaTime;
            
            // Check if should execute this frame
            if (ShouldExecuteThisFrame(moduleDef))
            {
                // CRITICAL: Pass accumulated time, not frame time
                float moduleDelta = _accumulatedTime[moduleDef.Module];
                
                tasks.Add(Task.Run(() =>
                {
                    var view = moduleDef.Provider.AcquireSnapshot();
                    
                    // Execute module's Simulation phase systems
                    moduleDef.Module.Tick(view, moduleDelta);
                    
                    moduleDef.Provider.ReleaseSnapshot(view);
                }));
                
                // Reset accumulator after execution
                _accumulatedTime[moduleDef.Module] = 0f;
            }
        }
        
        Task.WaitAll(tasks.ToArray());
        
        // ═══════════ [PLAYBACK COMMANDS] ═══════════
        PlaybackCommands();
        
        // ═══════════ PHASE: PostSimulation ═══════════
        _globalScheduler.ExecutePhase(SystemPhase.PostSimulation, _liveWorldView, deltaTime);
        
        // ═══════════ PHASE: Export ═══════════
        _globalScheduler.ExecutePhase(SystemPhase.Export, _liveWorldView, deltaTime);
        
        // ═══════════ [TICK] ═══════════
        _liveWorld.Tick();
    }
}
```

**Key:** Module delta time is **accumulated** until execution. A 10Hz module receives `~0.1s`, not `0.016s`.

---

## Event Handling - Golden Rule

**Systems NEVER touch `FdpEventBus` directly.**

### Reading Events

```csharp
public interface ISimulationView
{
    // Unmanaged events (Tier 1)
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    
    // Managed events (Tier 2)
    IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class;
}
```

**Implementation in EntityRepository.View.cs:**
```csharp
public sealed partial class EntityRepository : ISimulationView
{
    ReadOnlySpan<T> ISimulationView.ConsumeEvents<T>()
    {
        return Bus.Consume<T>();
    }
    
    IReadOnlyList<T> ISimulationView.ConsumeManagedEvents<T>()
    {
        return Bus.ConsumeManaged<T>();
    }
}
```

**Usage:**
```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class CollisionAudioSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Consume unmanaged events
        var collisions = view.ConsumeEvents<CollisionEvent>();
        
        foreach (ref readonly var collision in collisions)
        {
            if (collision.ImpactForce > 10.0f)
                PlaySound("impact_heavy");
        }
        
        // Consume managed events
        var achievements = view.ConsumeManagedEvents<AchievementEvent>();
        
        foreach (var achievement in achievements)
        {
            Console.WriteLine($"Achievement: {achievement.Title}");
        }
    }
}
```

---

### Writing Events

```csharp
public interface IEntityCommandBuffer
{
    // Existing
    Entity CreateEntity();
    void SetComponent<T>(Entity entity, T component) where T : struct;
    void DestroyEntity(Entity entity);
    
    // Event publishing (DEMO-04)
    void PublishEvent<T>(T evt) where T : unmanaged;
    void PublishManagedEvent<T>(T evt) where T : class;
}
```

**Usage:**
```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class CombatLogicSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        var query = view.Query().With<Health>().Build();
        
        foreach (var entity in query)
        {
            ref readonly var health = ref view.GetComponentRO<Health>(entity);
            
            if (health.Value <= 0)
            {
                // Publish event via command buffer
                cmd.PublishEvent(new EntityDeathEvent 
                { 
                    Entity = entity,
                    Timestamp = view.Tick 
                });
                
                cmd.DestroyEntity(entity);
            }
        }
    }
}
```

---

### Event Flow

```
FRAME N:
├─ [Background] Module System:
│  └─ cmd.PublishEvent(new ExplosionEvent { ... })
│     (Stored in command buffer, not published yet)
│
├─ [Main Thread] Phase: Playback
│  └─ cmd.Playback() → event pushed to World A's bus
│
└─ [Main Thread] Synchronous systems (PostSimulation, Export) can see it immediately

FRAME N+1:
├─ [Main Thread] EventAccumulator captures from World A
├─ [Main Thread] DoubleBufferProvider syncs A → B
│  └─ Accumulator flushed to World B's bus
│
└─ [Background] Another Module:
   └─ view.ConsumeEvents<ExplosionEvent>()
      (Sees explosion from previous frame)
```

**Bus Ownership:**
- `EntityRepository` creates its own `FdpEventBus` in constructor
- World A, B, C each have independent buses
- `ModuleHostKernel` synchronizes events between buses via `EventAccumulator`

---

## Example Systems

### Input Phase

```csharp
[UpdateInPhase(SystemPhase.Input)]
public class InputProcessingSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Process keyboard/mouse input
        // Queue input commands to command buffer
    }
}
```

---

### Simulation Phase

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class TargetSelectionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        var bots = view.Query().With<AIState>().With<Position>().Build();
        var players = view.Query().With<Health>().With<Position>().Build();
        
        foreach (var bot in bots)
        {
            var target = FindNearestPlayer(bot, players, view);
            cmd.SetComponent(bot, new AIState { Target = target });
        }
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class CombatDecisionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        var bots = view.Query().With<AIState>().Build();
        
        foreach (var bot in bots)
        {
            ref readonly var aiState = ref view.GetComponentRO<AIState>(bot);
            
            if (aiState.Target != Entity.Null && InRange(bot, aiState.Target, view))
            {
                SpawnProjectile(cmd, bot, aiState.Target);
            }
        }
    }
}
```

---

### PostSimulation Phase (Interpolation)

```csharp
[UpdateInPhase(SystemPhase.PostSimulation)]
public class NetworkSmoothingSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query()
            .With<Position>()
            .With<NetworkTargetPosition>()
            .Build();
        
        var posTable = view.GetComponentTable<Position>();
        var targetTable = view.GetComponentTable<NetworkTargetPosition>();
        
        var entities = query.ToList();
        
        // ═══ FORK-JOIN PARALLELISM ═══
        Parallel.For(0, entities.Count, i =>
        {
            var entity = entities[i];
            
            ref var pos = ref posTable.Get(entity.Index);
            ref readonly var target = ref targetTable.Get(entity.Index);
            
            float lerpFactor = deltaTime / 0.1f;
            pos.X = Lerp(pos.X, target.X, lerpFactor);
            pos.Y = Lerp(pos.Y, target.Y, lerpFactor);
        });
        // ═══ JOIN (implicit - Parallel.For blocks main thread) ═══
    }
}

[UpdateInPhase(SystemPhase.PostSimulation)]
[UpdateAfter(typeof(NetworkSmoothingSystem))]
public class AnimationSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Update animations based on smoothed positions
    }
}
```

---

### Export Phase

```csharp
[UpdateInPhase(SystemPhase.Export)]
public class NetworkSendSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        var query = view.Query().With<NetworkState>().With<Position>().Build();
        
        foreach (var e in query)
        {
            var packet = SerializeEntity(e, view);
            _network.Send(packet);
        }
    }
}

[UpdateInPhase(SystemPhase.Export)]
public class FlightRecorderSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Record frame to disk (uses AsyncRecorder internally)
        _recorder.RecordFrame(view);
    }
}
```

---

## Fork-Join Internal Parallelism

**Systems can parallelize their internal logic safely.**

### Pattern

```csharp
public void Execute(ISimulationView view, float deltaTime)
{
    var entities = view.Query().With<Position>().Build().ToList();
    var posTable = view.GetComponentTable<Position>();
    
    // ═══ FORK ═══
    Parallel.For(0, entities.Count, i =>
    {
        ref var pos = ref posTable.Get(entities[i].Index);
        // Parallel work on position
    });
    // ═══ JOIN (implicit - main thread blocks) ═══
}
```

### Why This Works

1. Main thread **blocks** on `Parallel.For`
2. No other system accesses Position during this time
3. Thread-safe without explicit locks
4. Massive parallelism (thousands of entities)
5. Simple to reason about

---

## Complete Example Setup

```csharp
// Program.cs
var world = new EntityRepository();
var accumulator = new EventAccumulator();
var moduleHost = new ModuleHostKernel(world, accumulator);

// ═══════════ Register Modules ═══════════
moduleHost.RegisterModule(new AIModule());
moduleHost.RegisterModule(new NetworkModule());
moduleHost.RegisterModule(new PhysicsModule());

// ═══════════ Register Global Systems ═══════════
moduleHost.RegisterGlobalSystem(new InputProcessingSystem());       // Input
moduleHost.RegisterGlobalSystem(new NetworkSmoothingSystem());      // PostSimulation
moduleHost.RegisterGlobalSystem(new AnimationSystem());             // PostSimulation
moduleHost.RegisterGlobalSystem(new NetworkSendSystem());           // Export
moduleHost.RegisterGlobalSystem(new FlightRecorderSystem());        // Export

// ═══════════ Build Execution Orders ═══════════
moduleHost.Initialize();
// Builds dependency graphs, performs topological sort
// Throws CircularDependencyException if cycles detected

// Debug output
Console.WriteLine(moduleHost.SystemScheduler.ToDebugString());

// ═══════════ Game Loop ═══════════
while (running)
{
    moduleHost.Update(deltaTime);
}
```

**Execution Flow:**
```
1. [Main] InputProcessingSystem
2. [Main] Sync A → B
3. [Bg  ] AIModule: TargetSelection → PathFinding → Combat
4. [Bg  ] NetworkModule: Receive → Send
5. [Bg  ] PhysicsModule: Projectile queries
6. [Main] Playback commands (structural changes)
7. [Main] NetworkSmoothingSystem (with Fork-Join)
8. [Main] AnimationSystem
9. [Main] NetworkSendSystem
10.[Main] FlightRecorderSystem
11.[Main] Tick()
```

---

## Benefits

### 1. Self-Documenting

```csharp
[UpdateAfter(typeof(PhysicsSystem))]  // ✅ Clear why this runs after physics
```

vs.

```csharp
Priority = 50  // ❌ Why 50? What does it depend on?
```

---

### 2. Compile-Time Safety

Rename system → all `[UpdateAfter(typeof(X))]` cause compile errors → easy to fix

---

### 3. Cycle Detection

```csharp
[UpdateAfter(typeof(SystemB))]
class SystemA { }

[UpdateAfter(typeof(SystemA))]  // Cycle!
class SystemB { }

// Throws CircularDependencyException at startup
```

---

### 4. Reusability

```csharp
// NetworkSmoothingSystem used by multiple modules
networkModule.RegisterSystem(new NetworkSmoothingSystem());
predictionModule.RegisterSystem(new NetworkSmoothingSystem());
```

---

### 5. Testability

```csharp
// Test a single system in isolation
var system = new TargetSelectionSystem();
var mockView = CreateMockView();
system.Execute(mockView, 0.016f);
Assert.Equal(expectedTarget, result);
```

---

### 6. Performance

**Fork-Join pattern:**
- Massive parallelism within systems
- No complex locking
- Safe (main thread blocks)

**Module parallelism:**
- Multiple modules run simultaneously
- GDB/SoD ensures thread safety

---

## Summary

**ModuleHost System Scheduling:**

**Systems:**
- Small, focused units of logic
- Declare phase and dependencies via attributes
- Reusable across modules

**Attributes:**
- `[UpdateInPhase(phase)]`
- `[UpdateAfter(typeof(X))]`
- `[UpdateBefore(typeof(Y))]`

**Scheduling:**
- Topological sort (Kahn's algorithm)
- Deterministic execution order
- Cycle detection at startup
- Cross-phase dependencies handled by kernel

**Phases:**
- Input, BeforeSync (main thread)
- Simulation (background, modules)
- PostSimulation, Export (main thread)

**Module Delta Time:**
- Accumulated until execution
- 10Hz module receives ~0.1s, not 0.016s

**Event Handling:**
- Read: `view.ConsumeEvents<T>()` / `ConsumeManagedEvents<T>()`
- Write: `cmd.PublishEvent<T>()` / `PublishManagedEvent<T>()`
- Never touch FdpEventBus directly

**Parallelism:**
- Modules run in parallel
- Systems within modules run sequentially (dependency order)
- Systems use Fork-Join for internal parallelism

**Safety:**
- No race conditions (GDB, SoD, Fork-Join)
- Compile-time type safety
- Runtime cycle detection

---

**This architecture provides deterministic, high-performance execution with clear dependencies!** ⚡🎯✨

**Architect Approved:** Professional-grade design aligned with FDP hybrid philosophy.
