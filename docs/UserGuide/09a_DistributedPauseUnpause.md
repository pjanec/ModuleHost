## Distributed Pause/Unpause

### Overview

**Distributed pause/unpause** enables synchronized control of simulation time across multiple networked nodes. When you pause on one node (Master), all connected nodes (Slaves) coordinate to pause at the exact same simulation state - ensuring perfect consistency.

**Key Features:**
- **Jitter-Free:** Uses "Future Barrier" pattern to avoid micro-rewinds
- **Coordinated:** All nodes stop at the exact same frame
- **Seamless:** Automatic controller swapping (Continuous ↔ Deterministic modes)
- **Network-Safe:** Coordinate via events, no direct RPC calls

---

### Architecture: Controller Swapping

The system uses **runtime controller swapping** instead of a hybrid controller:

**Running (Continuous Mode):**
```
Master: MasterTimeController (PLL-based wall-clock sync)
Slaves: SlaveTimeController (PLL sync to master's TimePulse)
```

**Paused (Deterministic Mode):**
```
Master: SteppedMasterController (lockstep frame coordination)
Slaves: SteppedSlaveController (waiting for FrameOrder events)
```

**Why Swap Controllers?**
- ✅ **Single Responsibility:** Each controller does one thing well
- ✅ **Reuses Existing Code:** Deterministic mode already implemented
- ✅ **Clean State Transfer:** `GetCurrentState()` / `SeedState()` APIs
- ✅ **Different Protocols:** Continuous uses `TimePulse`, Deterministic uses `FrameOrder`/`FrameAck`

---

### Future Barrier Pattern

**The Problem:** Naive immediate pause causes jitter on Slaves:
```
Time:  Master pauses → publishes "PAUSE NOW"
       Slave gets event 50ms later → has advanced 3 more frames
       → Must rewind 3 frames → jitter/micro-stutter
```

**The Solution:** Future Barrier Frame:
```
Time:  Master decides to pause
       → Chooses BarrierFrame = Current + Lookahead (e.g., +10 frames)
       → Publishes "PAUSE AT FRAME N"
       → All nodes continue running normally
       → All nodes monitor their frame count
       → At BarrierFrame: All nodes simultaneously swap controllers
       → Perfect sync, no rewind needed
```

**Benefits:**
- ✅ **Zero Jitter:** No micro-rewinds on Slaves
- ✅ **Guaranteed Sync:** All nodes at same simulation state
- ✅ **Network Tolerant:** Handles latency variations
- ✅ **Smooth Transition:** No visual artifacts

---

### API: Distributed Time Coordinator (Master)

The `DistributedTimeCoordinator` manages mode switches for  the Master node:

```csharp
using ModuleHost.Core.Time;

// Create coordinator (Master only)
var coordinator = new DistributedTimeCoordinator(
    kernel: moduleHostKernel,
    eventBus: eventBus,
    config: timeConfig
);

// Pause: Switch to Deterministic mode
var slaveNodeIds = new HashSet<int> { 1, 2, 3 };  // IDs of connected slaves
coordinator.SwitchToDeterministic(slaveNodeIds);

// Unpause: Switch back to Continuous mode
coordinator.SwitchToContinuous();

// In simulation loop: Check for pending barrier swaps
coordinator.Update();  // Call every frame
```

**What `SwitchToDeterministic()` Does:**
1. Gets current simulation state
2. Calculates **BarrierFrame** = Current + Lookahead (default: +10 frames)
3. Publishes `SwitchTimeModeEvent` to network
4. Schedules local controller swap for when BarrierFrame is reached

---

### API: Slave Time Mode Listener (Slave)

The `SlaveTimeModeListener` responds to mode switch events from the Master:

```csharp
using ModuleHost.Core.Time;

// Create listener (Slave only)
var listener = new SlaveTimeModeListener(
    kernel: moduleHostKernel,
    eventBus: eventBus,
    localNodeId: 1  // This node's unique ID
);

// In simulation loop: Monitor for mode switch events
listener.Update();  // Call every frame

// Listener automatically:
// - Subscribes to SwitchTimeModeEvent
// - Monitors BarrierFrame countdown
// - Swaps controller at the correct frame
// - Logs diagnostic messages
```

