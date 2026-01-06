# Car Kinematics & Formation Module - Implementation Design

**Version:** 1.0  
**Target Architecture:** FDP/ModuleHost  
**Date:** 2026-01-06  
**Status:** Implementation-Ready

---

## Executive Summary

This document provides an implementation-ready design for a high-performance vehicle kinematics and formation system integrated with the FDP (Fast Data Plane) Entity Component System and ModuleHost orchestration layer.

**Key Design Constraints:**
- Target: 50,000 vehicles @ 60Hz on desktop-class CPU (8 cores max)
- Zero GC allocations on hot path
- Float-based physics (System.Numerics.Vector2)
- Blittable components for Flight Recorder compatibility
- Tolerance-based replay (within ε_pos = 1e-3, ε_ang = 1e-4 rad)
- Formation limit: 16 members max per formation
- No reverse driving (Speed >= 0 constraint)

---

## System Architecture Overview

### 1. FDP Integration Model

The system integrates with FDP using the **hybrid tier architecture**:

1. **Tier 1 (Unmanaged Components):** `VehicleState`, `VehicleParams`, `FormationRoster`, `FormationMember`
2. **Tier 2 (Managed Components):** Road network metadata, formation templates (BlobAssets)
3. **Module Pattern:** AI/Control logic runs in background `IModule` implementations
4. **System Pattern:** Physics/Formation updates run as `ComponentSystem` implementations

### 2. Execution Pipeline

```
┌─────────────────────────────────────────────────────────────┐
│ Frame N                                                      │
├─────────────────────────────────────────────────────────────┤
│ 1. [Input Phase]                                            │
│    - VehicleCommandSystem: Process spawns, joins, orders    │
│    - Update FormationRoster, NavState via command buffer    │
├─────────────────────────────────────────────────────────────┤
│ 2. [Simulation Phase] - Main thread, direct write access    │
│    a. SpatialHashSystem                                     │
│       - Build NativeMultiHashMap<int, int> (CellID → EntityID)
│       - Input: VehicleState (Position)                      │
│       - Output: SpatialGrid (transient, frame-local)        │
│    b. FormationTargetSystem                                 │
│       - Iterate followers WITH FormationMember              │
│       - Read leader VehicleState                            │
│       - Write FormationTarget component                     │
│    c. CarKinematicsSystem                                   │
│       - Read: FormationTarget, SpatialGrid, RoadNetwork     │
│       - Compute: Preferred velocity, RVO avoidance,         │
│                  Pure Pursuit steering, Speed control       │
│       - Write: VehicleState (Position, Forward, Speed)      │
├─────────────────────────────────────────────────────────────┤
│ 3. [Presentation Phase]                                     │
│    - Interpolation for rendering (if needed)                │
└─────────────────────────────────────────────────────────────┘
```

**Key Architectural Decision:**  
Systems run **synchronously** in the Simulation phase with full read/write access to `EntityRepository`. This is the FDP `ComponentSystem` pattern, NOT the async `IModule` pattern. Modules (AI) queue commands via `IEntityCommandBuffer`, which are processed in the Input phase.

---

## Data Structures (Tier 1 - Unmanaged)

### Core Components

```csharp
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinematics.Core
{
    /// <summary>
    /// Per-vehicle physics state (double-buffered by ECS).
    /// Uses bicycle kinematic model.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleState
    {
        public Vector2 Position;    // World position (meters)
        public Vector2 Forward;     // Normalized heading vector
        public float Speed;         // Scalar forward speed (m/s, >= 0)
        public float SteerAngle;    // Current wheel angle (radians)
        public float Accel;         // Longitudinal acceleration (m/s²)
        
        // Presentation only (derived)
        public float Pitch;         // Forward/backward tilt for visuals
        public float Roll;          // Lateral tilt for visuals
        
        // Metadata
        public int CurrentLaneIndex; // For lane-aware logic
    }
    
    /// <summary>
    /// Per-vehicle configuration (flyweight pattern).
    /// Referenced by index from VehicleState.
    /// Stored in global NativeArray<VehicleParams> table.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleParams
    {
        public float Length;         // Vehicle length (meters)
        public float Width;          // Vehicle width (meters)
        public float WheelBase;      // Distance between axles (meters)
        
        public float MaxSpeedFwd;    // Max forward speed (m/s)
        public float MaxSpeedRev;    // Reserved for future (currently unused)
        
        public float MaxAccel;       // Max acceleration (m/s²)
        public float MaxDecel;       // Max braking deceleration (m/s²)
        
        public float MaxSteerAngle;  // Max steering angle (radians)
        public float MaxSteerRate;   // Max steering rate (rad/s)
        
        public float MaxLatAccel;    // Max lateral acceleration for curvature limits
        public float AvoidanceRadius; // Collision radius for RVO (meters)
        
        // Control tuning
        public float LookaheadTimeMin; // Pure Pursuit lookahead min (seconds)
        public float LookaheadTimeMax; // Pure Pursuit lookahead max (seconds)
        public float AccelGain;        // Speed controller proportional gain
    }
    
    /// <summary>
    /// Navigation mode enumeration.
    /// Determines how vehicle calculates its target.
    /// </summary>
    public enum NavigationMode : byte
    {
        None = 0,           // No active navigation (stationary or manual control)
        RoadGraph = 1,      // Follow road network (approach → follow → leave)
        CustomTrajectory = 2, // Follow custom trajectory from trajectory pool
        Formation = 3       // Follow formation target (overrides other modes)
    }
    
    /// <summary>
    /// Road graph state machine.
    /// Tracks progress through approach → follow → leave phases.
    /// </summary>
    public enum RoadGraphPhase : byte
    {
        Approaching = 0,    // Moving to closest entry point on road graph
        Following = 1,      // Following road segments
        Leaving = 2,        // Moving from road exit point to final destination
        Arrived = 3         // Reached final destination
    }
    
    /// <summary>
    /// Navigation/control state.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NavState
    {
        // Navigation mode and state
        public NavigationMode Mode;       // Current navigation mode
        public RoadGraphPhase RoadPhase;  // Road graph state machine (only if Mode == RoadGraph)
        
        // Trajectory references
        public int TrajectoryId;     // Index into custom trajectory pool (-1 if none)
        public int CurrentSegmentId; // Current road segment ID (only if Mode == RoadGraph)
        
        // Progress tracking
        public float ProgressS;      // Arc-length progress along path (meters)
        public float TargetSpeed;    // Desired cruise/arrival speed (m/s)
        
        // Destination (for RoadGraph mode: final off-road target)
        public Vector2 FinalDestination;
        public float ArrivalRadius;  // Distance to consider "arrived" (meters)
        
        // Controller internals (for stability)
        public float SpeedErrorInt;  // Speed integral term (for PI control)
        public float LastSteerCmd;   // Previous steering command
        
        // Flags (use byte for blittable safety)
        public byte ReverseAllowed;  // 1 = allow reverse (NOT IMPLEMENTED in v1)
        public byte HasArrived;      // 1 = within arrival tolerance
        public byte IsBlocked;       // 1 = obstacle detected ahead
    }
}
```

