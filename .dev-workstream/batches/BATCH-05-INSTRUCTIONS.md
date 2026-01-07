# BATCH 05: Execution Modes Refactor

**Batch ID:** BATCH-05  
**Phase:** Consolidation - Flexible Execution Modes  
**Priority:** MEDIUM (P2)  
**Estimated Effort:** 4 days  
**Dependencies:** BATCH-01, 02, 03, 04 (consolidation batch)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 5 (Execution Modes)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 05 section
4. **Previous Reviews:** Read all reviews from BATCH-01 through BATCH-04 to understand context
5. **Current Implementation:** Review `ModuleHost.Core/Abstractions/IModule.cs`

---

## 🎯 Batch Objectives

### Primary Goal
Replace the binary Fast/Slow tier system with explicit, composable execution policies.

### Success Criteria
- ✅ `ExecutionPolicy` struct replaces `ModuleTier` enum
- ✅ Support arbitrary combinations (Sync+Direct, FrameSynced+GDB, Async+SoD, etc.)
- ✅ Backward compatibility maintained for existing modules
- ✅ Factory methods provide sensible defaults
- ✅ Provider assignment logic uses new policies
- ✅ Examples migrated to new API
- ✅ All tests passing

### Why This Matters
The current Fast/Slow tier is too rigid. Physics needs Direct (main thread), Network needs FrameSynced+GDB, AI needs Async+SoD. This batch makes execution strategy explicit and composable, enabling domain-specific optimizations while maintaining clarity.

---

## 📋 Tasks

### Task 5.1: ExecutionPolicy Structure ⭐⭐

**Objective:** Create comprehensive execution policy struct with factory methods.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 5, Section 5.2 - "API Changes"

**What to Create:**

