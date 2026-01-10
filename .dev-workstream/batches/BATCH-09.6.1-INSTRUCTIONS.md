# BATCH-09.6.1: Distributed Pause Edge Case Tests

**Batch ID:** BATCH-09.6.1  
**Phase:** Advanced - Distributed Simulation Foundation - Test Coverage  
**Priority:** MEDIUM (P2) - Quality assurance  
**Estimated Effort:** 0.5 days  
**Dependencies:** BATCH-09.6 (Distributed Pause/Unpause)  
**Starting Point:** BATCH-09.6 completed  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Context

BATCH-09.6 successfully implemented distributed pause/unpause with Future Barrier synchronization. The core functionality works correctly with 3 existing tests:

1. ✅ `FutureBarrier_Pause_SyncsAtScheduledFrame()` - Basic barrier test
2. ✅ `Unpause_SwitchesImmediately()` - Unpause behavior
3. ✅ `PausedStepping_AdvancesFrameByFrame()` - Manual stepping

**However**, several edge cases and failure modes are not tested, leaving gaps in coverage for production robustness.

---

## 🎯 Objective

Add **5 additional integration tests** to cover edge cases and failure modes in distributed pause/unpause:

1. **Late-Arriving Slave** - Slave receives pause event after barrier frame passed
2. **State Transfer Accuracy** - Verify frame and time preservation across swaps
3. **Rapid Pause/Unpause** - Multiple mode switches before barrier reached
4. **StepFrame on Wrong Controller** - Error handling for invalid stepping
5. **Barrier Frame Propagation** - Verify event data integrity across network

---

## 📋 Tasks

### Task 1: Add Late-Arriving Slave Test ⭐⭐⭐

**Objective:** Verify that high-latency slaves correctly handle arriving past the barrier frame.

**Test Scenario:**
- Master pauses at Frame 10 (Barrier Frame = 20)
- Master advances to Frame 25
- Slave is still at Frame 5
- Slave finally receives pause event (BarrierFrame=20)
- **Expected:** Slave immediately snaps to Deterministic mode using local state (no rewind)

**Implementation:**

```csharp
// File: ModuleHost.Core.Tests/Time/DistributedPauseTests.cs (UPDATE)

[Fact]
public void LateArrivingSlave_SwapsImmediately_NoRewind()
{
    // Arrange: Master and Slave with simulated high latency
    var sharedBus = new FdpEventBus();
    
    // Master Setup
    var masterRepo = new EntityRepository();
    var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
    var masterConfig = new TimeControllerConfig 
    { 
        Role = TimeRole.Master,
        SyncConfig = new TimeConfig { PauseBarrierFrames = 5 }
    };
    masterKernel.ConfigureTime(masterConfig);
    masterKernel.Initialize();
    
    var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
    
    // Slave Setup (starts delayed)
    var slaveRepo = new EntityRepository();
    var slaveKernel = new ModuleHostKernel(slaveRepo, new EventAccumulator());
    var slaveConfig = new TimeControllerConfig 
    { 
        Role = TimeRole.Slave,
        LocalNodeId = 1
    };
    slaveKernel.ConfigureTime(slaveConfig);
    slaveKernel.Initialize();
    
    var listener = new SlaveTimeModeListener(sharedBus, slaveKernel, slaveConfig);
    
    // Advance Master ahead
    for (int i = 0; i < 10; i++)
    {
        masterKernel.Update();
        Thread.Sleep(5);
    }
    
    long masterFrameBeforePause = masterKernel.CurrentTime.FrameNumber;
    
    // Master initiates pause (Barrier = Current + 5)
    coordinator.SwitchToDeterministic(new HashSet<int>{1});
    
    // Master advances to and past barrier
    for (int i = 0; i < 10; i++)
    {
        masterKernel.Update();
        coordinator.Update();
        Thread.Sleep(5);
    }
    
    // Verify Master is now paused
    Assert.Equal(TimeMode.Deterministic, masterKernel.GetTimeController().GetMode());
    long masterFrameAfterPause = masterKernel.CurrentTime.FrameNumber;
    
    // Slave is still running normally (simulating network delay - hasn't received event)
    // Advance Slave but don't swap buffers yet (event stuck in transit)
    for (int i = 0; i < 5; i++)
    {
        slaveKernel.Update();
        Thread.Sleep(5);
    }
    
    long slaveFrameBeforeEvent = slaveKernel.CurrentTime.FrameNumber;
    double slaveTimeBeforeEvent = slaveKernel.CurrentTime.TotalTime;
    
    // NOW: Event finally arrives (swap buffers)
    sharedBus.SwapBuffers();
    listener.Update();
    
    // Act & Assert
    // Slave should immediately swap (past barrier) using LOCAL state
    Assert.Equal(TimeMode.Deterministic, slaveKernel.GetTimeController().GetMode());
    
    // Verify NO REWIND: Frame should be >= before event
    Assert.True(slaveKernel.CurrentTime.FrameNumber >= slaveFrameBeforeEvent,
        $"Slave rewound! Was {slaveFrameBeforeEvent}, now {slaveKernel.CurrentTime.FrameNumber}");
    
    // Verify Time continuity
    Assert.True(slaveKernel.CurrentTime.TotalTime >= slaveTimeBeforeEvent,
        $"Slave time jumped backwards! Was {slaveTimeBeforeEvent}, now {slaveKernel.CurrentTime.TotalTime}");
    
    // Cleanup
    masterKernel.Dispose();
    slaveKernel.Dispose();
}
```

