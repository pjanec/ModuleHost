using System;
using Fdp.Kernel;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Flags for EntityMaster descriptor configuration.
    /// </summary>
    [Flags]
    public enum MasterFlags
    {
        /// <summary>No special flags</summary>
        None = 0,
        
        /// <summary>
        /// Reliable initialization mode: Master waits for all peers to confirm
        /// entity activation before considering construction complete.
        /// </summary>
        ReliableInit = 1 << 0,
    }

    public class EntityMasterDescriptor
    {
        public long EntityId { get; set; }
        public int OwnerId { get; set; }
        public DISEntityType Type { get; set; }
        public string Name { get; set; }
        public MasterFlags Flags { get; set; } = MasterFlags.None;
    }
}
