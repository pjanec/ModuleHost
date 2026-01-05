# Performance Analysis: ModuleHost

## Summary
The system meets the performance targets for BATCH-05. The architecture successfully decouples high-frequency simulation from complex logic modules without significant overhead.

## Benchmark Results
*(Derived from `ModuleHost.Benchmarks` suite)*

### 1. Snapshot Synchronization (Write Path)
**Scenario**: Syncing 10,000 entities from Live to Replica (GDB).
- **Time**: ~150us (micro-seconds) for delta sync.
- **Allocation**: Zero (amortized) due to internal buffer reuse.
- **Analysis**: The `SyncFrom` operation is highly optimized using unmanaged memory copies for component data.

### 2. Event Capture
**Scenario**: Capturing 100 events/frame.
- **Time**: < 5us.
- **Analysis**: Circular buffer in `NativeEventStream` ensures O(1) capture time. `EventAccumulator` overhead is negligible.

### 3. Command Playback
**Scenario**: Creating 100 entities via Command Buffer.
- **Time**: ~20us.
- **Analysis**: Command buffer replay is effectively a bulk-insert operation. Thread-local barriers prevent lock contention during recording.

## Optimization Strategies

### A. Zero-Copy Providers
The `DoubleBufferProvider` avoids recreating the entire world state. It maintains a persistent replica and only copies "dirty" chunks. This is critical for scaling to 100k+ entities.

### B. Filtered Snapshots
The `OnDemandProvider` allocates tables only for requested components (`BitMask256`).
- **Benefit**: A module needing only `Position` ignores `Health`, `Inventory`, `AIState`, saving massive amounts of memory bandwidth.

### C. Thread Safety without Locks
- **Read Path**: Snapshots are isolated. No locks needed during module `Tick`.
- **Write Path**: `ThreadLocal<EntityCommandBuffer>` allows parallel recording. Locks are only taken once per frame during the Playback phase (Main Thread).

## Latency
- **Input Lag**: 1 Frame (Modules react to frame N-1 state).
- **Throughput**: Scalable with CPU cores (Modules run on Thread Pool).

## Conclusion
The Hybrid Architecture introduces minimal overhead (< 0.5ms per frame overhead for orchestration) while enabling massive horizontal scaling for game logic.

## Iteration Best Practices (BATCH-07 Update)

### 🚀 Use `foreach (var e in query)`
**Do NOT use `query.ForEach(e => ...)`**. The lambda version allocates a closure on the heap every frame, causing significant GC pressure in hot loops.

| Method | Time (10k entities) | Allocations | Status |
|--------|--------------------|-------------|--------|
| `ForEach(lambda)` | ~150 μs | 80 bytes | ❌ AVOID |
| `foreach (var e)` | ~50 μs | **0 bytes** | ✅ USE THIS |

**Example:**

```csharp
// ❌ BAD: Allocates closure every frame
query.ForEach(e => {
    ref var pos = ref repo.GetComponentRW<Position>(e);
    pos.X++;
});

// ✅ GOOD: Zero allocation, struct enumerator inlined
foreach (var e in query)
{
    ref var pos = ref repo.GetComponentRW<Position>(e);
    pos.X++;
}
```

The `EntityEnumerator` (introduced in BATCH-07) is a `ref struct` that fully inlines all BitMask checks, resulting in 3x faster iteration for simple loops.

## Hot Path Optimization (BATCH-08 Update)

### 🔥 Hoist Component Table Lookups

For **tight loops** processing >1000 entities, you can eliminate Dictionary lookup overhead by hoisting the `ComponentTable` lookup outside the loop.

**Performance Impact**: 2-3x faster component access in hot loops.

| Pattern | Time (100k entities) | Overhead | Status |
|---------|---------------------|----------|--------|
| `repo.GetComponentRW<T>(e)` | ~13 ms | Dict lookup + validation | ⚠️ SLOW |
| `table.Get(e.Index)` | ~5.6 ms | Direct access | ✅ FAST |

**Example:**

```csharp
// ❌ SLOW: Dictionary lookup every iteration
foreach (var e in query)
{
    ref var pos = ref repo.GetComponentRW<Position>(e);
    pos.X += velocity.X * dt;
}

// ✅ FAST: Hoist table lookup
var posTable = repo.GetComponentTable<Position>();
foreach (var e in query)
{
    ref var pos = ref posTable.Get(e.Index);
    pos.X += velocity.X * dt;
}
```

**⚠️ Important Notes:**
- `table.Get(e.Index)` does **NOT** validate entity liveness or component existence
- Only use after filtering entities via `EntityQuery`
- Does **NOT** update change tracking versions (use `table.GetRW(e.Index, version)` if needed)
- Best for physics, animation, and AI systems processing thousands of entities per frame

### Advanced: Chunk-Level Access

For maximum performance, you can access entire chunks as `Span<T>`:

```csharp
var table = repo.GetComponentTable<Position>();
int chunkIndex = entity.Index / 1024; // Assuming 1024 entities per chunk
Span<Position> positions = table.GetSpan(chunkIndex);

// Process entire chunk with SIMD-friendly access
for (int i = 0; i < positions.Length; i++)
{
    positions[i].X += 1.0f;
}
```

**When to use Hot Path APIs:**
- ✅ Physics systems (10k+ entities)
- ✅ Animation systems (5k+ entities)
- ✅ AI pathfinding (1k+ entities)
- ❌ UI systems (< 100 entities)
- ❌ One-time initialization code

