# System Scheduling Architecture

**Date:** January 6, 2026  
**Purpose:** Design comprehensive system scheduling for ModuleHost  
**Status:** 📋 DESIGN PROPOSAL

---

## Current State: Module-Level Scheduling

**What we have now:**

```csharp
// ModuleHostKernel.Update()
public void Update(float deltaTime)
{
    // PHASE 1: Sync World A → World B (Main Thread)
    _doubleBufferProvider.Sync();
    
    // PHASE 2: Execute Modules (Background Threads)
    var tasks = new List<Task>();
    foreach (var module in _modules)
    {
        if (ShouldExecute(module.UpdateFrequency))
        {
            tasks.Add(Task.Run(() => module.Tick(view, deltaTime)));
        }
    }
    Task.WaitAll(tasks.ToArray());
    
    // PHASE 3: Playback Command Buffers (Main Thread)
    PlaybackCommands();
}
```

**Scheduling:**
- ✅ Modules scheduled by `UpdateFrequency`
- ✅ Modules run in parallel (Phase 2)
- ❌ No control over system execution within modules
- ❌ No explicit ordering beyond module tier

---

## Proposed: Phase-Aware System Scheduling

### Execution Pipeline

```
FRAME N:
├─ PHASE 1: Main Thread Systems (Pre-Sync)
│  ├─ BeforeSync systems (on World A)
│  └─ [Sync A → B]
│
├─ PHASE 2: Module Systems (Background)
│  ├─ Fast tier modules (parallel tasks)
│  │  ├─ Module A: System 1, 2, 3 (sequential)
│  │  └─ Module B: System 1, 2 (sequential)
│  └─ Slow tier modules (parallel tasks)
│     └─ Module C: System 1, 2, 3 (sequential)
│
└─ PHASE 3: Main Thread Systems (Post-Sync)
   ├─ [Playback Command Buffers]
   └─ AfterPlayback systems (on World A)
```

---

## System Phase Enum

```csharp
public enum SystemPhase
{
    // PHASE 1: Main thread, before sync
    BeforeSync = 1,           // Run on World A before sync
    
    // PHASE 2: Background threads (module systems)
    ModuleExecution = 2,      // Run inside modules (default)
    
    // PHASE 3: Main thread, after playback
    AfterPlayback = 3,        // Run on World A after command playback
}
```

---

## System Interface (Extended)

```csharp
public interface IComponentSystem
{
    // Existing
    void Execute(ISimulationView view, float deltaTime);
    
    // NEW: Scheduling metadata
    SystemPhase Phase { get; }
    int Priority { get; }  // Within same phase (lower = earlier)
}
```

---

## Example: Network Smoothing System

```csharp
public class NetworkSmoothingSystem : IComponentSystem
{
    // Run in PHASE 3 (after network updates applied)
    public SystemPhase Phase => SystemPhase.AfterPlayback;
    
    // Run early (before rendering)
    public int Priority => 10;
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Smooth Position toward NetworkTargetPosition
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
```

---

## System Registry (Enhanced)

```csharp
public class SystemRegistry : ISystemRegistry
{
    private readonly Dictionary<SystemPhase, List<IComponentSystem>> _systemsByPhase = new();
    
    public void RegisterSystem<T>(T system) where T : IComponentSystem
    {
        if (!_systemsByPhase.ContainsKey(system.Phase))
            _systemsByPhase[system.Phase] = new List<IComponentSystem>();
        
        _systemsByPhase[system.Phase].Add(system);
        
        // Sort by priority within phase
        _systemsByPhase[system.Phase].Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }
    
    public void ExecutePhase(SystemPhase phase, ISimulationView view, float deltaTime)
    {
        if (_systemsByPhase.TryGetValue(phase, out var systems))
        {
            foreach (var system in systems)
            {
                system.Execute(view, deltaTime);
            }
        }
    }
}
```

---

## ModuleHostKernel (Updated)