**How It Works:**
1. Receives `SwitchTimeModeEvent` from Master
2. Stores `BarrierFrame` and pending event
3. Each `Update()` checks: `if (CurrentFrame >= BarrierFrame) → ExecuteSwap()`
4. Swaps controller with state transfer
5. **Safety:** If BarrierFrame already passed, swaps immediately (catch-up)

---

### Network Event: SwitchTimeModeEvent

The coordination event published by Master, consumed by all Slaves:

```csharp
using ModuleHost.Core.Time;
using MessagePack;

[MessagePackObject]
public struct SwitchTimeModeEvent
{
    [Key(0)]
    public TimeMode TargetMode { get; set; }  // Continuous or Deterministic
    
    [Key(1)]
    public long BarrierFrame { get; set; }  // Frame at which to switch
    
    [Key(2)]
    public long FrameNumber { get; set; }  // Current reference frame
    
    [Key(3)]
    public double TotalTime { get; set; }  // Current simulation time
    
    [Key(4)]
    public HashSet<int>? AllNodeIds { get; set; }  // For Deterministic mode
    
    [Key(5)]
    public float FixedDeltaSeconds { get; set; }  // For Deterministic mode
}
```

**Usage:**
```csharp
// Publish pause command (Master)
eventBus.Publish(new SwitchTimeModeEvent
{
    TargetMode = Time Mode.Deterministic,
    BarrierFrame = 12345,  // Calculated barrier
    FrameNumber = 12335,   // Current frame
    TotalTime = 205.583,   // Current time
    AllNodeIds = new HashSet<int> { 1, 2, 3 },
    FixedDeltaSeconds = 1.0f / 60.0f
});
```

---

### Configuration: Barrier Lookahead

Configure the future barrier lookahead in `TimeConfig`:

```csharp
var timeConfig = new TimeControllerConfig
{
    Role = TimeRole.Master,
    Mode = TimeMode.Continuous,
    SyncConfig = new TimeConfig
    {
        PauseBarrierFrames = 15  // Frames ahead for pause barrier
    }
};
```

**Guidelines:**
- **Low Latency Network (LAN):** 5-10 frames (~83-166ms @ 60FPS)
- **High Latency Network (Internet):** 15-30 frames (~250-500ms @ 60FPS)
- **High Jitter Network:** 30+ frames (more safety margin)

**Trade-offs:**
- **Higher Values:**
  - ✅ More network tolerance
  - ✅ Guaranteed jitter-free
  - ❌ Slower pause response (user presses pause → ~0.5s delay)
  
- **Lower Values:**
  - ✅ Faster pause response
  - ❌ Risk of jitter on high-latency slaves

---

### Example: Standalone Application with Pause

```csharp
using ModuleHost.Core;
using ModuleHost.Core.Time;
using Fdp.Kernel;

public class GameSimulation
{
    private ModuleHostKernel _kernel;
    private DistributedTimeCoordinator _coordinator;
    private bool _isPaused = false;
    
    public void Initialize()
    {
        var repository = new EntityRepository();
        var eventBus = new FdpEventBus();
        
        _kernel = new ModuleHostKernel(repository, eventBus);
        
        // Configure for standalone mode
        var timeConfig = new TimeControllerConfig
        {
            Role = TimeRole.Standalone,  // No network
            Mode = TimeMode.Continuous,
            InitialTimeScale = 1.0f
        };
        
        _kernel.ConfigureTime(timeConfig);
        _kernel.Initialize();
        
        // Create coordinator (works in standalone too!)
        _coordinator = new DistributedTimeCoordinator(
            _kernel,
            eventBus,
            timeConfig
        );
    }
    
    public void Update()
    {
        // Handle coordinator updates
        _coordinator.Update();
        
        // Handle user input (e.g., from keyboard)
        if (PauseKeyPressed())
        {
            TogglePause();
        }
        
        // Update simulation
        if (!_isPaused)
        {
            _kernel.Update();  // Continuous mode
        }
        else
        {
            // In paused mode, use Step() for manual frame advance
            if (StepKeyPressed())
            {
                _kernel.StepFrame(1.0f / 60.0f);  // Manual step
            }
        }
        
        // Run systems, render, etc.
        // ...
    }
    
    private void TogglePause()
    {
        if (_isPaused)
        {
            // Unpause
            _coordinator.SwitchToContinuous();
            _isPaused = false;
            Console.WriteLine("Simulation RESUMED");
        }
        else
        {
            // Pause (standalone: empty slave set)
            _coordinator.SwitchToDeterministic(new HashSet<int>());
            _isPaused = true;
            Console.WriteLine("Simulation PAUSED");
        }
    }
}
```

