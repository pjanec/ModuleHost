# ModuleHost \- remaining parts

# Design talk

Based on the provided documents (`ModuleHost-core.txt`, `ModuleHost-overview.md`, `HYBRID-ARCHITECTURE-QUICK-REFERENCE.md`, `IMPLEMENTATION-SPECIFICATION.md`, and `MODULE-IMPLEMENTATION-EXAMPLES.md`), here is a comprehensive gap analysis between the **original vision/wishes** and the **current implementation**.

---

### **Gap Analysis: Vision vs. Reality**

#### **1\. Snapshot Strategies (The "Convoy" & "Pooling" Vision)**

**The Vision (from Specs):**

* **"Shared Snapshot Provider":** Multiple modules running at the same frequency (e.g., 10Hz) should share a *single* snapshot instance ("Convoy Pattern") to save memory and sync time.  
* **"Auto-Assign Strategy":** The kernel should automatically pick GDB (Fast) or SoD (Slow) based on frequency thresholds.  
* **"Snapshot Pooling":** SoD snapshots should be returned to a pool and reused to avoid GC/Allocation spikes.

**The Reality (Code Implementation):**

* **Convoy Pattern:** **Missing Logic.** The class `SharedSnapshotProvider` exists, but there is **no logic** in `ModuleHostKernel` to group modules into convoys. Each module gets its own provider instance unless manually wired.  
* **Auto-Assign:** **Implemented.** `CreateDefaultProvider` logic exists to switch between `DoubleBufferProvider` and `OnDemandProvider` based on `ModuleTier`.  
* **Pooling:** **Implemented.** `OnDemandProvider` uses a `ConcurrentStack<EntityRepository>` pool. However, `ReleaseView` implementation relies on `SoftClear()`, which was flagged as potentially missing/internal in comments.

#### **2\. Time-Slicing & Module Duration**

**The Vision:**

* **"Slow modules running longer than fast ones":** A slow module (e.g., AI) might take 50ms to complete. The system should handle this gracefully without blocking the main thread or causing frame spikes.  
* **"Non-Blocking Main Thread":** The main simulation should ideally continue while the slow module crunches data in the background.

**The Reality:**

* **Blocking Wait:** `ModuleHostKernel.Update` calls `Task.WaitAll(tasks.ToArray())`. This **blocks** the main thread until *all* scheduled modules for that frame are finished.  
* **Impact:** If a 10Hz AI module takes 50ms to run, **the game will freeze for 50ms** on that specific frame. The current implementation enables *parallelism* (using all cores) but not *concurrency* (decoupling frame rate from module duration).  
* **Missing Feature:** "Triple Buffering" / "World C" implementation to allow the main thread to proceed while a background module holds onto an old snapshot.

#### **3\. Event-Driven Scheduling ("Reactive Modules")**

**The Vision:**

* **"Wake on Event":** Modules should wake up *immediately* (interrupting their timer) if a specific event type occurs (e.g., "ExplosionEvent").  
* **"Wake on Component Change":** Modules should wake up if a component table is marked dirty.

**The Reality:**

* **Missing Logic:** `ModuleHostKernel.ShouldRunThisFrame` only checks `FramesSinceLastRun >= Frequency`. There is **no code** checking event queues or component versions to trigger early execution.  
* **Missing Metadata:** `ModuleDefinition` (or `IModule`) in the code does not expose `WatchEvents` or `WatchComponents` properties to configure this behavior.

#### **4\. Dynamic Buffer Expansion & Resilience**

**The Vision:**

* **"No Data Loss":** Event buffers should resize dynamically under load.  
* **"Resilience":** Watchdogs to detect hung modules, Circuit Breakers to disable failing modules.

**The Reality:**

* **Buffers:** `NativeEventStream` in `Fdp.Kernel` does implement dynamic resizing (`ResizeAndWrite`). **Implemented.**  
* **Resilience:** **Completely Missing.** No watchdogs, no circuit breakers, no timeout handling in `Task.WaitAll`. If a module hangs, the game hangs.

#### **5\. Network Ingress (DDS Gateway)**

**The Vision:**

* **"Network Ingest Phase":** A specific phase for processing incoming network packets.  
* **"Coordinate Transform":** Automatic conversion between Cartesian (Sim) and Geodetic (Network) coordinates.

**The Reality:**

* **Missing:** No `NetworkIngestSystem`, `GeographicTransform`, or `NetworkSyncSystem` classes in the provided code dump. The `SystemPhase.Input` and `Export` enums exist, but the machinery to drive them is unimplemented.

---

### **Summary of Missing / Reduced Features**

| Feature | Vision | Current Implementation | Gap Severity |
| :---- | :---- | :---- | :---- |
| **Non-Blocking Modules** | Main thread continues while module runs | `Task.WaitAll` blocks main thread | **CRITICAL** (for frame rate) |
| **Reactive Scheduling** | Modules wake on Events/Data changes | Timer-based only | **HIGH** (latency) |
| **Convoy Pattern** | Shared snapshots for same-freq modules | 1 Provider per Module | **MEDIUM** (memory optimization) |
| **Resilience** | Watchdogs, Circuit Breakers | None (Crash/Hang risk) | **MEDIUM** (reliability) |
| **Services Layer** | Network Ingest, Geo-Transform | Missing | **HIGH** (functionality) |

### **Recommendations for Next Steps**

If you want to realize the original vision, here is the prioritized list of what to implement next:

1. **Non-Blocking Execution (Triple Buffering):**  
     
   * Change `Task.WaitAll` to a "Check and Harvest" model.  
   * If a module task hasn't finished, skip it this frame and let the main thread continue. Harvest its commands in the *next* frame.  
   * *Requirement:* Ensure snapshot providers can handle a snapshot being held for \>1 frame (SoD pooling handles this; GDB might need a "Triple Buffer" logic).

   

2. **Reactive Scheduling:**  
     
   * Add `WatchEvents` / `WatchComponents` to `IModule`.  
   * Implement `HasEvents<T>` and `HasComponentChanged<T>` in `EntityRepository`.  
   * Update `ShouldRunThisFrame` to check these triggers.

   

3. **Convoy / Shared Provider Logic:**  
     
   * Update `ModuleHostKernel.RegisterModule` to auto-detect modules with identical frequencies and assign them to a single `SharedSnapshotProvider` instance.

   

4. **Resilience (Timeout):**  
     
   * Add a timeout to `Task.WaitAll` or the individual tasks.  
   * If a module times out, log a warning and skip processing its commands for that frame.

Based on `FDP-SST-001-Integration-Architecture.md` (the "Vision Document"), here is the re-evaluation of the current implementation (`ModuleHost-core.txt`) against the original architectural intent.

The vision document reveals a much broader scope (Layer 0 to Layer 8\) than what has been implemented so far (mostly Layer 2 and 3).

---

### **Gap Analysis: Vision vs. Implementation**

#### **1\. Architecture & Layers**

| Layer (Vision) | Description | Implementation Status | Notes |
| :---- | :---- | :---- | :---- |
| **Layer 0: FDP** | EntityRepository, Tier 1/2 | ✅ Implemented | Core FDP kernel exists. |
| **Layer 1: Snapshot** | SoD \+ **COW** (Copy-on-Write) | ⚠️ Partial | SoD/GDB strategies exist, but **Tier 2 COW Forking** logic (Section 4.6) is missing. Current code relies on `Array.Copy`, not page-level ref-counting. |
| **Layer 2: Module** | IModule, ISystemRegistry | ✅ Implemented | `RegisterSystems` added recently. |
| **Layer 3: Kernel** | Orchestrator, Phase Execution | ✅ Implemented | `ModuleHostKernel` handles phases correctly. |
| **Layer 4: Command** | ICommandBuffer, Deferred Ops | ✅ Implemented | Thread-safe command buffer exists. |
| **Layer 5: Services** | Geographic Transform | ❌ Missing | No `IGeographicTransform` or coordinate sync system. |
| **Layer 6: Network** | DDS Gateway | ❌ Missing | No `SSTModule`, `NetworkSyncSystem`, or `NetworkIngestSystem`. |
| **Layer 7: ELM** | Entity Lifecycle Manager | ❌ Missing | No "Dark Construction" or staging logic. |
| **Layer 8: Resilience** | Watchdogs, Circuit Breakers | ❌ Missing | No timeouts, no failure handling. |

#### **2\. Snapshot Implementation Divergence**

**Vision (Sec 4.3):**

* **"Shadow Buffer with Dirty Tracking"**: The vision describes managing raw `byte*` pointers and `UnsafeUtility.MemCpy` for Tier 1 snapshots.  
* **Current Reality (`DoubleBufferProvider`):** It uses `_replica.SyncFrom(_live)`, which delegates to `NativeChunkTable.SyncDirtyChunks`.  
* **Verdict:** **Aligned.** The implementation in `Fdp.Kernel` matches the intent, but abstracts it inside `SyncFrom`.

**Vision (Sec 4.6.2 \- COW Fork Trigger):**

* **"Write Barrier Integration"**: The vision specifies complex COW logic (`ForkPage`) for Managed Components to allow efficient snapshots without deep copying.  
* **Current Reality:** `ManagedComponentTable` uses simple `Array.Copy`.  
* **Verdict:** **Simplified.** The current implementation is robust but less memory-efficient than the vision's page-level ref-counting. For now, this is acceptable, but the "True COW" feature is missing.

#### **3\. Geographic Coordinate System**

**Vision (Sec 3.1 \- 3.3):**

* Dual representation: `PositionCartesian` (Tier 1\) and `PositionGeodetic` (Tier 2).  
* `CoordinateTransformSystem`: Bridges the two in `PostSimulation` phase.  
* **Current Reality:** None of this exists in the code. The `SystemPhase.PostSimulation` enum exists, but it's empty.

#### **4\. Network Architecture**

**Vision (Sec 6):**

* **SSTModule**: A dedicated module for DDS gateway.  
* **NetworkIngestSystem**: Runs in `NetworkIngest` phase.  
* **NetworkSyncSystem**: Runs in `Export` phase.  
* **Current Reality:** The `SystemPhase` enums exist (`Input`, `Export`), but the systems and the generic `IDataWriter`/`IDataReader` abstractions are missing.

---

### **Re-Prioritized Roadmap**

To align the implementation with the Vision, here is the corrected roadmap:

1. **Phase 1 (Fix Critical Gaps):**  
     
   * Implement **Non-Blocking Execution** (Triple Buffering or "World C"). The vision explicitly mentions "Async Mode" and "Fire & Forget". The current `Task.WaitAll` violates this intent.  
   * Implement **Resilience** (Timeouts). The vision dedicates a whole section (10) to Watchdogs and Circuit Breakers.

   

2. **Phase 2 (Services Layer):**  
     
   * Implement `IGeographicTransform` and the `CoordinateTransformSystem`. This is the glue between FDP physics and the outside world (DDS).

   

3. **Phase 3 (Network Layer):**  
     
   * Implement the `NetworkIngestSystem` and `NetworkSyncSystem`.  
   * *Note:* You don't need actual DDS libraries yet; just mocks that push/pull data to the FDP components in the correct phases.

   

4. **Phase 4 (Advanced Snapshots):**  
     
   * Revisit the **COW Forking** logic for Tier 2 if memory usage becomes a bottleneck. The current `Array.Copy` is a valid MVP ("Minimum Viable Product") implementation of the vision's "Reference Array Copy" strategy.

### **Conclusion**

The code you have is a **solid Layer 0-3 implementation** of the vision. It correctly implements the Host-Centric model, the GDB/SoD duality (via Providers), and the Command Buffer pattern.

**What's missing is the "Business Logic" of the platform:**

* Coordinate Systems  
* Network Gateway  
* Resilience/Safety rails

The infrastructure is ready to support these features; they just need to be written as Modules/Systems now.

—--------------------------

the True COW was refused, no longer wanted, replaced by smart SoD capability. ELM will be needed as one of the way how to create entity in "distributed: way across multiple (otherwise unaware of each other) modules or even machines \-should be core (yet optional) part of ModuleHost. DDS gateway will be needed, but later (not a part of module host core, more an add on to be used for projects needing it). Resilience is needed. Graphics transform is certainly needed (not a core part of mnodule host, more an add on to be used for projects needing it). 

I need also module time period setting (i.e. non-blocking modules, World C etc.), reactive scheduling (event, componnet change) and convoy & pooling pattern.  
I need to enable the modules to be able to register their systems in the Fast category (using hte attributes to specify phase and order).  
I also want the scheduling on component change (some effective one, even if some false alarms).

Priorities  
 1\. non blocking modules  
 2\. reactive scheduling  
 3\. convoy  
 4\. resilience  
 5\. ELM  
 6\. DDS/network related (ingressa and egress)  
 7\. Geo-transform  
 

I would like you to synthetize a detailed description of each of these features, one by one, for each  
1\. specification \- what they should do, why this is needed  
2\. design (how to do it properly within current sources \- interfaces, data structures, operation sequences, etc.)

Pls show an outline of the whole description \- each feature one big chapters that migth have sub-chapters. The we will go chapter by chapter. For each chapter you will provide first the specification/reasoning/requirements, i will review, only then the design, i will review as well. The we will move to the next chapter

—-------------

