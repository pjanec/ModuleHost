# Workstream Report - Batch 03

## Overview
This report details the implementation of the **Convoy Pattern** (Batch 03). This architecture optimization significantly reduces memory pressure and synchronization overhead for modules running at similar frequencies by enabling them to share immutable snapshots of the simulation state.

---

## Batch 03: Convoy Pattern & Snapshot Pooling

### Objectives
*   Implement a **Snapshot Pool** to reduce Garbage Collection (GC) pressure by reusing `EntityRepository` instances.
*   Implement the **Convoy Pattern** (`SharedSnapshotProvider`) to share a single snapshot instance among multiple "Slow" modules with the same update frequency.
*   Implement **Auto-Grouping** logic in the Kernel to automatically assign appropriate providers (GDB, SoD, or Shared) based on module tier and frequency.
*   Validate memory savings and correctness via integration tests.

### Implementation Details

#### 1. Snapshot Pooling (`SnapshotPool`)
*   **Thread-Safe Pooling**: Implemented a `ConcurrentStack<EntityRepository>` based pool to handle rapid acquire/release cycles from multiple threads.
*   **Warmup Capability**: Added `warmupCount` support to pre-allocate repositories during initialization.
*   **Schema Setup**: Integrated a schema setup delegate to ensure all pooled repositories are correctly initialized (components registered) before use.
*   **Event Cleanup**: IMPORTANT. Updated `EntityRepository.SoftClear()` to call `Bus.ClearAll()`. This ensures that pooled snapshots do not retain "Ghost Events" from previous frames.

#### 2. Convoy Pattern (`SharedSnapshotProvider`)
*   **Reference Counting**: Implemented a thread-safe `_activeReaders` counter. The underlying snapshot is only returned to the pool when all sharing modules have released their views.
*   **Lazy Synchronization**: The snapshot is acquired and synced from the live world only upon the *first* `AcquireView` call in a frame.
*   **Union Masking**: The provider computes a union of component requirements (`BitMask256`) from all participating modules.
*   **BitMask Optimization**: Added `BitwiseOr` to `BitMask256` struct.

#### 3. Kernel Auto-Grouping (`ModuleHostKernel`)
*   **Tier & Frequency Analysis**: During `Initialize()`, the kernel analyzes all registered modules.
*   **Grouping Logic**:
    *   **Fast Tier**: All grouped to use a single `DoubleBufferProvider` (GDB).
    *   **Slow Tier (Convoy)**: Modules with identical `UpdateFrequency` are grouped. A single `SharedSnapshotProvider` is created for each group.
    *   **Slow Tier (Isolated)**: Modules with unique frequencies use individual `OnDemandProvider` (SoD) instances.

---

## Developer Notes

### Design Decisions & Adaptations
1.  **Component Mask Defaulting**:
    *   **Issue**: The `IModule` interface does not currently expose which components a module requires.
    *   **Decision**: In `ModuleHostKernel.GetComponentMask`, I defaulted to a **Full Mask** (all 256 bits set).
    *   **Impact**: Correctness is guaranteed, but bandwidth optimization is pending `IModule` updates.

2.  **Kernel Schema Injection**:
    *   **Decision**: Added `SetSchemaSetup(Action<EntityRepository>)` to `ModuleHostKernel`. This allows the application setup phase to inject the schema registration logic required for new snapshots.

3.  **BitMask Utility Extension**:
    *   **Decision**: Added `BitwiseOr` to the `BitMask256` struct to efficiently calculate the Union Mask for convoys.

4.  **Pool Configuration**:
    *   **Decision**: Configured internal pools with hardcoded reasonable defaults (Size=10/5) for now.

### Troubles & Solutions
1.  **Interface Abstraction Leaks**:
    *   **Problem**: Unit tests needed to check `GlobalVersion` to verify synchronization.
    *   **Solution**: Cast `ISimulationView` to concrete `EntityRepository` in tests.

2.  **API Inconsistencies**:
    *   **Problem**: `OnDemandProvider` constructor parameter naming mismatch.
    *   **Solution**: Standardized on `initialPoolSize`.

3.  **Test Logic vs. Pooling**:
    *   **Problem**: `ProviderLeaseTests` originally asserted that releasing and re-acquiring a view yields a *different* instance.
    *   **Solution**: With `SnapshotPool`, the expected behavior is LIFO reuse (`Assert.Same`). Updated tests to reflect this logic.

4.  **Ghost Events**:
    *   **Problem**: Reusing `EntityRepository` without clearing the event bus left stale events.
    *   **Solution**: Implemented `FdpEventBus.ClearAll()` and called it from `EntityRepository.SoftClear()`.

---

## Architectural Feedback & Questions

### 1. Missing Module Dependency Metadata
**Observation**: The Convoy and SoD patterns rely heavily on `BitMask256` to filter data sync. Currently, we have no standard way for an `IModule` to declare its valid Read/Write dependencies.
**Recommendation**: Add `IEnumerable<Type> GetRequiredComponents()` to `IModule`.

### 2. Fast Tier Configuration
**Observation**: The implementation assumes all `ModuleTier.Fast` modules can share a single `DoubleBufferProvider`.
**Question**: Is it possible for a "Fast" module to have a custom frequency?
**Recommendation**: Clarify if Fast Tier implies strictly "Every Tick".

---

## Artifacts Modified/Created

**ModuleHost.Core**
*   `Providers/SnapshotPool.cs` (New)
*   `Providers/SharedSnapshotProvider.cs` (Modified)
*   `Providers/OnDemandProvider.cs` (Modified)
*   `ModuleHostKernel.cs` (Modified)

**Fdp.Kernel**
*   `BitMask256.cs` (Modified)
*   `FdpEventBus.cs` (Modified)
*   `EntityRepository.cs` (Modified)

**Tests**
*   `ModuleHost.Core.Tests/SnapshotPoolTests.cs` (New)
*   `ModuleHost.Core.Tests/SharedSnapshotProviderTests.cs` (New)
*   `ModuleHost.Core.Tests/ConvoyAutoGroupingTests.cs` (New)
*   `ModuleHost.Core.Tests/ConvoyIntegrationTests.cs` (New)
*   `ModuleHost.Core.Tests/ProviderLeaseTests.cs` (Updated)
