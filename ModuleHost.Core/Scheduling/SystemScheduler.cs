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
    /// Systems execute in deterministic order based on Fdp.Kernel [UpdateAfter]/[UpdateBefore] attributes.
    /// </summary>
    public class SystemScheduler : ISystemRegistry
    {
        private readonly Dictionary<SystemPhase, List<IModuleSystem>> _systemsByPhase = new();
        private readonly Dictionary<SystemPhase, List<IModuleSystem>> _sortedSystems = new();
        private readonly Dictionary<IModuleSystem, SystemPhase> _systemPhases = new();
        
        // Profiling data
        private readonly Dictionary<IModuleSystem, SystemProfileData> _profileData = new();
        
        /// <summary>
        /// Register a system for execution.
        /// System's phase is determined by [UpdateInPhase] attribute.
        /// </summary>
        public void RegisterSystem<T>(T system) where T : IModuleSystem
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));
            
            var phase = GetPhaseAttribute(system);
            
            if (!_systemsByPhase.ContainsKey(phase))
                _systemsByPhase[phase] = new List<IModuleSystem>();
            
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
                    if (systems == null) throw new InvalidOperationException("Systems list is null");
                    
                    var systemNames = systems.Select(s => s?.GetType().Name ?? "null");
                    var message = $"Circular dependency detected in phase {phase}. Systems: {string.Join(", ", systemNames)}";
                    
                    // Console.WriteLine(message); // For debugging
                    throw new CircularDependencyException(message);
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
        
        private void ExecuteSystem(IModuleSystem system, ISimulationView view, float deltaTime)
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
        
        private SystemPhase GetPhaseAttribute(IModuleSystem system)
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
        
        private DependencyGraph BuildDependencyGraph(List<IModuleSystem> systems)
        {
            var graph = new DependencyGraph();
            
            // CRITICAL: Create lookup for systems in THIS phase only
            var systemTypesInPhase = new HashSet<Type>(systems.Select(s => s.GetType()));
            
            // First pass: Add all nodes
            foreach (var system in systems)
            {
                graph.AddNode(system);
            }

            // Second pass: Add edges
            foreach (var system in systems)
            {
                // Extract [UpdateAfter] attributes (Using Fdp.Kernel Attribute)
                var afterAttrs = Attribute.GetCustomAttributes(
                    system.GetType(), typeof(Fdp.Kernel.UpdateAfterAttribute), inherit: true)
                    .Cast<Fdp.Kernel.UpdateAfterAttribute>();
                
                foreach (var attr in afterAttrs)
                {
                    // DEBUG: Console.WriteLine($"System {system.GetType().Name} has UpdateAfter({attr.Target.Name})");
                    
                    // CRITICAL FIX: Only add edge if dependency is in CURRENT phase
                    if (systemTypesInPhase.Contains(attr.Target))
                    {
                        var dependency = systems.First(s => s.GetType() == attr.Target);
                        graph.AddEdge(dependency, system); // dependency -> system
                        // DEBUG: Console.WriteLine($"Added edge: {dependency.GetType().Name} -> {system.GetType().Name}");
                    }
                }
                
                // Extract [UpdateBefore] attributes (Using Fdp.Kernel Attribute)
                var beforeAttrs = Attribute.GetCustomAttributes(
                    system.GetType(), typeof(Fdp.Kernel.UpdateBeforeAttribute), inherit: true)
                    .Cast<Fdp.Kernel.UpdateBeforeAttribute>();
                
                foreach (var attr in beforeAttrs)
                {
                    // DEBUG: Console.WriteLine($"System {system.GetType().Name} has UpdateBefore({attr.Target.Name})");

                    if (systemTypesInPhase.Contains(attr.Target))
                    {
                        var dependent = systems.First(s => s.GetType() == attr.Target);
                        graph.AddEdge(system, dependent); // system -> dependent
                        // DEBUG: Console.WriteLine($"Added edge: {system.GetType().Name} -> {dependent.GetType().Name}");
                    }
                }
            }
            
            return graph;
        }
        
        private List<IModuleSystem>? TopologicalSort(DependencyGraph graph)
        {
            // Kahn's algorithm
            var sorted = new List<IModuleSystem>();
            var inDegree = new Dictionary<IModuleSystem, int>();
            var queue = new Queue<IModuleSystem>();
            
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
        public SystemProfileData? GetProfileData(IModuleSystem system)
        {
            return _profileData.TryGetValue(system, out var data) ? data : null;
        }
        
        /// <summary>
        /// Get profiling data for a specific system by type.
        /// </summary>
        public SystemProfileData? GetProfileData<T>() where T : IModuleSystem
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
                                sb.AppendLine($"       -> {nested.GetType().Name} " +
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
