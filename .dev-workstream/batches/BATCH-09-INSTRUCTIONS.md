# BATCH 09: Time Control & Synchronization System

**Batch ID:** BATCH-09  
**Phase:** Advanced - Distributed Simulation Foundation  
**Priority:** HIGH (P1)  
**Estimated Effort:** 2 weeks  
**Dependencies:** None (Foundation system)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Time Management
3. **User Guide:** `../../docs/FDP-ModuleHost-User-Guide.md` - Time Control & Synchronization section
4. **Reference:** `../../docs/reference-archive/drill-clock-sync.md` - PLL Algorithm Specification
5. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 09 section

---

## 🎯 Batch Objectives

### Primary Goal
Implement distributed time synchronization system supporting both Continuous (PLL-based) and Deterministic (lockstep) modes.

### Success Criteria
- ✅ GlobalTime singleton working
- ✅ ITimeController interface with Master/Slave implementations
- ✅ PLL algorithm achieving <10ms sync variance
- ✅ Lockstep mode for deterministic replay
- ✅ Network translators for TimePulse and FrameOrder/Ack
- ✅ Integration with ModuleHost deltaTime accumulation
- ✅ Pause/resume/speed control functional
- ✅ All tests passing

### Why This Matters
Time is the foundation of distributed simulation. Without proper synchronization:
- Nodes see different "now" → Events out of order
- Rubber-banding from time snaps → Visual jitter
- Non-deterministic replay → Can't debug from logs
The PLL algorithm provides smooth, low-latency synchronization while lockstep provides perfect determinism when needed.

---

## 📋 Tasks

### Task 9.1: GlobalTime Singleton & Core Interfaces ⭐⭐

**Objective:** Define time model data structures and controller abstraction.

**Design Reference:**
- Document: `FDP-ModuleHost-User-Guide.md`
- Section: "Time Control & Synchronization"

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/GlobalTime.cs (NEW)


// WARNING!!! There might already be a GlobalTime struct in Fdp.Kernel namespace.
//   Please unify the two - put the unified GlobalTime struct in Fdp.Kernel namespace only.

using System;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Singleton descriptor for simulation time state.
    /// Pushed into ECS world every frame.
    /// </summary>
    public struct GlobalTime
    {
        /// <summary>
        /// Total elapsed simulation time (seconds).
        /// Affected by TimeScale and pausing.
        /// </summary>
        public double TotalTime;
        
        /// <summary>
        /// Time elapsed since last frame (seconds).
        /// Used for physics integration (pos += vel * DeltaTime).
        /// </summary>
        public float DeltaTime;
        
        /// <summary>
        /// Speed multiplier.
        /// 0.0 = Paused, 1.0 = Realtime, 2.0 = 2x speed.
        /// </summary>
        public float TimeScale;
        
        /// <summary>
        /// Convenience flag (TimeScale == 0.0).
        /// </summary>
        public bool IsPaused => TimeScale == 0.0f;
        
        /// <summary>
        /// Current frame number (increments every frame regardless of pause).
        /// </summary>
        public long FrameNumber;
        
        /// <summary>
        /// Wall clock time when simulation started (UTC ticks).
        /// </summary>
        public long StartWallTicks;
    }
    
    /// <summary>
    /// Abstraction over time control logic.
    /// Implementations: MasterTimeController, SlaveTimeController, SteppedTimeController.
    /// </summary>
    public interface ITimeController : IDisposable
    {
        /// <summary>
        /// Update clock state and calculate time for this frame.
        /// Called once per frame by ModuleHostKernel.
        /// </summary>
        /// <param name="dt">Output: DeltaTime for this frame (seconds)</param>
        /// <param name="totalTime">Output: Total simulation time (seconds)</param>
        void Update(out float dt, out double totalTime);
        
        /// <summary>
        /// Change simulation speed.
        /// </summary>
        void SetTimeScale(float scale);
        
        /// <summary>
        /// Get current time scale.
        /// </summary>
        float GetTimeScale();
        
        /// <summary>
        /// Get current mode (Continuous or Deterministic).
        /// </summary>
        TimeMode GetMode();
    }
    
    /// <summary>
    /// Time synchronization mode.
    /// </summary>
    public enum TimeMode
    {
        /// <summary>
        /// Continuous (Real-Time/Scaled) mode.
        /// Uses PLL for smooth synchronization.
        /// </summary>
        Continuous,
        
        /// <summary>
        /// Deterministic (Lockstep/Stepped) mode.
        /// Frame-by-frame synchronization via ACKs.
        /// </summary>
        Deterministic
    }
}
```

**Acceptance Criteria:**
- [ ] `GlobalTime` struct defined with all fields
- [ ] `ITimeController` interface defined
- [ ] `TimeMode` enum defined
- [ ] XML documentation complete

**Unit Tests:**

```csharp
// File: ModuleHost.Core.Tests/Time/GlobalTimeTests.cs (NEW)

