# BATCH-CK-13: Hermite Spline Custom Trajectories

**Batch ID:** BATCH-CK-13  
**Phase:** Quality - Smooth Path Following  
**Priority:** MEDIUM (P2) - Enhances trajectory quality  
**Estimated Effort:** 1.0 day  
**Dependencies:** None  
**Starting Point:** Current main branch  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading & Workflow

**IMPORTANT: Read these documents before starting:**

### Developer Workflow
- **Workflow Guide**: `d:\Work\ModuleHost\.dev-workstream\README.md`
  - How to work, report, ask questions
  - Definition of Done
  - Communication standards

### Design & Architecture
- **Design Document**: `d:\Work\ModuleHost\docs\car-kinem-implementation-design.md`
  - Full architecture specification
  - Trajectory system design
  - Hermite spline mathematics

### Source Code Locations
- **CarKinem Core**: `Fdp.Examples.CarKinem\CarKinem\`
  - Trajectory: `CarKinem\Trajectory\TrajectoryPoolManager.cs`
  - Road: `CarKinem\Road\RoadGraphNavigator.cs` (reference for Hermite)
- **Tests**: `Fdp.Examples.CarKinem\CarKinem.Tests\`
- **Demo Application**: `Fdp.Examples.CarKinem\`

### Reporting Requirements
**When complete, submit:**
- **Report**: `d:\Work\ModuleHost\.dev-workstream\reports\BATCH-CK-13-REPORT.md`
  - Use template: `.dev-workstream\templates\BATCH-REPORT-TEMPLATE.md`
  - **MUST include**: Visual comparison screenshots, test results
- **Questions** (if needed): `.dev-workstream\reports\BATCH-CK-13-QUESTIONS.md`
- **Blockers** (if blocked): `.dev-workstream\reports\BATCH-CK-13-BLOCKERS.md`

---

## 📚 Context & Problem Statement

### The Issue

Custom trajectories currently use **linear interpolation** between waypoints, resulting in **sharp corners** and robotic-looking paths. The road network has full Cubic Hermite spline support with smooth curves, but custom trajectories don't benefit from this.

**Current Implementation (TrajectoryPoolManager.cs:53):**
```csharp
waypoints[i] = new TrajectoryWaypoint
{
    Position = positions[i],
    Tangent = Vector2.Zero, // Linear interpolation (can be enhanced to Hermite)
    DesiredSpeed = speeds?[i] ?? 10.0f,
    CumulativeDistance = cumulativeDistance
};
```

**Visual Comparison:**
```
LINEAR (Current):          HERMITE (Target):
    P1                         P1
   /|                         /  \
  / |                        /    \
 /  |                       /      \
P0  P2                     P0       P2
    ^--- Sharp corner          ^--- Smooth curve
```

### Impact

- **AI patrol paths** look unnatural (vehicles jerk at waypoints)
- **Racing circuits** lack smooth racing lines
- **Quality disparity**: Road following is smooth, custom paths are jagged
- **User experience**: Vehicles appear to "snap" between waypoints

### Design Specification

From `docs/car-kinem-implementation-design.md`:
> "Linear interpolation between waypoints **(can be enhanced to Hermite later)**"

**Road network already has Hermite support:**
- `RoadGraphNavigator.EvaluateHermite()` ✅
- `RoadGraphNavigator.EvaluateHermiteTangent()` ✅
- Arc-length parameterization with LUT ✅

**Goal:** Extend this to custom trajectories.

---

## 🎯 Goal

Implement Cubic Hermite spline interpolation for custom trajectories to provide smooth, natural-looking paths for AI vehicles.

### Success Criteria

✅ **Smooth curves between waypoints:**
```csharp
var positions = new[] {
    new Vector2(0, 0),
    new Vector2(100, 50),   // Will be smooth curve, not sharp corner
    new Vector2(200, 0)
};
int trajId = pool.RegisterTrajectory(positions, tangents: "auto");
```

✅ **Automatic tangent generation:**
```csharp
// Option 1: Catmull-Rom (auto-tangents)
RegisterTrajectory(positions, useCatmullRom: true);

