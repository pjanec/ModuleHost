using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Road
{
    /// <summary>
    /// Road graph node (junction/intersection).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RoadNode
    {
        public Vector2 Position;          // World position
        public int FirstSegmentIndex;     // Index of first outgoing segment
        public int SegmentCount;          // Number of outgoing segments
    }
}
