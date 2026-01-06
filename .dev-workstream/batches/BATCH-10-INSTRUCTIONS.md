# BATCH-10 Instructions: System Scheduling Implementation

**Assigned:** Developer  
**Date:** January 6, 2026  
**Estimated:** 11 SP (8 core + 3 optional now included)  
**Status:** 📋 READY FOR IMPLEMENTATION

---

## Overview

Implement the complete system scheduling architecture with attribute-based dependency resolution, topological sorting, and hierarchical profiling support. This batch includes critical architect feedback and prepares ModuleHost for complex multi-system modules.

**Reference:** `docs/SYSTEM-SCHEDULING-FINAL.md`

---

## Part 1: Critical Fixes (3 SP)

### TASK-031: Remove Structural Phase (0.5 SP)

**File:** `Fdp.Kernel/Phase.cs`

**Current enum** (if it exists):
```csharp
public enum SystemPhase
{
    Input = 1,
    BeforeSync = 2,
    Simulation = 10,
    PostSimulation = 20,
    Structural = 30,  // ❌ REMOVE THIS
    Export = 40
}
```

**Action:** Remove `Structural = 30` from enum if present. Structural changes are kernel operations (command buffer playback), not user-system phases.

**Expected:** Enum only has: Input, BeforeSync, Simulation, PostSimulation, Export

---

### TASK-032: Add ConsumeManagedEvents to ISimulationView (0.5 SP)

**File:** Create or update `ModuleHost.Core/Abstractions/ISimulationView.cs`

**Current interface** (based on EntityRepository.View.cs):
```csharp
public interface ISimulationView
{
    uint Tick { get; }
    float Time { get; }
    
    IEntityCommandBuffer GetCommandBuffer();
    ref readonly T GetComponentRO<T>(Entity e) where T : struct;
    T GetManagedComponentRO<T>(Entity e) where T : class;
    bool IsAlive(Entity e);
    bool HasComponent<T>(Entity e) where T : struct;
    bool HasManagedComponent<T>(Entity e) where T : class;
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    QueryBuilder Query();
}
```

**Add:**
```csharp
// NEW: Managed event consumption
IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class;
```

**Update implementation in:** `Fdp.Kernel/EntityRepository.View.cs`

```csharp
IReadOnlyList<T> ISimulationView.ConsumeManagedEvents<T>()
{
    return Bus.ConsumeManaged<T>();
}
```

---

### TASK-033: Module Delta Time Accumulation (1 SP)

**File:** `ModuleHost.Core/ModuleHostKernel.cs`

**Problem:** Slow tier modules (10Hz) currently receive frame deltaTime (0.016s) instead of accumulated time (0.1s).

**Add field:**
```csharp
private readonly Dictionary<IModule, float> _accumulatedTime = new();
```

**Update `Update()` method** (around line 61-110):

**Current:**
```csharp
foreach (var entry in _modules)
{
    if (ShouldRunThisFrame(entry))
    {
        var view = entry.Provider.AcquireView();
        float moduleDelta = (entry.FramesSinceLastRun + 1) * deltaTime;  // ❌ Wrong calculation
        
        var task = Task.Run(() => {
            entry.Module.Tick(view, moduleDelta);
        });
    }
}
```

**Replace with:**
```csharp
foreach (var entry in _modules)
{
    // ⚠️ CRITICAL: Accumulate time for ALL modules
    if (!_accumulatedTime.ContainsKey(entry.Module))
        _accumulatedTime[entry.Module] = 0f;
    
    _accumulatedTime[entry.Module] += deltaTime;
    
    if (ShouldRunThisFrame(entry))
    {
        var view = entry.Provider.AcquireView();
        entry.LastView = view;
        
        // ⚠️ CRITICAL: Pass accumulated time, not frame time
        float moduleDelta = _accumulatedTime[entry.Module];
        
        var task = Task.Run(() => {
            try
            {
                entry.Module.Tick(view, moduleDelta);
                System.Threading.Interlocked.Increment(ref entry.ExecutionCount);
            }
            finally
            {
                entry.Provider.ReleaseView(view);
            }
        });
        
        tasks.Add(task);
        
        // ⚠️ CRITICAL: Reset accumulator after execution
        _accumulatedTime[entry.Module] = 0f;
    }
}
```

**Verification:** 10Hz module logs should show `moduleDelta ~= 0.1s`, not `0.016s`.

