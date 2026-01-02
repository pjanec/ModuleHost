# Events in snapshots
We need to include events to snapshots for background modules.

If you do not include events in the snapshot, your Background Modules (Satellites) will suffer from the **"Blinking Light" problem**.

### The Problem: Frequency Mismatch
*   **Simulation:** Runs at **60Hz**.
*   **AI Satellite:** Runs at **10Hz** (once every 6 frames).

If an explosion happens on Frame 2, the Event Bus is normally cleared on Frame 3. By the time the AI wakes up on Frame 6, the event is gone. The AI never "hears" the explosion.

**Conclusion:** A Snapshot for a slow consumer must contain **Accumulated History** of events since that consumer last ran.

---

### The Solution: "Retained History" (Reference Counting)

Do **not** copy events into the snapshot buffer (that’s slow/wasteful).
Instead, **defer the recycling** of the Event Bus buffers.

#### 1. The Concept
Normally, `SwapBuffers()` overwrites the old "Read" buffer.
In this model, `SwapBuffers()` **retires** the buffer to a "History Queue".

1.  **Frame 1:** Events written to **Buffer A**. End of frame: **Buffer A** moved to History (Age 0).
2.  **Frame 2:** Events written to **Buffer B**. End of frame: **Buffer B** moved to History (Age 0). A becomes Age 1.
3.  **Frame 6 (Sync Point):**
    *   AI requests Snapshot.
    *   Host calculates: "AI last saw Frame 0. Current is Frame 6."
    *   Snapshot includes pointers to **Buffers A, B, C, D, E, F**.
4.  **Cleanup:** Once all Satellites have processed those buffers (or a `MaxHistory` limit is reached), the buffers are returned to the memory pool.

### 2. Implementation Logic

#### A. Refactoring `FdpEventBus`
The bus needs to track "Generations" of buffers.

```csharp
public class FdpEventBus
{
    // Instead of just Front/Back, we have a History Chain
    // Key: FrameNumber, Value: The Buffer used that frame
    private readonly Dictionary<ulong, INativeEventStream> _history = new();
    
    public void RetireBuffer(ulong frameNumber, INativeEventStream stream)
    {
        // Don't Clear() yet. Just store it.
        _history[frameNumber] = stream;
    }

    public void PruneHistory(ulong oldestNeededFrame)
    {
        // Recycle buffers older than what any satellite needs
        foreach(var frame in _history.Keys) {
            if (frame < oldestNeededFrame) {
                _history[frame].Clear(); // Reset pointers
                _pool.Return(_history[frame]); // Back to pool
                _history.Remove(frame);
            }
        }
    }
}
```

#### B. The Snapshot Interface
The Snapshot exposes events as a **Series of Spans**, not a single Span. This avoids allocating a massive new array to merge them.

```csharp
public interface ISimWorldSnapshot
{
    // ... Component Access ...

    // Returns an iterator over the batches of events
    // Example: [Frame 1 Events], [Frame 2 Events], ...
    IEnumerable<ReadOnlySpan<T>> GetEventHistory<T>() where T : unmanaged;
}
```

#### C. The Consumer (Satellite) Logic
The background module iterates the history.

```csharp
public void UpdateAsync(ISimWorldSnapshot snap)
{
    // Loop through history (Oldest to Newest)
    foreach (ReadOnlySpan<Explosion> batch in snap.GetEventHistory<Explosion>())
    {
        foreach (ref readonly var exp in batch)
        {
            // React to explosion
            _threatMap.AddDanger(exp.Position);
        }
    }
}
```

---

### 3. Performance Analysis

| Operation | Standard Bus | Retained History Bus |
| :--- | :--- | :--- |
| **Write Cost** | Fast (Append) | Fast (Append) |
| **Swap Cost** | Fast (Pointer Swap) | Fast (Pointer Move to Dictionary) |
| **Snapshot Cost** | N/A | **Zero.** (Just passing a list of buffer pointers) |
| **Read Cost** | Linear (1 Buffer) | Linear (N Buffers) |
| **Memory Usage** | Low (2 Buffers) | Moderate (2 + N Buffers) |

**The "Memory Spike" Risk:**
If you have a huge spike in events (e.g., 10,000 particles) and your AI runs at 1Hz (every 60 frames), you are holding 60 frames of heavy event data in RAM.
*   **Mitigation:** Set a strict `MaxHistoryFrames` (e.g., 10). If a Satellite is too slow (runs at 1Hz), it **loses data**. It receives a `DataLossDetected` flag in the snapshot so it knows its state might be invalid.

---

### 4. Tier 2 (Managed) Events implication
The same logic applies to Tier 2 `List<T>`.
*   Do not `Clear()` the list at the end of the frame.
*   Move the `List<T>` reference to a `HistoryQueue`.
*   Allocate a new `List<T>` (from a pool) for the new frame.
*   The Snapshot holds references to the old Lists.

### Summary

1.  **Feasibility:** High. It reuses your existing Pooling logic.
2.  **Performance:** Excellent. It is a "Zero-Copy" history mechanism. You are trading RAM (holding buffers longer) for CPU (no copying).
3.  **Correctness:** This is the **only** way to write correct asynchronous logic. Without history, your AI is deaf to the world 5 frames out of 6.

**Implementation Tip:**
Add `LastProcessedTick` to your Satellites.
When asking the Host for a Snapshot:
`repo.CreateSnapshot(mask, satellite.LastProcessedTick);`
The Host uses that tick to decide which Event Buffers to attach to the snapshot.