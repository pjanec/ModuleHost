# **B-One NG Whitepaper \- Part 1: The Paradigm Shift**

For: Lead Developer, Fast Data Plane (FDP)

From: B-One NG Architecture Team

Date: January 3, 2026

Subject: Moving from "Simulation Engine" to "Simulation Kernel"

## **1\. Introduction: The "Game Engine" Misconception**

Historically, FDP was conceived as the **high-performance engine** of the simulator—analogous to how Unreal Engine or Unity is the "owner" of a game. In that model, the physics/simulation loop was king, and everything else was a plugin.

The Problem:

In B-One NG, a "Simulator" is just one type of node. We also have:

* **Monitoring Nodes:** Passive observers that need State (SST) but have zero Physics logic.  
* **Replay Servers:** Nodes that stream historical data but perform no integration.  
* **Headless Servers:** Pure logic nodes running AI but no graphics or hardware I/O.

If FDP is "owned" by the Physics Module (SimModule), then a Monitoring Node is forced to load a massive physics engine just to display a map icon. This breaks modularity.

## **2\. The New Architecture: The Universal Host**

We are inverting the ownership model.

### **2.1 The "Kernel" Concept**

FDP is transitioning from being an **Engine** (Logic \+ Data) to being a **Kernel** (Data Lake \+ Scheduler).

* **Old Model:**  
  * SimModule creates World.  
  * SimModule runs World.Tick().  
  * Other modules beg SimModule for access.  
* **New Model (Host-Centric):**  
  * **The Host (Backend.Host.exe)** is a lightweight shell.  
  * The Host initializes the **FDP EntityRepository** at startup. This is the empty "Data Lake."  
  * **Modules are Guests.** They *inject* logic into the Host.  
  * SimModule is just a plugin. It injects PhysicsSystem into the Host.  
  * SSTModule is just a plugin. It injects NetworkSystem into the Host.

### **2.2 Polymorphism in Action**

This architecture allows us to build radically different applications using the *exact same binary*:

| Application Type | Loaded Modules | FDP State |
| :---- | :---- | :---- |
| **Full Simulator** | SimModule \+ SSTModule \+ InputModule | Physics \+ Network \+ Inputs active. |
| **Map Monitor** | SSTModule \+ UIModule | **Zero Physics.** FDP populated purely by Network packets. |
| **AI Server** | SSTModule \+ AIModule | Network \+ AI logic. No Physics or Graphics. |

## **3\. The Consequences for FDP**

To support this, FDP must evolve in three specific ways:

1. **Decoupled Lifecycle:** The EntityRepository must be usable even if no systems are registered. It must serve as a passive database if needed.  
2. **Generic Scheduling:** FDP cannot hard-code a "Physics Loop." It must expose a generic **Phase System** where the Host defines the phases (Input, Sim, Export) and modules register systems into them.  
3. **Data Neutrality (Hybrid Model):**  
   * FDP must treat Physics Data and Logic Data as peers.  
   * **Crucially:** Logic is **not** restricted to Tier 2\. High-frequency logic (Health, Ammo, TeamID) lives in Tier 1 (Structs) for speed, just like Physics data.  
   * The Snapshot system must support efficient copying of *both* tiers to Background Modules.

Key Takeaway for Devs:

You are no longer building a car engine. You are building the chassis. The SimModule provides the engine, the SSTModule provides the radio, and the Host ensures they all bolt onto the chassis correctly.

# **B-One NG Whitepaper \- Part 2: Actors and Roles**

For: Lead Developer, Fast Data Plane (FDP)

From: B-One NG Architecture Team

Subject: Defining the Ecosystem

## **1\. The Stage: The Module Host**

The **Module Host** is the container process. It provides:

1. **Memory:** The FDP EntityRepository.  
2. **Time:** The Master Clock (Frame Number, DeltaTime).  
3. **Scheduler:** The SystemRegistry where modules plug in their logic.

## **2\. The Protagonists: Synchronous Modules**

Definition:

Synchronous Modules are those that run directly on the Main Thread. They execute sequentially within the primary simulation loop.

**Characteristics:**

