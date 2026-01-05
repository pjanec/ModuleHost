# HOT-PATH OPTIMIZATION ANALYSIS

## 1. The Bottleneck
Profiling reveals that `EntityRepository.GetComponent<T>(Entity e)` is the dominant cost in tight loops.
Cost breakdown per call:
1. **Generic Lookup**: `ComponentType<T>.ID` (Fast, cached static).
2. **Table Lookup**: Dictionary/Array lookup to find `IComponentTable` for `T`.
3. **Type Cast**: `(ComponentTable<T>)table`.
4. **Interface Dispatch**: If accessing via `IComponentTable` (virtual call).
5. **Inner Logic**: `NativeChunkTable` index calculation (`id / capacity`, `id % capacity`).
6. **Chunk Check**: `IsChunkCommitted` bitmask check.

In a loop of 100k entities, steps 2-4 are repeated 100k times unnecessarily. The table never changes during the loop.

## 2. The Solution: Hoisting
By exposing `ComponentTable<T>`, systems can resolve steps 2-4 **once** before the loop.

**Before:**
```csharp
// Loop: 100k iterations
foreach (var e in query) {
   // O(1) Dictionary Lookup + Cast per entity
   repo.GetComponent<Pos>(e).X++; 
}
```

**After:**
```csharp
// Setup: Once
var table = repo.GetTable<Pos>(); 

// Loop: 100k iterations
foreach (var e in query) {
   // Pointer Math only
   table.Get(e.Index).X++;
}
```

## 3. Further Optimization: Span Slicing (Task-025)
Since data is stored in chunks, we can expose `GetSpan(chunkIndex)`.
Queries iterate by chunk. If `EntityQuery` exposes chunk indices, we can:
```csharp
foreach (var chunk in query.GetChunks()) {
    var span = table.GetSpan(chunk.Index);
    foreach (var i in chunk) {
        span[i].X++;
    }
}
```
*Note for Batch-08: We will focus effectively on hoisting the table lookup first. Span optimization is enabled by exposing `GetSpan` but requires `EntityQuery` updates to be fully utilized. For now, hoisting `GetTable` is the primary 6x gain.*

## 4. Expected Gains
- **Dictionary Lookup**: Eliminated (Hot path).
- **Type Cast**: Eliminated (Hot path).
- **Virtual Call**: Eliminated (Hot path).
- **Result**: ~6x-10x faster access time per entity.
