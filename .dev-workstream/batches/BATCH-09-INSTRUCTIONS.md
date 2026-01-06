# BATCH-09 Instructions: Command Buffer Playback Optimization

**Assigned:** Developer  
**Date:** January 6, 2026  
**Estimated:** 3 SP  
**Priority:** 🟡 MEDIUM - Performance optimization for production scale

---

## Overview

Optimize `EntityCommandBuffer` playback for high-throughput scenarios (network ingress, 5,000+ updates/frame). Current implementation has dictionary lookup + lock overhead per command. Optimize using direct array lookup.

**Performance Goal:** 3-4x faster playback (2ms → 0.6ms for 5,000 updates)

**Reference:** `.dev-workstream/reviews/COMMAND-BUFFER-PLAYBACK-OPTIMIZATION.md`

---

## Problem Analysis

### Current Bottleneck

**In `EntityRepository.SetComponentRaw()` (called per command):**
```csharp
internal unsafe void SetComponentRaw(Entity entity, int typeId, IntPtr dataPtr, int size)
{
    // 1. ❌ LOCK: ComponentTypeRegistry lookup (thread-safe dictionary)
    Type? componentType = ComponentTypeRegistry.GetType(typeId);
    
    // 2. ❌ HASH: Dictionary lookup (Type → Table)
    if (!_componentTables.TryGetValue(componentType, out var table)) ...
    
    // 3. ✅ FAST: Actual memory copy
    table.SetRaw(...);
}
```

**At Scale:**
- 5,000 commands = 5,000 locks + 5,000 dictionary lookups
- **~2ms overhead** on main thread

---

## Solution: Direct Array Lookup

**Replace:** `TypeId → Type → Dictionary → Table` (O(1) with overhead)  
**With:** `TypeId →Array → Table` (O(1) pure array access)

---

## Implementation

### TASK-028: Add Table Cache Array (1 SP)

**File:** `Fdp.Kernel/EntityRepository.cs`

**Add field (after line ~20-30):**

```csharp
private readonly Dictionary<Type, IComponentTable> _componentTables = new();

// ⭐ NEW: Fast lookup cache (array indexed by ComponentType.ID)
private IComponentTable?[] _tableCache = new IComponentTable[FdpConfig.MAX_COMPONENT_TYPES];
```

**Update `GetTable<T>()` method (around line 100-150):**

```csharp
public ComponentTable<T> GetTable<T>(bool allowCreate = false) where T : struct
{
    int typeId = ComponentType<T>.ID;
    Type type = typeof(T);
    
    if (!_componentTables.TryGetValue(type, out var table))
    {
        if (!allowCreate) return null;
        
        // Create new table
        var newTable = new ComponentTable<T>(this);
        _componentTables[type] = newTable;
        
        // ⭐ UPDATE CACHE
        if (typeId < _tableCache.Length)
        {
            _tableCache[typeId] = newTable;
        }
        else
        {
            // Expand cache if needed
            Array.Resize(ref _tableCache, typeId + 1);
            _tableCache[typeId] = newTable;
        }
        
        return newTable;
    }
    
    return (ComponentTable<T>)table;
}
```

**Also update managed component table registration** (if `GetManagedTable()` exists):

```csharp
public ManagedComponentTable<T> GetManagedTable<T>(bool allowCreate = false) where T : class
{
    // ... existing code ...
    
    // After creating table
    _componentTables[type] = newTable;
    
    // ⭐ UPDATE CACHE
    int typeId = ComponentType<T>.ID;
    if (typeId < _tableCache.Length)
    {
        _tableCache[typeId] = newTable;
    }
    else
    {
        Array.Resize(ref _tableCache, typeId + 1);
        _tableCache[typeId] = newTable;
    }
}
```

---

### TASK-029: Implement SetComponentRawFast() (1 SP)

**File:** `Fdp.Kernel/EntityRepository.cs`

**Add method (after existing `SetComponentRaw()`):**

```csharp
/// <summary>
/// OPTIMIZED: Fast path for command buffer playback.
/// Uses direct array lookup instead of dictionary + lock.
/// </summary>
internal unsafe void SetComponentRawFast(Entity entity, int typeId, IntPtr dataPtr, int size)
{
    // O(1) array access - NO LOCKS, NO HASHING
    if (typeId >= _tableCache.Length || _tableCache[typeId] == null)
    {
        #if DEBUG || FDP_PARANOID_MODE
        throw new InvalidOperationException(
            $"Component type {typeId} not registered. " +
            $"All components must be registered before command buffer playback.");
        #else
        // In release: fallback to slow path (should never happen in production)
        SetComponentRaw(entity, typeId, dataPtr, size);
        return;
        #endif
    }
    
    var table = _tableCache[typeId];
    
    // Direct memory copy
    table.SetRaw(entity.Index, dataPtr, size, _globalVersion);
    
    // Update component mask (if needed - check existing SetComponentRaw implementation)
    // UpdateComponentMask(entity, typeId); // If this method exists
}
```

