# BATCH-CK-FIX-01 Assignment

**Batch:** BATCH-CK-FIX-01  
**Priority:** 🔴 **URGENT - Architectural Corrections**  
**Assignee:** Developer  
**Estimated Time:** 2-3 hours  

---

## 📋 Context

The architect has reviewed BATCH-CK-07 and BATCH-CK-08 implementations and identified architectural violations that must be corrected before proceeding to BATCH-CK-09 and BATCH-CK-10.

**Root Cause:** Developer implemented reasonable workarounds for missing APIs, but some violate FDP's Data-Oriented Design principles.

---

## 🎯 What Needs to Be Fixed

### 1. **System Dependencies (CRITICAL)**
**Problem:** `CarKinematicsSystem` tried to access `SpatialHashSystem` via `GetSystem<T>()`.

**Why This Violates FDP:**
- Systems must NEVER reference other systems directly
- Creates tight coupling
- Breaks Data-Oriented Design

**The FDP Way:**
- Systems communicate through **Data** (Singletons, Components, Events)
- Producer system writes data to singleton
- Consumer system reads data from singleton

**Fix:** Use `SpatialGridData` singleton component

---

### 2. **FormationRoster Entity Storage (CRITICAL)**
**Problem:** Stores only entity IDs (`int`), not full Entity handles (ID + generation).

**Why This Is Unsafe:**
- Entity IDs alone can become stale (entity deleted, ID reused)
- Current workaround uses `GetHeader()` hack
- Not safe for production

**Fix:** Store `fixed long MemberEntities[16]` (full Entity handles)

---

### 3. **Parallel Iteration Performance**
**Problem:** Manually collecting entities to `List<Entity>` causes GC allocations.

**Why This Hurts Performance:**
- Allocates managed array/list every frame
- Triggers garbage collection
- Defeats zero-GC goal

**Fix:** Use FDP's built-in `ForEachParallel` (pooled batches)

---

### 4. **System Attributes**
**Problem:** Used non-existent `[SystemAttributes]`.

**Fix:** Use FDP Kernel attributes: `[UpdateInGroup]`, `[UpdateBefore]`, `[UpdateAfter]`

---

### 5. **SetComponent API**
**Problem:** Only `AddComponent` exists, semantically confusing for updates.

**Fix:** Add `SetComponent<T>()` as zero-overhead alias

---

## 📚 Required Reading

**MUST READ BEFORE STARTING:**
1. `D:\WORK\ModuleHost\docs\car-kinem-design-addendum.md` (Architect decisions)
2. `D:\WORK\ModuleHost\.dev-workstream\batches\BATCH-CK-FIX-01-INSTRUCTIONS.md` (Implementation guide)

---

## 🔑 Key Changes Summary

| Component | Change | Why |
|-----------|--------|-----|
| **EntityRepository** | Add `SetComponent<T>()` | API clarity |
| **SpatialGridData** | NEW singleton component | Data-Oriented pattern |
| **SpatialHashSystem** | Write to singleton | Remove system coupling |
| **CarKinematicsSystem** | Read from singleton | Remove system coupling |
| **CarKinematicsSystem** | Use `ForEachParallel` | Zero GC |
| **FormationRoster** | Store `Entity` not `int` | Safety |
| **FormationRosterExtensions** | Helper methods | Clean API |
| **FormationTargetSystem** | Use Entity helpers | Remove hacks |
| **All Systems** | Add FDP attributes | Declarative scheduling |

---

## ✅ Acceptance Criteria

**Critical:**
- [ ] No system references other systems directly
- [ ] Spatial grid accessed via singleton
- [ ] FormationRoster stores full Entity handles
- [ ] No manual entity collection (use `ForEachParallel`)

**Quality:**
- [ ] All 87 tests pass
- [ ] Zero warnings
- [ ] Zero GC allocations in hot path

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-FIX-01-REPORT.md`

Include:
- Confirmation of all changes
- Test results (all 87 passing)
- Performance notes (GC allocations before/after)

---

## 🚦 Impact on Schedule

**BLOCKS:** BATCH-CK-09, BATCH-CK-10 until complete

**Rationale:** Architectural violations must be fixed before integration and demo.

---

**This is not a failure - this is iterative design! The architect reviewed real working code and gave definitive guidance. Now we implement the FDP-correct patterns.** 💪
