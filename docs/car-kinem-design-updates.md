# Car Kinematics Design Update Summary

**Date:** 2026-01-06  
**Document:** `car-kinem-implementation-design.md`  
**Status:** ✅ Updated with all clarified requirements

---

## Changes Made

### 1. ✅ Navigation System - Three Modes

**Added NavigationMode enum:**
```csharp
public enum NavigationMode : byte
{
    None = 0,
    RoadGraph = 1,      // Follow road network (approach → follow → leave)
    CustomTrajectory = 2, // Follow custom trajectory from pool
    Formation = 3       // Follow formation target (overrides other modes)
}
```

**Updated NavState component:**
- Added `NavigationMode Mode` field
- Added `RoadGraphPhase RoadPhase` for state machine
- Added `FinalDestination` and `ArrivalRadius` for road graph mode
- Added `CurrentSegmentId` to track road segment

---

### 2. ✅ Custom Trajectory System

**Added TrajectoryPool components:**
- `TrajectoryWaypoint` struct (position, tangent, speed, cumulative distance)
- `CustomTrajectory` struct (ID, waypoints, length, looped flag)
- `TrajectoryPoolManager` singleton class

**Key Features:**
- Register trajectories: `RegisterTrajectory(positions, speeds, looped)` → returns ID
- Sample trajectories: `SampleTrajectory(id, progressS)` → returns (pos, tangent, speed)
- Linear interpolation between waypoints
- Supports looped trajectories
- Zero-allocation after registration

---

### 3. ✅ JSON Road Network Loading

**Added complete JSON schema:**
```json
{
  "nodes": [...],
  "segments": [{ "controlPoints": { "p0", "t0", "p1", "t1" }, ... }],
  "metadata": { "worldBounds", "gridCellSize" }
}
```

**Added RoadNetworkLoader class:**
- `LoadFromJson(jsonPath)` → `RoadNetworkBlob`
- Deserializes JSON using System.Text.Json
- Auto-builds spatial grid with metadata dimensions
- Precomputes distance LUTs for all segments

---

### 4. ✅ Road Graph Navigation Algorithm

**Added RoadGraphNavigator helper class:**

**Three-phase state machine:**
1. **Approaching:** Find closest road entry point → drive to it
2. **Following:** Follow road segments using Hermite spline evaluation
3. **Leaving:** Exit road at closest point to destination → drive directly to target
4. **Arrived:** Within arrival radius of final destination

**Key methods:**
- `FindClosestRoadPoint(position, roadNetwork)` → finds nearest road segment
- `SampleRoadSegment(segment, progressS)` → evaluates Hermite spline with LUT
- `UpdateRoadGraphNavigation(nav, currentPos, roadNetwork)` → executes state machine

---

### 5. ✅ Updated Command API

**Added new command events:**
- `CmdNavigateViaRoad` (5005) - Navigate using road network
- `CmdFollowTrajectory` (5006) - Follow custom trajectory
- `CmdNavigateToPoint` (5004) - Direct point-to-point (renamed from CmdNavigateTo)
- `CmdStop` (5007) - Stop current navigation

**Added VehicleAPI facade:**
```csharp
public static class VehicleAPI
{
    static int RegisterTrajectory(positions, speeds, looped);
    static void NavigateViaRoad(entityId, destination, arrivalRadius);
    static void FollowTrajectory(entityId, trajectoryId, startProgress);
    static void NavigateToPoint(entityId, target, speed);
    static void JoinFormation(followerId, leaderId, slotIndex);
    static void Stop(entityId);
}
```

---

### 6. ✅ Parallel Execution in CarKinematicsSystem

**Updated OnUpdate() method:**
```csharp
var entities = query.ToEntityArray();
Parallel.ForEach(entities, (e) => {
    ref var state = ref stateSpan[e.Index];
    ref var nav = ref navSpan[e.Index];
    
    // Switch on nav.Mode (RoadGraph, CustomTrajectory, Formation)
    // ... physics calculations ...
});
```

**Thread-safety guarantees:**
- Pre-snapshot entity list before parallel iteration
- Direct span access to component tables
- Each thread writes to unique entity index
- Shared read-only data (SpatialHash, RoadNetwork, TrajectoryPool)

---

### 7. ✅ Confirmed Design Decisions

All five clarifying questions resolved:

