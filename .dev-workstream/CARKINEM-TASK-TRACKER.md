# Car Kinematics Module - Task Tracker

**Project:** CarKinem  
**Start Date:** 2026-01-06  
**Target:** 50k vehicles @ 60Hz, zero-GC hot path  
**Design Doc:** `docs/car-kinem-implementation-design.md`

---

## Batch Status

| Batch | Phase | Status | Started | Completed | Notes |
|-------|-------|--------|---------|-----------|-------|
| BATCH-CK-01 | Foundation | 🟢 COMPLETE | 2026-01-06 | 2026-01-06 | Core data structures |
| BATCH-CK-02 | Foundation | 🟢 COMPLETE | 2026-01-06 | 2026-01-06 | Math & controls |
| BATCH-CK-03 | Trajectory | 🟢 COMPLETE | 2026-01-06 | 2026-01-06 | Trajectory pool |
| BATCH-CK-04 | Road Network | 🟢 COMPLETE | 2026-01-06 | 2026-01-06 | JSON loading |
| BATCH-CK-05 | Road Navigation | 🟢 COMPLETE | 2026-01-07 | 2026-01-07 | State machine |
| BATCH-CK-06 | Spatial Hash | 🟢 COMPLETE | 2026-01-07 | 2026-01-07 | Collision detection |
| BATCH-CK-07 | Kinematics | 🟢 COMPLETE | 2026-01-07 | 2026-01-07 | Main physics system |
| BATCH-CK-08 | Formation | 🟢 COMPLETE | 2026-01-07 | 2026-01-07 | Formation support |
| BATCH-CK-FIX-01 | Corrections | 🟢 COMPLETE | 2026-01-07 | 2026-01-07 | Architectural fixes |
| BATCH-CK-09 | Commands | 🔵 READY | - | - | Event processing |
| BATCH-CK-10 | Integration | 🔵 READY | - | - | Demo app |

**Legend:** 🔵 Ready | 🟡 In Progress | 🟢 Complete | 🔴 Urgent | 🟠 Blocked | ⚪ Planned

---

## BATCH-CK-01: Foundation - Core Data Structures

**Objective:** Establish project structure and define all core data structures (Tier 1 unmanaged).

### Tasks

- [x] **CK-01-01**: Project setup (CarKinem library + test project)
  - Create `CarKinem.csproj` targeting net8.0
  - Reference `Fdp.Kernel`
  - Create `CarKinem.Tests.csproj` with xUnit
  - Verify build succeeds

- [ ] **CK-01-02**: Core enumerations
  - `NavigationMode` enum (None, RoadGraph, CustomTrajectory, Formation)
  - `RoadGraphPhase` enum (Approaching, Following, Leaving, Arrived)
  - `FormationType` enum (Column, Wedge, Line, Custom)
  - `FormationMemberState` enum (InSlot, CatchingUp, Rejoining, Waiting, Broken)
  - **Test:** Enum size validation (all must be `byte`)

- [ ] **CK-01-03**: Vehicle components
  - `VehicleState` struct (Position, Forward, Speed, SteerAngle, etc.)
  - `VehicleParams` struct (flyweight configuration)
  - `NavState` struct (Mode, Progress, Destination, etc.)
  - **Test:** Blittability validation
  - **Test:** Struct size verification (no padding issues)
  - **Test:** Default values correctness

- [ ] **CK-01-04**: Formation components
  - `FormationParams` struct
  - `FormationRoster` struct (unsafe fixed arrays)
  - `FormationMember` struct
  - `FormationTarget` struct
  - `FormationSlot` struct
  - **Test:** Blittability validation
  - **Test:** Fixed array access correctness
  - **Test:** FormationRoster capacity (16 members)

- [ ] **CK-01-05**: Trajectory components
  - `TrajectoryWaypoint` struct
  - `CustomTrajectory` struct
  - **Test:** Blittability validation
  - **Test:** Cumulative distance calculation

- [ ] **CK-01-06**: Road network components
  - `RoadSegment` struct (unsafe fixed LUT array)
  - `RoadNode` struct
  - `RoadNetworkBlob` struct with Dispose pattern
  - **Test:** Blittability validation
  - **Test:** Distance LUT array size (8 elements)
  - **Test:** Dispose implementation

### Acceptance Criteria

✅ All projects build without warnings  
✅ All structs are validated as blittable via unit tests  
✅ All struct sizes documented in test output  
✅ No managed references in Tier 1 components  
✅ Test coverage: 100% of data structures  

### Dependencies

- Fdp.Kernel (existing)
- System.Numerics.Vectors (built-in)

---

## BATCH-CK-02: Math & Control Algorithms

**Status:** 🔵 READY  
**Objective:** Implement core mathematical helpers and control algorithms.

