# BATCH-09.6: Distributed Pause/Unpause with Mode Switching

**Batch ID:** BATCH-09.6  
**Phase:** Advanced - Distributed Simulation Foundation  
**Priority:** HIGH (P1) - Critical for distributed simulation control  
**Estimated Effort:** 1.5 days  
**Dependencies:** BATCH-09.5 (commit 7d9ddd9a - Time Controller Factory)  
**Starting Point:** Clean commit 7d9ddd9a1e0be365b5aecad0320eddeb63d58ded  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Context & Architecture Decision

### Starting Point: Commit 7d9ddd9a (BATCH-09.5)

**What Exists:**
- ✅ `MasterTimeController` - Continuous mode (wall-clock + PLL)
- ✅ `SlaveTimeController` - Continuous mode (PLL sync to master)
- ✅ `SteppedMasterController` - Deterministic mode (lockstep coordination)
- ✅ `SteppedSlaveController` - Deterministic mode (frame ACK protocol)
- ✅ `ITimeController` interface with `Update()`, `Step()`, `SetTimeScale()`
- ✅ `GetCurrentState()` and `SeedState()` for state transfer

**What's Broken:**
- ❌ `MasterTimeController.Update()` line 41: Reads stopwatch but never resets → accumulation bug
- ❌ No mechanism to switch between Continuous ↔ Deterministic modes
- ❌ CarKinem can pause locally, but distributed pause not implemented

---

## 🎯 Goal

Enable **distributed pause/unpause** across multi-computer simulations:

**On Pause:**
1. Master chooses a **Future Barrier Frame** (Current + Margin).
2. Master publishes `SwitchToDeterministic` event with `BarrierFrame`.
3. All nodes continue normally until they reach `BarrierFrame`.
4. At `BarrierFrame`, all nodes synchronously swap to `SteppedMasterT/SlaveController`.

**On Unpause:**
1. Master publishes `SwitchToContinuous` event.
2. All nodes swap back: `SteppedMasterController` → `MasterTimeController`.
3. Simulation resumes smooth wall-clock time.

**Why Future Barrier?**
- Prevents "micro-rewinds" (jitter) on Slaves.
- Ensures all nodes stop at the exact same simulation state.
- Allows Slaves to "coast" to the pause point smoothly.

---

## 🔍 Architecture Analysis: Swap vs Integrated

### Option A: Controller Swapping ⭐⭐⭐⭐⭐ (RECOMMENDED)

**How it Works:**
```
Running:  MasterTimeController ────┐
                                   │ Pause → SwapController()
Paused:   SteppedMasterController ─┘ Unpause → SwapController()
```

**Pros:**
- ✅ **Single Responsibility**: Each controller = one mode
- ✅ **Reuses Existing Code**: `SteppedMasterController` already exists and works
- ✅ **Clean State Transfer**: `GetCurrentState()` + `SeedState()` already implemented
- ✅ **Different Protocols**: Master/Slave use `TimePulse`, Stepped use `FrameOrder`/`FrameAck`
- ✅ **Testable**: Test each controller independently
- ✅ **Distributed-Ready**: Swap command broadcasts easily via events

**Cons:**
- ⚠️ Need to dispose old controller and create new one
- ⚠️ Must transfer state correctly (already implemented)

---

### Option B: Integrated Mode-Switching Controller ❌ (NOT RECOMMENDED)

**How it Would Work:**
```csharp
class HybridTimeController : ITimeController
{
    private TimeMode _currentMode;
    
    public GlobalTime Update()
    {
        if (_currentMode == TimeMode.Continuous)
            return UpdateContinuous();  // PLL logic
        else
            return UpdateDeterministic();  // Lockstep logic
    }
}
```

**Pros:**
- ✅ No object swapping
- ✅ Single object reference

