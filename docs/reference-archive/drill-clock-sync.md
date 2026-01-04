Here is the detailed technical specification for **Drill Clock Synchronization**.

This document defines the **Software PLL (Phase-Locked Loop)** algorithm used to keep distributed simulation nodes within the **<10ms** skew tolerance using a low-frequency (1Hz) heartbeat, even in the presence of network jitter.

---

# SPEC-TIME-001: Distributed Clock Synchronization (PLL)

| Metadata | Details |
| :--- | :--- |
| **Project** | ModuleHOst |
| **Type** | Algorithm Specification (Core) |
| **Status** | Draft |
| **Date** | January 2026 |
| **Dependency** | DOC-004 (Time Model) |

---

## 1. Introduction

Distributed simulation requires all nodes to agree on "Now." If Node A fires a shot at $T=10.00$, Node B must process that shot at $T=10.00$.

### 1.1 The Challenge
*   **Latency:** Network packets take time to travel.
*   **Jitter:** That time varies packet-to-packet.
*   **Drift:** Hardware crystal oscillators on motherboards drift by seconds per day.

### 1.2 The Goal
Maintain a local **Simulation Clock** that tracks the Master (Orchestrator) Clock with **< 10ms** variance, using a low-bandwidth **1Hz** synchronization pulse.

---

## 2. The Synchronization Protocol

The synchronization does not send "Simulation Time" directly (because SimTime pauses/scales). It synchronizes the underlying **Reference Wall Clock**.

### 2.1 The TimePulse Message
**Topic:** `Sys.TimePulse`
**Frequency:** 1.0 Hz (and immediately upon Pause/Scale change).

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct TimePulse
{
    public long MasterWallTicks;   // Orchestrator's High-Resolution Clock (UTC)
    public double SimTimeSnapshot; // SimTime at the moment of MasterWallTicks
    public double TimeScale;       // Current speed (0.0, 1.0, 2.0)
    public long SequenceId;        // Detection of dropped packets
}
```

---

## 3. The Software PLL Algorithm

We do not simply set `LocalTime = MasterTime` when a packet arrives. That would cause "Time Snaps" (teleporting physics). Instead, we use a **Control Loop** to adjust the *speed* of the local clock to gently converge with the Master.

### 3.1 Components
1.  **Offset ($E$):** The difference between `MasterTime` and `LocalTime`.
2.  **Gain ($P$):** The aggressiveness of correction (0.1 = correct 10% of error per second).
3.  **Slew Limit:** The maximum allowed speed deviation (e.g., $\pm 5\%$) to prevent physics instability.

### 3.2 The Algorithm (Per Frame)

**Step 1: Ingress (On Pulse Received)**
When a `TimePulse` arrives, we calculate the raw error. We assume a static configured `NetworkLatency` (e.g., 2ms for LAN) or use a sliding average of Ping.

```csharp
void OnPulse(TimePulse pulse)
{
    // 1. Reconstruct what time it is "Right Now" on the Master
    // MasterTime + Latency
    long targetTime = pulse.MasterWallTicks + _config.AverageLatencyTicks;
    
    // 2. Calculate raw error
    long localTime = _stopwatch.ElapsedTicks;
    long errorTicks = targetTime - localTime;
    
    // 3. Push to Smoothing Filter (Jitter rejection)
    _errorFilter.AddSample(errorTicks);
}
```

**Step 2: Jitter Filter (Median/Average)**
Network jitter can cause a single packet to look like a massive time jump.
*   **Logic:** Maintain a circular buffer of the last 5 error samples.
*   **Output:** `FilteredError` = Median(Samples).
*   **Result:** A packet that arrives 50ms late due to a Wi-Fi spike is ignored as an outlier.

**Step 3: The Control Loop (Every Frame)**
In the Host Kernel's main loop, we adjust the `FrameTime`.

```csharp
void UpdateClock()
{
    // Standard accumulation
    long rawDelta = _stopwatch.ElapsedTicks - _lastFrameTicks;
    
    // PLL Logic: Determine dynamic frequency adjustment
    // P-Controller: Adjust speed proportional to error
    double currentError = _errorFilter.GetFilteredValue();
    double correctionFactor = currentError * _gain; // e.g., Gain = 0.1
    
    // Clamp Slew Rate (Safety)
    // Don't let time run faster than 105% or slower than 95%
    correctionFactor = Math.Clamp(correctionFactor, -0.05, 0.05);
    
    // Apply to Delta
    // If we are behind (Positive Error), we run slightly faster (1.05x).
    long adjustedDelta = (long)(rawDelta * (1.0 + correctionFactor));
    
    _virtualWallClock += adjustedDelta;
}
```

---

## 4. Handling Network Exceptions

### 4.1 Extreme Jitter (The Wi-Fi Case)
If the variance of samples in the buffer exceeds a threshold (e.g., > 50ms):
1.  **Action:** The PLL lowers its Gain ($P$) drastically (e.g., 0.01).
2.  **Effect:** The clock becomes "Stiffer." It trusts its local quartz crystal more than the noisy network. It relies on the average over a longer period (10-20 seconds) rather than chasing every packet.

### 4.2 Hard Desync (Snap Threshold)
If `FilteredError > 1000ms` (1 second):
1.  **Diagnosis:** The node has fallen hopelessly behind (e.g., thread freeze, OS sleep). The PLL cannot slew fast enough to catch up.
2.  **Action:** **Hard Snap**.
    *   `_virtualWallClock = targetTime;`
    *   `_errorFilter.Reset();`
3.  **Visual Consequence:** Objects teleport. Physics might pop.
4.  **Logging:** Warning logged: "Clock Sync: Hard Snap performed (Delta: 1.2s)."

---

## 5. SimTime Derivation

The PLL synchronizes the **Virtual Wall Clock**. Simulation Time is derived from that.

$$ SimTime = SimBase + (VirtualWall - WallBase) \times TimeScale $$

*   **Pause:** When `TimeScale` becomes 0.0, `SimTime` freezes. The PLL continues running in the background, keeping `VirtualWall` synced.
*   **Resume:** When `TimeScale` becomes 1.0, `SimTime` resumes smoothly from the new `VirtualWall` anchor.

This ensures that even while Paused, nodes are still negotiating clock alignment, so the "Resume" command is processed synchronously.

---

## 6. Configuration Parameters

| Parameter | Default | Description |
| :--- | :--- | :--- |
| `Sync.PulseFrequency` | 1.0 Hz | Rate of DDS TimePulse. |
| `Sync.Gain` | 0.1 | Convergence speed (Higher = Faster catchup, Lower = Smoother). |
| `Sync.MaxSlew` | 0.05 | Max frequency deviation ($\pm 5\%$). |
| `Sync.SnapThresholdMs` | 500 ms | Error limit triggering a hard teleport. |
| `Sync.JitterWindow` | 5 | Number of samples for median filtering. |

## 7. Verification

*   **Metric:** `sys.clock.error_ms` (OpenTelemetry).
*   **Success Criteria:** During steady state (LAN), error should oscillate within $\pm 2ms$.
*   **Visual Test:** Two screens side-by-side. A rotating radar dish (360 deg/sec) should appear to point at the same angle on both screens within visual tolerance (~1-2 frames).