* **Context:** Main Thread.  
* **Frequency:** Driven by the Frame Rate (e.g., 60Hz) or Time-Sliced.  
* **Access:** Read/Write access to the "Live" FDP state.  
* **Data Preference:** Heavy usage of **Tier 1 (Raw Pointers)** for speed.

**Examples:**

* **SimModule (Physics):** Integrates velocity, resolves collisions.  
* **InputModule (Hardware):** Reads joystick axes.  
* **PostSimModule (Transforms):** Converts Cartesian physics to Geodetic coordinates.

## **3\. The Observers: Background Modules**

Definition:

Background Modules (formerly "Satellites") are logic blocks that run purely asynchronously. They observe the simulation and make decisions, but their execution is decoupled from the main tick.

**Characteristics:**

* **Async:** Asynchronous execution (Thread Pool / Tasks).  
* **Frequency:** Varied (1Hz to 200Hz) and independent of Frame Rate.  
* **Access:** **Read-Only** access to a consistent **Snapshot**.  
* **Data Preference:** **Hybrid.**  
  * Uses **Tier 1 Structs** for fast counters (Health, Ammo, State Enums).  
  * Uses **Tier 2 Records** for complex data (Strings, Lists, Orders).

**Examples:**

* **AIModule:** Reads the battlefield state, thinks for 100ms, issues a "Move" command.  
* **SSTModule (Network):** Scans the world state, serializes it, and publishes to DDS.  
* **UIModule:** Reads fuel/ammo state and prepares HUD data.

The "Background Rule":

A Background Module MUST NEVER block the Synchronous Loop. It operates solely on the Snapshot leased from the Host.

## **4\. The Data Lake: Tier 1 vs. Tier 2**

FDP stores data in two tiers based on **Data Type**, not Domain. Logic uses both.

### **4.1 Tier 1: High Frequency / Blittable**

* **What:** Fixed-size Structs.  
  * *Physics:* Position, Velocity, Forces.  
  * *Logic:* Health, Ammo, TeamID, UnitStateEnum.  
* **Storage:** Unmanaged Memory (NativeChunkTable).  
* **Access:** Raw Pointers (float\*, int\*).  
* **Why:** Speed. Zero GC pressure.  
* **Snapshot Strategy:** **Selective Shadow Buffer Copy.**  
  * Since Logic uses Tier 1 heavily, we rely on "Dirty Chunk" tracking to efficiently copy only changed memory to the Background Module's snapshot buffer.

### **4.2 Tier 2: Complex / Managed**

* **What:** Variable-size Objects (Heap Data).  
  * *Logic:* Callsign (String), RoutePoints (List), CurrentOrders (Graph).  
* **Storage:** Managed Arrays (ManagedComponentTable).  
* **Access:** Object References.  
* **Why:** Flexibility for complex data structures.  
* **Snapshot Strategy:** **Reference Copy.**  
  * We shallow-copy the reference array.  
  * **Contract:** Objects must be **Immutable Records**. We replace the pointer, never mutate the object.

**Visual Summary:**

* **Synchronous Modules** live in the fast lane (mostly Tier 1).  
* **Background Modules** watch from the balcony, seeing a **Hybrid Snapshot** composed of Tier 1 Shadow Buffers and Tier 2 Reference Arrays.  
* 

# **B-One NG Whitepaper \- Part 3: The Execution Model**

For: Lead Developer, Fast Data Plane (FDP)

Subject: The Frame Lifecycle, Synchronization, and Module Categories

## **1\. The Three Execution Categories**

To simplify scheduling and data access, we categorize all logic into three distinct execution modes.

| Category | Execution Context | Synchronization | Data View | Use Case |
| :---- | :---- | :---- | :---- | :---- |
| **1\. Synchronous** | Main Thread | Sequential | **Live (R/W)** | Physics, Input, Sensors, Timers, Simple Logic. |
| **2\. Parallel Live** | Thread Pool | Fork-Join (Blocking) | **Live (Read)** | Heavy math needed *now* (Raycasts, Particles). |
| **3\. Background** | Thread Pool | Fire-and-Forget | **Snapshot** | AI, Network, Analytics. (Modules that think slowly or independently). |

## **2\. The Main Thread Timeline (The "Synchronous Loop")**

The Host drives a strict loop. This is the heartbeat of the simulation.

### **Phase 1: Input (Synchronous)**

