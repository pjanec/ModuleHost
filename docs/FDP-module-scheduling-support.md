# **Module scheduling based on component change or bus event arrival**

To support **Signal-Based Scheduling** (waking modules only when data changes), FDP must expose coarse-grained "Dirty Flags" at the **Type/Table Level**.

Currently, FDP tracks versions at the **Chunk Level** (fine-grained). Scanning 1,000 chunk headers every frame just to decide *if* a module should run is too slow for the Scheduler.

Here is the architectural plan to support `OnComponentChange<T>` and `OnEvent<T>`.

---

### **1\. FDP Architecture Changes**

#### **A. Optimization: Global Table Versioning**

We need an $O(1)$ check to see if *any* entity has modified component `T`.

**Concept:** Every time a Chunk Version is updated, we also update a `LastWriteTick` on the `ComponentTable` itself.

**Modifications (`IComponentTable` / `ComponentTable<T>` / `ManagedComponentTable<T>`):**

public interface IComponentTable

{

    // ... existing ...

    uint LastWriteTick { get; } // New Property

}

// Inside NativeChunkTable / ManagedComponentTable

public void Set(int entityIndex, T value, uint version)

{

    // ... Existing Chunk Logic ...

    

    // NEW: Update Global Table Version

    // (Simple assignment is atomic for 32-bit uint, no Interlocked needed usually 

    // if we accept "eventual consistency" within the frame, but for strictness:)

    \_lastWriteTick \= version; 

}

#### **B. API: The Repository Query Interface**

The Scheduler needs a unified way to ask these questions.

// Fdp.Kernel/EntityRepository.cs

public sealed class EntityRepository

{

    // Check if Component T was modified after 'tick'

    public bool HasComponentChanged\<T\>(uint sinceTick)

    {

        // 1\. Get Table O(1)

        if (\_componentTables.TryGetValue(typeof(T), out var table))

        {

            // 2\. Check Version O(1)

            return table.LastWriteTick \> sinceTick;

        }

        return false;

    }

    // Check if Structure changed (Create/Destroy)

    public bool HasStructureChanged(uint sinceTick)

    {

        // EntityIndex needs to track a LastChangeTick too

        return \_entityIndex.LastChangeTick \> sinceTick;

    }

}

#### **C. API: Event Bus "Peeking"**

The Scheduler needs to know if events exist *without* consuming them (because the Module needs to consume them).

// Fdp.Kernel/FdpEventBus.cs

public class FdpEventBus

{

    public bool HasEvents\<T\>()

    {

        int id \= EventType\<T\>.Id;

        

        // Check Native

        if (\_nativeStreams.TryGetValue(id, out var ns) && ns.Count \> 0\) return true;

        

        // Check Managed

        if (\_managedStreams.TryGetValue(id, out var ms) && ms.Count \> 0\) return true;

        

        return false;

    }

}

---

### **2\. The Scheduler Logic (ModuleHost)**

Now the Scheduler can make intelligent decisions without touching entity memory.

#### **Data Structure: Module Dependencies**

Modules must declare what wakes them up.

public class ModuleDependencies

{

    public List\<Type\> WatchComponents \= new();

    public List\<Type\> WatchEvents \= new();

    

    // e.g.

    // WatchComponents.Add(typeof(Position));

    // WatchEvents.Add(typeof(ExplosionEvent));

}

#### **The Evaluation Loop**

In `Phase 4 (Sync Point)`, the Scheduler iterates modules.

// Inside Scheduler.ExecuteFrame()

foreach (var module in \_modules)

{

    bool shouldRun \= false;

    // 1\. Check Timer (e.g. 10Hz)

    if (Time.time \- module.LastRunTime \>= module.Interval)

    {

        // 2\. Check Triggers

        if (CheckTriggers(module, \_repo))

        {

            shouldRun \= true;

        }

    }

    if (shouldRun)

    {

        // Calculate Union Mask & Snapshot

        // Dispatch Task

        module.LastRunTick \= \_repo.GlobalVersion;

    }

}

