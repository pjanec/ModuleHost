# BATCH-CK-05: Road Graph Navigation State Machine

**Batch ID:** BATCH-CK-05  
**Phase:** Road Navigation  
**Prerequisites:** 
- BATCH-CK-01 (NavState, RoadGraphPhase) COMPLETE
- BATCH-CK-02 (VectorMath) COMPLETE
- BATCH-CK-04 (Road network data) COMPLETE  
**Assigned:** 2026-01-07  

---

## 📋 Objectives

Implement the three-phase road graph navigation state machine:
1. RoadGraphNavigator helper class
2. Find closest point on road network (spatial hash lookup)
3. Hermite spline evaluation and sampling
4. State machine logic (Approaching → Following → Leaving → Arrived)
5. Progress tracking and phase transitions

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Road Navigation Section:** Lines 862-1054 in design doc

---

## 📁 Project Structure

Add to existing `CarKinem` project:

```
D:\WORK\ModuleHost\CarKinem\
└── Road\
    ├── RoadNetworkBlob.cs          ← EXISTS (from CK-01)
    ├── RoadSegment.cs              ← EXISTS (from CK-01)
    ├── RoadNetworkBuilder.cs       ← EXISTS (from CK-04)
    └── RoadGraphNavigator.cs       ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Road\
    ├── RoadGraphNavigatorTests.cs  ← NEW
    └── HermiteEvaluationTests.cs   ← NEW
```

---

## 🎯 Tasks

### Task CK-05-01: RoadGraphNavigator Implementation

**File:** `CarKinem/Road/RoadGraphNavigator.cs`

Implement navigation helper with state machine (design doc lines 865-1054):

```csharp
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
            
            // Search current cell and adjacent cells (3×3 grid)
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
        /// Evaluate Hermite spline at parameter t ∈ [0,1].
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
            
            // Use LUT to convert distance → parameter t
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
```

---

### Task CK-05-02: Hermite Evaluation Tests

**File:** `CarKinem.Tests/Road/HermiteEvaluationTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class HermiteEvaluationTests
    {
        [Fact]
        public void EvaluateHermite_AtT0_ReturnsP0()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 result = RoadGraphNavigator.EvaluateHermite(0f, p0, t0, p1, t1);
            
            Assert.Equal(p0, result);
        }
        
        [Fact]
        public void EvaluateHermite_AtT1_ReturnsP1()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 result = RoadGraphNavigator.EvaluateHermite(1f, p0, t0, p1, t1);
            
            Assert.Equal(p1.X, result.X, precision: 2);
            Assert.Equal(p1.Y, result.Y, precision: 2);
        }
        
        [Fact]
        public void EvaluateHermite_StraightLine_InterpolatesLinearly()
        {
            // Straight horizontal segment
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 midpoint = RoadGraphNavigator.EvaluateHermite(0.5f, p0, t0, p1, t1);
            
            // Should be approximately halfway
            Assert.Equal(50f, midpoint.X, precision: 1);
            Assert.Equal(0f, midpoint.Y, precision: 1);
        }
        
        [Fact]
        public void EvaluateHermiteTangent_StraightLine_PointsForward()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 tangent = RoadGraphNavigator.EvaluateHermiteTangent(0.5f, p0, t0, p1, t1);
            Vector2 normalized = Vector2.Normalize(tangent);
            
            // Should point right (positive X)
            Assert.True(normalized.X > 0.9f);
        }
    }
}
```

---

### Task CK-05-03: Navigation State Machine Tests

