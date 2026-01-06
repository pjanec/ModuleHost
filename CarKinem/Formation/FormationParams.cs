using System.Runtime.InteropServices;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation parameters (stored in formation entity or singleton table).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationParams
    {
        public float Spacing;            // Nominal spacing between vehicles (meters)
        public float WedgeAngleRad;      // Wedge angle (radians, only for Wedge type)
        public float MaxCatchUpFactor;   // Speed multiplier when catching up (e.g., 1.2 = 20% faster)
        public float BreakDistance;      // Distance beyond which formation breaks (meters)
        public float ArrivalThreshold;   // Distance to consider "in slot" (meters)
        public float SpeedFilterTau;     // Time constant for speed filtering (seconds)
    }
}