### Formation Components

```csharp
namespace CarKinematics.Formation
{
    /// <summary>
    /// Formation type enumeration.
    /// </summary>
    public enum FormationType : byte
    {
        Column = 0,  // Single file, vehicles behind leader
        Wedge = 1,   // V-formation, vehicles spread left/right/back
        Line = 2,    // Abreast, vehicles left/right
        Custom = 3   // User-defined slot offsets
    }
    
    /// <summary>
    /// Formation member state enum.
    /// </summary>
    public enum FormationMemberState : byte
    {
        InSlot = 0,      // Within tolerance of assigned slot
        CatchingUp = 1,  // Behind slot, accelerating to catch up
        Rejoining = 2,   // Far from slot, executing rejoin maneuver
        Waiting = 3,     // Leader stopped, maintaining spacing
        Broken = 4       // Too far from formation, independent control
    }
    
    /// <summary>
    /// Formation parameters (stored in formation entity or singleton table).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationParams
    {
        public float Spacing;            // Nominal spacing between vehicles (meters)
        public float WedgeAngleRad;      // Wedge angle (radians, only for Wedge type)
        public float MaxCatchUpFactor;   // Speed multiplier when catching up (e.g., 1.2 = 20% faster)
        public float BreakDistance;      // Distance beyond which formation breaks (meters)
        public float ArrivalThreshold;   // Distance to consider "in slot" (meters)
        public float SpeedFilterTau;     // Time constant for speed filtering (seconds)
    }
    
    /// <summary>
    /// Formation roster (attached to leader entity or formation manager).
    /// Fixed capacity of 16 members.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FormationRoster
    {
        public int Count;                 // Number of active members (0-16)
        public int TemplateId;            // Index into formation template blob
        public FormationType Type;        // Formation type
        public FormationParams Params;    // Formation parameters
        
        // Fixed-capacity arrays (zero GC, cache-friendly)
        public fixed int MemberEntityIds[16];   // Entity IDs of members
        public fixed ushort SlotIndices[16];    // Slot index for each member
    }
    
    /// <summary>
    /// Formation member component (attached to follower entities).
    /// Enables "pull" pattern: follower reads leader state.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationMember
    {
        public int LeaderEntityId;          // Entity ID of formation leader
        public ushort SlotIndex;            // Which slot in template (0-15)
        public FormationMemberState State;  // Current formation state
        public byte IsInFormation;          // 1 = active member, 0 = inactive
        
        // State tracking
        public float SlotDistFiltered;      // Low-pass filtered distance to slot
        public float RejoinTimer;           // Time spent in Rejoining state
    }
    
    /// <summary>
    /// Formation target (transient scratchpad component).
    /// Written by FormationTargetSystem, read by CarKinematicsSystem.
    /// Not persisted between frames.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationTarget
    {
        public Vector2 TargetPosition;   // Desired world position
        public Vector2 TargetHeading;    // Desired forward vector
        public float TargetSpeed;        // Desired speed (includes catch-up factor)
        public byte IsValid;             // 1 = valid target, 0 = ignore
    }
    
    /// <summary>
    /// Formation slot definition (stored in BlobAsset or lookup table).
    /// Defines offset in leader's local frame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationSlot
    {
        public float ForwardOffset;   // Longitudinal offset (+ = ahead, - = behind)
        public float LateralOffset;   // Lateral offset (+ = right, - = left)
        public float HeadingOffset;   // Heading offset relative to leader (radians)
    }
}
```

### Road Network Components

```csharp
namespace CarKinematics.Road
{
    /// <summary>
    /// Road segment using Cubic Hermite spline representation.
    /// Precomputed with distance LUT for constant-speed sampling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct RoadSegment
    {
        // Cubic Hermite geometry (C1 continuous)
        public Vector2 P0;           // Start position
        public Vector2 T0;           // Start tangent (velocity vector)
        public Vector2 P1;           // End position
        public Vector2 T1;           // End tangent (velocity vector)
        
        // Precomputed properties
        public float Length;         // Arc length (meters)
        public float SpeedLimit;     // Speed limit (m/s)
        public float LaneWidth;      // Width of single lane (meters)
        public int LaneCount;        // Number of lanes
        
        // Graph connectivity
        public int StartNodeIndex;   // Index into RoadNode array
        public int EndNodeIndex;     // Index into RoadNode array
        
        // Distance-to-parameter lookup table (8 samples)
        // Maps normalized distance [0,1] → parameter t [0,1]
        // Enables constant-speed movement without Newton-Raphson
        public fixed float DistanceLUT[8];
    }
    
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
```

### Trajectory Pool Components