**Update existing `SetComponentRaw()` to use fast path:**

```csharp
internal unsafe void SetComponentRaw(Entity entity, int typeId, IntPtr dataPtr, int size)
{
    // Use fast path if cache available
    if (typeId < _tableCache.Length && _tableCache[typeId] != null)
    {
        SetComponentRawFast(entity, typeId, dataPtr, size);
        return;
    }
    
    // Fallback: original slow path (for late-registered components)
    Type? componentType = ComponentTypeRegistry.GetType(typeId);
    if (componentType == null)
        throw new InvalidOperationException($"Unknown component type ID: {typeId}");
    
    if (!_componentTables.TryGetValue(componentType, out var table))
        throw new InvalidOperationException($"Component {componentType.Name} not registered");
    
    table.SetRaw(entity.Index, dataPtr, size, _globalVersion);
}
```

**Update `EntityCommandBuffer.Playback()`:**

**File:** `Fdp.Kernel/EntityCommandBuffer.cs`

**Find `Playback()` method (around line 200-300), update SetComponent case:**

```csharp
public unsafe void Playback(EntityRepository repo)
{
    int readPos = 0;
    
    while (readPos < _position)
    {
        var opCode = (OpCode)_buffer[readPos++];
        
        switch (opCode)
        {
            case OpCode.SetComponent:
            {
                Entity entity = ReadEntity(ref readPos);
                int typeId = ReadInt(ref readPos);
                int size = ReadInt(ref readPos);
                
                fixed (byte* ptr = &_buffer[readPos])
                {
                    // ⭐ Use optimized fast path
                    repo.SetComponentRawFast(entity, typeId, (IntPtr)ptr, size);
                }
                
                readPos += size;
                break;
            }
            
            // ... other opcodes unchanged ...
        }
    }
}
```

---

### TASK-030: Benchmarks and Verification (1 SP)

**File:** Create `Fdp.Benchmarks/CommandBufferPlaybackBenchmarks.cs`

```csharp
using System;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Fdp.Kernel;

namespace Fdp.Benchmarks
{
    [MemoryDiagnoser]
    public class CommandBufferPlaybackBenchmarks
    {
        private EntityRepository _repo;
        private EntityCommandBuffer _cmd;
        private const int UpdateCount = 5000;
        
        [GlobalSetup]
        public void Setup()
        {
            _repo = new EntityRepository();
            
            // Register components
            _repo.GetTable<Position>(allowCreate: true);
            _repo.GetTable<Velocity>(allowCreate: true);
            
            // Create entities
            for (int i = 0; i < UpdateCount; i++)
            {
                var e = _repo.CreateEntity();
                _repo.AddComponent(e, new Position { X = i, Y = i });
            }
            
            // Pre-allocate command buffer
            _cmd = new EntityCommandBuffer(UpdateCount * 32); // ~160KB
        }
        
        [Benchmark]
        public void PlaybackBenchmark_5000Updates()
        {
            // Record commands
            for (int i = 0; i < UpdateCount; i++)
            {
                _cmd.SetComponent(new Entity(i), new Position { X = i + 1, Y = i + 1 });
            }
            
            // Playback (this is what we're measuring)
            _cmd.Playback(_repo);
            
            // Clear for next iteration
            _cmd.Clear();
        }
        
        [Benchmark]
        public void PlaybackBenchmark_10000Updates()
        {
            const int count = 10000;
            
            for (int i = 0; i < count; i++)
            {
                if (i < UpdateCount)
                    _cmd.SetComponent(new Entity(i), new Position { X = i + 1, Y = i + 1 });
            }
            
            _cmd.Playback(_repo);
            _cmd.Clear();
        }
    }
    
    struct Position
    {
        public float X, Y;
    }
    
    struct Velocity
    {
        public float Vx, Vy;
    }
}
```

**Run benchmarks:**

```powershell
cd d:\WORK\ModuleHost\Fdp.Benchmarks
dotnet run -c Release
```

**Expected Results:**

**Before optimization:**
```
| Method                      | Mean     | Allocated |
|---------------------------- |---------:|----------:|
| PlaybackBenchmark_5000Updates  | 2.0 ms   | 0 B       |
| PlaybackBenchmark_10000Updates | 4.0 ms   | 0 B       |
```

**After optimization:**
```
| Method                      | Mean     | Allocated |
|---------------------------- |---------:|----------:|
| PlaybackBenchmark_5000Updates  | 0.6 ms   | 0 B       |
| PlaybackBenchmark_10000Updates | 1.2 ms   | 0 B       |
```

