# BATCH 01: Non-Blocking Execution

**Batch ID:** BATCH-01  
**Phase:** Foundation - Non-Blocking Execution ("World C")  
**Priority:** CRITICAL (P0)  
**Estimated Effort:** 1 week  
**Developer:** TBD  
**Assigned Date:** 2026-01-07

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 1 (Non-Blocking Execution)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 01 section
4. **Current Implementation:** Review `../../ModuleHost.Core/ModuleHostKernel.cs`

---

## 🎯 Batch Objectives

### Primary Goal
Implement non-blocking module execution to decouple slow module runtime from main thread frame rate.

### Success Criteria
- ✅ Main simulation runs at stable 60Hz even with slow modules taking 50+ms
- ✅ Slow modules can span multiple frames without blocking
- ✅ Commands from late-completing modules harvested correctly
- ✅ Frame time variance <1ms with concurrent slow modules
- ✅ All tests passing (unit + integration + performance)

### Why This Matters
Currently, `ModuleHostKernel.Update()` blocks on `Task.WaitAll()` for ALL modules every frame. If any module takes longer than 16ms, the entire simulation stutters. This batch fixes that critical issue by implementing a "Check-and-Harvest" pattern where slow modules run asynchronously while the main thread continues.

---

## 📋 Tasks

### Task 1.1: Module Entry State Tracking ⭐

**Objective:** Add async execution state fields to `ModuleEntry` class.

**Design Reference:** 
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 1, Section 1.2 - "Data Structures"

**Current State:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Class: Private nested class `ModuleEntry` (around line 299)
- Currently has: `Module`, `Provider`, `FramesSinceLastRun`

**What to Add:**
```csharp
// Async State (NEW - for World C)
public Task? CurrentTask { get; set; }
public ISimulationView? LeasedView { get; set; }
public float AccumulatedDeltaTime { get; set; }
public uint LastRunTick { get; set; }  // For reactive scheduling prep
```

**Acceptance Criteria:**
- [ ] Fields added to `ModuleEntry` class
- [ ] Fields initialized properly in constructor/initializer
- [ ] No breaking changes to existing module registration flow
- [ ] Nullable types handled correctly

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ModuleEntryStateTests.cs

[Fact]
public void ModuleEntry_InitialState_AllFieldsNull()
{
    // Verifies new fields start null/zero
}

[Fact]
public void ModuleEntry_AccumulatedDeltaTime_StartsAtZero()
{
    // Verifies time accumulation starts correctly
}

[Fact]
public void ModuleEntry_LastRunTick_TracksCorrectly()
{
    // Verifies tick tracking for future reactive scheduling
}
```

**Deliverables:**
- [ ] Modified file: `ModuleHost.Core/ModuleHostKernel.cs`
- [ ] New test file: `ModuleHost.Core.Tests/ModuleEntryStateTests.cs`
- [ ] 3+ unit tests passing

---

### Task 1.2: Harvest-and-Dispatch Loop ⭐⭐⭐

**Objective:** Replace blocking `Task.WaitAll()` with non-blocking check-and-harvest pattern.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 1, Section 1.2 - "Execution Flow"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Method: `Update(float deltaTime)` (around line 102)
- Current logic: Dispatches all modules, then blocks on `Task.WaitAll(tasks.ToArray())`

**Required Changes:**

1. **Modify `Update()` method flow:**
   ```
   OLD:
   1. Capture events
   2. Update providers
   3. Dispatch all modules
   4. WaitAll (BLOCKS HERE)
   5. Execute phases
   
   NEW:
   1. Execute Input phase (global systems)
   2. Execute BeforeSync phase
   3. Capture events
   4. Update FrameSynced providers
   5. HARVEST PHASE: Check completed async tasks
   6. DISPATCH PHASE: Start new tasks (non-blocking)
   7. Wait ONLY for FrameSynced tasks
   8. Harvest FrameSynced immediately
   9. Execute PostSimulation phase
   10. Execute Export phase
   ```

2. **Harvest Logic (for each module with CurrentTask):**
   - If `CurrentTask.IsCompleted`:
     - Playback command buffers from `LeasedView`
     - Call `Provider.ReleaseView(LeasedView)`
     - Clear `CurrentTask` and `LeasedView`
     - Reset `AccumulatedDeltaTime = 0`
     - Update `LastRunTick = currentFrame`
   - Else (still running):
     - Continue to next module
     - Add `deltaTime` to `AccumulatedDeltaTime`

3. **Dispatch Logic (for idle modules that should run):**
   - Check `ShouldRunThisFrame(entry)` 
   - If yes:
     - Call `view = Provider.AcquireView()`
     - Store in `entry.LeasedView`
     - Capture `dt = entry.AccumulatedDeltaTime`
     - Start `entry.CurrentTask = Task.Run(() => module.Tick(view, dt))`
     - If module.Policy.Mode == FrameSynced: add to waitList
   
4. **Synchronized Module Handling:**
   - Collect FrameSynced tasks into `tasksToWait` list
   - Call `Task.WaitAll(tasksToWait.ToArray())` ONLY for these
   - Immediately harvest them (they're completed now)

**Acceptance Criteria:**
- [ ] Main thread no longer blocks on async modules
- [ ] Async modules can span multiple frames
- [ ] FrameSynced modules still execute synchronously
- [ ] Commands harvested in correct order
- [ ] Accumulated delta time calculated properly
- [ ] Frame counter increments correctly

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/NonBlockingExecutionTests.cs

[Fact]
public async Task NonBlockingExecution_SlowAsyncModule_DoesntBlockMainThread()
{
    // Setup: Module that sleeps for 50ms
    // Run: 5 frames of Update()
    // Assert: Each frame completes in <20ms
    // Assert: Module completes after ~3 frames
    // Assert: Commands eventually applied
}

[Fact]
public async Task NonBlockingExecution_FrameSyncedModule_StillBlocksUntilComplete()
{
    // Setup: FrameSynced module that sleeps 10ms
    // Run: Update()
    // Assert: Update() takes ~10ms (waited)
    // Assert: Command applied in same frame
}

[Fact]
public async Task NonBlockingExecution_AccumulatedDeltaTime_CalculatedCorrectly()
{
    // Setup: Async module at 10Hz (should run every 6 frames)
    // Run: But module takes 3 frames to complete
    // Assert: Next run gets dt = 6 * frameDelta
}

[Fact]
public async Task NonBlockingExecution_MultipleSlowModules_RunConcurrently()
{
    // Setup: 3 async modules, each taking 30ms
    // Run: Update()
    // Assert: All 3 run in parallel
    // Assert: Frame time ~30ms, not ~90ms
}
```