```csharp
namespace CarKinematics.Trajectory
{
    /// <summary>
    /// Custom trajectory waypoint.
    /// Linear interpolation between waypoints.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TrajectoryWaypoint
    {
        public Vector2 Position;      // World position
        public Vector2 Tangent;       // Optional tangent for smooth curves (zero for linear)
        public float DesiredSpeed;    // Desired speed at this waypoint (m/s)
        public float CumulativeDistance; // Precomputed distance from start (meters)
    }
    
    /// <summary>
    /// Custom trajectory definition.
    /// Stored in global trajectory pool.
    /// </summary>
    public struct CustomTrajectory
    {
        public int Id;                        // Unique trajectory ID
        public NativeArray<TrajectoryWaypoint> Waypoints; // Trajectory path
        public float TotalLength;             // Total arc length (meters)
        public byte IsLooped;                 // 1 = loop back to start, 0 = one-shot
    }
    
    /// <summary>
    /// Trajectory pool singleton (managed component).
    /// Stores all custom trajectories in the simulation.
    /// Thread-safe for reads (immutable after creation).
    /// </summary>
    public class TrajectoryPoolManager
    {
        private Dictionary<int, CustomTrajectory> _trajectories = new();
        private int _nextId = 1;
        
        /// <summary>
        /// Register a new custom trajectory.
        /// Returns trajectory ID for reference in commands.
        /// </summary>
        public int RegisterTrajectory(Vector2[] positions, float[] speeds = null, bool looped = false)
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
        
        /// <summary>
        /// Get trajectory by ID (read-only).
        /// </summary>
        public bool TryGetTrajectory(int id, out CustomTrajectory trajectory)
        {
            return _trajectories.TryGetValue(id, out trajectory);
        }
        
        /// <summary>
        /// Sample trajectory at given progress distance.
        /// Returns (position, tangent, desired speed).
        /// </summary>
        public (Vector2 pos, Vector2 tangent, float speed) SampleTrajectory(int id, float progressS)
        {
            if (!_trajectories.TryGetValue(id, out var traj))
            {
                return (Vector2.Zero, new Vector2(1, 0), 0f);
            }
            
            // Handle looping
            if (traj.IsLooped == 1)
            {
                progressS = progressS % traj.TotalLength;
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
                    Vector2 tangent = Vector2.Normalize(waypoints[i].Position - waypoints[i - 1].Position);
                    float speed = MathF.Lerp(waypoints[i - 1].DesiredSpeed, waypoints[i].DesiredSpeed, t);
                    
                    return (pos, tangent, speed);
                }
            }
            
            // End of trajectory
            var lastWp = waypoints[waypoints.Length - 1];
            return (lastWp.Position, new Vector2(1, 0), lastWp.DesiredSpeed);
        }
        
        /// <summary>
        /// Cleanup all trajectories (call on shutdown).
        /// </summary>
        public void Dispose()
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
```

### Road Network JSON Format

The road network can be loaded from a JSON file with the following schema:

```json
{
  "nodes": [
    {
      "id": 0,
      "position": { "x": 0.0, "y": 0.0 }
    },
    {
      "id": 1,
      "position": { "x": 100.0, "y": 0.0 }
    }
  ],
  "segments": [
    {
      "id": 0,
      "startNodeId": 0,
      "endNodeId": 1,
      "controlPoints": {
        "p0": { "x": 0.0, "y": 0.0 },
        "t0": { "x": 50.0, "y": 0.0 },
        "p1": { "x": 100.0, "y": 0.0 },
        "t1": { "x": 50.0, "y": 0.0 }
      },
      "speedLimit": 25.0,
      "laneWidth": 3.5,
      "laneCount": 2
    }
  ],
  "metadata": {
    "worldBounds": { "min": { "x": -500, "y": -500 }, "max": { "x": 500, "y": 500 } },
    "gridCellSize": 5.0
  }
}
```

**JSON Loading Implementation:**

```csharp
using System.Text.Json;

namespace CarKinematics.Road
{
    public class RoadNetworkLoader
    {
        public static RoadNetworkBlob LoadFromJson(string jsonPath)
        {
            string jsonContent = File.ReadAllText(jsonPath);
            var roadData = JsonSerializer.Deserialize<RoadNetworkJson>(jsonContent);
            
            var builder = new RoadNetworkBuilder();
            
            // Add nodes
            foreach (var node in roadData.Nodes)
            {
                builder.AddNode(new Vector2(node.Position.X, node.Position.Y));
            }
            
            // Add segments
            foreach (var seg in roadData.Segments)
            {
                builder.AddSegment(
                    new Vector2(seg.ControlPoints.P0.X, seg.ControlPoints.P0.Y),
                    new Vector2(seg.ControlPoints.T0.X, seg.ControlPoints.T0.Y),
                    new Vector2(seg.ControlPoints.P1.X, seg.ControlPoints.P1.Y),
                    new Vector2(seg.ControlPoints.T1.X, seg.ControlPoints.T1.Y),
                    seg.SpeedLimit,
                    seg.LaneWidth
                );
            }
            
            // Build with metadata
            float cellSize = roadData.Metadata?.GridCellSize ?? 5.0f;
            var bounds = roadData.Metadata?.WorldBounds;
            int width = (int)((bounds.Max.X - bounds.Min.X) / cellSize);
            int height = (int)((bounds.Max.Y - bounds.Min.Y) / cellSize);
            
            return builder.Build(cellSize, width, height);
        }
    }
    
    // JSON schema classes (for deserialization)
    public class RoadNetworkJson
    {
        public List<NodeJson> Nodes { get; set; }
        public List<SegmentJson> Segments { get; set; }
        public MetadataJson Metadata { get; set; }
    }
    
    public class NodeJson
    {
        public int Id { get; set; }
        public Vec2Json Position { get; set; }
    }
    
    public class SegmentJson
    {
        public int Id { get; set; }
        public int StartNodeId { get; set; }
        public int EndNodeId { get; set; }
        public ControlPointsJson ControlPoints { get; set; }
        public float SpeedLimit { get; set; }
        public float LaneWidth { get; set; }
        public int LaneCount { get; set; }
    }
    
    public class ControlPointsJson
    {
        public Vec2Json P0 { get; set; }
        public Vec2Json T0 { get; set; }
        public Vec2Json P1 { get; set; }
        public Vec2Json T1 { get; set; }
    }
    
    public class Vec2Json
    {
        public float X { get; set; }
        public float Y { get; set; }
    }
    
    public class MetadataJson
    {
        public BoundsJson WorldBounds { get; set; }
        public float GridCellSize { get; set; }
    }
    
    public class BoundsJson
    {
        public Vec2Json Min { get; set; }
        public Vec2Json Max { get; set; }
    }
}
```


### 1. Pure Pursuit Steering Controller

**Algorithm:** Geometric path-following using lookahead point.

**Input:**
- Current position, heading (Forward vector)
- Desired velocity vector (from formation target or path)

**Output:** Steering angle

**Implementation:**

