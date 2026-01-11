using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Abstraction for network peer discovery and topology management.
    /// Supports both static configuration and dynamic peer detection.
    /// </summary>
    public interface INetworkTopology
    {
        /// <summary>
        /// Gets the local node ID.
        /// </summary>
        int LocalNodeId { get; }
        
        /// <summary>
        /// Returns the list of peer node IDs expected to participate in
        /// construction of entities of the given type.
        /// Used for reliable initialization barriers.
        /// </summary>
        /// <param name="entityType">DIS entity type</param>
        /// <returns>Collection of peer node IDs (excluding LocalNodeId)</returns>
        IEnumerable<int> GetExpectedPeers(DISEntityType entityType);
    }
}
