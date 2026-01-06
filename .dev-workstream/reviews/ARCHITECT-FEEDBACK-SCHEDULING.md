# Architect Feedback - System Scheduling Updates

**Date:** January 6, 2026  
**Purpose:** Incorporate architect review feedback into scheduling design  
**Status:** ⚠️ CRITICAL FIXES REQUIRED

---

## Executive Summary

**Architect Verdict:** ✅ **APPROVED** with critical fixes

**Strengths:**
- Two-level scheduling (global + module-local)
- Attribute-driven topology
- Fork-Join safety
- Phase clarity

**Required Fixes:**
1. ⚠️ Remove `Structural` from `SystemPhase` enum
2. ⚠️ Fix cross-phase dependency handling
3. ⚠️ Ensure `moduleDelta` calculated correctly for slow modules
4. ⚠️ Add `ConsumeManagedEvents` to `ISimulationView`

---

## Critical Fix #1: Remove Structural Phase

### Problem

**Current Design:**
```csharp
public enum SystemPhase
{
    // ...
    Structural = 30,  // ❌ WRONG: Structural is a kernel operation!
}
```

**Issue:** Command buffer playback is a **kernel operation**, not a user-system phase. If users register systems in `Structural`, it's unclear when they run (before/after playback), causing race conditions.

### Solution

**Corrected Enum:**
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

**Key:** Structural changes happen **automatically** between Simulation and PostSimulation. Not a phase users can hook into.

---

## Critical Fix #2: Cross-Phase Dependency Handling

### Problem

**Scenario:**
```csharp
[UpdateInPhase(SystemPhase.Export)]
[UpdateAfter(typeof(InputProcessingSystem))]  // InputProcessingSystem is in Input phase!
public class NetworkSendSystem : IComponentSystem { }
```

**Issue:** Scheduler sorts **per-phase**. It won't find `InputProcessingSystem` in the `Export` list.

**Current Code:** Might crash or silently ignore.

### Solution

**Fixed BuildDependencyGraph:**
```csharp
private DependencyGraph BuildDependencyGraph(List<IComponentSystem> systems)
{
    var graph = new DependencyGraph();
    
    // CRITICAL FIX: Create lookup for fast checking
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
            // ⚠️ CRITICAL FIX: Only add edge if dependency is in CURRENT phase
            if (systemTypesInPhase.Contains(attr.SystemType))
            {
                var dependency = systems.First(s => s.GetType() == attr.SystemType);
                graph.AddEdge(dependency, system);
            }
            // Else: Dependency in another phase (implicitly handled by kernel) or missing (ignore)
        }
        
        // Same logic for [UpdateBefore]
        var beforeAttrs = system.GetType()
            .GetCustomAttributes(typeof(UpdateBeforeAttribute), true)
            .Cast<UpdateBeforeAttribute>();
        
        foreach (var attr in beforeAttrs)
        {
            if (systemTypesInPhase.Contains(attr.SystemType))
            {
                var dependent = systems.First(s => s.GetType() == attr.SystemType);
                graph.AddEdge(system, dependent);
            }
        }
    }
    
    return graph;
}
```

**Explanation:** Cross-phase ordering is guaranteed by `ModuleHostKernel.Update()` phase sequence, not topological sort.

---

## Critical Fix #3: Module Delta Time

### Problem

**Scenario:** `AIModule` runs at 10 Hz (every 6 frames at 60 FPS).

**Question:** What `deltaTime` should it receive?
- Frame time (0.016s)? ❌ WRONG
- Accumulated time (0.1s)? ✅ CORRECT

### Solution

**Updated ModuleHostKernel:**
```csharp
public class ModuleHostKernel
{
    private readonly Dictionary<IModule, float> _accumulatedTime = new();
    
    public void Update(float deltaTime)
    {
        // ... phases 1-2 ...
        
        // PHASE: Simulation (Background)
        var tasks = new List<Task>();
        
        foreach (var moduleDef in _modules)
        {
            // Accumulate time
            _accumulatedTime[moduleDef.Module] += deltaTime;
            
            // Check if should execute this frame
            if (ShouldExecuteThisFrame(moduleDef))
            {
                // ⚠️ CRITICAL: Pass accumulated time, not frame time
                float moduleDelta = _accumulatedTime[moduleDef.Module];
                
                tasks.Add(Task.Run(() =>
                {
                    var view = moduleDef.Provider.AcquireSnapshot();
                    moduleDef.Module.Tick(view, moduleDelta);
                    moduleDef.Provider.ReleaseSnapshot(view);
                }));
                
                // Reset accumulator after execution
                _accumulatedTime[moduleDef.Module] = 0f;
            }
        }
        
        Task.WaitAll(tasks.ToArray());
        
        // ... phases 3-4 ...
    }
}
```