```csharp
float CalculatePurePursuitSteering(
    Vector2 currentPos,
    Vector2 currentForward,
    Vector2 desiredVelocity,
    float currentSpeed,
    VehicleParams prm)
{
    // 1. Calculate dynamic lookahead distance
    float lookaheadDist = MathF.Max(
        prm.LookaheadTimeMin * currentSpeed,
        prm.LookaheadTimeMax * currentSpeed
    );
    lookaheadDist = Clamp(lookaheadDist, 2.0f, 10.0f);
    
    // 2. Project lookahead point along desired velocity
    Vector2 lookaheadPoint;
    if (desiredVelocity.LengthSquared() < 0.01f)
    {
        // Stopped: maintain heading
        lookaheadPoint = currentPos + currentForward * lookaheadDist;
    }
    else
    {
        // Moving: follow desired velocity direction
        lookaheadPoint = currentPos + Vector2.Normalize(desiredVelocity) * lookaheadDist;
    }
    
    // 3. Calculate signed angle to lookahead point
    Vector2 toLookahead = lookaheadPoint - currentPos;
    float alpha = SignedAngle(currentForward, toLookahead);
    
    // 4. Compute curvature (bicycle model)
    float kappa = (2.0f * MathF.Sin(alpha)) / lookaheadDist;
    
    // 5. Convert curvature to steering angle
    float steerAngle = MathF.Atan(kappa * prm.WheelBase);
    
    // 6. Clamp to vehicle limits
    return Clamp(steerAngle, -prm.MaxSteerAngle, prm.MaxSteerAngle);
}

// Helper: Signed angle between two vectors
float SignedAngle(Vector2 from, Vector2 to)
{
    float dot = Vector2.Dot(from, to);
    float det = from.X * to.Y - from.Y * to.X;
    return MathF.Atan2(det, dot);
}
```

### 2. RVO-Lite Collision Avoidance

**Algorithm:** Velocity-space obstacle avoidance using separation forces.

**Input:**
- Preferred velocity (from formation/path)
- Neighbor positions/velocities (from SpatialHash)

**Output:** Adjusted velocity (collision-free)

**Implementation:**

```csharp
Vector2 ApplyRVOAvoidance(
    Vector2 preferredVel,
    Vector2 selfPos,
    Vector2 selfForward,
    float selfSpeed,
    VehicleParams prm,
    NativeMultiHashMap<int, int> spatialHash,
    NativeArray<VehicleState> allStates,
    int selfEntityIdx)
{
    Vector2 avoidanceForce = Vector2.Zero;
    
    // 1. Get cell ID for spatial lookup
    int cellId = GetCellId(selfPos, gridCellSize);
    
    // 2. Query neighbors in same cell (and adjacent cells for robustness)
    if (spatialHash.TryGetFirstValue(cellId, out int neighborIdx, out var iterator))
    {
        do
        {
            if (neighborIdx == selfEntityIdx) continue;
            
            ref readonly var neighbor = ref allStates[neighborIdx];
            Vector2 relPos = neighbor.Position - selfPos;
            float dist = relPos.Length();
            
            // 3. Check if neighbor is within danger zone
            float dangerRadius = prm.AvoidanceRadius * 2.5f;
            if (dist < dangerRadius && dist > 0.01f)
            {
                // 4. Calculate relative velocity
                Vector2 neighborVel = neighbor.Forward * neighbor.Speed;
                Vector2 selfVel = selfForward * selfSpeed;
                Vector2 relVel = selfVel - neighborVel;
                
                // 5. Time-to-collision heuristic
                float ttc = dist / MathF.Max(relVel.Length(), 0.1f);
                
                // 6. Apply repulsion if on collision course
                if (Vector2.Dot(relVel, relPos) < 0 && ttc < 2.0f)
                {
                    // Repulsion inversely proportional to distance
                    Vector2 repulsion = -Vector2.Normalize(relPos) * (5.0f / dist);
                    avoidanceForce += repulsion;
                }
            }
        }
        while (spatialHash.TryGetNextValue(out neighborIdx, ref iterator));
    }
    
    // 7. Blend preferred velocity with avoidance
    Vector2 finalVel = preferredVel + avoidanceForce;
    
    // 8. Clamp to max speed
    if (finalVel.LengthSquared() > prm.MaxSpeedFwd * prm.MaxSpeedFwd)
    {
        finalVel = Vector2.Normalize(finalVel) * prm.MaxSpeedFwd;
    }
    
    return finalVel;
}
```

### 3. Speed Control (Proportional Controller)

**Algorithm:** Simple P-controller with acceleration clamping.

**Implementation:**

```csharp
float CalculateAcceleration(
    float currentSpeed,
    float targetSpeed,
    VehicleParams prm)
{
    float speedError = targetSpeed - currentSpeed;
    float rawAccel = speedError * prm.AccelGain;
    
    // Clamp to vehicle limits
    return Clamp(rawAccel, -prm.MaxDecel, prm.MaxAccel);
}
```

### 4. Bicycle Model Integration

**Algorithm:** Kinematic bicycle model (non-holonomic constraints).

**Implementation:**

```csharp
void IntegrateBicycleModel(
    ref VehicleState state,
    float steerAngle,
    float accel,
    float dt,
    VehicleParams prm)
{
    // 1. Update speed
    state.Speed += accel * dt;
    
    // QA FIX #3: No reverse driving (deadlock prevention)
    if (state.Speed < 0) state.Speed = 0;
    
    // 2. Calculate angular velocity (yaw rate)
    float angularVel = (state.Speed / prm.WheelBase) * MathF.Tan(steerAngle);
    
    // 3. Rotate forward vector (2D rotation matrix)
    float rotAngle = angularVel * dt;
    float c = MathF.Cos(rotAngle);
    float s = MathF.Sin(rotAngle);
    
    Vector2 newForward = new Vector2(
        state.Forward.X * c - state.Forward.Y * s,
        state.Forward.X * s + state.Forward.Y * c
    );
    state.Forward = Vector2.Normalize(newForward); // Re-normalize to prevent drift
    
    // 4. Update position
    state.Position += state.Forward * state.Speed * dt;
    
    // 5. Update state
    state.SteerAngle = steerAngle;
    state.Accel = accel;
}
```

```

### 5. Road Graph Navigation (Approach → Follow → Leave)

**Algorithm:** Three-phase state machine for road network navigation.

**Phases:**
1. **Approaching:** Drive to closest entry point on road graph
2. **Following:** Follow road segments to exit point nearest destination
3. **Leaving:** Drive from road exit point to final destination

**Implementation:**

```csharp
/// <summary>
/// Road graph navigation helper.
/// Calculates target position/speed for vehicles in RoadGraph mode.
/// </summary>
public static class RoadGraphNavigator
{
    /// <summary>
    /// Find closest point on entire road network.
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
        