// Helper

bool CheckTriggers(IModule module, EntityRepository repo)

{

    // A. Check Events (Transient)

    // Note: Events are frame-specific. If they exist this frame, we run.

    foreach (var evtType in module.Deps.WatchEvents)

    {

        // We need a non-generic access pattern here or Reflection

        // Optimization: Cache a delegate for "HasEvents\<T\>"

        if (repo.Bus.HasEvents(evtType)) return true;

    }

    // B. Check Components (Persistent)

    // Compare Table Version vs Module's Last Run Tick

    foreach (var compType in module.Deps.WatchComponents)

    {

        if (repo.HasComponentChanged(compType, module.LastRunTick)) return true;

    }

    return false;

}

---

### **3\. Edge Cases & "Gotchas"**

#### **A. The "Event Gap" (Frequency Mismatch)**

* **Scenario:** AI watches `ExplosionEvent`. AI runs at 10Hz.  
* **Frame 1:** Explosion happens. `HasEvents` is true. AI Scheduler checks timer... **Timer says "Not due yet"**. AI sleeps.  
* **Frame 2:** Event Bus clears. Explosion gone.  
* **Frame 6:** AI Timer is due. `HasEvents` is false.  
* **Result:** **AI missed the signal.**

**Solution:** If a module depends on **Events**, the Scheduler **MUST ignore the Timer** if an event is present.

* *Rule:* Events are high-priority interrupts.  
* *Revised Logic:*  
    
  bool timerDue \= (Time \- LastRun \>= Interval);  
    
  bool hasEvents \= CheckEvents(module);  
    
  if (hasEvents || (timerDue && CheckComponents(module)))   
    
  {  
    
      Run(module);  
    
  }

#### **B. Granularity Issues**

* **Scenario:** You modify `Position` on Entity 1\.  
* **Result:** The `Position` Table Version increments.  
* **Consequence:** The AI Module wakes up. It scans 100,000 entities. It realizes Entity 1 is irrelevant. It goes back to sleep.  
* **Cost:** High overhead for a false alarm.  
* **Mitigation:** This is acceptable. FDP optimizes for *Batch* processing. Waking up once to find nothing is better than polling 100,000 entities every frame when *nothing* changed globally.

### **Summary of Required Changes**

1. **Modify `ChunkTable`:** Add `uint _lastWriteTick`. Update it on every write.  
2. **Modify `EntityRepository`:** Expose `HasComponentChanged(Type, uint)`.  
3. **Modify `FdpEventBus`:** Expose `HasEvents(Type)`.  
4. **Scheduler:** Implement the logic to prioritize Event presence over Timer intervals to prevent missed signals.

This is a subtle but critical architectural decision.

The short answer: **Do NOT let modules check on their own.** Waking up a thread, context switching, and initializing a module just to check a version number and go back to sleep is wasteful (Thread Pool pressure).

**However**, implementing `LastWriteTick` naively inside the hot loop will kill your physics performance due to **Cache Contention (False Sharing)**.

Here is the performance-friendly way to implement this API.

---

### **1\. The Performance Trap: "False Sharing"**

If you implement `LastWriteTick` exactly as I described in the previous turn, you create a CPU bottleneck.

**The Naive Implementation:**

// Inside NativeChunkTable.Set() \- running on 12 different threads

public void Set(...) {

    // ... write data ...

    

    // TRAP: All 12 threads try to write to this ONE memory address simultaneously.

    // The CPU cores fight over ownership of this Cache Line.

    \_lastWriteTick \= globalVersion; 

}

**Result:** Your parallel physics job slows down significantly because cores are waiting on cache synchronization for that one variable.

---

### **2\. The Optimized Solution: "Lazy Scan"**