**Example:**
```
Frame 1: AIModule skips (delta accumulated: 0.016s)
Frame 2: AIModule skips (delta accumulated: 0.033s)
...
Frame 6: AIModule executes (delta passed: 0.1s) ✅
```

---

## Critical Fix #4: Managed Event Support

### Problem

**Current `ISimulationView`:**
```csharp
public interface ISimulationView
{
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    // ❌ Missing: Managed events!
}
```

**Issue:** FdpEventBus supports managed events, but ISimulationView doesn't expose them.

### Solution

**Updated Interface:**
```csharp
public interface ISimulationView
{
    // Unmanaged events (Tier 1)
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    
    // NEW: Managed events (Tier 2)
    IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class;
}
```

**Implementation in EntityRepository.View.cs:**
```csharp
public sealed partial class EntityRepository : ISimulationView
{
    // Existing unmanaged implementation
    ReadOnlySpan<T> ISimulationView.ConsumeEvents<T>()
    {
        return Bus.Consume<T>();
    }
    
    // NEW: Managed events implementation
    IReadOnlyList<T> ISimulationView.ConsumeManagedEvents<T>()
    {
        return Bus.ConsumeManaged<T>();
    }
}
```

**Usage in Systems:**
```csharp
public class AchievementSystem : IComponentSystem
{
    public void Execute(ISimulationView view, float deltaTime)
    {
        // Consume managed events
        var achievements = view.ConsumeManagedEvents<AchievementUnlockedEvent>();
        
        foreach (var achievement in achievements)
        {
            Console.WriteLine($"Achievement: {achievement.Title}");
        }
    }
}
```

---

## Enhancement: Debug Visualization

### Recommendation

**Add GraphViz export for dependency graphs:**

```csharp
public class SystemScheduler
{
    public string ToDebugString()
    {
        var sb = new StringBuilder();
        
        foreach (var (phase, systems) in _sortedSystems)
        {
            sb.AppendLine($"PHASE: {phase}");
            
            for (int i = 0; i < systems.Count; i++)
            {
                var system = systems[i];
                var deps = GetDependencies(system);
                
                if (deps.Any())
                    sb.AppendLine($"{i + 1}. {system.GetType().Name} (depends on: {string.Join(", ", deps)})");
                else
                    sb.AppendLine($"{i + 1}. {system.GetType().Name}");
            }
            
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
    
    public string ToGraphViz()
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph SystemSchedule {");
        
        foreach (var (phase, systems) in _sortedSystems)
        {
            sb.AppendLine($"  subgraph cluster_{phase} {{");
            sb.AppendLine($"    label=\"{phase}\";");
            
            foreach (var system in systems)
            {
                var name = system.GetType().Name;
                sb.AppendLine($"    \"{name}\";");
                
                foreach (var dep in GetDependencies(system))
                {
                    sb.AppendLine($"    \"{dep}\" -> \"{name}\";");
                }
            }
            
            sb.AppendLine("  }");
        }
        
        sb.AppendLine("}");
        return sb.ToString();
    }
}
```

**Usage:**
```csharp
moduleHost.Initialize();

// Debug output
Console.WriteLine(moduleHost.SystemScheduler.ToDebugString());

// GraphViz export
File.WriteAllText("schedule.dot", moduleHost.SystemScheduler.ToGraphViz());
// Render with: dot -Tpng schedule.dot -o schedule.png
```

---

## Enhancement: System Groups (Optional)

### Concept

**Allow hierarchical grouping for profiling:**