        // Search current cell and adjacent cells
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
                    
                    // Project point onto Hermite curve (simplified: sample-based)
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
    /// </summary>
    private static Vector2 ProjectPointOntoHermiteSegment(Vector2 point, RoadSegment segment)
    {
        const int SAMPLES = 16;
        Vector2 closestPoint = segment.P0;
        float minDist = float.MaxValue;
        
        for (int i = 0; i <= SAMPLES; i++)
        {
            float t = i / (float)SAMPLES;
            Vector2 samplePoint = EvaluateHermite(t, segment.P0, segment.T0, segment.P1, segment.T1);
            float dist = Vector2.DistanceSquared(point, samplePoint);
            
            if (dist < minDist)
            {
                minDist = dist;
                closestPoint = samplePoint;
            }
        }
        
        return closestPoint;
    }
    
    /// <summary>
    /// Evaluate Hermite spline at parameter t.
    /// </summary>
    private static Vector2 EvaluateHermite(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
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
        tangent = Vector2.Normalize(tangent);
        
        return (pos, tangent, segment.SpeedLimit);
    }
    
    private static Vector2 EvaluateHermiteTangent(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
    {
        float t2 = t * t;
        
        float dh00 = 6 * t2 - 6 * t;
        float dh10 = 3 * t2 - 4 * t + 1;
        float dh01 = -6 * t2 + 6 * t;
        float dh11 = 3 * t2 - 2 * t;
        
        return dh00 * p0 + dh10 * t0 + dh01 * p1 + dh11 * t1;
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
                return (entryPoint, Vector2.Normalize(toEntry), 10.0f);
            }
            
            case RoadGraphPhase.Following:
            {
                // Follow current road segment
                ref readonly var segment = ref roadNetwork.Segments[nav.CurrentSegmentId];
                
                // Check if close enough to destination to leave road
                float distToDest = Vector2.Distance(currentPos, nav.FinalDestination);
                var (exitSegId, exitPoint, distToExit) = FindClosestRoadPoint(nav.FinalDestination, roadNetwork);
                
                if (distToExit < 5.0f && distToDest < 50.0f)
                {
                    // Close enough - transition to Leaving
                    nav.RoadPhase = RoadGraphPhase.Leaving;
                    return (nav.FinalDestination, Vector2.Normalize(nav.FinalDestination - currentPos), 5.0f);
                }
                
                // Continue following road
                var (pos, tangent, speedLimit) = SampleRoadSegment(segment, nav.ProgressS);
                
                // Advance progress based on current speed (handled externally)
                // nav.ProgressS += speed * dt; // Done by caller
                
                // Check if reached end of segment
                if (nav.ProgressS >= segment.Length - 1.0f)
                {
                    // TODO: Pathfinding to next segment (simplified: stay on current)
                    nav.ProgressS = Math.Min(nav.ProgressS, segment.Length);
                }
                
                return (pos + tangent * 10.0f, tangent, speedLimit);
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
                return (nav.FinalDestination, Vector2.Normalize(toDest), 5.0f);
            }
            
            case RoadGraphPhase.Arrived:
            default:
                return (currentPos, new Vector2(1, 0), 0f);
        }
    }
}
```

---

## Control Algorithms (Continued)


### CarKinematicsSystem (Hot Path)

```csharp
using Fdp.Kernel;
using System.Numerics;
using CarKinematics.Core;
using CarKinematics.Formation;

namespace CarKinematics.Systems
{
    /// <summary>
    /// Core vehicle physics system.
    /// Runs in Simulation phase on main thread with full R/W access.
    /// </summary>
    [UpdateInPhase(Phase.Simulation)]
    [UpdateAfter(typeof(FormationTargetSystem))]
    [UpdateAfter(typeof(SpatialHashSystem))]
    public class CarKinematicsSystem : ComponentSystem
    {
        private NativeArray<VehicleParams> _vehicleParamsTable;
        private NativeMultiHashMap<int, int> _spatialHash;
        private RoadNetworkBlob _roadNetwork;
        
        protected override void OnCreate()
        {
            // Initialize vehicle params table
            _vehicleParamsTable = new NativeArray<VehicleParams>(8, Allocator.Persistent);
            
            // Default vehicle type
            _vehicleParamsTable[0] = new VehicleParams
            {
                Length = 4.5f,
                Width = 2.0f,
                WheelBase = 2.7f,
                MaxSpeedFwd = 30.0f,
                MaxAccel = 3.0f,
                MaxDecel = 6.0f,
                MaxSteerAngle = 0.6f,
                MaxLatAccel = 8.0f,
                AvoidanceRadius = 2.5f,
                LookaheadTimeMin = 0.5f,
                LookaheadTimeMax = 1.5f,
                AccelGain = 2.0f
            };
        }
        