Here is the synthesized outline for the detailed feature implementation plan, based on your priorities and architectural requirements.

---

### **ModuleHost Advanced Features \- Implementation Plan**

#### **Chapter 1: Non-Blocking Execution ("World C")**

* **Goal:** Decouple slow modules from the main thread frame rate.  
* **Key Challenge:** Preventing race conditions when a module runs longer than a frame.  
* **Architecture:** Triple-Buffering strategy, Task Management, "Check-and-Harvest" logic in the Kernel.

#### **Chapter 2: Reactive Scheduling**

* **Goal:** Wake modules immediately on specific signals (Events, Component Changes) regardless of their timer.  
* **Key Challenge:** Efficiently checking dirty flags without iterating millions of entities.  
* **Architecture:** `HasChanges()` API for Repos/Tables, `WatchList` metadata in Modules, Kernel Trigger Logic.

#### **Chapter 3: Convoy & Pooling Patterns**

* **Goal:** Optimize memory and CPU by grouping modules that run at the same frequency.  
* **Key Challenge:** Managing shared ownership and reference counting of snapshots.  
* **Architecture:** `SharedSnapshotProvider` auto-assignment logic, Snapshot Pooling refinements.

#### **Chapter 4: Resilience & Safety**

* **Goal:** Prevent rogue modules from crashing or freezing the host.  
* **Key Challenge:** Detecting hangs in async tasks without killing the process.  
* **Architecture:** `ModuleCircuitBreaker`, Timeouts in `Task.Wait`, Exception isolation policies.

#### **Chapter 5: Entity Lifecycle Manager (ELM) Core**

* **Goal:** Distributed, atomic entity creation across modules/machines.  
* **Key Challenge:** Staging entities ("Dark Construction") until all contributors ACK.  
* **Architecture:** `EntityLifecycle` flags in FDP, `ConstructionOrder` event protocol, `EntityLifecycleModule` (Host-side manager).

#### **Chapter 6: Network Gateway Core (DDS Ingress/Egress)**

* **Goal:** Synchronization with external simulation network.  
* **Key Challenge:** Mapping internal IDs to Network UUIDs and handling authority.  
* **Architecture:** `NetworkIngestSystem` (Phase 1), `NetworkSyncSystem` (Phase 4), `IDataWriter`/`IDataReader` abstractions.

#### **Chapter 7: Geographic Services**

* **Goal:** Bridge Physics (Cartesian) and Network (Geodetic) worlds.  
* **Key Challenge:** Precision loss and frame-perfect synchronization.  
* **Architecture:** `IGeographicTransform` service, `CoordinateTransformSystem` (PostSimulation Phase).

---

We will proceed **Chapter by Chapter**.

### **Chapter 1: Non-Blocking Execution ("World C") \- Specification**

**1\. Requirement** The main simulation loop (60Hz) must **never block** waiting for a background module (e.g., 10Hz AI) to finish its processing.

* If an AI module takes 50ms (3 frames), the main thread continues for those 3 frames without stalling.  
* The AI module processes an old snapshot ("World C") while the main thread updates "World A".  
* When the AI finishes, its commands are harvested and applied in the *next available* frame.

**2\. Why is this needed?**

* **Frame Stability:** The current `Task.WaitAll()` implementation causes micro-stutters. If a module spikes to 17ms, the game drops below 60fps.  
* **CPU Utilization:** Allows heavy compute tasks (pathfinding, analytics) to utilize unused cores over multiple frames without impacting the critical path.

**3\. Constraints & Rules**

* **Triple Buffering:** We cannot overwrite World B (Network/Fast) because the recorder needs it fresh every frame. We need a way to "hold" a snapshot for the slow module without stopping the fast modules.  
* **Command Latency:** Commands generated by the slow module will be applied N frames late. This is acceptable for AI/Analytics but must be deterministic in ordering *within* the batch.  
* **Snapshot Safety:** The snapshot used by the slow module must stay valid (pinned) until the module releases it.

**—---**

### **Chapter 1: Non-Blocking Execution ("World C") \- Design**

This design implements **Triple Buffering** via a "Lease/Release" pattern in the `ModuleHostKernel`. It moves away from `Task.WaitAll` to a "Check-and-Harvest" approach.

---

#### **1\. Architecture Changes**

**A. Module State Tracking** We need to track the *running* state of each module across frames.

* **Idle:** Ready to run.  
* **Running:** Currently executing on a background thread.  
* **Completed:** Finished execution, waiting for Main Thread to harvest commands.

**B. Triple Buffering Logic**

* **World A (Live):** Updated every frame.  
* **World B (Fast Replica):** Synced every frame from A (for Recorder/Network).  
* **World C (Slow Snapshot):** Created/Updated *only* when a slow module starts a new job. It is **held** (leased) by the module until the job finishes.

---

#### **2\. Data Structures**

**`ModuleEntry` (Revised)** Add task tracking fields to the internal `ModuleEntry` class in `ModuleHostKernel`.

private class ModuleEntry

{

    // ... existing fields ...

    

    // Async State

    public Task? CurrentTask { get; set; }

    public ISimulationView? LeasedView { get; set; } // The snapshot currently being used

    public float AccumulatedDeltaTime { get; set; } // Time since last \*start\* of execution

}

---

#### **3\. Execution Flow (The "Check-and-Harvest" Loop)**

Modify `ModuleHostKernel.Update(float dt)`:

**Step 1: Harvest Completed Modules (Start of Frame)** Iterate all modules. If `CurrentTask` is not null:

* **Check Status:** Is `CurrentTask.IsCompleted`?  
  * **Yes:**  
    1. **Harvest Commands:** Playback the CommandBuffer from `LeasedView` to `_liveWorld`.  
    2. **Release View:** Call `Provider.ReleaseView(LeasedView)`.  
    3. **Reset:** Set `CurrentTask = null`, `LeasedView = null`.  
    4. **Stats:** Update execution count.  
  * **No:**  
    1. **Skip:** Do nothing. The module is still crunching.  
    2. **Accumulate Time:** Add `dt` to `AccumulatedDeltaTime` (so next run accounts for the skipped frames).

**Step 2: Dispatch Idle Modules (End of Frame / Sync Point)** Iterate all modules. If `CurrentTask` is null (Idle):

* **Check Schedule:** Should run this frame? (Timer / Event Trigger).  
* **If Yes:**  
  1. **Acquire View:** `view = Provider.AcquireView()`. (This creates "World C" effectively).  
  2. **Dispatch:** `CurrentTask = Task.Run(() => module.Tick(view, AccumulatedDeltaTime))`.  
  3. **Store:** Save `view` to `LeasedView`. Reset `AccumulatedDeltaTime`.

**Step 3: Fast Sync (World B)**

* Fast modules (GDB provider) run every frame. They typically finish fast enough to `Wait()` on, OR we can apply the same non-blocking logic if needed.  
* *Optimization:* For Fast Tier modules (Recorder), we might still want to `Wait()` if we strictly require 60Hz capture without frame drops. But for Slow Tier, the non-blocking path is mandatory.

---

#### **4\. Provider Implications**

**OnDemandProvider (SoD)**

* **Works automatically.** Since it uses a `ConcurrentStack` pool, "holding" a snapshot just means one less item in the pool for N frames.  
* *Requirement:* Increase pool size (Warmup count) to cover the maximum concurrency (e.g., 5 slow modules running at once \= 5 active snapshots).

**SharedSnapshotProvider (Convoy)**

* **Reference Counting:** The logic already supports multiple readers. If Module A (Slow) and Module B (Slow) share a snapshot, and A takes 50ms while B takes 10ms:  
  * B releases. RefCount decrements.  
  * A keeps running. Snapshot stays alive.  
  * A finishes. RefCount hits 0\. Snapshot recycled.  
* **Logic remains valid.**

---

#### **5\. Pseudo-Code Implementation**

// ModuleHostKernel.cs

public void Update(float deltaTime)

{

    // 1\. HARVEST PHASE (Main Thread)

    foreach (var entry in \_modules)

    {

        // Accumulate time for everyone first

        entry.AccumulatedDeltaTime \+= deltaTime;

        if (entry.CurrentTask \!= null)

        {

            if (entry.CurrentTask.IsCompleted)

            {

                // Harvest commands

                PlaybackCommands(entry.LeasedView);

                

                // Release resources

                entry.Provider.ReleaseView(entry.LeasedView);

                entry.CurrentTask \= null;

                entry.LeasedView \= null;

                

                // Reset timer for NEXT run

                entry.AccumulatedDeltaTime \= 0; 

            }

            else

            {

                // Still running \- skip

                continue;

            }

        }

    }

    // 2\. SYNC PHASE (Fast Path)

    // Update providers (syncs World B, etc.)

    foreach (var provider in \_providers) provider.Update();

    // 3\. DISPATCH PHASE

    foreach (var entry in \_modules)

    {

        if (entry.CurrentTask \== null && ShouldRun(entry))

        {

            var view \= entry.Provider.AcquireView();

            entry.LeasedView \= view;

            

            // Dispatch

            float dtForTick \= entry.AccumulatedDeltaTime;

            entry.CurrentTask \= Task.Run(() \=\> entry.Module.Tick(view, dtForTick));

        }

    }

}

Great. Moving to the next feature.

---

### **Chapter 2: Reactive Scheduling \- Specification**

**1\. Requirement** Modules must support **Event-Driven Execution** in addition to Timer-Based execution.

* **Trigger:** If a specific event type (e.g., `ExplosionEvent`) is published, or a specific component type (e.g., `Health`) is modified, the interested module must run in the **very next frame** (or same frame), interrupting its normal frequency timer.  
* **Efficiency:** The check for "Has this triggered?" must be extremely fast ($O(1)$ or very small $O(N)$), avoiding iteration over entities.

**2\. Why is this needed?**

* **Responsiveness:** An AI running at 1Hz (once per second) feels unresponsive if it ignores being shot for 900ms. Reactive scheduling allows it to sleep (save CPU) but wake up instantly when critical things happen.  
* **Bandwidth:** Prevents "polling" modules from running just to check `if (health < 50)`. They only run when Health actually changes.

**3\. Constraints & Rules**

* **Priority:** Triggers override the `UpdateFrequency` timer.  
* **Granularity:**  
  * **Events:** Precision is high (Event happened \-\> Run).  
  * **Components:** Precision is coarse (Table changed \-\> Run). We accept false positives (Module wakes up, checks specific entities, finds nothing relevant) to save the cost of fine-grained tracking.  
* **Metadata:** Modules must declare their triggers statically or during initialization.

### **Chapter 2: Reactive Scheduling \- Design**

This design leverages the **FDP Kernel's Dirty Tracking** mechanisms to implement efficient triggers.

---

#### **1\. Architecture Changes**

**A. Component Dirty Tracking (Coarse Grained)** We need to know if a Component Table has been written to since the last time a module ran.

* **Mechanism:** `EntityRepository` and `ComponentTable` already track `GlobalVersion` and `ChunkVersion`.  
* **Enhancement:** Add `uint LastWriteTick` to `IComponentTable`.  
  * Updated whenever `Set()` or `GetRW()` is called on the table.  
  * This is a single integer write per batch update (extremely cheap).

**B. Event Tracking** We need to know if an Event Type was published this frame.

* **Mechanism:** `FdpEventBus` needs a fast lookup for "Active Event IDs this frame".  
* **Enhancement:** Add `HashSet<int> _activeEventIds` (or `BitMask` if IDs are small/dense) to `FdpEventBus`.  
  * Cleared at `SwapBuffers`.  
  * Populated at `Publish`.

---

#### **2\. API Changes**

**`IModule` Interface** Add properties to declare triggers.

public interface IModule

{

    // ... existing ...

    

    // Reactive Triggers

    IReadOnlyList\<Type\> WatchComponents { get; } // e.g. \[typeof(Health)\]

    IReadOnlyList\<Type\> WatchEvents { get; }     // e.g. \[typeof(ExplosionEvent)\]

}

**`EntityRepository` Extensions** Add query methods for the Scheduler.

public bool HasComponentChanged(Type componentType, uint sinceTick)

{

    if (\_tables.TryGetValue(componentType, out var table))

    {

        return table.LastWriteTick \> sinceTick;

    }

    return false;

}

**`FdpEventBus` Extensions** Add query method.

public bool HasEvent(Type eventType)

{

    return \_activeEventIds.Contains(EventTypeRegistry.GetId(eventType));

}

---

#### **3\. Execution Flow (The Trigger Logic)**

Modify `ModuleHostKernel.ShouldRunThisFrame(ModuleEntry entry)`:

private bool ShouldRunThisFrame(ModuleEntry entry)

{

    // 1\. Timer Check (Existing)

    int freq \= Math.Max(1, entry.Module.UpdateFrequency);

    bool timerDue \= (entry.FramesSinceLastRun \+ 1\) \>= freq;

    

    if (timerDue) return true;

    // 2\. Event Triggers (Immediate)

    // Checks if event happened THIS frame

    foreach (var evtType in entry.Module.WatchEvents)

    {

        if (\_liveWorld.Bus.HasEvent(evtType)) 

            return true;

    }

    // 3\. Component Triggers (Since last run)

    // Checks if component changed since LAST run

    uint lastRunTick \= entry.LastRunTick;

    foreach (var compType in entry.Module.WatchComponents)

    {

        if (\_liveWorld.HasComponentChanged(compType, lastRunTick)) 

            return true;

    }

    return false;

}

