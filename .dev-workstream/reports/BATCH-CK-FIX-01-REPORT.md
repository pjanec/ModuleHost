# BATCH-CK-FIX-01 Implementation Report

## Status
- **Result:** Success
- **Tests Passed:** 87/87 (All `CarKinem` and `Fdp.Kernel` tests)
- **Build Status:** Clean (0 Errors, 0 Warnings)

## Architectural Corrections Implemented

### 1. System Dependencies Refactor
- **Change:** Removed direct dependency of `CarKinematicsSystem` on `SpatialHashSystem`.
- **Implementation:** Introduced `SpatialGridData` singleton (unmanaged) to pass the `SpatialHashGrid` from `SpatialHashSystem` (producer) to `CarKinematicsSystem` (consumer).
- **Benefit:** Decouples systems and adheres to Data-Oriented Design principles using global data components.

### 2. Entity Storage Safety
- **Change:** Updated `FormationRoster` to store full `Entity` handles (index + generation) instead of raw `int` IDs.
- **Implementation:** Changed `fixed int MemberEntityIds[16]` to `fixed long MemberEntities[16]` (reinterpreted as `Entity`).
- **Helpers:** Added `FormationRosterExtensions` with `SetMember`, `GetMember`, `GetSlotIndex`, `SetSlotIndex` for safe access to fixed buffers.
- **Benefit:** Prevents stale reference issues common with reusing entity indices.

### 3. Parallel Iteration Optimization
- **Change:** Replaced `Parallel.ForEach` with FDP's `ForEachParallel` in `CarKinematicsSystem`.
- **Implementation:** Leverages `EntityQuery`'s zero-GC parallel iterator for high-performance updates.
- **Benefit:** Reduces GC pressure and overhead during simulation loops.

### 4. Unified Singleton API (Tier 1 Update)
- **Change:** Added `SetSingleton<T>`, `GetSingleton<T>`, and `HasSingleton<T>` to `EntityRepository` and `UnsafeShim`.
- **Implementation:** 
  - Implemented unified dispatch logic in `EntityRepository` to handle both Managed and Unmanaged singletons seamlessly.
  - Renamed conflicting `HasSingleton<T>` to `HasSingletonUnmanaged<T>` to resolve ambiguity.
  - Updated `UnsafeShim` to dynamically bind to the correct internal methods.
- **Benefit:** Simplifies singleton access for module developers and standardizes the API.

### 5. System Attributes
- **Change:** Applied proper scheduling attributes to systems.
- **Implementation:**
  - `CarKinematicsSystem`: `[UpdateInGroup(typeof(SimulationSystemGroup))]`, `[UpdateAfter(typeof(SpatialHashSystem))]`, `[UpdateAfter(typeof(FormationTargetSystem))]`.
  - `FormationTargetSystem`: `[UpdateInGroup(typeof(SimulationSystemGroup))]`.
- **Benefit:** Ensures deterministic execution order without manual "System Groups" management.

### 6. API Semantic Clarity
- **Change:** Adopted `SetComponent<T>` alias usage.
- **Implementation:** Replaced `AddComponent<T>` with `SetComponent<T>` in update loops where semantic intent is to "set or update" state.
- **Benefit:** clearer implementation intent.

## File Changes
- `CarKinem\Systems\CarKinematicsSystem.cs`: Major refactor for dependencies and iteration.
- `CarKinem\Systems\FormationTargetSystem.cs`: Updated for new Roster API and attributes.
- `CarKinem\Systems\SpatialHashSystem.cs`: Writes `SpatialGridData` singleton.
- `CarKinem\Formation\FormationRoster.cs`: Struct layout update.
- `CarKinem\Formation\FormationRosterExtensions.cs`: New helper class.
- `Fdp.Kernel\EntityRepository.cs`: Added Unified Singleton API.
- `Fdp.Kernel\Internal\UnsafeShim.cs`: Added Singleton delegate support.
- `CarKinem.Tests\Systems\CarKinematicsSystemTests.cs`: Updated tests for singletons.
- `CarKinem.Tests\DataStructures\FormationComponentsTests.cs`: Updated tests for new Roster API.
- `CarKinem\Trajectory\TrajectoryPoolManager.cs`: Fixed nullable warning.
- `Fdp.Kernel\StandardSystemGroups.cs`: Added missing standard system groups.

## Verification
- Verified that `CarKinematicsSystem` correctly reads the grid from the singleton.
- Verified that formation members are correctly tracked via full `Entity` handles.
- Confirmed all unit tests pass, ensuring no regression in kinematics or formation logic.
