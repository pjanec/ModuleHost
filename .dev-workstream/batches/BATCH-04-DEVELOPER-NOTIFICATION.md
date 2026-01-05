# Developer Assignment: BATCH-04

**Date:** January 4, 2026  
**From:** Development Leader  
**To:** Developer

---

## 🎉 Outstanding Work on BATCH-03!

Your Snapshot Provider implementation was exceptional. You completed the optional SharedSnapshotProvider, added SoftClear proactively, and created an elegant schema setup pattern. This was the largest batch (33 SP) and you delivered flawlessly. Excellent work!

---

## 📋 Next Assignment: BATCH-04 - ModuleHost Integration

**Batch Focus:** Orchestration Layer

**Tasks:** 3 (TASK-012 through TASK-014)  
**Story Points:** 16  
**Estimated Duration:** 3-4 days

**Instructions:**  
**`d:\WORK\ModuleHost\.dev-workstream\batches\BATCH-04-INSTRUCTIONS.md`**

---

## ⚠️ IMPORTANT REMINDER

**Before you start coding, read:**

1. **`.dev-workstream/README.md`** - Your workflow guide
2. **`BATCH-04-INSTRUCTIONS.md`** - This batch's specific tasks

**This is mandatory for every batch!**

---

## 🎯 What You're Building

**ModuleHost Orchestration System:**

1. **IModule Interface** - Contract for all background modules
2. **ModuleHostKernel** - Central orchestrator managing module lifecycle
3. **FDP Integration** - Example showing how it all connects

**Key Challenges:**
- Module registration and provider assignment
- Execution frequency management (Fast every frame, Slow every N frames)
- Thread-safe async dispatch
- View lifecycle (acquire → tick → release)
- Integration with FDP simulation loop

---

## 🔍 Focus Areas

This batch brings together everything from BATCH-01, 02, and 03:

1. **BATCH-01** - SyncFrom enables provider updates
2. **BATCH-02** - ISimulationView is what modules receive
3. **BATCH-03** - Providers are assigned to modules
4. **BATCH-04** - Orchestrator connects all the pieces

**Architecture:**
```
Simulation Loop (Main Thread)
    ↓
ModuleHostKernel.Update()
    ↓ (captures events)
    ↓ (syncs providers)
    ↓
Dispatch Modules (Background Threads)
    Module.Tick(ISimulationView)
    ↓
Release Views
```

---

## 💡 Key Design Points

### Module Tiers

**Fast Tier (GDB):**
- Runs every frame
- Uses DoubleBufferProvider
- Low latency, zero-copy
- Examples: Network, Recorder, InputHandler

**Slow Tier (SoD):**
- Runs every N frames (UpdateFrequency)
- Uses OnDemandProvider or SharedSnapshotProvider
- Higher latency, pooled snapshots
- Examples: AI, Analytics, Pathfinding

### Execution Pipeline

```csharp
// Main thread
kernel.Update(deltaTime);
    ↓
// 1. Capture events
eventAccumulator.CaptureFrame(liveWorld.Bus, frame);
    ↓
// 2. Update providers
foreach (provider)
    provider.Update(); // Syncs replicas/snapshots
    ↓
// 3. Dispatch modules
foreach (module)
    if (ShouldRunThisFrame(module))
        view = provider.AcquireView();
        Task.Run(() => module.Tick(view, deltaTime));
    ↓
// 4. Wait for completion
Task.WaitAll(tasks);
```

---

## 📊 Complexity Assessment

**Story Points:** 16 (Medium complexity)

**Compared to previous batches:**
- BATCH-01: 21 SP (foundation, dirty tracking)
- BATCH-02: 13 SP (event system, interface)
- BATCH-03: 33 SP (strategy pattern, 3 providers) ← Largest
- BATCH-04: 16 SP (orchestration) ← Smaller than BATCH-03

**Good news:** This is smaller than BATCH-03, and you're building on solid foundations you've already created.

---

## ⚠️ Critical Rules

**Thread Safety:**
1. ⛔ Provider.Update() ONLY on main thread
2. ✅ AcquireView() can be called from module threads
3. ✅ Module.Tick() runs on background threads
4. ⛔ Modules must NOT modify live world (read-only)

**View Lifecycle:**
1. ✅ Always ReleaseView in finally block
2. ⛔ Never hold view across frames
3. ✅ Release even if module throws exception

**Execution Frequency:**
1. ✅ Fast tier: Every frame (UpdateFrequency ignored)
2. ✅ Slow tier: Every N frames (UpdateFrequency = N)
3. ⛔ UpdateFrequency must be ≥ 1

---

## 📝 Deliverables

**When complete:**

**Submit:** `reports/BATCH-04-REPORT.md`

**Include:**
- Status of all 3 tasks
- Test results (12 unit + 2 integration = 14 tests)
- Example code execution results
- Performance measurements (Update() overhead)
- Files created/modified

**If blocked or questions:**
- **Blockers:** Update `reports/BLOCKERS-ACTIVE.md` immediately
- **Questions:** Create `reports/BATCH-04-QUESTIONS.md`

---

## 🎯 Success Criteria

1. ✅ IModule interface clean and simple
2. ✅ ModuleHostKernel manages lifecycle correctly
3. ✅ Provider assignment works (Fast → GDB, Slow → SoD)
4. ✅ Execution frequency correct (Fast every frame, Slow every N)
5. ✅ View lifecycle proper (acquire → tick → release)
6. ✅ Integration example runs successfully
7. ✅ Zero compiler warnings
8. ✅ All tests pass

---

## 🚀 Next Steps

1. **Read** `.dev-workstream/README.md` (refresh memory)
2. **Read** `BATCH-04-INSTRUCTIONS.md` (full task details)
3. **Review** BATCH-03 providers (understand lifecycle)
4. **Start** with TASK-012 (IModule interface - simple)
5. **Submit** report when done

---

## 💡 Tips from Previous Batches

**What works well:**
- ✅ Start with simplest task (interface definition)
- ✅ Build incrementally (interface → kernel → integration)
- ✅ Test frequently
- ✅ Proactive problem-solving (like you did with SoftClear)

**This batch:**
- Focus on getting the execution pipeline right
- Test frequency logic carefully (off-by-one errors)
- Ensure view lifecycle (acquire/release) always balanced
- Integration example is key to validation

---

## 🎯 Looking Ahead

**After BATCH-04:**
- BATCH-05 (Final): Command buffer, final tests, performance validation
- Then: Production-ready hybrid architecture! 🎉

You're almost there - only 2 batches left!

---

**Good luck, and feel free to ask questions early!**

---

**Development Leader**  
January 4, 2026