**Key Insights:**
- Works in standalone mode (no network)
- Empty slave set → no frame coordination needed
- Still uses controller swapping for consistency
- Manual stepping uses current controller's `Step()` method

---

### Example: Distributed Simulation (Master + Slaves)

**Master Node:**
```csharp
public class MasterSimulation
{
    private ModuleHostKernel _kernel;
    private DistributedTimeCoordinator _coordinator;
    
    public void Initialize()
    {
        var repository = new EntityRepository();
        var eventBus = new FdpEventBus();
        
        _kernel = new ModuleHostKernel(repository, eventBus);
        
        // Master configuration
        var timeConfig = new TimeControllerConfig
        {
            Role = TimeRole.Master,
            Mode = TimeMode.Continuous,
            AllNodeIds = new HashSet<int> { 1, 2, 3 },  // Slave IDs
            SyncConfig = new TimeConfig
            {
                PLLGain = 0.1f,
                PauseBarrierFrames = 10  // 10-frame lookahead
            }
        };
        
        _kernel.ConfigureTime(timeConfig);
        _kernel.Initialize();
        
        // Create coordinator
        _coordinator = new DistributedTimeCoordinator(
            _kernel,
            eventBus,
            timeConfig
        );
    }
    
    public void Update()
    {
        _coordinator.Update();  // Check for barrier swaps
        
        _kernel.Update();
        
        // Handle pause requests from UI
        if (UserRequestsPause())
        {
            _coordinator.SwitchToDeterministic(
                new HashSet<int> { 1, 2, 3 }
            );
        }
        
        if (UserRequestsUnpause())
        {
            _coordinator.SwitchToContinuous();
        }
    }
}
```

**Slave Node (ID = 1):**
```csharp
public class SlaveSimulation
{
    private ModuleHostKernel _kernel;
    private SlaveTimeModeListener _listener;
    
    public void Initialize()
    {
        var repository = new EntityRepository();
        var eventBus = new FdpEventBus();
        
        _kernel = new ModuleHostKernel(repository, eventBus);
        
        // Slave configuration
        var timeConfig = new TimeControllerConfig
        {
            Role = TimeRole.Slave,
            Mode = TimeMode.Continuous,
            LocalNodeId = 1,  // This slave's ID
            SyncConfig = new TimeConfig
            {
                NetworkLatencyMs = 30,  // Measured RTT/2
                PLLGain = 0.1f
            }
        };
        
        _kernel.ConfigureTime(timeConfig);
        _kernel.Initialize();
        
        // Create listener
        _listener = new SlaveTimeModeListener(
            _kernel,
            eventBus,
            localNodeId: 1
        );
    }
    
    public void Update()
    {
        // Listener automatically handles mode switches
        _listener.Update();
        
        _kernel.Update();
        
        // No manual pause/unpause needed!
        // Listener swaps controller automatically when Master commands
    }
}
```

---

### Execution Flow: Pause Sequence

**Timeline:**
```
Frame 100 (Master):  User presses Pause
                     → Coordinator calculates BarrierFrame = 110
                     → Publishes SwitchTimeModeEvent { BarrierFrame = 110 }
                     
Frame 100 (Slave):   Normal operation

Frame 101 (Slave):   Receives SwitchTimeModeEvent
                     → Stores _pendingBarrierFrame = 110
                     → Console: "[Slave-1] Scheduled Pause at Frame 110"

Frame 102-109:       All nodes continue normal operation
                     Continuous mode (PLL sync active)

Frame 110 (Master):  Coordinator detects: CurrentFrame >= BarrierFrame
                     → Executes ExecuteSwapToDeterministic()
                     → Creates SteppedMasterController
                     → Transfers state via SeedState()
                     → Kernel swaps controllers
                     → Console: "[Master] Executed Pause Swap at Frame 110"

Frame 110 (Slave):   Listener detects: CurrentFrame >= BarrierFrame
                     → Executes ExecuteSwapToDeterministic()
                     → Creates SteppedSlaveController
                     → Transfers state via SeedState()
                     → Kernel swaps controllers
                     → Console: "[Slave-1] Executed Pause Swap at Frame 110"

Frame 111+:          All nodes in Deterministic mode (lockstep)
                     Waiting for FrameOrder events
                     No automatic time advancement
```

