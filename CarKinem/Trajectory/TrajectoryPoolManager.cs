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
        /// Register a new custom trajectory.
        /// Returns trajectory ID for reference in commands.
        /// </summary>
        /// <param name="positions">Waypoint positions</param>
        /// <param name="speeds">Desired speeds at each waypoint (optional, defaults to 10 m/s)</param>
        /// <param name="looped">True if trajectory loops back to start</param>
        /// <returns>Unique trajectory ID</returns>
        public int RegisterTrajectory(Vector2[] positions, float[] speeds = null, bool looped = false)
        {
            if (positions == null || positions.Length < 2)
                throw new ArgumentException("Trajectory must have at least 2 waypoints", nameof(positions));
            
            if (speeds != null && speeds.Length != positions.Length)
                throw new ArgumentException("Speeds array must match positions length", nameof(speeds));
            
            lock (_lock)
            {
                int id = _nextId++;
                
                // Precompute waypoints with cumulative distance
                var waypoints = new NativeArray<TrajectoryWaypoint>(positions.Length, Allocator.Persistent);
                float cumulativeDistance = 0f;
                
                for (int i = 0; i < positions.Length; i++)
                {
                    if (i > 0)
                    {
                        cumulativeDistance += Vector2.Distance(positions[i - 1], positions[i]);
                    }
                    
                    waypoints[i] = new TrajectoryWaypoint
                    {
                        Position = positions[i],
                        Tangent = Vector2.Zero, // Linear interpolation (can be enhanced to Hermite)
                        DesiredSpeed = speeds?[i] ?? 10.0f,
                        CumulativeDistance = cumulativeDistance
                    };
                }
                
                var trajectory = new CustomTrajectory
                {
                    Id = id,
                    Waypoints = waypoints,
                    TotalLength = cumulativeDistance,
                    IsLooped = (byte)(looped ? 1 : 0)
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
                    // Interpolate between waypoints[i-1] and waypoints[i]
                    float segmentDist = waypoints[i].CumulativeDistance - waypoints[i - 1].CumulativeDistance;
                    float localProgress = progressS - waypoints[i - 1].CumulativeDistance;
                    float t = segmentDist > 0.001f ? localProgress / segmentDist : 0f;
                    
                    Vector2 pos = Vector2.Lerp(waypoints[i - 1].Position, waypoints[i].Position, t);
                    Vector2 tangent = waypoints[i].Position - waypoints[i - 1].Position;
                    if (tangent.LengthSquared() > 0.001f)
                        tangent = Vector2.Normalize(tangent);
                    else
                        tangent = new Vector2(1, 0);
                    
                    float speed = waypoints[i - 1].DesiredSpeed + (waypoints[i].DesiredSpeed - waypoints[i - 1].DesiredSpeed) * t;
                    
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