        protected override void OnUpdate()
        {
            // Get singleton spatial hash (built by SpatialHashSystem)
            if (!World.HasSingleton<SpatialHashData>()) return;
            ref var hashData = ref World.GetSingletonUnmanaged<SpatialHashData>();
            _spatialHash = hashData.Hash;
            
            // Get trajectory pool
            var trajectoryPool = World.GetSingletonManaged<TrajectoryPoolManager>();
            
            // Get all vehicles with state tables for parallel access
            var stateTable = World.GetComponentTable<VehicleState>();
            var navTable = World.GetComponentTable<NavState>();
            var stateSpan = stateTable.GetSpan();
            var navSpan = navTable.GetSpan();
            
            // Query all vehicles
            var query = World.Query()
                .WithAll<VehicleState, NavState>()
                .Build();
            
            var entities = query.ToEntityArray(); // Snapshot entity list for parallel iteration
            
            // PARALLEL EXECUTION: Process vehicles on multiple cores
            Parallel.ForEach(entities, (e) =>
            {
                ref var state = ref stateSpan[e.Index];
                ref var nav = ref navSpan[e.Index];
                
                // Get vehicle params (assume index 0 for now)
                ref readonly var prm = ref _vehicleParamsTable[0];
                
                // === 1. DETERMINE TARGET BASED ON NAVIGATION MODE ===
                Vector2 targetPos;
                float targetSpeed;
                
                // Formation mode takes priority
                if (World.HasComponent<FormationTarget>(e))
                {
                    ref readonly var formTarget = ref World.GetComponentRO<FormationTarget>(e);
                    if (formTarget.IsValid == 1)
                    {
                        targetPos = formTarget.TargetPosition;
                        targetSpeed = formTarget.TargetSpeed;
                        goto TargetCalculated; // Skip other navigation modes
                    }
                }
                
                // Process navigation mode
                switch (nav.Mode)
                {
                    case NavigationMode.RoadGraph:
                    {
                        var (pos, heading, speed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                            ref nav,
                            state.Position,
                            _roadNetwork
                        );
                        targetPos = pos;
                        targetSpeed = speed;
                        
                        // Update progress (advance along road)
                        nav.ProgressS += state.Speed * DeltaTime;
                        break;
                    }
                    
                    case NavigationMode.CustomTrajectory:
                    {
                        if (nav.TrajectoryId >= 0)
                        {
                            var (pos, tangent, speed) = trajectoryPool.SampleTrajectory(
                                nav.TrajectoryId,
                                nav.ProgressS
                            );
                            targetPos = pos + tangent * 10.0f; // Lookahead
                            targetSpeed = speed;
                            
                            // Update progress
                            nav.ProgressS += state.Speed * DeltaTime;
                        }
                        else
                        {
                            // Invalid trajectory - stop
                            targetPos = state.Position;
                            targetSpeed = 0f;
                        }
                        break;
                    }
                    
                    case NavigationMode.None:
                    default:
                    {
                        // No navigation - decelerate to stop
                        targetPos = state.Position;
                        targetSpeed = 0f;
                        break;
                    }
                }
                
                TargetCalculated:
                
                // === 2. CALCULATE PREFERRED VELOCITY ===
                Vector2 toTarget = targetPos - state.Position;
                Vector2 preferredVel = (toTarget.LengthSquared() > 0.1f)
                    ? Vector2.Normalize(toTarget) * targetSpeed
                    : Vector2.Zero;
                
                // === 3. APPLY RVO AVOIDANCE ===
                Vector2 finalVel = ApplyRVOAvoidance(
                    preferredVel,
                    state.Position,
                    state.Forward,
                    state.Speed,
                    prm,
                    _spatialHash,
                    stateSpan,
                    e.Index
                );
                
                // === 4. CALCULATE STEERING (Pure Pursuit) ===
                float steerAngle = CalculatePurePursuitSteering(
                    state.Position,
                    state.Forward,
                    finalVel,
                    state.Speed,
                    prm
                );
                
                // === 5. CALCULATE ACCELERATION ===
                float desiredSpeed = finalVel.Length();
                float accel = CalculateAcceleration(state.Speed, desiredSpeed, prm);
                
                // === 6. INTEGRATE (Bicycle Model) ===
                IntegrateBicycleModel(ref state, steerAngle, accel, DeltaTime, prm);
            });
        }
        
        protected override void OnDestroy()
        {
            if (_vehicleParamsTable.IsCreated)
                _vehicleParamsTable.Dispose();
            
            if (_roadNetwork.Segments.IsCreated)
                _roadNetwork.Dispose();
        }
        
        // Helper methods implemented as shown in Control Algorithms section
        // ...
    }
    
    /// <summary>
    /// Singleton component to share spatial hash across systems.
    /// </summary>
    public struct SpatialHashData
    {
        public NativeMultiHashMap<int, int> Hash;
        public float CellSize;
    }
}
```

---

## Command API (Module Integration)

Modules (AI, BT systems) interact with the vehicle system via **command events**:

```csharp
using Fdp.Kernel;
using System.Numerics;

namespace CarKinematics.Commands
{
    /// <summary>
    /// Command: Spawn a new vehicle.
    /// </summary>
    [EventId(5001)]
    public struct CmdSpawnVehicle
    {
        public int EntityId;        // Pre-allocated entity ID
        public Vector2 Position;    // Initial position
        public float Heading;       // Initial heading (radians)
        public int VehicleTypeId;   // Index into VehicleParams table
    }
    
    /// <summary>
    /// Command: Create a formation.
    /// </summary>
    [EventId(5002)]
    public struct CmdCreateFormation
    {
        public int LeaderEntityId;
        public FormationType Type;
        public FormationParams Params;
    }
    
    /// <summary>
    /// Command: Join a formation.
    /// </summary>
    [EventId(5003)]
    public struct CmdJoinFormation
    {
        public int FollowerEntityId;
        public int LeaderEntityId;
        public ushort SlotIndex;     // 0-15
    }
    
    /// <summary>
    /// Command: Set navigation target (direct point-to-point).
    /// </summary>
    [EventId(5004)]
    public struct CmdNavigateToPoint
    {
        public int EntityId;
        public Vector2 TargetPosition;
        public float TargetSpeed;
    }
    
    /// <summary>
    /// Command: Navigate via road network.
    /// Vehicle will approach road, follow it, then leave at closest point to destination.
    /// </summary>
    [EventId(5005)]
    public struct CmdNavigateViaRoad
    {
        public int EntityId;
        public Vector2 FinalDestination; // Off-road destination
        public float ArrivalRadius;      // Arrival tolerance (meters)
    }
    
    /// <summary>
    /// Command: Follow custom trajectory.
    /// Trajectory must be pre-registered via TrajectoryPoolManager.
    /// </summary>
    [EventId(5006)]
    public struct CmdFollowTrajectory
    {
        public int EntityId;
        public int TrajectoryId;     // ID from TrajectoryPoolManager.RegisterTrajectory()
        public float StartProgress;  // Initial progress (0 = start, can resume mid-path)
    }
    
    /// <summary>
    /// Command: Stop current navigation.
    /// </summary>
    [EventId(5007)]
    public struct CmdStop
    {
        public int EntityId;
    }
}
```

### Public API (Managed Layer)

For ease of use from game code, provide a managed API facade:

```csharp
using Fdp.Kernel;
using System.Numerics;

namespace CarKinematics.API
{
    /// <summary>
    /// High-level API for vehicle control.
    /// Thread-safe (uses command buffer pattern).
    /// </summary>
    public static class VehicleAPI
    {
        private static EntityRepository _world;
        private static TrajectoryPoolManager _trajectoryPool;
        
