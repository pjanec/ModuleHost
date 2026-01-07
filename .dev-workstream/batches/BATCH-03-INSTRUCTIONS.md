# BATCH 03: Convoy & Pooling Patterns

**Batch ID:** BATCH-03  
**Phase:** Optimization - Memory & Sync Performance  
**Priority:** MEDIUM (P2)  
**Estimated Effort:** 1 week  
**Dependencies:** None (can run parallel with BATCH-02)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 3 (Convoy & Pooling)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 03 section
4. **Current Implementation:** Review providers in `ModuleHost.Core/Providers/`

---

## 🎯 Batch Objectives

### Primary Goal
Optimize memory and sync performance for modules running at the same frequency by sharing snapshots.

### Success Criteria
- ✅ Modules with same frequency share single snapshot (Convoy Pattern)
- ✅ Snapshots pooled and reused (zero GC in steady state)
- ✅ Memory usage <20% of individual snapshots for convoy
- ✅ Sync time <30% of individual syncs for convoy
- ✅ All tests passing

### Why This Matters
Currently, 5 AI modules at 10Hz create 5 separate snapshots (5 × 100MB = 500MB) and sync 5 times. Convoy pattern: 1 snapshot (100MB), 1 sync. This is critical for scaling to many modules.

---

## 📋 Tasks

### Task 3.1: Snapshot Pool Implementation ⭐⭐

**Objective:** Create reusable pool for EntityRepository instances.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 3, Section 3.2 - "Snapshot Pool"

**What to Create:**

```csharp
// File: ModuleHost.Core/Providers/SnapshotPool.cs

using System.Collections.Concurrent;
using Fdp.Kernel;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Thread-safe pool of EntityRepository instances for snapshot reuse.
    /// Eliminates GC allocations by recycling repositories.
    /// </summary>
    public class SnapshotPool
    {
        private readonly ConcurrentStack<EntityRepository> _pool = new();
        private readonly Action<EntityRepository>? _schemaSetup;
        private readonly int _warmupCount;
        
        public SnapshotPool(Action<EntityRepository>? schemaSetup, int warmupCount = 0)
        {
            _schemaSetup = schemaSetup;
            _warmupCount = warmupCount;
            
            // Pre-populate pool
            for (int i = 0; i < warmupCount; i++)
            {
                var repo = CreateNew();
                _pool.Push(repo);
            }
        }
        
        /// <summary>
        /// Get a repository from pool or create new if empty.
        /// </summary>
        public EntityRepository Get()
        {
            if (_pool.TryPop(out var repo))
            {
                return repo;
            }
            
            return CreateNew();
        }
        
        /// <summary>
        /// Return repository to pool after clearing.
        /// </summary>
        public void Return(EntityRepository repo)
        {
            // CRITICAL: Clear state but keep buffer capacity
            repo.SoftClear();
            
            _pool.Push(repo);
        }
        
        private EntityRepository CreateNew()
        {
            var repo = new EntityRepository();
            _schemaSetup?.Invoke(repo);
            return repo;
        }
        
        /// <summary>
        /// Statistics for monitoring
        /// </summary>
        public int PooledCount => _pool.Count;
    }
}
```

**Acceptance Criteria:**
- [ ] Thread-safe ConcurrentStack backing store
- [ ] `Get()` returns pooled or new instance
- [ ] `Return()` calls `SoftClear()` before pooling
- [ ] Schema setup applied to new instances
- [ ] Warmup count pre-populates pool
- [ ] Statistics exposed for monitoring

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/SnapshotPoolTests.cs

[Fact]
public void SnapshotPool_Get_ReturnsNewWhenEmpty()
{
    var pool = new SnapshotPool(null);
    var repo = pool.Get();
    Assert.NotNull(repo);
}

[Fact]
public void SnapshotPool_ReturnThenGet_ReusesInstance()
{
    var pool = new SnapshotPool(null);
    var repo1 = pool.Get();
    pool.Return(repo1);
    var repo2 = pool.Get();
    Assert.Same(repo1, repo2);
}

[Fact]
public void SnapshotPool_Return_CallsSoftClear()
{
    var pool = new SnapshotPool(null);
    var repo = pool.Get();
    
    // Add entities
    var e1 = repo.CreateEntity();
    Assert.NotEqual(Entity.Null, e1);
    
    // Return to pool
    pool.Return(repo);
    
    // Get again - should be cleared
    var repo2 = pool.Get();
    Assert.Equal(0, repo2.EntityCount);
}