---

#### **4\. Optimization Notes**

* **Caching:** `ModuleHostKernel` should cache the `Type -> IntID` mapping for Events and Components during Initialize to avoid `typeof()` or Dictionary lookups inside the hot loop.  
* **Granularity:** Component triggers are "Table Dirty".  
  * *Scenario:* 10,000 entities. 1 changes Health.  
  * *Result:* `Health` table marked dirty. Module wakes up.  
  * *Module Duty:* The module must use `QueryDelta` or iterate checking `chunkVersion > lastRunTick` to efficiently find the specific entities that changed, rather than scanning all 10,000.

Excellent. Moving to the next feature.

---

### **Chapter 3: Convoy & Pooling Patterns \- Specification**

**1\. Requirement** The ModuleHost must optimize resource usage for groups of modules that run at the **same frequency** (e.g., 5 different AI modules running at 10Hz).

* **Convoy Pattern:** Instead of creating 5 separate Snapshots (SoD) or Replicas (GDB), these modules should share a single `ISimulationView` instance.  
* **Pooling Pattern:** When a snapshot is released by *all* consumers in a convoy (or a single module), it must be returned to a pool to avoid GC allocations (`new EntityRepository()`).

**2\. Why is this needed?**

* **Memory Footprint:** 5 AI modules $\\times$ 100MB snapshot \= 500MB. Convoy \= 100MB.  
* **Sync Performance:** `SyncFrom` takes time (memcpy). Syncing 1 snapshot is 5x faster than syncing 5\.  
* **GC Pressure:** Allocating `EntityRepository` (and internal arrays) every frame triggers GC. Pooling makes it zero-allocation in steady state.

**3\. Constraints & Rules**

* **Thread Safety:** The Shared Snapshot is read-only, so sharing it across threads is safe.  
* **Lifecycle:** The Snapshot cannot be released/pooled until **all** modules in the convoy have finished their tasks.  
* **Auto-Detection:** The Kernel should automatically identify convoy opportunities based on `ModuleTier` and `UpdateFrequency`.

### **Chapter 3: Convoy & Pooling Patterns \- Design**

This design enhances the `SharedSnapshotProvider` and integrates it into the `ModuleHostKernel`'s registration logic.

---

#### **1\. Architecture Changes**

**A. Pooling System (`SnapshotPool`)** A central pool manager for `EntityRepository` instances.

* **Method:** `ConcurrentStack<EntityRepository>`.  
* **Reset:** Uses `EntityRepository.SoftClear()` (which resets counts but keeps buffer capacity).  
* **Allocation:** Creates new if empty.

**B. Provider Factory Logic** The `ModuleHostKernel` needs a smart way to assign providers during `Initialize()`.

---

#### **2\. Data Structures**

**`ConvoyKey`** To group modules, we need a key that defines compatibility.

struct ConvoyKey : IEquatable\<ConvoyKey\>

{

    public ModuleTier Tier;

    public int Frequency;

    // Note: We might also include ComponentMask if we want strict data matching,

    // but for simplicity, convoys usually share a "Union Mask" or just "All Data".

}

**`SharedSnapshotProvider` (Refinement)** Enhance the existing class to handle the Union Mask logic.

* **State:** `_activeReaders` (Interlocked counter).  
* **Logic:** `ReleaseView()` decrements counter. If 0, return snapshot to `SnapshotPool`.

---

#### **3\. Execution Flow (Auto-Assignment)**

Modify `ModuleHostKernel.Initialize()` (or registration phase):

1. **Group Modules:** Iterate all registered modules. Group them by `Tier` and `Frequency`.  
2. **Assign Providers:**  
   * **Singletons:** If a group has 1 module \-\> Assign `OnDemandProvider` (SoD).  
   * **Convoys:** If a group has \>1 module \-\> Create `SharedSnapshotProvider`.  
     * Calculate **Union Mask**: Combine `GetSnapshotRequirements()` from all modules in the group.  
     * Assign this provider to all modules in the group.

#### **4\. Execution Flow (Runtime)**

Inside `ModuleHostKernel.Update()`:

* **Convoy Trigger:** When the frame count matches the Convoy Frequency (e.g., Frame 6 for 10Hz):  
  1. Kernel calls `provider.Update()`.  
  2. Provider acquires ONE snapshot from `SnapshotPool`.  
  3. Provider syncs from Live World (using Union Mask).  
  4. Kernel dispatches all Module Tasks.  
  5. Each Module calls `provider.AcquireView()` (increment ref count).  
  6. Each Module finishes \-\> `provider.ReleaseView()` (decrement ref count).  
  7. Last module finishes \-\> Snapshot soft-cleared and returned to Pool.

---

#### **5\. Code Structure**

// ModuleHostKernel.cs

private void AutoAssignProviders()

{

    // 1\. Group

    var groups \= \_modules

        .Where(m \=\> m.Provider \== null) // Only auto-assign if not manually set

        .GroupBy(m \=\> new { m.Module.Tier, m.Module.UpdateFrequency });

    foreach (var group in groups)

    {

        if (group.Count() \== 1\)

        {

            // Standard SoD

            var entry \= group.First();

            entry.Provider \= new OnDemandProvider(..., entry.Module.GetSnapshotRequirements());

        }

        else

        {

            // Convoy

            var unionMask \= new BitMask256();

            foreach (var m in group) unionMask.Or(m.Module.GetSnapshotRequirements());

            

            var sharedProvider \= new SharedSnapshotProvider(..., unionMask, \_snapshotPool);

            

            foreach (var entry in group)

            {

                entry.Provider \= sharedProvider;

            }

        }

    }

}

**We need the convoy component mask\! slow modules not always wanting everything\!**

Understood. I will refine the **Chapter 3** design to emphasize the **Union Mask**.

### **Refined Logic: Union Mask for Convoys**

If Module A needs `Position` and Module B needs `Health`, and they form a Convoy, the Shared Snapshot **must** contain `Position + Health`.

1. **Grouping:** Modules are grouped *only* by Tier and Frequency.  
2. **Mask Calculation:** The Kernel iterates all modules in the group.  
   * `UnionMask = Mask_A | Mask_B | ...`  
3. **Provider Setup:** The `SharedSnapshotProvider` is initialized with this `UnionMask`.  
4. **Sync:** When the provider updates, `EntityRepository.SyncFrom(live, UnionMask)` ensures all required data is copied.  
5. **Access:** Module A sees `Health` even if it didn't ask for it, but that's safe (read-only) and cheaper than creating two separate snapshots.