        public static void Initialize(EntityRepository world, TrajectoryPoolManager trajectoryPool)
        {
            _world = world;
            _trajectoryPool = trajectoryPool;
        }
        
        /// <summary>
        /// Register a custom trajectory for later use.
        /// Returns trajectory ID.
        /// </summary>
        public static int RegisterTrajectory(Vector2[] positions, float[] speeds = null, bool looped = false)
        {
            return _trajectoryPool.RegisterTrajectory(positions, speeds, looped);
        }
        
        /// <summary>
        /// Command vehicle to navigate via road network to destination.
        /// </summary>
        public static void NavigateViaRoad(int entityId, Vector2 destination, float arrivalRadius = 2.0f)
        {
            _world.Bus.Publish(new CmdNavigateViaRoad
            {
                EntityId = entityId,
                FinalDestination = destination,
                ArrivalRadius = arrivalRadius
            });
        }
        
        /// <summary>
        /// Command vehicle to follow custom trajectory.
        /// </summary>
        public static void FollowTrajectory(int entityId, int trajectoryId, float startProgress = 0f)
        {
            _world.Bus.Publish(new CmdFollowTrajectory
            {
                EntityId = entityId,
                TrajectoryId = trajectoryId,
                StartProgress = startProgress
            });
        }
        
        /// <summary>
        /// Command vehicle to navigate directly to point (no road following).
        /// </summary>
        public static void NavigateToPoint(int entityId, Vector2 target, float speed = 10.0f)
        {
            _world.Bus.Publish(new CmdNavigateToPoint
            {
                EntityId = entityId,
                TargetPosition = target,
                TargetSpeed = speed
            });
        }
        
        /// <summary>
        /// Join a formation.
        /// </summary>
        public static void JoinFormation(int followerId, int leaderId, int slotIndex = 0)
        {
            _world.Bus.Publish(new CmdJoinFormation
            {
                FollowerEntityId = followerId,
                LeaderEntityId = leaderId,
                SlotIndex = (ushort)slotIndex
            });
        }
        
        /// <summary>
        /// Stop vehicle.
        /// </summary>
        public static void Stop(int entityId)
        {
            _world.Bus.Publish(new CmdStop { EntityId = entityId });
        }
    }
}
```

**Usage Example from Module:**

```csharp
public class VehicleAIModule : IModule
{
    public string Name => "VehicleAI";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6; // Every 6 frames
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Example: Find idle vehicles and assign them destinations
        view.Query()
            .WithAll<VehicleState, NavState>()
            .WithNone<FormationMember>()
            .ForEach((Entity e) =>
            {
                ref readonly var nav = ref view.GetComponentRO<NavState>(e);
                
                if (nav.Mode == NavigationMode.None)
                {
                    // Assign random destination via road network
                    Vector2 randomDest = new Vector2(
                        Random.Shared.NextSingle() * 1000f,
                        Random.Shared.NextSingle() * 1000f
                    );
                    
                    VehicleAPI.NavigateViaRoad(e.Index, randomDest);
                }
            });
    }
}
```


---

## Design Decisions (Confirmed)

Based on the requirements clarification, here are the **confirmed design decisions**:

### 1. Road Network Implementation ✅

**Decision:** Road network is **fully static** after initial load.

**Implementation:**
- Roads loaded once from JSON file via `RoadNetworkLoader.LoadFromJson()`
- No runtime modifications supported
- Spatial grid built once during load
- **Benefit:** Maximum performance, no dynamic rebuild overhead

---

### 2. Formation Templates Storage ✅

**Decision:** Option A - **Managed singleton Dictionary<int, FormationTemplate>**

**Implementation:**
```csharp
// Global singleton (Tier 2 managed component)
public class FormationTemplateManager
{
    private Dictionary<int, FormationTemplate> _templates = new();
    