```csharp
// File: ModuleHost.Core/Abstractions/ExecutionPolicy.cs (NEW)

using System;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Defines how a module executes and what data strategy it uses.
    /// Replaces the binary Fast/Slow tier system with composable policies.
    /// </summary>
    public struct ExecutionPolicy
    {
        /// <summary>
        /// How the module runs (main thread, background synced, background async).
        /// </summary>
        public RunMode Mode { get; set; }
        
        /// <summary>
        /// What data structure the module uses (live world, replica, snapshot).
        /// </summary>
        public DataStrategy Strategy { get; set; }
        
        /// <summary>
        /// Target execution frequency in Hz (1-60).
        /// 0 means "every frame" (60Hz).
        /// </summary>
        public int TargetFrequencyHz { get; set; }
        
        /// <summary>
        /// Maximum expected runtime in milliseconds.
        /// Used for timeout detection and circuit breaker.
        /// </summary>
        public int MaxExpectedRuntimeMs { get; set; }
        
        /// <summary>
        /// Number of consecutive failures before circuit breaker opens.
        /// </summary>
        public int FailureThreshold { get; set; }
        
        /// <summary>
        /// Time in milliseconds before attempting recovery after circuit opens.
        /// </summary>
        public int CircuitResetTimeoutMs { get; set; }
        
        // ============================================================
        // FACTORY METHODS: Common Profiles
        // ============================================================
        
        /// <summary>
        /// Synchronous execution on main thread with direct world access.
        /// Use for: Physics, Input, critical systems that must run on main thread.
        /// </summary>
        public static ExecutionPolicy Synchronous() => new()
        {
            Mode = RunMode.Synchronous,
            Strategy = DataStrategy.Direct,
            TargetFrequencyHz = 60, // Every frame
            MaxExpectedRuntimeMs = 16, // Must complete within frame
            FailureThreshold = 1, // Immediate failure = fatal
            CircuitResetTimeoutMs = 1000
        };
        
        /// <summary>
        /// Frame-synced background execution with GDB replica.
        /// Main thread waits for completion.
        /// Use for: Network sync, Flight Recorder, low-latency background tasks.
        /// </summary>
        public static ExecutionPolicy FastReplica() => new()
        {
            Mode = RunMode.FrameSynced,
            Strategy = DataStrategy.GDB,
            TargetFrequencyHz = 60, // Every frame
            MaxExpectedRuntimeMs = 15, // Must complete quickly
            FailureThreshold = 3,
            CircuitResetTimeoutMs = 5000
        };
        
        /// <summary>
        /// Asynchronous background execution with SoD snapshots.
        /// Main thread doesn't wait. Module can span multiple frames.
        /// Use for: AI, Analytics, Pathfinding, slow computation.
        /// </summary>
        public static ExecutionPolicy SlowBackground(int frequencyHz) => new()
        {
            Mode = RunMode.Asynchronous,
            Strategy = DataStrategy.SoD,
            TargetFrequencyHz = frequencyHz,
            MaxExpectedRuntimeMs = Math.Max(100, 1000 / frequencyHz), // At least 1 frame worth
            FailureThreshold = 5, // More tolerant of transient failures
            CircuitResetTimeoutMs = 10000
        };
        
        /// <summary>
        /// Custom policy builder (fluent API).
        /// </summary>
        public static ExecutionPolicy Custom() => new()
        {
            Mode = RunMode.Asynchronous,
            Strategy = DataStrategy.SoD,
            TargetFrequencyHz = 10,
            MaxExpectedRuntimeMs = 100,
            FailureThreshold = 3,
            CircuitResetTimeoutMs = 5000
        };
        
        // ============================================================
        // FLUENT CONFIGURATION
        // ============================================================
        
        public ExecutionPolicy WithMode(RunMode mode)
        {
            Mode = mode;
            return this;
        }
        
        public ExecutionPolicy WithStrategy(DataStrategy strategy)
        {
            Strategy = strategy;
            return this;
        }
        
        public ExecutionPolicy WithFrequency(int hz)
        {
            TargetFrequencyHz = hz;
            return this;
        }
        
        public ExecutionPolicy WithTimeout(int ms)
        {
            MaxExpectedRuntimeMs = ms;
            return this;
        }
        
        // ============================================================
        // VALIDATION
        // ============================================================
        
        /// <summary>
        /// Validates policy configuration for common mistakes.
        /// </summary>
        public void Validate()
        {
            if (Mode == RunMode.Synchronous && Strategy != DataStrategy.Direct)
            {
                throw new InvalidOperationException(
                    "Synchronous mode requires Direct strategy (no snapshot needed on main thread)");
            }
            
            if (Strategy == DataStrategy.Direct && Mode != RunMode.Synchronous)
            {
                throw new InvalidOperationException(
                    "Direct strategy only valid for Synchronous mode (background threads need snapshot)");
            }
            
            if (TargetFrequencyHz < 0 || TargetFrequencyHz > 60)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TargetFrequencyHz), 
                    "Frequency must be 0-60 Hz");
            }
            
            if (MaxExpectedRuntimeMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxExpectedRuntimeMs),
                    "Timeout must be positive");
            }
        }
        
        public override string ToString()
        {
            return $"ExecutionPolicy({Mode}, {Strategy}, {TargetFrequencyHz}Hz, {MaxExpectedRuntimeMs}ms timeout)";
        }
    }
    
    /// <summary>
    /// How the module runs (threading model).
    /// </summary>
    public enum RunMode
    {
        /// <summary>
        /// Runs on main thread, blocks frame.
        /// Use for: Physics, critical systems.
        /// </summary>
        Synchronous,
        
        /// <summary>
        /// Runs on background thread, main waits for completion.
        /// Use for: Network, recorder, low-latency tasks.
        /// </summary>
        FrameSynced,
        
        /// <summary>
        /// Runs on background thread, main doesn't wait.
        /// Use for: AI, analytics, slow computation.
        /// </summary>
        Asynchronous
    }
    
    /// <summary>
    /// What data structure the module uses.
    /// </summary>
    public enum DataStrategy
    {
        /// <summary>
        /// Direct access to live world (only valid for Synchronous mode).
        /// No snapshot overhead, but runs on main thread.
        /// </summary>
        Direct,
        
        /// <summary>
        /// Persistent double-buffered replica (GDB).
        /// Low latency, synced every frame.
        /// Use for: Network, recorder.
        /// </summary>
        GDB,
        
        /// <summary>
        /// Pooled snapshot created on-demand (SoD).
        /// Higher latency, memory efficient.
        /// Use for: AI, analytics.
        /// </summary>
        SoD
    }
}
```