---

### TASK-034: Fix Cross-Phase Dependency Handling (1 SP)

**Note:** This will be implemented in SystemScheduler (TASK-035), but documented here for awareness.

**Requirement:** When building dependency graph, only add edges for dependencies **within the current phase**. Cross-phase dependencies are implicitly satisfied by kernel phase execution order.

**Example:**
```csharp
// This is OK - scheduler ignores cross-phase dependencies
[UpdateInPhase(SystemPhase.Export)]
[UpdateAfter(typeof(InputSystem))]  // InputSystem is in Input phase - ignored
public class NetworkSendSystem : IComponentSystem { }
```

**Implementation details in TASK-035.**

---

## Part 2: System Attributes (1 SP)

### TASK-035: Define System Attributes (1 SP)

**File:** Create `ModuleHost.Core/Abstractions/SystemAttributes.cs`

```csharp
using System;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Specifies which phase a system executes in.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class UpdateInPhaseAttribute : Attribute
    {
        public SystemPhase Phase { get; }
        
        public UpdateInPhaseAttribute(SystemPhase phase)
        {
            Phase = phase;
        }
    }
    
    /// <summary>
    /// Specifies that this system must run after another system within the same phase.
    /// If the dependency is in a different phase, this attribute is ignored.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class UpdateAfterAttribute : Attribute
    {
        public Type SystemType { get; }
        
        public UpdateAfterAttribute(Type systemType)
        {
            if (systemType == null)
                throw new ArgumentNullException(nameof(systemType));
            
            if (!typeof(IComponentSystem).IsAssignableFrom(systemType))
                throw new ArgumentException($"{systemType} must implement IComponentSystem");
            
            SystemType = systemType;
        }
    }
    
    /// <summary>
    /// Specifies that this system must run before another system within the same phase.
    /// If the dependent is in a different phase, this attribute is ignored.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public class UpdateBeforeAttribute : Attribute
    {
        public Type SystemType { get; }
        
        public UpdateBeforeAttribute(Type systemType)
        {
            if (systemType == null)
                throw new ArgumentNullException(nameof(systemType));
            
            if (!typeof(IComponentSystem).IsAssignableFrom(systemType))
                throw new ArgumentException($"{systemType} must implement IComponentSystem");
            
            SystemType = systemType;
        }
    }
}
```

**Also define:**

**File:** Create `ModuleHost.Core/Abstractions/SystemPhase.cs`

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Execution phases for systems within the simulation loop.
    /// </summary>
    public enum SystemPhase
    {
        /// <summary>
        /// Input phase: Hardware input, early processing (Main Thread).
        /// </summary>
        Input = 1,
        
        /// <summary>
        /// BeforeSync phase: Pre-sync preparation (Main Thread).
        /// </summary>
        BeforeSync = 2,
        
        // [SYNC A → B] - Kernel Operation
        
        /// <summary>
        /// Simulation phase: Main logic - modules (Background Threads).
        /// </summary>
        Simulation = 10,
        
        // [PLAYBACK COMMANDS] - Kernel Operation
        
        /// <summary>
        /// PostSimulation phase: Transform sync, interpolation (Main Thread).
        /// </summary>
        PostSimulation = 20,
        
        /// <summary>
        /// Export phase: Network send, recording (Main Thread).
        /// </summary>
        Export = 40
    }
}
```

---

## Part 3: Core System Interfaces (1 SP)

### TASK-036: Define Core System Interfaces (1 SP)

**File:** Create `ModuleHost.Core/Abstractions/IComponentSystem.cs`

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// A focused unit of logic that operates on components.
    /// Systems execute in a deterministic order based on declared dependencies.
    /// </summary>
    public interface IComponentSystem
    {
        /// <summary>
        /// Execute system logic.
        /// Called by scheduler in dependency order.
        /// </summary>
        /// <param name="view">Read-only simulation view</param>
        /// <param name="deltaTime">Time since last execution (seconds)</param>
        void Execute(ISimulationView view, float deltaTime);
    }
}
```

**File:** Create `ModuleHost.Core/Abstractions/ISystemGroup.cs`

```csharp
using System.Collections.Generic;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// A group of related systems for hierarchical organization and profiling.
    /// </summary>
    public interface ISystemGroup : IComponentSystem
    {
        /// <summary>
        /// Name of this system group (for profiling/debugging).
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// Systems contained in this group.
        /// </summary>
        IReadOnlyList<IComponentSystem> GetSystems();
    }
}
```

