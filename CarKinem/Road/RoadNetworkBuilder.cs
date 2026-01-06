using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel.Collections;

namespace CarKinem.Road
{
    /// <summary>
    /// Builder for constructing RoadNetworkBlob from components.
    /// Handles Hermite spline LUT precomputation and spatial grid rasterization.
    /// </summary>
    public class RoadNetworkBuilder
    {
        private readonly List<RoadNode> _nodes = new();
        private readonly List<RoadSegment> _segments = new();
        
        /// <summary>
        /// Add a road node (junction/intersection).
        /// </summary>
        public void AddNode(Vector2 position)
        {
            _nodes.Add(new RoadNode
            {
                Position = position,
                SegmentCount = 0
            });
        }
        
        /// <summary>
        /// Add a road segment with Hermite control points.
        /// </summary>
        public void AddSegment(
            Vector2 p0, Vector2 t0,
            Vector2 p1, Vector2 t1,
            float speedLimit = 25.0f,
            float laneWidth = 3.5f,
            int laneCount = 1,
            int startNodeIdx = -1,
            int endNodeIdx = -1)
        {
            // Precompute length via sampling
            float length = ComputeHermiteLength(p0, t0, p1, t1);
            
            var segment = new RoadSegment
            {
                P0 = p0,
                T0 = t0,
                P1 = p1,
                T1 = t1,
                Length = length,
                SpeedLimit = speedLimit,
                LaneWidth = laneWidth,
                LaneCount = laneCount,
                StartNodeIndex = startNodeIdx,
                EndNodeIndex = endNodeIdx
            };
            
            // Precompute distance LUT (8 samples)
            ComputeDistanceLUT(ref segment);
            
            _segments.Add(segment);
        }
        
        /// <summary>
        /// Build final RoadNetworkBlob with spatial grid.
        /// </summary>
        public RoadNetworkBlob Build(float cellSize, int gridWidth, int gridHeight)
        {
            var blob = new RoadNetworkBlob
            {
                Nodes = new NativeArray<RoadNode>(_nodes.Count, Allocator.Persistent),
                Segments = new NativeArray<RoadSegment>(_segments.Count, Allocator.Persistent),
                GridHead = new NativeArray<int>(gridWidth * gridHeight, Allocator.Persistent),
                GridNext = new NativeArray<int>(_segments.Count * 100, Allocator.Persistent), // Estimate
                GridValues = new NativeArray<int>(_segments.Count * 100, Allocator.Persistent),
                CellSize = cellSize,
                Width = gridWidth,
                Height = gridHeight
            };
            
            // Copy nodes
            for (int i = 0; i < _nodes.Count; i++)
                blob.Nodes[i] = _nodes[i];
            
            // Copy segments
            for (int i = 0; i < _segments.Count; i++)
                blob.Segments[i] = _segments[i];
            
            // Build spatial grid
            BuildSpatialGrid(ref blob);
            
            return blob;
        }
        
        /// <summary>
        /// Compute Hermite spline arc length via trapezoidal integration.
        /// </summary>
        private float ComputeHermiteLength(Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            const int SAMPLES = 32;
            float length = 0f;
            Vector2 prevPoint = p0;
            
            for (int i = 1; i <= SAMPLES; i++)
            {
                float t = i / (float)SAMPLES;
                Vector2 point = EvaluateHermite(t, p0, t0, p1, t1);
                length += Vector2.Distance(prevPoint, point);
                prevPoint = point;
            }
            
            return length;
        }
        
        /// <summary>
        /// Precompute distance LUT for constant-speed sampling.
        /// Maps 8 uniformly spaced distances to parameter t.
        /// </summary>
        private unsafe void ComputeDistanceLUT(ref RoadSegment segment)
        {
            const int LUT_SIZE = 8;
            
            for (int i = 0; i < LUT_SIZE; i++)
            {
                float targetDist = (i / (float)(LUT_SIZE - 1)) * segment.Length;
                
                // Binary search for t that gives targetDist
                float t = FindParameterForDistance(segment, targetDist);
                segment.DistanceLUT[i] = t;
            }
        }
        