* **Who:** InputModule.  
* **Action:** Writes to InputComponent (Tier 1).

### **Phase 2: Simulation (Synchronous)**

* **Who:** SimModule (Physics), TimerSystems (Sensors).  
* **Action:**  
  * Physics integrates Velocity $\\to$ Position.  
  * Sensors check Time.Now and run logic if due (Time-Slicing).  
* **Constraint:** All systems here run sequentially.

### **Phase 3: Parallel Live (The "Job System")**

* **Who:** Heavy Computation (e.g., Visibility Checks).  
* **Mechanism:**  
  1. **Fork:** Host spawns tasks to Thread Pool.  
  2. **Barrier:** Main Thread **WAITS**.  
  3. **Join:** Tasks complete.  
* **Data Safety:** Safe to read Live Data because the Main Thread is paused (no writers).

### **Phase 4: PostSimulation (Synchronous)**

* **Who:** CoordinateTransformSystem.  
* **Action:** Bridges Tier 1 Physics $\\to$ Tier 2 Geodetic State.

### **Phase 5: The Sync Point (The "Dispatcher")**

* **Who:** **Host Kernel**.  
* **Action:**  
  1. Host checks **Background Modules**: "Who is due to run?"  
  2. If any are due:  
     * Generate **Union Snapshot** (Lazy).  
     * Dispatch **Tasks** to Thread Pool (Fire-and-Forget).  
  3. If none are due: Do nothing.

### **Phase 6: Export (Synchronous)**

* **Who:** SSTModule (Network Egress).  
* **Action:** Reads Snapshot $\\to$ Serializes to DDS.

## **3\. The Background Scheduler**

Background Modules (Category 3\) run completely decoupled from the Main Thread.

### **3.1 Frequency and Clamping**

Background Modules define their own update rate (e.g., 10Hz, 200Hz).

The Host does NOT clamp this rate.

* **Slower than Host (e.g., 10Hz):** Host takes a snapshot and dispatches the task only every 6th frame (at 60Hz).  
* **Faster than Host (e.g., 200Hz):** Host dispatches the task 3-4 times per frame.  
  * *Result:* The Background Module receives the **same snapshot** multiple times.  
  * *Why?* The module might be driving external hardware (e.g., a motion chair) that requires a 200Hz heartbeat, even if the physics hasn't changed.

## **4\. Summary**

* **Synchronous:** Runs on the Main Thread. Fast, simple, live access.  
* **Parallel Live:** Runs on threads but blocks the frame. Used for heavy per-frame math.  
* **Background:** Runs on threads asynchronously. Uses Snapshots. Can run at any frequency, decoupled from the simulation rate.  
* 

# **B-One NG Whitepaper \- Part 4: The Data Concurrency Challenge**

For: Lead Developer, Fast Data Plane (FDP)

From: B-One NG Architecture Team

Subject: Solving the Reader/Writer Conflict

## **1\. The Conflict**

We have a 60Hz Synchronous Loop (Physics) that mutates state every 16ms.

We have 10Hz Background Loops (AI) that read state for 100ms.

The Problem:

If the AI reads Entity\[5\].Position at T=0, and Physics updates it to T=1 while the AI is still reading Entity\[5\].Fuel, the AI sees a "Torn Frame" (inconsistent state). This causes AI hallucinations and logic bugs.

## **2\. Why We REJECTED Kernel Copy-On-Write (COW)**

We considered implementing OS-level Copy-On-Write (forking memory pages on write). We rejected it for three reasons:

1. **Complexity:** It requires unsafe pointer swizzling and complex interlocks in the hot path.  
2. **Debugging:** It breaks the debugger's ability to show "Global Truth" (Global state becomes a lie).  
3. **Overkill:** We don't need to snapshot Physics (Tier 1\) often enough to justify this cost.

## **3\. The Solution: Structural Sharing & Shadow Buffers**

We use a hybrid strategy tailored to our two data tiers.

### **3.1 Tier 2: Reference Copy (Immutable Records)**

Tier 2 data (Logic/SST) is stored as **Arrays of References** (object\[\]).

* **The Snapshot:**  
  * When we want to save the state, we **Shallow Copy the Array**.  
  * SnapshotArray \= LiveArray.Clone();  
  * *Cost:* We copy the *pointers*. We do **NOT** clone the objects they point to.  
  * *Result:* SnapshotArray\[5\] and LiveArray\[5\] point to the **same** object in memory.  
