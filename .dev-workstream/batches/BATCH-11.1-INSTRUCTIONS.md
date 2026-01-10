# BATCH-11.1: Missing Tests & Performance Validation

**Batch ID:** BATCH-11.1  
**Phase:** BATCH-11 Corrective - Testing & Benchmarking  
**Priority:** CRITICAL (P0) - Blocking BATCH-11 merge  
**Estimated Effort:** 0.5 days (4 hours)  
**Dependencies:** BATCH-11 (code complete, tests incomplete)  
**Parent Batch:** BATCH-11  
**Developer:** Same as BATCH-11  
**Assignment Date:** 2026-01-10

---

## 🎯 Objective

Complete the **MANDATORY** testing requirements from BATCH-11 that were not delivered:
1. Create 3 missing test files with comprehensive coverage
2. Add performance benchmarks for cloning operations
3. Validate clone correctness and E2E scenarios
4. Ensure 100% test pass rate

**This is a corrective batch. The code from BATCH-11 is excellent, but testing was incomplete.**

---

## 📋 Context

### What Was Delivered in BATCH-11
✅ Core implementation (DataPolicy, DeepClone, Registry)  
✅ 1 test file: `DataPolicyTests.cs` (basic coverage)  
❌ 3 missing test files per specification  
❌ No clone correctness validation  
❌ No performance benchmarks

### What's Required Now
You must create the 3 missing test files exactly as specified in **BATCH-11-INSTRUCTIONS.md Tasks 11-14**, plus add performance benchmarks.

---

## 📝 Tasks

### **Task 1: ComponentTypeRegistryPolicyTests.cs** ⭐⭐⭐

**Objective:** Test the 3 new registry methods and flag getters.

**File to Create:** `FDP/Fdp.Tests/ComponentTypeRegistryPolicyTests.cs`

**Required Tests (Minimum 15):**

```csharp
using Xunit;
using Fdp.Kernel;

namespace Fdp.Tests
{
    public class ComponentTypeRegistryPolicyTests
    {
        [Fact]
        public void SetRecordable_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetRecordable(0, true);
            Assert.True(ComponentTypeRegistry.IsRecordable(0));
            
            ComponentTypeRegistry.SetRecordable(0, false);
            Assert.False(ComponentTypeRegistry.IsRecordable(0));
        }
        
        [Fact]
        public void SetSaveable_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetSaveable(0, true);
            Assert.True(ComponentTypeRegistry.IsSaveable(0));
            
            ComponentTypeRegistry.SetSaveable(0, false);
            Assert.False(ComponentTypeRegistry.IsSaveable(0));
        }
        
        [Fact]
        public void SetNeedsClone_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetNeedsClone(0, true);
            Assert.True(ComponentTypeRegistry.NeedsClone(0));
            
            ComponentTypeRegistry.SetNeedsClone(0, false);
            Assert.False(ComponentTypeRegistry.NeedsClone(0));
        }
        
        [Fact]
        public void GetRecordableTypeIds_ReturnsOnlyRecordable()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetRecordable(0, true);
            ComponentTypeRegistry.SetRecordable(1, false);
            ComponentTypeRegistry.SetRecordable(2, true);
            
            var ids = ComponentTypeRegistry.GetRecordableTypeIds();
            Assert.Contains(0, ids);
            Assert.DoesNotContain(1, ids);
            Assert.Contains(2, ids);
        }
        
        [Fact]
        public void GetSaveableTypeIds_ReturnsOnlySaveable()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetSaveable(0, true);
            ComponentTypeRegistry.SetSaveable(1, false);
            ComponentTypeRegistry.SetSaveable(2, true);
            
            var ids = ComponentTypeRegistry.GetSaveableTypeIds();
            Assert.Contains(0, ids);
            Assert.DoesNotContain(1, ids);
            Assert.Contains(2, ids);
        }
        
        [Fact]
        public void IsRecordable_OutOfRange_ReturnsFalse()
        {
            ComponentTypeRegistry.Clear();
            Assert.False(ComponentTypeRegistry.IsRecordable(999));
        }
        
        [Fact]
        public void IsSaveable_OutOfRange_ReturnsFalse()
        {
            ComponentTypeRegistry.Clear();
            Assert.False(ComponentTypeRegistry.IsSaveable(999));
        }
        
        [Fact]
        public void NeedsClone_OutOfRange_ReturnsFalse()
        {
            ComponentTypeRegistry.Clear();
            Assert.False(ComponentTypeRegistry.NeedsClone(999));
        }
        
        [Fact]
        public void EnsureCapacity_InitializesNewFlags()
        {
            ComponentTypeRegistry.Clear();
            
            // Force capacity expansion
            ComponentTypeRegistry.SetRecordable(10, true);
            
            // Verify defaults for intermediate IDs
            Assert.True(ComponentTypeRegistry.IsRecordable(5), "Should default to true");
            Assert.True(ComponentTypeRegistry.IsSaveable(5), "Should default to true");
            Assert.False(ComponentTypeRegistry.NeedsClone(5), "Should default to false");
        }
        
        [Fact]
        public void Clear_ResetsAllFlags()
        {
            ComponentTypeRegistry.SetRecordable(0, true);
            ComponentTypeRegistry.SetSaveable(0, true);
            ComponentTypeRegistry.SetNeedsClone(0, true);
            
            ComponentTypeRegistry.Clear();
            
            var recordableIds = ComponentTypeRegistry.GetRecordableTypeIds();
            var saveableIds = ComponentTypeRegistry.GetSaveableTypeIds();
            
            Assert.Empty(recordableIds);
            Assert.Empty(saveableIds);
        }
        
        // Add 5 more tests covering:
        // - Thread safety (if applicable)
        // - Large type IDs
        // - Flag independence (setting one doesn't affect others)
        // - Default values for new types
        // - Interaction with IsSnapshotable
    }
}
```

