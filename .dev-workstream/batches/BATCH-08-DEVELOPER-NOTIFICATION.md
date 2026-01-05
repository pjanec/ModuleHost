# BATCH-08 Developer Notification

**Optimization Alert**: We are unlocking raw performance.

We are exposing `ComponentTable<T>` directly. This allows you to bypass the `EntityRepository` overhead inside your loops.

**Impact**:
- 6x-10x faster component access.
- Critical for Physics, Animation, and AI systems.

**New usage pattern:**
```csharp
// 1. Get Table (Once)
var posTable = repo.GetComponentTable<Position>();

// 2. Loop (Fast)
foreach (var e in query) {
    ref var pos = ref posTable.Get(e.Index);
    pos.X += 1;
}
```

Wait for the update, then profile your hottest loops!