```csharp
public class ModuleHostKernel
{
    private readonly SystemRegistry _globalRegistry = new();
    private readonly List<ModuleDefinition> _modules = new();
    
    // NEW: Register global systems (not in modules)
    public void RegisterGlobalSystem<T>(T system) where T : IComponentSystem
    {
        _globalRegistry.RegisterSystem(system);
    }
    
    public void Update(float deltaTime)
    {
        // ═══════════════════════════════════════════════════════════
        // PHASE 1: Main Thread - Pre-Sync Systems
        // ═══════════════════════════════════════════════════════════
        
        // Execute BeforeSync systems on World A
        _globalRegistry.ExecutePhase(SystemPhase.BeforeSync, _liveWorldView, deltaTime);
        
        // Sync World A → World B (GDB)
        _doubleBufferProvider.Sync();
        
        // Prepare event history
        _accumulator.InjectIntoCurrent(_replicaEventBus);
        
        // ═══════════════════════════════════════════════════════════
        // PHASE 2: Background Threads - Module Systems
        // ═══════════════════════════════════════════════════════════
        
        var tasks = new List<Task>();
        
        foreach (var moduleDef in _modules.Where(ShouldExecuteThisFrame))
        {
            tasks.Add(Task.Run(() => 
            {
                var view = moduleDef.Provider.AcquireSnapshot();
                
                // Execute module's ModuleExecution phase systems
                moduleDef.Module.Tick(view, deltaTime);
                
                moduleDef.Provider.ReleaseSnapshot(view);
            }));
        }
        
        Task.WaitAll(tasks.ToArray());
        
        // ═══════════════════════════════════════════════════════════
        // PHASE 3: Main Thread - Command Playback + Post Systems
        // ═══════════════════════════════════════════════════════════
        
        // Playback all command buffers
        PlaybackCommands();
        
        // Execute AfterPlayback systems on World A
        _globalRegistry.ExecutePhase(SystemPhase.AfterPlayback, _liveWorldView, deltaTime);
        
        // Increment tick
        _liveWorld.Tick();
    }
}
```

---

## Module with Phase-Aware Systems

```csharp
public class NetworkModule : IModule
{
    private readonly SystemRegistry _registry = new();
    
    public NetworkModule()
    {
        // Module systems run in PHASE 2 (background)
        _registry.RegisterSystem(new NetworkReceiveSystem());  // ModuleExecution phase
        _registry.RegisterSystem(new NetworkSendSystem());     // ModuleExecution phase
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Execute only ModuleExecution phase systems
        _registry.ExecutePhase(SystemPhase.ModuleExecution, view, deltaTime);
    }
}
```

---

## Global Systems (Main Thread)

```csharp
// In Program.cs
var moduleHost = new ModuleHostKernel(world, accumulator);

// Register modules (Phase 2)
moduleHost.RegisterModule(new NetworkModule());
moduleHost.RegisterModule(new AIModule());

// Register global systems (Phase 1 & 3)
moduleHost.RegisterGlobalSystem(new NetworkSmoothingSystem());  // Phase 3, Priority 10
moduleHost.RegisterGlobalSystem(new ParticleUpdateSystem());    // Phase 3, Priority 20
moduleHost.RegisterGlobalSystem(new AnimationSystem());         // Phase 3, Priority 30
```

---

## Execution Order Control

### Priority-Based Ordering

```csharp
public class RenderingSystem : IComponentSystem
{
    public SystemPhase Phase => SystemPhase.AfterPlayback;
    public int Priority => 100;  // Run LAST (higher priority = later)
}

public class NetworkSmoothingSystem : IComponentSystem
{
    public SystemPhase Phase => SystemPhase.AfterPlayback;
    public int Priority => 10;  // Run FIRST (lower priority = earlier)
}

public class PhysicsSystem : IComponentSystem
{
    public SystemPhase Phase => SystemPhase.AfterPlayback;
    public int Priority => 50;  // Run MIDDLE
}

// Execution order: Smoothing → Physics → Rendering
```

---

### Explicit Dependencies (Advanced)

```csharp
public interface IComponentSystem
{
    SystemPhase Phase { get; }
    int Priority { get; }
    
    // NEW: Dependency management
    Type[] DependsOn { get; }
}

public class MovementSystem : IComponentSystem
{
    public Type[] DependsOn => new[] { typeof(PathfindingSystem) };
    
    // SystemRegistry automatically orders this AFTER PathfindingSystem
}
```

**Implementation:**
```csharp
public class SystemRegistry
{
    public void RegisterSystem<T>(T system) where T : IComponentSystem
    {
        // Build dependency graph
        _dependencyGraph.AddNode(system);
        
        foreach (var depType in system.DependsOn)
        {
            _dependencyGraph.AddEdge(depType, typeof(T));
        }
    }
    
    private List<IComponentSystem> TopologicalSort()
    {
        // Kahn's algorithm for topological sort
        // Returns systems in dependency order
    }
}
```

---

## Parallel Execution Within Modules

### Current: Sequential Execution

```csharp
public void Tick(ISimulationView view, float deltaTime)
{
    // Systems run sequentially
    _registry.ExecutePhase(SystemPhase.ModuleExecution, view, deltaTime);
}
```

---

### Advanced: Parallel System Execution

```csharp
public class SystemRegistry
{
    public void ExecutePhaseParallel(SystemPhase phase, ISimulationView view, float deltaTime)
    {
        var systems = _systemsByPhase[phase];
        
        // Group independent systems
        var groups = AnalyzeDependencies(systems);
        
        foreach (var group in groups)
        {
            // Execute systems in same group in parallel
            Parallel.ForEach(group, system => system.Execute(view, deltaTime));
        }
    }
}
```

