# Architectural Decision Record: Snapshot-on-Demand over COW

**Decision ID:** ADR-001  
**Date:** January 3, 2026  
**Status:** ✅ ACCEPTED  
**Supersedes:** FDP-SST-001 Rev 2.0 Section 4 (COW Implementation)

---

## Context

We need a way for background modules (AI, UI, Analytics) to safely read FDP state while the main thread physics loop mutates it at 60Hz. We evaluated two approaches:

1. **Copy-On-Write (COW)** with ref-counted ManagedPages
2. **Snapshot-on-Demand (SoD)** with dirty chunk tracking

---

## Decision

**We are adopting Snapshot-on-Demand (SoD)** instead of kernel-level Copy-On-Write.

---

## Rationale

### Simplicity ✅
- **SoD:** Keeps FDP kernel unchanged - no pointer swizzling, no indirection
- **COW:** Requires rewriting component accessors, adding ManagedPage layer, complex fork logic

### Physics Hot Path Performance ✅
- **SoD:** Zero overhead - raw pointer access unchanged
- **COW:** Every write checks `RefCount > 1` (even when RefCount=1 99% of the time)

### Debugging Experience ✅
- **SoD:** Single source of truth - what you see in debugger is reality
- **COW:** Multiple versions of same data at different addresses - confusing

### Memory Allocator Compatibility ✅
- **SoD:** Works with existing `VirtualAlloc` sparse array design
- **COW:** Requires dynamic ManagedPage pool with fragmentation management

### Bandwidth Cost (Trade-off) ⚠️
- **SoD:** memcpy cost for changed chunks (~150MB/s at 10% entity activity)
- **COW:** Near-zero snapshot cost (just increment RefCount)

**Mitigation:** Dirty chunk tracking ensures sleeping entities (static buildings, parked vehicles) cost ZERO bandwidth. In practice, most entities are sleeping most of the time.

---

## Consequences

### What Stays Simple

1. **FDP Iterators:** No indirection - chunk addresses are stable within a frame
2. **Component Accessors:** Return raw `ref T` - no fork checks
3. **Memory Management:** Static reservation - no page pooling complexity
4. **Flight Recorder:** Already uses memcpy - no changes needed

### What Becomes Critical

1. **Sync Point Constraint:**  
   - Must pause main thread to take snapshots (prevent torn reads)
   - Constraint: Sync point must complete in <2ms (60Hz = 16.67ms budget)

2. **Dirty Chunk Tracking:**  
   - **Every** write (including raw pointer writes) must update `_chunkVersions`
   - This is enforced by FDP's existing dirty tracking for Flight Recorder

3. **Tier 2 Immutability Still Required:**  
   - Even without COW, Tier 2 uses reference sharing
   -Records must be immutable to prevent snapshot corruption

---

## Performance Analysis

### Tier 1 (Unmanaged Structs):

| Scenario | Entities | Active% | Bandwidth | Notes |
|----------|----------|---------|-----------|-------|
| Static world | 100K | 0% | **0 MB/s** | Dirty tracking magic |
| Normal | 100K | 10% | ~150 MB/s | Acceptable |
| Heavy combat | 100K | 30% | ~450 MB/s | Still 1.8% of DDR4 bandwidth (25GB/s) |

**Verdict:** Acceptable. Modern memory is fast enough.

### Tier 2 (Managed Records):

- **Cost:** `Array.Copy` of object references (~10μs for 1000 entities)
- **Memory:** ArrayPool reuse - no allocations
- **Safety:** Immutable records prevent shared object mutation

---

## Comparison to COW

| Aspect | COW (Ref-Counted) | SoD (Selected) |
|--------|-------------------|----------------|
| **Snapshot Cost** | O(1) increment | O(N dirty chunks) memcpy |
| **Write Cost (Tier 1)** | `if (RefCount>1)` branch | **Zero** |
| **Write Cost (Tier 2)** | Fork + shallow copy (~50μs) | Array pointer swap |
| **Memory Overhead** | RefCount per page (~8B) | Shadow buffer per consumer |
| **Kernel Complexity** | **HIGH** (pointer swizzling) | **LOW** (direct memcpy) |
| **Debug Experience** | Confusing (multiple versions) | **Clear** (single truth) |

