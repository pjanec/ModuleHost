## Time Control & Synchronization

### The GlobalTime Descriptor

In distributed simulations, each node needs a consistent view of **simulation time**. This is separate from **wall clock time** (real world).

**GlobalTime Singleton:**

```csharp
public struct GlobalTime
{
    public double TotalTime;        // Elapsed simulation time (seconds)
    public float DeltaTime;         // Time since last frame (seconds)
    public float TimeScale;         // Speed multiplier (0.0 = paused, 1.0 = realtime, 2.0 = 2x speed)
    public bool IsPaused;           // Convenience flag (TimeScale == 0.0)
    public long FrameNumber;        // Current frame index
}
```

**Usage in Systems:**

```csharp
public class PhysicsSystem : ComponentSystem
{
    protected override void OnUpdate()
    {
        // Get global time from world
        var time = World.GetSingleton<GlobalTime>();
        
        // Use simulation time, not wall clock
        float dt = time.DeltaTime;
        
        // Physics updates with scaled time
        foreach (var entity in _query)
        {
            var vel = World.GetComponent<Velocity>(entity);
            var pos = World.GetComponent<Position>(entity);
            
            // This respects TimeScale automatically
            pos.Value += vel.Value * dt;
            
            World.SetComponent(entity, pos);
        }
    }
}
```

### Two Modes of Time Synchronization

Distributed simulations face a fundamental challenge: **How do multiple nodes stay synchronized?**

FDP/ModuleHost supports two modes:

#### Mode 1: Continuous (Real-Time / Scaled)

**Best-effort synchronization**. Time flows continuously. Nodes chase the master clock.

**When to use:**
- Training simulations (flight simulators, tactical trainers)
- Game servers (MMOs, multiplayer games)
- Live demonstrations
- Most simulation scenarios (90% of use-cases)

**Characteristics:**
- ✅ Low latency (~20ms variance)
- ✅ Smooth playback
- ✅ Can pause/resume/speed up
- ⚠️ Not perfectly deterministic (acceptable for most use-cases)

#### Mode 2: Deterministic (Lockstep / Stepped)

**Strict synchronization**. Frame N starts only when Frame N-1 is done everywhere.

**When to use:**
- Scientific simulations requiring exact reproducibility
- Regulatory compliance (aerospace, medical)
- Debugging distributed bugs (replay from logs)
- Network testing (controlled timing)

**Characteristics:**
- ✅ Perfectly deterministic
- ✅ Repeatable from logs
- ⚠️ High latency (limited by slowest node)
- ⚠️ No smooth playback if network lags

### The Clock Model

**Separation of Concerns:**
- **Wall Clock** - Real world time (UTC ticks)
- **Simulation Clock** - Virtual world time (can be paused, scaled, stepped)

**Master/Slave Architecture:**
- **Master Clock** - One node (Orchestrator) owns authoritative time
- **Slave Clocks** - All other nodes follow Master using Phase-Locked Loop (PLL)

**The Simulation Time Equation:**

$$T_{sim} = T_{base} + (T_{wall} - T_{start}) \times Scale$$

Where:
- $T_{sim}$ - Current simulation time
- $T_{base}$ - Simulation time when last speed change happened
- $T_{wall}$ - Current wall clock time (UTC)
- $T_{start}$ - Wall clock time when last speed change happened
- $Scale$ - Speed coefficient

**Example:**

```
Initial State:
  T_base = 0.0
  T_start = 12:00:00 UTC
  Scale = 1.0 (realtime)

At 12:00:10 UTC:
  T_sim = 0.0 + (12:00:10 - 12:00:00) × 1.0 = 10.0 seconds

Speed up to 2x at T_sim = 10.0:
  T_base = 10.0
  T_start = 12:00:10 UTC
  Scale = 2.0

At 12:00:20 UTC:
  T_sim = 10.0 + (12:00:20 - 12:00:10) × 2.0 = 30.0 seconds
  (10 wall seconds = 20 sim seconds due to 2x speed)

Pause at T_sim = 30.0:
  T_base = 30.0
  T_start = 12:00:20 UTC
  Scale = 0.0

At 12:00:40 UTC:
  T_sim = 30.0 + (12:00:40 - 12:00:20) × 0.0 = 30.0 seconds
  (Frozen at 30.0 despite 20 wall seconds passing)
```

