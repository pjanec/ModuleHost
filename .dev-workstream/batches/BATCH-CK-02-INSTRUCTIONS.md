# BATCH-CK-02: Math & Control Algorithms

**Batch ID:** BATCH-CK-02  
**Phase:** Foundation  
**Prerequisites:** BATCH-CK-01 (Core Data Structures) COMPLETE  
**Assigned:** 2026-01-06  

---

## 📋 Objectives

Implement the core mathematical helpers and control algorithms that power vehicle behavior:
1. Vector math utilities for 2D navigation
2. Pure Pursuit steering controller (geometric path following)
3. P-controller for speed regulation
4. Bicycle kinematic model integration
5. RVO-Lite collision avoidance algorithm

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Algorithms Section:** Lines 323-733 in design doc

---

## 📁 Project Structure

Add to existing `CarKinem` project:

```
D:\WORK\ModuleHost\CarKinem\
├── Core\
│   └── VectorMath.cs          ← NEW
├── Controllers\
│   ├── PurePursuitController.cs   ← NEW
│   ├── SpeedController.cs         ← NEW
│   └── BicycleModel.cs            ← NEW
└── Avoidance\
    └── RVOAvoidance.cs        ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Algorithms\
    ├── VectorMathTests.cs     ← NEW
    ├── PurePursuitTests.cs    ← NEW
    ├── SpeedControllerTests.cs← NEW
    ├── BicycleModelTests.cs   ← NEW
    └── RVOAvoidanceTests.cs   ← NEW
```

---

## 🎯 Tasks

### Task CK-02-01: Vector Math Utilities

**File:** `CarKinem/Core/VectorMath.cs`

Implement essential 2D vector operations (design doc lines 326-341):

```csharp
using System;
using System.Numerics;

namespace CarKinem.Core
{
    /// <summary>
    /// 2D vector math utilities for vehicle navigation.
    /// </summary>
    public static class VectorMath
    {
        /// <summary>
        /// Calculate signed angle from vector 'from' to vector 'to' (radians).
        /// Returns positive for counter-clockwise, negative for clockwise.
        /// Range: [-PI, PI]
        /// </summary>
        public static float SignedAngle(Vector2 from, Vector2 to)
        {
            float dot = Vector2.Dot(from, to);
            float det = from.X * to.Y - from.Y * to.X;
            return MathF.Atan2(det, dot);
        }
        
        /// <summary>
        /// Rotate vector by angle (radians).
        /// </summary>
        public static Vector2 Rotate(Vector2 v, float angleRad)
        {
            float cos = MathF.Cos(angleRad);
            float sin = MathF.Sin(angleRad);
            return new Vector2(
                v.X * cos - v.Y * sin,
                v.X * sin + v.Y * cos
            );
        }
        
        /// <summary>
        /// Get perpendicular vector (90° counter-clockwise).
        /// </summary>
        public static Vector2 Perpendicular(Vector2 v)
        {
            return new Vector2(-v.Y, v.X);
        }
        
        /// <summary>
        /// Get right vector (90° clockwise).
        /// </summary>
        public static Vector2 Right(Vector2 forward)
        {
            return new Vector2(forward.Y, -forward.X);
        }
        
        /// <summary>
        /// Safe normalize with fallback for zero-length vectors.
        /// </summary>
        public static Vector2 SafeNormalize(Vector2 v, Vector2 fallback)
        {
            float lengthSq = v.LengthSquared();
            return lengthSq > 1e-6f ? Vector2.Normalize(v) : fallback;
        }
    }
}
```

**Verification Tests:**

```csharp
[Fact]
public void SignedAngle_ForwardToRight_ReturnsNegativePiOver2()
{
    Vector2 forward = new Vector2(1, 0);
    Vector2 right = new Vector2(0, -1);
    
    float angle = VectorMath.SignedAngle(forward, right);
    
    Assert.Equal(-MathF.PI / 2, angle, precision: 4);
}

[Fact]
public void SignedAngle_ForwardToLeft_ReturnsPositivePiOver2()
{
    Vector2 forward = new Vector2(1, 0);
    Vector2 left = new Vector2(0, 1);
    
    float angle = VectorMath.SignedAngle(forward, left);
    
    Assert.Equal(MathF.PI / 2, angle, precision: 4);
}

[Fact]
public void Rotate_Vector_By90Degrees()
{
    Vector2 v = new Vector2(1, 0);
    Vector2 rotated = VectorMath.Rotate(v, MathF.PI / 2);
    
    Assert.Equal(0f, rotated.X, precision: 4);
    Assert.Equal(1f, rotated.Y, precision: 4);
}

[Fact]
public void SafeNormalize_ZeroVector_ReturnsFallback()
{
    Vector2 zero = Vector2.Zero;
    Vector2 fallback = new Vector2(1, 0);
    
    Vector2 result = VectorMath.SafeNormalize(zero, fallback);
    
    Assert.Equal(fallback, result);
}
```

