# BATCH-06 Report: Entity Lifecycle Manager (ELM)

## Executive Summary
This batch successfully implemented the **Entity Lifecycle Manager (ELM)**, a critical subsystem for coordinating entity creation and destruction across distributed modules. The ELM ensures that entities are fully initialized by all participating modules (e.g., Physics, AI, Network) before becoming `Active` in the simulation, and properly cleaned up before removal.

## Features Implemented

### 1. Cooperative Lifecycle Protocol
- **Event Definitions**: Implemented high-performance, unmanaged event structs in `LifecycleEvents.cs`:
  - `ConstructionOrder`: Signals start of entity setup.
  - `ConstructionAck`: Module response indicating success/failure.
  - `DestructionOrder`: Signals start of teardown.
  - `DestructionAck`: Module response indicating cleanup complete.
- **Unmanaged Compatibility**: Used `FixedString64` for error messages and reasons to ensure compatibility with `FdpEventBus`'s unmanaged requirement.

### 2. Kernel Enhancements
- **Entity Header Update**: Modified `EntityHeader` to include `EntityLifecycle LifecycleState` (Constructing, Active, TearDown).
- **Default States**: 
  - `CreateEntity()` defaults to `Active`.
  - `CreateStagedEntity()` defaults to `Constructing`.
- **Query Filtering**: Updated `EntityQuery` and `QueryBuilder` to support high-speed filtering by lifecycle state:
  - `.WithLifecycle(EntityLifecycle.Active)` (Default)
  - `.IncludeConstructing()`
  - `.IncludeTearDown()`
  - `.IncludeAll()`
- **Command Buffer**: Extended `IEntityCommandBuffer` with `SetLifecycleState` to support state transitions during playback.

### 3. Entity Lifecycle Module (ELM)
- **Central Coordinator**: Implemented `EntityLifecycleModule` to:
  - Track pending entities waiting for ACKs.
  - Manage a registry of participating modules.
  - Handle timeouts (default 5 seconds/300 frames) to prevent stalled entities.
- **Lifecycle System**: Created `LifecycleSystem` running in the `BeforeSync` phase to process ACKs and trigger state transitions (Constructing -> Active) or final destruction.
- **Error Handling**: Implemented logic to immediately abort construction and destroy entities if any module reports failure (NACK) or times out.

## Technical Details

### State Transitions
The lifecycle flow is now strictly state-managed:
1. **Spawn**: `SpawnerSystem` calls `elm.BeginConstruction()`. Entity created in `Constructing` state.
2. **Order**: ELM publishes `ConstructionOrder`.
3. **Module Processing**: Modules (Physics, AI, etc.) receive order, perform setup, and publish `ConstructionAck`.
4. **Activation**: When ELM receives ACKs from ALL participating modules, it sets entity state to `Active`.

### Performance Optimizations
- **Bitwise Filtering**: Lifecycle filtering in `EntityQuery` uses efficient O(1) checks in the hot loop.
- **Unmanaged Events**: All lifecycle events are unmanaged structs, allowing zero-garbage event passing via `FdpEventBus`.

## Verification & Testing

### Unit Tests
- `LifecycleEventsTests`: Verified event struct layout, EventIDs, and `FixedString64` serialization.
- `EntityQueryLifecycleTests`: Verified that queries correctly include/exclude entities based on `Constructing`, `Active`, and `TearDown` states.
- `EntityLifecycleModuleTests`: Verified internal logic for ACK tracking, timeout detection, and abort-on-failure.

### Integration Tests
- `EntityLifecycleIntegrationTests`: 
  - **Scenario**: Simulated a multi-module environment (Physics, AI, Network) coordinating a spawn.
  - **Result**: Confirmed that the entity remained in `Constructing` state until all 3 mock modules sent ACKs, after which it automatically transitioned to `Active`.

## Status
- [x] Lifecycle Events Defined
- [x] Kernel Lifecycle State & Query Support
- [x] EntityLifecycleModule Implementation
- [x] LifecycleSystem Implementation
- [x] Integration Tests Passing

**Result**: Ready for merge.