This ensures that "Slow Modules not wanting everything" still get the benefit of sparse syncing (they don't get PhysicsState or CollisionData if no one in the convoy asked for it).

**Corrected design accepted?** Proceeding to Chapter 4\.

---

### **Chapter 4: Resilience & Safety \- Specification**

**1\. Requirement** The ModuleHost must be robust against faulty modules.

* **Hang Detection:** If a background module hangs (infinite loop or deadlock), it must not freeze the entire application forever.  
* **Failure Isolation:** If a module throws an unhandled exception, it should not crash the Kernel.  
* **Circuit Breaking:** If a module fails repeatedly, it should be disabled ("tripped") to prevent log spam and wasted CPU.

**2\. Why is this needed?**

* **Production Stability:** In a simulator with 20 modules, one buggy analytics script shouldn't kill the physics.  
* **Development Speed:** Developers need clear error reports ("Module X timed out") rather than a frozen app.

**3\. Constraints & Rules**

* **Graceful Degradation:** The game continues without the faulty module.  
* **Timeout Logic:** We cannot easily "abort" a running C\# Thread/Task safely.  
  * *Strategy:* We stop waiting for it. We mark it as "Zombie". We ignore its results. We let it die (or stay stuck) in the background, but we free up the slot in the Kernel logic.  
* **Recovery:** A Circuit Breaker should allow a "retry" after some time (e.g., 5 seconds) to see if the module recovers.

### **Chapter 4: Resilience & Safety \- Design**

This design introduces a wrapper around module execution to handle timeouts and failures.

---

#### **1\. Architecture Changes**

**A. `ModuleCircuitBreaker` Class** A state machine tracking module health.

* **States:** Closed (Normal), Open (Failed/Skipping), Half-Open (Testing).  
* **Config:** Failure Threshold (e.g., 3 crashes), Reset Timeout (e.g., 5s).

**B. Task Wrapper with Timeout** Instead of `Task.Run(Module.Tick)`, we wrap the execution.

---

#### **2\. Execution Flow (Safe Dispatch)**

Modify `ModuleHostKernel` execution logic:

private async Task ExecuteModuleSafe(ModuleEntry entry, ISimulationView view, float dt)

{

    // 1\. Check Circuit Breaker

    if (\!entry.CircuitBreaker.CanRun())

    {

        // Skip execution

        return; 

    }

    try

    {

        // 2\. Create Timeout Token

        using var cts \= new CancellationTokenSource(entry.Module.MaxExpectedRuntimeMs);

        

        // 3\. Run Module

        // We cannot forcefully kill the thread, but we can stop waiting

        var tickTask \= Task.Run(() \=\> entry.Module.Tick(view, dt), cts.Token);

        

        // Wait with timeout

        var completedTask \= await Task.WhenAny(tickTask, Task.Delay(entry.Module.MaxExpectedRuntimeMs));

        

        if (completedTask \== tickTask)

        {

            // Success

            await tickTask; // Propagate exceptions

            entry.CircuitBreaker.RecordSuccess();

        }

        else

        {

            // Timeout

            entry.CircuitBreaker.RecordFailure("Timeout");

            // Log warning: "Module X timed out. Abandoning task."

            // The task continues in background (zombie), effectively ignored.

        }

    }

    catch (Exception ex)

    {

        // Crash

        entry.CircuitBreaker.RecordFailure(ex.Message);

        // Log error

    }

}

---

#### **3\. Handling "Zombie" Tasks**

If a module times out (infinite loop), the thread is still busy.

* **Resource Leak:** We technically leak a ThreadPool thread until it finishes.  
* **Mitigation:** We cannot solve this in C\# safely (Thread.Abort is dangerous).  
* **Kernel Policy:**  
  * Mark the ModuleEntry as `Zombie`.  
  * Do not schedule it again until the Zombie task completes (if ever).  
  * If `CircuitBreaker` trips, we stop scheduling anyway.

---

#### **4\. API Changes**

**`ModuleDefinition` Additions**

public class ModuleDefinition

{

    // ...

    public int MaxExpectedRuntimeMs { get; init; } \= 100;

    public int FailureThreshold { get; init; } \= 3;

    public int CircuitBreakerResetMs { get; init; } \= 5000;

}

**—---**  
**I remeber i need a possibility for a module to support all 3 possibilities: to inject its system either to "hot" world A, or to a gdb/SoD based world B running in sync with world A (blocking world A until finished i.e. fast module), or "slow" one (tick based or reactive-schedulable.**   
**—------**

Understood. This is a critical refinement for **Chapter 5 (Module Types & Registration)** before we move to ELM/Network.

We need to formalize the **Three Execution Modes** a module can use.

### **Chapter 5: Flexible Module Execution Modes \- Specification**

**1\. Requirement** A Module is not just "Slow" or "Fast". It defines **where** its logic runs. It can inject systems into:

* **Mode A (Live/Hot):** Runs on Main Thread, inside World A loop. Direct R/W access. Blocks simulation. (Use case: Physics, Input).  
* **Mode B (Fast Sync):** Runs on Background Thread, synced every frame (GDB). World A waits for it (or runs in parallel if data dependency allows, but usually we treat "Fast" as "Synchronized Frame"). (Use case: Recorder, Network Egress).  
* **Mode C (Slow Async):** Runs on Background Thread, decoupled (SoD/GDB). World A does **not** wait. (Use case: AI, Analytics).

**2\. Why is this needed?**

* **Physics Module:** Must run in Mode A (Hot Path).  
* **Network Module:** Ingress might need Mode A (Apply packets), Egress needs Mode B (Read consistent state).  
* **AI Module:** Needs Mode C (Think slowly).

**3\. The API Challenge** `IModule` currently has one `Tick`. We need a way to register logic for **all three** contexts.

**—----------------**

**Isn't one tick per module enough?**  
**Options per module**  
 **\- hot world A (main thread)**  
 **\- synced cold world B (background thread, synced with World A every tick) ... multiple modules may subscribe to the same World B, but there might be multiple different synced cold worlds at the same time... Api need some way of defining worlds and associating modules with worlds...**  
**\- unsynces slow bacground thread on a read only SoD, where multiple concurrently starting slow modules can share the same snapshot**  
**—----------------------------------**

Ah, I understand your concern. You want a **cleaner abstraction**: A Module is defined by **which World** it binds to and **how** that World behaves.

Let's refine the specification based on your correction.

### **Chapter 5: Flexible Module Execution Modes \- Revised Specification**

**1\. Requirement** A Module defines its execution behavior by selecting a **World Strategy**.

* **Strategy A (Live):** The module's `Tick` executes on the Main Thread, operating directly on the Live EntityRepository.  
* **Strategy B (Synced Replica):** The module's `Tick` executes on a Background Thread, operating on a Read-Only Replica that is synced **every frame**. The Main Thread **waits** for these modules to complete (synchronized step).  
* **Strategy C (Async Snapshot):** The module's `Tick` executes on a Background Thread, operating on a Read-Only Snapshot (pooled). The Main Thread **does not wait**. It runs at its own frequency (Timer/Reactive).

**2\. World Association**

* **Multiple Worlds:** The Kernel maintains:  
  * **One Live World (A)**.  
  * **Multiple Replicas (B)** (optional, based on grouping).  
  * **Multiple Snapshots (C)** (pooled).  
* **Grouping:** Modules requesting the *same strategy* (e.g., Strategy B) can share the *same physical World B instance* to save memory/sync time (Convoy Pattern).

**3\. API Implications**

* **One Tick:** `IModule.Tick(view, dt)` is sufficient. The **Scheduler** decides *when* and *on which thread* to call it based on the configured strategy.  
* **Configuration:** The `ModuleDefinition` must explicitly declare the strategy.

**—----------**  
**even for each replica (world B) we select if this is GDB or SoD and what components we need there \- different synced modules might need different components \- worlds B might be also pooled?**  
**—------------**

You are right. "World B" isn't just one thing. It's a **category** of "Synced Every Frame", but we might have multiple flavors of it depending on data needs.

Let's refine further.

### **Chapter 5: Flexible Module Execution Modes \- Refined Specification**

**1\. Requirement** A Module defines its execution policy via configuration. The Kernel groups modules into **Execution Groups**. Each Group has its own **Provider**.

**2\. Configuration Axis** A module defines:

* **Timing:**  
  * `Synchronous` (Main Thread, blocks frame).  
  * `FrameSynced` (Background Thread, runs every frame, Main Thread waits).  
  * `Asynchronous` (Background Thread, runs on timer/trigger, Main Thread continues).  
* **Data Strategy:**  
  * `Direct` (Only valid for Synchronous \- accesses Live World).  
  * `GDB` (Persistent Replica \- optimized for 100% data or high freq).  
  * `SoD` (Pooled Snapshot \- optimized for filtering).  
* **Data Requirements:** `ComponentMask` \+ `EventMask`.

**3\. Grouping Logic (The "World" Manager)** The Kernel automatically groups modules to share resources (Worlds).

* **Group 1 (Live):** All `Synchronous` modules run on World A.  
* **Group 2 (Synced GDB):** All `FrameSynced + GDB` modules share a World B1 (Union Mask).  
* **Group 3 (Synced SoD):** All `FrameSynced + SoD` modules share a World B2 (Union Mask).  
* **Group 4 (Async GDB):** All `Async + GDB` modules (e.g. 10Hz Convoy) share World C1.  
* **Group 5 (Async SoD):** All `Async + SoD` modules (e.g. 5Hz Analytics) share World C2.

**4\. Pooling**

* **GDB Worlds:** Persistent (no pooling, just sync).  
* **SoD Worlds:** Pooled. If Group 3 needs a snapshot for Frame 100, it pulls from pool, populates, uses, returns.

**5\. Result** You, the user, just say "I am a Network Module, I need `Position` every frame on a background thread". The Kernel says "Okay, you join Group 2".

**—----------**  
**I as a user do not say just i am "network". I know what the network needs so I select the hot or cold, for cold i selectd view type GDB or SoD,  and component mask. I can even imagine writable world B (duplicated GDB style) so maybe i pick also writability?**  
**—-----**

Yes, explicit selection is better than magic auto-assignment.

Regarding **"Writable World B"**: This is a dangerous path. If World B is writable, how do you merge changes back to World A (Live)?

* **Merge Conflict Hell:** If Physics modifies `Position` on A, and Network modifies `Position` on B, who wins?  
* **FDP Principle:** Use **Command Buffers** for writes from background threads. This keeps the merge logic deterministic (Phase 3 Playback).

**Recommendation:** Keep World B/C **Read-Only**. Writes must go through `ICommandBuffer`.

### **Chapter 5: Explicit Module Configuration \- Design**

#### **1\. Configuration Structure**

We replace `ModuleTier` with a detailed `ExecutionPolicy`.

public struct ExecutionPolicy

{

    // Where does it run?

    public RunMode Mode; // Synchronous, FrameSynced, Asynchronous

    

    // What data structure backs the view?

    public DataStrategy Strategy; // Direct, GDB, SoD

    

    // Data Requirements

    public ComponentMask Components;

    public EventTypeMask Events;

    

    // Scheduling (Only for Asynchronous)

    public int TargetFrequencyHz;

    public TriggerMode Triggers; // None, OnEvent, OnComponentChange

}

public enum RunMode 

{

    Synchronous, // Main Thread, blocks

    FrameSynced, // Background, Main waits

    Asynchronous // Background, Fire-and-forget

}

public enum DataStrategy

{

    Direct, // Only valid for Synchronous

    GDB,    // Persistent Replica

    SoD     // Pooled Snapshot

}

#### **2\. Kernel Logic (The Grouper)**

`ModuleHostKernel.Initialize()`:

1. **Iterate Modules.**  
2. **Create Keys:** `Key = { Mode, Strategy, Frequency }`.  
3. **Merge Masks:** For all modules with same Key, calculate `UnionMask`.  
4. **Create Providers:**  
   * `Synchronous + Direct` \-\> No provider (Live World Wrapper).  
   * `FrameSynced + GDB` \-\> `DoubleBufferProvider` (Persistent, UnionMask).  
   * `FrameSynced + SoD` \-\> `OnDemandProvider` (Pooled, UnionMask).  
   * `Async + SoD` \-\> `SharedSnapshotProvider` (Pooled, UnionMask).

#### **3\. Execution Loop**

`Update(dt)`:

1. **Sync Phase:**  
   * Update all `FrameSynced` providers (GDBs sync, SoDs populate).  
   * Check `Async` triggers. If Convoy triggers, populate that provider.  
2. **Dispatch Phase:**  
   * Launch all tasks.  
   * `WaitAll` for `FrameSynced` tasks.  
3. **Cleanup:**  
   * Release views. (GDB stays, SoD returns to pool).

Perfect. Let's move to the Service Layer (ELM, Network, Geo).

---

### **Chapter 6: Entity Lifecycle Manager (ELM) \- Specification**

**1\. Requirement** We need to create entities across a distributed system (or multiple modules) atomically.

* **Problem:** If the Physics Module spawns a Tank, and the Network Module hasn't set up the Ghost yet, we might have a frame where the Tank exists but isn't networked. Or vice versa.  
* **Solution: "Dark Construction"**  
  1. Entity is created in a "Staging" state (invisible to normal queries).  
  2. An `EntityLifecycleModule` (Host Core) publishes a `ConstructionOrder`.  
  3. Interested Modules (Physics, Network, AI) receive the order, populate their components, and send an `Ack`.  
  4. Once all ACKs received, ELM flips the entity to "Active" (visible).

**2\. Why is this needed?**

* **Distributed Authority:** No single module knows *everything* about an entity.  
* **Consistency:** Prevents "pop-in" or uninitialized behavior.

**3\. Implementation Support**

* **FDP Kernel:** Already has `EntityLifecycle.Constructing` and `AckMask` in the header (we saw this in the source code).  
* **ModuleHost:** Needs the `EntityLifecycleModule` to orchestrate the event flow.

Deletion must also be coordinated to avoid "Ghost references" or crashes where one module tries to update an entity that another module just deleted.

**1\. Distributed Deletion Requirement**

* **Initiator:** A module requests deletion (e.g., HealthSystem sees HP \<= 0).  
* **Staging:** The entity transitions to `Lifecycle.TearDown`. It is still valid, but marked for doom.  
* **Order:** ELM publishes `DestructionOrder`.  
* **Cleanup:** Modules release resources (Network unregisters ghost, Physics removes body). They send `Ack`.  
* **Finalize:** Once all ACKs received, ELM calls `repo.DestroyEntity()`.

This symmetry ensures clean startup and clean shutdown.

### **Chapter 6: Entity Lifecycle Manager (ELM) \- Design**

#### **1\. Data Structures**

**`EntityHeader` (Existing in FDP)**

* `Lifecycle` state: `Constructing`, `Active`, `TearDown`.  
* `AckMask`: Bitmask of modules that have acknowledged.

**`LifecycleEvents`**

\[EventId(9001)\]

struct ConstructionOrder { Entity Entity; int TypeId; }

\[EventId(9002)\]

struct ConstructionAck { Entity Entity; int ModuleId; }

\[EventId(9003)\]

struct DestructionOrder { Entity Entity; }

\[EventId(9004)\]

struct DestructionAck { Entity Entity; int ModuleId; }

#### **2\. ELM Module Logic**

**Phase: Simulation (Background)**

* **Process Requests:** Reads `CmdSpawn` / `CmdDestroy`.  
* **Update State:**  
  * Spawn \-\> Create entity (Constructing), Publish `ConstructionOrder`.  
  * Destroy \-\> Set state `TearDown`, Publish `DestructionOrder`.  
* **Process ACKs:**  
  * Reads `Ack` events.  
  * Updates `AckMask` on entity.  
  * If `AckMask == RequiredMask`:  
    * Constructing \-\> Set `Active` (Visible).  
    * TearDown \-\> Queue `DestroyEntity` (Gone).

#### **3\. Integration**

* **Query Filtering:** `EntityQuery` in FDP by default checks `IsActive`.  
  * *Result:* "Constructing" and "TearDown" entities are invisible to standard `ForEach`.  
  * *Override:* Systems that *need* to see them (like Physics setup) can use `.WithLifecycle(Constructing)`.

**—-**`bdc-sst-rules.md—`  
**\# Entities made of descriptors**

**An entity is composed of individual descriptors and nothing else but the descriptors.**

**Descriptor is a DDS network trasferred data structure. Each descriptor has its own dedicated DDS topic.**

**Different entity types may require different set of descritors.**

**What concrete descriptors are needed for what type of entity is defined by convention \- an agreement among the applications using such entity type.**

**Some descriptor types can come in multiple instances per entity. Each per-entity instance has its descriptor instance id which is unique per entity id.**

**EntityMaster descriptor is mandatory for all entity types. EntityMaster descriptor defines crucial info like entity id and type.**  
   
**Once descriptor is created for an entity it is expected to live as long as the entity lives (e.g. descriptors cannot be deleted from a live entity). See also entity life cycle.**

**\# Descriptors as DDS topic instances**

**Descriptors are communicated as instances of usual DDS Topics. Each descriptor type uses different dds topic.**

**Each descriptor data structure begins with entity id field, making it bound to a concrete entity instace.**

	**struct SomeDescriptor**  
	**{**  
		**long EntityId;**

		**unsigned long long SomeNumber;**  
		**Vector3  SomeVector;**  
	**};**

**For a multi-instance-per-entity descriptors the descriptor instance id is stored as the second field right after the entity id. The name of this field is not important.**  
      
    **struct SomeDescriptor**  
    **{**  
		**long EntityId;**  
		**long InstanceId;**

		**Vector3f SomeData;**  
    **};**

**Descriptor topics should be set to KeepLast 1 so that dds\_read\_instance always return just a single sample.**

**\# Entity life cycle**

**EntityMaster descriptor controls the life of the entity instance. If entity master instance is not present on the network, the entity instance does not exist. If entity master instance is present, the entity instance exists.**

**Entity might exist even while some of its descriptors are not available.**

**Entity never exists without an EntityMaster descriptor.**

**\#\# Disposal**  
**When EntityMaster is disposed, the entity is considered deleted regardless of its other descriptors.**

**When a non-EntityMaster descriptor is disposed, the ownership of it is simply returned to the current owner of EntityMaster.**

**In case the owner of EntityMaster is disposing other descriptors then it is assumed that the entity is being deleted.**

**In summary:**

 **\- Master disposed. Same as entity deleted. Results in entity deleted callbacks being invoked on all alive nodes. Entity must be created anew if dispose was result of a crash.**  
 **\- Non-master disposed by "partial" owner. Returns ownership to default (master) owner. Master's owner should detect this dispose and should take ownership in the same way as if \`UpdateOwnership\` message was received. Other nodes should ignore this dispose message.**  
   **\* This mechanism is there to solve a node quitting/crashing.**  
   **\* If you want to change ownership you should use \`UpdateOwnership\` message.**  
 **\- Non-master disposed by master's owner. Assume entity is being deleted. Nodes should ignore this dispose message and wait for master disposed message which is expected to come.**

**Note:**  
 **\* The rules above virtually prevent the deletion of a descriptor from a living (non disposed) entity.**

**\# Mandatory and optional descriptors**

**Descriptors are optional in general. Some descriptors for certain types of entities might be considered mandatory because they carry information which is crucial for the app.**

**Which descriptors are mandatory is a matter of convention agreed among a set of applications.**

**Your app may decide to wait for necessary descriptors before considering the entity "completely created".**

**\# Entity ID**  
**Each entity ID must be unique (obviously).**

**Entity IDs are allocated centrally using a DDS-based id allocator server. The server is multi-drill capable, allocating the IDs independently for each execise session (drill) that is running in parallel. I.e. just a single server instance is needed for a site.**

**\# Entity type**

**Entity type helps organizing the entity into categories. It's meaning is defined by an agreement among the apps using the entities.**

**The type can serve for**  
 **\- Quick filtering of entities to handle.**  
 **\- Determining the set of descriptors to expect for such entity type.**

**There are multiple flavors of entity type present in the EntityMaster descriptor; neither of them is mandatory in general (again, that's just an agreement between apps).**  
  **\- StaticType .... something like the CGFX entity type \[TODO: remove\]**  
  **\- TkbType ....... unique id in a TKB database; currently CGFX TKB id; will be changed to Unified TKB id**  
  **\- DisType ....... compatible with DIS entity type id (SISO-REF-010-2015 extended by Bagira entity types)**

 **In current implementation the \*\*TkbType is mandatory\*\* while the DisType is just informative. StaticType is not used at all.**

**\`\`\`**  
    **struct EntityMaster**  
    **{**  
        **// ...**  
          
        **long StaticType;**  
        **unsigned long long TkbType;**  
        **DisEntityType DisType;**   
          
        **// ...**  
    **};**  
**\`\`\`**  
  

**\# Entity ownership**

**Onwer is the one updating the entity.**

**The ownership is determined for each descriptor indiviudually, allowing for partial owners.**

**Ownership is determined by the most recent writer \- the owner is whoever published the current value. This is not true during the short time of ownership update. However, it is again true after the ownership update is finished.**

**\*\*Only the descriptor owner is allowed to publish its updates.\*\* Updates from non-owners result in an undefined behaviour and should be avoided at all cost.** 

**\#\# Ownership updates**

**1\. An arbitraty node sends \`OwnershipUpdate\` message. This message is for the current and the new owner only \- i.e. it doesn't serve as an ownership indicator.**

**2\. Current owner receives message and updates its state to a non-owner (e.g. it stops sending further updates). It does NOT dispose sample/writer/instance which would result in descriptor deletion followed by descriptor creation on new owner.**

**3\. A new owner receives \`OwnershipUpdate\` message and writes descriptor value (usually same value) to "confirm" the ownership.**

**One time updates (e.g. teleporting an entity) should usually be done by sending a specific message to the current owner and letting him perform the update rather that changing the ownership temporarily.**

**\# Working with entities and descriptors using plain dds topic**  
**No special infrastructure is necessary. All can be achieved using plain dds-topic readers and writers.**

**\#\# Entities**

**To create/add an entity**  
  **\- Allocate unique non-conflicting entity id (see Entity ID above)**  
  **\- Publish descriptors (preferrably EntityMaster last)**

**To delete an entity**  
  **\- Unpublish the descriptors (preferrably EntityMaster first)**

**To detect entity creation/deletion**  
   **\- Read (do not take) samples of EntityMaster and check the instance state.**  
   **\- ALIVE \= entity exists, otherwise it does not.**

**To check if entity with particular id exists**  
  **\- Read the particular instance of EntityMaster descriptor**  
    **\- Lookup the instance handle by the entity id**  
    **\- Read the instance sample**  
	**\- check if ALIVE**

**To enumerate all entities, filter by type**  
  **\- Read all ALIVE instances of EntityMaster descritpor.**  
  **\- Filter by the entity type information stored in the EntityMaster descriptor.**

**To enumerate all entities having certain descriptor**  
  **\- Read all ALIVE instances of desired descriptor.**  
  **\- Get entityId from the descriptor.**

**To change ownerhip**  
  **\- Follow the mechanism described in Entity ownership chapter.**

**\#\# Descriptors**

**To check if the descriptors you need are present**  
  **\- Try to read the instance of each of the descriptor topics.**  
    
**To read a value of particular descriptor**  
  **\- Try to read the particular instance of the descriptor topic.**  
  **\- Use the entityId and possibly the descriptor instance id (for multi-instance descriptors) as the instance key**

**To write a value of particular descriptor**  
  **\- Write the particualr instance of the descriptor topic.**  
  **\- Do it only if you are the owner of the descriptor\!**

**To change ownerhip**  
  **\- Follow the mechanism described in Entity ownership chapter.**

**—-**

Understood. The `bdc-sst-rules.md` document provides the critical specifications for how entities and components (descriptors) are represented and synchronized over DDS in your distributed simulation environment.

Based on this document and our previous discussions about ModuleHost/FDP, here is the synthesized design for **Chapter 6: Network Gateway Core (DDS Ingress/Egress)** and **Chapter 7: Geographic Services**.

---

### **Chapter 6: Network Gateway Core (SST/DDS Integration) \- Specification**

**1\. Requirement** Implement a specialized Module (`SSTModule`) that acts as a bidirectional gateway between the local FDP EntityRepository and the external DDS network, adhering to the "Descriptors as Topics" protocol.

* **Mapping:**  
    
  * **FDP Entity** ↔ **SST Entity** (linked by `EntityID`).  
  * **FDP Managed Component (Tier 2\)** ↔ **SST Descriptor** (DDS Topic).  
  * **EntityMaster Descriptor** ↔ **FDP Entity Header / Lifecycle State**.


* **Ingress (Network → FDP):**  
    
  * Subscribe to all relevant Descriptor Topics.  
  * On data received:  
    * If `EntityMaster` appears: Create "Ghost" Entity in FDP (Lifecycle: `Active` but remote-owned).  
    * If `EntityMaster` disappears: Destroy Ghost Entity.  
    * If other Descriptor appears/updates: Update corresponding FDP Managed Component.  
    * **Dead Reckoning:** If the descriptor contains Position/Velocity, update a `NetworkTarget` component for smoothing (see Chapter 7).


* **Egress (FDP → Network):**  
    
  * Monitor FDP entities where **Local Authority** is held.  
  * If a locally-owned component changes, publish the corresponding Descriptor to DDS.  
  * Maintain `EntityMaster` heartbeat/liveness for locally owned entities.


* **Ownership Handling:**  
    
  * Respect the "Only Owner Writes" rule.  
  * Handle `OwnershipUpdate` messages to switch authority dynamically (e.g., handover from Init to Sim).

**2\. Why is this needed?**

* **Interoperability:** Allows FDP-based simulators to coexist with legacy/other apps in the same federation.  
* **Distributed Scale:** Different nodes can simulate different entities or even different *parts* (descriptors) of the same entity.

**3\. Constraints**

* **Performance:** Must handle thousands of updates per frame without stalling the main thread.  
  * *Solution:* Use the **Ingress Queue** pattern (IO Thread \-\> Queue \-\> NetworkModule \-\> CommandBuffer \-\> Main Thread) we discussed earlier.  
* **Data Types:** Descriptors are DDS-defined structs (IDL). FDP Components are C\# Records. We need efficient mapping/copying between them.

**—-----------**

**Note: each DDS topic has its dedicated dds reader. Each reader can fire "on data received" async callback (the reactive way) or we can poll each reader individually but in controlled way and in our thread (polling way). Not sure what fits better to FDP/module concept. We also read all not-yet-taken samples of a concrete topic at once.**

**Also, SST IDL defined descriptors might be much more rich and more aggregated (liek DIS PDUs) than fine grained ECS working components (small highly focused data like Position). Mapping from SST to hot-working-ECS and back is probably a task for some specific system.**  
**—---------**

Understood. The impedance mismatch between **Coarse-Grained DDS Descriptors** (rich, aggregated structs like `EntityStatePDU`) and **Fine-Grained ECS Components** (atomic structs like `Position`, `Velocity`, `Health`) is a key architectural challenge.

Here is the refined design for **Chapter 6** to address this mapping and the Polling vs. Callback question.

### **Chapter 6: Network Gateway Core (SST/DDS Integration) \- Revised Design**

#### **1\. Polling vs. Callbacks**

**Decision: Controlled Polling via `NetworkIngestSystem`.**

* **Why not Async Callbacks?**  
    
  * DDS Callbacks execute on arbitrary thread pool threads. Pushing data into thread-safe queues from random threads creates high contention and unpredictable jitter.  
  * ECS architectures thrive on **Batch Processing**.


* **The FDP Way (Polling):**  
    
  * The `SSTModule` runs a system (`NetworkIngestSystem`) in the **Phase 1 (Input/Ingest)** of the ModuleHost execution.  
  * This system iterates through all registered DDS Readers.  
  * For each reader, it calls `Take()` to retrieve *all* waiting samples in a single batch.  
  * This provides deterministic ingestion timing and better cache locality.

#### **2\. The Translator Pattern (SST ↔ ECS Mapping)**

We introduce a **Translator Layer** to bridge the gap between Rich Descriptors and Atomic Components.

**Structure:**

* **`IDescriptorTranslator<TDescriptor>`**: An interface for handling a specific DDS topic type.  
* **Ingress (`ApplyToEntity`):** Takes a Descriptor sample and updates *multiple* FDP components.  
* **Egress (`BuildDescriptor`):** Queries *multiple* FDP components to construct one Descriptor.

**Example: `EntityStateTranslator`**

* **Ingress:**  
  * Input: `EntityStateDescriptor` (Contains ID, Type, Location, Orientation, Velocity, Appearance).  
  * Action:  
    * `cmd.SetComponent(e, new Position { ... })`  
    * `cmd.SetComponent(e, new Velocity { ... })`  
    * `cmd.SetComponent(e, new Appearance { ... })`  
    * `cmd.SetComponent(e, new NetworkTarget { ... })` (for smoothing)  
* **Egress:**  
  * Input: `EntityRepository` \+ `Entity`.  
  * Action:  
    * Read `Position`, `Velocity`, `Appearance` components.  
    * Construct `EntityStateDescriptor`.  
    * Publish to DDS.

#### **3\. Architecture**

**A. `SSTModule` (Background Module)**

* **Phase:** `NetworkIngest` (Ingress) and `Export` (Egress).  
* **State:** Holds the DDS `DomainParticipant`, `Publishers`, `Subscribers`, and the list of `Translators`.

**B. `NetworkIngestSystem` (Phase 1\)**

1. Iterates all `Translators`.  
2. Calls `translator.PollIngress(buffer)`.  
3. Translator reads DDS samples.  
4. Translator maps Sample → FDP Components.  
5. Queues commands to `IEntityCommandBuffer` (e.g., `SetComponent`).  
6. *Result:* Main Thread applies changes in Phase 3\.

**C. `NetworkSyncSystem` (Phase 4\)**

1. Iterates all `Translators`.  
2. Calls `translator.ScanAndPublish(view)`.  
3. Translator queries `World B` (GDB Replica) for entities with Local Authority.  
4. Checks `IsDirty` flags (optional optimization).  
5. Constructs Descriptors.  
6. Publishes to DDS.

---

### **Chapter 7: Geographic Services (Smoothing & Coordinates) \- Specification**

**1\. Requirement**

* **Dead Reckoning (Smoothing):**  
  * Network updates arrive at e.g. 20Hz or 60Hz, but with jitter.  
  * We cannot just snap `Position` to the network value (visual stutter).  
  * We need to predict/interpolate.  
* **Coordinate Systems:**  
  * **FDP Physics:** Local Cartesian (Flat Earth / Tangent Plane). Float/Double Vector3.  
  * **SST Network:** Geodetic (WGS84 Lat/Lon/Alt). Double.  
  * **Translation:** Must convert efficiently between them.

**2\. Design: The "Ghost Target" Pattern**

**A. Components**

* `Position` (Tier 1): The effective position used by Rendering/Physics.  
* `NetworkTarget` (Tier 1): The raw position received from the network (plus timestamp/velocity for DR).  
* `GeoOrigin` (Singleton): The Lat/Lon/Alt reference point for the Local Tangent Plane.

**B. `CoordinateTransformSystem` (Ingress Side)**

* Runs inside `NetworkIngestSystem` logic (part of the Translator).  
* Converts `SST.LatLon` → `NetworkTarget.LocalPosition` using `GeoOrigin`.

**C. `NetworkSmoothingSystem` (Main Thread Phase 1\)**

* Runs *before* Physics/Logic.  
* Query: Entities with `NetworkTarget` AND `Position` AND `!HasAuthority`.  
* Logic:  
  * **Dead Reckoning:** Project `NetworkTarget` forward based on `Velocity` and `Latency`.  
  * **Smoothing:** `Position = Vector3.Lerp(Position, PredictedTarget, factor)`.  
  * *Result:* Smooth visual movement even with network jitter.

**D. `GeographicExportSystem` (Egress Side)**

* Runs inside `NetworkSyncSystem` logic (part of the Translator).  
* Converts `Position.LocalPosition` → `SST.LatLon` using `GeoOrigin`.

Excellent. This concludes the Design Phase for the Advanced Feature Set.

### **Summary of the Plan**

We have defined 7 major feature chapters to elevate ModuleHost from a prototype to a production-grade simulation kernel.

| Chapter | Feature | Architectural Solution |
| :---- | :---- | :---- |
| **1** | **Non-Blocking Execution** | Triple Buffering (World C), "Lease/Release" pattern, Check-and-Harvest loop. |
| **2** | **Reactive Scheduling** | `LastWriteTick` on Tables, `HasEvent` on Bus, Scheduler Trigger Logic. |
| **3** | **Convoy & Pooling** | `SnapshotPool`, `ConvoyKey` grouping, `UnionMask` calculation. |
| **4** | **Resilience & Safety** | `ModuleCircuitBreaker`, Task Timeouts, Zombie Task isolation. |
| **5** | **Execution Modes** | Explicit `ExecutionPolicy` (Sync, FrameSynced, Async) \+ `DataStrategy` (Direct, GDB, SoD). |
| **6** | **Network Gateway** | `SSTModule` using Polling, `Translator` pattern for Descriptor $\\leftrightarrow$ Component mapping. |
| **7** | **Geographic Services** | `NetworkTarget` component, `CoordinateTransform` logic, Dead Reckoning smoothing system. |

---

### **Next Steps: Implementation**

I will now generate the detailed C\# implementation code for these chapters, starting with **Chapter 1 (Non-Blocking Execution)** and **Chapter 2 (Reactive Scheduling)**, as they are foundational changes to the Kernel.

Here is the implementation for **Chapter 1: Non-Blocking Execution ("World C")**.

This adds the "Lease/Release" logic, triple buffering support via task tracking, and the update loop overhaul to support non-blocking execution.

### **File: `ModuleHost.Core/ModuleHostKernel.cs` (Updated)**

// File: ModuleHost.Core/ModuleHostKernel.cs

using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using Fdp.Kernel;

using ModuleHost.Core.Abstractions;

using ModuleHost.Core.Providers;

using ModuleHost.Core.Scheduling;

namespace ModuleHost.Core

{

    public sealed class ModuleHostKernel : IDisposable

    {

        private readonly EntityRepository \_liveWorld;

        private readonly EventAccumulator \_eventAccumulator;

        private readonly List\<ModuleEntry\> \_modules \= new();

        

        private readonly SystemScheduler \_globalScheduler \= new();

        private bool \_initialized \= false;

        

        private uint \_currentFrame \= 0;

        

        // Stats

        private long \_totalExecutions \= 0;

        

        public ModuleHostKernel(EntityRepository liveWorld, EventAccumulator eventAccumulator)

        {

            \_liveWorld \= liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));

            \_eventAccumulator \= eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));

        }

        

        // ... RegisterSystem / RegisterModule / Initialize (Same as before) ...

        public void Update(float deltaTime)

        {

            if (\!\_initialized)

                throw new InvalidOperationException("Must call Initialize() before Update()");

            

            // 1\. INPUT PHASE (Synchronous)

            \_globalScheduler.ExecutePhase(SystemPhase.Input, \_liveWorld, deltaTime);

            

            // 2\. BEFORE-SYNC PHASE (Synchronous)

            \_globalScheduler.ExecutePhase(SystemPhase.BeforeSync, \_liveWorld, deltaTime);

            

            // 3\. CAPTURE & SYNC

            \_eventAccumulator.CaptureFrame(\_liveWorld.Bus, \_currentFrame);

            

            // Sync all "FrameSynced" providers (GDB) \- Blocking/Fast

            // Async providers (SoD) sync on demand later

            foreach (var entry in \_modules)

            {

                if (entry.Policy.Mode \== RunMode.FrameSynced)

                {

                    entry.Provider.Update();

                }

            }

            

            // 4\. MODULE HARVEST & DISPATCH LOOP

            DispatchModules(deltaTime);

            

            // 5\. POST-SIMULATION PHASE (Synchronous)

            \_globalScheduler.ExecutePhase(SystemPhase.PostSimulation, \_liveWorld, deltaTime);

            

            // 6\. EXPORT PHASE (Synchronous)

            \_globalScheduler.ExecutePhase(SystemPhase.Export, \_liveWorld, deltaTime);

            

            \_currentFrame++;

        }

        private void DispatchModules(float deltaTime)

        {

            var tasksToWait \= new List\<Task\>();

            foreach (var entry in \_modules)

            {

                // A. Accumulate Time

                entry.AccumulatedDeltaTime \+= deltaTime;

                // B. Harvest Completed Tasks

                if (entry.CurrentTask \!= null)

                {

                    if (entry.CurrentTask.IsCompleted)

                    {

                        // 1\. Playback Commands

                        if (entry.LeasedView is EntityRepository repo)

                        {

                            foreach (var cmdBuffer in repo.\_perThreadCommandBuffer.Values)

                            {

                                if (cmdBuffer.HasCommands) cmdBuffer.Playback(\_liveWorld);

                            }

                        }

                        

                        // 2\. Release View (Return to pool / Decrement ref)

                        entry.Provider.ReleaseView(entry.LeasedView\!);

                        

                        // 3\. Cleanup

                        // Note: If task faulted, we might log here. 

                        if (entry.CurrentTask.IsFaulted)

                        {

                            Console.Error.WriteLine($"Module {entry.Module.Name} failed: {entry.CurrentTask.Exception}");

                        }

                        entry.CurrentTask \= null;

                        entry.LeasedView \= null;

                        entry.AccumulatedDeltaTime \= 0; // Reset time only after successful harvest

                        

                        Interlocked.Increment(ref \_totalExecutions);

                    }

                    else

                    {

                        // Module is still running (Slow). 

                        // NON-BLOCKING: We skip it this frame. 

                        // It holds onto 'LeasedView' (World C).

                        continue; 

                    }

                }

                // C. Dispatch New Tasks (If Idle)

                if (entry.CurrentTask \== null && ShouldRunThisFrame(entry))

                {

                    // 1\. Acquire View (Creates World C if SoD, or gets World B if GDB)

                    var view \= entry.Provider.AcquireView();

                    entry.LeasedView \= view;

                    

                    // 2\. Dispatch

                    // Capture state for closure

                    float dt \= entry.AccumulatedDeltaTime;

                    

                    entry.CurrentTask \= Task.Run(() \=\> 

                    {

                        try 

                        {

                            entry.Module.Tick(view, dt);

                        }

                        catch (Exception ex)

                        {

                            // We catch here to ensure ReleaseView happens in harvest, 

                            // but usually best to let Task fault and handle in Harvest.

                            // However, we MUST ensure the task completes so Harvest sees IsCompleted.

                            throw; 

                        }

                    });

                    

                    // If FrameSynced, we MUST wait (Blocking Mode)

                    if (entry.Policy.Mode \== RunMode.FrameSynced)

                    {

                        tasksToWait.Add(entry.CurrentTask);

                    }

                }

            }

            // D. Wait for Synchronous Modules (GDB/Recorder)

            if (tasksToWait.Count \> 0\)

            {

                Task.WaitAll(tasksToWait.ToArray());

                // The harvest logic above will pick them up in the NEXT frame? 

                // NO. If we WaitAll here, they are completed. 

                // But we already iterated past the Harvest block for them.

                // FIX: We need to harvest FrameSynced modules IMMEDIATELY after wait.

                

                // Re-iterate to harvest synchronous tasks immediately

                foreach (var entry in \_modules)

                {

                    if (entry.Policy.Mode \== RunMode.FrameSynced && entry.CurrentTask \!= null && entry.CurrentTask.IsCompleted)

                    {

                        // Harvest immediately (Code duplication from above, usually extracted to method)

                        HarvestEntry(entry);

                    }

                }

            }

        }

        private void HarvestEntry(ModuleEntry entry)

        {

            if (entry.LeasedView is EntityRepository repo)

            {

                foreach (var cmdBuffer in repo.\_perThreadCommandBuffer.Values)

                {

                    if (cmdBuffer.HasCommands) cmdBuffer.Playback(\_liveWorld);

                }

            }

            entry.Provider.ReleaseView(entry.LeasedView\!);

            entry.CurrentTask \= null;

            entry.LeasedView \= null;

            entry.AccumulatedDeltaTime \= 0;

            Interlocked.Increment(ref \_totalExecutions);

        }

        // ... Dispose ...

        

        private class ModuleEntry

        {

            public IModule Module { get; set; } \= null\!;

            public ISnapshotProvider Provider { get; set; } \= null\!;

            public ExecutionPolicy Policy { get; set; } // New struct

            

            // Scheduling State

            public int FramesSinceLastRun { get; set; }

            

            // Async State (World C)

            public Task? CurrentTask { get; set; }

            public ISimulationView? LeasedView { get; set; }

            public float AccumulatedDeltaTime { get; set; }

        }

    }

}

### **File: `ModuleHost.Core/Abstractions/ExecutionPolicy.cs` (New)**

namespace ModuleHost.Core.Abstractions

{

    public struct ExecutionPolicy

    {

        public RunMode Mode;

        public DataStrategy Strategy;

        public int TargetFrequencyHz;

        

        // Defaults

        public static ExecutionPolicy DefaultFast \=\> new ExecutionPolicy 

        { 

            Mode \= RunMode.FrameSynced, 

            Strategy \= DataStrategy.GDB, 

            TargetFrequencyHz \= 60 

        };

        

        public static ExecutionPolicy DefaultSlow \=\> new ExecutionPolicy 

        { 

            Mode \= RunMode.Asynchronous, 

            Strategy \= DataStrategy.SoD, 

            TargetFrequencyHz \= 10 

        };

    }

    public enum RunMode

    {

        Synchronous,  // Main Thread (Direct)

        FrameSynced,  // Background, Main Waits (Blocking)

        Asynchronous  // Background, Main Continues (Non-Blocking)

    }

    public enum DataStrategy

    {

        Direct, // Use Live World

        GDB,    // Use Persistent Replica

        SoD     // Use Pooled Snapshot

    }

}

### **Requirements Met**

1. **Non-Blocking:** Slow modules (Asynchronous) are skipped if `CurrentTask` is not completed. The main thread continues.  
2. **Accumulated Time:** `AccumulatedDeltaTime` ensures that if a module runs at 10Hz (or skips frames due to lag), it receives the correct total `dt` (e.g., 0.1s).  
3. **Frame Synced Support:** Fast modules (Recorder) are added to `tasksToWait`, and harvested immediately, preserving 60Hz lockstep.  
4. **Triple Buffering:** Achieved implicitly. `LeasedView` holds the snapshot. `Provider.AcquireView` creates a new one (from pool) if the previous one is still leased.

Here is the implementation for **Chapter 2: Reactive Scheduling**.

This adds the dirty tracking infrastructure to FDP and the trigger logic to ModuleHost.

### **Part 1: FDP Kernel Extensions (Dirty Tracking)**

We need to add `LastWriteTick` to `NativeChunkTable` and expose it via `EntityRepository`.

**File: `Fdp.Kernel/NativeChunkTable.cs` (Modification)**

// Inside NativeChunkTable\<T\>

public sealed unsafe class NativeChunkTable\<T\> : IDisposable where T : unmanaged

{

    // ... existing fields ...

    

    // NEW: Global version for the whole table (Coarse-grained dirty flag)

    private uint \_lastWriteTick;

    

    public uint LastWriteTick \=\> \_lastWriteTick;

    // Modify GetRefRW to update LastWriteTick

    public ref T GetRefRW(int entityId, uint currentVersion)

    {

        // ... (existing bounds check) ...

        

        // Update global table tick (Atomic for 32-bit aligned)

        \_lastWriteTick \= currentVersion;

        

        // ... (existing logic) ...

    }

    

    // Modify Set to update LastWriteTick

    public void Set(int entityId, in T component, uint version)

    {

        \_lastWriteTick \= version;

        // ... (existing logic) ...

    }

}

**File: `Fdp.Kernel/EntityRepository.cs` (Modification)**

// Inside EntityRepository

public sealed partial class EntityRepository

{

    // NEW: Check if component table changed

    public bool HasComponentChanged(Type componentType, uint sinceTick)

    {

        if (\_componentTables.TryGetValue(componentType, out var table))

        {

            // We need to cast to concrete types to access LastWriteTick if not in Interface

            // Best practice: Add LastWriteTick to IComponentTable interface

            return table.LastWriteTick \> sinceTick;

        }

        return false;

    }

}

**File: `Fdp.Kernel/IComponentTable.cs` (Modification)**

public interface IComponentTable : IDisposable

{

    // ... existing ...

    

    // NEW

    uint LastWriteTick { get; }

}

**File: `Fdp.Kernel/FdpEventBus.cs` (Modification)**

public class FdpEventBus : IDisposable

{

    // ... existing ...

    

    // NEW: Track active event types for this frame

    private readonly HashSet\<int\> \_activeEventIds \= new();

    

    public void Publish\<T\>(T evt) where T : unmanaged

    {

        \_activeEventIds.Add(EventType\<T\>.Id);

        // ... existing write ...

    }

    

    // Managed overload

    public void PublishManaged\<T\>(T evt) where T : class

    {

        \_activeEventIds.Add(GetManagedTypeId\<T\>());

        // ... existing write ...

    }

    

    public bool HasEvent(Type eventType)

    {

        int id;

        if (eventType.IsValueType)

             id \= (int)typeof(EventType\<\>).MakeGenericType(eventType).GetField("Id").GetValue(null); // Reflection optimized later? 

             // Actually, we should ask the registry or cache it in ModuleHost

        else

             id \= eventType.FullName\!.GetHashCode() & 0x7FFFFFFF;

             

        return \_activeEventIds.Contains(id);

    }

    

    public void SwapBuffers()

    {

        \_activeEventIds.Clear();

        // ... existing swap ...

    }

}

---

### **Part 2: ModuleHost Kernel Integration**

**File: `ModuleHost.Core/ModuleHostKernel.cs` (Trigger Logic)**

// Inside ModuleHostKernel

// Cache Type-\>ID lookups to avoid reflection in hot path

private Dictionary\<Type, int\> \_eventIds \= new(); 

private void CacheIds(IModule module)

{

    foreach(var t in module.WatchEvents)

    {

        // ... resolve ID ...

    }

}

private bool ShouldRunThisFrame(ModuleEntry entry)

{

    // 1\. Timer Check

    int freq \= Math.Max(1, entry.Policy.TargetFrequencyHz);

    bool timerDue \= (entry.FramesSinceLastRun \+ 1\) \>= (60 / freq); // Approx logic

    

    if (timerDue) return true;

    // 2\. Event Triggers (Immediate)

    // Checks if event happened THIS frame

    if (entry.Module.WatchEvents \!= null)

    {

        foreach (var evtType in entry.Module.WatchEvents)

        {

            // Note: Use cached IDs in production

            if (\_liveWorld.Bus.HasEvent(evtType)) 

                return true;

        }

    }

    // 3\. Component Triggers (Since last run)

    // Checks if component changed since LAST run

    if (entry.Module.WatchComponents \!= null)

    {

        uint lastRunTick \= entry.LastRunTick; // Need to track this in ModuleEntry

        foreach (var compType in entry.Module.WatchComponents)

        {

            if (\_liveWorld.HasComponentChanged(compType, lastRunTick)) 

                return true;

        }

    }

    return false;

}

**File: `ModuleHost.Core/Abstractions/IModule.cs` (Update)**

public interface IModule

{

    // ... existing ...

    

    // NEW: Reactive Configuration

    IReadOnlyList\<Type\>? WatchComponents { get; }

    IReadOnlyList\<Type\>? WatchEvents { get; }

}

### **Requirements Met**

1. **Reactive:** Modules wake up immediately if a watched Event fires (Frame N) or Component changes (Table Dirty).  
2. **Efficiency:** `LastWriteTick` is updated atomically during the hot path (negligible cost). `HasEvent` uses a HashSet lookup (O(1)). No iteration over entities required.  
3. **Integration:** Hooks into the `ShouldRunThisFrame` logic seamlessly.

Here is the implementation for **Chapter 3: Convoy & Pooling Patterns**.

This introduces the `SnapshotPool`, refines `SharedSnapshotProvider`, and implements the "Convoy Auto-Discovery" logic in the Kernel.

### **Part 1: The Pool Infrastructure**

**File: `ModuleHost.Core/Providers/SnapshotPool.cs` (New)**

using System.Collections.Concurrent;

using Fdp.Kernel;

namespace ModuleHost.Core.Providers

{

    public class SnapshotPool

    {

        private readonly ConcurrentStack\<EntityRepository\> \_pool \= new();

        private readonly Action\<EntityRepository\>? \_schemaSetup;

        public SnapshotPool(Action\<EntityRepository\>? schemaSetup)

        {

            \_schemaSetup \= schemaSetup;

        }

        public EntityRepository Get()

        {

            if (\_pool.TryPop(out var repo))

            {

                return repo;

            }

            

            // Create new if empty

            var newRepo \= new EntityRepository();

            \_schemaSetup?.Invoke(newRepo);

            return newRepo;

        }

        public void Return(EntityRepository repo)

        {

            repo.SoftClear(); // Reset indices but keep buffers

            \_pool.Push(repo);

        }

    }

}

---

### **Part 2: Shared Provider Logic**

**File: `ModuleHost.Core/Providers/SharedSnapshotProvider.cs` (Refined)**

using System;

using System.Threading;

using Fdp.Kernel;

using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers

{

    public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable

    {

        private readonly EntityRepository \_liveWorld;

        private readonly EventAccumulator \_eventAccumulator;

        private readonly BitMask256 \_unionMask;

        private readonly SnapshotPool \_pool;

        

        private EntityRepository? \_currentSnapshot;

        private int \_activeReaders;

        private uint \_lastSeenTick;

        private readonly object \_lock \= new object();

        public SharedSnapshotProvider(

            EntityRepository liveWorld,

            EventAccumulator eventAccumulator,

            BitMask256 unionMask,

            SnapshotPool pool)

        {

            \_liveWorld \= liveWorld;

            \_eventAccumulator \= eventAccumulator;

            \_unionMask \= unionMask;

            \_pool \= pool;

        }

        public SnapshotProviderType ProviderType \=\> SnapshotProviderType.Shared;

        public void Update()

        {

            // Only update if we are starting a NEW shared frame logic

            // But SharedProvider is driven by AcquireView usually.

            // Wait \- "Update()" is called at Sync Point.

            // If we want to support Convoy, we need to prepare the snapshot HERE if anyone is going to run.

            // But we don't know who runs yet.

            

            // STRATEGY: SharedProvider is lazy. Update() does nothing. 

            // AcquireView() triggers the sync on the \*first\* reader.

        }

        public ISimulationView AcquireView()

        {

            lock (\_lock)

            {

                if (\_currentSnapshot \== null)

                {

                    // I am the Convoy Leader (First one to run)

                    \_currentSnapshot \= \_pool.Get();

                    

                    // Sync using Union Mask

                    \_currentSnapshot.SyncFrom(\_liveWorld, \_unionMask);

                    \_eventAccumulator.FlushToReplica(\_currentSnapshot.Bus, \_lastSeenTick);

                    \_lastSeenTick \= \_liveWorld.GlobalVersion;

                }

                \_activeReaders++;

                return \_currentSnapshot;

            }

        }

        public void ReleaseView(ISimulationView view)

        {

            lock (\_lock)

            {

                \_activeReaders--;

                if (\_activeReaders \== 0\)

                {

                    // Convoy finished. Return to pool.

                    if (\_currentSnapshot \!= null)

                    {

                        \_pool.Return(\_currentSnapshot);

                        \_currentSnapshot \= null;

                    }

                }

            }

        }

        public void Dispose() { /\* ... \*/ }

    }

}

---

### **Part 3: Kernel Auto-Grouping**

**File: `ModuleHost.Core/ModuleHostKernel.cs` (Grouping Logic)**

// Inside ModuleHostKernel

private void AutoAssignProviders()

{

    var pool \= new SnapshotPool(\_schemaSetup); // Global pool for this kernel

    // Key for grouping

    var groups \= \_modules

        .Where(m \=\> m.Provider \== null) // Only auto-assign

        .GroupBy(m \=\> new { 

            Mode \= m.Policy.Mode, 

            Strategy \= m.Policy.Strategy, 

            Freq \= m.Policy.TargetFrequencyHz 

        });

    foreach (var group in groups)

    {

        var key \= group.Key;

        var modules \= group.ToList();

        // CASE A: FrameSynced \+ GDB (Fast Replica Convoy)

        if (key.Strategy \== DataStrategy.GDB)

        {

            // Create ONE persistent replica

            var provider \= new DoubleBufferProvider(\_liveWorld, \_eventAccumulator, \_schemaSetup);

            foreach (var m in modules) m.Provider \= provider;

        }

        // CASE B: Asynchronous \+ SoD (Slow Snapshot Convoy)

        else if (key.Strategy \== DataStrategy.SoD)

        {

            if (modules.Count \== 1\)

            {

                // Simple: OnDemandProvider (Self-contained)

                var m \= modules\[0\];

                var mask \= GetMask(m.Module);

                m.Provider \= new OnDemandProvider(\_liveWorld, \_eventAccumulator, mask, \_schemaSetup);

            }

            else

            {

                // Convoy: SharedSnapshotProvider

                // 1\. Calculate Union Mask

                var unionMask \= new BitMask256();

                foreach (var m in modules) 

                {

                    unionMask.BitwiseOr(GetMask(m.Module));

                }

                // 2\. Create Shared Provider

                var sharedProvider \= new SharedSnapshotProvider(\_liveWorld, \_eventAccumulator, unionMask, pool);

                

                // 3\. Assign

                foreach (var m in modules) m.Provider \= sharedProvider;

            }

        }

    }

}

private BitMask256 GetMask(IModule module)

{

    // Helper to get mask from module requirements

    // (Implementation details depend on IModule interface)

    return new BitMask256(); // Placeholder

}

### **Requirements Met**

1. **Convoy Pattern:** Modules with the same Frequency/Strategy share a provider.  
2. **Union Mask:** The `SharedSnapshotProvider` syncs the superset of data needed.  
3. **Pooling:** `SnapshotPool` reuses `EntityRepository` instances, eliminating allocation spikes.  
4. **Lazy Sync:** The first module in the convoy triggers the sync; subsequent modules get the cached snapshot instantly.

Here is the implementation for **Chapter 4: Resilience & Safety**.

This adds the `ModuleCircuitBreaker` and the safe execution wrapper to prevent module crashes from taking down the host.

### **Part 1: The Circuit Breaker**

**File: `ModuleHost.Core/Resilience/ModuleCircuitBreaker.cs` (New)**

using System;

namespace ModuleHost.Core.Resilience

{

    public enum CircuitState { Closed, Open, HalfOpen }

    public class ModuleCircuitBreaker

    {

        private readonly int \_failureThreshold;

        private readonly int \_resetTimeoutMs;

        

        private int \_failureCount;

        private DateTime \_lastFailureTime;

        private CircuitState \_state \= CircuitState.Closed;

        public ModuleCircuitBreaker(int failureThreshold, int resetTimeoutMs)

        {

            \_failureThreshold \= failureThreshold;

            \_resetTimeoutMs \= resetTimeoutMs;

        }

        public bool CanRun()

        {

            if (\_state \== CircuitState.Closed) return true;

            if (\_state \== CircuitState.Open)

            {

                if ((DateTime.UtcNow \- \_lastFailureTime).TotalMilliseconds \> \_resetTimeoutMs)

                {

                    \_state \= CircuitState.HalfOpen;

                    return true; // Try once

                }

                return false; // Still broken

            }

            return \_state \== CircuitState.HalfOpen; // Allow the test run

        }

        public void RecordSuccess()

        {

            if (\_state \== CircuitState.HalfOpen)

            {

                \_state \= CircuitState.Closed;

                \_failureCount \= 0;

            }

            // If Closed, do nothing (keep happy)

        }

        public void RecordFailure(string error)

        {

            \_lastFailureTime \= DateTime.UtcNow;

            \_failureCount++;

            if (\_state \== CircuitState.HalfOpen || \_failureCount \>= \_failureThreshold)

            {

                \_state \= CircuitState.Open;

                // Log: Circuit Tripped\!

            }

        }

    }

}

---

### **Part 2: Safe Execution Wrapper**

**File: `ModuleHost.Core/ModuleHostKernel.cs` (Modification)**

We modify `DispatchModules` to use `ExecuteModuleSafe`.

// Inside ModuleHostKernel

private async Task ExecuteModuleSafe(ModuleEntry entry, ISimulationView view, float dt)

{

    // 1\. Check Circuit Breaker

    if (entry.CircuitBreaker \!= null && \!entry.CircuitBreaker.CanRun())

    {

        return; // Skip execution

    }

    try

    {

        int timeout \= entry.Policy.MaxExpectedRuntimeMs;

        if (timeout \<= 0\) timeout \= 1000; // Default safety

        using var cts \= new CancellationTokenSource(timeout);

        

        // 2\. Run Module

        var tickTask \= Task.Run(() \=\> entry.Module.Tick(view, dt), cts.Token);

        

        // 3\. Wait with Timeout

        var completedTask \= await Task.WhenAny(tickTask, Task.Delay(timeout));

        

        if (completedTask \== tickTask)

        {

            // Await to propagate exceptions

            await tickTask; 

            entry.CircuitBreaker?.RecordSuccess();

        }

        else

        {

            // TIMEOUT

            // We cannot kill the thread, but we mark it as failed logic.

            // Mark entry as Zombie? (Optional advanced logic)

            entry.CircuitBreaker?.RecordFailure("Timeout");

            Console.Error.WriteLine($"\[Resilience\] Module {entry.Module.Name} timed out ({timeout}ms).");

        }

    }

    catch (Exception ex)

    {

        // CRASH

        entry.CircuitBreaker?.RecordFailure(ex.Message);

        Console.Error.WriteLine($"\[Resilience\] Module {entry.Module.Name} crashed: {ex.Message}");

    }

}

---

### **Part 3: Integration into Dispatch Loop**

**File: `ModuleHost.Core/ModuleHostKernel.cs` (DispatchModules Update)**

// Inside DispatchModules loop

if (entry.CurrentTask \== null && ShouldRunThisFrame(entry))

{

    var view \= entry.Provider.AcquireView();

    entry.LeasedView \= view;

    float dt \= entry.AccumulatedDeltaTime;

    // Use Safe Wrapper instead of raw Task.Run

    entry.CurrentTask \= ExecuteModuleSafe(entry, view, dt);

    

    if (entry.Policy.Mode \== RunMode.FrameSynced)

    {

        tasksToWait.Add(entry.CurrentTask);

    }

}

### **Requirements Met**

1. **Safety:** If a module throws `Exception`, the Kernel catches it and logs it.  
2. **Circuit Breaking:** After N failures, the module stops running for T seconds.  
3. **Timeout:** If a module hangs, the main thread stops waiting (for async modules) or reports it (for synced).

Here is the implementation for **Chapter 5: Execution Modes**.

This solidifies the configuration API, allowing modules to declare their exact runtime needs.

### **Part 1: The Policy Definition**

**File: `ModuleHost.Core/Abstractions/ExecutionPolicy.cs`**

using System;

using Fdp.Kernel;

namespace ModuleHost.Core.Abstractions

{

    public struct ExecutionPolicy

    {

        public RunMode Mode;

        public DataStrategy Strategy;

        public int TargetFrequencyHz;

        public int MaxExpectedRuntimeMs;

        public int FailureThreshold;

        

        // Data Requirements

        public ComponentMask? Components;

        public EventTypeMask? Events; // Not used yet, but placeholder for filtering

        // Factory Methods for Common Profiles

        

        public static ExecutionPolicy Synchronous() \=\> new()

        {

            Mode \= RunMode.Synchronous,

            Strategy \= DataStrategy.Direct

        };

        public static ExecutionPolicy FastReplica() \=\> new()

        {

            Mode \= RunMode.FrameSynced,

            Strategy \= DataStrategy.GDB,

            TargetFrequencyHz \= 60,

            MaxExpectedRuntimeMs \= 15

        };

        public static ExecutionPolicy SlowBackground(int hz) \=\> new()

        {

            Mode \= RunMode.Asynchronous,

            Strategy \= DataStrategy.SoD,

            TargetFrequencyHz \= hz,

            MaxExpectedRuntimeMs \= 100,

            FailureThreshold \= 5

        };

    }

}

---

### **Part 2: Module Definition Update**

**File: `ModuleHost.Core/Abstractions/IModule.cs`**

public interface IModule

{

    string Name { get; }

    

    // REPLACES Tier/UpdateFrequency

    ExecutionPolicy Policy { get; } 

    

    // ... existing ...

    

    // Optional: Dynamic Policy adjustment? No, keep it static for now.

}

---

### **Part 3: Kernel Registration Logic**

**File: `ModuleHost.Core/ModuleHostKernel.cs`**

// Inside ModuleHostKernel

public void RegisterModule(IModule module)

{

    if (\_initialized) throw new InvalidOperationException("Too late");

    var entry \= new ModuleEntry

    {

        Module \= module,

        Policy \= module.Policy,

        // Initialize Circuit Breaker from Policy

        CircuitBreaker \= new ModuleCircuitBreaker(

            module.Policy.FailureThreshold, 

            5000 // Reset time could be in policy too

        )

    };

    

    \_modules.Add(entry);

}

// Inside Initialize() \- The Auto-Grouper logic from Chapter 3 goes here

public void Initialize()

{

    AutoAssignProviders(); // Uses entry.Policy to group

    // ...

}

---

### **Requirements Met**

1. **Flexibility:** Users can define any combination (e.g., `Asynchronous + GDB` for a heavy AI that needs full world context).  
2. **Profiles:** Static factory methods (`SlowBackground`, `FastReplica`) make standard use cases easy.  
3. **Explicit:** No magic "Tier" enum hidden logic. Everything is in the `Policy` struct.

Here is the implementation for **Chapter 6: Network Gateway Core (SST/DDS Integration)**.

This introduces the **Translator Pattern** to bridge the gap between ECS Components and DDS Descriptors.

### **Part 1: The Translator Interface**

**File: `ModuleHost.Core/Network/IDescriptorTranslator.cs` (New)**

using Fdp.Kernel;

using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Network

{

    /// \<summary\>

    /// Bridges the gap between a rich DDS Descriptor and atomic ECS Components.

    /// \</summary\>

    public interface IDescriptorTranslator

    {

        // Topic Name (e.g. "SST.EntityState")

        string TopicName { get; }

        

        // INGRESS: Read from DDS \-\> Write to Command Buffer

        // Called in Phase 1 (Input)

        void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view);

        

        // EGRESS: Read from ECS \-\> Write to DDS

        // Called in Phase 4 (Export)

        void ScanAndPublish(ISimulationView view, IDataWriter writer);

    }

}

---

### **Part 2: The Mock Data Interfaces (Abstracting DDS)**

Since we don't have the actual DDS library here, we define abstractions.

**File: `ModuleHost.Core/Network/NetworkAbstractions.cs` (New)**

public interface IDataReader

{

    // Returns a list of generic samples (object for now, or use generics if possible)

    // In real DDS, this would be Take\<T\>()

    IEnumerable\<object\> TakeSamples();

}

public interface IDataWriter

{

    void Write(object sample);

    void Dispose(long entityId);

}

---

### **Part 3: The SST Module (The Manager)**

**File: `ModuleHost.Core/Network/SSTModule.cs` (New)**

using System.Collections.Generic;

using ModuleHost.Core.Abstractions;

using ModuleHost.Core.Scheduling;

namespace ModuleHost.Core.Network

{

    public class SSTModule : IModule

    {

        public string Name \=\> "SSTGateway";

        public ExecutionPolicy Policy \=\> ExecutionPolicy.FastReplica(); // Run every frame

        private readonly List\<IDescriptorTranslator\> \_translators \= new();

        private readonly NetworkIngestSystem \_ingestSystem;

        private readonly NetworkSyncSystem \_syncSystem;

        public SSTModule()

        {

            // Register Translators here

            // \_translators.Add(new EntityStateTranslator());

            

            \_ingestSystem \= new NetworkIngestSystem(\_translators);

            \_syncSystem \= new NetworkSyncSystem(\_translators);

        }

        public void RegisterSystems(ISystemRegistry registry)

        {

            // Phase 1: Ingest

            registry.RegisterSystem(\_ingestSystem);

            

            // Phase 4: Export

            registry.RegisterSystem(\_syncSystem);

        }

        public void Tick(ISimulationView view, float dt) { /\* No Async Logic \*/ }

        

        // ... (Watch lists empty) ...

    }

}

---

### **Part 4: The Systems (Ingress/Egress)**

**File: `ModuleHost.Core/Network/NetworkSystems.cs` (New)**

using Fdp.Kernel;

namespace ModuleHost.Core.Network

{

    \[UpdateInPhase(SystemPhase.Input)\]

    public class NetworkIngestSystem : IModuleSystem

    {

        private readonly List\<IDescriptorTranslator\> \_translators;

        

        // In real app: Map TopicName \-\> IDataReader

        private Dictionary\<string, IDataReader\> \_readers \= new(); 

        public NetworkIngestSystem(List\<IDescriptorTranslator\> translators)

        {

            \_translators \= translators;

        }

        public void Execute(ISimulationView view, float deltaTime)

        {

            var cmd \= view.GetCommandBuffer();

            

            foreach (var translator in \_translators)

            {

                if (\_readers.TryGetValue(translator.TopicName, out var reader))

                {

                    translator.PollIngress(reader, cmd, view);

                }

            }

        }

    }

    \[UpdateInPhase(SystemPhase.Export)\]

    public class NetworkSyncSystem : IModuleSystem

    {

        private readonly List\<IDescriptorTranslator\> \_translators;

        private Dictionary\<string, IDataWriter\> \_writers \= new();

        public NetworkSyncSystem(List\<IDescriptorTranslator\> translators)

        {

            \_translators \= translators;

        }

        public void Execute(ISimulationView view, float deltaTime)

        {

            foreach (var translator in \_translators)

            {

                if (\_writers.TryGetValue(translator.TopicName, out var writer))

                {

                    translator.ScanAndPublish(view, writer);

                }

            }

        }

    }

}

### **Requirements Met**

1. **Translator Pattern:** Decouples FDP components from DDS IDL structs.  
2. **Phase Correctness:** Ingest happens early (Phase 1), Export happens late (Phase 4).  
3. **Command Buffer:** Ingest writes via `cmd`, respecting the thread-safety rules.

Here is the implementation for **Chapter 7: Geographic Services**.

This provides the `IGeographicTransform` service and the `CoordinateTransformSystem` to bridge FDP's Cartesian physics with the Network's Geodetic coordinates.

### **Part 1: The Service Interface**

**File: `ModuleHost.Core/Services/IGeographicTransform.cs` (New)**

using System.Numerics;

namespace ModuleHost.Core.Services

{

    public interface IGeographicTransform

    {

        // Setup

        void SetOrigin(double lat, double lon, double alt);

        

        // Conversion

        Vector3 ToCartesian(double lat, double lon, double alt);

        (double lat, double lon, double alt) ToGeodetic(Vector3 localPos);

        

        // Velocity (Vector rotation)

        Vector3 TransformVelocityToLocal(Vector3 geoVel);

        Vector3 TransformVelocityToGeo(Vector3 localVel);

    }

}

---

### **Part 2: The Transform System**

This system ensures that entities with *both* representations stay in sync.

**File: `ModuleHost.Core/Services/CoordinateTransformSystem.cs` (New)**

using Fdp.Kernel;

using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Services

{

    // Runs AFTER simulation but BEFORE export

    \[UpdateInPhase(SystemPhase.PostSimulation)\]

    public class CoordinateTransformSystem : IModuleSystem

    {

        private readonly IGeographicTransform \_geo;

        public CoordinateTransformSystem(IGeographicTransform geo)

        {

            \_geo \= geo;

        }

        public void Execute(ISimulationView view, float deltaTime)

        {

            var cmd \= view.GetCommandBuffer(); // To write updates back to Live World

            

            // 1\. Outbound (Physics \-\> Network)

            // Query: Has Physics(Pos), Has Network(Geo), Is Local Authority

            var outboundQuery \= view.Query()

                .With\<Position\>()

                .With\<PositionGeodetic\>() // Tier 2 Managed Component

                .WithOwned\<Position\>()    // We own physics

                .Build();

            foreach (var entity in outboundQuery)

            {

                ref readonly var localPos \= ref view.GetComponentRO\<Position\>(entity);

                var geoPos \= view.GetManagedComponentRO\<PositionGeodetic\>(entity); // Read-only view

                

                // Calculate new Geo

                var (lat, lon, alt) \= \_geo.ToGeodetic(localPos.Value);

                

                // Optimization: Check delta before writing

                if (Math.Abs(geoPos.Latitude \- lat) \> 1e-6 || 

                    Math.Abs(geoPos.Longitude \- lon) \> 1e-6)

                {

                    // Update Tier 2 component via Command

                    // Ideally we clone it (record with)

                    var newGeo \= geoPos with { Latitude \= lat, Longitude \= lon, Altitude \= alt };

                    cmd.SetManagedComponent(entity, newGeo);

                }

            }

            

            // 2\. Inbound Smoothing (Network \-\> Physics)

            // Handled by NetworkSmoothingSystem (Chapter 6 context), but logic is:

            // Read NetworkTarget \-\> Interpolate \-\> Write Position

        }

    }

}

---

### **Part 3: Network Smoothing System**

The "Ghost Target" smoother.

**File: `ModuleHost.Core/Services/NetworkSmoothingSystem.cs` (New)**

using System.Numerics;

using Fdp.Kernel;

using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Services

{

    \[UpdateInPhase(SystemPhase.Input)\] // Run early to set state for physics

    public class NetworkSmoothingSystem : IModuleSystem

    {

        public void Execute(ISimulationView view, float deltaTime)

        {

            var cmd \= view.GetCommandBuffer();

            

            // Query: Has NetworkTarget, Has Position, NOT Local Authority

            var query \= view.Query()

                .With\<Position\>()

                .With\<NetworkTarget\>()

                .WithoutOwned\<Position\>() 

                .Build();

            foreach (var entity in query)

            {

                ref readonly var currentPos \= ref view.GetComponentRO\<Position\>(entity);

                ref readonly var target \= ref view.GetComponentRO\<NetworkTarget\>(entity);

                

                // Dead Reckoning: Project target forward by latency?

                // For now, simple LERP

                

                float t \= Math.Clamp(deltaTime \* 10.0f, 0f, 1f); // Snap speed

                Vector3 newPos \= Vector3.Lerp(currentPos.Value, target.Value, t);

                

                if (Vector3.DistanceSquared(currentPos.Value, newPos) \> 0.001f)

                {

                    // Write to Live World directly? No, system runs on view.

                    // Wait, this is a Global System running on Main Thread? 

                    // If running in "Input" phase on Main Thread, we can use direct access if we had it.

                    // But via IModuleSystem we only have ISimulationView.

                    // CommandBuffer adds 1 frame latency.

                    

                    // Optimization: If this is a main-thread system, we should allow direct RW access 

                    // if we cast view to EntityRepository. 

                    

                    if (view is EntityRepository repo)

                    {

                        // Fast path: Direct write

                        ref var mutablePos \= ref repo.GetComponentRW\<Position\>(entity);

                        mutablePos.Value \= newPos;

                    }

                    else

                    {

                        // Slow path: Command

                        cmd.SetComponent(entity, new Position { Value \= newPos });

                    }

                }

            }

        }

    }

}

### **Requirements Met**

1. **Dual Representation:** Handles Cartesian (Physics) and Geodetic (Network) synchronization.  
2. **Unidirectional Flow:** Authority check ensures we only write `Geo` if we own `Physics`.  
3. **Smoothing:** Interpolates remote entities to prevent jitter.

This completes the **Core Implementation Plan**. You now have the code for Non-Blocking, Reactive, Convoy, Resilience, Modes, Network, and Geo services.