---

### Task CK-02-02: Pure Pursuit Steering Controller

**File:** `CarKinem/Controllers/PurePursuitController.cs`

Implement geometric steering controller (design doc lines 343-379):

```csharp
using System;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Controllers
{
    /// <summary>
    /// Pure Pursuit steering controller.
    /// Geometric path-following using lookahead point.
    /// </summary>
    public static class PurePursuitController
    {
        /// <summary>
        /// Calculate steering angle for Pure Pursuit.
        /// </summary>
        /// <param name="currentPos">Current vehicle position</param>
        /// <param name="currentForward">Current heading (normalized)</param>
        /// <param name="desiredVelocity">Desired velocity vector</param>
        /// <param name="currentSpeed">Current speed (m/s)</param>
        /// <param name="wheelBase">Distance between axles (meters)</param>
        /// <param name="lookaheadMin">Minimum lookahead distance (meters)</param>
        /// <param name="lookaheadMax">Maximum lookahead distance (meters)</param>
        /// <param name="maxSteerAngle">Maximum steering angle (radians)</param>
        /// <returns>Steering angle (radians)</returns>
        public static float CalculateSteering(
            Vector2 currentPos,
            Vector2 currentForward,
            Vector2 desiredVelocity,
            float currentSpeed,
            float wheelBase,
            float lookaheadMin,
            float lookaheadMax,
            float maxSteerAngle)
        {
            // 1. Calculate dynamic lookahead distance
            float lookaheadTime = 0.5f; // Default: 0.5s
            float lookaheadDist = MathF.Max(
                lookaheadMin,
                MathF.Min(lookaheadMax, currentSpeed * lookaheadTime)
            );
            
            // 2. Calculate lookahead point
            Vector2 lookaheadPoint;
            if (desiredVelocity.LengthSquared() < 0.01f)
            {
                // Stopped: maintain heading
                lookaheadPoint = currentPos + currentForward * lookaheadDist;
            }
            else
            {
                // Moving: follow desired velocity direction
                Vector2 desiredDir = Vector2.Normalize(desiredVelocity);
                lookaheadPoint = currentPos + desiredDir * lookaheadDist;
            }
            
            // 3. Calculate signed angle to lookahead point
            Vector2 toLookahead = lookaheadPoint - currentPos;
            float alpha = VectorMath.SignedAngle(currentForward, toLookahead);
            
            // 4. Compute curvature (bicycle model)
            float kappa = (2.0f * MathF.Sin(alpha)) / lookaheadDist;
            
            // 5. Convert curvature to steering angle
            float steerAngle = MathF.Atan(kappa * wheelBase);
            
            // 6. Clamp to vehicle limits
            return Math.Clamp(steerAngle, -maxSteerAngle, maxSteerAngle);
        }
    }
}
```

**Verification Tests:**

```csharp
[Fact]
public void PurePursuit_StraightPath_ReturnsZeroSteering()
{
    Vector2 pos = Vector2.Zero;
    Vector2 forward = new Vector2(1, 0);
    Vector2 desiredVel = new Vector2(10, 0); // Straight ahead
    
    float steer = PurePursuitController.CalculateSteering(
        pos, forward, desiredVel,
        currentSpeed: 10f,
        wheelBase: 2.7f,
        lookaheadMin: 2f,
        lookaheadMax: 10f,
        maxSteerAngle: 0.6f
    );
    
    Assert.Equal(0f, steer, precision: 3);
}

[Fact]
public void PurePursuit_LeftTurn_ReturnsPositiveSteering()
{
    Vector2 pos = Vector2.Zero;
    Vector2 forward = new Vector2(1, 0);
    Vector2 desiredVel = new Vector2(10, 10); // 45° left
    
    float steer = PurePursuitController.CalculateSteering(
        pos, forward, desiredVel,
        currentSpeed: 10f,
        wheelBase: 2.7f,
        lookaheadMin: 2f,
        lookaheadMax: 10f,
        maxSteerAngle: 0.6f
    );
    
    Assert.True(steer > 0f, "Left turn should produce positive steering");
}

[Fact]
public void PurePursuit_ClampsSteering_ToMaxAngle()
{
    Vector2 pos = Vector2.Zero;
    Vector2 forward = new Vector2(1, 0);
    Vector2 desiredVel = new Vector2(0, 10); // 90° left (extreme)
    
    float maxSteer = 0.6f;
    float steer = PurePursuitController.CalculateSteering(
        pos, forward, desiredVel,
        currentSpeed: 10f,
        wheelBase: 2.7f,
        lookaheadMin: 2f,
        lookaheadMax: 10f,
        maxSteerAngle: maxSteer
    );
    
    Assert.InRange(steer, -maxSteer, maxSteer);
}
```