**Cons:**
- ❌ **Violates Single Responsibility**: One class doing two complex jobs
- ❌ **Complex State Machine**: Must manage PLL state + lockstep ACKs in one class
- ❌ **Duplicate Code**: Would copy logic from `MasterTimeController` + `SteppedMasterController`
- ❌ **Testing Nightmare**: Must test all mode transitions and edge cases
- ❌ **Network Complexity**: Must handle both `TimePulse` AND `FrameOrder` protocols
- ❌ **Maintenance Burden**: Changes to either mode affect single mega-class

**Example Complexity:**
```csharp
// This is a nightmare:
public GlobalTime Update()
{
    if (_currentMode == Continuous)
    {
        // PLL adjustment
        var pulses = _eventBus.GetEvents<TimePulse>();
        double error = latestPulse.TotalTime - _localTotalTime;
        _pllCorrection += error * _pllGain;
        
        // Wall clock
        double elapsed = _stopwatch.Elapsed.TotalSeconds;
        // ...
    }
    else // Deterministic
    {
        // ACK collection
        var acks = _eventBus.Consume<FrameAck>();
        foreach (var ack in acks) _pendingAcks.Remove(ack.NodeID);
        
        if (_pendingAcks.Count > 0)
            return FrozenTime();  // Wait for ACKs
        
        // Publish FrameOrder
        _eventBus.Publish(new FrameOrder { ... });
        // ...
    }
}
```

**Conclusion:** This would create a **1000-line monster class** that's hard to test, maintain, and understand.

---

## ✅ **Decision: Use Controller Swapping**

**Rationale:**
1. **Existing controllers work**: Don't reinvent the wheel
2. **Clean architecture**: Each controller remains simple and focused
3. **Distributed-ready**: Event-based swap command fits network model
4. **Testable**: Test swapping separately from controller logic
5. **Maintainable**: Changes to Continuous mode don't affect Deterministic mode
6. **Jitter-Free**: Future Barrier ensures synchronized entry into pause.

---

## 📋 Tasks

### Task 1: Update ITimeController Interface ⭐⭐

**Objective:** Add state transfer methods to the interface.

**Update `ModuleHost.Core/Time/ITimeController.cs`:**
```csharp
public interface ITimeController : IDisposable
{
    // ... existing methods ...
    
    /// <summary>
    /// Get current time state for transfer/save.
    /// </summary>
    GlobalTime GetCurrentState();
    
    /// <summary>
    /// Initialize controller with specific time state.
    /// </summary>
    void SeedState(GlobalTime state);
}
```

---

### Task 2: Fix Stopwatch Bug & Implement State Methods ⭐⭐

**Objective:** Fix accumulation bug and implement new interface methods in Master/Slave controllers.

**Part A: Fix Stopwatch Reset & Implement Interface in `MasterTimeController`**
```csharp
public GlobalTime Update()
{
    double elapsedSeconds = _wallClock.Elapsed.TotalSeconds;
    
    // FIX: Manual accumulation for precision
    float scaledDelta = (float)(elapsedSeconds * _timeScale);
    _totalTime += scaledDelta;
    _unscaledTotalTime += elapsedSeconds;  // Accumulate manually!
    _frameNumber++;
    
    // FIX: Reset stopwatch
    _wallClock.Restart();
    
    // IMPORTANT: Publish Pulse Logic
    // ...
}

// Implement Interface
public GlobalTime GetCurrentState() => new GlobalTime { ... };
public void SeedState(GlobalTime state) 
{
    _frameNumber = state.FrameNumber;
    _totalTime = state.TotalTime;
    _unscaledTotalTime = state.UnscaledTotalTime;
    _simTimeBase = state.TotalTime; // Reset base
    _wallClock.Restart();
    
    // FORCE PULSE on next update to lock slaves immediately
    _lastPulseTicks = -PulseIntervalTicks; 
}
```

**Part B: `SlaveTimeController` Updates**
- Implement `GetCurrentState` and `SeedState`.
- Fix stopwatch reset logic in `Update`.