// Option 2: Explicit tangents
var tangents = new[] { new Vector2(50, 0), new Vector2(50, 25), new Vector2(50, 0) };
RegisterTrajectory(positions, tangents);
```

✅ **Backward compatibility:**
```csharp
// Linear mode still available
RegisterTrajectory(positions, interpolation: TrajectoryInterpolation.Linear);
```

✅ **Visual quality matches road network smoothness**

---

## 📋 Implementation Tasks

### **Task 1: Add Interpolation Mode Enum** ⭐

**Objective:** Define interpolation modes for trajectory creation.

**File to Create:** `CarKinem/Trajectory/TrajectoryInterpolation.cs`

```csharp
namespace CarKinem.Trajectory
{
    /// <summary>
    /// Trajectory interpolation modes.
    /// </summary>
    public enum TrajectoryInterpolation : byte
    {
        /// <summary>
        /// Linear interpolation between waypoints (sharp corners).
        /// Fast and simple, but robotic-looking paths.
        /// </summary>
        Linear = 0,
        
        /// <summary>
        /// Cubic Hermite spline interpolation with automatic tangents (Catmull-Rom).
        /// Smooth curves passing through all waypoints.
        /// Tangents computed automatically from neighboring waypoints.
        /// </summary>
        CatmullRom = 1,
        
