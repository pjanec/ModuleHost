# Integrated System Scheduling Design

**Date:** January 6, 2026  
**Purpose:** Combine attribute-based dependency system with ModuleHost architecture  
**Status:** 🎯 RECOMMENDED DESIGN

---

## Design Comparison

### Your Original Design ✅

**Strengths:**
- ✅ Fine-grained phases (Input, Simulation, PostSimulation, Structural, Export)
- ✅ Attribute-based dependencies (`[UpdateAfter]`, `[UpdateBefore]`)
- ✅ Topological sorting (DAG) - robust, no integer priorities
- ✅ Internal system parallelism (Fork-Join)
- ✅ Background module dispatch

**Phases:**
1. Input - Hardware reads, command buffer
2. Simulation - Physics, logic
3. PostSimulation - Transform sync, dirty flags
4. Structural - Entity creation/destruction
5. Export - Network, recording

---

### Current ModuleHost Design

**Characteristics:**
- 3 phases (BeforeSync, ModuleExecution, AfterPlayback)
- Module-level parallelism (modules run on background threads)
- Priority-based ordering (simpler but less flexible)
- Command buffer playback (Phase 3)

---

## ✅ Integrated Design: Best of Both Worlds

### Unified Phase Architecture

**Map your phases to ModuleHost:**

```csharp
public enum SystemPhase
{
    // ═══ MAIN THREAD PHASES (World A) ═══
    
    Input = 1,              // Your: Phase.Input
                            // Hardware input, early pre-processing
    
    BeforeSync = 2,         // New: Pre-sync preparation
                            // Prepare data before sync
    
    // [SYNC A → B]
    
    // ═══ MODULE PHASES (Background Threads, World B) ═══
    
    Simulation = 10,        // Your: Phase.Simulation
                            // Main logic (AI, physics queries)
    
    // [PLAYBACK COMMANDS]
    
    // ═══ MAIN THREAD PHASES (World A) ═══
    
    PostSimulation = 20,    // Your: Phase.PostSimulation
                            // Transform sync, aggregation
    
    Structural = 30,        // Your: Phase.Structural
                            // Entity lifecycle (already via command buffer)
    
    Export = 40             // Your: Phase.Export
                            // Network send, recording
}
```

**Key insight:** Your phases fit perfectly! Just need ordering.

---

### Attribute-Based Dependencies

**Keep your attribute design - it's superior:**

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(PhysicsQuerySystem))]
[UpdateBefore(typeof(DamageSystem))]
public class CollisionResolutionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // ... logic
    }
}
```

**Why better than priority integers:**
- ✅ Self-documenting (clear why order matters)
- ✅ No magic numbers
- ✅ Compile-time type safety
- ✅ Easy refactoring (rename system = updates all deps)

---

### Topological Sorting (DAG)

**Your approach is production-ready:**

```csharp
public class SystemScheduler
{
    private readonly Dictionary<SystemPhase, List<IComponentSystem>> _systemsByPhase = new();
    private readonly Dictionary<SystemPhase, List<IComponentSystem>> _sortedSystems = new();
    
    public void RegisterSystem<T>(T system) where T : IComponentSystem
    {
        var phase = GetPhaseAttribute(system);
        
        if (!_systemsByPhase.ContainsKey(phase))
            _systemsByPhase[phase] = new List<IComponentSystem>();
        
        _systemsByPhase[phase].Add(system);
    }
    
    public void BuildExecutionOrders()
    {
        foreach (var (phase, systems) in _systemsByPhase)
        {
            // Build dependency graph
            var graph = BuildDependencyGraph(systems);
            
            // Topological sort
            var sorted = TopologicalSort(graph);
            
            // Detect cycles
            if (sorted == null)
                throw new CircularDependencyException($"Circular dependency detected in phase {phase}");
            
            _sortedSystems[phase] = sorted;
        }
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
        if (sorted.Count != graph.Nodes.Count)
            return null; // Cycle detected
        
        return sorted;
    }
}
```

---

### System Attributes

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

### Internal System Parallelism (Fork-Join)

**Your pattern is perfect for hot-path systems:**

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
        
        // Get component tables for parallel access
        var posTable = view.GetComponentTable<Position>();
        var targetTable = view.GetComponentTable<NetworkTargetPosition>();
        
        // Convert query to entity list
        var entities = query.ToList();
        
        // ═══ FORK: Parallel execution ═══
        Parallel.For(0, entities.Count, i =>
        {
            var entity = entities[i];
            
            ref var pos = ref posTable.Get(entity.Index);
            ref readonly var target = ref targetTable.Get(entity.Index);
            
            // Smooth position
            float lerpFactor = deltaTime / 0.1f;
            pos.X = Lerp(pos.X, target.X, lerpFactor);
            pos.Y = Lerp(pos.Y, target.Y, lerpFactor);
        });
        
        // ═══ JOIN: Implicit (Parallel.For blocks) ═══
    }
}
```

