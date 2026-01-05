# BATCH-08: ECS Hot-Path Optimizations

## Overview
Implement APIs to allow hoisting of ComponentTable lookups out of hot loops. This eliminates the overhead of Dictionary lookups, interface dispatch, and type casting for every entity.

## Task Breakdown

### TASK-024: Make ComponentTable Public
**File**: `Fdp.Kernel/ComponentTable.cs`
- Change class visibility from `internal` (or implicitly internal properties) to `public`.
- Ensure `Get`, `GetRW`, `GetRO` are public and aggressively inlined.

### TASK-025: Add Fast-Path APIs
**File**: `Fdp.Kernel/EntityRepository.cs`
- Add `public ComponentTable<T> GetComponentTable<T>()`.
  - Should look up the table once and cast it.
  - Throw exception if `T` is not registered.

**File**: `Fdp.Kernel/ComponentTable.cs`
- Add `public Span<T> GetSpan(int chunkIndex)`.
  - For advanced users (like Query iterators).
  - Should return `Span<T>` wrapping the specific chunk memory.

### TASK-026: Benchmarks
**File**: `Fdp.Tests/Benchmarks/FastPathBenchmarks.cs`
- Compare:
  1. `Standard`: `repo.GetComponent<T>(e)` in loop.
  2. `Hoisted`: `table.Get(e.Index)` in loop.
  3. `Span`: `table.GetSpan(chunk)` (Optional, if feasible to mock chunks).
- **Goal**: Hoisted must be >6x faster than Standard.

### TASK-027: Update Documentation
**File**: `docs/PERFORMANCE.md`
- Add section "Hot Path Optimization".
- Show code samples for Hoisting.
- Explain when to use it (Tight loops > 1000 entities).

## Verification
- Run `Fdp.Tests` to ensure no regressions.
- Run `FastPathBenchmarks` and capture output.
