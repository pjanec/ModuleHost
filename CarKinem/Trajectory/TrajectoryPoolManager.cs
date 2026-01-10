using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel.Collections;

namespace CarKinem.Trajectory
{
    /// <summary>
    /// Trajectory pool singleton (managed component).
    /// Stores all custom trajectories in the simulation.
    /// Thread-safe for reads (immutable after creation).
    /// </summary>
    public class TrajectoryPoolManager : IDisposable
    {
        private readonly Dictionary<int, CustomTrajectory> _trajectories = new();
        private int _nextId = 1;
        private readonly object _lock = new();
        
        /// <summary>
        /// Register a new custom trajectory with optional interpolation mode.
        /// </summary>
        /// <param name="positions">Waypoint positions</param>
        /// <param name="speeds">Desired speeds at each waypoint (optional)</param>
        /// <param name="looped">True if trajectory loops back to start</param>
        /// <param name="interpolation">Interpolation mode (default: Linear for backward compat)</param>
        /// <param name="tangents">Explicit tangents (only for HermiteExplicit mode)</param>
        /// <returns>Unique trajectory ID</returns>
        public int RegisterTrajectory(
            Vector2[] positions, 
            float[]? speeds = null, 
            bool looped = false,
            TrajectoryInterpolation interpolation = TrajectoryInterpolation.Linear,
            Vector2[]? tangents = null)
        {
            if (positions == null || positions.Length < 2)
                throw new ArgumentException("Trajectory must have at least 2 waypoints", nameof(positions));
            
            if (speeds != null && speeds.Length != positions.Length)
                throw new ArgumentException("Speeds array must match positions length", nameof(speeds));
            
            if (interpolation == TrajectoryInterpolation.HermiteExplicit && tangents == null)
                throw new ArgumentException("HermiteExplicit mode requires tangents array", nameof(tangents));
            
            if (tangents != null && tangents.Length != positions.Length)
                throw new ArgumentException("Tangents array must match positions length", nameof(tangents));
            
            lock (_lock)
            {
                int id = _nextId++;
                
                // Precompute waypoints with cumulative distance
                var waypoints = new NativeArray<TrajectoryWaypoint>(positions.Length, Allocator.Persistent);
                float cumulativeDistance = 0f;
                
                for (int i = 0; i < positions.Length; i++)
                {
                    // Compute arc length (depends on interpolation mode)
                    if (i > 0)
                    {
                        if (interpolation == TrajectoryInterpolation.Linear)
                        {
                            // Linear: Straight line distance
                            cumulativeDistance += Vector2.Distance(positions[i - 1], positions[i]);
                        }
                        else
                        {
                            // Hermite: Sample-based arc length
                            Vector2 p0 = positions[i - 1];
                            Vector2 p1 = positions[i];
                            Vector2 t0 = GetTangent(positions, tangents, i - 1, interpolation);
                            Vector2 t1 = GetTangent(positions, tangents, i, interpolation);
                            
                            cumulativeDistance += ComputeHermiteArcLength(p0, t0, p1, t1);
                        }
                    }
                    
                    // Compute tangent based on mode
                    Vector2 tangent = GetTangent(positions, tangents, i, interpolation);
                    
                    waypoints[i] = new TrajectoryWaypoint
                    {
                        Position = positions[i],
                        Tangent = tangent,  // Now actually used!
                        DesiredSpeed = speeds?[i] ?? 10.0f,
                        CumulativeDistance = cumulativeDistance
                    };
                }
                
                var trajectory = new CustomTrajectory
                {
                    Id = id,
                    Waypoints = waypoints,
                    TotalLength = cumulativeDistance,
                    IsLooped = (byte)(looped ? 1 : 0),
                    Interpolation = interpolation
                };
                
                _trajectories[id] = trajectory;
                return id;
            }
        }
        
        /// <summary>
        /// Get trajectory by ID (read-only).
        /// </summary>
        public bool TryGetTrajectory(int id, out CustomTrajectory trajectory)
        {
            lock (_lock)
            {
                return _trajectories.TryGetValue(id, out trajectory);
            }
        }
        
