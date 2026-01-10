using System.Collections.Generic;
using MessagePack;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    [MessagePackObject]
    [EventId(101)]
    public struct FrameOrderDescriptor
    {
        [Key(0)]
        public long FrameID { get; set; }
        
        [Key(1)]
        public float FixedDelta { get; set; }
        
        [Key(2)]
        public long SequenceID { get; set; }
    }
    
    [MessagePackObject]
    [EventId(100)]
    public struct TimePulseDescriptor
    {
        [Key(0)]
        public long MasterWallTicks { get; set; }
        
        [Key(1)]
        public double SimTimeSnapshot { get; set; }
        
        [Key(2)]
        public float TimeScale { get; set; }
        
        [Key(3)]
        public long SequenceId { get; set; }
    }
    
    [MessagePackObject]
    [EventId(102)]
    public struct FrameAckDescriptor
    {
        [Key(0)]
        public long FrameID { get; set; }
        
        [Key(1)]
        public int NodeID { get; set; }
        
        [Key(2)]
        public int Checksum { get; set; } // Optional state hash for sync verification
    }

    /// <summary>
    /// Network event to switch time mode across distributed system.
    /// Published by Master, consumed by all Slaves.
    /// </summary>
    [MessagePackObject]
    [EventId(103)]
    public struct SwitchTimeModeEvent
    {
        [Key(0)]
        public TimeMode TargetMode { get; set; }  // Continuous or Deterministic
        
        [Key(1)]
        public long FrameNumber { get; set; }  // Current frame for synchronization
        
        [Key(2)]
        public double TotalTime { get; set; }  // Current simulation time
        
        // Removed to satisfy unmanaged constraint
        // public HashSet<int>? AllNodeIds { get; set; }
        
        [Key(3)]
        public float FixedDeltaSeconds { get; set; }  // For Deterministic mode
        
        [Key(4)]
        public long BarrierFrame { get; set; } // Frame at which to switch (0 = immediate)
    }
}