### Continuous Mode Implementation

**Network Protocol:**

**Topic:** `Sys.TimePulse` (1Hz heartbeat + on-change)

**Payload:**
```csharp
public class TimePulseDescriptor
{
    public long MasterWallTime;      // Master's UTC ticks
    public double SimTimeSnapshot;   // Master's current T_sim
    public float TimeScale;          // Master's current Scale
    public bool IsPaused;            // Master's pause state
}
```

**Master Node Behavior:**

```csharp
public class MasterTimeController : ITimeController
{
    private Stopwatch _wallClock = Stopwatch.StartNew();
    private double _simTimeBase = 0.0;
    private long _scaleChangeWallTicks = 0;
    private float _timeScale = 1.0f;
    
    public void Update(out float dt, out double totalTime)
    {
        // Calculate wall delta
        long currentWallTicks = _wallClock.ElapsedTicks;
        double wallDelta = (currentWallTicks - _lastWallTicks) / (double)Stopwatch.Frequency;
        _lastWallTicks = currentWallTicks;
        
        // Calculate sim delta (respecting scale)
        dt = (float)(wallDelta * _timeScale);
        totalTime = _simTimeBase + (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
        
        // Publish to network (1Hz or on-change)
        if (ShouldPublishPulse())
        {
            _networkWriter.Write(new TimePulseDescriptor
            {
                MasterWallTime = DateTimeOffset.UtcNow.Ticks,
                SimTimeSnapshot = totalTime,
                TimeScale = _timeScale,
                IsPaused = _timeScale == 0.0f
            });
        }
    }
    
    public void SetTimeScale(float scale)
    {
        // Save current sim time as new base
        _simTimeBase = CalculateCurrentSimTime();
        _scaleChangeWallTicks = _wallClock.ElapsedTicks;
        _timeScale = scale;
        
        // Immediately publish to slaves
        PublishTimePulse();
    }
}
```

**Slave Node Behavior (with PLL):**

```csharp
public class SlaveTimeController : ITimeController
{
    private Stopwatch _wallClock = Stopwatch.StartNew();
    private double _simTimeBase = 0.0;
    private long _scaleChangeWallTicks = 0;
    private float _timeScale = 1.0f;
    
    // PLL state
    private double _timeError = 0.0;
    private const float _correctionFactor = 0.01f; // 1% adjustment per frame
    
    public void OnTimePulseReceived(TimePulseDescriptor pulse)
    {
        // Calculate what our sim time SHOULD be based on master's snapshot
        long currentWallTicks = DateTimeOffset.UtcNow.Ticks;
        double wallDeltaSincePulse = (currentWallTicks - pulse.MasterWallTime) / (double)TimeSpan.TicksPerSecond;
        double masterSimTime = pulse.SimTimeSnapshot + wallDeltaSincePulse * pulse.TimeScale;
        
        // Calculate our current sim time
        double localSimTime = CalculateCurrentSimTime();
        
        // Calculate error
        _timeError = masterSimTime - localSimTime;
        
        // Update scale
        _timeScale = pulse.TimeScale;
    }
    
    public void Update(out float dt, out double totalTime)
    {
        // Calculate wall delta
        long currentWallTicks = _wallClock.ElapsedTicks;
        double wallDelta = (currentWallTicks - _lastWallTicks) / (double)Stopwatch.Frequency;
        _lastWallTicks = currentWallTicks;
        
        // PLL Correction: Gently adjust dt to converge with master
        // If we're behind (error > 0), run slightly faster
        // If we're ahead (error < 0), run slightly slower
        float correction = (float)(_timeError * _correctionFactor);
        float adjustedScale = _timeScale + correction;
        
        // Calculate dt with adjusted scale
        dt = (float)(wallDelta * adjustedScale);
        totalTime = CalculateCurrentSimTime() + dt;
        
        // Reduce error by what we just corrected
        _timeError -= correction * wallDelta;
    }
}
```

**Why PLL (Phase-Locked Loop)?**