* **The Write Barrier:**  
  * **Rule:** Tier 2 Objects are **Immutable Records**.  
  * When SimModule wants to update fuel, it does **NOT** do obj.Fuel \= 50.  
  * Instead, it creates a **New Record**: newObj \= oldObj with { Fuel \= 50 };  
  * It updates the **Live Array** to point to newObj.  
  * *Result:* LiveArray\[5\] points to newObj. SnapshotArray\[5\] still points to oldObj.

### **3.2 Tier 1: Dirty Chunk Shadow Buffers**

Tier 1 data (Structs) cannot be immutable. It is mutated in place.

For Background Modules that need Tier 1 data (e.g. Health, Ammo):

* **Mechanism:** Shadow Buffers.  
* **Optimization:** "Dirty Chunk" Tracking.  
* We only memcpy chunks that have *changed version* since the last snapshot for that specific background consumer.

## **4\. Why This Works for B-One**

* **Performance:** Array.Clone() is extremely fast. memcpy of dirty chunks is bandwidth-efficient.  
* **Memory:** We only allocate new memory when data *actually changes*. Unchanged data is shared perfectly.  
* **Safety:** Relies on standard C\# GC. No manual memory management required.

# **B-One NG Whitepaper \- Part 5: The Tiered Data Strategy**

For: Lead Developer, Fast Data Plane (FDP)

Subject: Handling Physics vs. Logic Data

## **1\. The Split**

FDP manages two types of memory. They are treated differently because their usage patterns are opposite.

| Feature | Tier 1 (Blittable) | Tier 2 (Managed) |
| :---- | :---- | :---- |
| **Content** | Position, Velocity, Health, Ammo | Callsign, RoutePoints, Orders |
| **Storage** | Unmanaged (NativeChunkTable) | Managed (ManagedComponentTable) |
| **Access** | Raw Pointers (float\*) | Object References |
| **Snapshot Strategy** | **Shadow Buffer (Dirty Copy)** | **Reference Copy** |

## **2\. Tier 1 Strategy: "Keep it Raw"**

Both Physics and High-Frequency Logic use Tier 1\.

* **No Write Barriers:** We never check "IsShared?" inside a synchronous loop.  
* **No Immutability:** Systems mutate memory in-place.

### **Snapshotting Tier 1**

Background Modules often need Tier 1 logic data (Health, Ammo).

1. **Shadow Buffers:** Each active background consumer acts as a "View".  
2. **Dirty Patching:** The Host checks Chunk.Version. If modified, it copies the chunk to the Shadow Buffer.  
3. **Cost:** Minimal. Sleeping entities consume zero bandwidth.

## **3\. Tier 2 Strategy: "Immutable Records"**

Complex Logic requires safety and flexibility.

* **Storage:** object\[\] arrays.  
* **Type Constraint:** All Tier 2 components must be public record.

### **The Contract**

The FDP API will enforce this pattern:

* // BAD (Compiler Error ideally, or Runtime Exception):  
* var data \= repo.Get\<Status\>(id);  
* data.Callsign \= "Bravo"; // Mutation forbidden on Records  
*   
* // GOOD:  
* var data \= repo.Get\<Status\>(id);  
* var newData \= data with { Callsign \= "Bravo" };  
* repo.Set(id, newData); // Atomic Replacement

### **Why Records?**

1. **Value Semantics:** Easy equality checking (if (old \== new)).  
2. **Non-Destructive Mutation:** The with keyword makes partial updates cheap.  
3. **Thread Safety:** Immutable objects are inherently thread-safe.

## **4\. The Bridge: Coordinate Transform**

How does data move from Tier 1 Physics to Tier 2 State (if needed)?

The CoordinateTransformSystem:

* Runs in Phase.PostSimulation.  
* Reads Tier 1 CartesianPosition.  
* Converts to Geodetic.  
* Creates new Tier 2 GeoPosition record (if Geodetic is stored as Tier 2\) OR updates Tier 1 Geodetic struct (if Tier 1).  
* **Result:** The Snapshot (taken immediately after) contains fresh Geodetic positions derived from the latest Physics frame.  
* 


