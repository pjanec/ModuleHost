# BATCH-CK-03: Trajectory Pool System

**Batch ID:** BATCH-CK-03  
**Phase:** Trajectory  
**Prerequisites:** BATCH-CK-01 (Core Data Structures) COMPLETE  
**Assigned:** 2026-01-06  

---

## 📋 Objectives

Implement the custom trajectory storage and sampling system:
1. TrajectoryPoolManager singleton for trajectory registration
2. Trajectory waypoint interpolation (linear)
3. Looping trajectory support
4. Distance-based sampling with arc-length parameterization
5. Thread-safe read access for parallel systems

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Trajectory Section:** Lines 383-522 in design doc

---

## 📁 Project Structure

Add to existing `CarKinem` project:

```
D:\WORK\ModuleHost\CarKinem\
└── Trajectory\
    ├── TrajectoryWaypoint.cs      ← EXISTS (from CK-01)
    ├── CustomTrajectory.cs         ← EXISTS (from CK-01)
    └── TrajectoryPoolManager.cs    ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Trajectory\
    ├── TrajectoryPoolTests.cs      ← NEW
    └── TrajectoryInterpolationTests.cs ← NEW
```

---

## 🎯 Tasks

### Task CK-03-01: TrajectoryPoolManager Implementation

**File:** `CarKinem/Trajectory/TrajectoryPoolManager.cs`

Implement singleton manager for trajectory lifecycle (design doc lines 407-520):

```csharp
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
                    
                    float speed = MathF.Lerp(waypoints[i - 1].DesiredSpeed, waypoints[i].DesiredSpeed, t);
                    
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
```

---

### Task CK-03-02: Registration Tests

**File:** `CarKinem.Tests/Trajectory/TrajectoryPoolTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.Trajectory
{
    public class TrajectoryPoolTests
    {
        [Fact]
        public void RegisterTrajectory_ValidInput_ReturnsId()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),
                new Vector2(100, 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            
            Assert.True(id > 0, "Should return positive ID");
        }
        
        [Fact]
        public void RegisterTrajectory_MultipleTrajectories_HaveUniqueIds()
        {
            using var pool = new TrajectoryPoolManager();
            
            var path1 = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var path2 = new[] { new Vector2(20, 0), new Vector2(30, 0) };
            
            int id1 = pool.RegisterTrajectory(path1);
            int id2 = pool.RegisterTrajectory(path2);
            
            Assert.NotEqual(id1, id2);
        }
        
        [Fact]
        public void RegisterTrajectory_SingleWaypoint_ThrowsException()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0) };
            
            Assert.Throws<ArgumentException>(() => pool.RegisterTrajectory(positions));
        }
        
        [Fact]
        public void RegisterTrajectory_MismatchedSpeedsLength_ThrowsException()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var speeds = new[] { 10f }; // Too short
            
            Assert.Throws<ArgumentException>(() => pool.RegisterTrajectory(positions, speeds));
        }
        
        [Fact]
        public void TryGetTrajectory_ValidId_ReturnsTrue()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            int id = pool.RegisterTrajectory(positions);
            
            bool found = pool.TryGetTrajectory(id, out var traj);
            
            Assert.True(found);
            Assert.Equal(id, traj.Id);
        }
        
        [Fact]
        public void TryGetTrajectory_InvalidId_ReturnsFalse()
        {
            using var pool = new TrajectoryPoolManager();
            
            bool found = pool.TryGetTrajectory(999, out _);
            
            Assert.False(found);
        }
        
        [Fact]
        public void RemoveTrajectory_ExistingId_ReturnsTrue()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            int id = pool.RegisterTrajectory(positions);
            
            bool removed = pool.RemoveTrajectory(id);
            
            Assert.True(removed);
            Assert.False(pool.TryGetTrajectory(id, out _));
        }
        
        [Fact]
        public void Dispose_ReleasesAllTrajectories()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            pool.RegisterTrajectory(positions);
            pool.RegisterTrajectory(positions);
            
            Assert.Equal(2, pool.Count);
            
            pool.Dispose();
            
            // After dispose, pool should be empty
            Assert.Equal(0, pool.Count);
        }
    }
}
```

---

### Task CK-03-03: Interpolation Tests