**File:** Create `ModuleHost.Core/Abstractions/ISystemRegistry.cs`

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Registry for system registration and scheduling.
    /// </summary>
    public interface ISystemRegistry
    {
        /// <summary>
        /// Register a system for execution.
        /// System's phase and dependencies are determined by attributes.
        /// </summary>
        void RegisterSystem<T>(T system) where T : IComponentSystem;
    }
}
```

**Update:** `ModuleHost.Core/Abstractions/IModule.cs`

**Add method:**
```csharp
/// <summary>
/// Register systems for this module.
/// Called once during initialization.
/// </summary>
/// <param name="registry">System registry</param>
void RegisterSystems(ISystemRegistry registry)
{
    // Default: no systems (modules can override)
}
```

Make it optional with default implementation if C# version supports, otherwise leave as optional virtual method.

---

## Part 4: Dependency Graph (2 SP)

### TASK-037: Implement Dependency Graph (2 SP)

**File:** Create `ModuleHost.Core/Scheduling/DependencyGraph.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Scheduling
{
    /// <summary>
    /// Directed graph for system dependencies.
    /// Used for topological sorting to determine execution order.
    /// </summary>
    internal class DependencyGraph
    {
        private readonly HashSet<IComponentSystem> _nodes = new();
        private readonly Dictionary<IComponentSystem, HashSet<IComponentSystem>> _edges = new();
        
        public IReadOnlyCollection<IComponentSystem> Nodes => _nodes;
        
        public void AddNode(IComponentSystem system)
        {
            _nodes.Add(system);
            if (!_edges.ContainsKey(system))
                _edges[system] = new HashSet<IComponentSystem>();
        }
        
        /// <summary>
        /// Add edge: from → to (from must execute before to).
        /// </summary>
        public void AddEdge(IComponentSystem from, IComponentSystem to)
        {
            if (!_nodes.Contains(from))
                throw new ArgumentException($"System {from.GetType().Name} not in graph");
            if (!_nodes.Contains(to))
                throw new ArgumentException($"System {to.GetType().Name} not in graph");
            
            _edges[from].Add(to);
        }
        
        /// <summary>
        /// Get all systems that depend on this system (outgoing edges).
        /// </summary>
        public IEnumerable<IComponentSystem> GetOutgoingEdges(IComponentSystem system)
        {
            return _edges.TryGetValue(system, out var deps) ? deps : Enumerable.Empty<IComponentSystem>();
        }
        
        /// <summary>
        /// Get all systems this system depends on (incoming edges).
        /// </summary>
        public IEnumerable<IComponentSystem> GetIncomingEdges(IComponentSystem system)
        {
            return _edges.Where(kvp => kvp.Value.Contains(system))
                         .Select(kvp => kvp.Key);
        }
        
        /// <summary>
        /// Get count of incoming edges (dependencies).
        /// </summary>
        public int GetInDegree(IComponentSystem system)
        {
            return GetIncomingEdges(system).Count();
        }
    }
}
```

---

## Part 5: System Scheduler (3 SP)

### TASK-038: Implement SystemScheduler (3 SP)

**File:** Create `ModuleHost.Core/Scheduling/SystemScheduler.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Scheduling
{
    /// <summary>
    /// Schedules system execution using topological sorting of dependencies.
    /// Systems execute in deterministic order based on [UpdateAfter]/[UpdateBefore] attributes.
    /// </summary>
    public class SystemScheduler : ISystemRegistry
    {
        private readonly Dictionary<SystemPhase, List<IComponentSystem>> _systemsByPhase = new();
        private readonly Dictionary<SystemPhase, List<IComponentSystem>> _sortedSystems = new();
        private readonly Dictionary<IComponentSystem, SystemPhase> _systemPhases = new();
        
        // Profiling data
        private readonly Dictionary<IComponentSystem, SystemProfileData> _profileData = new();
        
        /// <summary>
        /// Register a system for execution.
        /// System's phase is determined by [UpdateInPhase] attribute.
        /// </summary>
        public void RegisterSystem<T>(T system) where T : IComponentSystem
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));
            
            var phase = GetPhaseAttribute(system);
            
            if (!_systemsByPhase.ContainsKey(phase))
                _systemsByPhase[phase] = new List<IComponentSystem>();
            
            _systemsByPhase[phase].Add(system);
            _systemPhases[system] = phase;
            _profileData[system] = new SystemProfileData(system.GetType().Name);
        }
        
        /// <summary>
        /// Build execution orders for all phases.
        /// Must be called after all systems registered, before execution.
        /// </summary>
        public void BuildExecutionOrders()
        {
            foreach (var (phase, systems) in _systemsByPhase)
            {
                var graph = BuildDependencyGraph(systems);
                var sorted = TopologicalSort(graph);
                
                if (sorted == null)
                {
                    throw new CircularDependencyException(
                        $"Circular dependency detected in phase {phase}. " +
                        $"Systems: {string.Join(", ", systems.Select(s => s.GetType().Name))}");
                }
                
                _sortedSystems[phase] = sorted;
            }
        }
        
        /// <summary>
        /// Execute all systems in a phase.
        /// </summary>
        public void ExecutePhase(SystemPhase phase, ISimulationView view, float deltaTime)
        {
            if (!_sortedSystems.TryGetValue(phase, out var systems))
                return;
            
            foreach (var system in systems)
            {
                ExecuteSystem(system, view, deltaTime);
            }
        }
        
        private void ExecuteSystem(IComponentSystem system, ISimulationView view, float deltaTime)
        {
            var profile = _profileData[system];
            var sw = Stopwatch.StartNew();
            
            try
            {
                // Check if system is a group
                if (system is ISystemGroup group)
                {
                    ExecuteGroup(group, view, deltaTime);
                }
                else
                {
                    system.Execute(view, deltaTime);
                }
                
                sw.Stop();
                profile.RecordExecution(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                profile.RecordError(ex);
                throw new SystemExecutionException(
                    $"System {system.GetType().Name} failed", ex);
            }
        }
        
        private void ExecuteGroup(ISystemGroup group, ISimulationView view, float deltaTime)
        {
            var groupProfile = _profileData[group];
            var groupSw = Stopwatch.StartNew();
            
            foreach (var system in group.GetSystems())
            {
                // Ensure nested systems are profiled
                if (!_profileData.ContainsKey(system))
                    _profileData[system] = new SystemProfileData(system.GetType().Name);
                
                ExecuteSystem(system, view, deltaTime);
            }
            
            groupSw.Stop();
            groupProfile.RecordExecution(groupSw.Elapsed.TotalMilliseconds);
        }
        
        private SystemPhase GetPhaseAttribute(IComponentSystem system)
        {
            var attr = (UpdateInPhaseAttribute?)Attribute.GetCustomAttribute(
                system.GetType(), typeof(UpdateInPhaseAttribute), inherit: true);
            
            if (attr == null)
            {
                throw new InvalidOperationException(
                    $"System {system.GetType().Name} must have [UpdateInPhase] attribute");
            }
            
            return attr.Phase;
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
                var afterAttrs = Attribute.GetCustomAttributes(
                    system.GetType(), typeof(UpdateAfterAttribute), inherit: true)
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
                var beforeAttrs = Attribute.GetCustomAttributes(
                    system.GetType(), typeof(UpdateBeforeAttribute), inherit: true)
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
        
        private List<IComponentSystem>? TopologicalSort(DependencyGraph graph)
        {
            // Kahn's algorithm
            var sorted = new List<IComponentSystem>();
            var inDegree = new Dictionary<IComponentSystem, int>();
            var queue = new Queue<IComponentSystem>();
            
            // Calculate in-degrees
            foreach (var node in graph.Nodes)
            {
                int degree = graph.GetIncomingEdges(node).Count();
                inDegree[node] = degree;
                
                if (degree == 0)
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
            if (sorted.Count != graph.Nodes.Count)
                return null; // Cycle detected
            
            return sorted;
        }
        
        /// <summary>
        /// Get profiling data for a specific system.
        /// </summary>
        public SystemProfileData? GetProfileData(IComponentSystem system)
        {
            return _profileData.TryGetValue(system, out var data) ? data : null;
        }
        
        /// <summary>
        /// Get profiling data for a specific system by type.
        /// </summary>
        public SystemProfileData? GetProfileData<T>() where T : IComponentSystem
        {
            var system = _systemsByPhase.Values
                .SelectMany(list => list)
                .FirstOrDefault(s => s is T);
            
            return system != null ? GetProfileData(system) : null;
        }
        
        /// <summary>
        /// Get all profiling data grouped by phase.
        /// </summary>
        public Dictionary<SystemPhase, List<SystemProfileData>> GetAllProfileData()
        {
            var result = new Dictionary<SystemPhase, List<SystemProfileData>>();
            
            foreach (var (phase, systems) in _sortedSystems)
            {
                result[phase] = systems.Select(s => _profileData[s]).ToList();
            }
            
            return result;
        }
        
        /// <summary>
        /// Debug output of execution order.
        /// </summary>
        public string ToDebugString()
        {
            var sb = new StringBuilder();
            
            foreach (var (phase, systems) in _sortedSystems.OrderBy(kvp => (int)kvp.Key))
            {
                sb.AppendLine($"PHASE: {phase}");
                
                for (int i = 0; i < systems.Count; i++)
                {
                    var system = systems[i];
                    var profile = _profileData[system];
                    
                    sb.AppendLine($"  {i + 1}. {system.GetType().Name}");
                    
                    if (profile.ExecutionCount > 0)
                    {
                        sb.AppendLine($"     Avg: {profile.AverageMs:F2}ms | " +
                                    $"Max: {profile.MaxMs:F2}ms | " +
                                    $"Runs: {profile.ExecutionCount}");
                    }
                    
                    // Show nested systems for groups
                    if (system is ISystemGroup group)
                    {
                        foreach (var nested in group.GetSystems())
                        {
                            if (_profileData.TryGetValue(nested, out var nestedProfile))
                            {
                                sb.AppendLine($"       → {nested.GetType().Name} " +
                                            $"(Avg: {nestedProfile.AverageMs:F2}ms)");
                            }
                        }
                    }
                }
                
                sb.AppendLine();
            }
            
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// Exception thrown when circular dependencies detected.
    /// </summary>
    public class CircularDependencyException : Exception
    {
        public CircularDependencyException(string message) : base(message) { }
    }
    
    /// <summary>
    /// Exception thrown when system execution fails.
    /// </summary>
    public class SystemExecutionException : Exception
    {
        public SystemExecutionException(string message, Exception inner) 
            : base(message, inner) { }
    }
}
```

---

## Part 6: Profiling Support (1 SP)

### TASK-039: System Profiling Data (1 SP)

**File:** Create `ModuleHost.Core/Scheduling/SystemProfileData.cs`

```csharp
using System;
using System.Collections.Generic;

namespace ModuleHost.Core.Scheduling
{
    /// <summary>
    /// Performance profiling data for a system.
    /// </summary>
    public class SystemProfileData
    {
        public string SystemName { get; }
        
        public long ExecutionCount { get; private set; }
        public double TotalMs { get; private set; }
        public double AverageMs => ExecutionCount > 0 ? TotalMs / ExecutionCount : 0;
        public double MinMs { get; private set; } = double.MaxValue;
        public double MaxMs { get; private set; }
        public double LastMs { get; private set; }
        
        public int ErrorCount { get; private set; }
        public Exception? LastError { get; private set; }
        
        private readonly Queue<double> _recentExecutions = new();
        private const int MaxRecentSamples = 60; // Last 60 executions
        
        public SystemProfileData(string systemName)
        {
            SystemName = systemName;
        }
        
        public void RecordExecution(double milliseconds)
        {
            ExecutionCount++;
            TotalMs += milliseconds;
            LastMs = milliseconds;
            
            if (milliseconds < MinMs)
                MinMs = milliseconds;
            if (milliseconds > MaxMs)
                MaxMs = milliseconds;
            
            _recentExecutions.Enqueue(milliseconds);
            if (_recentExecutions.Count > MaxRecentSamples)
                _recentExecutions.Dequeue();
        }
        
        public void RecordError(Exception ex)
        {
            ErrorCount++;
            LastError = ex;
        }
        
        /// <summary>
        /// Get average of recent executions (last 60).
        /// </summary>
        public double GetRecentAverageMs()
        {
            if (_recentExecutions.Count == 0)
                return 0;
            
            double sum = 0;
            foreach (var ms in _recentExecutions)
                sum += ms;
            
            return sum / _recentExecutions.Count;
        }
        
        /// <summary>
        /// Reset all statistics.
        /// </summary>
        public void Reset()
        {
            ExecutionCount = 0;
            TotalMs = 0;
            MinMs = double.MaxValue;
            MaxMs = 0;
            LastMs = 0;
            ErrorCount = 0;
            LastError = null;
            _recentExecutions.Clear();
        }
    }
}
```

---

## Part 7: ModuleHost Integration (1 SP)

### TASK-040: Integrate SystemScheduler into ModuleHostKernel (1 SP)

**File:** `ModuleHost.Core/ModuleHostKernel.cs`

**Add fields:**
```csharp
private readonly SystemScheduler _globalScheduler = new();
private bool _initialized = false;
```

**Add methods:**
```csharp
/// <summary>
/// Register a global system (runs on main thread).
/// </summary>
public void RegisterGlobalSystem<T>(T system) where T : IComponentSystem
{
    if (_initialized)
        throw new InvalidOperationException("Cannot register systems after Initialize() called");
    
    _globalScheduler.RegisterSystem(system);
}

/// <summary>
/// Access to system scheduler for profiling/debugging.
/// </summary>
public SystemScheduler SystemScheduler => _globalScheduler;

/// <summary>
/// Initialize kernel: build execution orders, validate dependencies.
/// Must be called after all modules/systems registered, before Update().
/// </summary>
public void Initialize()
{
    if (_initialized)
        throw new InvalidOperationException("Already initialized");
    
    // Modules register their systems
    foreach (var entry in _modules)
    {
        entry.Module.RegisterSystems(_globalScheduler);
    }
    
    // Build dependency graphs and sort
    _globalScheduler.BuildExecutionOrders();
    
    // Throws CircularDependencyException if cycles detected
    
    _initialized = true;
}
```

**Update `Update()` method:**

**Add at start:**
```csharp
public void Update(float deltaTime)
{
    if (!_initialized)
        throw new InvalidOperationException("Must call Initialize() before Update()");
    
    // ═══════════ PHASE: Input ═══════════
    _globalScheduler.ExecutePhase(SystemPhase.Input, _liveWorld, deltaTime);
    
    // ═══════════ PHASE: BeforeSync ═══════════
    _globalScheduler.ExecutePhase(SystemPhase.BeforeSync, _liveWorld, deltaTime);
    
    // ... existing sync and module execution ...
```

**Add after command buffer playback:**
```csharp
    // ═══════════ PHASE: PostSimulation ═══════════
    _globalScheduler.ExecutePhase(SystemPhase.PostSimulation, _liveWorld, deltaTime);
    
    // ═══════════ PHASE: Export ═══════════
    _globalScheduler.ExecutePhase(SystemPhase.Export, _liveWorld, deltaTime);
    
    // ... existing tick increment ...
}
```

---

## Part 8: Tests (1 SP)

### TASK-041: System Scheduling Tests (1 SP)

**File:** Create `ModuleHost.Tests/SystemSchedulerTests.cs`

```csharp
using System;
using Xunit;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Scheduling;

namespace ModuleHost.Tests
{
    public class SystemSchedulerTests
    {
        [Fact]
        public void TopologicalSort_SimpleChain_CorrectOrder()
        {
            var scheduler = new SystemScheduler();
            
            var systemA = new TestSystemA();
            var systemB = new TestSystemB();
            var systemC = new TestSystemC();
            
            scheduler.RegisterSystem(systemA);
            scheduler.RegisterSystem(systemB);
            scheduler.RegisterSystem(systemC);
            
            scheduler.BuildExecutionOrders();
            
            // Expected order: A → B → C
            // (Verify by checking execution in mock view)
        }
        
        [Fact]
        public void CircularDependency_ThrowsException()
        {
            var scheduler = new SystemScheduler();
            
            scheduler.RegisterSystem(new CircularSystemA());
            scheduler.RegisterSystem(new CircularSystemB());
            
            Assert.Throws<CircularDependencyException>(() => 
                scheduler.BuildExecutionOrders());
        }
        
        [Fact]
        public void CrossPhaseDependency_Ignored()
        {
            var scheduler = new SystemScheduler();
            
            scheduler.RegisterSystem(new InputSystem());
            scheduler.RegisterSystem(new ExportSystemDependingOnInput());
            
            // Should not throw - cross-phase deps ignored
            scheduler.BuildExecutionOrders();
        }
        
        [Fact]
        public void SystemGroup_ExecutesNestedSystems()
        {
            var scheduler = new SystemScheduler();
            var group = new TestSystemGroup();
            
            scheduler.RegisterSystem(group);
            scheduler.BuildExecutionOrders();
            
            var mockView = new MockSimulationView();
            scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
            
            // Verify all nested systems executed
            Assert.True(group.WasExecuted);
        }
        
        [Fact]
        public void Profiling_TracksExecutionTime()
        {
            var scheduler = new SystemScheduler();
            var system = new TestSystemA();
            
            scheduler.RegisterSystem(system);
            scheduler.BuildExecutionOrders();
            
            var mockView = new MockSimulationView();
            scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
            
            var profile = scheduler.GetProfileData(system);
            Assert.NotNull(profile);
            Assert.Equal(1, profile.ExecutionCount);
            Assert.True(profile.LastMs >= 0);
        }
    }
    
    // Test systems
    [UpdateInPhase(SystemPhase.Simulation)]
    class TestSystemA : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Simulation)]
    [UpdateAfter(typeof(TestSystemA))]
    class TestSystemB : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Simulation)]
    [UpdateAfter(typeof(TestSystemB))]
    class TestSystemC : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Simulation)]
    [UpdateAfter(typeof(CircularSystemB))]
    class CircularSystemA : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Simulation)]
    [UpdateAfter(typeof(CircularSystemA))]
    class CircularSystemB : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Input)]
    class InputSystem : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Export)]
    [UpdateAfter(typeof(InputSystem))] // Cross-phase - should be ignored
    class ExportSystemDependingOnInput : IComponentSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhase(SystemPhase.Simulation)]
    class TestSystemGroup : ISystemGroup
    {
        public string Name => "TestGroup";
        public bool WasExecuted { get; private set; }
        
        private readonly List<IComponentSystem> _systems = new()
        {
            new TestSystemA(),
            new TestSystemB()
        };
        
        public IReadOnlyList<IComponentSystem> GetSystems() => _systems;
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            WasExecuted = true;
            foreach (var system in _systems)
                system.Execute(view, deltaTime);
        }
    }
}
```

---

## Deliverables

**Files to create:**
1. `ModuleHost.Core/Abstractions/SystemPhase.cs`
2. `ModuleHost.Core/Abstractions/SystemAttributes.cs`
3. `ModuleHost.Core/Abstractions/IComponentSystem.cs`
4. `ModuleHost.Core/Abstractions/ISystemGroup.cs`
5. `ModuleHost.Core/Abstractions/ISystemRegistry.cs`
6. `ModuleHost.Core/Scheduling/DependencyGraph.cs`
7. `ModuleHost.Core/Scheduling/SystemScheduler.cs`
8. `ModuleHost.Core/Scheduling/SystemProfileData.cs`
9. `ModuleHost.Tests/SystemSchedulerTests.cs`

**Files to modify:**
1. `ModuleHost.Core/Abstractions/IModule.cs` (add RegisterSystems)
2. `ModuleHost.Core/Abstractions/ISimulationView.cs` (add ConsumeManagedEvents)
3. `ModuleHost.Core/ModuleHostKernel.cs` (add scheduler, Initialize, phases)
4. `Fdp.Kernel/EntityRepository.View.cs` (implement ConsumeManagedEvents)
5. `Fdp.Kernel/Phase.cs` (remove Structural if present)

---

## Verification

**1. Build:**
```powershell
dotnet build ModuleHost.Core/ModuleHost.Core.csproj --nologo
```
Expected: 0 errors, 0 warnings

**2. Tests:**
```powershell
dotnet test ModuleHost.Tests/ModuleHost.Tests.csproj --nologo --filter "FullyQualifiedName~SystemSchedulerTests"
```
Expected: All tests pass

**3. Demo integration:**
- Update BattleRoyale to call `moduleHost.Initialize()` before game loop
- Verify profiling output with `moduleHost.SystemScheduler.ToDebugString()`

---

## Notes

- Cross-phase dependencies are **ignored** by topological sort (kernel guarantees phase order)
- Module delta time is **accumulated** until execution (10Hz module gets ~0.1s, not 0.016s)
- SystemGroups are **automatically profiled** at both group and individual system level
- Circular dependencies throw `CircularDependencyException` at **initialization** (fail-fast)

---

**Total: 11 SP - Complete system scheduling with profiling!** ⚡🎯✨