**Why this works:**
- Main thread blocks on `Parallel.For`
- No other system accesses Position during this time
- Thread-safe without locks

---

### Module System Registration

**Modules declare their systems with attributes:**

```csharp
public class AIModule : IModule
{
    public void RegisterSystems(ISystemRegistry registry)
    {
        // Systems declare their own phases via attributes
        registry.RegisterSystem(new TargetSelectionSystem());
        registry.RegisterSystem(new PathfindingSystem());
        registry.RegisterSystem(new CombatSystem());
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
public class TargetSelectionSystem : IComponentSystem { ... }

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class PathfindingSystem : IComponentSystem { ... }

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class CombatSystem : IComponentSystem { ... }
```

**Scheduler automatically:**
1. Groups systems by phase
2. Sorts each phase by dependencies
3. Detects cycles at startup

---

## Complete Execution Flow

### Startup (Build Time)

```csharp
var moduleHost = new ModuleHostKernel(world, accumulator);

// Modules register systems
var aiModule = new AIModule();
aiModule.RegisterSystems(moduleHost.SystemRegistry);

var networkModule = new NetworkModule();
networkModule.RegisterSystems(moduleHost.SystemRegistry);

// Build execution orders (topological sort)
moduleHost.SystemRegistry.BuildExecutionOrders();

// Verify no circular dependencies (throws if cycle detected)
```

---

### Runtime (Frame Loop)

```csharp
public void Update(float deltaTime)
{
    // ═══════════════════════════════════════════════════════════
    // PHASE: Input (Main Thread, World A)
    // ═══════════════════════════════════════════════════════════
    _scheduler.ExecutePhase(SystemPhase.Input, _liveWorldView, deltaTime);
    //   Example: InputSystem, CommandBufferProcessingSystem
    
    // ═══════════════════════════════════════════════════════════
    // PHASE: BeforeSync (Main Thread, World A)
    // ═══════════════════════════════════════════════════════════
    _scheduler.ExecutePhase(SystemPhase.BeforeSync, _liveWorldView, deltaTime);
    //   Example: PreSyncValidationSystem
    
    // ═══════════════════════════════════════════════════════════
    // [SYNC A → B]
    // ═══════════════════════════════════════════════════════════
    _doubleBufferProvider.Sync();
    _accumulator.InjectIntoCurrent(_replicaEventBus);
    
    // ═══════════════════════════════════════════════════════════
    // PHASE: Simulation (Background Threads, World B)
    // ═══════════════════════════════════════════════════════════
    var tasks = new List<Task>();
    
    foreach (var moduleDef in _modules)
    {
        if (ShouldExecute(moduleDef))
        {
            tasks.Add(Task.Run(() =>
            {
                var view = moduleDef.Provider.AcquireSnapshot();
                
                // Execute module's Simulation phase systems
                // (Each module has its own sorted system list)
                _scheduler.ExecuteModulePhase(
                    moduleDef.Module, 
                    SystemPhase.Simulation, 
                    view, 
                    deltaTime
                );
                
                moduleDef.Provider.ReleaseSnapshot(view);
            }));
        }
    }
    
    Task.WaitAll(tasks.ToArray());
    
    // ═══════════════════════════════════════════════════════════
    // [PLAYBACK COMMANDS]
    // ═══════════════════════════════════════════════════════════
    PlaybackCommands();
    
    // ═══════════════════════════════════════════════════════════
    // PHASE: PostSimulation (Main Thread, World A)
    // ═══════════════════════════════════════════════════════════
    _scheduler.ExecutePhase(SystemPhase.PostSimulation, _liveWorldView, deltaTime);
    //   Example: NetworkSmoothingSystem (uses Fork-Join internally)
    //   Example: TransformSyncSystem
    
    // ═══════════════════════════════════════════════════════════
    // PHASE: Structural (Main Thread, World A)
    // ═══════════════════════════════════════════════════════════
    // (Already handled by command buffer playback)
    
    // ═══════════════════════════════════════════════════════════
    // PHASE: Export (Main Thread, World A)
    // ═══════════════════════════════════════════════════════════
    _scheduler.ExecutePhase(SystemPhase.Export, _liveWorldView, deltaTime);
    //   Example: NetworkSendSystem
    //   Example: FlightRecorderSystem (direct AsyncRecorder call)
    
    // ═══════════════════════════════════════════════════════════
    // [TICK]
    // ═══════════════════════════════════════════════════════════
    _liveWorld.Tick();
}
```

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
        // Queue input commands
    }
}
```

---

### Simulation Phase (Module Systems)

```csharp
[UpdateInPhase(SystemPhase.Simulation)]
public class TargetSelectionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // AI selects targets
    }
}