**Deliverables:**
- [ ] Minimum 15 tests
- [ ] All green
- [ ] Edge cases covered

---

### **Task 2: MutableClassRecordingTests.cs** ⭐⭐⭐⭐

**Objective:** Validate the **actual user problem** - mutable classes can now be recorded without crashes.

**File to Create:** `FDP/Fdp.Tests/MutableClassRecordingTests.cs`

**Required Tests (Minimum 8):**

```csharp
using System.Collections.Generic;
using MessagePack;
using Xunit;
using Fdp.Kernel;

namespace Fdp.Tests
{
    // Test component: Mutable class WITHOUT DataPolicy attribute
    [MessagePackObject]
    public class CombatHistory
    {
        [Key(0)] public int TotalDamage { get; set; }
        [Key(1)] public List<string> Events { get; set; } = new();
        
        public void RecordDamage(int amount, string source)
        {
            TotalDamage += amount;
            Events.Add($"{amount} from {source}");
        }
    }
    
    public class MutableClassRecordingTests
    {
        [Fact]
        public void MutableClass_NoAttribute_DoesNotCrash()
        {
            // THE FIX: This should NOT throw
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            var e = repo.CreateEntity();
            repo.AddComponent(e, new CombatHistory());
            
            Assert.True(repo.HasComponent<CombatHistory>(e));
        }
        
        [Fact]
        public void MutableClass_DefaultsToRecordable()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            int typeId = ManagedComponentType<CombatHistory>.ID;
            
            Assert.True(ComponentTypeRegistry.IsRecordable(typeId));
            Assert.True(ComponentTypeRegistry.IsSaveable(typeId));
        }
        
        [Fact]
        public void MutableClass_DefaultsToNoSnapshot()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            int typeId = ManagedComponentType<CombatHistory>.ID;
            
            // Should NOT be snapshotable (unsafe for background threads)
            Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
            Assert.False(ComponentTypeRegistry.NeedsClone(typeId));
        }
        
        [Fact]
        public void GetRecordableMask_IncludesMutableClass()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            var mask = repo.GetRecordableMask();
            int typeId = ManagedComponentType<CombatHistory>.ID;
            
            Assert.True(mask.IsSet(typeId));
        }
        
        [Fact]
        public void GetSnapshotableMask_ExcludesMutableClass()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            var mask = repo.GetSnapshotableMask();
            int typeId = ManagedComponentType<CombatHistory>.ID;
            
            Assert.False(mask.IsSet(typeId));
        }
        
        [Fact]
        public void GetSaveableMask_IncludesMutableClass()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            var mask = repo.GetSaveableMask();
            int typeId = ManagedComponentType<CombatHistory>.ID;
            
            Assert.True(mask.IsSet(typeId));
        }
        
        [Fact]
        public void MutableClass_CanMutateOnMainThread()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<CombatHistory>();
            
            var e = repo.CreateEntity();
            var history = new CombatHistory();
            repo.AddComponent(e, history);
            
            // Mutate
            history.RecordDamage(50, "Dragon");
            
            // Verify mutation visible
            var retrieved = repo.GetComponent<CombatHistory>(e);
            Assert.Equal(50, retrieved.TotalDamage);
            Assert.Single(retrieved.Events);
        }
        
        [Fact]
        public void MutableClass_NotInBackgroundSnapshot()
        {
            var mainRepo = new EntityRepository();
            var snapshotRepo = new EntityRepository();
            
            mainRepo.RegisterManagedComponent<CombatHistory>();
            snapshotRepo.RegisterManagedComponent<CombatHistory>();
            
            var e = mainRepo.CreateEntity();
            mainRepo.AddComponent(e, new CombatHistory { TotalDamage = 100 });
            
            // Simulate background snapshot
            snapshotRepo.SyncFrom(mainRepo);
            
            // Should NOT have the component (not snapshotable)
            Assert.False(snapshotRepo.HasComponent<CombatHistory>(e));
        }
    }
}
```

