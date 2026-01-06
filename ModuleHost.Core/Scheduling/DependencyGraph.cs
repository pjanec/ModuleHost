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
        private readonly HashSet<IModuleSystem> _nodes = new();
        private readonly Dictionary<IModuleSystem, HashSet<IModuleSystem>> _edges = new();
        
        public IReadOnlyCollection<IModuleSystem> Nodes => _nodes;
        
        public void AddNode(IModuleSystem system)
        {
            _nodes.Add(system);
            if (!_edges.ContainsKey(system))
                _edges[system] = new HashSet<IModuleSystem>();
        }
        
        /// <summary>
        /// Add edge: from -> to (from must execute before to).
        /// </summary>
        public void AddEdge(IModuleSystem from, IModuleSystem to)
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
        public IEnumerable<IModuleSystem> GetOutgoingEdges(IModuleSystem system)
        {
            return _edges.TryGetValue(system, out var deps) ? deps : Enumerable.Empty<IModuleSystem>();
        }
        
        /// <summary>
        /// Get all systems this system depends on (incoming edges).
        /// </summary>
        public IEnumerable<IModuleSystem> GetIncomingEdges(IModuleSystem system)
        {
            return _edges.Where(kvp => kvp.Value.Contains(system))
                         .Select(kvp => kvp.Key);
        }
        
        /// <summary>
        /// Get count of incoming edges (dependencies).
        /// </summary>
        public int GetInDegree(IModuleSystem system)
        {
            return GetIncomingEdges(system).Count();
        }
    }
}