# **B-One NG Whitepaper \- Part 6: The Snapshot Manager**

For: Lead Developer, Fast Data Plane (FDP)

Subject: The State Vending Machine

## **1\. Concept: The "Lazy" Vending Machine**

The Snapshot Manager is a Host Kernel service responsible for producing ISimWorldSnapshot objects.

Unlike a traditional game engine that might double-buffer the whole world every frame, the Snapshot Manager is Lazy. It operates on a "Pull" model.

**The Rule:** If no Background Module is ready to consume data, the Snapshot Manager does **nothing**.

## **2\. The "Union of Needs" Strategy**

We do not generate specific snapshots for specific modules (e.g., "AI Snapshot" vs "Net Snapshot"). This would cause redundant memory copying. Instead, we calculate the **Union** of all requirements for the current frame.

**The Algorithm:**

1. **Registration:** Background Modules register requirements: \[RequireComponent(typeof(Health))\].  
2. **Poll:** At the Sync Point, Host checks which modules are *idle* and *waiting* for a new snapshot.  
3. **Union Mask:** Host calculates Mask \= Union(WaitingModules.Requirements).  
4. **Capture:** Host generates *one* snapshot containing the data for that Mask.

## **3\. Tier 1 Optimization: "Dirty Chunk" Patching**

This is the most critical performance optimization to prevent the **"Bandwidth Wall"** when snapshotting Logic Structs (Tier 1).

The Problem:

memcpy of 100,000 entities (Health, Ammo, etc.) at 60Hz generates \~480MB/s of traffic, polluting the CPU cache.

The Solution: Persistent Shadow Buffers

Instead of allocating a new buffer every frame, the Host maintains a Persistent Shadow Buffer for the active snapshot stream. We only memcpy chunks that have changed since the last capture.

### **3.1 The Logic**

1. **Tracking:** FDP NativeChunkTable tracks \_chunkVersions\[chunkIndex\].  
2. **Context:** The Snapshot Manager holds a LastCapturedVersion for every chunk in its Shadow Buffer.  
3. **The Patch:**

foreach (var chunk in visibleChunks) {  
    if (chunk.Version \> shadowBuffer.GetVersion(chunk.Index)) {  
        // DATA CHANGED: Perform memcpy  
        UnsafeUtility.MemCpy(  
            shadowBuffer.GetPtr(chunk.Index),  
            liveRepo.GetPtr(chunk.Index),  
            chunk.ByteSize  
        );  
        shadowBuffer.SetVersion(chunk.Index, chunk.Version);  
    }  
    else {  
        // NO CHANGE: Skip memcpy entirely.  
        // The Shadow Buffer already holds the valid data.  
    }  
}

4. 

**Result:** For sleeping entities (static units), bandwidth usage drops to **Zero**.

## **4\. Tier 2 Optimization: Reference Copy & Pooling**

For Managed Data (Immutable Records), we use the Reference Copy strategy detailed in Part 5\.

**Optimizations:**

1. **Array Pooling:** The object\[\] arrays used to hold the references are rented from ArrayPool\<object\>. They are returned when the Background Module calls Dispose().  
2. **Versioning:** If *no* Tier 2 data changed globally (rare, but possible), we return a cached Snapshot wrapper object.

## **5\. Staggered Execution (Time-Slicing)**

To further reduce the spike at the Sync Point, we stagger the updates.

**Scenario:**

* AI runs at 10Hz.  
* Network runs at 20Hz.  
* Physics runs at 60Hz.

**The Scheduler:**

* **Frame 1:** Network is ready. Host patches Shadow Buffer (Net+AI Union). Net reads.  
* **Frame 2:** Network is busy. AI is busy. **Host does nothing.**  
* **Frame 3:** Network is ready. Host patches Shadow Buffer.  
* **Frame 6:** AI is ready. Host patches Shadow Buffer.

**Key Takeaway:** The "Patching" cost is amortized based on the *consumer's* frequency, not the *simulation's* frequency.

## **6\. The Public Interface**

The interface must abstract away the difference between Tier 1 (Shadow Buffer) and Tier 2 (Reference Array).