| Decision | Choice |
|----------|--------|
| Road Network | ✅ Static (never changes) |
| Formation Templates | ✅ Option A (Managed Dictionary) |
| Trajectory System | ✅ Full three-mode system (Road/Custom/Formation) |
| Parallel Execution | ✅ Parallel.ForEach from start |
| Spatial Grid Cell Size | ✅ Hardcoded 5.0m |

---

## API Usage Examples

### Load Road Network from JSON
```csharp
var roadNetwork = RoadNetworkLoader.LoadFromJson("maps/city_roads.json");
carKinematicsSystem.SetRoadNetwork(roadNetwork);
```

### Register Custom Trajectory
```csharp
Vector2[] path = new[] {
    new Vector2(0, 0),
    new Vector2(100, 50),
    new Vector2(200, 100)
};
float[] speeds = new[] { 10f, 15f, 10f };
int trajId = VehicleAPI.RegisterTrajectory(path, speeds, looped: false);
```

### Command Vehicle Modes
```csharp
// Navigate via road network
VehicleAPI.NavigateViaRoad(vehicleId, destination: new Vector2(500, 500));

// Follow custom trajectory
VehicleAPI.FollowTrajectory(vehicleId, trajId, startProgress: 0);

// Direct navigation (no roads)
VehicleAPI.NavigateToPoint(vehicleId, target: new Vector2(100, 100), speed: 20f);

// Join formation
VehicleAPI.JoinFormation(followerId: vehicle2, leaderId: vehicle1, slotIndex: 0);
```

---

## File Structure (Updated)

```
CarKinematics/
├── Core/
│   ├── VehicleState.cs          # Updated with all enums
│   ├── NavState.cs               # NEW: NavigationMode, RoadGraphPhase
│   └── VehicleParams.cs
├── Formation/
│   ├── FormationComponents.cs
│   ├── FormationTemplateManager.cs  # NEW: Managed singleton
│   └── FormationTargetSystem.cs
├── Trajectory/
│   ├── TrajectoryPool.cs         # NEW: Custom trajectory storage
│   └── TrajectoryPoolManager.cs  # NEW: Singleton manager
├── Road/
│   ├── RoadNetworkBlob.cs
│   ├── RoadNetworkBuilder.cs
│   ├── RoadNetworkLoader.cs      # NEW: JSON loading
│   └── RoadGraphNavigator.cs     # NEW: Approach/follow/leave logic
├── Systems/
│   ├── CarKinematicsSystem.cs    # UPDATED: Parallel execution, 3 modes
│   ├── SpatialHashSystem.cs
│   └── VehicleCommandSystem.cs   # UPDATED: New commands
├── Commands/
│   └── VehicleCommands.cs        # UPDATED: 7 command types
└── API/
    └── VehicleAPI.cs             # NEW: High-level facade
```

---

## Next Steps for Implementation

1. **Week 1 - Core + Trajectory:**
   - Implement NavigationMode enum and NavState updates
   - Implement TrajectoryPoolManager
   - Implement RoadNetworkLoader (JSON parsing)
   - Unit tests for trajectory sampling

2. **Week 2 - Road Navigation:**
   - Implement RoadGraphNavigator
   - Implement Hermite spline evaluation
   - Implement state machine (Approaching → Following → Leaving)
   - Unit tests for road graph navigation

3. **Week 3 - Integration:**
   - Update CarKinematicsSystem with mode switching
   - Implement parallel execution with Parallel.ForEach
   - Update VehicleCommandSystem with new commands
   - Integration tests with all 3 modes

4. **Week 4 - Formation (unchanged):**
   - FormationTargetSystem
   - FormationTemplateManager
   - Integration tests

5. **Week 5 - Polish:**
   - Performance profiling
   - Replay determinism validation
   - API documentation
   - Example JSON road networks

---

## Key Improvements Over Original Design

1. **✅ Explicit mode system** - Clear separation of Road/Custom/Formation navigation
2. **✅ JSON loading** - Easy road network authoring and editing
3. **✅ Three-phase road navigation** - Natural approach/follow/leave flow
4. **✅ Custom trajectories** - Flexible waypoint system with looping support
5. **✅ Parallel-first** - Multi-core utilization from day one
6. **✅ Clean API facade** - Simple VehicleAPI for game code

---

## Document Status

**✅ COMPLETE AND READY FOR IMPLEMENTATION**

All design ambiguities resolved. All algorithms specified. All data structures defined. Ready to proceed to Phase 1 implementation.
