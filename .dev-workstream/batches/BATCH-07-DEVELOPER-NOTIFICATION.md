# BATCH-07 DEVELOPER NOTIFICATION
## Critical Performance Upgrade: Zero-Alloc Query Iteration

**Date**: 2026-01-05
**Priority**: P1 (High)
**Status**: Ready for Implementation

### 1. The Problem: Hidden Allocations
We have identified a significant performance bottleneck in the `EntityQuery.ForEach` method. Currently, every call to `ForEach(e => ...)` creates a temporary delegate and a closure object on the heap.

- **Impact**: In a simulation with 50 systems running at 60 FPS, this generates **~180,000 allocations per minute**.
- **Consequence**: This causes frequent Gen0 Garbage Collection pauses, causing micro-stutters that violate our <16ms frame budget.
- **Secondary Issue**: Delegate calls prevent the JIT compiler from "inlining" your system logic, adding call overhead to every single entity iteration.

### 2. The Solution: `EntityEnumerator`
We are introducing a custom `ref struct` enumerator that allows you to use standard C# `foreach` syntax with **zero allocations** and **full inlining support**.

### 3. Migration Guide

You should migrate your hot loops from `ForEach` to `foreach`.

**❌ BEFORE (Allocates memory, slower):**
```csharp
var query = _repo.Query().With<Position>().Build();

// This lambda creates garbage every frame!
query.ForEach(e => 
{
    ref var pos = ref _repo.GetComponentRW<Position>(e);
    pos.X += dt;
});
```

**✅ AFTER (Zero allocation, 2-3x faster):**
```csharp
var query = _repo.Query().With<Position>().Build();

// No allocations. Logic is inlined directly into the loop.
foreach (var e in query)
{
    ref var pos = ref _repo.GetComponentRW<Position>(e);
    pos.X += dt;
}
```

### 4. Technical Details
- The new `GetEnumerator()` returns a `ref struct`, which guarantees it lives only on the stack.
- The `MoveNext()` method inlines all filter checks (BitMasks, Version checks).
- The compiler treats this exactly like a raw `for` loop over an array, but with the safety and convenience of ECS filtering.

### 5. Action Required
1. **Pull BATCH-07** when released.
2. **run tests** to verify `EntityQuery` stability.
3. **Update your systems** to use `foreach` where possible.
4. Note that `ForEach` is now marked `[Obsolete]` but will continue to work for backward compatibility.