**Deliverables:**
- [ ] Minimum 8 tests
- [ ] Cover the user's original problem (registration without crash)
- [ ] Validate mask methods
- [ ] Verify exclusion from snapshots

---

### **Task 3: ComponentCloningTests.cs** ⭐⭐⭐⭐⭐

**Objective:** Validate deep cloning correctness - THE MOST CRITICAL TEST FILE.

**File to Create:** `FDP/Fdp.Tests/ComponentCloningTests.cs`

**Required Tests (Minimum 12):**

```csharp
using System.Collections.Generic;
using MessagePack;
using Xunit;
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;

namespace Fdp.Tests
{
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class SimpleCloneableClass
    {
        [Key(0)] public int Value { get; set; }
        [Key(1)] public string Name { get; set; } = "";
    }
    
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class ComplexCloneableClass
    {
        [Key(0)] public int Value { get; set; }
        [Key(1)] public List<int> Items { get; set; } = new();
        [Key(2)] public Dictionary<string, int> Dict { get; set; } = new();
    }
    
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class NestedCloneableClass
    {
        [Key(0)] public SimpleCloneableClass Inner { get; set; } = new();
        [Key(1)] public int OuterValue { get; set; }
    }
    
    public class ComponentCloningTests
    {
        [Fact]
        public void DeepClone_SimpleClass_CreatesIndependentCopy()
        {
            var original = new SimpleCloneableClass 
            { 
                Value = 42, 
                Name = "Test" 
            };
            
            var clone = FdpAutoSerializer.DeepClone(original);
            
            // Verify clone has same data
            Assert.Equal(42, clone.Value);
            Assert.Equal("Test", clone.Name);
            
            // Verify independence: mutating original doesn't affect clone
            original.Value = 99;
            original.Name = "Changed";
            
            Assert.Equal(42, clone.Value);  // Clone unchanged
            Assert.Equal("Test", clone.Name);  // Clone unchanged
        }
        
        [Fact]
        public void DeepClone_ComplexClass_CreatesDeepCopy()
        {
            var original = new ComplexCloneableClass
            {
                Value = 100,
                Items = new List<int> { 1, 2, 3 },
                Dict = new Dictionary<string, int> { ["A"] = 10, ["B"] = 20 }
            };
            
            var clone = FdpAutoSerializer.DeepClone(original);
            
            // Verify data
            Assert.Equal(100, clone.Value);
            Assert.Equal(new[] { 1, 2, 3 }, clone.Items);
            Assert.Equal(10, clone.Dict["A"]);
            
            // Verify independence: mutate collections
            original.Items.Add(4);
            original.Dict["C"] = 30;
            
            Assert.Equal(3, clone.Items.Count);  // Clone unchanged
            Assert.False(clone.Dict.ContainsKey("C"));  // Clone unchanged
        }
        
        [Fact]
        public void DeepClone_NestedClass_ClonesRecursively()
        {
            var original = new NestedCloneableClass
            {
                Inner = new SimpleCloneableClass { Value = 50, Name = "Inner" },
                OuterValue = 200
            };
            
            var clone = FdpAutoSerializer.DeepClone(original);
            
            // Verify data
            Assert.Equal(50, clone.Inner.Value);
            Assert.Equal("Inner", clone.Inner.Name);
            Assert.Equal(200, clone.OuterValue);
            
            // Verify deep independence
            original.Inner.Value = 999;
            
            Assert.Equal(50, clone.Inner.Value);  // Clone's inner unchanged
            Assert.NotSame(original.Inner, clone.Inner);  // Different instances
        }
        
        [Fact]
        public void DeepClone_String_ReturnsReference()
        {
            string original = "test";
            string clone = FdpAutoSerializer.DeepClone(original);
            
            // Strings are immutable, so reference copy is safe
            Assert.Same(original, clone);
        }
        
        [Fact]
        public void DeepClone_Null_ReturnsNull()
        {
            SimpleCloneableClass? original = null;
            var clone = FdpAutoSerializer.DeepClone(original);
            
            Assert.Null(clone);
        }
        
        [Fact]
        public void SyncDirtyChunks_CloneableComponent_CreatesIndependentCopies()
        {
            var repo1 = new EntityRepository();
            var repo2 = new EntityRepository();
            
            repo1.RegisterManagedComponent<SimpleCloneableClass>();
            repo2.RegisterManagedComponent<SimpleCloneableClass>();
            
            var e = repo1.CreateEntity();
            var original = new SimpleCloneableClass { Value = 100, Name = "Original" };
            repo1.AddComponent(e, original);
            
            // Simulate snapshot sync
            repo2.SyncFrom(repo1);
            
            // Mutate original
            original.Value = 999;
            original.Name = "Mutated";
            
            // Verify snapshot is isolated
            var snapshotCopy = repo2.GetComponent<SimpleCloneableClass>(e);
            Assert.Equal(100, snapshotCopy.Value);  // Clone unchanged
            Assert.Equal("Original", snapshotCopy.Name);  // Clone unchanged
        }
        
        [Fact]
        public void CloneableComponent_IsSnapshotable()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<SimpleCloneableClass>();
            
            int typeId = ManagedComponentType<SimpleCloneableClass>.ID;
            
            Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
            Assert.True(ComponentTypeRegistry.NeedsClone(typeId));
        }
        
        [Fact]
        public void GetSnapshotableMask_IncludesCloneableComponent()
        {
            var repo = new EntityRepository();
            repo.RegisterManagedComponent<SimpleCloneableClass>();
            
            var mask = repo.GetSnapshotableMask();
            int typeId = ManagedComponentType<SimpleCloneableClass>.ID;
            
            Assert.True(mask.IsSet(typeId));
        }
        
        [Fact]
        public void DeepClone_EmptyList_Clones()
        {
            var original = new ComplexCloneableClass
            {
                Items = new List<int>(),  // Empty
                Dict = new Dictionary<string, int>()  // Empty
            };
            
            var clone = FdpAutoSerializer.DeepClone(original);
            
            Assert.NotNull(clone.Items);
            Assert.Empty(clone.Items);
            Assert.NotSame(original.Items, clone.Items);  // Different instances
        }
        
        [Fact]
        public void DeepClone_Performance_IsCached()
        {
            // First call compiles
            var obj1 = new SimpleCloneableClass { Value = 1 };
            var clone1 = FdpAutoSerializer.DeepClone(obj1);
            
            // Second call should use cached delegate (very fast)
            var obj2 = new SimpleCloneableClass { Value = 2 };
            var clone2 = FdpAutoSerializer.DeepClone(obj2);
            
            Assert.Equal(1, clone1.Value);
            Assert.Equal(2, clone2.Value);
        }
        
        // Add 2 more tests covering:
        // - Null fields in complex class
        // - Multiple clones from same source
    }
}
```