**File:** `CarKinem.Tests/Trajectory/TrajectoryInterpolationTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.Trajectory
{
    public class TrajectoryInterpolationTests
    {
        [Fact]
        public void SampleTrajectory_AtStart_ReturnsFirstWaypoint()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 0f);
            
            Assert.Equal(new Vector2(0, 0), pos);
            Assert.Equal(10f, speed); // Default speed
        }
        
        [Fact]
        public void SampleTrajectory_AtEnd_ReturnsLastWaypoint()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 1000f); // Beyond end
            
            Assert.Equal(new Vector2(100, 0), pos);
        }
        
        [Fact]
        public void SampleTrajectory_Midpoint_InterpolatesPosition()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // Midpoint
            
            // Should be halfway
            Assert.Equal(50f, pos.X, precision: 1);
            Assert.Equal(0f, pos.Y, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_TangentVector_PointsForward()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),
                new Vector2(100, 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // First segment
            
            // Tangent should point right (positive X)
            Assert.True(tangent.X > 0.9f, "Should point right");
            Assert.Equal(1f, tangent.Length(), precision: 3); // Should be normalized
        }
        
        [Fact]
        public void SampleTrajectory_WithCustomSpeeds_InterpolatesSpeed()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            var speeds = new[] { 5f, 15f };
            
            int id = pool.RegisterTrajectory(positions, speeds);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // Midpoint
            
            // Speed should be halfway between 5 and 15
            Assert.Equal(10f, speed, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_LoopedTrajectory_WrapsAround()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions, looped: true);
            
            // Sample beyond total length (100m)
            var (pos1, _, _) = pool.SampleTrajectory(id, progressS: 0f);
            var (pos2, _, _) = pool.SampleTrajectory(id, progressS: 100f); // Should wrap to start
            
            Assert.Equal(pos1.X, pos2.X, precision: 1);
            Assert.Equal(pos1.Y, pos2.Y, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_InvalidId_ReturnsDefault()
        {
            using var pool = new TrajectoryPoolManager();
            
            var (pos, tangent, speed) = pool.SampleTrajectory(999, progressS: 0f);
            
            Assert.Equal(Vector2.Zero, pos);
            Assert.Equal(0f, speed);
        }
        
        [Fact]
        public void SampleTrajectory_MultiSegment_CorrectCumulativeDistance()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),   // 100m from start
                new Vector2(100, 100)  // 200m from start (100 + 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            
            // Sample at 150m (should be in second segment, halfway)
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 150f);
            
            Assert.Equal(100f, pos.X, precision: 1);
            Assert.Equal(50f, pos.Y, precision: 1); // Halfway up
        }
    }
}
```

---

## ✅ Acceptance Criteria

### Build & Quality
- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 16 unit tests (8+ per test file)
- [ ] XML documentation on all public methods

### Functionality
- [ ] Trajectory registration assigns unique IDs
- [ ] Cumulative distance correctly calculated
- [ ] Linear interpolation between waypoints works
- [ ] Looping trajectories wrap around correctly
- [ ] Custom speeds interpolate correctly
- [ ] Thread-safe for concurrent reads
- [ ] Dispose releases all NativeArray resources

### Code Quality
- [ ] Lock used correctly for thread safety
- [ ] Proper validation of input parameters
- [ ] No allocations in SampleTrajectory (hot path)
- [ ] Handles edge cases (start, end, beyond bounds)
- [ ] Tangent vectors are normalized

### Test Quality
- [ ] Tests cover:
  - Happy path (valid registration and sampling)
  - Edge cases (start, end, midpoint, beyond bounds)
  - Looping behavior
  - Speed interpolation
  - Multi-segment trajectories
  - Invalid inputs (exceptions)
  - Thread safety (bonus: concurrent access test)
  - Dispose pattern

---

## 📤 Submission Instructions

Submit your report to:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-03-REPORT.md
```

Include:
- Test results (all 16+ tests passing)
- Any performance observations
- Memory management validation (no leaks)
- Questions for review

---

## 📚 Reference Materials

- **Design Doc (Trajectory Pool):** Lines 383-522
- **TrajectoryPoolManager:** Lines 407-520
- **JSON Integration (future):** Note that trajectory manager will also support JSON loading in later batches

---

**Time Estimate:** 3-4 hours

**Focus:** Thread safety, memory management, interpolation accuracy.

---

_Batch prepared by: Development Lead_  
_Date: 2026-01-06 23:39_