Without PLL:
```
Master says: T_sim = 10.0
Slave has: T_sim = 9.8

Bad approach: Snap to 10.0
Result: Time jumps! Entities teleport! Rubber-banding!

Good approach (PLL): Gradually increase dt by 1% for next few frames
Frame 0: dt = 0.01616 (instead of 0.016)
Frame 1: dt = 0.01616
Frame 2: dt = 0.01616
...
After 100 frames: Converged to 10.0 smoothly
```

### Deterministic Mode Implementation

**Network Protocol:**

**Topic:** `Sys.FrameOrder` (Master → All)
**Topic:** `Sys.FrameAck` (All → Master)

**Frame Order Descriptor:**
```csharp
public class FrameOrderDescriptor
{
    public long FrameID;        // Frame number to execute
    public float FixedDelta;    // Fixed dt for this frame (e.g., 0.016s)
}
```

**Frame Ack Descriptor:**
```csharp
public class FrameAckDescriptor
{
    public long FrameID;        // Frame just completed
    public int NodeID;          // Who completed it
}
```

**Lockstep Cycle:**

```
1. Master waits for all ACKs for Frame N-1

2. Master publishes FrameOrder { FrameID: N, FixedDelta: 0.016 }

3. Slave receives FrameOrder:
   - Runs simulation with dt = 0.016
   - Executes all systems
   - **BARRIER: Pauses at end of frame**
   - Publishes FrameAck { FrameID: N, NodeID: Me }

4. Repeat
```

**Master Implementation:**

```csharp
public class SteppedTimeController : ITimeController
{
    private long _currentFrame = 0;
    private float _fixedDelta = 0.016f;
    private HashSet<int> _pendingAcks = new();
    private bool _waitingForAcks = false;
    
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
                _networkWriter.Write(new FrameOrderDescriptor
                {
                    FrameID = _currentFrame,
                    FixedDelta = _fixedDelta
                });
                
                // Reset pending ACKs
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
}
```

**Slave Implementation:**

```csharp
public class SteppedSlaveController : ITimeController
{
    private long _currentFrame = 0;
    private float _fixedDelta = 0.016f;
    private bool _hasFrameOrder = false;
    
    public void OnFrameOrderReceived(FrameOrderDescriptor order)
    {
        _currentFrame = order.FrameID;
        _fixedDelta = order.FixedDelta;
        _hasFrameOrder = true;
    }
    
    public void Update(out float dt, out double totalTime)
    {
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
        
        // After simulation completes (end of Update), send ACK
        _hasFrameOrder = false;
    }
    
    public void SendFrameAck()
    {
        _networkWriter.Write(new FrameAckDescriptor
        {
            FrameID = _currentFrame,
            NodeID = _localNodeId
        });
    }
}
```



### Deterministic Mode (Lockstep)

For **frame-perfect synchronization** across distributed peers, use **lockstep mode**. The master waits for all slaves to finish each frame before advancing.

#### When to Use

| Mode | Use Case | Sync Variance | Latency Sensitivity |
|------|----------|---------------|---------------------|
| **Continuous** | Real-time simulation | ~10ms | Low (PLL smooths) |
| **Deterministic** | Frame-perfect replay, anti-cheat | 0ms | High (stalls on slow peer) |

**Use Deterministic when:**
- Server must verify client state (anti-cheat)
- Debugging requires exact frame matching
- Replay must be bit-identical to live run

#### Architecture

```
Master                Slave 1              Slave 2
  |                      |                    |
  |---FrameOrder 0------>|                    |
  |---FrameOrder 0-------------------->|
  |                      |                    |
  |                   [Execute               |
  |                    Frame 0]              |
  |                      |                    |
  |<--FrameAck 0---------|                    |
  |                                       [Execute
  |                                        Frame 0]
  |                                           |
  |<--FrameAck 0-----------------------------|
  |                                           |
[All ACKs received]                          |
  |                                           |
  |---FrameOrder 1------>|                    |
  |---FrameOrder 1-------------------->|
  |                   [Execute               |
  |                    Frame 1]          [Execute
  |                      |                Frame 1]
```

#### Setup

