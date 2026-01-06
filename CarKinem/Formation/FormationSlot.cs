using System.Runtime.InteropServices;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation slot definition (stored in BlobAsset or lookup table).
    /// Defines offset in leader's local frame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationSlot
    {
        public float ForwardOffset;   // Longitudinal offset (+ = ahead, - = behind)
        public float LateralOffset;   // Lateral offset (+ = right, - = left)
        public float HeadingOffset;   // Heading offset relative to leader (radians)
    }
}