public interface ISimWorldSnapshot : IDisposable {  
    // Tier 1 Access (Reads from Shadow Buffer)  
    // Returns explicit struct copy to ensure safety  
    T GetStruct\<T\>(int entityId) where T : struct;

    // Tier 2 Access (Reads from Reference Array)  
    // Returns reference to Immutable Record  
    T GetRecord\<T\>(int entityId) where T : class;

    // Bulk Access (for high-perf iterators)  
    // Returns ReadOnlySpan to the Shadow Buffer  
    ReadOnlySpan\<T\> GetStructSpan\<T\>(int chunkId) where T : struct;  
}

# **B-One NG Whitepaper \- Part 7: The Background Workflow**

For: Lead Developer, Fast Data Plane (FDP)

Subject: Asynchronous Safety & The Background Lifecycle

## **1\. The Async Reality**

Background Modules (Category 3: AI, Network, Analytics) live in a delayed reality.

When an AI unit decides to "Shoot," it is basing that decision on data that is 10-100ms old.

**The Risk:**

1. AI reads Frame 100: "Target is Alive at (10,10)."  
2. AI thinks for 50ms.  
3. Frame 103 (Synchronous): Target moves to (20,20).  
4. Frame 105 (Synchronous): Target is destroyed.  
5. Frame 106 (AI Action): AI sends "Fire at (10,10)."

If we execute this command blindly, the AI shoots at empty space (or a corpse).

## **2\. The Background Lifecycle**

Unlike Synchronous Modules, Background Modules do not have a simple OnUpdate().

### **Step 1: The Trigger (Host Side)**

The Host determines it is time for the Module to run.

// Host Main Thread

if (IsTimeFor(aiModule)) {

    // Lazy Snapshot: Only created if not already cached for this frame/mask

    var snap \= snapshotManager.GetUnionSnapshot();

    

    // Fire-and-Forget Task

    Task.Run(() \=\> aiModule.RunAsync(snap));

}

### **Step 2: The Read (Worker Thread)**

The Background Module reads data transparently.

* **Hybrid Access:** It can read fast Tier 1 Structs (via Shadow Buffer) or complex Tier 2 Records (via Reference Array).  
* **Consistency:** The snapshot guarantees all data belongs to the *same frame*, even if the Main Thread has moved on.

### **Step 3: The Command (Optimistic Concurrency)**

Background Modules cannot write to the Read-Only Snapshot. They must submit Commands.

Crucially, they must attach Validation Data (Preconditions).

var cmd \= new FireCommand {

    TargetId \= 500,

    // PRECONDITIONS:

    ExpectedGen \= snapshot.GetGeneration(500), // "It was alive in Gen 12"

    ExpectedPos \= snapshot.GetPosition(500)    // "It was at (10,10)"

};

host.CommandBuffer.Enqueue(cmd);

### **Step 4: Release**

The Module finishes. It calls snapshot.Dispose().

* The Host recycles the internal arrays to the pool.

## **3\. Structural Changes (Create/Destroy)**

The TempID Problem:

If the Network Module receives a packet "Spawn Tank ID: 999", it cannot create Entity 999 immediately because CreateEntity is not thread-safe on the Live World.

**The Solution:**

1. **TempID:** The Module generates a negative ID (e.g., \-1) locally.  
2. **Batched Commands:**  
   * Cmd 1: CreateEntity(Prefab="Tank", OutTempId=-1)  
   * Cmd 2: SetComponent(Entity=-1, Position=(10,10))  
3. **Resolution:** When the Host executes the buffer (Start of Next Frame):  
   * It creates Real Entity (ID: 500).  
   * It maps {-1 : 500}.  
   * It executes Cmd 2 on Entity 500\.

## **4\. Summary for FDP Devs**

1. **You provide the Read-Only View:** Your job is to make GetStruct and GetRecord fast and thread-safe.  
2. **You provide the Write Queue:** Implement EntityCommandBuffer that is thread-safe for multi-threaded producers.  
3. **Trust No One:** When processing the Command Buffer, always validate that the entity is still alive and in the expected state.

# **B-One NG Whitepaper \- Part 8: Implementation Checklist**

**For:** Lead Developer, Fast Data Plane (FDP)

**Subject:** Action Items for FDP Kernel Evolution

## **1\. Managed Component Table Updates**