```csharp
public interface ISystemGroup : IComponentSystem
{
    IReadOnlyList<IComponentSystem> GetSystems();
}

[UpdateInPhase(SystemPhase.Simulation)]
public class CombatGroup : ISystemGroup
{
    private readonly List<IComponentSystem> _systems = new();
    
    public CombatGroup()
    {
        _systems.Add(new TargetingSystem());
        _systems.Add(new FiringSystem());
        _systems.Add(new DamageSystem());
    }
    
    public IReadOnlyList<IComponentSystem> GetSystems() => _systems;
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var stopwatch = Stopwatch.StartNew();
        
        foreach (var system in _systems)
        {
            system.Execute(view, deltaTime);
        }
        
        stopwatch.Stop();
        Console.WriteLine($"CombatGroup: {stopwatch.ElapsedMilliseconds}ms");
    }
}
```

**Benefits:**
- Hierarchical profiling
- Logical grouping
- Easier reasoning about subsystems

---

## Task List Updates

### BATCH-10: System Scheduling Implementation (8 SP)

**Critical Fixes:**
- [ ] TASK-031: Remove Structural from SystemPhase enum (0.5 SP)
- [ ] TASK-032: Fix cross-phase dependency handling in BuildDependencyGraph (1 SP)
- [ ] TASK-033: Implement module delta time accumulation (1 SP)
- [ ] TASK-034: Add ConsumeManagedEvents to ISimulationView (0.5 SP)

**Core Implementation:**
- [ ] TASK-035: Implement SystemScheduler with topological sort (2 SP)
- [ ] TASK-036: Implement attribute parsing (UpdateAfter, UpdateBefore, UpdateInPhase) (1 SP)
- [ ] TASK-037: Integrate SystemScheduler into ModuleHostKernel (1 SP)
- [ ] TASK-038: Add circular dependency detection + tests (1 SP)

**Enhancements:**
- [ ] TASK-039: Add ToDebugString() and ToGraphViz() methods (1 SP) - Optional
- [ ] TASK-040: Implement ISystemGroup support (2 SP) - Optional

**Total:** 8 SP core + 3 SP optional

---

## Event Handling Clarifications

### Golden Rule

**Systems NEVER touch `FdpEventBus` directly.**

**Reading:** Use `ISimulationView.ConsumeEvents<T>()` or `ConsumeManagedEvents<T>()`  
**Writing:** Use `IEntityCommandBuffer.PublishEvent<T>()` or `PublishManagedEvent<T>()`

### Event Flow Diagram

```
FRAME 1:
├─ [Background] Module System:
│  └─ cmd.PublishEvent(new ExplosionEvent { ... })
│     (Stored in command buffer)
│
├─ [Main Thread] Phase 3 Playback:
│  └─ cmd.Playback() → event pushed to World A's bus
│
└─ [Main Thread] Synchronous systems can see it immediately

FRAME 2:
├─ [Main Thread] EventAccumulator captures from World A
├─ [Main Thread] DoubleBufferProvider syncs A → B
│  └─ Accumulator flushed to World B's bus
│
└─ [Background] Another Module:
   └─ view.ConsumeEvents<ExplosionEvent>()
      (Sees explosion from previous frame)
```

### Bus Ownership

**Who creates the bus?**
- `EntityRepository` constructor creates its own `FdpEventBus`
- World A, B, C each have independent buses

**Who populates World B's bus?**
- `ModuleHostKernel` via `EventAccumulator.FlushToReplica(worldB.Bus)`

**Who consumes from the bus?**
- Systems via `view.ConsumeEvents<T>()` → delegates to `worldB.Bus.Consume<T>()`

---

## Summary of Changes

### Critical Fixes (Must Implement)

1. ✅ Remove `Structural` from `SystemPhase`
2. ✅ Fix cross-phase dependency handling
3. ✅ Implement module delta time accumulation
4. ✅ Add `ConsumeManagedEvents` to `ISimulationView`

### Core Implementation (Required)

5. ✅ SystemScheduler with topological sort (Kahn's algorithm)
6. ✅ Attribute parsing
7. ✅ Integration with ModuleHostKernel
8. ✅ Circular dependency detection

### Enhancements (Optional)

9. ⭐ Debug visualization (ToDebugString, ToGraphViz)
10. ⭐ System Groups for hierarchical profiling

---

**Architect approval contingent on implementing critical fixes!** ⚠️🎯
