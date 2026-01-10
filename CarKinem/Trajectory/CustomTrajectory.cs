using Fdp.Kernel.Collections;
using System.Runtime.InteropServices;

namespace CarKinem.Trajectory
{
    /// <summary>
    /// Custom trajectory definition.
    /// Stored in global trajectory pool.
    /// </summary>
    public struct CustomTrajectory
    {
        public int Id;                        // Unique trajectory ID
        public NativeArray<TrajectoryWaypoint> Waypoints; // Trajectory path
        public float TotalLength;             // Total arc length (meters)
        public byte IsLooped;                 // 1 = loop back to start, 0 = one-shot
        public TrajectoryInterpolation Interpolation; // Interpolation mode
    }
}
