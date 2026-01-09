using System;
using System.Collections.Generic;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Configuration for time controller instantiation.
    /// </summary>
    public class TimeControllerConfig
    {
        /// <summary>
        /// Role of this peer in time synchronization.
        /// </summary>
        public TimeRole Role { get; set; } = TimeRole.Standalone;
        
        /// <summary>
        /// Synchronization mode (Continuous vs. Deterministic).
        /// </summary>
        public TimeMode Mode { get; set; } = TimeMode.Continuous;
        
        /// <summary>
        /// PLL and synchronization parameters.
        /// </summary>
        public TimeConfig SyncConfig { get; set; } = TimeConfig.Default;
        
        /// <summary>
        /// For Deterministic mode: IDs of all nodes in the cluster.
        /// Required for Master role in lockstep mode.
        /// </summary>
        public HashSet<int>? AllNodeIds { get; set; }
        
        /// <summary>
        /// For Slave role: ID of this local node.
        /// Required for sending ACKs in lockstep mode.
        /// </summary>
        public int LocalNodeId { get; set; } = 0;
        
        /// <summary>
        /// Initial time scale (0.0 = paused, 1.0 = realtime).
        /// </summary>
        public float InitialTimeScale { get; set; } = 1.0f;
        
        /// <summary>
        /// For testing: inject custom tick source.
        /// Only supported for Slave modes (Continuous and Deterministic).
        /// Standalone mode always uses System.Diagnostics.Stopwatch.
        /// </summary>
        internal Func<long>? TickProvider { get; set; }
    }
    
    /// <summary>
    /// Role of this peer in distributed time synchronization.
    /// </summary>
    public enum TimeRole
    {
        /// <summary>
        /// Standalone (no network synchronization).
        /// Uses local wall clock.
        /// </summary>
        Standalone,
        
        /// <summary>
        /// Master (authoritative time source).
        /// Publishes TimePulse (Continuous) or FrameOrder (Deterministic).
        /// </summary>
        Master,
        
        /// <summary>
        /// Slave (follows master).
        /// Consumes TimePulse (Continuous) or FrameOrder (Deterministic).
        /// </summary>
        Slave
    }
}