### Tasks

- [ ] **CK-02-01**: Vector math utilities
  - `SignedAngle()` - signed angle between vectors
  - `Rotate()` - 2D rotation
  - `Perpendicular()`, `Right()` - perpendicular vectors
  - `SafeNormalize()` - zero-safe normalization
  - **Test:** All quadrants for SignedAngle
  - **Test:** Rotation accuracy

- [ ] **CK-02-02**: Pure Pursuit steering controller
  - Dynamic lookahead calculation
  - Geometric steering angle from curvature
  - Clamping to max steering angle
  - **Test:** Zero steering for straight paths
  - **Test:** Correct sign for left/right turns
  - **Test:** Clamping to max angle

- [ ] **CK-02-03**: Speed controller (P-controller)
  - Proportional speed error correction
  - Acceleration/deceleration clamping
  - **Test:** Positive accel when below target
  - **Test:** Negative accel when above target
  - **Test:** Clamping to limits

- [ ] **CK-02-04**: Bicycle model integration
  - Speed integration with no-reverse constraint
  - Angular velocity from steering
  - Forward vector rotation and re-normalization
  - **Test:** Position update accuracy
  - **Test:** Heading rotation correctness
  - **Test:** Speed clamping to zero (no reverse)

- [ ] **CK-02-05**: RVO-Lite collision avoidance
  - Time-to-collision calculation
  - Repulsion force from neighbors
  - Max speed clamping
  - **Test:** No change with no neighbors
  - **Test:** Deviation with obstacle
  - **Test:** Speed limit enforcement

### Acceptance Criteria

✅ 20+ unit tests covering all algorithms  
✅ Zero allocations in algorithm implementations  
✅ All edge cases tested (zero, negative, extreme values)  
✅ Build with zero warnings  
✅ Algorithm correctness validated  

### Dependencies

- BATCH-CK-01 (VehicleState, VehicleParams)

---

## BATCH-CK-03: Trajectory Pool System

**Status:** 🔵 READY  
**Objective:** Implement custom trajectory storage and sampling.

### Tasks

- [ ] **CK-03-01**: TrajectoryPoolManager implementation
  - Dictionary-based trajectory storage with unique IDs
  - Thread-safe registration with lock
  - Cumulative distance calculation during registration
  - NativeArray allocation for waypoint storage
  - **Test:** Unique ID assignment
  - **Test:** Thread-safe concurrent registration

- [ ] **CK-03-02**: Trajectory sampling
  - Linear interpolation between waypoints
  - Arc-length parameterization (progressS)
  - Looping trajectory support (modulo wraparound)
  - Tangent vector calculation and normalization
  - Speed interpolation
  - **Test:** Sample at start/end/midpoint
  - **Test:** Multi-segment cumulative distance
  - **Test:** Looping wraparound

- [ ] **CK-03-03**: Lifecycle management
  - RemoveTrajectory with proper disposal
  - Dispose pattern for cleanup
  - Count property for pool size
  - **Test:** Remove trajectory
  - **Test:** Dispose releases all resources
  - **Test:** No memory leaks

### Acceptance Criteria

✅ 16+ unit tests covering all scenarios  
✅ Thread-safe for concurrent trajectory sampling  
✅ Zero allocations in SampleTrajectory hot path  
✅ Proper NativeArray disposal in Dispose/Remove  
✅ Build with zero warnings  

### Dependencies

- BATCH-CK-01 (TrajectoryWaypoint, CustomTrajectory structs)
- Fdp.Kernel.Collections (NativeArray)

---

## BATCH-CK-04: Road Network Loading

**Status:** 🔵 READY  
**Objective:** JSON road network loading and builder.

### Tasks

- [ ] **CK-04-01**: JSON schema classes
  - RoadNetworkJson, RoadNodeJson, RoadSegmentJson
  - HermiteControlPointsJson, Vector2Json
  - RoadMetadataJson, BoundsJson
  - Proper JSON attributes for System.Text.Json
  - **Test:** JSON deserialization

- [ ] **CK-04-02**: RoadNetworkBuilder
  - AddNode(), AddSegment() methods
  - Hermite arc-length computation (trapezoidal integration)
  - Distance LUT precomputation (binary search)
  - Spatial grid rasterization (Bresenham-like)
  - Duplicate detection in grid cells
  - **Test:** Segment length accuracy (within 5%)
  - **Test:** LUT boundary values (0.0, 1.0)
  - **Test:** Spatial indexing correctness

- [ ] **CK-04-03**: RoadNetworkLoader
  - LoadFromJson() implementation
  - File.ReadAllText + JsonSerializer
  - Metadata parsing (grid size, bounds)
  - Error handling (file not found, invalid JSON)
  - **Test:** Valid file loading
  - **Test:** Missing file exception
  - **Test:** Property loading (speed, lanes)

