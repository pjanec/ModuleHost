using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Trajectory
{
    /// <summary>
    /// Custom trajectory waypoint.
    /// Linear interpolation between waypoints.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TrajectoryWaypoint
    {
        public Vector2 Position;      // World position
        public Vector2 Tangent;       // Optional tangent for smooth curves (zero for linear)
        public float DesiredSpeed;    // Desired speed at this waypoint (m/s)
        public float CumulativeDistance; // Precomputed distance from start (meters)
    }
}
