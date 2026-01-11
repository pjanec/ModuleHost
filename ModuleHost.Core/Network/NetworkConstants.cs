namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Centralized network constants and descriptor type IDs.
    /// </summary>
    public static class NetworkConstants
    {
        // === Descriptor Type IDs ===
        /// <summary>DDS Descriptor ID for EntityMaster (entity lifecycle)</summary>
        public const long ENTITY_MASTER_DESCRIPTOR_ID = 0;
        
        /// <summary>DDS Descriptor ID for EntityState (position/velocity)</summary>
        public const long ENTITY_STATE_DESCRIPTOR_ID = 1;
        
        /// <summary>DDS Descriptor ID for WeaponState</summary>
        public const long WEAPON_STATE_DESCRIPTOR_ID = 2;
        
        // === System Message IDs ===
        /// <summary>Lifecycle status messages for reliable init</summary>
        public const long ENTITY_LIFECYCLE_STATUS_ID = 900;
        
        /// <summary>Ownership transfer messages</summary>
        public const long OWNERSHIP_UPDATE_ID = 901;
        
        // === Timeouts ===
        /// <summary>Ghost entity timeout in frames (5 sec @ 60Hz)</summary>
        public const int GHOST_TIMEOUT_FRAMES = 300;
        
        /// <summary>Reliable init ACK timeout in frames (5 sec @ 60Hz)</summary>
        public const int RELIABLE_INIT_TIMEOUT_FRAMES = 300;
    }
}
