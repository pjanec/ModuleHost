using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation target (transient scratchpad component).
    /// Written by FormationTargetSystem, read by CarKinematicsSystem.
    /// Not persisted between frames.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationTarget
    {
        public Vector2 TargetPosition;   // Desired world position
        public Vector2 TargetHeading;    // Desired forward vector
        public float TargetSpeed;        // Desired speed (includes catch-up factor)
        public byte IsValid;             // 1 = valid target, 0 = ignore
    }
}