**Acceptance Criteria:**
- [ ] `ExecutionPolicy` struct created with all fields
- [ ] `RunMode` enum defined (Synchronous, FrameSynced, Asynchronous)
- [ ] `DataStrategy` enum defined (Direct, GDB, SoD)
- [ ] Factory methods: `Synchronous()`, `FastReplica()`, `SlowBackground()`
- [ ] Fluent API for custom configuration
- [ ] Validation logic prevents invalid combinations
- [ ] XML documentation complete

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ExecutionPolicyTests.cs

using Xunit;
using ModuleHost.Core.Abstractions;
using System;

namespace ModuleHost.Core.Tests
{
    public class ExecutionPolicyTests
    {
        [Fact]
        public void ExecutionPolicy_Synchronous_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.Synchronous();
            
            Assert.Equal(RunMode.Synchronous, policy.Mode);
            Assert.Equal(DataStrategy.Direct, policy.Strategy);
            Assert.Equal(60, policy.TargetFrequencyHz);
            Assert.True(policy.MaxExpectedRuntimeMs > 0);
        }
        
        [Fact]
        public void ExecutionPolicy_FastReplica_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.FastReplica();
            
            Assert.Equal(RunMode.FrameSynced, policy.Mode);
            Assert.Equal(DataStrategy.GDB, policy.Strategy);
            Assert.Equal(60, policy.TargetFrequencyHz);
        }
        
        [Fact]
        public void ExecutionPolicy_SlowBackground_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.SlowBackground(10);
            
            Assert.Equal(RunMode.Asynchronous, policy.Mode);
            Assert.Equal(DataStrategy.SoD, policy.Strategy);
            Assert.Equal(10, policy.TargetFrequencyHz);
            Assert.True(policy.MaxExpectedRuntimeMs >= 100);
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsSyncWithNonDirect()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Synchronous,
                Strategy = DataStrategy.GDB // INVALID
            };
            
            Assert.Throws<InvalidOperationException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsDirectWithAsync()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Asynchronous,
                Strategy = DataStrategy.Direct // INVALID
            };
            
            Assert.Throws<InvalidOperationException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsInvalidFrequency()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Asynchronous,
                Strategy = DataStrategy.SoD,
                TargetFrequencyHz = 100 // >60 Hz
            };
            
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_FluentAPI_WorksCorrectly()
        {
            var policy = ExecutionPolicy.Custom()
                .WithMode(RunMode.FrameSynced)
                .WithStrategy(DataStrategy.GDB)
                .WithFrequency(30)
                .WithTimeout(50);
            
            Assert.Equal(RunMode.FrameSynced, policy.Mode);
            Assert.Equal(DataStrategy.GDB, policy.Strategy);
            Assert.Equal(30, policy.TargetFrequencyHz);
            Assert.Equal(50, policy.MaxExpectedRuntimeMs);
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Abstractions/ExecutionPolicy.cs`
- [ ] New test file: `ModuleHost.Core.Tests/ExecutionPolicyTests.cs`
- [ ] 7+ unit tests passing

---

### Task 5.2: IModule API Update ⭐⭐

**Objective:** Update `IModule` interface to use `ExecutionPolicy` instead of Tier/UpdateFrequency.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 5, Section 5.2 - "IModule Changes"

**Files to Modify:**

1. **`ModuleHost.Core/Abstractions/IModule.cs`:**

```csharp
using System;
using System.Collections.Generic;

namespace ModuleHost.Core.Abstractions
{
    public interface IModule
    {
        /// <summary>
        /// Module name for diagnostics and logging.
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// Execution policy defining how this module runs.
        /// Replaces Tier + UpdateFrequency.
        /// </summary>
        ExecutionPolicy Policy { get; }
        
        /// <summary>
        /// Register systems for this module (called during initialization).
        /// </summary>
        void RegisterSystems(ISystemRegistry registry) { }
        
        /// <summary>
        /// Main module execution method.
        /// </summary>
        void Tick(ISimulationView view, float deltaTime);
        
        // From BATCH-02 (if implemented)
        /// <summary>
        /// Component types to watch for changes (reactive scheduling).
        /// </summary>
        IReadOnlyList<Type>? WatchComponents { get; }
        
        /// <summary>
        /// Event types to watch for firing (reactive scheduling).
        /// </summary>
        IReadOnlyList<Type>? WatchEvents { get; }
        
        // ============================================================
        // DEPRECATED (Kept for backward compatibility)
        // ============================================================
        
        /// <summary>
        /// [OBSOLETE] Use Policy instead.
        /// </summary>
        [Obsolete("Use Policy.Mode and Policy.Strategy instead. Will be removed in v2.0.")]
        ModuleTier Tier => ConvertPolicyToTier(Policy);
        
        /// <summary>
        /// [OBSOLETE] Use Policy.TargetFrequencyHz instead.
        /// </summary>
        [Obsolete("Use Policy.TargetFrequencyHz instead. Will be removed in v2.0.")]
        int UpdateFrequency => GetUpdateFrequencyFromPolicy(Policy);
        
        // Helper conversions for backward compat
        private static ModuleTier ConvertPolicyToTier(ExecutionPolicy policy)
        {
            return policy.Mode == RunMode.Synchronous || policy.Mode == RunMode.FrameSynced
                ? ModuleTier.Fast
                : ModuleTier.Slow;
        }
        
        private static int GetUpdateFrequencyFromPolicy(ExecutionPolicy policy)
        {
            if (policy.TargetFrequencyHz == 0 || policy.TargetFrequencyHz >= 60)
                return 1; // Every frame
            
            return 60 / policy.TargetFrequencyHz; // Convert Hz to frame count
        }
    }
    
    /// <summary>
    /// [OBSOLETE] Module execution tier. Use ExecutionPolicy instead.
    /// </summary>
    [Obsolete("Use ExecutionPolicy instead. Will be removed in v2.0.")]
    public enum ModuleTier
    {
        Fast,
        Slow
    }
}
```

**Acceptance Criteria:**
- [ ] `Policy` property added
- [ ] `Tier` and `UpdateFrequency` marked `[Obsolete]` with clear messages
- [ ] Backward compatibility maintained (old properties return computed values)
- [ ] Existing modules compile with deprecation warnings
- [ ] XML documentation updated

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ModulePolicyApiTests.cs

[Fact]
public void IModule_Policy_ReplacesOldAPI()
{
    var module = new TestModule
    {
        Policy = ExecutionPolicy.SlowBackground(10)
    };
    
    Assert.Equal(10, module.Policy.TargetFrequencyHz);
    Assert.Equal(RunMode.Asynchronous, module.Policy.Mode);
}

[Fact]
public void IModule_BackwardCompat_TierReturnsCorrectValue()
{
    var fastModule = new TestModule
    {
        Policy = ExecutionPolicy.FastReplica()
    };
    
    #pragma warning disable CS0618 // Type or member is obsolete
    Assert.Equal(ModuleTier.Fast, fastModule.Tier);
    #pragma warning restore CS0618
}

[Fact]
public void IModule_BackwardCompat_UpdateFrequencyComputed()
{
    var module = new TestModule
    {
        Policy = ExecutionPolicy.SlowBackground(10) // 10 Hz = every 6 frames
    };
    
    #pragma warning disable CS0618
    Assert.Equal(6, module.UpdateFrequency);
    #pragma warning restore CS0618
}

[Fact]
public void IModule_NewModule_UsesPolicy()
{
    var module = new ModernTestModule(); // Uses Policy
    
    Assert.NotNull(module.Policy);
    Assert.Equal(ExecutionPolicy.Synchronous().Mode, module.Policy.Mode);
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/Abstractions/IModule.cs`
- [ ] Deprecation warnings compile correctly
- [ ] New tests in: `ModuleHost.Core.Tests/ModulePolicyApiTests.cs`
- [ ] 4+ unit tests passing

---

### Task 5.3: Provider Assignment Refactor ⭐⭐⭐

**Objective:** Update provider auto-assignment to use execution policies.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 5, Section 5.2 - "Grouping Logic"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Method: `AutoAssignProviders()` (from BATCH-03)
- Current logic: Groups by Tier + UpdateFrequency

**Updated Implementation:**

```csharp
// In ModuleHostKernel.cs

private void AutoAssignProviders()
{
    // Modules can manually set provider; only auto-assign if null
    var modulesNeedingProvider = _modules.Where(m => m.Provider == null).ToList();
    
    if (modulesNeedingProvider.Count == 0)
        return;
    
    // Validate policies first
    foreach (var entry in modulesNeedingProvider)
    {
        try
        {
            entry.Module.Policy.Validate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Module '{entry.Module.Name}' has invalid execution policy: {ex.Message}", ex);
        }
    }
    
    // Group by execution characteristics
    var groups = modulesNeedingProvider
        .GroupBy(m => new
        {
            m.Module.Policy.Mode,
            m.Module.Policy.Strategy,
            Frequency = m.Module.Policy.TargetFrequencyHz
        });
    
    foreach (var group in groups)
    {
        var key = group.Key;
        var moduleList = group.ToList();
        
        switch (key.Strategy)
        {
            case DataStrategy.Direct:
                // No provider needed - direct world access
                // (modules run on main thread)
                foreach (var entry in moduleList)
                {
                    entry.Provider = null; // Explicit null
                }
                break;
            
            case DataStrategy.GDB:
                // All modules in group share ONE persistent replica
                var unionMask = CalculateUnionMask(moduleList);
                
                var gdbProvider = new DoubleBufferProvider(
                    _liveWorld,
                    _eventAccumulator,
                    unionMask,
                    _schemaSetup
                );
                
                foreach (var entry in moduleList)
                {
                    entry.Provider = gdbProvider;
                }
                break;
            
            case DataStrategy.SoD:
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
                        poolSize: 5 // From BATCH-01
                    );
                }
                else
                {
                    // Convoy: SharedSnapshotProvider
                    var unionMask = CalculateUnionMask(moduleList);
                    
                    var sharedProvider = new SharedSnapshotProvider(
                        _liveWorld,
                        _eventAccumulator,
                        unionMask,
                        _snapshotPool! // From BATCH-03
                    );
                    
                    foreach (var entry in moduleList)
                    {
                        entry.Provider = sharedProvider;
                    }
                }
                break;
        }
    }
}

private BitMask256 CalculateUnionMask(List<ModuleEntry> modules)
{
    var unionMask = new BitMask256();
    
    foreach (var entry in modules)
    {
        var moduleMask = GetComponentMask(entry.Module);
        unionMask.BitwiseOr(moduleMask);
    }
    
    return unionMask;
}

private BitMask256 GetComponentMask(IModule module)
{
    // TODO: Get actual component requirements from module
    // For now, return all components mask
    return BitMask256.All; // Placeholder
}
```

**Acceptance Criteria:**
- [ ] Groups modules by `{Mode, Strategy, Frequency}`
- [ ] Direct strategy: no provider assigned
- [ ] GDB strategy: shared DoubleBufferProvider per group
- [ ] SoD strategy: OnDemandProvider (single) or SharedSnapshotProvider (convoy)
- [ ] Policy validation before assignment
- [ ] Union mask calculation for groups

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Tests/ProviderAssignmentTests.cs

[Fact]
public void ProviderAssignment_SynchronousDirect_NoProvider()
{
    var kernel = CreateKernel();
    var module = new TestModule
    {
        Policy = ExecutionPolicy.Synchronous()
    };
    
    kernel.RegisterModule(module);
    kernel.Initialize();
    
    var entry = GetModuleEntry(kernel, module);
    Assert.Null(entry.Provider); // Direct access, no provider
}

[Fact]
public void ProviderAssignment_FrameSyncedGDB_SharedReplica()
{
    var kernel = CreateKernel();
    var module1 = new TestModule { Policy = ExecutionPolicy.FastReplica() };
    var module2 = new TestModule { Policy = ExecutionPolicy.FastReplica() };
    
    kernel.RegisterModule(module1);
    kernel.RegisterModule(module2);
    kernel.Initialize();
    
    var provider1 = GetModuleEntry(kernel, module1).Provider;
    var provider2 = GetModuleEntry(kernel, module2).Provider;
    
    Assert.NotNull(provider1);
    Assert.Same(provider1, provider2); // Share GDB replica
    Assert.IsType<DoubleBufferProvider>(provider1);
}

[Fact]
public void ProviderAssignment_AsyncSoD_SingleModule_OnDemand()
{
    var kernel = CreateKernel();
    var module = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
    
    kernel.RegisterModule(module);
    kernel.Initialize();
    
    var provider = GetModuleEntry(kernel, module).Provider;
    Assert.IsType<OnDemandProvider>(provider);
}

[Fact]
public void ProviderAssignment_AsyncSoD_MultipleModules_Convoy()
{
    var kernel = CreateKernel();
    var module1 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
    var module2 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
    var module3 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
    
    kernel.RegisterModule(module1);
    kernel.RegisterModule(module2);
    kernel.RegisterModule(module3);
    kernel.Initialize();
    
    var provider = GetModuleEntry(kernel, module1).Provider;
    
    Assert.IsType<SharedSnapshotProvider>(provider);
    Assert.Same(provider, GetModuleEntry(kernel, module2).Provider);
    Assert.Same(provider, GetModuleEntry(kernel, module3).Provider);
}

[Fact]
public void ProviderAssignment_InvalidPolicy_ThrowsClear Error()
{
    var kernel = CreateKernel();
    var module = new TestModule
    {
        Policy = new ExecutionPolicy
        {
            Mode = RunMode.Asynchronous,
            Strategy = DataStrategy.Direct // INVALID combo
        }
    };
    
    kernel.RegisterModule(module);
    
    var ex = Assert.Throws<InvalidOperationException>(() => kernel.Initialize());
    Assert.Contains("invalid execution policy", ex.Message);
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs`
- [ ] New test file: `ModuleHost.Tests/ProviderAssignmentTests.cs`
- [ ] 5+ integration tests passing

---

### Task 5.4: Migration & Documentation ⭐

**Objective:** Update examples and create migration guide for developers.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 5 - "Migration Path"

**Files to Update:**

1. **`Fdp.Examples.CarKinem/` modules** (any custom modules):
   - Replace `Tier` with `Policy`
   - Use factory methods for standard policies

2. **`ModuleHost.Core/README.md`:**
   - Update examples to use new API
   - Document execution policy patterns

3. **Create `MIGRATION.md`:**

```markdown
# Migration Guide: Tier → ExecutionPolicy

## Overview
ModuleHost v1.5 replaces the binary Fast/Slow tier system with explicit execution policies.

## Quick Migration

### Before (v1.0):
```csharp
public class MyModule : IModule
{
    public string Name => "MyModule";
    public ModuleTier Tier => ModuleTier.Slow;
    public int UpdateFrequency => 6; // Every 6 frames (10Hz)
    
    public void Tick(ISimulationView view, float deltaTime) { ... }
}
```

### After (v1.5):
```csharp
public class MyModule : IModule
{
    public string Name => "MyModule";
    public ExecutionPolicy Policy => ExecutionPolicy.SlowBackground(10); // 10 Hz
    
    public void Tick(ISimulationView view, float deltaTime) { ... }
}
```

## Policy Patterns

### Synchronous (Main Thread)
```csharp
Policy => ExecutionPolicy.Synchronous();
```
**Use for:** Physics, Input, critical systems

### Fast Frame-Synced
```csharp
Policy => ExecutionPolicy.FastReplica();
```
**Use for:** Network sync, Flight Recorder

### Slow Background
```csharp
Policy => ExecutionPolicy.SlowBackground(10); // 10 Hz
```
**Use for:** AI, Analytics, Pathfinding

### Custom
```csharp
Policy => ExecutionPolicy.Custom()
    .WithMode(RunMode.FrameSynced)
    .WithStrategy(DataStrategy.GDB)
    .WithFrequency(30)
    .WithTimeout(50);
```

## Backward Compatibility

Old API still works with deprecation warnings:
```csharp
#pragma warning disable CS0618
public ModuleTier Tier => ModuleTier.Fast;
#pragma warning restore CS0618
```

Will be removed in v2.0.

## Breaking Changes
- None (fully backward compatible)

## Benefits
- Explicit execution strategy
- Composable policies
- Better performance tuning
- Clearer intent
```

**Acceptance Criteria:**
- [ ] CarKinem examples migrated
- [ ] README updated with new patterns
- [ ] MIGRATION.md created
- [ ] Deprecation warnings appear for old usage
- [ ] Examples compile and run

**Tests:**

```csharp
[Fact]
public void Migration_CarKinemExample_UsesNewAPI()
{
    // Load CarKinem example
    // Assert: No obsolete API usage
    // Assert: Modules use ExecutionPolicy
}

[Fact]
public void Migration_OldAPIStillWorks()
{
    // Module uses old Tier property
    // Assert: Compiles with warning
    // Assert: Runs correctly
}
```

**Deliverables:**
- [ ] Updated: `Fdp.Examples.CarKinem/` modules
- [ ] Updated: `ModuleHost.Core/README.md`
- [ ] New file: `docs/MIGRATION.md`
- [ ] 2+ tests validating migration

---

## ✅ Definition of Done

This batch is complete when:

- [ ] All 4 tasks completed
- [ ] ExecutionPolicy struct implemented
- [ ] IModule API updated with backward compatibility
- [ ] Provider assignment uses new policies
- [ ] Examples migrated
- [ ] Migration guide written
- [ ] All unit tests passing (18+ tests)
- [ ] All integration tests passing (5+ tests)
- [ ] No breaking changes for existing code
- [ ] Documentation complete
- [ ] No compiler warnings (except intentional deprecations)
- [ ] Changes committed to git
- [ ] Report submitted

---

## 📊 Success Metrics

### Functional Targets
| Metric | Target |
|--------|--------|
| Policy combinations supported | All valid combos |
| Backward compatibility | 100% |
| Migration guide clarity | Complete, with examples |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| All tests | Passing |
| Documentation | Complete |
| Compiler warnings | 0 (except deprecations) |

---

## 🚧 Potential Challenges

### Challenge 1: Backward Compatibility
**Issue:** Old modules using Tier/UpdateFrequency must still work  
**Solution:** Computed properties with [Obsolete] attribute  
**Ask if:** Breaking changes unavoidable

### Challenge 2: Policy Validation
**Issue:** Invalid combinations (Async+Direct) must be caught early  
**Solution:** Validate() method called during Initialize()  
**Ask if:** Unclear which combinations are invalid

### Challenge 3: Migration Complexity
**Issue:** Many existing modules to migrate  
**Solution:** Step-by-step migration guide, no forced migration  
**Ask if:** Migration path is unclear

###Challenge 4: GetComponentMask Placeholder
**Issue:** GetComponentMask returns BitMask256.All (inefficient)  
**Solution:** For now, acceptable; future batch will add component introspection  
**Ask if:** Need immediate implementation

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-05-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-05-QUESTIONS.md`

---

## 🔗 References

**Primary Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 5  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 05 section  
**Workflow README:** `../README.md`

**Code to Review:**
- Previous batches for context (01-04)
- `ModuleHost.Core/Abstractions/IModule.cs`
- `ModuleHost.Core/ModuleHostKernel.cs`

---

## 💡 Implementation Tips

1. **Start with ExecutionPolicy** - it's independent
2. **Test validation thoroughly** - prevent invalid combinations
3. **Keep backward compat clean** - use #pragma directives in tests
4. **Document patterns** - developers need clear guidance
5. **Migrate one example fully** - proves the approach works
6. **Think about diagnostics** - Policy.ToString() helps debugging

**This is a consolidation batch - it brings together work from BATCH 01-04!**

Good luck! 🚀
