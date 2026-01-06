using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Core
{
    /// <summary>
    /// Per-vehicle physics state (double-buffered by ECS).
    /// Uses bicycle kinematic model.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleState
    {
        public Vector2 Position;    // World position (meters)
        public Vector2 Forward;     // Normalized heading vector
        public float Speed;         // Scalar forward speed (m/s, >= 0)
        public float SteerAngle;    // Current wheel angle (radians)
        public float Accel;         // Longitudinal acceleration (m/s²)
        
        // Presentation only (derived)
        public float Pitch;         // Forward/backward tilt for visuals
        public float Roll;          // Lateral tilt for visuals
        
        // Metadata
        public int CurrentLaneIndex; // For lane-aware logic
    }
}