**Part C: Update `TimeConfig` for Barrier Configuration**
```csharp
// File: ModuleHost.Core/Time/TimeConfig.cs
public class TimeConfig
{
    // ... existing properties ...
    
    /// <summary>
    /// Number of frames to plan ahead for distributed pause barrier.
    /// Higher values = safer for high jitter networks.
    /// Lower values = faster pause response.
    /// </summary>
    public int PauseBarrierFrames { get; set; } = 10;
}
```

**Deliverables:**
- [ ] Update `ITimeController`.
- [ ] Update `MasterTimeController` (Fix + Interface).
- [ ] Update `SlaveTimeController` (Fix + Interface).
- [ ] Update `TimeConfig` (Add property).

---

### Task 3: Add Missing Methods to Stepped Controllers ⭐⭐

**Objective:** Ensure all controllers implement full `ITimeController` interface.

**What's Missing:**

`SteppedMasterController` (line 12-146) needs:
```csharp
public GlobalTime Step(float fixedDeltaTime)
{
    // Manual step in deterministic mode
    _currentFrame++;
    _totalTime += _fixedDelta;  // Ignore parameter, use configured delta
    
    // Publish FrameOrder
    _eventBus.Publish(new FrameOrderDescriptor
    {
        FrameID = _currentFrame,
        FixedDelta = _fixedDelta,
        SequenceID = _currentFrame
    });
    
    _waitingForAcks = true;
    _pendingAcks = new HashSet<int>(_allNodeIds);
    _frameStartTime = DateTime.UtcNow;
    
    return new GlobalTime
    {
        FrameNumber = _currentFrame,
        DeltaTime = _fixedDelta,
        TotalTime = _totalTime,
        TimeScale = 1.0f,
        UnscaledDeltaTime = _fixedDelta,
        UnscaledTotalTime = _totalTime
    };
}

public GlobalTime GetCurrentState()
{
    return new GlobalTime
    {
        FrameNumber = _currentFrame,
        DeltaTime = 0.0f,
        TotalTime = _totalTime,
        TimeScale = 1.0f,
        UnscaledDeltaTime = 0.0f,
        UnscaledTotalTime = _totalTime
    };
}

public void SeedState(GlobalTime state)
{
    _currentFrame = state.FrameNumber;
    _totalTime = state.TotalTime;
    
    // Reset ACK tracking for new state
    _pendingAcks = new HashSet<int>(_allNodeIds);
    _waitingForAcks = false;
}
```

**Apply to SteppedSlaveController too.**

**Deliverables:**
- [ ] Add `Step(float)` to `SteppedMasterController`
- [ ] Add `GetCurrentState()` to `SteppedMasterController`
- [ ] Add `SeedState(GlobalTime)` to `SteppedMasterController`
- [ ] Add same methods to `SteppedSlaveController`

---

### Task 4: Add SwapTimeController() to Kernel ⭐⭐⭐

**Objective:** Enable runtime controller replacement.

**What to Add:**

```csharp
// File: ModuleHost.Core/ModuleHostKernel.cs (UPDATE)

/// <summary>
/// Swap the time controller at runtime (e.g., pause/unpause in distributed systems).
/// Transfers state from old to new controller.
/// </summary>
public void SwapTimeController(ITimeController newController)
{
    if (newController == null)
        throw new ArgumentNullException(nameof(newController));
    
    if (!_initialized)
        throw new InvalidOperationException("Cannot swap controller before Initialize()");
    
    // Get current state from old controller
    var currentState = _timeController!.GetCurrentState();
    
    // Seed new controller with current state
    newController.SeedState(currentState);
    
    // Dispose old controller
    _timeController?.Dispose();
    
    // Install new controller
    _timeController = newController;
    
    // Update CurrentTime property
    CurrentTime = currentState;
    
    Console.WriteLine($"[TimeController] Swapped to {newController.GetType().Name}, " +
                     $"TotalTime={currentState.TotalTime:F3}s, Frame={currentState.FrameNumber}");
}

/// <summary>
/// Get current time controller (for inspection/debugging).
/// </summary>
public ITimeController GetTimeController()
{
    if (!_initialized)
        throw new InvalidOperationException("Time controller not initialized yet");
    
    return _timeController!;
}
```