**Master:**
```csharp
using ModuleHost.Core.Time;

var nodeIds = new HashSet<int> { 1, 2, 3 };  // IDs of all slave peers

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Master,
    Mode = TimeMode.Deterministic,
    AllNodeIds = nodeIds,  // Required for lockstep
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f  // 60 FPS
    }
};

var controller = TimeControllerFactory.Create(eventBus, timeConfig);
```

**Slave:**
```csharp
var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Slave,
    Mode = TimeMode.Deterministic,
    LocalNodeId = 1,  // This slave's ID
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f  // Must match master
    }
};

var controller = TimeControllerFactory.Create(eventBus, timeConfig);
```

#### Network Messages

Lockstep uses two event types:

```csharp
// Master → Slaves: "Execute Frame N"
[EventId(2001)]
public struct FrameOrderDescriptor
{
    public long FrameID;         // Frame to execute
    public float FixedDelta;     // Timestep (usually constant)
    public long SequenceID;      // For reliability checking
}

// Slaves → Master: "Frame N complete"
[EventId(2002)]
public struct FrameAckDescriptor
{
    public long FrameID;         // Completed frame
    public int NodeID;           // Slave ID
    public double TotalTime;     // For verification
}
```

#### Execution Flow

1. **Master publishes FrameOrder**
2. **Slaves consume FrameOrder, execute frame**
3. **Slaves publish FrameAck**
4. **Master waits for all ACKs**
5. **When all ACKs received** → Master advances to next frame
6. **Repeat**

#### Stalling Behavior

If one slave is slow, **the entire cluster waits**:

```
Frame 50:
Master: Waiting for ACKs from [1, 2, 3]
Slave 1: ACK sent (10ms)
Slave 2: ACK sent (12ms)
Slave 3: Still processing... (500ms)  ← Bottleneck

Master: Stalled (500ms total)
Result: Frame 50 took 500ms for entire cluster
```

**Mitigation:**
- Use **equal hardware** for all slaves
- Set **timeout warnings** in `TimeConfig.SnapThresholdMs`
- Monitor `Console.WriteLine` output: `"[Lockstep] Frame 50 took 500ms"`

#### Debugging

```csharp
// Enable diagnostic logging
var config = new TimeConfig
{
    SnapThresholdMs = 100.0  // Warn if frame > 100ms
};

// Console output:
// [Lockstep] Frame 50 took 523.4ms (threshold: 100.0ms)
// [Lockstep] Late ACK from Node 3: Frame 48 (current: 50)
```

#### Comparison: Continuous vs Deterministic

```csharp
// Continuous Mode (PLL)
var time = controller.Update();
// Returns immediately with best-effort sync
// dt may vary slightly (16.5ms, 16.8ms) due to PLL correction

// Deterministic Mode (Lockstep)
var time = controller.Update();
// May return immediately OR stall waiting for ACKs
// dt is ALWAYS exactly FixedDeltaSeconds (16.667ms)
```

#### Best Practices

1. **Use for verification, not primary gameplay:** Lockstep adds latency
2. **Monitor ACK times:** Identify slow peers proactively
3. **Match hardware:** Heterogeneous clusters will stall
4. **Test with network simulation:** Add artificial latency to catch edge cases

---






### Time Control Usage Examples

#### Example 1: Pause Simulation

```csharp
public class SimulationController
{
    private MasterTimeController _timeController;
    
    public void OnPauseButtonClicked()
    {
        _timeController.SetTimeScale(0.0f);
        // All slave nodes will receive TimePulse and pause smoothly
    }
    
    public void OnResumeButtonClicked()
    {
        _timeController.SetTimeScale(1.0f);
        // Resume at normal speed
    }
}
```

#### Example 2: Variable Speed Playback

```csharp
public class TrainingControls
{
    public void SetPlaybackSpeed(float speed)
    {
        // 0.5x = Slow motion for analysis
        // 1.0x = Realtime
        // 2.0x = Fast forward
        _timeController.SetTimeScale(speed);
    }
}
```

#### Example 3: Deterministic Replay from Log