Instead of writing to a global variable during the **Hot Path** (Physics), we calculate the global change during the **Sync Phase** (Scheduler).

**Why this is better:**

* **Physics Loop:** Remains untouched. Zero overhead. It just updates `_chunkVersions[i]` (which is padded/safe).  
* **Scheduler Loop:** Scans the array of chunk versions.

**The Math:**

* **Scenario:** 100,000 entities $\\approx$ **100 Chunks**.  
* **Cost:** Scanning an array of 100 integers takes **nanoseconds** (it fits in L1 cache, CPU prefetches it perfectly).  
* **Verdict:** It is effectively free for the Scheduler to check.

**Implementation:**

// Fdp.Kernel/NativeChunkTable.cs

// New Method: Optimized for Scheduler

public bool HasChanges(uint sinceVersion)

{

    // Simple array scan

    // Optimization: Can use Vector\<int\> (SIMD) if you have 1000s of chunks

    for (int i \= 0; i \< \_chunkVersions.Length; i++)

    {

        if (\_chunkVersions\[i\].Value \> sinceVersion)

            return true;

    }

    return false;

}

---

### **3\. Comparison: Scheduler Check vs. Module Check**

| Feature | Scheduler Checks (Centralized) | Modules Check (Decentralized) |
| :---- | :---- | :---- |
| **CPU Cost** | **Microscopic.** (Scanning \~100 ints per table). | **High.** (Task Dispatch overhead \+ Context Switch). |
| **Thread Usage** | **Main Thread.** No wake-up if no work. | **Worker Thread.** Wakes up, checks, sleeps. |
| **Latency** | **Zero.** Decision made instantly. | **Variable.** Depends on ThreadPool availability. |
| **API Cleanliness** | **Clean.** `Scheduler` handles policy. | **Messy.** Module needs boilerplate "Early Exit" logic. |

**Winner:** **Scheduler Checks.** It prevents "Death by 1000 Papercuts" where 50 modules wake up 60 times a second just to realize they have nothing to do.

---

### **4\. Implementation Strategy for Events**

For **Events**, the performance characteristics are different. `ConcurrentDictionary` lookups are slower than array scans, but still faster than waking a thread.

**Optimization:** Cache the "Active Event Types" in a `BitMask` or `HashSet` inside `FdpEventBus` during the `Publish` phase.

public class FdpEventBus

{

    // A dirty flag for the whole bus

    private bool \_anyEventPublished \= false;

    

    // Optimization: A fast lookup for "Did Type ID 5 happen?"

    // BitMask256 is perfect if IDs \< 256, otherwise HashSet\<int\>

    private readonly HashSet\<int\> \_activeEventIds \= new();

    public void Publish\<T\>(T evt)

    {

        // ... write to stream ...

        

        if (\!\_anyEventPublished) 

        {

            \_activeEventIds.Add(EventType\<T\>.Id); // Hash Add is O(1)

            \_anyEventPublished \= true;

        }

        else if (\!\_activeEventIds.Contains(EventType\<T\>.Id))

        {

             \_activeEventIds.Add(EventType\<T\>.Id);

        }

    }

    public bool HasEvents(int typeId)

    {

        if (\!\_anyEventPublished) return false;

        return \_activeEventIds.Contains(typeId);

    }

    public void SwapBuffers()

    {

        // ... swap logic ...

        \_activeEventIds.Clear();

        \_anyEventPublished \= false;

    }

}

### **Final Recommendation**

1. **Do NOT implement `LastWriteTick` in the Table via writing in the Set/GetRW methods.** It causes cache contention.  
2. **Do implement `HasChanges(version)`** by scanning the chunk versions array. It is extremely fast.  
3. **Do implement `HasEvents(type)`** in the Bus.  
4. **Let the Scheduler drive.** Keep your Modules passive code blocks. This is crucial for scaling to Headless Servers where you might want to run 100 instances on one machine; you can't afford thousands of idle threads waking up constantly.