**Deliverables:**
- [ ] Add `SwapTimeController(ITimeController)` to `ModuleHostKernel`
- [ ] Add `GetTimeController()` accessor
- [ ] Add debug logging for swaps

---

### Task 5: Create SwitchTimeModeEvent for Network ⭐⭐

**Objective:** Network event to coordinate mode switches across nodes.

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/TimeModeEvents.cs (NEW)

using System.Collections.Generic;
using MessagePack;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Network event to switch time mode across distributed system.
    /// Published by Master, consumed by all Slaves.
    /// </summary>
    [MessagePackObject]
    public struct SwitchTimeModeEvent
    {
        [Key(0)]
        public TimeMode TargetMode { get; set; }  // Continuous or Deterministic
        
        [Key(1)]
        public long FrameNumber { get; set; }  // Current frame for synchronization
        
        [Key(2)]
        public double TotalTime { get; set; }  // Current simulation time
        
        [Key(3)]
        public HashSet<int>? AllNodeIds { get; set; }  // For Deterministic mode
        
        [Key(4)]
        public float FixedDeltaSeconds { get; set; }  // For Deterministic mode
        
        [Key(5)]
        public long BarrierFrame { get; set; } // Frame at which to switch (0 = immediate)
    }
}
```

**Deliverables:**
- [ ] Create `ModuleHost.Core/Time/TimeModeEvents.cs`
- [ ] Register event descriptor in `FdpEventBus`

---

### Task 6: Implement Distributed Pause/Unpause Logic ⭐⭐⭐⭐

**Objective:** Coordinate mode switches across Master and Slaves.

**Master Side:**

```csharp
// File: ModuleHost.Core/Time/DistributedTimeCoordinator.cs (NEW)

using System;
using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Coordinates time mode switches for distributed Master node.
    /// </summary>
    public class DistributedTimeCoordinator
    {
        private readonly ModuleHostKernel _kernel;
        private readonly FdpEventBus _eventBus;
        private readonly TimeControllerConfig _config;
        private long _pendingBarrierFrame = -1;
        private HashSet<int>? _pendingSlaveIds;
        
        public DistributedTimeCoordinator(
            ModuleHostKernel kernel,
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            _kernel = kernel;
            _eventBus = eventBus;
            _config = config;
        }
        
        /// <summary>
        /// Switch to Deterministic mode (Pause) with Future Barrier.
        /// </summary>
        public void SwitchToDeterministic(HashSet<int> slaveNodeIds)
        {
            var currentState = _kernel.GetTimeController().GetCurrentState();
            
            // Plan barrier using configured lookahead
            int lookahead = _config.SyncConfig.PauseBarrierFrames;
            long barrierFrame = currentState.FrameNumber + lookahead;
            
            _pendingBarrierFrame = barrierFrame;
            _pendingSlaveIds = slaveNodeIds;
            
            // Publish mode switch event
            _eventBus.Publish(new SwitchTimeModeEvent
            {
                TargetMode = TimeMode.Deterministic,
                FrameNumber = currentState.FrameNumber, // Ref time
                TotalTime = currentState.TotalTime,     // Ref time
                AllNodeIds = slaveNodeIds,
                BarrierFrame = barrierFrame,
                FixedDeltaSeconds = _config.SyncConfig.FixedDeltaSeconds
            });
            
            Console.WriteLine($"[Master] Scheduled Pause at Frame {barrierFrame} (Current: {currentState.FrameNumber}, Lookahead: {lookahead})");
        }
        
        public void Update()
        {
            if (_pendingBarrierFrame != -1)
            {
                var currentFrame = _kernel.CurrentTime.FrameNumber;
                
                if (currentFrame >= _pendingBarrierFrame)
                {
                    // Execute Swap
                    ExecuteSwapToDeterministic();
                    _pendingBarrierFrame = -1;
                }
            }
        }
        
        private void ExecuteSwapToDeterministic()
        {
             var steppedMaster = new SteppedMasterController(
                eventBus: _eventBus,
                nodeIds: _pendingSlaveIds!,
                config: _config
            );
            _kernel.SwapTimeController(steppedMaster);
            Console.WriteLine($"[Master] Executed Pause Swap at Frame {_kernel.CurrentTime.FrameNumber}");
        }
        
        /// <summary>
        /// Switch to Continuous mode (Unpause).
        /// </summary>
        public void SwitchToContinuous()
        {
            var currentState = _kernel.GetTimeController().GetCurrentState();
            
            // Publish mode switch event
            _eventBus.Publish(new SwitchTimeModeEvent
            {
                TargetMode = TimeMode.Continuous,
                FrameNumber = currentState.FrameNumber,
                TotalTime = currentState.TotalTime,
                BarrierFrame = 0 // Immediate unpause
            });
            
            // Swap local controller
            var masterContinuous = new MasterTimeController(_eventBus, _config);
            _kernel.SwapTimeController(masterContinuous);
            
            Console.WriteLine($"[Master] Switched to Continuous mode (PLL sync)");
        }
    }
}
```

**Slave Side:**

```csharp
// File: ModuleHost.Core/Time/SlaveTimeModeListener.cs (NEW)

