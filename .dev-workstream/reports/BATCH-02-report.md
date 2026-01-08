# Workstream Report - Batch 01 & 02

## Overview
This report details the implementation of **Component Dirty Tracking** (Batch 01) and **Reactive Module Scheduling** (Batch 02). These features are critical for optimizing the ModuleHost architecture, enabling systems to run only when necessary based on data changes or events, rather than polling every frame.

---

## Batch 01: Component Dirty Tracking

### Objectives
*   Enable efficient detection of modifications in component data tables.
*   Avoid performance penalties on write operations (no per-write flags).
*   Provide a type-safe API for querying changes from the main simulation loop.

### Implementation Details

#### 1. Core Dirty Tracking (`NativeChunkTable` & `ComponentTable`)
*   **Lazy Scan Architecture**: Instead of setting a dirty flag on every component write (which causes cache thrashing and overhead), we utilize the existing `Version` tracking per chunk.
*   **Lazy Scan Logic**: The `HasChanges(uint sinceVersion)` method iterates through the `_chunkVersions` array.
*   **Performance**:
    *   **Complexity**: O(Chunks) where Chunks << Entities. For 100k entities, this is ~6 chunk checks.
    *   **Cache Friendliness**: Scans a contiguous `uint[]` array, maximizing L1 cache hits.
    *   **Concurrency**: Read-only scan is safe during single-threaded phases; thread-safe with respect to chunk allocation.

#### 2. EntityRepository Integration
*   Added `HasComponentChanged(Type componentType, uint sinceTick)` to `EntityRepository`.
*   This acts as a high-level facade, performing type lookup and delegating to the specific underlying `IComponentTable`.

#### 3. Interface Updates
*   Updated `IComponentTable` interface to include:
    ```csharp
    bool HasChanges(uint sinceVersion);
    ```
*   Ensured both `NativeChunkTable<T>` (Unmanaged) and `ManagedComponentTable<T>` (Managed) implement this contract.

### Verification
*   **Unit Tests**: `Fdp.Tests.ComponentDirtyTrackingTests`
    *   `NativeChunkTable_HasChanges_DetectsWrite`: Verifies basic version comparison logic.
    *   `EntityRepository_HasComponentChanged_DetectsTableChanges`: Verifies repository-level integration.
    *   `ComponentDirtyTracking_PerformanceScan`: Confirmed scan speed is < 200ns per call.
    *   `ComponentDirtyTracking_NoCacheContention_ConcurrentWrites`: Verified thread safety under load.

---

## Batch 02: Reactive Scheduling & Event Bus Tracking

### Objectives
*   Enable modules to define **Triggers** for execution (e.g., "Run only when Event X occurs" or "Run only when Component Y changes").
*   Implement `HasEvent<T>` tracking in the Event Bus with O(1) lookup cost.
*   Update the Kernel scheduler to respect these triggers.

### Implementation Details

#### 1. FdpEventBus Active Tracking
*   **Challenge**: The Event Bus is designed for high-throughput write-only access during simulation. Reading requires checking streams.
*   **Solution**: Implemented a Frame-Summary approach.
    *   Added `_activeEventIds` (HashSet) to `FdpEventBus`.
    *   Modified `SwapBuffers()` (called once per frame) to scan all streams. If a stream has data, its ID is added to the active set.
    *   **API**: Added `HasEvent<T>()` (Unmanaged), `HasManagedEvent<T>()` (Managed), and `HasEvent(Type)` (Dynamic/Cached).
    *   **Performance**: Lookup is O(1) during the Dispatch phase. Construction is O(Streams) (linear with number of event types, not events).

#### 2. Module Execution Policy Enhancements
*   Updated `ModuleExecutionPolicy` struct in `ModuleHost.Core.Abstractions`:
    *   Added `TriggerType`: `Always`, `Interval`, `OnEvent`, `OnComponentChange`.
    *   Added `TriggerArg`: The `Type` of the event or component to monitor.
    *   Added `IntervalMs`: For time-based throttling of Async modules.
*   Added static factory methods for easier configuration:
    *   `ModuleExecutionPolicy.OnEvent<T>()`
    *   `ModuleExecutionPolicy.OnComponentChange<T>()`
    *   `ModuleExecutionPolicy.FixedInterval(int ms)`

#### 3. Kernel Scheduler Logic (`ModuleHostKernel`)
*   Refactored `ShouldRunThisFrame(ModuleEntry entry)`:
    *   **Interval**: Checks `entry.AccumulatedDeltaTime >= policy.IntervalMs`.
    *   **OnEvent**: Checks `_liveWorld.Bus.HasEvent(policy.TriggerArg)`.
    *   **OnComponentChange**: Checks `_liveWorld.HasComponentChanged(policy.TriggerArg, entry.LastRunTick)`.
*   **Async Version Tracking Fix**:
    *   Async modules span multiple frames. Detecting changes requires tracking from the *last time checking started*.
    *   Moved `LastRunTick` assignment to the **Dispatch** phase (start of task) using `GlobalVersion`.
    *   This ensures that `HasComponentChanged` captures all modifications that occurred while the async module was busy or waiting.

### Verification
*   **Unit Tests**:
    *   `Fdp.Tests.EventBusActiveTrackingTests`: Verifies `HasEvent` returns false initially, true after Publish+Swap, and false again next frame.
    *   `ModuleHost.Core.Tests.ReactiveSchedulingTests`:
        *   `ShouldRun_Interval_RespectsTime`: Verifies accumulation logic.
        *   `ShouldRun_OnEvent_TriggersOnlyWhenEventPresent`: Verifies integration with EventBus.
        *   `ShouldRun_OnComponentChange_TriggersOnlyWhenChanged`: Verifies integration with EntityRepository dirty tracking.

---

## Artifacts Modified/Created

**Fdp.Kernel**
*   `NativeChunkTable.cs` (Modified)
*   `ManagedComponentTable.cs` (Modified)
*   `IComponentTable.cs` (Modified)
*   `ComponentTable.cs` (Modified)
*   `EntityRepository.cs` (Modified)
*   `FdpEventBus.cs` (Modified)
*   `EventType.cs` (Viewed/Verified)

**ModuleHost.Core**
*   `Abstractions/ModuleExecutionPolicy.cs` (Modified)
*   `ModuleHostKernel.cs` (Modified)

**Tests**
*   `Fdp.Tests/ComponentDirtyTrackingTests.cs` (New)
*   `Fdp.Tests/EventBusActiveTrackingTests.cs` (New)
*   `ModuleHost.Core.Tests/ReactiveSchedulingTests.cs` (New)
