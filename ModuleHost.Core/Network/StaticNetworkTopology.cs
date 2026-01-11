using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Static network topology with hardcoded peer lists.
    /// For simple deployments and testing.
    /// </summary>
    public class StaticNetworkTopology : INetworkTopology
    {
        private readonly int _localNodeId;
        private readonly int[] _allNodes;
        
        public int LocalNodeId => _localNodeId;
        
        /// <summary>
        /// Creates a static topology where all nodes participate in all entity types.
        /// </summary>
        /// <param name="localNodeId">This node's ID</param>
        /// <param name="allNodes">All node IDs in the cluster (including local)</param>
        public StaticNetworkTopology(int localNodeId, int[] allNodes)
        {
            _localNodeId = localNodeId;
            _allNodes = allNodes ?? throw new System.ArgumentNullException(nameof(allNodes));
        }
        
        public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
        {
            // Return all nodes except local
            return _allNodes.Where(id => id != _localNodeId);
        }
    }
}