**Deliverables:**
- [ ] Minimum 12 tests
- [ ] Validate independence (mutations don't leak)
- [ ] Test simple, complex, and nested classes
- [ ] Verify SyncDirtyChunks integration
- [ ] Edge cases (null, empty collections)

---

### **Task 4: Performance Benchmarks** ⭐⭐⭐⭐

**Objective:** Measure cloning performance and validate Expression Tree optimization.

**File to Create:** `FDP/Fdp.Benchmarks/CloningBenchmarks.cs`

**If Benchmarks project doesn't exist, create file in:** `FDP/Fdp.Tests/CloningPerformanceTests.cs`

**Required Benchmarks:**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Fdp.Kernel.FlightRecorder;
using MessagePack;

namespace Fdp.Tests
{
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class SimpleBenchClass
    {
        [Key(0)] public int A { get; set; }
        [Key(1)] public int B { get; set; }
        [Key(2)] public string Name { get; set; } = "";
    }
    
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class ComplexBenchClass
    {
        [Key(0)] public int Value { get; set; }
        [Key(1)] public List<int> Items { get; set; } = new();
        [Key(2)] public Dictionary<string, int> Dict { get; set; } = new();
        [Key(3)] public SimpleBenchClass Nested { get; set; } = new();
    }
    
    public class CloningPerformanceTests
    {
        private readonly ITestOutputHelper _output;
        
        public CloningPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }
        
        [Fact]
        public void Benchmark_SimpleClass_Cloning()
        {
            var original = new SimpleBenchClass { A = 1, B = 2, Name = "Test" };
            
            // Warmup (compile)
            _ = FdpAutoSerializer.DeepClone(original);
            
            const int iterations = 100_000;
            var sw = Stopwatch.StartNew();
            
            for (int i = 0; i < iterations; i++)
            {
                _ = FdpAutoSerializer.DeepClone(original);
            }
            
            sw.Stop();
            
            double avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000) / iterations;
            _output.WriteLine($"Simple Class Clone: {avgMicroseconds:F3} μs/op ({iterations:N0} iterations)");
            
            // Performance target: Should be < 1 microsecond (Expression Trees are FAST)
            Assert.True(avgMicroseconds < 5.0, 
                $"Clone too slow: {avgMicroseconds:F3} μs (expected < 5 μs)");
        }
        
        [Fact]
        public void Benchmark_ComplexClass_Cloning()
        {
            var original = new ComplexBenchClass
            {
                Value = 100,
                Items = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
                Dict = new Dictionary<string, int> { ["A"] = 1, ["B"] = 2, ["C"] = 3 },
                Nested = new SimpleBenchClass { A = 50, B = 60, Name = "Nested" }
            };
            
            // Warmup
            _ = FdpAutoSerializer.DeepClone(original);
            
            const int iterations = 10_000;
            var sw = Stopwatch.StartNew();
            
            for (int i = 0; i < iterations; i++)
            {
                _ = FdpAutoSerializer.DeepClone(original);
            }
            
            sw.Stop();
            
            double avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000) / iterations;
            _output.WriteLine($"Complex Class Clone: {avgMicroseconds:F3} μs/op ({iterations:N0} iterations)");
            
            // Complex class should still be fast (< 50 μs)
            Assert.True(avgMicroseconds < 100.0, 
                $"Clone too slow: {avgMicroseconds:F3} μs (expected < 100 μs)");
        }
        
        [Fact]
        public void Benchmark_CacheEffectiveness()
        {
            var obj = new SimpleBenchClass { A = 1 };
            
            // First call: Compile (slow)
            var sw1 = Stopwatch.StartNew();
            _ = FdpAutoSerializer.DeepClone(obj);
            sw1.Stop();
            
            // Second call: Use cache (fast)
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                _ = FdpAutoSerializer.DeepClone(obj);
            }
            sw2.Stop();
            
            double firstCallMs = sw1.Elapsed.TotalMilliseconds;
            double cachedAvgUs = (sw2.Elapsed.TotalMilliseconds * 1000) / 1000;
            
            _output.WriteLine($"First call (compile): {firstCallMs:F3} ms");
            _output.WriteLine($"Cached calls: {cachedAvgUs:F3} μs/op");
            
            // Cached should be >>100x faster than first compile
            Assert.True(cachedAvgUs < firstCallMs * 10, 
                "Cache not effective - calls should be much faster after compilation");
        }
        
        [Fact]
        public void Benchmark_MemoryAllocation()
        {
            var original = new SimpleBenchClass { A = 1, B = 2, Name = "Test" };
            
            // Measure allocations
            long memBefore = GC.GetTotalMemory(true);
            
            const int iterations = 10_000;
            for (int i = 0; i < iterations; i++)
            {
                _ = FdpAutoSerializer.DeepClone(original);
            }
            
            long memAfter = GC.GetTotalMemory(false);
            long allocatedBytes = memAfter - memBefore;
            long avgBytesPerClone = allocatedBytes / iterations;
            
            _output.WriteLine($"Memory per clone: ~{avgBytesPerClone} bytes");
            
            // Should allocate a new instance each time (reasonable overhead)
            Assert.True(avgBytesPerClone > 0, "Should allocate memory for clone");
            Assert.True(avgBytesPerClone < 1000, "Should not have excessive overhead");
        }
    }
}
```

**Deliverables:**
- [ ] 4 performance tests
- [ ] Timing measurements logged to test output
- [ ] Performance assertions (prevent regressions)
- [ ] Cache effectiveness validated

---

## ✅ Validation Criteria

### Build & Test
```powershell
# Build
dotnet build FDP/Fdp.Kernel/Fdp.Kernel.csproj --nologo
dotnet build FDP/Fdp.Tests/Fdp.Tests.csproj --nologo