**Deliverables:**
- [ ] Add `LateArrivingSlave_SwapsImmediately_NoRewind()` test
- [ ] Verify no frame rewind on late arrival
- [ ] Verify time continuity

---

### Task 2: Add State Transfer Accuracy Test ⭐⭐

**Objective:** Verify that swapping controllers preserves frame number and total time exactly.

**Test Scenario:**
- Run Master to Frame 100, TotalTime = 5.0s
- Swap to Deterministic mode
- **Expected:** New controller has exactly Frame=100, Time=5.0s

**Implementation:**

```csharp
[Fact]
public void SwapController_PreservesExactFrameAndTime()
{
    // Arrange
    var eventBus = new FdpEventBus();
    var repo = new EntityRepository();
    var kernel = new ModuleHostKernel(repo, new EventAccumulator());
    
    kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Master });
    kernel.Initialize();
    
    // Advance to known state
    for (int i = 0; i < 50; i++)
    {
        kernel.Update();
        Thread.Sleep(10); // ~500ms total
    }
    
    long frameBeforeSwap = kernel.CurrentTime.FrameNumber;
    double timeBeforeSwap = kernel.CurrentTime.TotalTime;
    
    Assert.True(frameBeforeSwap > 0, "Should have advanced some frames");
    Assert.True(timeBeforeSwap > 0, "Should have accumulated time");
    
    // Act: Swap to Deterministic
    var steppedMaster = new SteppedMasterController(
        eventBus,
        new HashSet<int> { 1 },
        new TimeControllerConfig { Role = TimeRole.Master }
    );
    
    kernel.SwapTimeController(steppedMaster);
    
    // Assert: State preserved EXACTLY
    Assert.Equal(frameBeforeSwap, kernel.CurrentTime.FrameNumber);
    Assert.Equal(timeBeforeSwap, kernel.CurrentTime.TotalTime, precision: 6);
    
    // Verify new controller also reports same state
    var newState = kernel.GetTimeController().GetCurrentState();
    Assert.Equal(frameBeforeSwap, newState.FrameNumber);
    Assert.Equal(timeBeforeSwap, newState.TotalTime, precision: 6);
    
    // Cleanup
    kernel.Dispose();
}
```

**Deliverables:**
- [ ] Add `SwapController_PreservesExactFrameAndTime()` test
- [ ] Verify frame number accuracy
- [ ] Verify total time accuracy to 6 decimal places

---

### Task 3: Add Rapid Pause/Unpause Test ⭐⭐⭐

**Objective:** Verify system handles multiple rapid mode switches correctly.

**Test Scenario:**
- Pause (Barrier = Frame 10)
- Before barrier: Unpause
- **Expected:** Pending barrier canceled, continuous mode restored

**Implementation:**

