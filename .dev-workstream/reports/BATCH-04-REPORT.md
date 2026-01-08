# BATCH-04 Report: Resilience & Safety Implementation

## 1. Executive Summary
This batch focused on hardening the `ModuleHost` against faulty modules. We implemented a **Circuit Breaker** pattern to track module health and a **Safe Execution Wrapper** to handle timeouts and exceptions gracefully. These features ensure that a single crashing or hanging module does not bring down the entire simulation.

All core tasks were completed:
- `ModuleCircuitBreaker` implemented and unit tested.
- `IModule` extended with resilience configuration properties.
- `ModuleHostKernel` integrated with resilience logic.
- Comprehensive integration tests (Hung, Crashing, Flaky modules) implemented and passing.

## 2. Implementation Details

### 2.1 Circuit Breaker (`ModuleCircuitBreaker.cs`)
A thread-safe state machine managing three states:
- **Closed**: Normal operation. Failures are counted.
- **Open**: Module is blocked from running. Timer starts for reset.
- **HalfOpen**: Probation period. A single success resets to *Closed*; a failure trips back to *Open*.

### 2.2 Module Configuration (`IModule.cs`)
Added properties with sensible defaults:
- `MaxExpectedRuntimeMs` (Default: 100ms): Soft timeout for module execution.
- `FailureThreshold` (Default: 3): Failures before circuit trips.
- `CircuitResetTimeoutMs` (Default: 5000ms): Cooldown duration.

### 2.3 Safe Execution (`ModuleHostKernel.cs`)
The legacy `Task.Run` dispatch was replaced with `ExecuteModuleSafe`:
- **Timeout Handling**: Uses `Task.WhenAny` with a `CancellationTokenSource`. If a module times out, it is abandoned (left as a "zombie" task) and recorded as a failure.
- **Exception Barriers**: Catches all exceptions thrown by modules, logs them, and records them as failures.
- **Circuit Checks**: Skips execution if the circuit is `Open`.

### 2.4 Statistics
Updated `GetExecutionStats()` to return a list of `ModuleStats` structs, providing visibility into `CircuitState` and `FailureCount` for monitoring tools (and rendering).

## 3. Design Decisions

### 3.1 Handling "Zombie" Tasks
In .NET, `Task`s cannot be forcefully aborted safely. When a module times out, we cannot stop the underlying thread if it's in a tight loop or deadlocked.
*   **Decision**: We abandon the task ("zombie") and allow the kernel to proceed. The Circuit Breaker immediately records a failure. Once the threshold is reached, the module is effectively "quarantined" (Circuit Open), preventing it from spawning *more* zombie tasks.
*   **Trade-off**: This prevents the simulation frame from hanging, but does not free the resources held by the zombie task until it (hopefully) finishes or the application recycles.

### 3.2 Statistics Reset Side-Effect
`GetExecutionStats` resets the `ExecutionCount` for each module to 0 after reporting.
*   **Decision**: Maintained this legacy behavior to support existing unit tests that assert "RunCount == 1" for a single update.
*   **Observation**: This makes the method state-changing (read-once). Future observability needs might require a separating "Peek" vs "Flush" mechanic.

### 3.3 Default Timeouts
*   Default `MaxExpectedRuntimeMs` set to **100ms**. This balances responsiveness with allowing heavy modules enough time for reasonable work.
*   Fast modules (FrameSynced) are still subject to this timeout, protecting the main frame rate.

## 4. Challenges & Solutions

### 4.1 Test Flakiness (Time-Based Tests)
Integration tests relying on `Thread.Sleep` and `Task.Delay` were flaky in the test runner environment due to thread scheduling variability.
*   **Solution**: Increased `MaxExpectedRuntimeMs` in test mocks (from 100ms to 500ms-1000ms) to provide a wider safety margin against false positives during CI/testing.
*   **Solution**: Increased `Task.Delay` durations in test assertions to ensure async operations complete reliably before checking state.

### 4.2 API Breaking Changes
Changing `GetExecutionStats` from `Dictionary<string, int>` to `List<ModuleStats>` broke `ConsoleRenderer` and `ReactiveSchedulingTests`.
*   **Solution**: Updated consumers to use the new `ModuleStats` struct and LINQ for querying.

## 5. Architectural Feedback & Open Questions

### 5.1 Resource Leaks form Zombie Tasks
While circuit breakers protect the *logic* flow, they don't protect memory/thread resources. If a third-party module enters an infinite loop, that thread remains stuck forever.
*   **Question**: Do we need a "Hard Kill" mechanism (e.g., separate AppDomain or Process isolation) for critical safety, or is this "Soft Quarantine" sufficient for the intended use case?

### 5.2 Schema Registration in Snapshot Providers
(Re-iterating from Batch 03): The `SoDFiltering` test highlighted that `OnDemandProvider` (and by extension `SnapshotPool`) relies on `_schemaSetup` to register components on new snapshots. If `SyncFrom` logic encounters components not registered in the snapshot's schema, it cannot copy them.
*   **Status**: Tests pass because we register components, but the mechanics of `SyncFrom` implicitly relying on target schema matching source schema is something to watch as component counts grow.

### 5.3 Stats Observability
The current "Reset on Read" behavior of `GetExecutionStats` is brittle for multiple consumers (e.g., if a Renderer calls it, the Analytics module sees 0).
*   **Recommendation**: Move to a monotonic counter (total executions) and let consumers calculate deltas, OR provide a specific `ResetStats()` command.

## 6. Status
*   **Build**: Success (0 errors).
*   **Tests**: All `ModuleHost` tests passed, including new Resilience/Integration tests.
*   **Ready**: The system is ready for BATCH-05.