        /// <summary>
        /// Cubic Hermite spline interpolation with explicit tangents.
        /// Maximum control over curve shape.
        /// Requires user-provided tangent vectors.
        /// </summary>
        HermiteExplicit = 2
    }
}
```

**Deliverables:**
- [ ] Create `TrajectoryInterpolation.cs` enum
- [ ] Add XML documentation for each mode

---

### **Task 2: Update CustomTrajectory Struct** ⭐⭐

**Objective:** Store interpolation mode in trajectory.

**File to Modify:** `CarKinem/Trajectory/CustomTrajectory.cs`

**Add field:**
```csharp
public struct CustomTrajectory
{
    public int Id;
    public NativeArray<TrajectoryWaypoint> Waypoints;
    public float TotalLength;
    public byte IsLooped;
    public TrajectoryInterpolation Interpolation;  // NEW
}
```

**Deliverables:**
- [ ] Add `Interpolation` field to `CustomTrajectory`
- [ ] Update struct size documentation

---

### **Task 3: Implement Catmull-Rom Tangent Generation** ⭐⭐⭐

**Objective:** Auto-compute tangents from neighboring waypoints.

**File to Modify:** `CarKinem/Trajectory/TrajectoryPoolManager.cs`

**Add helper method:**
```csharp
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
```

**Deliverables:**
- [ ] Implement `ComputeCatmullRomTangent()` method
- [ ] Handle endpoint cases correctly
- [ ] Add unit test for tangent computation

---

### **Task 4: Update RegisterTrajectory Method** ⭐⭐⭐⭐

**Objective:** Add tangent/interpolation parameters to registration.

**File to Modify:** `CarKinem/Trajectory/TrajectoryPoolManager.cs`

**New signature:**
```csharp
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
            Interpolation = interpolation  // NEW
        };
        
        _trajectories[id] = trajectory;
        return id;
    }
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
```

**Deliverables:**
- [ ] Update `RegisterTrajectory()` signature
- [ ] Implement tangent computation based on mode
- [ ] Compute Hermite arc length for non-linear modes
- [ ] Update trajectory struct with interpolation mode

---

### **Task 5: Implement Hermite Arc Length Computation** ⭐⭐⭐

**Objective:** Compute accurate arc length for Hermite segments.

**File to Add:** `CarKinem/Trajectory/TrajectoryPoolManager.cs` (add method)

**Implementation (adapted from RoadNetworkBuilder):**
```csharp
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
```

**Deliverables:**
- [ ] Implement `ComputeHermiteArcLength()` method
- [ ] Add `EvaluateHermite()` helper (or reuse from RoadGraphNavigator)
- [ ] Add `EvaluateHermiteTangent()` helper

---

### **Task 6: Update SampleTrajectory for Hermite** ⭐⭐⭐⭐

**Objective:** Use Hermite evaluation when sampling trajectories.

**File to Modify:** `CarKinem/Trajectory/TrajectoryPoolManager.cs`

**Update SampleTrajectory():**
```csharp
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
    
    // End of trajectory
    var lastWp = waypoints[waypoints.Length - 1];
    Vector2 lastTangent = waypoints.Length > 1
        ? Vector2.Normalize(lastWp.Position - waypoints[waypoints.Length - 2].Position)
        : new Vector2(1, 0);
    
    return (lastWp.Position, lastTangent, lastWp.DesiredSpeed);
}
```

**Deliverables:**
- [ ] Update `SampleTrajectory()` to use Hermite evaluation
- [ ] Maintain backward compatibility with linear mode
- [ ] Normalize tangent vectors

---

## 🧪 Testing Strategy

### **Task 7: Unit Tests - Catmull-Rom Tangents** ⭐⭐⭐

**File to Create:** `CarKinem.Tests/Trajectory/HermiteTrajectoryTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.Trajectory
{
    public class HermiteTrajectoryTests
    {
        [Fact]
        public void CatmullRomTangent_MiddlePoint_UsesCentralDifference()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(50, 50),   // Middle point
                new Vector2(100, 0)
            };
            
            int trajId = pool.RegisterTrajectory(
                positions, 
                interpolation: TrajectoryInterpolation.CatmullRom
            );
            
            Assert.True(pool.TryGetTrajectory(trajId, out var traj));
            
            // Middle tangent should be (p2 - p0) / 2 = ((100,0) - (0,0)) / 2 = (50, 0)
            Vector2 middleTangent = traj.Waypoints[1].Tangent;
            Assert.Equal(50f, middleTangent.X, 1);
            Assert.Equal(0f, middleTangent.Y, 1);
            
            pool.Dispose();
        }
        
        [Fact]
        public void HermiteTrajectory_SmoothCurve_NoSharpCorners()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(50, 50),   // Should be smooth curve, not sharp
                new Vector2(100, 0)
            };
            
            int trajId = pool.RegisterTrajectory(
                positions, 
                interpolation: TrajectoryInterpolation.CatmullRom
            );
            
            // Sample before waypoint 1
            var (pos0, tan0, _) = pool.SampleTrajectory(trajId, 20f);
            
            // Sample after waypoint 1
            var (pos2, tan2, _) = pool.SampleTrajectory(trajId, 80f);
            
            // Tangents should be continuous (no sharp angle)
            float angleDiff = Vector2.Dot(Vector2.Normalize(tan0), Vector2.Normalize(tan2));
            Assert.True(angleDiff > 0.5f, 
                $"Sharp corner detected: angle cosine {angleDiff} < 0.5");
            
            pool.Dispose();
        }
        
        [Fact]
        public void LinearTrajectory_BackwardCompatible()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            // Default = Linear (backward compat)
            int trajId = pool.RegisterTrajectory(positions);
            
            // Sample midpoint (should be exactly (50, 0))
            var (pos, tan, _) = pool.SampleTrajectory(trajId, 50f);
            
            Assert.Equal(50f, pos.X, 1);
            Assert.Equal(0f, pos.Y, 1);
            Assert.Equal(1f, tan.X, 0.01f);
            Assert.Equal(0f, tan.Y, 0.01f);
            
            pool.Dispose();
        }
        
        [Fact]
        public void HermiteExplicit_UsesProvidedTangents()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            var tangents = new[]
            {
                new Vector2(0, 50),    // Curved upward at start
                new Vector2(0, -50)    // Curved downward at end
            };
            
            int trajId = pool.RegisterTrajectory(
                positions, 
                interpolation: TrajectoryInterpolation.HermiteExplicit,
                tangents: tangents
            );
            
            Assert.True(pool.TryGetTrajectory(trajId, out var traj));
            Assert.Equal(TrajectoryInterpolation.HermiteExplicit, traj.Interpolation);
            Assert.Equal(new Vector2(0, 50), traj.Waypoints[0].Tangent);
            Assert.Equal(new Vector2(0, -50), traj.Waypoints[1].Tangent);
            
            pool.Dispose();
        }
    }
}
```

**Deliverables:**
- [ ] Create `HermiteTrajectoryTests.cs` with 4+ tests
- [ ] Test Catmull-Rom tangent computation
- [ ] Test smooth curve property
- [ ] Test linear backward compatibility
- [ ] Test explicit tangent mode

---

### **Task 8: Visual Test in Demo** ⭐⭐

**Objective:** Add UI option to toggle interpolation modes.

**File to Modify:** `Fdp.Examples.CarKinem/UI/SpawnControlsPanel.cs` (or similar)

**Add toggle:**
```csharp
// In UI rendering code:
ImGui.Text("Trajectory Interpolation:");
ImGui.RadioButton("Linear", ref _interpolationMode, 0);
ImGui.RadioButton("Catmull-Rom (Smooth)", ref _interpolationMode, 1);