        /// <summary>
        /// Find parameter t that produces a given arc-length distance.
        /// Uses binary search with numerical integration.
        /// </summary>
        private float FindParameterForDistance(RoadSegment segment, float targetDist)
        {
            float tMin = 0f;
            float tMax = 1f;
            const int MAX_ITERATIONS = 10;
            
            for (int iter = 0; iter < MAX_ITERATIONS; iter++)
            {
                float tMid = (tMin + tMax) * 0.5f;
                float distAtMid = ComputeDistanceAtT(segment, tMid);
                
                if (MathF.Abs(distAtMid - targetDist) < 0.01f)
                    return tMid;
                
                if (distAtMid < targetDist)
                    tMin = tMid;
                else
                    tMax = tMid;
            }
            
            return (tMin + tMax) * 0.5f;
        }
        
        /// <summary>
        /// Compute arc-length distance from start to parameter t.
        /// </summary>
        private float ComputeDistanceAtT(RoadSegment segment, float t)
        {
            const int SAMPLES = 16;
            float dist = 0f;
            Vector2 prevPoint = segment.P0;
            
            for (int i = 1; i <= (int)(t * SAMPLES); i++)
            {
                float tSample = i / (float)SAMPLES;
                Vector2 point = EvaluateHermite(tSample, segment.P0, segment.T0, segment.P1, segment.T1);
                dist += Vector2.Distance(prevPoint, point);
                prevPoint = point;
            }
            
            return dist;
        }
        
        /// <summary>
        /// Evaluate Hermite spline at parameter t.
        /// </summary>
        private Vector2 EvaluateHermite(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            float h00 = 2 * t3 - 3 * t2 + 1;
            float h10 = t3 - 2 * t2 + t;
            float h01 = -2 * t3 + 3 * t2;
            float h11 = t3 - t2;
            
            return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
        }
        
        /// <summary>
        /// Build spatial hash grid for fast segment lookup.
        /// Uses Bresenham-like rasterization to find all cells a segment touches.
        /// </summary>
        private void BuildSpatialGrid(ref RoadNetworkBlob blob)
        {
            // Initialize grid heads to -1 (empty)
            for (int i = 0; i < blob.GridHead.Length; i++)
                blob.GridHead[i] = -1;
            
            int nextFreeSlot = 0;
            
            // Rasterize each segment
            for (int segId = 0; segId < blob.Segments.Length; segId++)
            {
                var segment = blob.Segments[segId];
                
                // Sample segment and add to all touched cells
                const int RASTER_SAMPLES = 16;
                for (int i = 0; i <= RASTER_SAMPLES; i++)
                {
                    float t = i / (float)RASTER_SAMPLES;
                    Vector2 point = EvaluateHermite(t, segment.P0, segment.T0, segment.P1, segment.T1);
                    
                    int cellX = (int)(point.X / blob.CellSize);
                    int cellY = (int)(point.Y / blob.CellSize);
                    
                    if (cellX < 0 || cellX >= blob.Width || cellY < 0 || cellY >= blob.Height)
                        continue;
                    
                    int cellIdx = cellY * blob.Width + cellX;
                    
                    // Add to linked list (avoid duplicates)
                    if (!ContainsSegment(blob, cellIdx, segId))
                    {
                        blob.GridValues[nextFreeSlot] = segId;
                        blob.GridNext[nextFreeSlot] = blob.GridHead[cellIdx];
                        blob.GridHead[cellIdx] = nextFreeSlot;
                        nextFreeSlot++;
                    }
                }
            }
        }
        
        /// <summary>
        /// Check if segment is already in cell's linked list.
        /// </summary>
        private bool ContainsSegment(RoadNetworkBlob blob, int cellIdx, int segId)
        {
            int head = blob.GridHead[cellIdx];
            while (head >= 0)
            {
                if (blob.GridValues[head] == segId)
                    return true;
                head = blob.GridNext[head];
            }
            return false;
        }
    }
}
