using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Road
{
    /// <summary>
    /// Road segment using Cubic Hermite spline representation.
    /// Precomputed with distance LUT for constant-speed sampling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct RoadSegment
    {
        public Vector2 P0, T0, P1, T1;
        public float Length;
        public float SpeedLimit;
        public float LaneWidth;
        public int LaneCount;
        public int StartNodeIndex;
        public int EndNodeIndex;
        
        public fixed float DistanceLUT[8];  // UNSAFE FIXED
    }
}