// When spawning vehicles with custom paths:
var interpolation = _interpolationMode == 0 
    ? TrajectoryInterpolation.Linear 
    : TrajectoryInterpolation.CatmullRom;

int trajId = trajectoryPool.RegisterTrajectory(
    waypointPositions, 
    interpolation: interpolation
);
```

**Deliverables:**
- [ ] Add UI toggle for interpolation mode
- [ ] Visualize difference between linear and smooth
- [ ] Add to demo controls panel

---

## ✅ Validation Criteria

### Build Verification
```powershell
dotnet build CarKinem/CarKinem.csproj --nologo
# Expected: Build succeeded. 0 Warning(s)
```

### Test Verification
```powershell
dotnet test CarKinem.Tests/CarKinem.Tests.csproj --filter "Hermite" --nologo
# Expected: All tests passed (4+ new tests)
```

### Visual Verification (Manual)
```
1. Run Fdp.Examples.CarKinem
2. Create custom trajectory with waypoints
3. Toggle between Linear and Catmull-Rom modes
4. Observe: Catmull-Rom paths should be smooth, no sharp corners
```

---

## 🎓 Developer Notes

### Catmull-Rom vs. Hermite

**Catmull-Rom** is a special case of Hermite splines with auto-computed tangents:
- **Tangent formula:** `T[i] = (P[i+1] - P[i-1]) / 2`
- **Passes through all waypoints** (interpolating, not approximating)
- **C1 continuous** (smooth velocity)
- **Local control:** Moving one waypoint only affects neighboring segments

### Arc Length Accuracy

**Sampling resolution:** 32 samples per segment (same as RoadNetworkBuilder)
- **Accuracy:** Within 1-2% of analytical length (acceptable for game use)
- **Performance:** ~1 μs per segment on modern CPU

### Performance Impact

**Registration (cold path):**
- Hermite: ~10x slower than linear (tangent computation + arc length)
- **Acceptable:** Registration happens once, not every frame

**Sampling (hot path):**
- Hermite: ~2x slower than linear (polynomial evaluation)
- **Acceptable:** Still sub-microsecond per sample

### Backward Compatibility

**Default behavior preserved:** `RegisterTrajectory(positions)` still uses Linear mode

**Migration path:**
```csharp
// Old code (still works):
int trajId = pool.RegisterTrajectory(positions);

// New code (opt-in):
int trajId = pool.RegisterTrajectory(positions, 
    interpolation: TrajectoryInterpolation.CatmullRom);
```

---

## 🚀 Completion Checklist

### Implementation
- [ ] Task 1: Add TrajectoryInterpolation enum
- [ ] Task 2: Update CustomTrajectory struct
- [ ] Task 3: Implement Catmull-Rom tangents
- [ ] Task 4: Update RegisterTrajectory method
- [ ] Task 5: Implement Hermite arc length
- [ ] Task 6: Update SampleTrajectory for Hermite

### Testing
- [ ] Task 7: Hermite trajectory unit tests (4+ tests)
- [ ] Task 8: Visual test in demo

### Final Validation
- [ ] Build clean (0 warnings)
- [ ] All tests pass (99+ tests total)
- [ ] Visual demo shows smooth curves
- [ ] Backward compatibility verified

---

## 📊 Success Metrics

**Before BATCH-CK-13:**
```
📐 Linear interpolation only
🔲 Sharp corners at waypoints
⚠️ Quality disparity vs. road network
```

**After BATCH-CK-13:**
```
✅ Hermite spline support
🎨 Smooth, natural curves
✨ Quality parity with road network
🔄 Backward compatible (linear still available)
```

**Visual Comparison:**
```
LINEAR:              HERMITE (Catmull-Rom):
  P1                      P1
 /|                      ╱  ╲
P0 P2                   P0   P2
   ^-Sharp              ^-Smooth
```

---

**END OF BATCH-CK-13 INSTRUCTIONS**
