using Fdp.Kernel;

namespace ModuleHost.Core.Network.Messages
{
    /// <summary>
    /// DDS message published by peer nodes to confirm entity activation
    /// in reliable initialization mode. Master node collects these to
    /// determine when all peers have completed construction.
    /// </summary>
    public class EntityLifecycleStatusDescriptor
    {
        /// <summary>Network entity ID</summary>
        public long EntityId { get; set; }
        
        /// <summary>Reporting node ID</summary>
        public int NodeId { get; set; }
        
        /// <summary>Lifecycle state achieved</summary>
        public EntityLifecycle State { get; set; }
        
        /// <summary>Timestamp of status update</summary>
        public long Timestamp { get; set; }
    }
}