[Fact]
public void SnapshotPool_SchemaSetup_AppliedToNew()
{
    bool setupCalled = false;
    var pool = new SnapshotPool(repo => {
        setupCalled = true;
        repo.RegisterComponent<Position>();
    });
    
    var repo = pool.Get();
    Assert.True(setupCalled);
    Assert.True(repo.HasComponentTable<Position>());
}

[Fact]
public void SnapshotPool_WarmupCount_PrePopulates()
{
    var pool = new SnapshotPool(null, warmupCount: 5);
    Assert.Equal(5, pool.PooledCount);
}

[Fact]
public void SnapshotPool_ThreadSafe_ConcurrentAccess()
{
    var pool = new SnapshotPool(null, warmupCount: 10);
    
    // 10 threads getting and returning concurrently
    var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
    {
        for (int i = 0; i < 100; i++)
        {
            var repo = pool.Get();
            Thread.Sleep(1); // Simulate work
            pool.Return(repo);
        }
    }));
    
    Task.WaitAll(tasks.ToArray());
    
    // Assert: No crashes, pool functional
    Assert.True(pool.PooledCount > 0);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Providers/SnapshotPool.cs`
- [ ] New test file: `ModuleHost.Core.Tests/SnapshotPoolTests.cs`
- [ ] 6+ unit tests passing

---

### Task 3.2: SharedSnapshotProvider Enhancements ⭐⭐⭐

**Objective:** Add reference counting and pool integration to SharedSnapshotProvider.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 3, Section 3.2 - "SharedSnapshotProvider"

**Current Code Location:**
- File: `ModuleHost.Core/Providers/SharedSnapshotProvider.cs`
- Current state: Basic implementation exists with some ref counting

**Required Enhancements:**

```csharp
public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable
{
    private readonly EntityRepository _liveWorld;
    private readonly EventAccumulator _eventAccumulator;
    private readonly BitMask256 _unionMask;  // NEW: Union of all module requirements
    private readonly SnapshotPool _pool;      // NEW: Pool for reuse
    
    private EntityRepository? _currentSnapshot;
    private int _activeReaders;               // NEW: Reference count
    private uint _lastSeenTick;
    private readonly object _lock = new object();
    
    public SharedSnapshotProvider(
        EntityRepository liveWorld,
        EventAccumulator eventAccumulator,
        BitMask256 unionMask,                // NEW parameter
        SnapshotPool pool)                   // NEW parameter
    {
        _liveWorld = liveWorld;
        _eventAccumulator = eventAccumulator;
        _unionMask = unionMask;
        _pool = pool;
    }
    
    public ISimulationView AcquireView()
    {
        lock (_lock)
        {
            if (_currentSnapshot == null)
            {
                // First reader in convoy: create snapshot
                _currentSnapshot = _pool.Get();
                
                // Sync using UNION MASK (critical)
                _currentSnapshot.SyncFrom(_liveWorld, _unionMask);
                
                // Sync events
                _eventAccumulator.FlushToReplica(
                    _currentSnapshot.Bus, 
                    _lastSeenTick
                );
                
                _lastSeenTick = _liveWorld.GlobalVersion;
            }
            
            _activeReaders++;
            return _currentSnapshot;
        }
    }
    
    public void ReleaseView(ISimulationView view)
    {
        lock (_lock)
        {
            _activeReaders--;
            
            if (_activeReaders == 0)
            {
                // Last reader finished: return to pool
                if (_currentSnapshot != null)
                {
                    _pool.Return(_currentSnapshot);
                    _currentSnapshot = null;
                }
            }
            else if (_activeReaders < 0)
            {
                throw new InvalidOperationException(
                    "ReleaseView called more than AcquireView");
            }
        }
    }
    
    public void Update()
    {
        // SharedProvider is lazy: sync happens on first AcquireView
        // This method can be empty or used for diagnostics
    }
    
    public SnapshotProviderType ProviderType => SnapshotProviderType.Shared;
    
    public void Dispose()
    {
        lock (_lock)
        {
            if (_currentSnapshot != null && _activeReaders == 0)
            {
                _pool.Return(_currentSnapshot);
                _currentSnapshot = null;
            }
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Union mask parameter added to constructor
- [ ] Pool parameter added to constructor
- [ ] Reference counting works correctly
- [ ] Snapshot created lazily on first acquire
- [ ] Snapshot synced with union mask
- [ ] Returned to pool when last reader releases
- [ ] Thread-safe under concurrent access
- [ ] Error if release count exceeds acquire count

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/SharedSnapshotProviderTests.cs

[Fact]
public void SharedSnapshotProvider_FirstAcquire_CreatesSnapshot()
{
    var pool = new SnapshotPool(SetupSchema);
    var provider = new SharedSnapshotProvider(_liveWorld, _eventAccum, _mask, pool);
    
    var view = provider.AcquireView();
    Assert.NotNull(view);
}

[Fact]
public void SharedSnapshotProvider_MultipleAcquires_SameSnapshot()
{
    var provider = CreateProvider();
    
    var view1 = provider.AcquireView();
    var view2 = provider.AcquireView();
    var view3 = provider.AcquireView();
    
    Assert.Same(view1, view2);
    Assert.Same(view2, view3);
}

[Fact]
public void SharedSnapshotProvider_RefCount_IncrementsCorrectly()
{
    var provider = CreateProvider();
    
    provider.AcquireView(); // count = 1
    provider.AcquireView(); // count = 2
    provider.AcquireView(); // count = 3
    
    // Internal assertion: _activeReaders == 3
}

[Fact]
public void SharedSnapshotProvider_OnlyPoolsWhenAllReleased()
{
    var pool = new SnapshotPool(SetupSchema);
    var provider = CreateProvider(pool);
    
    var view1 = provider.AcquireView();
    var view2 = provider.AcquireView();
    
    provider.ReleaseView(view1); // count = 1
    Assert.Equal(0, pool.PooledCount); // Not returned yet
    
    provider.ReleaseView(view2); // count = 0
    Assert.Equal(1, pool.PooledCount); // Now returned
}

[Fact]
public void SharedSnapshotProvider_UnionMask_SyncsAllComponents()
{
    // Setup: Create union mask with Position + Velocity
    var mask = new BitMask256();
    mask.SetBit(ComponentRegistry.GetId<Position>());
    mask.SetBit(ComponentRegistry.GetId<Velocity>());
    
    // Live world has entities with both components
    var e = _liveWorld.CreateEntity();
    _liveWorld.SetComponent(e, new Position { X = 1, Y = 2 });
    _liveWorld.SetComponent(e, new Velocity { X = 3, Y = 4 });
    
    var provider = new SharedSnapshotProvider(_liveWorld, _eventAccum, mask, _pool);
    var view = provider.AcquireView() as EntityRepository;
    
    // Assert: Both components synced
    Assert.True(view.HasComponent<Position>(e));
    Assert.True(view.HasComponent<Velocity>(e));
}

[Fact]
public void SharedSnapshotProvider_TooManyReleases_Throws()
{
    var provider = CreateProvider();
    var view = provider.AcquireView();
    
    provider.ReleaseView(view);
    Assert.Throws<InvalidOperationException>(() => provider.ReleaseView(view));
}

[Fact]
public void SharedSnapshotProvider_ViewValidAcrossFrames()
{
    var provider = CreateProvider();
    var view = provider.AcquireView();
    
    // Simulate 10 frames passing
    for (int i = 0; i < 10; i++)
    {
        _kernel.Update(0.016f);
    }
    
    // View should still be readable
    Assert.NotNull(view);
    // Access data (shouldn't crash)
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/Providers/SharedSnapshotProvider.cs`
- [ ] Updated tests: `ModuleHost.Core.Tests/SharedSnapshotProviderTests.cs`
- [ ] 7+ unit tests passing

---

### Task 3.3: Auto-Grouping Logic ⭐⭐⭐

**Objective:** Implement convoy detection in kernel initialization.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 3, Section 3.2 - "Auto-Grouping Logic"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Method: `CreateDefaultProvider(ModuleEntry entry)` (around line 269)
- Current logic: Creates individual providers per module

**New Implementation:**

Add new method `AutoAssignProviders()` called from `Initialize()`:

```csharp
private SnapshotPool? _snapshotPool;

public void Initialize()
{
    // Create global pool
    _snapshotPool = new SnapshotPool(_schemaSetup, warmupCount: 10);
    
    // Auto-assign providers to modules
    AutoAssignProviders();
    
    // ... rest of initialization
}

private void AutoAssignProviders()
{
    // Group modules by execution characteristics
    var groups = _modules
        .Where(m => m.Provider == null) // Only auto-assign if not manually set
        .GroupBy(m => new 
        { 
            Tier = m.Module.Tier,
            Frequency = m.Module.UpdateFrequency
        });
    
    foreach (var group in groups)
    {
        var key = group.Key;
        var moduleList = group.ToList();
        
        if (key.Tier == ModuleTier.Fast)
        {
            // Fast tier: GDB (DoubleBufferProvider)
            // All fast modules can share one GDB
            var gdbProvider = new DoubleBufferProvider(
                _liveWorld, 
                _eventAccumulator, 
                _schemaSetup
            );
            
            foreach (var entry in moduleList)
            {
                entry.Provider = gdbProvider;
            }
        }
        else // Slow tier
        {
            if (moduleList.Count == 1)
            {
                // Single module: OnDemandProvider
                var entry = moduleList[0];
                var mask = GetComponentMask(entry.Module);
                
                entry.Provider = new OnDemandProvider(
                    _liveWorld,
                    _eventAccumulator,
                    mask,
                    _schemaSetup,
                    poolSize: 5  // Configurable pool
                );
            }
            else
            {
                // CONVOY: Multiple modules at same frequency
                // Calculate union mask
                var unionMask = new BitMask256();
                foreach (var entry in moduleList)
                {
                    var mask = GetComponentMask(entry.Module);
                    unionMask.BitwiseOr(mask);
                }
                
                // Create shared provider
                var sharedProvider = new SharedSnapshotProvider(
                    _liveWorld,
                    _eventAccumulator,
                    unionMask,
                    _snapshotPool!
                );
                
                // Assign to all modules in convoy
                foreach (var entry in moduleList)
                {
                    entry.Provider = sharedProvider;
                }
            }
        }
    }
}

private BitMask256 GetComponentMask(IModule module)
{
    // Helper to get component requirements from module
    // This might need module API enhancement (could return all for now)
    return new BitMask256(); // Placeholder - implement based on module metadata
}
```

**Acceptance Criteria:**
- [ ] Modules grouped by Tier + Frequency
- [ ] Fast modules share DoubleBufferProvider
- [ ] Single slow module gets OnDemandProvider
- [ ] Multiple slow modules share SharedSnapshotProvider
- [ ] Union mask calculated correctly
- [ ] Manual provider assignment still possible
- [ ] Statistics: convoy count, memory savings

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ConvoyAutoGroupingTests.cs

[Fact]
public void AutoGrouping_SameTierAndFreq_SharesProvider()
{
    var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
    
    var module1 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    var module2 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    var module3 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    
    kernel.RegisterModule(module1);
    kernel.RegisterModule(module2);
    kernel.RegisterModule(module3);
    kernel.Initialize();
    
    // Assert: All 3 share same provider
    var provider1 = GetProvider(kernel, module1);
    var provider2 = GetProvider(kernel, module2);
    var provider3 = GetProvider(kernel, module3);
    
    Assert.Same(provider1, provider2);
    Assert.Same(provider2, provider3);
    Assert.IsType<SharedSnapshotProvider>(provider1);
}

[Fact]
public void AutoGrouping_DifferentFreq_SeparateProviders()
{
    var module1 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    var module2 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 10 };
    
    // Register and initialize
    // Assert: Different providers
}

[Fact]
public void AutoGrouping_SingleModule_OnDemandProvider()
{
    var module = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    
    // Register and initialize
    // Assert: OnDemandProvider assigned
}

[Fact]
public void AutoGrouping_UnionMask_CombinesRequirements()
{
    // Module1 needs Position
    // Module2 needs Velocity
    // Module3 needs Position + Velocity
    
    // Register all with same freq
    // Assert: Provider has union mask with all 2 components
}

[Fact]
public void AutoGrouping_ManualOverride_NotGrouped()
{
    var customProvider = new OnDemandProvider(...);
    
    var module1 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    var module2 = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
    
    kernel.RegisterModule(module1, customProvider);  // Manual
    kernel.RegisterModule(module2);                   // Auto
    kernel.Initialize();
    
    // Assert: module1 uses customProvider
    // Assert: module2 uses different provider
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (AutoAssignProviders)
- [ ] New test file: `ModuleHost.Core.Tests/ConvoyAutoGroupingTests.cs`
- [ ] 5+ unit tests passing

---

### Task 3.4: Integration & Performance Validation ⭐⭐

**Objective:** Validate convoy memory savings and sync performance.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 3, entire chapter

**Test Scenarios:**

1. **Memory Savings Benchmark:**
   - Setup: 5 modules at 10Hz
   - Measure: Individual providers vs convoy
   - Target: <20% memory usage with convoy

2. **Sync Performance Benchmark:**
   - Setup: Same 5 modules
   - Measure: Sync time individual vs convoy
   - Target: <30% sync time with convoy

3. **Stress Test:**
   - Setup: 20 modules in multiple convoys
   - Measure: Stability and performance

**Performance Benchmarks to Write:**

```csharp
// File: ModuleHost.Benchmarks/ConvoyPerformance.cs

[MemoryDiagnoser]
public class ConvoyMemoryBenchmark
{
    [Benchmark]
    [Arguments(false)] // Individual providers
    [Arguments(true)]  // Convoy
    public void Memory_5Modules(bool useConvoy)
    {
        // Setup 5 modules at 10Hz
        // Run 100 frames
        // Measure: GC allocations, memory footprint
    }
}

[Benchmark]
public class ConvoySyncBenchmark
{
    [Benchmark]
    [Arguments(5, false)] // 5 modules, individual
    [Arguments(5, true)]  // 5 modules, convoy
    public void Sync_MultipleModules(int moduleCount, bool useConvoy)
    {
        // Measure: Sync time
        // Report: Total sync time per frame
    }
}
```

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Tests/ConvoyIntegration Tests.cs

[Fact]
public async Task ConvoyIntegration_5Modules_ShareSnapshot()
{
    // Create 5 modules at same frequency
    // Run simulation
    // Assert: All see same snapshot instance
    // Assert: Commands from all applied correctly
}

[Fact]
public async Task ConvoyIntegration_MemoryUsage_Reduced()
{
    long memIndividual = MeasureMemory(useConvoy: false);
    long memConvoy = MeasureMemory(useConvoy: true);
    
    Assert.True(memConvoy < memIndividual * 0.3); // <30% memory
}

[Fact]
public async Task ConvoyIntegration_20Modules_StablePerformance()
{
    // 20 modules in various convoys
    // Run 100 frames
    // Assert: No crashes
    // Assert: Frame time stable
    // Assert: All modules execute correctly
}
```

**Deliverables:**
- [ ] New benchmark file: `ModuleHost.Benchmarks/ConvoyPerformance.cs`
- [ ] New test file: `ModuleHost.Tests/ConvoyIntegrationTests.cs`
- [ ] 3+ integration tests passing
- [ ] Benchmark results showing targets met

---

## ✅ Definition of Done

- [ ] All 4 tasks completed
- [ ] SnapshotPool implemented and tested
- [ ] SharedSnapshotProvider enhanced
- [ ] Auto-grouping logic working
- [ ] Memory savings >80% demonstrated
- [ ] Sync performance improvement >70% demonstrated
- [ ] All unit tests passing (18+ tests)
- [ ] All integration tests passing (3+ tests)
- [ ] Performance benchmarks meet targets
- [ ] No compiler warnings
- [ ] Changes committed
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Memory reduction (convoy vs individual) | >80% | >50% |
| Sync time reduction (convoy vs individual) | >70% | >50% |
| Pool reuse rate | >95% | >80% |
| GC collections (steady state) | 0 per 100 frames | <5 per 100 frames |

---

## 🚧 Potential Challenges

### Challenge 1: SoftClear Implementation
**Issue:** `EntityRepository.SoftClear()` might not exist  
**Solution:** If missing, implement it (reset counts, keep capacity)  
**Ask if:** Method doesn't exist or behavior unclear

### Challenge 2: BitMask Union Operation
**Issue:** BitMask256 might not have BitwiseOr method  
**Solution:** Implement bit-level OR operation  
**Ask if:** BitMask API is unclear

### Challenge 3: Component Mask Extraction
**Issue:** How to get component requirements from IModule  
**Solution:** For now, return "all components" mask  
**Ask if:** Need more sophisticated detection

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-03-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-03-QUESTIONS.md`

---

## 🔗 References

**Primary Design:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 3  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 03

Good luck! This batch has great performance wins! 🚀
