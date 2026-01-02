# Developer Assignment: BATCH-03

**Date:** January 4, 2026  
**From:** Development Leader  
**To:** Developer

---

## 🎉 Excellent Work on BATCH-02!

Your EventAccumulator and ISimulationView implementation was outstanding. Zero issues found, clean architecture, and proactive bug fixes. Well done!

---

## 📋 Next Assignment: BATCH-03 - Snapshot Providers

**Batch Focus:** Strategy Pattern Implementation

**Tasks:** 4 (TASK-008 through TASK-011)  
**Story Points:** 33 (largest batch so far)  
**Estimated Duration:** 5-6 days

**Instructions:**  
**`d:\WORK\ModuleHost\.dev-workstream\batches\BATCH-03-INSTRUCTIONS.md`**

---

## ⚠️ IMPORTANT REMINDER

**Before you start coding, read:**

1. **`.dev-workstream/README.md`** - Your workflow guide
   - Definition of Done checklist
   - Critical rules (NO warnings, ALL tests pass)
   - Code review checklist
   - Communication protocols

2. **`BATCH-03-INSTRUCTIONS.md`** - This batch's specific tasks

**This is mandatory for every batch!** Don't skip the README review.

---

## 🎯 What You're Building

**Strategy Pattern for Snapshot Provisioning:**

1. **ISnapshotProvider** - Interface abstracting GDB/SoD/Shared strategies
2. **DoubleBufferProvider** - GDB strategy (persistent replica, zero-copy)
3. **OnDemandProvider** - SoD strategy (pooled snapshots, filtered sync)
4. **SharedSnapshotProvider** - Convoy pattern (reference counting) - **P1, can defer if needed**

**Key Challenges:**
- Clean abstraction through interface
- Zero overhead for GDB (direct cast to ISimulationView)
- Pooling for SoD (no allocations per frame)
- Reference counting for Shared provider

---

## 📊 Complexity Note

This is the **largest batch** (33 SP vs 21 SP in BATCH-01, 13 SP in BATCH-02).

**Options if you hit complexity:**
1. **Recommended:** Complete TASK-008, 009, 010 (25 SP) - Core providers
2. **Defer:** TASK-011 (SharedSnapshotProvider, 8 SP, P1) to BATCH-04 if needed

**Prioritize quality over speed.** It's better to deliver 3 excellent providers than 4 rushed ones.

---

## 🔍 Focus Areas

Based on BATCH-02 success, emphasize:

1. **✅ Clean abstractions** - ISnapshotProvider must be simple
2. **✅ Buffer pooling** - You did this well in EventAccumulator, apply here
3. **✅ Thread safety** - Phase-based execution (see instructions)
4. **✅ Performance** - GDB <2ms, SoD <500μs, Shared <100μs (reuse)
5. **✅ Testing** - Comprehensive unit + integration tests

---

## 💡 Tips from Previous Batches

**What worked well:**
- ✅ Starting with simplest task (interface) first
- ✅ Proactive bug fixes (like InjectIntoCurrent append)
- ✅ Good architectural decisions (ISimulationView location)
- ✅ Thorough regression testing

**Continue this approach!**

---

## 📝 Deliverables

**When complete (or if deferring TASK-011):**

**Submit:** `reports/BATCH-03-REPORT.md`

**Include:**
- Status of all tasks (mark if TASK-011 deferred)
- Test results (aim for 23 unit + 1 integration, or 17 + 1 if deferred)
- Performance benchmarks
- Files created/modified
- Reasoning if anything deferred

**If blocked or questions:**
- **Blockers:** Update `reports/BLOCKERS-ACTIVE.md` immediately
- **Questions:** Create `reports/BATCH-03-QUESTIONS.md`

---

## 🎯 Success Criteria

1. ✅ All providers implement ISnapshotProvider correctly
2. ✅ GDB provider is zero-overhead (direct cast)
3. ✅ SoD provider uses pooling (no allocations)
4. ✅ Integration test shows all providers work
5. ✅ Zero compiler warnings
6. ✅ Performance targets met

---

## 🚀 Next Steps

1. **Read** `.dev-workstream/README.md` (refresh your memory)
2. **Read** `BATCH-03-INSTRUCTIONS.md` (full task details)
3. **Review** referenced design docs (API-REFERENCE.md, etc.)
4. **Start** with TASK-008 (Interface definition)
5. **Submit** report when done

---

**Good luck, and feel free to ask questions early!**

**Remember:** Quality over speed. 33 SP is a lot - take your time to get it right.

---

**Development Leader**  
January 4, 2026