**Key Points:**
- **Coordinated Entry:** All nodes swap at Frame 110 simultaneously
- **Zero Jitter:** No rewind needed, smooth transition
- **State Preservation:** TotalTime, FrameNumber preserved exactly
- **Mode Transition:** Continuous → Deterministic completed

---

### State Transfer APIs

All time controllers implement state transfer methods:

#### GetCurrentState()

Returns the current simulation state for transfer/saving:

```csharp
public interface ITimeController
{
    /// <summary>
    /// Get current time state for transfer/save.
    /// </summary>
    GlobalTime GetCurrentState();
}

// Usage
var currentState = timeController.GetCurrentState();

// Returns
public struct GlobalTime
{
    public long FrameNumber;           // Current frame
    public double TotalTime;           // Scaled simulation time
    public double UnscaledTotalTime;   // Wall-clock time
    public float DeltaTime;            // Last frame delta
    public float TimeScale;            // Current scale (0.0 = paused)
    // ...
}
```

#### SeedState()

Initializes controller with specific time state:

```csharp
public interface ITimeController
{
    /// <summary>
    /// Initialize controller with specific time state.
    /// Used when swapping controllers or loading saves.
    /// </summary>
    void SeedState(GlobalTime state);
}

// Usage
var newController = new M asterTimeController(eventBus, config);
newController.SeedState(savedState);  // Resume from checkpoint
```

**Used For:**
- Controller swapping (pause/unpause)
- Save/load systems
- Network synchronization
- Replay debugging

---

### Kernel API: SwapTimeController()

The `ModuleHostKernel` provides controller swapping:

```csharp
/// <summary>
/// Swap the time controller at runtime.
/// Transfers state from old to new controller.
/// </summary>
public void SwapTimeController(ITimeController newController)
{
    if (newController == null)
        throw new ArgumentNullException(nameof(newController));
    
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
}
```

**Safety:**
- ✅ Validates new controller is not null
- ✅ Transfers state before disposal
- ✅ Updates `CurrentTime` property
- ✅ Disposes old controller to free resources

---

### Best Practices

#### ✅ DO: Call Update() Every Frame

```csharp
public void GameLoop()
{
    while (running)
    {
        // CRITICAL: Update coordinator/listener every frame
        _coordinator?.Update();  // Master
        _listener?.Update();     // Slave
        
        _kernel.Update();
        
        // ... systems, rendering ...
    }
}
```

**Why:** Coordinator/Listener monitor frame count to execute barrier swaps at the correct time.

---

#### ✅ DO: Configure Appropriate Barrier Lookahead

```csharp
// LAN (low latency)
PauseBarrierFrames = 5   // ~83ms @ 60FPS

// Internet (variable latency)
PauseBarrierFrames = 15  // ~250ms @ 60FPS

// High jitter network
PauseBarrierFrames = 30  // ~500ms @ 60FPS
```

**Measure Your Network:**
```csharp
// Ping test to estimate RTT
var ping = MeasureNetworkRTT();  // e.g., 60ms
var oneWayLatency = ping / 2;     // 30ms
var framesNeeded = (int)Math.Ceiling(oneWayLatency / (1000.0 / 60.0));  // ~2 frames

// Add safety margin
PauseBarrierFrames = framesNeeded * 2;  // 4 frames minimum
```

---

#### ✅ DO: Handle Both Standalone and Distributed

```csharp
// Works in both modes
if (_config.TimeRole == TimeRole.Standalone)
{
    // Empty slave set → no network coordination
    _coordinator.SwitchToDeterministic(new HashSet<int>());
}
else
{
    // Actual slave IDs from network config
    _coordinator.SwitchToDeterministic(_networkConfig.SlaveNodeIds);
}
```