        /// <summary>
        /// Sample trajectory at given progress distance.
        /// Returns (position, tangent, desired speed).
        /// Thread-safe for concurrent reads from different trajectories.
        /// </summary>
        /// <param name="id">Trajectory ID</param>
        /// <param name="progressS">Progress along trajectory (meters from start)</param>
        /// <returns>Sampled position, tangent direction, and desired speed</returns>
        public (Vector2 pos, Vector2 tangent, float speed) SampleTrajectory(int id, float progressS)
        {
            if (!TryGetTrajectory(id, out var traj))
            {
                return (Vector2.Zero, new Vector2(1, 0), 0f);
            }
            
            // Handle looping
            if (traj.IsLooped == 1)
            {
                progressS = progressS % traj.TotalLength;
                if (progressS < 0f) progressS += traj.TotalLength;
            }
            else
            {
                progressS = Math.Clamp(progressS, 0f, traj.TotalLength);
            }
            
            // Find segment containing progressS
            var waypoints = traj.Waypoints;
            for (int i = 1; i < waypoints.Length; i++)
            {
                if (waypoints[i].CumulativeDistance >= progressS)
                {
                    float segmentDist = waypoints[i].CumulativeDistance - waypoints[i - 1].CumulativeDistance;
                    float localProgress = progressS - waypoints[i - 1].CumulativeDistance;
                    float t = segmentDist > 0.001f ? localProgress / segmentDist : 0f;
                    
                    Vector2 pos, tangent;
                    float speed;
                    
                    // Interpolation based on mode
                    if (traj.Interpolation == TrajectoryInterpolation.Linear)
                    {
                        // LINEAR MODE (existing behavior)
                        pos = Vector2.Lerp(waypoints[i - 1].Position, waypoints[i].Position, t);
                        Vector2 segmentDir = waypoints[i].Position - waypoints[i - 1].Position;
                        tangent = segmentDir.LengthSquared() > 0.001f 
                            ? Vector2.Normalize(segmentDir) 
                            : new Vector2(1, 0);
                        speed = waypoints[i - 1].DesiredSpeed + 
                                (waypoints[i].DesiredSpeed - waypoints[i - 1].DesiredSpeed) * t;
                    }
                    else
                    {
                        // HERMITE MODE (new behavior)
                        Vector2 p0 = waypoints[i - 1].Position;
                        Vector2 t0 = waypoints[i - 1].Tangent;
                        Vector2 p1 = waypoints[i].Position;
                        Vector2 t1 = waypoints[i].Tangent;
                        
                        pos = EvaluateHermite(t, p0, t0, p1, t1);
                        tangent = Vector2.Normalize(EvaluateHermiteTangent(t, p0, t0, p1, t1));
                        speed = waypoints[i - 1].DesiredSpeed + 
                                (waypoints[i].DesiredSpeed - waypoints[i - 1].DesiredSpeed) * t;
                    }
                    
                    return (pos, tangent, speed);
                }
            }
            
            // End of trajectory (or exactly at end)
            var lastWp = waypoints[waypoints.Length - 1];
            Vector2 lastTangent = waypoints.Length > 1
                ? Vector2.Normalize(lastWp.Position - waypoints[waypoints.Length - 2].Position)
                : new Vector2(1, 0);
            
            return (lastWp.Position, lastTangent, lastWp.DesiredSpeed);
        }
        
        /// <summary>
        /// Get tangent for waypoint i based on interpolation mode.
        /// </summary>
        private Vector2 GetTangent(
            Vector2[] positions, 
            Vector2[]? tangents, 
            int i, 
            TrajectoryInterpolation interpolation)
        {
            switch (interpolation)
            {
                case TrajectoryInterpolation.Linear:
                    return Vector2.Zero;  // Not used
                
                case TrajectoryInterpolation.CatmullRom:
                    return ComputeCatmullRomTangent(positions, i);
                
                case TrajectoryInterpolation.HermiteExplicit:
                    return tangents![i];
                
                default:
                    return Vector2.Zero;
            }
        }
        
        /// <summary>
        /// Compute Catmull-Rom tangent at waypoint i.
        /// Uses finite difference: tangent = (p[i+1] - p[i-1]) / 2
        /// </summary>
        private Vector2 ComputeCatmullRomTangent(Vector2[] positions, int i)
        {
            int n = positions.Length;
            
            if (n < 2)
                return Vector2.Zero;
            
            // Special cases for endpoints
            if (i == 0)
            {
                // Start: Use forward difference
                return (positions[1] - positions[0]);
            }
            else if (i == n - 1)
            {
                // End: Use backward difference
                return (positions[n - 1] - positions[n - 2]);
            }
            else
            {
                // Middle: Central difference (Catmull-Rom formula)
                return (positions[i + 1] - positions[i - 1]) * 0.5f;
            }
        }
        
        /// <summary>
        /// Compute Hermite spline arc length via trapezoidal integration.
        /// Uses same algorithm as RoadNetworkBuilder for consistency.
        /// </summary>
        private float ComputeHermiteArcLength(Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            const int SAMPLES = 32;  // Trade-off between accuracy and speed
            float length = 0f;
            Vector2 prevPoint = EvaluateHermite(0f, p0, t0, p1, t1);
            
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
        /// Evaluate Hermite spline at parameter t.
        /// Copy of RoadGraphNavigator.EvaluateHermite for trajectory use.
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
        /// Evaluate Hermite tangent at parameter t.
        /// Copy of RoadGraphNavigator.EvaluateHermiteTangent.
        /// </summary>
        private Vector2 EvaluateHermiteTangent(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            float t2 = t * t;
            
            float dh00 = 6 * t2 - 6 * t;
            float dh10 = 3 * t2 - 4 * t + 1;
            float dh01 = -6 * t2 + 6 * t;
            float dh11 = 3 * t2 - 2 * t;
            
            return dh00 * p0 + dh10 * t0 + dh01 * p1 + dh11 * t1;
        }

        /// <summary>
        /// Remove trajectory from pool.
        /// </summary>
        public bool RemoveTrajectory(int id)
        {
            lock (_lock)
            {
                if (_trajectories.TryGetValue(id, out var traj))
                {
                    if (traj.Waypoints.IsCreated)
                        traj.Waypoints.Dispose();
                    
                    return _trajectories.Remove(id);
                }
                return false;
            }
        }
        
        /// <summary>
        /// Get total number of registered trajectories.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _trajectories.Count;
                }
            }
        }
        
        /// <summary>
        /// Cleanup all trajectories (call on shutdown).
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var traj in _trajectories.Values)
                {
                    if (traj.Waypoints.IsCreated)
                        traj.Waypoints.Dispose();
                }
                _trajectories.Clear();
            }
        }
    }
}
