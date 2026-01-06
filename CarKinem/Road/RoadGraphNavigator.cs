using System;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Road
{
    /// <summary>
    /// Road graph navigation helper.
    /// Calculates target position/speed for vehicles in RoadGraph mode.
    /// </summary>
    public static class RoadGraphNavigator
    {
        /// <summary>
        /// Find closest point on entire road network.
        /// Uses spatial hash for efficient lookup.
        /// Returns (segmentId, nearestPoint, distance).
        /// </summary>
        public static (int segId, Vector2 point, float dist) FindClosestRoadPoint(
            Vector2 position,
            RoadNetworkBlob roadNetwork)
        {
            int closestSegId = -1;
            Vector2 closestPoint = Vector2.Zero;
            float minDist = float.MaxValue;
            
            // Get grid cell for spatial lookup
            int cellX = (int)(position.X / roadNetwork.CellSize);
            int cellY = (int)(position.Y / roadNetwork.CellSize);
            
            // Search current cell and adjacent cells (3x3 grid)
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = cellX + dx;
                    int cy = cellY + dy;
                    
                    if (cx < 0 || cx >= roadNetwork.Width || cy < 0 || cy >= roadNetwork.Height)
                        continue;
                    
                    int cellIdx = cy * roadNetwork.Width + cx;
                    int head = roadNetwork.GridHead[cellIdx];
                    
                    // Iterate linked list of segments in this cell
                    while (head >= 0)
                    {
                        int segId = roadNetwork.GridValues[head];
                        ref readonly var segment = ref roadNetwork.Segments[segId];
                        
                        // Project point onto Hermite curve (sample-based)
                        Vector2 nearestOnSeg = ProjectPointOntoHermiteSegment(position, segment);
                        float dist = Vector2.Distance(position, nearestOnSeg);
                        
                        if (dist < minDist)
                        {
                            minDist = dist;
                            closestPoint = nearestOnSeg;
                            closestSegId = segId;
                        }
                        
                        head = roadNetwork.GridNext[head];
                    }
                }
            }
            
            return (closestSegId, closestPoint, minDist);
        }
        
        /// <summary>
        /// Project point onto Hermite segment (sample-based approximation).
        /// Uses 16 samples to find closest point on curve.
        /// </summary>
        private static Vector2 ProjectPointOntoHermiteSegment(Vector2 point, RoadSegment segment)
        {
            const int SAMPLES = 16;
            Vector2 closestPoint = segment.P0;
            float minDistSq = float.MaxValue;
            
            for (int i = 0; i <= SAMPLES; i++)
            {
                float t = i / (float)SAMPLES;
                Vector2 samplePoint = EvaluateHermite(t, segment.P0, segment.T0, segment.P1, segment.T1);
                float distSq = Vector2.DistanceSquared(point, samplePoint);
                
                if (distSq < minDistSq)
                {
                    minDistSq = distSq;
                    closestPoint = samplePoint;
                }
            }
            
            return closestPoint;
        }
        
        /// <summary>
        /// Evaluate Hermite spline at parameter t in [0,1].
        /// </summary>
        public static Vector2 EvaluateHermite(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            // Hermite basis functions
            float h00 = 2 * t3 - 3 * t2 + 1;
            float h10 = t3 - 2 * t2 + t;
            float h01 = -2 * t3 + 3 * t2;
            float h11 = t3 - t2;
            
            return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
        }
        
        /// <summary>
        /// Evaluate Hermite tangent (derivative) at parameter t.
        /// Returns unnormalized tangent vector.
        /// </summary>
        public static Vector2 EvaluateHermiteTangent(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            float t2 = t * t;
            
            // Derivatives of Hermite basis functions
            float dh00 = 6 * t2 - 6 * t;
            float dh10 = 3 * t2 - 4 * t + 1;
            float dh01 = -6 * t2 + 6 * t;
            float dh11 = 3 * t2 - 2 * t;
            
            return dh00 * p0 + dh10 * t0 + dh01 * p1 + dh11 * t1;
        }
        
        /// <summary>
        /// Sample road segment at given arc-length progress.
        /// Uses precomputed LUT for constant-speed sampling.
        /// Returns (position, tangent, speed limit).
        /// </summary>
        public static unsafe (Vector2 pos, Vector2 tangent, float speed) SampleRoadSegment(
            RoadSegment segment,
            float progressS)
        {
            // Clamp progress to segment
            progressS = Math.Clamp(progressS, 0f, segment.Length);
            
            // Use LUT to convert distance -> parameter t
            float normalizedDist = progressS / segment.Length;
            int lutIndex = (int)(normalizedDist * 7); // 8 samples = 7 intervals
            lutIndex = Math.Clamp(lutIndex, 0, 6);
            
            float t0 = segment.DistanceLUT[lutIndex];
            float t1 = segment.DistanceLUT[lutIndex + 1];
            
            // Linear interpolation within LUT interval
            float localT = (normalizedDist * 7) - lutIndex;
            float t = t0 + (t1 - t0) * localT;
            
            // Evaluate position
            Vector2 pos = EvaluateHermite(t, segment.P0, segment.T0, segment.P1, segment.T1);
            
            // Evaluate tangent (derivative of Hermite)
            Vector2 tangent = EvaluateHermiteTangent(t, segment.P0, segment.T0, segment.P1, segment.T1);
            
            // Normalize tangent
            if (tangent.LengthSquared() > 0.001f)
                tangent = Vector2.Normalize(tangent);
            else
                tangent = new Vector2(1, 0); // Fallback
            
            return (pos, tangent, segment.SpeedLimit);
        }
        
        /// <summary>
        /// Execute road graph navigation state machine.
        /// Updates NavState and returns target (position, heading, speed).
        /// </summary>
        public static (Vector2 targetPos, Vector2 targetHeading, float targetSpeed) UpdateRoadGraphNavigation(
            ref NavState nav,
            Vector2 currentPos,
            RoadNetworkBlob roadNetwork)
        {
            switch (nav.RoadPhase)
            {
                case RoadGraphPhase.Approaching:
                {
                    // Find closest entry point
                    var (segId, entryPoint, dist) = FindClosestRoadPoint(currentPos, roadNetwork);
                    
                    if (dist < 2.0f) // Within approach threshold
                    {
                        // Transition to Following
                        nav.RoadPhase = RoadGraphPhase.Following;
                        nav.CurrentSegmentId = segId;
                        nav.ProgressS = 0f;
                    }
                    
                    // Target: entry point
                    Vector2 toEntry = entryPoint - currentPos;
                    Vector2 heading = toEntry.LengthSquared() > 0.01f 
                        ? Vector2.Normalize(toEntry) 
                        : new Vector2(1, 0);
                    
                    return (entryPoint, heading, 10.0f);
                }
                
                case RoadGraphPhase.Following:
                {
                    // Follow current road segment
                    if (nav.CurrentSegmentId < 0 || nav.CurrentSegmentId >= roadNetwork.Segments.Length)
                    {
                        // Invalid segment - transition to Leaving
                        nav.RoadPhase = RoadGraphPhase.Leaving;
                        goto case RoadGraphPhase.Leaving;
                    }
                    
                    ref readonly var segment = ref roadNetwork.Segments[nav.CurrentSegmentId];
                    
                    // Check if close enough to destination to leave road
                    float distToDest = Vector2.Distance(currentPos, nav.FinalDestination);
                    var (exitSegId, exitPoint, distToExit) = FindClosestRoadPoint(nav.FinalDestination, roadNetwork);
                    
                    if (distToExit < 5.0f && distToDest < 50.0f)
                    {
                        // Close enough - transition to Leaving
                        nav.RoadPhase = RoadGraphPhase.Leaving;
                        goto case RoadGraphPhase.Leaving;
                    }
                    
                    // Continue following road
                    var (pos, tangent, speedLimit) = SampleRoadSegment(segment, nav.ProgressS);
                    
                    // Lookahead target (10m ahead on road)
                    Vector2 targetPos = pos + tangent * 10.0f;
                    
                    // Check if reached end of segment
                    if (nav.ProgressS >= segment.Length - 1.0f)
                    {
                        // TODO: Pathfinding to next segment (simplified: stay on current)
                        nav.ProgressS = Math.Min(nav.ProgressS, segment.Length);
                    }
                    
                    return (targetPos, tangent, speedLimit);
                }
                
                case RoadGraphPhase.Leaving:
                {
                    // Drive directly to final destination
                    float distToDest = Vector2.Distance(currentPos, nav.FinalDestination);
                    
                    if (distToDest < nav.ArrivalRadius)
                    {
                        // Arrived!
                        nav.RoadPhase = RoadGraphPhase.Arrived;
                        nav.HasArrived = 1;
                        return (currentPos, new Vector2(1, 0), 0f);
                    }
                    
                    Vector2 toDest = nav.FinalDestination - currentPos;
                    Vector2 heading = toDest.LengthSquared() > 0.01f
                        ? Vector2.Normalize(toDest)
                        : new Vector2(1, 0);
                    
                    return (nav.FinalDestination, heading, 5.0f);
                }
                
                case RoadGraphPhase.Arrived:
                default:
                    return (currentPos, new Vector2(1, 0), 0f);
            }
        }
    }
}