---

### Task CK-02-03: Speed Controller

**File:** `CarKinem/Controllers/SpeedController.cs`

Implement P-controller for speed regulation (design doc lines 527-538):

```csharp
using System;

namespace CarKinem.Controllers
{
    /// <summary>
    /// Proportional speed controller.
    /// </summary>
    public static class SpeedController
    {
        /// <summary>
        /// Calculate acceleration command.
        /// </summary>
        /// <param name="currentSpeed">Current speed (m/s)</param>
        /// <param name="targetSpeed">Desired speed (m/s)</param>
        /// <param name="gain">Proportional gain</param>
        /// <param name="maxAccel">Maximum acceleration (m/s²)</param>
        /// <param name="maxDecel">Maximum deceleration (m/s²)</param>
        /// <returns>Acceleration command (m/s²)</returns>
        public static float CalculateAcceleration(
            float currentSpeed,
            float targetSpeed,
            float gain,
            float maxAccel,
            float maxDecel)
        {
            float speedError = targetSpeed - currentSpeed;
            float rawAccel = speedError * gain;
            
            // Clamp to vehicle limits
            return Math.Clamp(rawAccel, -maxDecel, maxAccel);
        }
    }
}
```

**Verification Tests:**

```csharp
[Fact]
public void SpeedController_Accelerate_WhenBelowTarget()
{
    float accel = SpeedController.CalculateAcceleration(
        currentSpeed: 5f,
        targetSpeed: 10f,
        gain: 2.0f,
        maxAccel: 3.0f,
        maxDecel: 6.0f
    );
    
    Assert.True(accel > 0f, "Should accelerate");
    Assert.InRange(accel, 0f, 3.0f);
}

[Fact]
public void SpeedController_Decelerate_WhenAboveTarget()
{
    float accel = SpeedController.CalculateAcceleration(
        currentSpeed: 15f,
        targetSpeed: 10f,
        gain: 2.0f,
        maxAccel: 3.0f,
        maxDecel: 6.0f
    );
    
    Assert.True(accel < 0f, "Should decelerate");
    Assert.InRange(accel, -6.0f, 0f);
}

[Fact]
public void SpeedController_ClampAcceleration_ToMaxValues()
{
    float accel = SpeedController.CalculateAcceleration(
        currentSpeed: 0f,
        targetSpeed: 100f, // Extreme difference
        gain: 10.0f,       // High gain
        maxAccel: 3.0f,
        maxDecel: 6.0f
    );
    
    Assert.Equal(3.0f, accel); // Should clamp to max accel
}
```

---

### Task CK-02-04: Bicycle Model Integration

**File:** `CarKinem/Controllers/BicycleModel.cs`

Implement kinematic bicycle model (design doc lines 540-580):

```csharp
using System;
using System.Numerics;

namespace CarKinem.Controllers
{
    /// <summary>
    /// Kinematic bicycle model for vehicle motion.
    /// </summary>
    public static class BicycleModel
    {
        /// <summary>
        /// Integrate bicycle model for one timestep.
        /// Updates position, heading, and speed.
        /// </summary>
        /// <param name="state">Current vehicle state (modified in-place)</param>
        /// <param name="steerAngle">Steering angle command (radians)</param>
        /// <param name="accel">Acceleration command (m/s²)</param>
        /// <param name="dt">Timestep (seconds)</param>
        /// <param name="wheelBase">Distance between axles (meters)</param>
        public static void Integrate(
            ref VehicleState state,
            float steerAngle,
            float accel,
            float dt,
            float wheelBase)
        {
            // 1. Update speed
            state.Speed += accel * dt;
            
            // QA FIX #3: No reverse driving (deadlock prevention)
            if (state.Speed < 0f)
                state.Speed = 0f;
            
            // 2. Calculate angular velocity (yaw rate)
            float angularVel = (state.Speed / wheelBase) * MathF.Tan(steerAngle);
            
            // 3. Rotate forward vector (2D rotation matrix)
            float rotAngle = angularVel * dt;
            float c = MathF.Cos(rotAngle);
            float s = MathF.Sin(rotAngle);
            
            Vector2 newForward = new Vector2(
                state.Forward.X * c - state.Forward.Y * s,
                state.Forward.X * s + state.Forward.Y * c
            );
            
            // Re-normalize to prevent drift
            state.Forward = Vector2.Normalize(newForward);
            
            // 4. Update position
            state.Position += state.Forward * state.Speed * dt;
            
            // 5. Update state metadata
            state.SteerAngle = steerAngle;
            state.Accel = accel;
        }
    }
}
```