```csharp
[Fact]
public void RapidPauseUnpause_BeforeBarrier_HandlesSafely()
{
    // Arrange
    var sharedBus = new FdpEventBus();
    var masterRepo = new EntityRepository();
    var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
    
    var masterConfig = new TimeControllerConfig 
    { 
        Role = TimeRole.Master,
        SyncConfig = new TimeConfig { PauseBarrierFrames = 10 }
    };
    masterKernel.ConfigureTime(masterConfig);
    masterKernel.Initialize();
    
    var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
    
    // Start in Continuous
    masterKernel.Update();
    Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
    
    long frameAtPause = masterKernel.CurrentTime.FrameNumber;
    
    // Act: Rapid Pause
    coordinator.SwitchToDeterministic(new HashSet<int>{1});
    
    // Advance 3 frames (still before barrier of +10)
    for (int i = 0; i < 3; i++)
    {
        masterKernel.Update();
        coordinator.Update();
        Thread.Sleep(5);
    }
    
    // Should still be Continuous (barrier not reached)
    Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
    
    // Act: Rapid Unpause (cancel pending pause)
    coordinator.SwitchToContinuous();
    
    // Advance more frames
    for (int i = 0; i < 15; i++)
    {
        masterKernel.Update();
        coordinator.Update();
        Thread.Sleep(5);
    }
    
    // Assert: Should remain Continuous (barrier canceled)
    Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
    
    // Verify frame count advanced normally
    Assert.True(masterKernel.CurrentTime.FrameNumber > frameAtPause + 15,
        "Should have advanced past original barrier without pausing");
    
    // Cleanup
    masterKernel.Dispose();
}
```

**Note:** This test may reveal a bug if the coordinator doesn't cancel pending barriers on unpause. If the test fails, the fix is:

```csharp
// In DistributedTimeCoordinator.SwitchToContinuous()
public void SwitchToContinuous()
{
    // Cancel pending barrier
    _pendingBarrierFrame = -1;  // ← ADD THIS LINE
    
    // ... rest of existing logic
}
```

**Deliverables:**
- [ ] Add `RapidPauseUnpause_BeforeBarrier_HandlesSafely()` test
- [ ] If test fails, fix coordinator to cancel pending barrier
- [ ] Verify continuous mode maintained after cancel

---

### Task 4: Add Invalid Stepping Test ⭐

**Objective:** Verify error handling when StepFrame() called on wrong controller type.

**Test Scenario:**
- Kernel running with MasterTimeController (Continuous)
- Call `StepFrame()`
- **Expected:** InvalidOperationException

**Implementation:**

```csharp
[Fact]
public void StepFrame_OnContinuousController_ThrowsInvalidOperation()
{
    // Arrange
    var repo = new EntityRepository();
    var kernel = new ModuleHostKernel(repo, new EventAccumulator());
    
    kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Master });
    kernel.Initialize();
    
    // Verify we're in Continuous mode
    Assert.Equal(TimeMode.Continuous, kernel.GetTimeController().GetMode());
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(() =>
    {
        kernel.StepFrame(0.016f);
    });
    
    // Verify error message is clear
    Assert.Contains("does not support", ex.Message);
    Assert.Contains("stepping", ex.Message.ToLower());
    
    // Cleanup
    kernel.Dispose();
}
```

**Deliverables:**
- [ ] Add `StepFrame_OnContinuousController_ThrowsInvalidOperation()` test
- [ ] Verify exception type
- [ ] Verify error message clarity

---

### Task 5: Add Barrier Frame Propagation Test ⭐⭐

**Objective:** Verify SwitchTimeModeEvent carries correct barrier frame across network.

**Test Scenario:**
- Master calculates Barrier = Current + Lookahead
- Publish event
- **Expected:** Slave receives event with correct BarrierFrame value

**Implementation:**

```csharp
[Fact]
public void SwitchEvent_PropagatesBarrierFrameCorrectly()
{
    // Arrange
    var sharedBus = new FdpEventBus();
    
    var masterRepo = new EntityRepository();
    var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
    
    int lookahead = 7; // Custom lookahead for test
    var masterConfig = new TimeControllerConfig 
    { 
        Role = TimeRole.Master,
        SyncConfig = new TimeConfig { PauseBarrierFrames = lookahead }
    };
    masterKernel.ConfigureTime(masterConfig);
    masterKernel.Initialize();
    
    var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
    
    // Advance to known frame
    for (int i = 0; i < 10; i++)
    {
        masterKernel.Update();
        Thread.Sleep(5);
    }
    
    long currentFrame = masterKernel.CurrentTime.FrameNumber;
    long expectedBarrier = currentFrame + lookahead;
    
    // Setup event capture
    SwitchTimeModeEvent? capturedEvent = null;
    sharedBus.Subscribe<SwitchTimeModeEvent>(evt => capturedEvent = evt);
    
    // Act: Initiate pause
    coordinator.SwitchToDeterministic(new HashSet<int>{1});
    
    // Swap to deliver event
    sharedBus.SwapBuffers();
    
    // Assert: Event captured
    Assert.NotNull(capturedEvent);
    Assert.Equal(TimeMode.Deterministic, capturedEvent.Value.TargetMode);
    
    // Verify Barrier Frame calculation
    Assert.Equal(expectedBarrier, capturedEvent.Value.BarrierFrame);
    
    // Verify reference time is current
    Assert.Equal(currentFrame, capturedEvent.Value.FrameNumber);
    
    // Cleanup
    masterKernel.Dispose();
}
```