**File:** `CarKinem.Tests/Road/RoadGraphNavigatorTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class RoadGraphNavigatorTests
    {
        [Fact]
        public void FindClosestRoadPoint_FindsNearestSegment()
        {
            // Build simple road network
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            // Point near the road
            Vector2 testPoint = new Vector2(50, 5); // 5m above midpoint
            
            var (segId, nearestPoint, dist) = RoadGraphNavigator.FindClosestRoadPoint(testPoint, blob);
            
            Assert.Equal(0, segId);
            Assert.True(dist < 10f, "Should find point within 10m");
            
            blob.Dispose();
        }
        
        [Fact]
        public void SampleRoadSegment_UsesDistanceLUT()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            ref readonly var segment = ref blob.Segments[0];
            
            // Sample at midpoint
            var (pos, tangent, speed) = RoadGraphNavigator.SampleRoadSegment(segment, segment.Length / 2);
            
            // Position should be approximately halfway
            Assert.InRange(pos.X, 40f, 60f);
            Assert.Equal(1f, tangent.Length(), precision: 2); // Normalized
            Assert.Equal(segment.SpeedLimit, speed);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Approaching_TransitionsToFollowing()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Approaching,
                FinalDestination = new Vector2(200, 0),
                ArrivalRadius = 2.0f
            };
            
            // Position very close to road
            Vector2 currentPos = new Vector2(1, 0.5f);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should have transitioned to Following
            Assert.Equal(RoadGraphPhase.Following, nav.RoadPhase);
            Assert.Equal(0, nav.CurrentSegmentId);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Following_ReturnsRoadTarget()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Following,
                CurrentSegmentId = 0,
                ProgressS = 25f,
                FinalDestination = new Vector2(200, 0),
                ArrivalRadius = 2.0f
            };
            
            Vector2 currentPos = new Vector2(25, 0);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should get target ahead on road
            Assert.True(targetHeading.X > 0.9f, "Should point forward");
            Assert.Equal(blob.Segments[0].SpeedLimit, targetSpeed);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Leaving_GoesToDestination()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Leaving,
                FinalDestination = new Vector2(110, 10),
                ArrivalRadius = 2.0f
            };
            
            Vector2 currentPos = new Vector2(100, 5);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should target final destination directly
            Assert.Equal(nav.FinalDestination, targetPos);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Arrived_StopsAtDestination()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Leaving,
                FinalDestination = new Vector2(101, 0),
                ArrivalRadius = 2.0f
            };
            
            Vector2 currentPos = new Vector2(100.5f, 0);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should have arrived
            Assert.Equal(RoadGraphPhase.Arrived, nav.RoadPhase);
            Assert.Equal(1, nav.HasArrived);
            Assert.Equal(0f, targetSpeed);
            
            blob.Dispose();
        }
    }
}
```

---

## ✅ Acceptance Criteria

### Build & Quality
- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 10 unit tests
- [ ] XML documentation on all public methods

### Functionality
- [ ] Hermite evaluation at t=0 returns P0, at t=1 returns P1
- [ ] Hermite tangent points in correct direction
- [ ] FindClosestRoadPoint uses spatial grid correctly
- [ ] SampleRoadSegment uses distance LUT for arc-length
- [ ] State machine transitions correctly:
  - Approaching → Following (when within 2m)
  - Following → Leaving (when close to destination)
  - Leaving → Arrived (when within arrival radius)
- [ ] Tangent vectors are normalized

### Code Quality
- [ ] Static helper class (no state)
- [ ] Unsafe code only where necessary (LUT access)
- [ ] Proper bounds checking and fallbacks
- [ ] No allocations in hot path methods

### Test Quality
- [ ] Tests cover:
  - Hermite evaluation (boundary values, interpolation)
  - Closest point finding (spatial lookup)
  - Segment sampling (LUT usage, tangent normalization)
  - State machine transitions (all phases)
  - Arrival detection
  - Edge cases (invalid segment ID, zero-length tangents)

---

## 📤 Submission Instructions

Submit your report to:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-05-REPORT.md
```

Include:
- Test results (all 10+ tests passing)
- State machine transition validation
- Any observations on Hermite accuracy
- Questions for review

---

## 📚 Reference Materials

- **Design Doc (Road Navigation):** Lines 862-1054
- **Hermite Splines:** Cubic Hermite interpolation formulas
- **State Machine:** Three-phase approach/follow/leave logic

---

**Time Estimate:** 4-5 hours

**Focus:** State machine correctness, Hermite math accuracy, spatial lookup efficiency.

---

_Batch prepared by: Development Lead_  
_Date: 2026-01-07 00:01_
