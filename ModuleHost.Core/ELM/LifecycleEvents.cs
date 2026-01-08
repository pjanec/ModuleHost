using System;
using Fdp.Kernel;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Published when an entity begins construction.
    /// Modules should initialize their components and respond with ConstructionAck.
    /// </summary>
    [EventId(9001)]
    public struct ConstructionOrder
    {
        /// <summary>
        /// Entity being constructed.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Entity type ID (for modules to decide if they care).
        /// </summary>
        public int TypeId;
        
        /// <summary>
        /// Frame number when construction started.
        /// </summary>
        public uint FrameNumber;
        
        /// <summary>
        /// Optional: Initiating module ID (who spawned it).
        /// </summary>
        public int InitiatorModuleId;
    }
    
    /// <summary>
    /// Response from a module indicating it has initialized the entity.
    /// </summary>
    [EventId(9002)]
    public struct ConstructionAck
    {
        /// <summary>
        /// Entity that was initialized.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Module ID that completed initialization.
        /// </summary>
        public int ModuleId;
        
        /// <summary>
        /// Optional: Success flag (allows modules to report initialization failure).
        /// </summary>
        public bool Success;
        
        /// <summary>
        /// Optional: Error message if Success == false.
        /// </summary>
        public FixedString64 ErrorMessage;
    }
    
    /// <summary>
    /// Published when an entity begins teardown.
    /// Modules should cleanup their state and respond with DestructionAck.
    /// </summary>
    [EventId(9003)]
    public struct DestructionOrder
    {
        /// <summary>
        /// Entity being destroyed.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Frame number when destruction started.
        /// </summary>
        public uint FrameNumber;
        
        /// <summary>
        /// Optional: Reason for destruction (debug info).
        /// </summary>
        public FixedString64 Reason;
    }
    
    /// <summary>
    /// Response from a module indicating it has cleaned up the entity.
    /// </summary>
    [EventId(9004)]
    public struct DestructionAck
    {
        /// <summary>
        /// Entity that was cleaned up.
        /// </summary>
        public Entity Entity;
        
        /// <summary>
        /// Module ID that completed cleanup.
        /// </summary>
        public int ModuleId;
        
        /// <summary>
        /// Optional: Success flag.
        /// </summary>
        public bool Success;
    }
}