using Xunit;
using ModuleHost.Core.Time;

namespace ModuleHost.Core.Tests.Time
{
    public class GlobalTimeTests
    {
        [Fact]
        public void GlobalTime_IsPaused_ReturnsTrueWhenScaleIsZero()
        {
            var time = new GlobalTime { TimeScale = 0.0f };
            Assert.True(time.IsPaused);
        }
        
        [Fact]
        public void GlobalTime_IsPaused_ReturnsFalseWhenScaleIsNonZero()
        {
            var time = new GlobalTime { TimeScale = 1.0f };
            Assert.False(time.IsPaused);
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Time/GlobalTime.cs`
- [ ] New test file: `ModuleHost.Core.Tests/Time/GlobalTimeTests.cs`
- [ ] 2+ unit tests passing

---

### Task 9.2: Master Time Controller (Continuous Mode) ⭐⭐⭐

**Objective:** Implement authoritative time source with network publishing.

**Design Reference:**
- Document: `FDP-ModuleHost-User-Guide.md`
- Section: "Continuous Mode Implementation"

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/MasterTimeController.cs (NEW)

using System;
using System.Diagnostics;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master time controller for Continuous mode.
    /// Owns authoritative simulation time and publishes TimePulse to network.
    /// </summary>
    public class MasterTimeController : ITimeController
    {
        private readonly Stopwatch _wallClock;
        private readonly IDataWriter _timePulseWriter;
        private readonly TimeConfig _config;
        
        // Time state
        private double _simTimeBase = 0.0;
        private long _scaleChangeWallTicks = 0;
        private float _timeScale = 1.0f;
        private long _frameNumber = 0;
        
        // Network publishing
        private long _lastPulseTicks = 0;
        private const long PulseIntervalTicks = Stopwatch.Frequency; // 1Hz
        
        public MasterTimeController(IDataWriter timePulseWriter, TimeConfig config = null)
        {
            _wallClock = Stopwatch.StartNew();
            _timePulseWriter = timePulseWriter ?? throw new ArgumentNullException(nameof(timePulseWriter));
            _config = config ?? TimeConfig.Default;
            _scaleChangeWallTicks = _wallClock.ElapsedTicks;
            _lastPulseTicks = _wallClock.ElapsedTicks;
        }
        
        public void Update(out float dt, out double totalTime)
        {
            _frameNumber++;
            
            // Calculate wall delta
            long currentWallTicks = _wallClock.ElapsedTicks;
            double wallDelta = (currentWallTicks - _lastFrameTicks) / (double)Stopwatch.Frequency;
            _lastFrameTicks = currentWallTicks;
            
            // Calculate simulation delta (respecting scale)
            dt = (float)(wallDelta * _timeScale);
            
            // Calculate total simulation time
            totalTime = _simTimeBase + 
                       (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            // Publish TimePulse (1Hz or on-change)
            if (ShouldPublishPulse(currentWallTicks))
            {
                PublishTimePulse(currentWallTicks, totalTime);
                _lastPulseTicks = currentWallTicks;
            }
        }
        
        public void SetTimeScale(float scale)
        {
            if (scale < 0.0f)
                throw new ArgumentException("TimeScale cannot be negative", nameof(scale));
            
            // Save current sim time as new base
            long currentWallTicks = _wallClock.ElapsedTicks;
            _simTimeBase = _simTimeBase + 
                          (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            _scaleChangeWallTicks = currentWallTicks;
            _timeScale = scale;
            
            // Immediately publish to slaves
            PublishTimePulse(currentWallTicks, _simTimeBase);
        }
        
        private bool ShouldPublishPulse(long currentTicks)
        {
            // Publish every second OR immediately after scale change
            return (currentTicks - _lastPulseTicks) >= PulseIntervalTicks;
        }
        
        private void PublishTimePulse(long wallTicks, double simTime)
        {
            var pulse = new TimePulseDescriptor
            {
                MasterWallTicks = wallTicks,
                SimTimeSnapshot = simTime,
                TimeScale = _timeScale,
                SequenceId = _frameNumber
            };
            
            _timePulseWriter.Write(pulse);
        }
        
        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Continuous;
        
        public void Dispose()
        {
            // Cleanup if needed
        }
        
        private long _lastFrameTicks = 0;
    }
    
    /// <summary>
    /// Configuration for time controllers.
    /// </summary>
    public class TimeConfig
    {
        public static TimeConfig Default => new();
        
        /// <summary>
        /// PLL gain for slave synchronization (0.0 - 1.0).
        /// Higher = faster convergence, lower = smoother.
        /// </summary>
        public double PLLGain { get; set; } = 0.1;
        
        /// <summary>
        /// Maximum frequency deviation for PLL (±5% default).
        /// Prevents physics instability from aggressive corrections.
        /// </summary>
        public double MaxSlew { get; set; } = 0.05;
        
        /// <summary>
        /// Error threshold triggering hard snap (milliseconds).
        /// </summary>
        public double SnapThresholdMs { get; set; } = 500.0;
        
        /// <summary>
        /// Number of samples for jitter filtering.
        /// </summary>
        public int JitterWindowSize { get; set; } = 5;
        
        /// <summary>
        /// Estimated average network latency (ticks).
        /// Used to compensate for transmission delay.
        /// </summary>
        public long AverageLatencyTicks { get; set} = Stopwatch.Frequency * 2 / 1000; // 2ms default
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Time/MasterTimeController.cs`
- [ ] 3+ unit tests

---

### Task 9.3: Slave Time Controller with PLL ⭐⭐⭐⭐

**Objective:** Implement smooth clock synchronization using Phase-Locked Loop algorithm.

**Design Reference:**
- Document: `drill-clock-sync.md`
- Section: "The Software PLL Algorithm"

**CRITICAL:** This is the core of distributed time synchronization. The PLL prevents time snaps (rubber-banding).

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/SlaveTimeController.cs (NEW)

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Slave time controller for Continuous mode.
    /// Uses Phase-Locked Loop (PLL) to smoothly sync with Master clock.
    /// </summary>
    public class SlaveTimeController : ITimeController
    {
        private readonly Stopwatch _wallClock;
        private readonly TimeConfig _config;
        
        // Virtual clock (PLL-adjusted)
        private long _virtualWallTicks = 0;
        private long _lastFrameTicks = 0;
        
        // Time state
        private double _simTimeBase = 0.0;
        private long _scaleChangeWallTicks = 0;
        private float _timeScale = 1.0f;
        private long _frameNumber = 0;
        
        // PLL state
        private readonly JitterFilter _errorFilter;
        private double _currentError = 0.0;
        
        public SlaveTimeController(TimeConfig config = null)
        {
            _wallClock = Stopwatch.StartNew();
            _config = config ?? TimeConfig.Default;
            _errorFilter = new JitterFilter(_config.JitterWindowSize);
            _virtualWallTicks = _wallClock.ElapsedTicks;
            _scaleChangeWallTicks = _virtualWallTicks;
            _lastFrameTicks = _virtualWallTicks;
        }
        
        /// <summary>
        /// Called by network system when TimePulse arrives.
        /// Calculates synchronization error for PLL.
        /// </summary>
        public void OnTimePulseReceived(TimePulseDescriptor pulse)
        {
            // 1. Reconstruct what time it is "Right Now" on the Master
            // Master's snapshot + network latency + time since pulse sent
            long currentWallTicks = _wallClock.ElapsedTicks;
            long timeSincePulse = currentWallTicks - pulse.MasterWallTicks;
            long targetWallTicks = pulse.MasterWallTicks + _config.AverageLatencyTicks + timeSincePulse;
            
            // 2. Calculate raw error (how far behind/ahead we are)
            long errorTicks = targetWallTicks - _virtualWallTicks;
            
            // 3. Push to jitter filter (median of last N samples)
            _errorFilter.AddSample(errorTicks);
            
            // 4. Update time scale from master
            _timeScale = pulse.TimeScale;
            
            // 5. Check for hard desync
            double errorMs = errorTicks / (double)Stopwatch.Frequency * 1000.0;
            if (Math.Abs(errorMs) > _config.SnapThresholdMs)
            {
                // Hard snap - we've fallen too far behind
                Console.WriteLine($"[TimePLL] Hard snap: {errorMs:F1}ms");
                _virtualWallTicks = targetWallTicks;
                _errorFilter.Reset();
                _currentError = 0.0;
            }
        }
        
        public void Update(out float dt, out double totalTime)
        {
            _frameNumber++;
            
            // Get current PLL-filtered error
            double filteredError = _errorFilter.GetFilteredValue();
            
            // P-Controller: Correction proportional to error
            double correctionFactor = (filteredError / (double)Stopwatch.Frequency) * _config.PLLGain;
            
            // Clamp to max slew rate (safety: prevent physics instability)
            correctionFactor = Math.Clamp(correctionFactor, -_config.MaxSlew, _config.MaxSlew);
            
            // Calculate raw wall delta
            long currentWallTicks = _wallClock.ElapsedTicks;
            long rawDelta = currentWallTicks - _lastFrameTicks;
            _lastFrameTicks = currentWallTicks;
            
            // Apply PLL correction to delta
            // If we're behind (positive error), run slightly faster (>1.0x)
            // If we're ahead (negative error), run slightly slower (<1.0x)
            long adjustedDelta = (long)(rawDelta * (1.0 + correctionFactor));
            
            // Update virtual wall clock
            _virtualWallTicks += adjustedDelta;
            
            // Calculate virtual wall delta (for sim time derivation)
            double virtualWallDelta = adjustedDelta / (double)Stopwatch.Frequency;
            
            // Calculate simulation delta (respecting time scale)
            dt = (float)(virtualWallDelta * _timeScale);
            
            // Calculate total simulation time from virtual wall clock
            totalTime = _simTimeBase + 
                       (_virtualWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            // Reduce error by what we just corrected
            _currentError -= correctionFactor * virtualWallDelta;
        }
        
        public void SetTimeScale(float scale)
        {
            // Slaves don't set scale directly - they receive it via TimePulse
            throw new InvalidOperationException("Slave cannot set time scale. Scale comes from Master via TimePulse.");
        }
        
        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Continuous;
        
        public void Dispose()
        {
            // Cleanup if needed
        }
    }
    
    /// <summary>
    /// Jitter filter using median of circular buffer.
    /// Rejects network outliers while allowing PLL to track real drift.
    /// </summary>
    internal class JitterFilter
    {
        private readonly long[] _samples;
        private int _index = 0;
        private int _count = 0;
        
        public JitterFilter(int windowSize)
        {
            _samples = new long[windowSize];
        }
        
        public void AddSample(long errorTicks)
        {
            _samples[_index] = errorTicks;
            _index = (_index + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }
        
        public double GetFilteredValue()
        {
            if (_count == 0)
                return 0.0;
            
            // Return median of samples (robust against outliers)
            var sorted = _samples.Take(_count).OrderBy(x => x).ToArray();
            return sorted[_count / 2];
        }
        
        public void Reset()
        {
            Array.Clear(_samples, 0, _samples.Length);
            _index = 0;
            _count = 0;
        }
    }
}
```

**How PLL Affects Module DeltaTime:**

The PLL correction is **invisible but effective**:

```
1. PLL detects we're lagging 5ms behind Master
2. PLL applies +1% correction
3. Adjusted virtual wall delta: 16.83ms (instead of 16.67ms)
4. SimDelta = 16.83ms × TimeScale (1.0) = 16.83ms
5. ModuleHost accumulator gets 16.83ms
6. When module runs, dt is slightly larger
7. Physics: pos += vel × dt uses the larger dt
8. Result: Entities move slightly farther → simulation catches up smoothly
```

**Visual Smoothness:** The slew rate limit (±5%) keeps dt variation imperceptible (16.6ms → 17.5ms max). User sees smooth motion, but simulation is mathematically converging.

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Time/SlaveTimeController.cs`
- [ ] 5+ unit tests including PLL convergence test

---

### Task 9.4: Deterministic Time Controller (Lockstep) ⭐⭐⭐

**Objective:** Implement frame-by-frame synchronization for perfect determinism.

**Design Reference:**
- Document: `FDP-ModuleHost-User-Guide.md`
- Section: "Deterministic Mode Implementation"

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/SteppedTimeController.cs (NEW)

using System;
using System.Collections.Generic;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master time controller for Deterministic (Lockstep) mode.
    /// Wait for all ACKs before advancing to next frame.
    /// </summary>
    public class SteppedMasterController : ITimeController
    {
        private readonly IDataWriter _frameOrderWriter;
        private readonly HashSet<int> _allNodeIds;
        private readonly float _fixedDelta;
        
        private long _currentFrame = 0;
        private HashSet<int> _pendingAcks;
        private bool _waitingForAcks = false;
        
        public SteppedMasterController(IDataWriter frameOrderWriter, HashSet<int> nodeIds, float fixedDelta = 0.016f)
        {
            _frameOrderWriter = frameOrderWriter ?? throw new ArgumentNullException(nameof(frameOrderWriter));
            _allNodeIds = nodeIds ?? throw new ArgumentNullException(nameof(nodeIds));
            _fixedDelta = fixedDelta;
            _pendingAcks = new HashSet<int>(_allNodeIds);
        }
        
        public void Update(out float dt, out double totalTime)
        {
            if (_waitingForAcks)
            {
                // Check if all ACKs received
                if (_pendingAcks.Count == 0)
                {
                    // All nodes finished Frame N-1, advance to Frame N
                    _currentFrame++;
                    _waitingForAcks = false;
                    
                    // Publish order for next frame
                    _frameOrderWriter.Write(new FrameOrderDescriptor
                    {
                        FrameID = _currentFrame,
                        FixedDelta = _fixedDelta
                    });
                    
                    // Reset pending ACKs for new frame
                    _pendingAcks = new HashSet<int>(_allNodeIds);
                }
                else
                {
                    // Still waiting - don't advance simulation
                    dt = 0.0f;
                    totalTime = _currentFrame * _fixedDelta;
                    return;
                }
            }
            
            // Execute this frame
            dt = _fixedDelta;
            totalTime = _currentFrame * _fixedDelta;
            
            // Mark waiting for ACKs
            _waitingForAcks = true;
        }
        
        public void OnFrameAckReceived(FrameAckDescriptor ack)
        {
            if (ack.FrameID == _currentFrame)
            {
                _pendingAcks.Remove(ack.NodeID);
            }
        }
        
        public void SetTimeScale(float scale)
        {
            // Time scale doesn't apply in deterministic mode
            throw new InvalidOperationException("TimeScale not supported in Deterministic mode.");
        }
        
        public float GetTimeScale() => 1.0f;
        public TimeMode GetMode() => TimeMode.Deterministic;
        
        public void Dispose() { }
    }
    
    /// <summary>
    /// Slave time controller for Deterministic (Lockstep) mode.
    /// Waits for FrameOrder before advancing.
    /// </summary>
    public class SteppedSlaveController : ITimeController
    {
        private readonly IDataWriter _frameAckWriter;
        private readonly int _localNodeId;
        private readonly float _defaultDelta;
        
        private long _currentFrame = 0;
        private float _fixedDelta = 0.016f;
        private bool _hasFrameOrder = false;
        private bool _needToSendAck = false;
        
        public SteppedSlaveController(IDataWriter frameAckWriter, int localNodeId, float defaultDelta = 0.016f)
        {
            _frameAckWriter = frameAckWriter ?? throw new ArgumentNullException(nameof(frameAckWriter));
            _localNodeId = localNodeId;
            _defaultDelta = defaultDelta;
            _fixedDelta = defaultDelta;
        }
        
        public void OnFrameOrderReceived(FrameOrderDescriptor order)
        {
            _currentFrame = order.FrameID;
            _fixedDelta = order.FixedDelta;
            _hasFrameOrder = true;
        }
        
        public void Update(out float dt, out double totalTime)
        {
            // Send ACK from previous frame (if needed)
            if (_needToSendAck)
            {
                SendFrameAck();
                _needToSendAck = false;
            }
            
            if (!_hasFrameOrder)
            {
                // Waiting for master - don't advance
                dt = 0.0f;
                totalTime = _currentFrame * _fixedDelta;
                return;
            }
            
            // Execute frame
            dt = _fixedDelta;
            totalTime = _currentFrame * _fixedDelta;
            
            // Mark that we need to send ACK after simulation completes
            _hasFrameOrder = false;
            _needToSendAck = true;
        }
        
        private void SendFrameAck()
        {
            _frameAckWriter.Write(new FrameAckDescriptor
            {
                FrameID = _currentFrame,
                NodeID = _localNodeId
            });
        }
        
        public void SetTimeScale(float scale)
        {
            throw new InvalidOperationException("TimeScale not supported in Deterministic mode.");
        }
        
        public float GetTimeScale() => 1.0f;
        public TimeMode GetMode() => TimeMode.Deterministic;
        
        public void Dispose() { }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Time/SteppedTimeController.cs`
- [ ] 4+ unit tests

---

### Task 9.5: Network Descriptors & Translators ⭐⭐

**Objective:** Define DDS messages and translation logic for time synchronization.

**What to Create:**

```csharp
// File: ModuleHost.Core/Time/TimeDescriptors.cs (NEW)

using System;
using System.Runtime.InteropServices;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// TimePulse descriptor for Continuous mode synchronization.
    /// Published by Master at 1Hz + on time scale changes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TimePulseDescriptor
    {
        /// <summary>
        /// Master's high-resolution clock at snapshot time (Stopwatch ticks).
        /// </summary>
        public long MasterWallTicks;
        
        /// <summary>
        /// Master's simulation time at snapshot moment (seconds).
        /// </summary>
        public double SimTimeSnapshot;
        
        /// <summary>
        /// Current time scale (0.0 = paused, 1.0 = realtime, 2.0 = 2x speed).
        /// </summary>
        public float TimeScale;
        
        /// <summary>
        /// Sequence number for detecting dropped packets.
        /// </summary>
        public long SequenceId;
    }
    
    /// <summary>
    /// FrameOrder descriptor for Deterministic mode.
    /// Sent by Master to all nodes to execute next frame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct FrameOrderDescriptor
    {
        /// <summary>
        /// Frame number to execute.
        /// </summary>
        public long FrameID;
        
        /// <summary>
        /// Fixed delta time for this frame (seconds).
        /// </summary>
        public float FixedDelta;
    }
    
    /// <summary>
    /// FrameAck descriptor for Deterministic mode.
    /// Sent by slaves to Master after frame execution completes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct FrameAckDescriptor
    {
        /// <summary>
        /// Frame number that was completed.
        /// </summary>
        public long FrameID;
        
        /// <summary>
        /// Node ID that completed the frame.
        /// </summary>
        public int NodeID;
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Time/TimeDescriptors.cs`

---

### Task 9.6: Integration with ModuleHost ⭐⭐

**Objective:** Replace existing time system with new controller-based architecture.

**What to Modify:**

```csharp
// File: ModuleHost.Core/ModuleHostKernel.cs (MODIFY)

using ModuleHost.Core.Time;

public class ModuleHostKernel
{
    private readonly EntityRepository _liveWorld;
    private readonly ITimeController _timeController;
    private long _currentFrame = 0;
    
    public ModuleHostKernel(EntityRepository world, ITimeController timeController)
    {
        _liveWorld = world ?? throw new ArgumentNullException(nameof(world));
        _timeController = timeController ?? throw new ArgumentNullException(nameof(timeController));
    }
    
    public void Update(float _ /* Ignored - time from controller */)
    {
        // 1. GET TIME FROM CONTROLLER
        _timeController.Update(out float deltaTime, out double totalTime);
        
        // Push GlobalTime singleton to ECS
        _liveWorld.SetSingleton(new GlobalTime
        {
            TotalTime = totalTime,
            DeltaTime = deltaTime,
            TimeScale = _timeController.GetTimeScale(),
            FrameNumber = _currentFrame
        });
        
        _currentFrame++;
        
        // 2. TICK WORLD (Advance versioning)
        _liveWorld.Tick();
        
        // 3. INPUT PHASE
        ExecuteSystemPhase(SystemPhase.Input);
        
        // 4. EVENT SWAP
        _liveWorld.Bus.SwapBuffers();
        
        // 5. BEFORE-SYNC PHASE
        ExecuteSystemPhase(SystemPhase.BeforeSync);
        
        // 6. HARVEST & SYNC PHASE
        HarvestCompletedModules();
        UpdateProviders();
        
        // 7. SIMULATION PHASE
        ExecuteSystemPhase(SystemPhase.Simulation);
        
        // 8. DISPATCH PHASE
        DispatchModules(deltaTime); // ← deltaTime from controller
        
        // 9. POST-SIMULATION PHASE
        ExecuteSystemPhase(SystemPhase.PostSimulation);
        
        // 10. EXPORT PHASE
        ExecuteSystemPhase(SystemPhase.Export);
        
        // 11. COMMAND BUFFERS
        FlushCommandBuffers();
    }
    
    private void DispatchModules(float deltaTime)
    {
        foreach (var entry in _modules)
        {
            // Accumulate delta time (PLL-adjusted!)
            entry.AccumulatedDeltaTime += deltaTime;
            
            // Check if should run
            if (ShouldRunThisFrame(entry))
            {
                var view = entry.Provider.AcquireView();
                var accumulated = entry.AccumulatedDeltaTime;
                
                // Dispatch with accumulated (PLL-adjusted) time
                entry.CurrentTask = Task.Run(() => entry.Module.Tick(view, accumulated));
                entry.AccumulatedDeltaTime = 0.0f;
            }
        }
    }
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs`
- [ ] GlobalTime pushed to world every frame
- [ ] DeltaTime from controller used for module accumulation
- [ ] Integration test

---

### Task 9.7: Time Control Integration Tests ⭐⭐

**Objective:** End-to-end validation of time synchronization.

**Test Scenarios:**

```csharp
// File: ModuleHost.Tests/Time/TimeIntegrationTests.cs (NEW)

[Fact]
public async Task MasterSlave_Continuous_ConvergesWithin10ms()
{
    // Setup: Master and Slave controllers
    var timePulseQueue = new MockDataWriter();
    var master = new MasterTimeController(timePulseQueue);
    var slave = new SlaveTimeController();
    
    // Run simulation for 10 seconds
    for (int i = 0; i < 600; i++) // 60fps × 10s
    {
        master.Update(out float masterDt, out double masterTime);
        
        // Simulate network: Deliver pulse to slave
        if (timePulseQueue.HasData())
        {
            var pulse = (TimePulseDescriptor)timePulseQueue.TakeLast();
            slave.OnTimePulseReceived(pulse);
        }
        
        slave.Update(out float slaveDt, out double slaveTime);
        
        // After 1 second, check convergence
        if (i > 60)
        {
            double error = Math.Abs(masterTime - slaveTime);
            Assert.True(error < 0.010, $"Sync error {error*1000:F1}ms exceeds 10ms threshold");
        }
        
        await Task.Delay(16); // Simulate 60fps
    }
}

[Fact]
public void SteppedMode_WaitsForAllAcks()
{
    // Setup: Master with 3 slave nodes
    var frameOrderQueue = new MockDataWriter();
    var nodeIds = new HashSet<int> { 1, 2, 3 };
    var master = new SteppedMasterController(frameOrderQueue, nodeIds);
    
    // Frame 1: Execute
    master.Update(out float dt1, out double time1);
    Assert.Equal(0.016f, dt1);
    Assert.Equal(0.016, time1);
    
    // Frame 2: Waiting for Frame 1 ACKs
    master.Update(out float dt2, out double time2);
    Assert.Equal(0.0f, dt2); // Blocked!
    Assert.Equal(0.016, time2); // Still at Frame 1 time
    
    // Send ACKs
    master.OnFrameAckReceived(new FrameAckDescriptor { FrameID = 1, NodeID = 1 });
    master.OnFrameAckReceived(new FrameAckDescriptor { FrameID = 1, NodeID = 2 });
    master.Update(out float dt3, out double time3);
    Assert.Equal(0.0f, dt3); // Still blocked (missing Node 3)
    
    // Final ACK
    master.OnFrameAckReceived(new FrameAckDescriptor { FrameID = 1, NodeID = 3 });
    master.Update(out float dt4, out double time4);
    Assert.Equal(0.016f, dt4); // Unblocked!
    Assert.Equal(0.032, time4); // Frame 2 time
}

[Fact]
public void PLLCorrection_SmoothlyConverges()
{
    var slave = new SlaveTimeController(new TimeConfig { PLLGain = 0.1 });
    
    // Simulate slave running slow (5ms behind)
    var errorTicks = Stopwatch.Frequency * 5 / 1000; // 5ms
    
    slave.OnTimePulseReceived(new TimePulseDescriptor
    {
        MasterWallTicks = Stopwatch.GetTimestamp() + errorTicks,
        SimTimeSnapshot = 10.0,
        TimeScale = 1.0f,
        SequenceId = 1
    });
    
    // Run 100 frames and track convergence
    double totalCorrection = 0.0;
    for (int i = 0; i < 100; i++)
    {
        slave.Update(out float dt, out double time);
        totalCorrection += dt - 0.01667; // Deviation from 60fps
    }
    
    // Verify correction was gradual (not a snap)
    Assert.True(totalCorrection > 0.004 && totalCorrection < 0.006, 
                "Expected ~5ms total correction over 100 frames");
}

[Fact]
public void TimeScale_AffectsSimulationSpeed()
{
    var master = new MasterTimeController(new MockDataWriter());
    
    // Run at 2x speed
    master.SetTimeScale(2.0f);
    
    Thread.Sleep(1000); // 1 wall second
    
    master.Update(out float dt, out double totalTime);
    
    // Should have ~2 sim seconds elapsed
    Assert.True(totalTime > 1.9 && totalTime < 2.1, 
                $"Expected ~2.0s sim time, got {totalTime:F2}s");
}
```

**Deliverables:**
- [ ] New test file: `ModuleHost.Tests/Time/TimeIntegrationTests.cs`
- [ ] 4+ integration tests passing
- [ ] PLL convergence test verifying <10ms sync

---

## ✅ Definition of Done

- [ ] All 7 tasks completed
- [ ] GlobalTime singleton working
- [ ] Master/Slave controllers implemented
- [ ] PLL achieving <10ms sync variance
- [ ] Lockstep mode functional
- [ ] Network descriptors defined
- [ ] ModuleHost integration complete
- [ ] DeltaTime correctly affected by PLL
- [ ] All unit tests passing (20+ tests)
- [ ] All integration tests passing (4+ tests)
- [ ] No compiler warnings
- [ ] Changes committed
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Sync variance (LAN) | <5ms | <10ms |
| PLL convergence time | <100 frames | <200 frames |
| Hard snap threshold | 500ms | 1000ms |
| CPU overhead | <1% | <2% |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >85% |
| All tests | Passing |

---

## 🚧 Potential Challenges

### Challenge 1: PLL Tuning
**Issue:** Gain too high = jitter, too low = slow convergence  
**Solution:** Default 0.1, expose as config parameter  
**Ask if:** Sync variance exceeds 10ms in testing

### Challenge 2: Hard Snap Visual Jitter
**Issue:** 500ms snap causes entities to teleport  
**Solution:** Log warning, consider increasing threshold  
**Ask if:** Frequent snaps occur on stable network

### Challenge 3: ModuleHost DeltaTime Integration
**Issue:** Module accumulator must use PLL-adjusted time  
**Solution:** Pass controller's dt to DispatchModules  
**Ask if:** Modules don't converge with Master

### Challenge 4: Deterministic Mode Latency
**Issue:** Slow node blocks all nodes  
**Solution:** Document trade-off, consider timeout/kick  
**Ask if:** Need auto-recovery from stuck nodes

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-09-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-09-QUESTIONS.md`

---

## 🔗 References

**Primary Design:** `../../docs/FDP-ModuleHost-User-Guide.md` - Time Control section  
**PLL Spec:** `../../docs/reference-archive/drill-clock-sync.md`  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 09

**Code to Review:**
- Existing `TimeSystem.cs` (if present)
- `ModuleHostKernel.Update()` flow

---

## 💡 Implementation Tips

1. **Start with Master** - Simpler than Slave, easier to test
2. **Test PLL in isolation** - Mock TimePulse delivery before network integration
3. **Use stopwatch diagnostics** - Log convergence metrics during development
4. **Visualize sync** - Create simple rotating object to visually verify sync
5. **Remember: Smooth > Perfect** - PLL trades perfect sync for smooth playback
6. **Document trade-offs** - Continuous vs Deterministic decision tree

**This enables worldwide distributed simulation with millisecond precision!**

Good luck! 🚀
