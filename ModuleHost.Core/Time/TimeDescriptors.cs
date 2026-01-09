using System;
using System.Runtime.InteropServices;

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
}