using System;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Listens for mode switch events and swaps time controller on Slave nodes.
    /// </summary>
    public class SlaveTimeModeListener
    {
        private readonly ModuleHostKernel _kernel;
        private readonly FdpEventBus _eventBus;
        private readonly int _localNodeId;
        
        private long _pendingBarrierFrame = -1;
        private SwitchTimeModeEvent? _pendingEvent;
        
        public SlaveTimeModeListener(
            ModuleHostKernel kernel,
            FdpEventBus eventBus,
            int localNodeId)
        {
            _kernel = kernel;
            _eventBus = eventBus;
            _localNodeId = localNodeId;
            
            // Subscribe to mode switch events
            _eventBus.Subscribe<SwitchTimeModeEvent>(OnModeSwitchRequested);
        }
        
        private void OnModeSwitchRequested(SwitchTimeModeEvent evt)
        {
            if (evt.TargetMode == TimeMode.Deterministic)
            {
                // Don't swap yet! Wait for barrier.
                _pendingBarrierFrame = evt.BarrierFrame;
                _pendingEvent = evt;
                
                // Safety: If barrier is 0 or already passed, swap immediately (catch up/snap)
                if (evt.BarrierFrame <= _kernel.CurrentTime.FrameNumber)
                {
                     ExecuteSwapToDeterministic(evt);
                     _pendingBarrierFrame = -1;
                }
                Console.WriteLine($"[Slave-{_localNodeId}] Scheduled Pause at Frame {evt.BarrierFrame} (Current: {_kernel.CurrentTime.FrameNumber})");
            }
            else if (evt.TargetMode == TimeMode.Continuous)
            {
                // Unpause: Immediate
                ExecuteSwapToContinuous(evt);
            }
        }
        
        public void Update()
        {
            if (_pendingBarrierFrame != -1)
            {
                if (_kernel.CurrentTime.FrameNumber >= _pendingBarrierFrame)
                {
                    ExecuteSwapToDeterministic(_pendingEvent!.Value);
                    _pendingBarrierFrame = -1;
                }
            }
        }
        
        private void ExecuteSwapToDeterministic(SwitchTimeModeEvent evt)
        {
            // Switch to Deterministic slave
            var steppedSlave = new SteppedSlaveController(
                eventBus: _eventBus,
                localNodeId: _localNodeId,
                fixedDeltaSeconds: evt.FixedDeltaSeconds
            );
            
                // Seed with local state (we are at Barrier)
                // This ensures we continue from exactly where we stopped
                var localState = _kernel.GetTimeController().GetCurrentState();
                
                // Force frame number to match barrier (handles minor snap cases)
                localState.FrameNumber = evt.BarrierFrame;
                
                steppedSlave.SeedState(localState);
            
            _kernel.SwapTimeController(steppedSlave);
            
            Console.WriteLine($"[Slave-{_localNodeId}] Executed Pause Swap at Frame {_kernel.CurrentTime.FrameNumber}");
        }
        
        private void ExecuteSwapToContinuous(SwitchTimeModeEvent evt)
        {
            // Switch to Continuous slave
            var slaveContinuous = new SlaveTimeController(
                eventBus: _eventBus,
                config: TimeConfig.Default,
                tickProvider: null
            );
            
            slaveContinuous.SeedState(new GlobalTime
            {
                FrameNumber = evt.FrameNumber,
                TotalTime = evt.TotalTime,
                TimeScale = 1.0f
            });
            
            _kernel.SwapTimeController(slaveContinuous);
            
            Console.WriteLine($"[Slave-{_localNodeId}] Switched to Continuous mode");
        }
    }
}
```

**Deliverables:**
- [ ] Update `DistributedTimeCoordinator` with Barrier logic
- [ ] Update `SlaveTimeModeListener` with Barrier logic
- [ ] Ensure `Update()` is called from Simulation loop

---

### Task 7: Update CarKinem for Distributed Pause ⭐⭐⭐

**Objective:** Use distributed pause for standalone AND networked modes.

**What to Update:**

```csharp
// File: Fdp.Examples.CarKinem/Simulation/DemoSimulation.cs (UPDATE)