    public int RegisterTemplate(FormationTemplate template);
    public FormationTemplate GetTemplate(int id);
}
```

**Rationale:** Simple, pragmatic, minimal GC pressure (templates registered once at startup).

---

### 3. Trajectory System ✅

**Decision:** **Full three-mode navigation system**

**Navigation Modes:**
1. **RoadGraph**: Vehicle approaches road → follows road → leaves at closest point to destination
2. **CustomTrajectory**: Follow custom waypoint path from TrajectoryPool
3. **Formation**: Follow formation target (overrides other modes)

**Custom Trajectory System:**
- `TrajectoryPoolManager` singleton stores all custom trajectories
- API: `int RegisterTrajectory(Vector2[] positions, float[] speeds, bool looped)`
- Trajectories stored as `NativeArray<TrajectoryWaypoint>` with precomputed arc-lengths
- Linear interpolation between waypoints (can be upgraded to Hermite later)

**Road Graph Navigation:**
- Three-phase state machine: `Approaching → Following → Leaving → Arrived`
- Uses spatial grid to find closest road entry/exit points
- Follows Hermite spline road segments using distance LUT
- Automatically transitions between phases

---

### 4. Parallel Execution ✅

**Decision:** **Parallel execution from the start**

**Implementation:**
```csharp
// CarKinematicsSystem.OnUpdate()
var entities = query.ToEntityArray();
Parallel.ForEach(entities, (e) => {
    // Process vehicle physics...
});
```

**Rationale:** 50k vehicles target requires multi-core utilization. Use `Parallel.ForEach` with pre-snapshotted entity list and direct span access to component tables.

**Thread Safety:**
- Each thread writes to unique entity index (no contention)
- Reads from shared read-only data (SpatialHash, RoadNetwork, TrajectoryPool)
- NavState is mutable for progress tracking (each entity owned by single thread)

---

### 5. Spatial Hash Grid Cell Size ✅

**Decision:** **Hardcoded 5.0 meters** (2× typical avoidance radius)

**Rationale:**
- Simplifies configuration
- Optimal for typical urban vehicle scenarios
- Can be overridden via JSON metadata if needed (`"gridCellSize": 5.0`)

**Grid Dimensions:**
- Calculated from world bounds in JSON: `width = (maxX - minX) / cellSize`
- Example: 1000m × 1000m world = 200 × 200 grid

---

### 6. JSON Road Network Loading ✅

**Decision:** Support JSON file format with Hermite control points

**JSON Schema:**
```json
{
  "nodes": [{ "id": 0, "position": { "x": 0, "y": 0 } }],
  "segments": [{
    "id": 0,
    "startNodeId": 0,
    "endNodeId": 1,
    "controlPoints": {
      "p0": { "x": 0, "y": 0 },
      "t0": { "x": 50, "y": 0 },
      "p1": { "x": 100, "y": 0 },
      "t1": { "x": 50, "y": 0 }
    },
    "speedLimit": 25.0,
    "laneWidth": 3.5,
    "laneCount": 2
  }],
  "metadata": {
    "worldBounds": { "min": { "x": -500, "y": -500 }, "max": { "x": 500, "y": 500 } },
    "gridCellSize": 5.0
  }
}
```

**Loader:**
- `RoadNetworkLoader.LoadFromJson(path)` → `RoadNetworkBlob`
- Automatically builds spatial grid
- Precomputes distance LUTs for all segments

---

## Summary of All Design Choices

## Migration Path from Original Design

The original design document was written assuming a Unity DOTS-like architecture with explicit Job system support. The FDP/ModuleHost architecture differs in several ways:

### Key Differences:

| Original Design | FDP/ModuleHost Reality |
|---|---|
| NativeArray allocated per-job | NativeArray allocated per-system in OnCreate |
| Parallel jobs with Burst | Single-threaded systems (for now) |
| Structural changes deferred | Command buffer processed in Input phase |
| ComponentSystem in World A | Same - ComponentSystem has direct access |
| IModule in World B | Same - IModule uses ISimulationView |

### Migration Actions:

1. **Replace Unity.Mathematics.float2** → **System.Numerics.Vector2** ✓ (Done in this doc)
2. **Remove Burst/Jobs references** → Use standard foreach loops ✓
3. **Replace BlobAssets** → Use NativeArray or managed Dictionary (simplified) ✓
4. **Adapt QueryBuilder API** → Use FDP's `World.Query()` ✓
5. **Use FDP EventBus** → `[EventId]` attributes for commands ✓

---

## Performance Budget & Validation

### Target Metrics:

- **50,000 vehicles @ 60Hz** = 833 μs per frame total
- Budget breakdown:
  - SpatialHashSystem: 100 μs (hash build)
  - FormationTargetSystem: 50 μs (formation math for ~5k followers)
  - CarKinematicsSystem: 600 μs (main physics loop)
  - Other overhead: 83 μs

### Validation Plan:

1. **Benchmark baseline** (1k, 10k, 50k vehicles)
2. **Profile hot path** (identify cache misses, branch mispredictions)
3. **Measure GC allocations** (must be zero in steady state)
4. **Record replay** (verify tolerance ε_pos < 1e-3)

### Optimization Levers (if needed):

1. SIMD vectorization of RVO calculations
2. Spatial hash LOD (reduce avoidance radius for distant vehicles)
3. Formation update frequency reduction (update every N frames)
4. Multithreading with chunked queries

---

## Testing Strategy

### Unit Tests:

-  Bicycle model integration (verify position/heading after rotation)
- ✅ Pure Pursuit steering (verify steering angle calculation)
- ✅ RVO avoidance (verify no overlap in simple 2-vehicle scenario)
- ✅ Formation slot calculation (verify world position from leader frame)
- ✅ Road network LUT (verify constant-speed sampling)

### Integration Tests:

- ✅ Command processing (spawn → join formation → arrive at target)
- ✅ Collision avoidance (50 vehicles in confined space, no overlaps)
- ✅ Formation cohesion (convoy maintains spacing through turns)
- ✅ Replay determinism (record/replay produces identical results)

### Performance Tests:

- ✅ 50k vehicle sustained @ 60Hz (measure frame time)
- ✅ GC allocation profiling (zero allocations after warmup)
- ✅ Memory footprint (track NativeArray usage)

---

## Implementation Roadmap

### Phase 1: Core Infrastructure (Week 1)

- [ ] Define all data structures (VehicleState, NavState, FormationMember, etc.)
- [ ] Implement VehicleCommandSystem (spawn, despawn)
- [ ] Implement CarKinematicsSystem (basic movement, no avoidance)
- [ ] Unit tests for bicycle model

### Phase 2: Formation System (Week 2)

- [ ] Implement FormationRoster, FormationMember components
- [ ] Implement FormationTargetSystem (pull pattern)
- [ ] Implement formation commands (create, join, leave)
- [ ] Integration test: convoy follows leader

### Phase 3: Collision Avoidance (Week 3)

- [ ] Implement SpatialHashSystem
- [ ] Implement RVO-Lite in CarKinematicsSystem
- [ ] Integration test: dense traffic without collisions

### Phase 4: Road Network (Week 4)

- [ ] Implement RoadNetworkBuilder
- [ ] Implement road-following controllers
- [ ] Hermite spline evaluation + LUT
- [ ] Integration test: vehicles follow road curve

### Phase 5: Polish & Optimization (Week 5)

- [ ] Performance profiling & optimization
- [ ] Replay determinism validation
- [ ] Documentation & examples
- [ ] Production-ready deliverable

---

## Appendix: Quick Reference

### Key Constraints:
- **Max Formation Size:** 16 members
- **No Reverse Driving:** Speed clamped to [0, MaxSpeed]
- **Float Precision:** All math uses `float` (System.Numerics)
- **Replay Tolerance:** ε_pos = 1e-3 m, ε_ang = 1e-4 rad

### File Structure:
```
CarKinematics/
├── Core/
│   ├── VehicleState.cs          # Core component definitions
│   ├── VehicleParams.cs
│   └── NavState.cs
├── Formation/
│   ├── FormationComponents.cs   # Formation data structures
│   └── FormationTargetSystem.cs # Formation "pull" logic
├── Road/
│   ├── RoadNetworkBlob.cs       # Road data structures
│   ├── RoadNetworkBuilder.cs    # Road builder (setup phase)
│   └── RoadSampling.cs          # Hermite spline evaluation
├── Systems/
│   ├── CarKinematicsSystem.cs   # Main physics system
│   ├── SpatialHashSystem.cs     # Spatial indexing
│   └── VehicleCommandSystem.cs  # Command processing
├── Commands/
│   └── VehicleCommands.cs       # Event definitions
└── Modules/
    └── VehicleAIModule.cs       # Example AI module
```

---

## End of Document

**Status:** Ready for implementation.  
**Next Step:** Review clarifying questions in Section "Clarifying Questions & Design Decisions", then proceed to Phase 1 implementation.