* \[ \] **Versioning:** Implement \_tableVersion counter that increments on any Set().  
* \[ \] **Snapshot API:** Implement GetSnapshot(ComponentMask mask) that performs shallow array cloning.  
* \[ \] **Pooling:** Integrate ArrayPool\<object\> for snapshot storage.

## **2\. Native Chunk Table Updates**

* \[ \] **Column Copy:** Implement optimized memcpy for specific columns (for Tier 1 snapshotting).

## **3\. System Registry**

* \[ \] **Interface:** Define ISystemRegistry allowing modules to register ComponentSystem \+ Phase.  
* \[ \] **Sorting:** Implement Topological Sort based on \[UpdateBefore/After\] attributes.

## **4\. Snapshot Manager**

* \[ \] **Aggregation:** Implement logic to calculate UnionMask from active satellites.  
* \[ \] **Wrapper:** Implement ISimWorldSnapshot concrete class.

## **5\. Command Buffer**

* \[ \] **Thread Safety:** Ensure EntityCommandBuffer is safe for concurrent writes from background threads.  
* \[ \] **Temp IDs:** Verify CreateEntity returns usable temporary handles for batched commands.

## **6\. Safety Analyzers (Optional but Recommended)**

* \[ \] **Roslyn Analyzer:** Create a build warning if a developer tries to mutate a property on a Tier 2 Record directly (if not caught by record semantics).  
* 

---

### **6\. Integration with FDP (Specifics)**

To make this work in your current codebase, here is how the pieces fit:

1. **`EntityRepository`:**  
     
   * Needs to expose a `GetSnapshot(BitMask256 componentMask)` method.  
   * Needs to lock `_chunkVersions` reading during the Sync Point (Phase 4).

   

2. **`NativeChunkTable` (Tier 1):**  
     
   * Add `CopyColumnTo(int chunkIndex, IntPtr destPtr, int stride)`.  
   * This is efficient because your `_basePtr` setup allows calculating the source address instantly.

   

3. **`ManagedComponentTable` (Tier 2):**  
     
   * Add `SnapshotPage(int chunkIndex, object[] destArray)`.  
   * Since you already use `T?[][] _chunks`, this is just `Array.Copy(_chunks[i], destArray, length)`.

### **Summary Recommendation**

The "Snapshot-on-Demand" approach is the **correct choice** for your goals, provided you mitigate the bandwidth issues:

1. **Minimize Tier 2:** Move every possible logic variable (ints, floats, bools, enums) into Tier 1 (Structs). Tier 1 snapshots are strictly faster and generate zero GC pressure.  
2. **Pool Snapshots:** Never allocate `new Snapshot()`. Reuse the container and its internal arrays.  
3. **Validate Commands:** Satellites must assume their commands might fail because the world moved on.

This architecture avoids the "kernel hacking" of full COW while delivering 90% of the benefits.




Choosing **Snapshot-on-Demand (SoD)** over Full COW allows you to keep the FDP Kernel "Boring" (in a good, high-performance way). It drastically simplifies the low-level mechanics while shifting the complexity to a specific "Sync Phase" at the end of the frame.

Here is exactly how this choice ripples through the rest of the FDP architecture.

---

### 1. What becomes Simpler / More Elegant?

The biggest win is that **Tier 1 (Physics/Hot Path) remains "Metal".**

#### A. Iterators & Systems Stay "Naive" (Huge Win)
*   **With COW:** Every `ForEach` loop needs to look up the chunk pointer from a directory (`_blocks[i]`) because the memory address could have changed since the last frame. You have to "Hoist" checks to avoid performance hits.
*   **With SoD:** You keep your current `VirtualAlloc` sparse array. Chunk 5 is *always* at `BasePtr + (5 * 64KB)`.
    *   **Consequence:** The JIT compiler can optimize your physics loops aggressively (constant offsets). Pointers obtained at the start of the frame are guaranteed valid until the end of the frame.

#### B. Debugging is "What You See Is What You Get"
*   **With COW:** If you inspect an Entity in Visual Studio, you might be looking at "Version 1" (Snapshot) while the engine is writing to "Version 2" (Live) at a different address. It is confusing.
*   **With SoD:** There is only one Live State. If the debugger says `X=50`, then `X=50`. Snapshots are distinct objects (`byte[]` buffers) that you have to explicitly look at.