```csharp
public class ReplayController
{
    private SteppedTimeController _timeController;
    private List<FrameOrderDescriptor> _recordedFrames;
    
    public void ReplayFromLog()
    {
        // Switch to deterministic mode
        _timeController.SetMode(TimeMode.Stepped);
        
        // Replay each frame exactly as it was recorded
        foreach (var frameOrder in _recordedFrames)
        {
            _timeController.ExecuteFrame(frameOrder);
            // Exact dt, exact frame number - deterministic!
        }
    }
}
```

### Choosing the Right Mode

**Use Continuous Mode when:**
- ✅ You need smooth, responsive playback
- ✅ Network latency varies
- ✅ Nodes have different performance characteristics
- ✅ Users need pause/speed controls
- ✅ "Good enough" synchronization is acceptable (~20ms variance)

**Use Deterministic Mode when:**
- ✅ You need perfect reproducibility
- ✅ Debugging distributed bugs
- ✅ Regulatory compliance (audit trails)
- ✅ Scientific validation
- ⚠️ Can tolerate latency (slowest node bottleneck)


---

## Time Control Setup Examples

The ModuleHostKernel provides flexible time control through the `TimeControllerFactory`, supporting both standalone and distributed simulation scenarios.

### Configuration Overview

Time control is configured via `TimeControllerConfig` with three key properties:

- **Role**: `Standalone`, `Master`, or `Slave`
- **Mode**: `Continuous` (PLL-synchronized) or `Deterministic` (lockstep)
- **SyncConfig**: Fine-tuning parameters (PLL gain, network latency, fixed delta)

### Standalone Application

For single-process simulations with local wall-clock time:

```csharp
using ModuleHost.Core;
using ModuleHost.Core.Time;
using Fdp.Kernel;

// Create kernel
var repository = new EntityRepository();
var eventAccumulator = new EventAccumulator();
var kernel = new ModuleHostKernel(repository, eventAccumulator);

// Configure standalone time
var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Standalone,
    Mode = TimeMode.Continuous,
    InitialTimeScale = 1.0f  // Realtime (0.0 = paused, 2.0 = 2x speed)
};

kernel.ConfigureTime(timeConfig);
kernel.Initialize();

// Game loop
while (running)
{
    kernel.Update();  // Automatically advances GlobalTime
    
    // Access current time
    var time = kernel.CurrentTime;
    Console.WriteLine($"Frame {time.FrameNumber}: {time.TotalTime:F2}s");
    
    // Runtime control
    if (pauseRequested)
        kernel.SetTimeScale(0.0f);  // Pause
    if (fastForwardRequested)
        kernel.SetTimeScale(2.0f);  // 2x speed
}
```

### Distributed Simulation - Continuous Mode

For networked simulations with smooth PLL-based time synchronization.

#### Server (Master)

The master publishes `TimePulse` events to synchronize slaves:

```csharp
var eventAccumulator = new EventAccumulator();
var kernel = new ModuleHostKernel(repository, eventAccumulator);

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Master,
    Mode = TimeMode.Continuous,
    InitialTimeScale = 1.0f,
    SyncConfig = TimeConfig.Default  // Can tune PLL parameters
};

kernel.ConfigureTime(timeConfig);
kernel.Initialize();

// Master drives time, slaves follow
while (running)
{
    kernel.Update();
}
```

#### Client (Slave)

The slave subscribes to `TimePulse` and adjusts local clock via PLL:

```csharp
var eventAccumulator = new EventAccumulator();
var kernel = new ModuleHostKernel(repository, eventAccumulator);

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Slave,
    Mode = TimeMode.Continuous,
    SyncConfig = new TimeConfig
    {
        NetworkLatencyMs = 30,      // Measured RTT/2
        PLLGain = 0.1f,             // Lower = smoother, higher = faster convergence
        MaxTimeDriftMs = 100        // Safety threshold
    }
};

kernel.ConfigureTime(timeConfig);
kernel.Initialize();

// Slave automatically follows master
while (running)
{
    kernel.Update();  // Consumes TimePulse events, adjusts local time
}
```

### Distributed Simulation - Deterministic Mode

For networked simulations requiring frame-perfect synchronization (lockstep).

#### Server (Master)

The master coordinates all peers, waiting for ACKs before advancing:

