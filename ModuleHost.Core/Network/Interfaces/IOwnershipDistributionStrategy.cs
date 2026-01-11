using Fdp.Kernel;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Strategy interface for determining initial descriptor ownership
    /// in partial ownership scenarios.
    /// </summary>
    public interface IOwnershipDistributionStrategy
    {
        /// <summary>
        /// Determines the initial owner for a specific descriptor on a newly created entity.
        /// </summary>
        /// <param name="descriptorTypeId">DDS descriptor type ID</param>
        /// <param name="entityType">DIS entity type from EntityMaster</param>
        /// <param name="masterNodeId">Primary owner (EntityMaster owner)</param>
        /// <param name="instanceId">Descriptor instance ID (0 for single-instance)</param>
        /// <returns>
        /// Node ID that should own this descriptor, or null to use masterNodeId as default.
        /// </returns>
        int? GetInitialOwner(
            long descriptorTypeId, 
            DISEntityType entityType, 
            int masterNodeId, 
            long instanceId);
    }
}