private DistributedTimeCoordinator? _timeCoordinator;  // For Master role
private SlaveTimeModeListener? _slaveListener; // For Slave role

public void Initialize()
{
    // ... existing init ...
    
    // If Master role, create coordinator
    if (_config.TimeRole == TimeRole.Master || _config.TimeRole == TimeRole.Standalone)
    {
        _timeCoordinator = new DistributedTimeCoordinator(
            _kernel,
            _eventAccumulator.EventBus,
            _timeConfig
        );
    }
    
    // If Slave role, create listener
    if (_config.TimeRole == TimeRole.Slave)
    {
        _slaveListener = new SlaveTimeModeListener(
            _kernel,
            _eventAccumulator.EventBus,
            localNodeId: _config.LocalNodeId
        );
    }
}

public void Tick(float deltaTime, float timeScale)
{
    // NEW: Update Coordinator/Listener every tick
    _timeCoordinator?.Update();
    _slaveListener?.Update();
    
    // ... existing replay logic ...
    
    // Live / Recording Mode - Handle Pause Toggle
    if (pauseToggleRequested)
    {
        if (IsPaused)
        {
            // UNPAUSE: Switch to Continuous
            if (_timeCoordinator != null)
            {
                _timeCoordinator.SwitchToContinuous();
            }
            IsPaused = false;
        }
        else
        {
            // PAUSE: Switch to Deterministic
            if (_timeCoordinator != null)
            {
                // Get slave IDs from network config (or empty set for standalone)
                var slaveIds = _config.TimeRole == TimeRole.Standalone 
                    ? new HashSet<int>() 
                    : _networkConfig.SlaveNodeIds;
                    
                _timeCoordinator.SwitchToDeterministic(slaveIds);
            }
            IsPaused = true;
        }
    }
    
    // Handle Stepping (works in both Standalone and Distributed)
    if (IsPaused && StepFrames > 0)
    {
        const float FIXED_STEP_DT = 1.0f / 60.0f;
        _kernel.StepFrame(FIXED_STEP_DT);  // Uses Step() on current controller
        StepFrames--;
    }
    else if (!IsPaused)
    {
        _kernel.Update();  // Normal wall-clock update
    }
    else
    {
        _kernel.Update();  // Paused - returns frozen time (Deterministic mode)
    }
    
    // ... rest of systems ...
}
```

**Key Changes:**
- **Standalone**: Switches to empty-set Deterministic (no network coordination)
- **Master**: Broadcasts `SwitchTimeModeEvent` to Slaves
- **Slave**: Listens for events and swaps controller automatically

**Deliverables:**
- [ ] Update `DemoSimulation.cs`
- [ ] Add `DistributedTimeCoordinator` initialization
- [ ] Add `SlaveTimeModeListener` initialization
- [ ] Update pause/unpause logic

---

### Task 8: Add Integration Tests ⭐⭐⭐⭐

**Objective:** Verify mode switching works correctly.

**What to Create:**

```csharp
// File: ModuleHost.Core.Tests/Time/DistributedPauseTests.cs (NEW)

