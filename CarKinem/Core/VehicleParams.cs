using System.Runtime.InteropServices;

namespace CarKinem.Core
{
    /// <summary>
    /// Per-vehicle configuration (flyweight pattern).
    /// Referenced by index from VehicleState.
    /// Stored in global NativeArray<VehicleParams> table.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleParams
    {
        public VehicleClass Class;   // Vehicle classification
        public float Length;         // Vehicle length (meters)
        public float Width;          // Vehicle width (meters)
        public float WheelBase;      // Distance between axles (meters)
        
        public float MaxSpeedFwd;    // Max forward speed (m/s)
        public float MaxSpeedRev;    // Reserved for future (currently unused)
        
        public float MaxAccel;       // Max acceleration (m/s²)
        public float MaxDecel;       // Max braking deceleration (m/s²)
        
        public float MaxSteerAngle;  // Max steering angle (radians)
        public float MaxSteerRate;   // Max steering rate (rad/s)
        
        public float MaxLatAccel;    // Max lateral acceleration for curvature limits
        public float AvoidanceRadius; // Collision radius for RVO (meters)
        
        // Control tuning
        public float LookaheadTimeMin; // Pure Pursuit lookahead min (seconds)
        public float LookaheadTimeMax; // Pure Pursuit lookahead max (seconds)
        public float AccelGain;        // Speed controller proportional gain
    }
}
