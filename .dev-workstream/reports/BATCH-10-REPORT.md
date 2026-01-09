# BATCH-10 Report: Transient Components & Snapshot Filtering

### Summary of Changes

1. Transient Component Convention:  
   * C\# Records: Automatically detected as `snapshotable` (immutable safe).  
   * C\# Classes: MUST be marked with `[TransientComponent]` attribute or registration throws an informative `InvalidOperationException`.  
   * Explicit Override: The `snapshotable` parameter in   
     RegisterComponent overrides all conventions.  
   * Implemented via `ComponentTypeRegistry.IsRecordType` and logic in `EntityRepository.RegisterComponent`.  
2. Snapshot Filtering Logic:  
   * Default Exclusion: `EntityRepository.SyncFrom` now automatically filters out transient components unless `includeTransient: true` is explicitly passed or an explicit mask is provided (which also filters transient by default for safety).  
   * Header Sanitization: `EntityIndex.ApplyComponentFilter` was fixed to ensure correctness when switching masks (e.g. toggling debug views) by invalidating chunk versions, forcing a header refresh from source on the next sync. This prevents "stale mask" bugs.  
3. Flight Recorder Integration:  
   * RecorderSystem: Updated to sanitize Entity Headers before writing to disk, ensuring that transient component bits are stripped from both Keyframes and Delta frames. This prevents "phantom component" errors during playback.  
4. Verification:  
   * Unit Tests: comprehensive suite added/updated:  
     * TransientComponentAttributeTests: Validates attribute detection, record convention, and overrides.  
     * `ComponentTypeRegistryTests`: Verifies   
       IsSnapshotable flag tracking.  
     * EntityRepositorySyncTests: Verifies correct filtering behavior and per-snapshot overrides.  
     * FlightRecorderTests: Confirms transient components are excluded from recordings.  
     * `DoubleBufferProviderTests`: Ensures standard providers respect the exclusion.

All tests passed successfully, confirming the robustness of the implementation.  


## Status
**Completed** ✅

## Summary
Implemented a robust mechanism to exclude "Transient Components" from snapshot operations (GDB, DoubleBuffer, FlightRecorder). This ensures thread safety for mutable managed components and optimizes snapshot performance.

## Key Features Implemented

1.  **Transient Component Detection**
    *   **[TransientComponent] Attribute:** Explicitly marks a component as transient.
    *   **Convention-Based Safety:**
        *   **Records:** Automatically treated as snapshotable (immutable).
        *   **Classes:** MUST be marked with `[TransientComponent]` or registration throws an error.
        *   **Structs:** Snapshotable by default.
    *   **Explicit Overrides:** `snapshotable` parameter in `RegisterComponent` overrides all conventions.

2.  **Snapshot Filtering Logic**
    *   **EntityRepository.SyncFrom:**
        *   Updated to accept `includeTransient` (debug override) and `excludeTypes` (optimization).
        *   **Safety Rule:** Explicit masks passed to SyncFrom now *automatically exclude* transient components unless `includeTransient: true` is set.
    *   **EntityIndex.ApplyComponentFilter:**
        *   Fixed to **invalidate chunk versions** when applying filters. This ensures that if a filter changes (e.g., toggling debug view), the repository correctly re-fetches headers from source.

3.  **Flight Recorder Integration**
    *   **Header Sanitization:** `RecorderSystem` now automatically strips transient component bits from Entity Headers before writing them to disk.
    *   **Delta Recording:** Delta frames also respect the transient filter.

4.  **Provider Updates**
    *   **DoubleBufferProvider:** Now uses default filtering (excludes transient).
    *   **OnDemandProvider:** Exclusion logic verified via SyncFrom safety rules.

## Tests

| Test Suite | Status | Notes |
| :--- | :--- | :--- |
| `TransientComponentAttributeTests` | ✅ Pass | Validates detection logic (Attribute, Record vs Class, Overrides) |
| `ComponentTypeRegistryTests` | ✅ Pass | Verifies `IsSnapshotable` flag tracking |
| `EntityRepositorySyncTests` | ✅ Pass | Verifies SyncFrom logic, bitmask filtering, and safety rules |
| `FlightRecorderTests` | ✅ Pass | Verifies transient components are not written to recordings |
| `DoubleBufferProviderTests` | ✅ Pass | Regression testing for providers |
| `OnDemandProviderTests` | ✅ Pass | Verified `AcquireView` respects transient filters even with partial masks |

## Technical Details

*   **API Changes:**
    *   `EntityRepository.RegisterComponent<T>(bool? snapshotable = null)`
    *   `EntityRepository.RegisterManagedComponent<T>(bool? snapshotable = null)`
    *   `EntityRepository.SyncFrom(source, mask, includeTransient, excludeTypes)`
*   **Performance:**
    *   Filtering adds negligible overhead (BitMask operations).
    *   Version invalidation ensures correctness at the cost of re-copying headers (4.8MB/frame for 100k entities), which is well within bandwidth limits.