[UpdateInPhase(SystemPhase.Simulation)]
[UpdateAfter(typeof(TargetSelectionSystem))]
public class CombatDecisionSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Decides to attack target
    }
}
```

---

### PostSimulation Phase (Transform Sync, Smoothing)

```csharp
[UpdateInPhase(SystemPhase.PostSimulation)]
public class NetworkSmoothingSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // ═══ FORK-JOIN PATTERN ═══
        var query = view.Query().With<Position>().With<NetworkTargetPosition>().Build();
        var entities = query.ToList();
        
        Parallel.For(0, entities.Count, i =>
        {
            // Smooth position (parallel)
        });
        // JOIN implicit
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

### Export Phase (Network Send)

```csharp
[UpdateInPhase(SystemPhase.Export)]
public class NetworkSendSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Serialize and send network updates
    }
}
```

---

## Benefits of Integrated Design

### 1. Declarative Dependencies ✅

**Before (priority ints):**
```csharp
public int Priority => 50;  // Why 50? What does it depend on?
```

**After (attributes):**
```csharp
[UpdateAfter(typeof(PhysicsSystem))]  // Self-documenting!
```

---

### 2. Compile-Time Safety ✅

**Refactor system name:**
```csharp
// Renamed PhysicsSystem → PhysicsQuerySystem
// All [UpdateAfter(typeof(PhysicsSystem))] cause compile errors
// Easy to find and fix!
```

---

### 3. Cycle Detection ✅

```csharp
[UpdateAfter(typeof(SystemB))]
class SystemA : IComponentSystem { }

[UpdateAfter(typeof(SystemA))]  // Cycle!
class SystemB : IComponentSystem { }

// Throws at startup:
// CircularDependencyException: SystemA → SystemB → SystemA
```

---

### 4. Internal Parallelism ✅

**Systems parallelize hot loops:**
```csharp
// Main thread blocks, workers do parallel work
Parallel.For(0, count, i => { /* safe */ });
```

**No complex locking needed!**

---

### 5. Module Isolation ✅

**Modules register systems independently:**
```csharp
// AIModule
registry.RegisterSystem(new AITargetingSystem());

// PhysicsModule
registry.RegisterSystem(new PhysicsQuerySystem());

// Scheduler automatically sorts across ALL systems
```

---

## Migration Strategy

### Phase 1: Current Design (DEMO-03) ✅

**As-is:** Simple modules, no systems

---

### Phase 2: Add System Support (DEMO-04)

**Add:** System registration, attributes

```csharp
public interface IComponentSystem
{
    void Execute(ISimulationView view, float deltaTime);
}

// Modules can optionally register systems
```

---

### Phase 3: Add Scheduler (DEMO-05)

**Add:** Topological sorting, phase execution

```csharp
moduleHost.SystemRegistry.BuildExecutionOrders();
```

---

### Phase 4: Optimize (DEMO-06)

**Add:** Internal parallelism (Fork-Join)

```csharp
Parallel.For(...);  // Within systems
```

---

## Summary

**Your original design is excellent! ✅**

**Key strengths:**
1. ✅ Attribute-based dependencies (better than priorities)
2. ✅ Topological sorting (robust, detects cycles)
3. ✅ Fork-Join parallelism (safe, simple)
4. ✅ Fine-grained phases (Input, Simulation, Post, Structural, Export)

**Integration with ModuleHost:**
- ✅ Your phases map perfectly to extended phase system
- ✅ Attributes work with module registration
- ✅ Scheduler fits in ModuleHostKernel
- ✅ Background dispatch already matches

**Recommendation:**
- Use your attribute-based dependency system
- Keep Fork-Join internal parallelism
- Integrate with current ModuleHost phases
- Add gradually (DEMO-04+)

---

**This integrated design is production-ready and flexible!** ⚡🎯✨