**Example:**
```
Group 1 (Parallel):
├─ TargetSelectionSystem  (no dependencies)
└─ ItemCollectionSystem   (no dependencies)

Group 2 (Parallel):
├─ PathfindingSystem      (depends on TargetSelection)
└─ CombatSystem           (depends on TargetSelection)

Group 3 (Sequential):
└─ AnimationSystem        (depends on both above)
```

---

## Complete Example: Game Loop

```csharp
public class GameLoop
{
    private readonly EntityRepository _world;
    private readonly ModuleHostKernel _moduleHost;
    
    public GameLoop()
    {
        _world = new EntityRepository();
        _moduleHost = new ModuleHostKernel(_world, accumulator);
        
        // Register modules (Phase 2 - background)
        _moduleHost.RegisterModule(new NetworkIngressModule());  // Fast
        _moduleHost.RegisterModule(new NetworkEgressModule());   // Fast
        _moduleHost.RegisterModule(new AIModule());              // Slow
        _moduleHost.RegisterModule(new PhysicsModule());         // Fast
        
        // Register global systems (Phase 1 & 3 - main thread)
        _moduleHost.RegisterGlobalSystem(new InputSystem());            // Phase 1, Priority 10
        _moduleHost.RegisterGlobalSystem(new NetworkSmoothingSystem()); // Phase 3, Priority 10
        _moduleHost.RegisterGlobalSystem(new PhysicsIntegrationSystem()); // Phase 3, Priority 20
        _moduleHost.RegisterGlobalSystem(new ParticleSystem());         // Phase 3, Priority 30
        _moduleHost.RegisterGlobalSystem(new AnimationSystem());        // Phase 3, Priority 40
        _moduleHost.RegisterGlobalSystem(new RenderingSystem());        // Phase 3, Priority 100
    }
    
    public void Update(float deltaTime)
    {
        _moduleHost.Update(deltaTime);
    }
}
```

**Execution Flow:**
```
1. [Main] InputSystem (Phase 1, Priority 10)
2. [Main] Sync A → B
3. [Bg  ] NetworkIngress, NetworkEgress, Physics (parallel)
4. [Bg  ] AI (if 10Hz tick)
5. [Main] Playback commands
6. [Main] NetworkSmoothing (Phase 3, Priority 10)
7. [Main] PhysicsIntegration (Phase 3, Priority 20)
8. [Main] Particles (Phase 3, Priority 30)
9. [Main] Animation (Phase 3, Priority 40)
10.[Main] Rendering (Phase 3, Priority 100)
11.[Main] Tick()
```

---

## Benefits of Phase-Aware Scheduling

### 1. Main Thread Control

**Before:** Modules run in background only
**After:** Can run systems on main thread (smoothing, rendering)

### 2. Explicit Ordering

**Before:** Module execution order undefined
**After:** Priority-based ordering within phases

### 3. Separation of Concerns

**Before:** Module mixes network + smoothing
**After:** 
- NetworkModule handles ingress (Phase 2)
- NetworkSmoothingSystem handles interpolation (Phase 3)

### 4. Performance

**Independent systems run in parallel:**
- TargetSelection + ItemCollection (no conflicts)
- Pathfinding + Combat (different component sets)

---

## Migration Path

### Phase 1: Current Design (DEMO-03) ✅

**As-is:**
- Modules with simple `Tick()`
- No system registration
- Works perfectly for current complexity

---

### Phase 2: Add System Support (DEMO-04/05)

**Optional:**
- Modules CAN register systems
- Still works without systems
- Gradual adoption

```csharp
// Old way (still works)
public void Tick(ISimulationView view, float deltaTime)
{
    DoStuff();
}

// New way (if needed)
public void Tick(ISimulationView view, float deltaTime)
{
    _registry.ExecutePhase(SystemPhase.ModuleExecution, view, deltaTime);
}
```

---

### Phase 3: Add Phase Scheduling (DEMO-06)

**When needed:**
- Complex main-thread systems (smoothing)
- Explicit ordering requirements
- Performance critical paths

---

## Summary

**System Scheduling Design:**

**Phases:**
1. **BeforeSync** - Main thread, World A (input, pre-processing)
2. **ModuleExecution** - Background threads, World B (modules)
3. **AfterPlayback** - Main thread, World A (smoothing, rendering)

**Ordering:**
- Priority within phase (lower = earlier)
- Optional dependency graph

**Parallelism:**
- Modules run in parallel (Phase 2)
- Independent systems can run in parallel (advanced)

**Current Demo:**
- ✅ Works without systems
- ✅ Add systems gradually as needed
- ✅ No breaking changes

---

**This design gives complete control over scheduling while maintaining simplicity!** ⚡🎯