**Performance Test to Write:**

```csharp
// File: ModuleHost.Benchmarks/FrameTimeStability.cs

[Benchmark]
public void FrameTimeVariance_WithSlowModules()
{
    // Setup: 10 modules, 5 slow (50ms), 5 fast (1ms)
    // Run: 100 frames
    // Measure: Frame time variance
    // Target: Stddev <1ms
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (Update method)
- [ ] New test file: `ModuleHost.Core.Tests/NonBlockingExecutionTests.cs`
- [ ] New benchmark: `ModuleHost.Benchmarks/FrameTimeStability.cs`
- [ ] 4+ integration tests passing
- [ ] Benchmark showing <1ms variance

---

### Task 1.3: Provider Lease/Release Logic ⭐⭐

**Objective:** Ensure providers support views being held across multiple frames.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 1, Section 1.2 - "Provider Implications"

**Files to Modify:**

1. **`OnDemandProvider.cs`:**
   - Current pool size: Likely hardcoded to 2-3
   - **Change:** Make pool size configurable via constructor parameter
   - **Default:** `poolSize = 5` (supports 5 concurrent slow modules)
   - **Verify:** View can be held for multiple frames without pool exhaustion

2. **`SharedSnapshotProvider.cs`:**
   - Current ref counting: Basic implementation exists
   - **Verify:** Multiple `AcquireView()` calls increment counter correctly
   - **Verify:** `ReleaseView()` decrements correctly
   - **Verify:** Snapshot only returned to pool when count == 0
   - **Test:** One module holds for 10 frames while another finishes quickly

**Acceptance Criteria:**
- [ ] OnDemandProvider pool configurable
- [ ] Pool doesn't exhaust with 5 concurrent slow modules
- [ ] SharedSnapshotProvider ref counting works across frames
- [ ] View remains valid until explicitly released
- [ ] No crashes or assertion failures when views held long-term

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ProviderLeaseTests.cs

[Fact]
public void OnDemandProvider_PoolSize_Configurable()
{
    // Create with poolSize=5
    // Acquire 5 views
    // Assert: No pool exhaustion
}

[Fact]
public void OnDemandProvider_ConcurrentLeases_DoesntExhaust()
{
    // Acquire 3 views but don't release
    // Run 10 frames
    // Assert: Pool still functional
}

[Fact]
public void SharedSnapshotProvider_RefCount_IncrementsOnAcquire()
{
    // AcquireView() twice
    // Assert: Internal ref count == 2
}

[Fact]
public void SharedSnapshotProvider_OnlyPoolsWhenAllReleased()
{
    // Acquire 3 times
    // Release 2 times
    // Assert: Snapshot NOT returned to pool
    // Release 1 more time
    // Assert: Snapshot returned to pool
}

[Fact]
public void SharedSnapshotProvider_ViewValidAcrossFrames()
{
    // Acquire view in frame 1
    // Run 10 frames without releasing
    // Assert: View still readable
    // Release
    // Assert: No errors
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/Providers/OnDemandProvider.cs`
- [ ] Modified: `ModuleHost.Core/Providers/SharedSnapshotProvider.cs`
- [ ] New test file: `ModuleHost.Core.Tests/ProviderLeaseTests.cs`
- [ ] 5+ unit tests passing