using System.Collections.Generic;
using System.Threading;
using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Time;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests.Time
{
    public class DistributedPauseTests
    {
        [Fact]
        public void SwapController_ContinuousToDeterministic_PreservesState()
        {
            // Arrange
            var repo = new EntityRepository();
            var eventBus = new FdpEventBus();
            var kernel = new ModuleHostKernel(repo, new EventAccumulator());
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            var config = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                Mode = TimeMode.Continuous
            };
            
            kernel.ConfigureTime(config);
            kernel.Initialize();
            
            // Run for some time
            Thread.Sleep(50);
            kernel.Update();
            var timeBefore = kernel.CurrentTime;
            
            // Act: Swap to Deterministic
            var steppedMaster = new SteppedMasterController(
                eventBus,
                new HashSet<int> { 1, 2 },
                TimeConfig.Default
            );
            kernel.SwapTimeController(steppedMaster);
            
            var timeAfter = kernel.CurrentTime;
            
            // Assert: State preserved
            Assert.Equal(timeBefore.TotalTime, timeAfter.TotalTime);
            Assert.Equal(timeBefore.FrameNumber, timeAfter.FrameNumber);
        }
        
        [Fact]
        public void DistributedPause_MasterPublishesEvent_SlavesReceive()
        {
            // Arrange: Master and Slave kernels
            var masterBus = new FdpEventBus();
            var slaveBus = new FdpEventBus();
            
            // Simulate network: events published to masterBus appear in slaveBus
            masterBus.Subscribe<SwitchTimeModeEvent>(evt => slaveBus.Publish(evt));
            
            var masterKernel = CreateKernel(masterBus, TimeRole.Master);
            var slaveKernel = CreateKernel(slaveBus, TimeRole.Slave);
            
            var coordinator = new DistributedTimeCoordinator(
                masterKernel,
                masterBus,
                TimeConfig.Default
            );
            
            bool slaveReceivedEvent = false;
            slaveBus.Subscribe<SwitchTimeModeEvent>(evt =>
            {
                slaveReceivedEvent = true;
                Assert.Equal(TimeMode.Deterministic, evt.TargetMode);
            });
            
            // Act: Master pauses
            coordinator.SwitchToDeterministic(new HashSet<int> { 1 });
            
            // Trigger event processing
            masterBus.SwapBuffers();
            slaveBus.SwapBuffers();
            
            // Assert
            Assert.True(slaveReceivedEvent, "Slave should receive mode switch event");
        }
        
        [Fact]
        public void PausedStepping_AdvancesFrameByFrame()
        {
            // Arrange: Kernel in Deterministic mode
            var repo = new EntityRepository();
            var eventBus = new FdpEventBus();
            var kernel = new ModuleHostKernel(repo, new EventAccumulator());
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            kernel.ConfigureTime(new TimeControllerConfig 
            { 
                Role = TimeRole.Standalone,
                Mode = TimeMode.Deterministic
            });
            kernel.Initialize();
            
            // Act: Step 3 times
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            
            var time = kernel.CurrentTime;
            
            // Assert: Time advanced by exactly 3 * (1/60)s
            Assert.Equal(3.0 / 60.0, time.TotalTime, precision: 5);
            Assert.Equal(3, time.FrameNumber);
        }
        
        private ModuleHostKernel CreateKernel(FdpEventBus eventBus, TimeRole role)
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            var kernel = new ModuleHostKernel(repo, new EventAccumulator(eventBus));
            kernel.ConfigureTime(new TimeControllerConfig { Role = role });
            kernel.Initialize();
            
            return kernel;
        }
    }
}
```

**Deliverables:**
- [ ] Create `DistributedPauseTests.cs`
- [ ] 3+ integration tests
- [ ] Test state preservation across swaps
- [ ] Test network event propagation
- [ ] Test stepping in paused mode

---

## 📊 Acceptance Criteria

- [ ] Stopwatch reset bug fixed in `MasterTimeController` and `SlaveTimeController`
- [ ] `SteppedMasterController` has `Step()`, `GetCurrentState()`, `SeedState()`
- [ ] `SteppedSlaveController` has `Step()`, `GetCurrentState()`, `SeedState()`
- [ ] `ModuleHostKernel.SwapTimeController()` implemented
- [ ] `SwitchTimeModeEvent` created for network
- [ ] `DistributedTimeCoordinator` created for Master
- [ ] `SlaveTimeModeListener` created for Slaves
- [ ] CarKinem updated for distributed pause/unpause
- [ ] 3+ integration tests passing
- [ ] Manual testing:
  - Standalone pause/unpause = smooth
  - Distributed pause (3 computers) = all enter lockstep
  - Step forward = all nodes advance together
  - Unpause = smooth return to continuous

---

## ⏱️ Estimated Timeline

**Total:** 1.5 days (~12 hours)

- **Task 1 (Stopwatch Fix):** 30 minutes
- **Task 2 (Stepped Controllers):** 1.5 hours
- **Task 3 (Kernel Swap API):** 1 hour
- **Task 4 (Network Event):** 30 minutes
- **Task 5 (Coordinator Logic):** 3 hours
- **Task 6 (CarKinem Update):** 2 hours
- **Task 7 (Tests):** 2.5 hours
- **Manual Testing:** 1 hour

---

## 📝 Commit Messages

**ModuleHost Master:**
```
feat: distributed pause/unpause with mode switching (BATCH-09.6)