**Verification Tests:**

```csharp
[Fact]
public void BicycleModel_StraightMotion_UpdatesPosition()
{
    var state = new VehicleState
    {
        Position = Vector2.Zero,
        Forward = new Vector2(1, 0),
        Speed = 10f
    };
    
    BicycleModel.Integrate(
        ref state,
        steerAngle: 0f,
        accel: 0f,
        dt: 1.0f,
        wheelBase: 2.7f
    );
    
    // After 1 second at 10 m/s
    Assert.Equal(10f, state.Position.X, precision: 3);
    Assert.Equal(0f, state.Position.Y, precision: 3);
}

[Fact]
public void BicycleModel_Turning_RotatesHeading()
{
    var state = new VehicleState
    {
        Position = Vector2.Zero,
        Forward = new Vector2(1, 0),
        Speed = 10f
    };
    
    // Apply left steering for 1 second
    BicycleModel.Integrate(
        ref state,
        steerAngle: 0.3f, // ~17 degrees
        accel: 0f,
        dt: 1.0f,
        wheelBase: 2.7f
    );
    
    // Heading should have rotated
    Assert.True(state.Forward.Y > 0f, "Should turn left (positive Y)");
    
    // Forward should still be normalized
    float length = state.Forward.Length();
    Assert.Equal(1f, length, precision: 4);
}

[Fact]
public void BicycleModel_NegativeSpeed_ClampsToZero()
{
    var state = new VehicleState
    {
        Position = Vector2.Zero,
        Forward = new Vector2(1, 0),
        Speed = 5f
    };
    
    // Apply extreme braking
    BicycleModel.Integrate(
        ref state,
        steerAngle: 0f,
        accel: -10f, // Heavy deceleration
        dt: 1.0f,
        wheelBase: 2.7f
    );
    
    // Speed should be clamped to zero (no reverse)
    Assert.Equal(0f, state.Speed);
}
```

---

### Task CK-02-05: RVO-Lite Collision Avoidance

**File:** `CarKinem/Avoidance/RVOAvoidance.cs`

Implement velocity-space avoidance (design doc lines 381-423):

```csharp
using System;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Avoidance
{
    /// <summary>
    /// RVO-Lite collision avoidance using velocity-space forces.
    /// </summary>
    public static class RVOAvoidance
    {
        /// <summary>
        /// Apply collision avoidance to preferred velocity.
        /// </summary>
        /// <param name="preferredVel">Desired velocity without avoidance</param>
        /// <param name="selfPos">Vehicle position</param>
        /// <param name="selfVel">Vehicle velocity</param>
        /// <param name="neighbors">Array of neighbor positions and velocities</param>
        /// <param name="avoidanceRadius">Danger zone radius (meters)</param>
        /// <param name="maxSpeed">Maximum allowed speed (m/s)</param>
        /// <returns>Adjusted velocity with avoidance</returns>
        public static Vector2 ApplyAvoidance(
            Vector2 preferredVel,
            Vector2 selfPos,
            Vector2 selfVel,
            ReadOnlySpan<(Vector2 pos, Vector2 vel)> neighbors,
            float avoidanceRadius,
            float maxSpeed)
        {
            Vector2 avoidanceForce = Vector2.Zero;
            float dangerRadius = avoidanceRadius * 2.5f;
            
            foreach (var (neighborPos, neighborVel) in neighbors)
            {
                Vector2 relPos = neighborPos - selfPos;
                float dist = relPos.Length();
                
                // Skip if too far or same position
                if (dist > dangerRadius || dist < 0.01f)
                    continue;
                
                // Calculate relative velocity
                Vector2 relVel = selfVel - neighborVel;
                
                // Time-to-collision heuristic
                float relSpeed = relVel.Length();
                float ttc = dist / MathF.Max(relSpeed, 0.1f);
                
                // Apply repulsion if on collision course
                if (Vector2.Dot(relVel, relPos) < 0f && ttc < 2.0f)
                {
                    // Repulsion inversely proportional to distance
                    Vector2 repulsion = -Vector2.Normalize(relPos) * (5.0f / dist);
                    avoidanceForce += repulsion;
                }
            }
            
            // Blend preferred velocity with avoidance
            Vector2 finalVel = preferredVel + avoidanceForce;
            
            // Clamp to max speed
            if (finalVel.LengthSquared() > maxSpeed * maxSpeed)
            {
                finalVel = Vector2.Normalize(finalVel) * maxSpeed;
            }
            
            return finalVel;
        }
    }
}
```