---

### Task 1.4: HarvestEntry Helper Method ⭐

**Objective:** Extract command playback logic into reusable method to avoid duplication.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 1, Section 1.2 - "Execution Flow"

**Current Issue:**
Task 1.2 will have harvest logic in TWO places:
1. Start of frame for async modules
2. After `WaitAll()` for synced modules

**Solution:**
Extract into private method:

```csharp
private void HarvestEntry(ModuleEntry entry)
{
    // 1. Playback commands
    if (entry.LeasedView is EntityRepository repo)
    {
        foreach (var cmdBuffer in repo._perThreadCommandBuffer.Values)
        {
            if (cmdBuffer.HasCommands)
                cmdBuffer.Playback(_liveWorld);
        }
    }
    
    // 2. Release view
    entry.Provider.ReleaseView(entry.LeasedView!);
    
    // 3. Handle faulted tasks
    if (entry.CurrentTask!.IsFaulted)
    {
        // Log error with module name and exception
        Console.Error.WriteLine($"Module {entry.Module.Name} failed: {entry.CurrentTask.Exception}");
    }
    
    // 4. Cleanup
    entry.CurrentTask = null;
    entry.LeasedView = null;
    entry.AccumulatedDeltaTime = 0;
    entry.LastRunTick = _currentFrame;
    
    // 5. Stats
    Interlocked.Increment(ref _totalExecutions);
}
```

**Acceptance Criteria:**
- [ ] Method created in `ModuleHostKernel`
- [ ] Used by both harvest points in `Update()`
- [ ] No code duplication
- [ ] Faulted tasks logged clearly
- [ ] Command playback order preserved

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/HarvestLogicTests.cs

[Fact]
public void HarvestEntry_FaultedTask_LogsErrorButContinues()
{
    // Setup: Module that throws exception
    // Run: Harvest
    // Assert: Error logged (capture Console.Error)
    // Assert: Entry cleaned up properly
    // Assert: System continues
}