Implemented distributed pause/unpause by swapping between Continuous and 
Deterministic time controllers at runtime.

Architecture:
- Controller Swapping: Clean state transfer between modes
- DistributedTimeCoordinator: Master publishes mode switch events
- SlaveTimeModeListener: Slaves respond to mode switch events

Fixes:
- MasterTimeController.Update(): Now resets stopwatch (fixes accumulation bug)
- SlaveTimeController.Update(): Now resets stopwatch
- Stepped controllers: Added Step(), GetCurrentState(), SeedState()

Changes:
- Added SwapTimeController() to ModuleHostKernel
- Added SwitchTimeModeEvent for network coordination
- Updated CARK inem for distributed pause/unpause

Tests: 3+ integration tests verifying mode switching and state transfer.

Manual Verified:
- Standalone pause → smooth unpause
- Distributed pause (3 nodes) → all enter lockstep
- Stepping → all nodes advance synchronously
- Unpause → smooth return to continuous PLL sync

Starting Point: Commit 7d9ddd9a (BATCH-09.5)
```

---

## 🎉 Completion

After this batch:

**Architecture:**
- ✅ Clean controller swapping (Single Responsibility maintained)
- ✅ Reuses existing Stepped controllers
- ✅ State transfer via GetCurrentState() + SeedState()
- ✅ No hybrid mega-class

**Standalone (CarKinem):**
- ✅ Pause → Deterministic mode (empty slave set)
- ✅ Step → Frame-perfect 16.67ms advances
- ✅ Unpause → Smooth wall-clock resumption

**Distributed (3+ Computers):**
- ✅ Master pause → broadcasts SwitchTimeModeEvent
- ✅ All Slaves swap to Deterministic mode
- ✅ Lockstep stepping → all nodes synchronized
- ✅ Unpause → all nodes return to Continuous PLL

**Code Quality:**
- ✅ Each controller remains simple and focused
- ✅ Testable components
- ✅ No stopwatch accumulation bugs

---

*Created: 2026-01-10*  
*Base Commit: 7d9ddd9a (BATCH-09.5)*  
*Implements: Distributed pause/unpause with clean controller swapping*
