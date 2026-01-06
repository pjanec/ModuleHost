using System;
using Fdp.Kernel.Collections;

namespace CarKinem.Road
{
    /// <summary>
    /// Road network blob (static data, built once during map load).
    /// Passed to systems as read-only reference.
    /// **CRITICAL:** Must be manually disposed when level unloads.
    /// </summary>
    public struct RoadNetworkBlob : IDisposable
    {
        // Core road data
        public NativeArray<RoadNode> Nodes;
        public NativeArray<RoadSegment> Segments;
        
        // Spatial lookup grid (flattened linked list)
        // Enables O(1) "which road segment am I on?" queries
        public NativeArray<int> GridHead;     // Head pointer for each grid cell
        public NativeArray<int> GridNext;     // Next pointer in linked list
        public NativeArray<int> GridValues;   // Segment indices
        
        public float CellSize;    // Grid cell size (meters)
        public int Width;         // Grid width (cells)
        public int Height;        // Grid height (cells)
        
        public void Dispose()
        {
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Segments.IsCreated) Segments.Dispose();
            if (GridHead.IsCreated) GridHead.Dispose();
            if (GridNext.IsCreated) GridNext.Dispose();
            if (GridValues.IsCreated) GridValues.Dispose();
        }
    }
}