**Verification Tests:**

```csharp
[Fact]
public void RVOAvoidance_NoNeighbors_ReturnsPreferredVelocity()
{
    Vector2 preferredVel = new Vector2(10, 0);
    Vector2 selfPos = Vector2.Zero;
    Vector2 selfVel = preferredVel;
    
    var neighbors = Array.Empty<(Vector2, Vector2)>();
    
    Vector2 result = RVOAvoidance.ApplyAvoidance(
        preferredVel, selfPos, selfVel,
        neighbors,
        avoidanceRadius: 2.5f,
        maxSpeed: 30f
    );
    
    Assert.Equal(preferredVel, result);
}

[Fact]
public void RVOAvoidance_StaticObstacleAhead_AvoidsIt()
{
    Vector2 preferredVel = new Vector2(10, 0); // Moving right
    Vector2 selfPos = Vector2.Zero;
    Vector2 selfVel = preferredVel;
    
    // Obstacle directly ahead
    var neighbors = new[] {
        (pos: new Vector2(5, 0), vel: Vector2.Zero)
    };
    
    Vector2 result = RVOAvoidance.ApplyAvoidance(
        preferredVel, selfPos, selfVel,
        neighbors,
        avoidanceRadius: 2.5f,
        maxSpeed: 30f
    );
    
    // Should deviate from straight line
    Assert.NotEqual(preferredVel.Y, result.Y);
}

[Fact]
public void RVOAvoidance_ClampsToMaxSpeed()
{
    Vector2 preferredVel = new Vector2(100, 100); // Extreme velocity
    Vector2 selfPos = Vector2.Zero;
    Vector2 selfVel = Vector2.Zero;
    
    var neighbors = Array.Empty<(Vector2, Vector2)>();
    float maxSpeed = 30f;
    
    Vector2 result = RVOAvoidance.ApplyAvoidance(
        preferredVel, selfPos, selfVel,
        neighbors,
        avoidanceRadius: 2.5f,
        maxSpeed: maxSpeed
    );
    
    Assert.True(result.Length() <= maxSpeed);
}
```

---

## ✅ Acceptance Criteria

### Build & Quality
- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 20 unit tests (4+ per algorithm)
- [ ] All public methods have XML documentation

### Algorithm Correctness
- [ ] SignedAngle returns correct values for all quadrants
- [ ] Pure Pursuit produces zero steering for straight paths
- [ ] Speed controller responds correctly to error sign
- [ ] Bicycle model maintains normalized heading vector
- [ ] RVO avoidance respects max speed constraint

### Code Quality
- [ ] All classes in correct namespaces
- [ ] Static classes for stateless algorithms
- [ ] No hardcoded magic numbers (use parameters)
- [ ] Performance: No allocations in hot paths

### Test Quality
- [ ] Each algorithm has:
  - Happy path test
  - Edge case tests (zero, negative, extreme values)
  - Clamping/boundary tests
- [ ] Test names clearly describe scenario
- [ ] Precision tolerance appropriate for floating-point

---

## 📤 Submission Instructions

Submit your report to:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-02-REPORT.md
```

Include:
- Test results (all 20+ tests passing)
- Algorithm validation notes
- Any performance observations
- Questions for review

---

## 📚 Reference Materials

- **Design Doc (Control Algorithms):** Lines 323-733
- **VectorMath:** Lines 326-341
- **Pure Pursuit:** Lines 343-379
- **Speed Controller:** Lines 527-538
- **Bicycle Model:** Lines 540-580
- **RVO Avoidance:** Lines 381-423

---

**Time Estimate:** 5-7 hours

**Focus:** Correctness and test coverage. These are the physics "brain" of the system.

---

_Batch prepared by: Development Lead_  
_Date: 2026-01-06 23:20_
