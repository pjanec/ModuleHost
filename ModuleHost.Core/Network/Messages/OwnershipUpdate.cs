using System;

namespace ModuleHost.Core.Network.Messages
{
    /// <summary>
    /// SST ownership transfer message.
    /// Sent when descriptor ownership changes between nodes.
    /// </summary>
    public struct OwnershipUpdate
    {
        /// <summary>
        /// Network entity ID (not FDP entity).
        /// </summary>
        public long EntityId;
        
        /// <summary>
        /// Descriptor type ID.
        /// Examples: 1=EntityState, 2=WeaponState, 0=EntityMaster
        /// </summary>
        public long DescrTypeId;
        
        /// <summary>
        /// Descriptor instance ID (for multi-instance descriptors).
        /// Zero if descriptor type has single instance per entity.
        /// </summary>
        public long InstanceId;
        
        /// <summary>
        /// New owner node ID.
        /// </summary>
        public int NewOwner;
        
        /// <summary>
        /// Timestamp of the update (ms since epoch).
        /// </summary>
        public long Timestamp;
    }
}