---

#### ⚠️ DON'T: Swap Controllers Manually During Pause

```csharp
// ❌ BAD: Manual swap breaks distributed coordination
_kernel.SwapTimeController(new SteppedMasterController(...));

// ✅ GOOD: Use coordinator
_coordinator.SwitchToDeterministic(slaveIds);
```

**Why:** Coordinator handles barrier calculation, event publishing, and synchronized swap.

---

#### ⚠️ DON'T: Forget State Transfer on Custom Controllers

```csharp
// ❌ BAD: Custom controller without state transfer
public class MyTimeController : ITimeController
{
    // Missing GetCurrentState() and SeedState()!
}

// ✅ GOOD: Implement interface fully
public class MyTimeController : ITimeController
{
    public GlobalTime GetCurrentState() { /* ... */ }
    public void SeedState(GlobalTime state) { /* ... */ }
    // ... other methods ...
}
```

---

### Troubleshooting

#### Problem: Jitter on Pause (Slaves Rewind)

**Symptoms:**
- Visual stutter when pausing
- Different frame counts on Master vs Slaves

**Cause:** BarrierFrame set too low or immediate pause used.

**Solution:**
```csharp
// Increase barrier lookahead
PauseBarrierFrames = 20  // from 5

// Check console logs
[Slave-1] Scheduled Pause at Frame 110 (Current: 105)  // ← 5-frame gap = good
[Slave-1] Scheduled Pause at Frame 100 (Current: 102)  // ← Already passed! Snap!
```

---

#### Problem: Pause Takes Too Long

**Symptoms:**
- Press pause → simulation continues for 0.5 seconds
- Feels unresponsive

**Cause:** Barrier lookahead too high.

**Solution:**
```csharp
// Reduce barrier for faster response (if network allows)
PauseBarrierFrames = 5  // from 30

// Trade-off: Faster pause, but less jitter tolerance
```

---

#### Problem: Slave Never Pauses

**Symptoms:**
- Master pauses correctly
- Slave continues running

**Cause:** Listener not receiving events or not calling `Update()`.

**Debug:**
```csharp
// Add console logging
public void Update()
{
    _listener?.Update();  
    
    // Check if event was received
    if (_listener != null && _listener.HasPendingSwap)
    {
        Console.WriteLine($"[Debug] Waiting for barrier: {_listener.PendingBarrierFrame}");
    }
}

// Check network connection
var events = eventBus.GetEvents<SwitchTimeModeEvent>();
if (events.Any())
{
    Console.WriteLine($"[Debug] Received mode switch event!");
}
```

---

### Performance Characteristics

| Operation | Master | Slave | Network Traffic |
|-----------|--------|-------|-----------------|
| **Pause Request** | 10µs | N/A | 1 event (~200 bytes) |
| **Barrier Check** | 5ns | 5ns | 0 |
| **Controller Swap** | 100µs | 100µs | 0 |
| **Total Pause Latency** | Barrier frames × frame time | Barrier frames × frame time | Negligible |

**Example @ 60 FPS with 10-frame barrier:**
- User presses pause → 166ms until all nodes paused
- No frame drops, smooth transition
- Network bandwidth: ~200 bytes total

---

### Summary

**Distributed Pause/Unpause** enables:
- ✅ **Coordinated control** across networked nodes
- ✅ **Jitter-free transitions** via Future Barrier pattern
- ✅ **Seamless mode switching** between Continuous and Deterministic
- ✅ **Works standalone and distributed** with same API
- ✅ **State preservation** across controller swaps

**Key Components:**
- `DistributedTimeCoordinator` - Master-side pause coordination
- `SlaveTimeModeListener` - Slave-side automatic response
- `SwitchTimeModeEvent` - Network coordination event
- `ITimeController.GetCurrentState()` / `SeedState()` - State transfer

**Pattern:**
1. Master calculates Future Barrier Frame
2. Broadcasts `SwitchTimeModeEvent`
3. All nodes continue to Barrier Frame
4. At Barrier: Synchronized controller swap
5. Resume: Immediate swap back to Continuous

---
