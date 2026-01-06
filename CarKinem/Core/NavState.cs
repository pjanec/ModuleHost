using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Core
{
    /// <summary>
    /// Navigation/control state.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavState
    {
        // Navigation mode and state
        public NavigationMode Mode;       // Current navigation mode
        public RoadGraphPhase RoadPhase;  // Road graph state machine (only if Mode == RoadGraph)
        
        // Trajectory references
        public int TrajectoryId;     // Index into custom trajectory pool (-1 if none)
        public int CurrentSegmentId; // Current road segment ID (only if Mode == RoadGraph)
        
        // Progress tracking
        public float ProgressS;      // Arc-length progress along path (meters)
        public float TargetSpeed;    // Desired cruise/arrival speed (m/s)
        
        // Destination (for RoadGraph mode: final off-road target)
        public Vector2 FinalDestination;
        public float ArrivalRadius;  // Distance to consider "arrived" (meters)
        
        // Controller internals (for stability)
        public float SpeedErrorInt;  // Speed integral term (for PI control)
        public float LastSteerCmd;   // Previous steering command
        
        // Flags (use byte for blittable safety)
        public byte ReverseAllowed;  // 1 = allow reverse (NOT IMPLEMENTED in v1)
        public byte HasArrived;      // 1 = within arrival tolerance
        public byte IsBlocked;       // 1 = obstacle detected ahead
        
        // Padding for alignment if necessary (optional, but good for explicit structs. 
        // 2 bytes + 2 ints + 2 floats + Vector2(2 floats) + 1 float + 2 floats + 3 bytes = 
        // 1+1 + 4*2 + 4*2 + 8 + 4 + 4*2 + 3 = 2+8+8+8+4+8+3 = 41 bytes?
        // Let's rely on StructLayout for now, usually it automates padding unless Pack is specified.
        // It says "Sequential" so it should be fine.
    }
}
