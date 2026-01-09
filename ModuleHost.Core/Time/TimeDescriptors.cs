using System;
using System.Runtime.InteropServices;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// TimePulse descriptor for Continuous mode synchronization.
    /// Published by Master at 1Hz + on time scale changes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TimePulseDescriptor
    {
        /// <summary>
        /// Master's high-resolution clock at snapshot time (Stopwatch ticks).
        /// </summary>
        public long MasterWallTicks;
        
        /// <summary>
        /// Master's simulation time at snapshot moment (seconds).
        /// </summary>
        public double SimTimeSnapshot;
        
        /// <summary>
        /// Current time scale (0.0 = paused, 1.0 = realtime, 2.0 = 2x speed).
        /// </summary>
        public float TimeScale;
        
        /// <summary>
        /// Sequence number for detecting dropped packets.
        /// </summary>
        public long SequenceId;
    }

    /// <summary>
    /// Frame order command from Master to Slaves (lockstep mode).
    /// Master broadcasts this when all ACKs from Frame N-1 are received.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    [EventId(2001)] // Unique ID for FrameOrder
    public struct FrameOrderDescriptor
    {
        /// <summary>
        /// Frame number to execute.
        /// </summary>
        public long FrameID;
        
        /// <summary>
        /// Fixed delta time for this frame (seconds).
        /// Usually constant (e.g., 16.67ms for 60Hz).
        /// </summary>
        public float FixedDelta;
        
        /// <summary>
        /// Sequence number for reliability check.
        /// </summary>
        public long SequenceID;
    }
    
    /// <summary>
    /// Frame ACK from Slave to Master (lockstep mode).
    /// Slave sends this AFTER completing frame execution.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    [EventId(2002)] // Unique ID for FrameAck
    public struct FrameAckDescriptor
    {
        /// <summary>
        /// Frame number that was completed.
        /// </summary>
        public long FrameID;
        
        /// <summary>
        /// Node ID of sender.
        /// </summary>
        public int NodeID;
        
        /// <summary>
        /// Simulation time at end of frame (for verification).
        /// </summary>
        public double TotalTime;
    }
}