---

## Implementation Impact

### FDP-SST-001 Changes

**Removed Sections:**
- 4.2: ManagedPage<T> structure
- 4.3: COW algorithm (RefCount increment/fork)
- 4.6: Fork trigger in accessors

**Added Sections:**
- 4.1: SoD decision rationale
- 4.3: Shadow buffer with dirty tracking
- 4.4: Reference array copy (Tier 2)
- 4.5: Sync point constraint

### Implementation Checklist Updates

**Phase 1 (Was: Add ManagedPage COW):**
- ~~Add `ManagedPage<T>` with RefCount~~
- ~~Implement write barrier~~
- ~~Implement ForkPage logic~~
- ✅ **NEW:** Implement `SnapshotManager` with shadow buffers
- ✅ **NEW:** Add dirty chunk version tracking (already exists!)
- ✅ **NEW:** Implement `CreateSnapshot(ComponentMask)` API

**Phase 2 (Minimal Changes):**
- Add sync point to Host Kernel `RunFrame()`
- Integrate `SnapshotManager.CreateSnapshot()` call
- No changes to module APIs (they already use `ISimWorldSnapshot`)

---

## Risks & Mitigations

### Risk 1: Sync Point Takes Too Long
**Symptom:** Snapshot memcpy takes >2ms → frame drops below 60Hz  
**Mitigation:**  
- Monitor per-frame snapshot time
- If >1.5ms, warn "Too many active entities"
- Optimize: SIMD-accelerated memcpy
- Ultimate: Stagger background module updates (not all run same frame)

### Risk 2: Dirty Tracking Missed Writes
**Symptom:** Snapshot shows stale data  
**Mitigation:**  
- FDP already tracks dirty state for Flight Recorder
- Raw pointer writes **must** mark chunks dirty
- Add debug validation: Compare shadow vs live after sync

### Risk 3: Tier 2 Mutability Violation
**Symptom:** Snapshot corruption (shared object mutated)  
**Mitigation:**  
- Enforce `record` types via Roslyn analyzer
- Runtime check: `if (obj is not ImmutableObject) throw`
- Code review checklist

---

## Alternatives Considered

### 1. Lock-Based Synchronization
**Rejected:** Blocking main thread with locks kills performance

### 2. Double-Buffering Everything
**Rejected:** 2x memory cost + still need sync point to swap buffers

### 3. OS-Level COW (fork() process)
**Rejected:** Windows doesn't support fork; Linux COW is not portable

### 4. Lock-Free Data Structures
**Rejected:** Too complex for large component arrays; limited to specific types

---

## Validation Criteria

### Success Metrics:
- ✅ Sync point completes in <2ms (99th percentile)
- ✅ Zero GC allocations in steady state
- ✅ Physics loop remains at <10ms (60Hz)
- ✅ Background modules get consistent snapshots
- ✅ No snapshot corruption detected

### Test Cases:
1. **Dirty Tracking Test:** Modify 10% entities, verify only those chunks copied
2. **Sleeping Entities Test:** 90K static entities → verify 0 bandwidth
3. **Concurrent Access Test:** Background module reads while main thread writes
4. **Tier 2 Immutability Test:** Attempt mutation → verify snapshot unaffected
5. **Sync Point Budget Test:** Measure actual memcpy time under load

---

## References

- **Whitepaper:** `docs/B-One-FDP-Data-Lake.md` (Part 4: "The Data Concurrency Challenge")
- **Updated Spec:** `docs/FDP-SST-001-Integration-Architecture.md` (Section 4)
- **Original COW Proposal:** FDP-SST-001 Rev 2.0 (Superseded)

---

## Sign-Off

**Approved By:** Architecture Team  
**Implementation Start Date:** TBD (awaiting FDP-SST-001 review)  
**Estimated Impact:** ~2 weeks FDP kernel changes, 1 week Host integration

**Next Actions:**
1. Update implementation checklist
2. Prototype shadow buffer SnapshotManager
3. Benchmark sync point timing
4. Implement dirty chunk optimization for Tier 1

---

**Status:** This decision is **FINAL** and supersedes all previous COW-based designs.
