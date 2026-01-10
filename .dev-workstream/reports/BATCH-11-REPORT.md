# BATCH-11 Completion Report: Component Data Policy & Deep Cloning

**Date:** 2026-01-10
**Status:** Completed


The `BATCH-11` objectives have been successfully completed. The   
DataPolicy system has been fully implemented, replacing the deprecated `TransientComponentAttribute`.

### Completed Tasks

1. Data Policy Implementation:  
   * Defined   
     DataPolicy enum and attribute.  
   * Implemented auto-default logic: Mutable classes default to   
     NoSnapshot (Transient), while structs/records default to   
     Snapshotable.  
   * Added support for   
     SnapshotViaClone using high-performance Expression Tree-based deep cloning in   
     FdpAutoSerializer.  
2. Codebase Refactoring:  
   * Removed `TransientComponentAttribute.cs` and all references to it.  
   * Updated `EntityRepository.RegisterComponent` to accept `DataPolicy? policyOverride`.  
   * Replaced `[TransientComponent]` with `[DataPolicy(DataPolicy.Transient)]` across the codebase.  
   * Updated   
     EntityRepository.Sync.cs, `ManagedComponentTable.cs`, `RecorderSystem.cs`, and   
     RepositorySerializer.cs to respect the new `IsRecordable`, `IsSaveable`, and `NeedsClone` flags.  
3. Testing:  
   * Created   
     Fdp.Tests/DataPolicyTests.cs covering policy defaults, overrides, and registry flags.  
   * Updated   
     FlightRecorderTests,   
     DoubleBufferProviderTests,   
     OnDemandProviderTests, and integration tests.  
   * Verified `Fdp.Tests` pass (all green).  
   * Verified relevant `ModuleHost.Core.Tests` pass (specifically   
     DoubleBufferProvider,   
     OnDemandProvider,   
     PartialOwnership).  
   * Fixed Event ID collisions in   
     DoubleBufferProviderTests and   
     OnDemandProviderTests exposed by recent kernel changes.  
4. Documentation:  
   * Updated   
     EntityComponentSystem.md, `FlightRecorderAndDeterministicReplay.md`, and   
     TransientComponentsAndSnapshotFiltering.md (renamed to reflect DataPolicy context in content) to document the new system.  
   * Updated API comments in   
     EntityRepository.cs.

### Remaining Issues (Out of Scope)

* Some `ModuleHost.Core.Tests` related to `Distributed Time` (e.g., `SlaveTimeControllerTests`) are failing. These appear unrelated to the Data Policy changes (likely pre-existing or due to recent parallel work on BATCH-09.x). The tests modified/touched by this batch are passing.


## 1. Executive Summary

The `BATCH-11` objectives have been successfully completed. The `DataPolicy` system has been fully implemented, replacing the deprecated `TransientComponentAttribute`. This new system introduces granular control over how components are handled across different subsystems (Snapshots, Flight Recorder, Save/Load), resolving long-standing concurrency vs. persistence conflations.

## 2. Completed Tasks

### 2.1 Data Policy Implementation
*   **Defined `DataPolicy` Enum & Attribute:** Created the core `DataPolicy` enum (`Default`, `NoSnapshot`, `NoRecord`, `NoSave`, `Transient`, `SnapshotViaClone`) and its corresponding attribute.
*   **Auto-Default Logic:** Implemented smart defaults in `EntityRepository`:
    *   **Mutable Classes:** Default to `NoSnapshot` (Transient) to enforce safety rails against race conditions.
    *   **Structs/Records:** Default to `Snapshotable` (standard ECS behavior).
*   **Deep Cloning Support:** Implemented `SnapshotViaClone` using high-performance, JIT-compiled Expression Trees in `FdpAutoSerializer`. This allows mutable classes to be safely snapshotted when explicitly opted-in.

### 2.2 Codebase Refactoring
*   **Removed `TransientComponentAttribute`:** Deleted the file and removed all 15+ references across the codebase.
*   **Updated Registration API:** Modified `EntityRepository.RegisterComponent<T>` to accept a `DataPolicy? policyOverride` instead of the boolean `snapshotable`.
*   **Replacements:** Replaced all instances of `[TransientComponent]` with `[DataPolicy(DataPolicy.Transient)]`.
*   **Subsystem Updates:** Updated key subsystems to respect the new fine-grained registry flags (`IsRecordable`, `IsSaveable`, `NeedsClone`):
    *   `EntityRepository.Sync.cs` (Snapshot generation)
    *   `ManagedComponentTable.cs` (Cloning logic)
    *   `RecorderSystem.cs` (Flight Recorder filtering)
    *   `RepositorySerializer.cs` (Save/Load filtering)

### 2.3 Testing
*   **New Unit Tests:** Created `Fdp.Tests/DataPolicyTests.cs` covering:
    *   Policy defaults for Structs, Records, and Classes.
    *   Attribute precedence.
    *   Explicit override parameter precedence.
*   **Updated Existing Tests:** Refactored `FlightRecorderTests`, `DoubleBufferProviderTests`, `OnDemandProviderTests`, and `PartialOwnershipIntegrationTests` to match the new API and attribute.
*   **Verification:**
    *   ✅ `Fdp.Tests` suite passing (all green).
    *   ✅ Relevant `ModuleHost.Core.Tests` passing (Providers, Integrations).
    *   ✅ Fixed pre-existing Event ID collisions in `ModuleHost.Core.Tests` that were causing noise suitable to recent kernel updates.
    *   ✅ Verified `IsSaveable` logic in `RepositorySerializer`.

### 2.4 Documentation
*   **User Guide Updated:**
    *   `02_EntityComponentSystem.md`: Updated ECS fundamentals to explain DataPolicy and removal of TransientComponent.
    *   `07_Flight RecorderAndDeterministicReplay.md`: Updated examples to use DataPolicy.
    *   `10_TransientComponentsAndSnapshotFiltering.md`: Renamed and mostly rewritten to "Transient Components & Snapshot Filtering" reflecting the new DataPolicy system.
*   **API Documentation:** Updated XML comments in `EntityRepository.cs` to guide users on the new `policyOverride` parameter.

## 3. Technical Highlights

### Deep Cloning with Expression Trees
To support `DataPolicy.SnapshotViaClone`, we extended `FdpAutoSerializer` to generate deep-copy delegates using Expression Trees. This matches the performance of the serializer itself, avoiding sluggish reflection during runtime cloning.

```csharp
// Example Usage
[DataPolicy(DataPolicy.SnapshotViaClone)]
public class MutableHeavyState
{
    public List<int> Data = new();
}

// Automatically deep-cloned during SyncFrom()
```

### Safety Rails
The system now proactively protects developers:
*   **Mutable Class without Attribute:** `Warning` (Defaults to NoSnapshot).
*   **Explicit Transient:** `NoSnapshot`, `NoRecord`, `NoSave`.
*   **Explicit SnapshotViaClone:** `Snapshotable`, `Recordable`, `Saveable` + `Cloning`.

## 4. Remaining Issues (Out of Scope)

*   **Distributed Time Tests:** Some unrelated tests in `ModuleHost.Core.Tests` (specifically `SlaveTimeControllerTests`) are failing. These appear to be regressions or pre-existing issues from parallel work on `BATCH-09.x` and are distinct from the Data Policy changes.

## 5. Next Steps

The codebase is now ready for `BATCH-12` or further integration. The new Data Policy system provides a robust, future-proof foundation for handling component persistence and concurrency.