[Fact]
public void HarvestEntry_CommandBuffer_OrderPreserved()
{
    // Setup: Module with 5 commands in buffer
    // Run: Harvest
    // Assert: Commands applied in FIFO order
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (add HarvestEntry method)
- [ ] Updated: Task 1.2 code to use HarvestEntry
- [ ] New tests in: `ModuleHost.Core.Tests/HarvestLogicTests.cs`
- [ ] 2+ unit tests passing

---

### Task 1.5: Integration Testing & Validation ⭐⭐

**Objective:** Comprehensive end-to-end testing of non-blocking execution system.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 1, entire chapter

**Test Scenarios:**

1. **Slow Module Doesn't Block Main Thread**
   - Setup: 60Hz sim, 10Hz module that takes 50ms
   - Run: 10 frames
   - Assert: Main thread frame time <20ms
   - Assert: Module completes after ~3 frames
   - Assert: Commands applied in correct frame

2. **Multiple Slow Modules Run Concurrently**
   - Setup: 3 modules, each 10Hz, each 30ms execution
   - Run: Frame where all 3 trigger
   - Assert: All 3 run in parallel
   - Assert: Frame time ~30ms (not 90ms sequential)
   - Assert: All commands eventually applied

3. **Commands from Late Modules Applied in Correct Frame**
   - Setup: Module finishes between frames N and N+1
   - Assert: Commands applied at start of frame N+1
   - Assert: Command ordering deterministic

4. **Frame Time Variance Benchmark**
   - Setup: 10 modules (5 fast 1ms, 5 slow 50ms, mixed frequencies)
   - Run: 100 frames
   - Measure: Frame time variance
   - Target: Standard deviation <1ms

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Tests/NonBlockingIntegrationTests.cs

[Fact(Timeout = 5000)]
public async Task Integration_SlowModule_DoesntBlockMainThread()
{
    // Full scenario test with real EntityRepository
}

[Fact]
public async Task Integration_MultipleSlowModules_RunParallel()
{
    // Verify concurrent execution with timing
}

[Fact]
public async Task Integration_LateCommands_AppliedCorrectly()
{
    // Verify command timing and ordering
}
```

**Performance Benchmark:**

```csharp
// File: ModuleHost.Benchmarks/FrameTimeStability.cs

[Benchmark]
[Arguments(10, 5)] // 10 total modules, 5 slow
public void FrameTimeVariance(int totalModules, int slowModules)
{
    // Run 100 frames
    // Report: Min, Max, Mean, StdDev, P95, P99
}
```

**Acceptance Criteria:**
- [ ] All integration tests passing
- [ ] Performance benchmark shows <1ms variance
- [ ] No test flakiness (run 10 times, all pass)
- [ ] Real-world scenario validated (CarKinem example)

**Deliverables:**
- [ ] New test file: `ModuleHost.Tests/NonBlockingIntegrationTests.cs`
- [ ] Updated benchmark: `ModuleHost.Benchmarks/FrameTimeStability.cs`
- [ ] 3+ integration tests passing
- [ ] Benchmark results documented in report

---

## ✅ Definition of Done

This batch is complete when:

- [ ] All 5 tasks completed
- [ ] All unit tests passing (15+ tests total)
- [ ] All integration tests passing (3+ tests)
- [ ] Performance benchmark showing <1ms variance
- [ ] Code review: No architectural violations
- [ ] Code review: Follows existing patterns
- [ ] No compiler warnings
- [ ] Changes committed to git
- [ ] Report submitted (see below)

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Main thread frame time | <16ms @ 60Hz | <20ms |
| Frame time variance (stddev) | <1ms | <2ms |
| Module dispatch overhead | <0.5ms | <1ms |
| Pool exhaustion | Never | Never |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| Unit tests | All passing |
| Integration tests | All passing |
| Compiler warnings | 0 |

---

## 🚧 Potential Challenges

### Challenge 1: Command Buffer Access
**Issue:** `EntityRepository._perThreadCommandBuffer` might be private  
**Solution:** If needed, add public accessor or use ISimulationView interface  
**Ask if:** Access pattern is unclear or seems wrong

### Challenge 2: Faulted Task Handling
**Issue:** Exception handling when module crashes  
**Solution:** Check `Task.IsFaulted` and log `Task.Exception`  
**Ask if:** Unsure how to handle propagated exceptions

### Challenge 3: Frame Timing
**Issue:** Accurate frame time measurement for benchmarks  
**Solution:** Use `Stopwatch.GetTimestamp()` for high-resolution timing  
**Ask if:** Benchmark results seem inconsistent

### Challenge 4: FrameSynced vs Async Logic
**Issue:** Distinguishing between module types  
**Solution:** Check `entry.Policy.Mode` (might need to add this field now)  
**Ask if:** Policy/Tier distinction is unclear

---

## 📝 Reporting

### When Complete

1. **Copy report template:**
   ```bash
   cp ../templates/BATCH-REPORT-TEMPLATE.md ../reports/BATCH-01-REPORT.md
   ```

2. **Fill out all sections:**
   - Task completion checklist
   - Test results (paste output)
   - Performance benchmark results
   - Any deviations or improvements
   - Known issues or limitations

3. **Submit report file:** `../reports/BATCH-01-REPORT.md`

### If Blocked

1. **Copy questions template:**
   ```bash
   cp ../templates/QUESTIONS-TEMPLATE.md ../questions/BATCH-01-QUESTIONS.md
   ```

2. **Document your questions clearly**

3. **Submit questions file:** `../questions/BATCH-01-QUESTIONS.md`

---

## 🔗 References

**Primary Design Document:**
`../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 1

**Task Tracker:**
`../TASK-TRACKER.md` - BATCH 01 section

**Workflow README:**
`../README.md`

**Existing Code to Review:**
- `../../ModuleHost.Core/ModuleHostKernel.cs`
- `../../ModuleHost.Core/Providers/OnDemandProvider.cs`
- `../../ModuleHost.Core/Providers/SharedSnapshotProvider.cs`
- `../../ModuleHost.Core.Tests/` (existing test patterns)

---

## 💡 Implementation Tips

1. **Start with Task 1.1** (state tracking) - it's the foundation
2. **Write tests FIRST** for Task 1.2 - TDD approach highly recommended
3. **Use existing test patterns** - look at current test files for style
4. **Run tests frequently** - don't wait until everything is done
5. **Commit often** - small logical commits
6. **Document decisions** - especially if you deviate from specs
7. **Profile early** - use benchmarks to catch performance issues

---

**Good luck! This is a critical batch. Take your time to get it right.**

**Questions? Create:** `../questions/BATCH-01-QUESTIONS.md`  
**Done? Submit:** `../reports/BATCH-01-REPORT.md`