#### C. Memory Allocator Remains Static
*   **With COW:** You need a dynamic pool of 64KB blocks to handle forks. You need to manage fragmentation and return memory to the OS manually.
*   **With SoD:** You keep the existing `NativeMemoryAllocator` design from `DUMP_1.txt`. It reserves space once. It commits pages lazily. It is robust and battle-tested.

#### D. Flight Recorder Implementation
*   **Consequence:** The existing `AsyncRecorder` logic in your current code (which uses `CopyChunkToBuffer`) is **already** an SoD implementation!
*   **Benefit:** You don't need to rewrite the recorder. You just need to generalize it so *Satellites* (AI/Network) can use the same copying mechanism.

---

### 2. What requires Greater Care? (The New Burdens)

While the low-level kernel stays simple, the **Scheduler** and **API** take on new responsibilities.

#### A. The "Sync Point" Constraint (Scheduler)
*   **With COW:** You can take a snapshot *anytime*, even in the middle of a physics update (atomic ref count).
*   **With SoD:** You strictly **cannot** read Tier 1 memory while it is being written to (race condition).
*   **Consequence:** You must enforce a **"Phase 4: Sync Point"** in your Loop.
    1.  Input/Simulate (Writers Active).
    2.  **BARRIER (Stop threads).**
    3.  Snapshot/Copy (Readers Active).
    4.  Resume.
*   *Note:* If this phase takes too long (e.g., copying too much data), you miss your 16ms frame target.

#### B. "Dirty Chunk" Tracking is now Critical
*   **With COW:** If you write to a chunk, you pay the cost immediately (Fork).
*   **With SoD:** You pay the cost at the Sync Point. To avoid copying 500MB per frame, you **must** know exactly which chunks changed.
*   **Consequence:** The `_chunkVersions` array in `NativeChunkTable` is no longer just for Deltas; it is now the primary mechanism for optimizing Satellite performance. You must ensure **every** write (including raw pointer writes) updates this version.

#### C. Tier 2 Immutability is Mandatory
*   **Consequence:** Even though you aren't doing OS-level COW, you **must** use `record` and `ImmutableList` for Tier 2.
*   **Why:** SoD relies on `Array.Copy` of references. If the objects pointed to are mutable, the "Snapshot" will mutate when the Main Thread updates the object next frame. This requirement persists regardless of the strategy.

---

### 3. Architecture Ripple Effects

#### The "Satellite Interface"
You need a formal way for modules to request data. You can't just let them touch `EntityRepository` directly anymore.

*   **Old Way:** AI Module holds reference to `EntityRepository`.
*   **New Way:** AI Module holds reference to `ISimulationSnapshot`.
    *   The `EntityRepository` needs a factory method: `CreateSnapshot(BitMask requirements)`.

#### The Command Buffer Validation
Since Satellites are looking at a copy (which might be 50ms old), their commands might be invalid by the time they execute.

*   **Consequence:** `EntityCommandBuffer` needs to be smarter.
    *   Commands like `MoveEntity(ID, Pos)` need safeguards: `MoveEntity(ID, ExpectedGen, Pos)`.
    *   If the ID was recycled in the meantime, the command must fail gracefully, or the AI will move a completely new unit that reused the ID.

---

### Summary of Consequences

| Feature | COW (Ref-Counted) | Snapshot-on-Demand (Copy) |
| :--- | :--- | :--- |
| **Kernel Core** | **Complex Rewrite** (Indirection) | **Stable** (Keep Existing) |
| **Physics Loop** | **Slower** (Checks/Forks) | **Fastest** (Raw Pointers) |
| **Snapshot Cost** | **Instant** (Free) | **Variable** (Bandwidth bound) |
| **Developer API** | **Dangerous** (Stale Pointers) | **Safe** (Stable Pointers) |
| **Scheduler** | **Loose** (Snapshot anytime) | **Strict** (Must pause for copy) |

**Conclusion:**
Selecting **Snapshot-on-Demand** preserves the elegance and speed of your current Tier 1 (Unmanaged) architecture. It localizes complexity to the **End-of-Frame** phase rather than scattering checks throughout every system. This is the correct engineering trade-off for a simulation kernel.