```csharp
var eventAccumulator = new EventAccumulator();
var kernel = new ModuleHostKernel(repository, eventAccumulator);

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Master,
    Mode = TimeMode.Deterministic,
    AllNodeIds = new HashSet<int> { 1, 2, 3 },  // IDs of all slave nodes
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f,  // 60 FPS fixed timestep
        AckTimeoutMs = 5000                 // Timeout for stragglers
    }
};

kernel.ConfigureTime(timeConfig);
kernel.Initialize();

// Master publishes FrameOrder, waits for FrameAck from all slaves
while (running)
{
    kernel.Update();  // Blocks until all ACKs received or timeout
}
```

#### Client 1 (Slave)

Each slave processes frames in lockstep, sending ACKs after completion:

```csharp
var eventAccumulator = new EventAccumulator();
var kernel = new ModuleHostKernel(repository, eventAccumulator);

var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Slave,
    Mode = TimeMode.Deterministic,
    LocalNodeId = 1,  // Unique ID for this peer
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f  // Must match master
    }
};

kernel.ConfigureTime(timeConfig);
kernel.Initialize();

// Slave waits for FrameOrder, processes frame, sends FrameAck
while (running)
{
    kernel.Update();  // Blocks until FrameOrder received
}
```

#### Client 2 (Slave)

```csharp
var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Slave,
    Mode = TimeMode.Deterministic,
    LocalNodeId = 2,  // Different ID
    SyncConfig = new TimeConfig
    {
        FixedDeltaSeconds = 1.0f / 60.0f
    }
};
// ... same as Client 1
```

### Runtime Time Control

All modes support dynamic time scale adjustments:

```csharp
// Pause simulation
kernel.SetTimeScale(0.0f);

// Resume at normal speed
kernel.SetTimeScale(1.0f);

// Slow motion (half speed)
kernel.SetTimeScale(0.5f);

// Fast forward (2x speed)
kernel.SetTimeScale(2.0f);

// Access current time state
var time = kernel.CurrentTime;
bool isPaused = (time.TimeScale == 0.0f);
```

### Best Practices

1. **Standalone**: Use for single-player games, tools, or development
2. **Continuous Mode**: Use for networked games with visual smoothness priority (PvE, co-op)
3. **Deterministic Mode**: Use for competitive games requiring exact reproducibility (PvP, replays)
4. **Network Latency**: Measure actual RTT and configure `NetworkLatencyMs = RTT/2`
5. **PLL Tuning**: Higher gain (0.2-0.5) for LAN, lower gain (0.05-0.1) for internet
6. **Fixed Delta**: Match your physics timestep (typically 1/60 or 1/120)
7. **Timeout Values**: Set `AckTimeoutMs` based on worst-case frame duration + network jitter

### Integration with Event Bus

Time controllers publish/consume events via the `EventAccumulator`:

- **TimePulse**: Published by Continuous Master, consumed by Continuous Slaves
- **FrameOrder**: Published by Deterministic Master, consumed by Deterministic Slaves
- **FrameAck**: Published by Deterministic Slaves, consumed by Deterministic Master

Ensure your network layer translates these events across process boundaries.

### Accessing Global Time

Systems and modules can access the current time singleton:

```csharp
public class MySystem : ComponentSystem
{
    public override void Create(EntityRepository world)
    {
        // ... setup
    }
    
    public override void Run()
    {
        var time = World.GetSingletonRO<GlobalTime>();
        
        float deltaTime = time.DeltaTime;
        double totalTime = time.TotalTime;
        ulong frameNumber = time.FrameNumber;
        
        // Use time for gameplay logic
        if (totalTime > 60.0)
        {
            // Trigger event after 60 seconds
        }
    }
}
```

### Migration from Legacy Code

If you have existing manual time management:

**Before:**
```csharp
float deltaTime = (float)stopwatch.Elapsed.TotalSeconds;
globalTime += deltaTime;
kernel.Update(deltaTime);
```

**After:**
```csharp
kernel.ConfigureTime(new TimeControllerConfig 
{ 
    Role = TimeRole.Standalone 
});
kernel.Update();  // No manual delta needed
```

The `TimeController` handles all timing internally, providing accurate, scalable time management.

---