- [ ] **CK-04-04**: Sample JSON test data
  - 3 nodes, 2 segments test file
  - Metadata with bounds and grid size
  - **Test:** Used as fixture in loader tests

### Acceptance Criteria

✅ 12+ unit tests covering all scenarios  
✅ Hermite length within 5% of analytical (for straight segments)  
✅ Distance LUT ranges from 0.0 to 1.0  
✅ Spatial grid correctly indexes all segments  
✅ JSON parsing handles errors gracefully  
✅ Build with zero warnings  

### Dependencies

- BATCH-CK-01 (RoadSegment, RoadNode, RoadNetworkBlob)
- System.Text.Json (built-in)

---

## BATCH-CK-05: Road Graph Navigation

**Status:** 🔵 READY  
**Objective:** Three-phase road navigation state machine.

### Tasks

- [ ] **CK-05-01**: RoadGraphNavigator implementation
  - FindClosestRoadPoint with spatial hash lookup
  - ProjectPointOntoHermiteSegment (sample-based)
  - EvaluateHermite, EvaluateHermiteTangent
  - SampleRoadSegment with LUT
  - UpdateRoadGraphNavigation state machine
  - **Test:** Hermite evaluation at boundaries (t=0, t=1)
  - **Test:** Tangent normalization

- [ ] **CK-05-02**: State machine transitions
  - Approaching phase (find entry, transition at 2m)
  - Following phase (progress tracking, lookahead)
  - Leaving phase (direct to destination)
  - Arrived phase (within arrival radius)
  - **Test:** Approaching → Following transition
  - **Test:** Following → Leaving transition
  - **Test:** Leaving → Arrived transition

- [ ] **CK-05-03**: Spatial lookup integration
  - FindClosestRoadPoint uses grid (3×3 cells)
  - Linked list iteration for segments
  - Distance calculation to curve
  - **Test:** Finds correct nearest segment
  - **Test:** Handles out-of-bounds positions

### Acceptance Criteria

✅ 10+ unit tests covering all scenarios  
✅ Hermite evaluation accurate at boundaries  
✅ State machine transitions correctly  
✅ Tangent vectors normalized  
✅ Spatial lookup efficient (uses grid)  
✅ Build with zero warnings  

### Dependencies

- BATCH-CK-01 (NavState, RoadGraphPhase)
- BATCH-CK-02 (VectorMath)
- BATCH-CK-04 (RoadNetworkBlob, RoadSegment)

---

## BATCH-CK-06: Spatial Hash System (PLANNED)

**Objective:** Spatial indexing for collision avoidance.

### Tasks (Draft)

- SpatialHashSystem implementation
- Grid build from VehicleState positions
- Neighbor query API
- Unit tests + performance benchmarks

---

## BATCH-CK-07: Car Kinematics System (PLANNED)

**Objective:** Main vehicle physics system with parallel execution.

### Tasks (Draft)

- CarKinematicsSystem implementation
- Navigation mode switching
- Parallel.ForEach execution
- Integration with all controllers
- Performance benchmarks (50k vehicles target)

---

## BATCH-CK-08: Formation Support (PLANNED)

**Objective:** Formation target calculation and member tracking.

### Tasks (Draft)

- FormationTargetSystem implementation
- FormationTemplateManager singleton
- Slot calculation helpers
- Unit tests

---

## BATCH-CK-09: Command Processing (PLANNED)

**Objective:** Event-based command API.

### Tasks (Draft)

- All command event structs
- VehicleCommandSystem implementation
- VehicleAPI facade
- Integration tests

---

## BATCH-CK-10: Integration & Example (PLANNED)

**Objective:** Complete example and performance validation.

### Tasks (Draft)

- Fdp.Examples.CarKinem example project
- Sample road network JSON
- AI module example
- Performance validation (50k @ 60Hz)
- Documentation

---

## Notes & Risks

### Current Risks
- None (not started)

### Technical Decisions Log
- 2026-01-06: Confirmed System.Numerics.Vector2 for all math
- 2026-01-06: Confirmed parallel execution from start (Parallel.ForEach)
- 2026-01-06: Confirmed 5.0m hardcoded spatial grid cell size
- 2026-01-06: Confirmed managed Dictionary for formation templates

### Performance Targets
- 50,000 vehicles @ 60Hz = 833 μs per frame total
- Spatial hash build: <100 μs
- Formation updates: <50 μs
- Kinematics: <600 μs
- Zero GC allocations in steady state

---

**Last Updated:** 2026-01-06 23:11  
**Next Review:** After BATCH-CK-01 completion