**Target:** **3-4x improvement**

---

## Alternative: Manual Performance Test

**File:** Create `Fdp.Tests/CommandBufferPerformanceTests.cs`

```csharp
using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Fdp.Kernel;

namespace Fdp.Tests
{
    public class CommandBufferPerformanceTests
    {
        private readonly ITestOutputHelper _output;
        
        public CommandBufferPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }
        
        [Fact]
        public void PlaybackPerformance_5000Updates_UnderTarget()
        {
            // Arrange
            var repo = new EntityRepository();
            repo.GetTable<TestComponent>(allowCreate: true);
            
            const int entityCount = 5000;
            for (int i = 0; i < entityCount; i++)
            {
                var e = repo.CreateEntity();
                repo.AddComponent(e, new TestComponent { Value = i });
            }
            
            var cmd = new EntityCommandBuffer(entityCount * 32);
            
            // Record commands
            for (int i = 0; i < entityCount; i++)
            {
                cmd.SetComponent(new Entity(i), new TestComponent { Value = i + 1 });
            }
            
            // Act: Measure playback
            var sw = Stopwatch.StartNew();
            cmd.Playback(repo);
            sw.Stop();
            
            // Assert
            _output.WriteLine($"Playback {entityCount} updates: {sw.Elapsed.TotalMilliseconds:F2}ms");
            
            // Target: < 1ms for 5000 updates (after optimization)
            Assert.True(sw.Elapsed.TotalMilliseconds < 1.0,
                $"Playback too slow: {sw.Elapsed.TotalMilliseconds:F2}ms (target: < 1.0ms)");
        }
        
        struct TestComponent
        {
            public int Value;
        }
    }
}
```

---

## Verification Checklist

**Build:**
- [ ] `dotnet build Fdp.Kernel` - 0 errors, 0 warnings
- [ ] No breaking changes to public API

**Tests:**
- [ ] All existing tests pass
- [ ] Performance test shows improvement

**Performance:**
- [ ] Before: Measure baseline (save results)
- [ ] After: Measure optimized (compare)
- [ ] Target: 3-4x improvement

**Integration:**
- [ ] BattleRoyale demo still runs
- [ ] No regressions in gameplay

---

## Safety Notes

**Component Registration:**
- All components MUST be registered before playback starts
- Registration typically happens during schema setup (main thread, before game loop)
- Late registration will work but falls back to slow path

**Thread Safety:**
- `_tableCache` is written during registration (main thread only)
- Playback always on main thread (Phase 3)
- No concurrent access during gameplay
- **Safe by design**

**Paranoid Mode:**
```csharp
// Debug builds: Check array bounds
#if DEBUG || FDP_PARANOID_MODE
if (_tableCache[typeId] == null)
    throw new InvalidOperationException($"Component {typeId} not registered");
#endif

// Release builds: Trust schema setup
```

---

## Production Recommendations

**Network Ingress Module:**
```csharp
public class NetworkModule : IModule
{
    private readonly EntityCommandBuffer _cmd;
    
    public NetworkModule()
    {
        // Pre-allocate for 10,000 updates/frame
        _cmd = new EntityCommandBuffer(10000 * 32); // 320KB
    }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        // Process network packets
        while (_dds.TryDequeue(out var packet))
        {
            var cmd = view.GetCommandBuffer();
            
            // Record updates (fast)
            foreach (var update in packet.EntityUpdates)
            {
                cmd.SetComponent(update.Entity, update.Position);
                cmd.SetComponent(update.Entity, update.Rotation);
            }
        }
        
        // Playback happens automatically in Phase 3 (optimized)
    }
}
```

---

## Deliverables

**Files to modify:**
1. `Fdp.Kernel/EntityRepository.cs` (add cache, implement SetComponentRawFast)
2. `Fdp.Kernel/EntityCommandBuffer.cs` (use fast path in Playback)

**Files to create:**
3. `Fdp.Benchmarks/CommandBufferPlaybackBenchmarks.cs` (performance verification)
4. `Fdp.Tests/CommandBufferPerformanceTests.cs` (integration test)

**Documentation:**
5. Update `.dev-workstream/reviews/COMMAND-BUFFER-PLAYBACK-OPTIMIZATION.md` with results

---

## Success Criteria

- ✅ `_tableCache` array added and populated
- ✅ `SetComponentRawFast()` implemented
- ✅ `Playback()` uses fast path
- ✅ All existing tests pass
- ✅ Performance improvement 3-4x (measured)
- ✅ 0 errors, 0 warnings
- ✅ No breaking changes

---

**This optimization is critical for high-scale networked games!** ⚡🚀