**Deliverables:**
- [ ] Add `SwitchEvent_PropagatesBarrierFrameCorrectly()` test
- [ ] Verify BarrierFrame = CurrentFrame + Lookahead
- [ ] Verify reference FrameNumber and TotalTime

---

## 📊 Acceptance Criteria

- [ ] All 5 new tests added to `DistributedPauseTests.cs`
- [ ] All 8 tests passing (3 existing + 5 new)
- [ ] Code coverage increased for edge cases
- [ ] If Task 3 reveals barrier-cancel bug, fix implemented
- [ ] No regressions in existing tests

---

## 🧪 Test Summary

**After BATCH-09.6.1:**

| Test | Scenario | Coverage |
|:-----|:---------|:---------|
| `FutureBarrier_Pause_SyncsAtScheduledFrame` | Normal barrier sync | ✅ Happy path |
| `Unpause_SwitchesImmediately` | Unpause behavior | ✅ Happy path |
| `PausedStepping_AdvancesFrameByFrame` | Manual stepping | ✅ Happy path |
| `LateArrivingSlave_SwapsImmediately_NoRewind` | High latency | ✅ **Edge case** |
| `SwapController_PreservesExactFrameAndTime` | State accuracy | ✅ **Validation** |
| `RapidPauseUnpause_BeforeBarrier_HandlesSafely` | Mode thrashing | ✅ **Edge case** |
| `StepFrame_OnContinuousController_ThrowsInvalidOperation` | Error handling | ✅ **Validation** |
| `SwitchEvent_PropagatesBarrierFrameCorrectly` | Network integrity | ✅ **Validation** |

**Total:** 8 tests covering happy path, edge cases, validation, and error handling.

---

## ⏱️ Estimated Timeline

**Total:** 0.5 days (~4 hours)

- **Task 1 (Late Slave):** 1 hour
- **Task 2 (State Accuracy):** 30 minutes
- **Task 3 (Rapid Pause/Unpause):** 1 hour
- **Task 4 (Invalid Step):** 30 minutes
- **Task 5 (Event Propagation):** 45 minutes
- **Bug Fixes (if any):** 15 minutes

---

## 📝 Commit Messages

**ModuleHost Master:**
```
test: add distributed pause edge case coverage (BATCH-09.6.1)

Added 5 integration tests for distributed pause/unpause edge cases:
- LateArrivingSlave: High-latency slaves snap immediately (no rewind)
- StateTransferAccuracy: Frame/time preserved exactly across swaps
- RapidPauseUnpause: Barrier cancellation on mode thrashing
- InvalidStepping: Error handling for StepFrame on continuous controller
- BarrierPropagation: Network event integrity verification

Fixed: DistributedTimeCoordinator.SwitchToContinuous() now cancels pending barrier.

Tests: 8/8 passing (3 existing + 5 new). Coverage: Happy path, edge cases, validation.

Depends on: BATCH-09.6
```

---

## 🎉 Completion

After this batch:

**Test Coverage:**
- ✅ Happy path scenarios (existing)
- ✅ Edge cases (late arrival, rapid switching)
- ✅ Validation (state accuracy, event integrity)
- ✅ Error handling (invalid operations)

**Quality:**
- ✅ Production-ready robustness
- ✅ Regression prevention
- ✅ Clear failure modes documented

**Confidence:**
- ✅ Safe for distributed multi-computer deployments
- ✅ Handles network latency gracefully
- ✅ No hidden state corruption bugs

---

*Created: 2026-01-10*  
*Base: BATCH-09.6 (Distributed Pause/Unpause)*  
*Purpose: Comprehensive edge case test coverage*