# Run ALL new tests
dotnet test FDP/Fdp.Tests/Fdp.Tests.csproj `
  --filter "FullyQualifiedName~ComponentTypeRegistryPolicyTests|MutableClassRecordingTests|ComponentCloningTests|CloningPerformanceTests" `
  --nologo `
  --logger "trx;LogFileName=BATCH-11.1-TestResults.trx"

# Expected: 35+ tests passing (15 + 8 + 12 + 4)
```

### Success Criteria
- [ ] All 3 test files created
- [ ] Minimum 35 tests total
- [ ] 100% pass rate (0 failures)
- [ ] Performance benchmarks run and documented
- [ ] Clone independence verified (critical!)
- [ ] Test output shows timing metrics

---

## 📊 Reporting Requirements

**File:** `reports/BATCH-11.1-REPORT.md`

**Must Include:**
1. **Test Summary:**
   - Total tests: X/35+
   - Pass rate: 100%
   - Coverage: What scenarios are tested

2. **Performance Results:**
   - Simple class clone time: X μs
   - Complex class clone time: X μs  
   - Cache speedup: Xx faster
   - Memory overhead: X bytes/clone

3. **Clone Correctness:**
   - Independence validated: Yes/No
   - Edge cases covered: List them
   - Integration verified: SyncDirtyChunks works

4. **Lessons Learned:**
   - Why were tests skipped in BATCH-11?
   - How will you ensure completeness next time?

---

## 🎯 Timeline

**Estimated:** 4 hours
- Task 1 (Registry tests): 1 hour
- Task 2 (Mutable class tests): 1 hour
- Task 3 (Cloning tests): 1.5 hours
- Task 4 (Benchmarks): 0.5 hours

**Deadline:** Complete within 1 working day

---

## 📝 Important Notes

### Why This Matters
1. **Clone Correctness is Critical**: If cloning leaks mutations, background modules will have race conditions
2. **Performance Regression Risk**: Cloning adds overhead - must be measured
3. **User Trust**: Testing validates the fix actually solves their problem

### Developer Guidance
- **Copy Existing Patterns**: Look at existing test files for xUnit patterns
- **Test Independence**: Use `repo.Clear()` or create new repos per test
- **Performance**: `Stopwatch` is sufficient for benchmarks
- **Output**: Use `ITestOutputHelper` to log benchmark results

---

**Let's finish this properly. Good luck! 🚀**

**File Path:** `d:\Work\ModuleHost\.dev-workstream\batches\BATCH-11.1-INSTRUCTIONS.md`
