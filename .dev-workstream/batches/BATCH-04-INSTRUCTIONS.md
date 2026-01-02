# BATCH-04: ModuleHost Integration

**Phase:** Week 4 - ModuleHost Orchestration  
**Difficulty:** High  
**Story Points:** 16  
**Estimated Duration:** 3-4 days  
**Dependencies:** BATCH-01 (SyncFrom), BATCH-02 (EventAccumulator, ISimulationView), BATCH-03 (Providers)

---

## ⚠️ IMPORTANT: Read Basic Instructions First!

**Before starting this batch, review:**

📖 **`.dev-workstream/README.md`** - Your complete workflow guide including:
- Definition of Done (DoD) checklist
- Critical rules (NO warnings, ALL tests must pass, etc.)
- Code review checklist
- Communication protocols

**This is mandatory for every batch!** The README contains essential guidelines that apply to all your work.

---

## 📋 Batch Overview

This batch implements the **ModuleHost orchestration layer** that brings together all the components built so far. You will create the system that manages module lifecycle, assigns providers to modules, and integrates with the FDP simulation loop.

**Critical Success Factors:**
- Clean module registration API
- Correct provider assignment based on module tier (Fast/Slow)
- Proper integration with FDP simulation phases
- Module execution pipeline (acquire → tick → release)
- Thread-safe module dispatch

---

## 📚 Required Reading

**Before starting, read these documents:**

1. **Primary References:**
   - `/docs/API-REFERENCE.md` - Sections: ModuleHostKernel, IModule
   - `/docs/HYBRID-ARCHITECTURE-QUICK-REFERENCE.md` - Module tiers, execution flow
   - `/docs/detailed-design-overview.md` - Layer 2: ModuleHost

2. **Design Context:**
   - `/docs/IMPLEMENTATION-SPECIFICATION.md` - Section: Module Orchestration
   - `/docs/IMPLEMENTATION-TASKS.md` - Tasks 012-014
   - `/docs/reference-archive/FDP-GDB-SoD-unified.md` - Module execution pipeline

3. **Previous Work:**
   - Review BATCH-03 providers (understand AcquireView/ReleaseView lifecycle)
   - Review BATCH-02 ISimulationView (understand what modules receive)

**Key Concepts to Understand:**
- Module tiers: Fast (GDB, every frame) vs Slow (SoD, every N frames)
- Provider assignment: Fast → DoubleBufferProvider, Slow → OnDemandProvider/Shared
- Execution pipeline: Update providers → Dispatch modules → Collect results
- Thread safety: Modules run on background threads, providers on main thread

---

## 🎯 Tasks in This Batch

### TASK-012: IModule Interface (3 SP)

**Priority:** P0 (Foundation)  
**File:** `ModuleHost.Core/Abstractions/IModule.cs` (new)

**Description:**  
Define the interface that all modules must implement. Modules are background systems that process simulation state (e.g., AI, networking, analytics).

**Acceptance Criteria:**
- [ ] Interface with `Tick(ISimulationView, float deltaTime)` method
- [ ] Properties: `Name`, `Tier` (Fast/Slow), `UpdateFrequency` (frames)
- [ ] Complete XML documentation
- [ ] ModuleTier enum (Fast, Slow)
- [ ] Compiles without errors

**Implementation:**

```csharp
// File: ModuleHost.Core/Abstractions/IModule.cs
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Defines a background module that processes simulation state.
    /// Modules run asynchronously and receive read-only views via ISimulationView.
    /// </summary>
    public interface IModule
    {
        /// <summary>
        /// Module name (for diagnostics and logging).
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// Module execution tier.
        /// Fast: Runs every frame via GDB (e.g., Network, Recorder)
        /// Slow: Runs every N frames via SoD (e.g., AI, Analytics)
        /// </summary>
        ModuleTier Tier { get; }
        
        /// <summary>
        /// Update frequency in frames (1 = every frame, 6 = every 6 frames).
        /// Only applies to Slow tier modules.
        /// Fast tier modules always run every frame (this value ignored).
        /// </summary>
        int UpdateFrequency { get; }
        
        /// <summary>
        /// Main module execution method.
        /// Called on background thread with read-only simulation view.
        /// 
        /// CRITICAL: Do NOT modify simulation state directly.
        /// Use command buffer pattern to queue mutations.
        /// 
        /// Thread-safety: Multiple modules may run concurrently.
        /// </summary>
        /// <param name="view">Read-only simulation view</param>
        /// <param name="deltaTime">Time since module's last tick (seconds)</param>
        void Tick(ISimulationView view, float deltaTime);
    }
    
    /// <summary>
    /// Module execution tier.
    /// </summary>
    public enum ModuleTier
    {
        /// <summary>
        /// Fast tier: Runs every frame via GDB.
        /// Low latency, persistent replica.
        /// Examples: Network sync, Flight Recorder, Input processing.
        /// </summary>
        Fast,
        
        /// <summary>
        /// Slow tier: Runs every N frames via SoD.
        /// Higher latency, pooled snapshots.
        /// Examples: AI, Analytics, Pathfinding, Physics simulation.
        /// </summary>
        Slow
    }
}
```

**Tests Required (2 tests):**

Create file: `ModuleHost.Core.Tests/IModuleTests.cs`

1. **Interface_Compiles**
   - Verify: IModule compiles

2. **ModuleTier_EnumHasValues**
   - Verify: Fast and Slow enum values exist

---

### TASK-013: ModuleHostKernel (10 SP)

**Priority:** P0 (Core orchestration)  
**File:** `ModuleHost.Core/ModuleHostKernel.cs` (new)

**Description:**  
Implements the central orchestrator that manages module lifecycle, assigns providers, and executes modules at the correct frequency.

**Acceptance Criteria:**
- [ ] Class manages list of registered modules
- [ ] Constructor accepts LiveWorld (EntityRepository) and EventAccumulator
- [ ] Method `RegisterModule(IModule, ISnapshotProvider?)` for registration
- [ ] Method `Update(float deltaTime)` called every frame
- [ ] Fast modules execute every frame
- [ ] Slow modules execute based on UpdateFrequency
- [ ] Provider assignment: Fast → GDB, Slow → SoD/Shared (if not specified)
- [ ] Thread-safe module execution (Task.Run or similar)
- [ ] Performance: <1ms overhead for dispatch (excluding module work)

**Implementation:**

```csharp
// File: ModuleHost.Core/ModuleHostKernel.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;

namespace ModuleHost.Core
{
    /// <summary>
    /// Central orchestrator for module execution.
    /// Manages module registration, provider assignment, and execution pipeline.
    /// </summary>
    public sealed class ModuleHostKernel : IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly List<ModuleEntry> _modules = new();
        
        private uint _currentFrame = 0;
        
        public ModuleHostKernel(EntityRepository liveWorld, EventAccumulator eventAccumulator)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
        }
        
        /// <summary>
        /// Registers a module with optional provider override.
        /// If provider is null, assigns default based on module tier:
        /// - Fast tier → DoubleBufferProvider (GDB)
        /// - Slow tier → OnDemandProvider (SoD)
        /// </summary>
        public void RegisterModule(IModule module, ISnapshotProvider? provider = null)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            
            // Auto-assign provider if not specified
            if (provider == null)
            {
                provider = CreateDefaultProvider(module);
            }
            
            var entry = new ModuleEntry
            {
                Module = module,
                Provider = provider,
                FramesSinceLastRun = 0
            };
            
            _modules.Add(entry);
        }
        
        /// <summary>
        /// Main update loop (called every simulation frame).
        /// 1. Captures event history
        /// 2. Updates providers (syncs replicas/snapshots)
        /// 3. Dispatches modules (async execution)
        /// </summary>
        public void Update(float deltaTime)
        {
            // Capture event history for this frame
            _eventAccumulator.CaptureFrame(_liveWorld.Bus, _currentFrame);
            
            // Update all providers (sync point)
            foreach (var entry in _modules)
            {
                entry.Provider.Update();
            }
            
            // Dispatch modules
            var tasks = new List<Task>();
            
            foreach (var entry in _modules)
            {
                // Check if module should run this frame
                if (ShouldRunThisFrame(entry))
                {
                    // Acquire view
                    var view = entry.Provider.AcquireView();
                    
                    // Calculate delta time for this module
                    float moduleDelta = (entry.FramesSinceLastRun + 1) * deltaTime;
                    
                    // Dispatch async
                    var task = Task.Run(() =>
                    {
                        try
                        {
                            entry.Module.Tick(view, moduleDelta);
                        }
                        finally
                        {
                            // Always release view (even on exception)
                            entry.Provider.ReleaseView(view);
                        }
                    });
                    
                    tasks.Add(task);
                    entry.FramesSinceLastRun = 0;
                }
                else
                {
                    entry.FramesSinceLastRun++;
                }
            }
            
            // Wait for all modules to complete
            // (In production, might use timeout or separate phase)
            Task.WaitAll(tasks.ToArray());
            
            _currentFrame++;
        }
        
        private bool ShouldRunThisFrame(ModuleEntry entry)
        {
            var module = entry.Module;
            
            // Fast tier always runs
            if (module.Tier == ModuleTier.Fast)
                return true;
            
            // Slow tier runs based on UpdateFrequency
            int frequency = Math.Max(1, module.UpdateFrequency);
            return (entry.FramesSinceLastRun + 1) >= frequency;
        }
        
        private ISnapshotProvider CreateDefaultProvider(IModule module)
        {
            // Fast tier → GDB (DoubleBufferProvider)
            if (module.Tier == ModuleTier.Fast)
            {
                return new DoubleBufferProvider(_liveWorld, _eventAccumulator);
            }
            
            // Slow tier → SoD (OnDemandProvider)
            // Default: all components (no mask filtering)
            var mask = new BitMask256();
            for (int i = 0; i < 256; i++)
                mask.SetBit(i);
            
            return new OnDemandProvider(_liveWorld, _eventAccumulator, mask);
        }
        
        public void Dispose()
        {
            // Dispose all providers
            foreach (var entry in _modules)
            {
                if (entry.Provider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _modules.Clear();
        }
        
        private class ModuleEntry
        {
            public IModule Module { get; set; } = null!;
            public ISnapshotProvider Provider { get; set; } = null!;
            public int FramesSinceLastRun { get; set; }
        }
    }
}
```

**Tests Required (8 tests):**

Create file: `ModuleHost.Core.Tests/ModuleHostKernelTests.cs`

1. **RegisterModule_AddsToList**
   - Execute: kernel.RegisterModule(mockModule)
   - Verify: Module registered

2. **RegisterModule_FastTier_AssignsGDBProvider**
   - Setup: Fast tier module (no provider specified)
   - Execute: RegisterModule
   - Verify: DoubleBufferProvider assigned

3. **RegisterModule_SlowTier_AssignsSODProvider**
   - Setup: Slow tier module (no provider specified)
   - Execute: RegisterModule
   - Verify: OnDemandProvider assigned

4. **Update_CallsProviderUpdate**
   - Execute: kernel.Update(deltaTime)
   - Verify: Provider.Update() called

5. **Update_FastModule_RunsEveryFrame**
   - Setup: Fast tier module
   - Execute: kernel.Update() 5 times
   - Verify: Module.Tick() called 5 times

6. **Update_SlowModule_RunsAtFrequency**
   - Setup: Slow tier module, UpdateFrequency = 3
   - Execute: kernel.Update() 10 times
   - Verify: Module.Tick() called 3 times (frames 3, 6, 9)

7. **Update_ModuleDeltaTime_Calculated**
   - Setup: Slow module, frequency = 6
   - Execute: Update 6 times with deltaTime = 0.016
   - Verify: Module receives moduleDelta = 0.096 (6 * 0.016)

8. **Update_ReleasesView_EvenOnException**
   - Setup: Module.Tick() throws exception
   - Execute: kernel.Update()
   - Verify: Provider.ReleaseView() still called

---

### TASK-014: Integration with FDP Simulation Loop (3 SP)

**Priority:** P0 (Connects to simulation)  
**File:** Example integration code and documentation

**Description:**  
Create example showing how ModuleHostKernel integrates with FDP simulation loop. This is primarily documentation and example code, not production code.

**Acceptance Criteria:**
- [ ] Example code showing integration
- [ ] Documentation of execution phases
- [ ] Performance considerations documented
- [ ] Thread-safety notes
- [ ] Example compiles and runs

**Implementation:**

Create file: `ModuleHost.Core.Tests/Integration/FdpIntegrationExample.cs`

```csharp
// File: ModuleHost.Core.Tests/Integration/FdpIntegrationExample.cs
using System;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using Xunit;

namespace ModuleHost.Core.Tests.Integration
{
    /// <summary>
    /// Example integration of ModuleHostKernel with FDP simulation loop.
    /// NOT production code - demonstrates integration pattern.
    /// </summary>
    public class FdpIntegrationExample
    {
        [Fact]
        public void ExampleSimulationLoop()
        {
            // ============================================
            // SETUP PHASE (Once at startup)
            // ============================================
            
            // Create live world (FDP)
            using var liveWorld = new EntityRepository();
            
            // Register components
            liveWorld.RegisterComponent<Position>();
            liveWorld.RegisterComponent<Velocity>();
            
            // Create entities
            for (int i = 0; i < 100; i++)
            {
                var e = liveWorld.CreateEntity();
                liveWorld.AddComponent(e, new Position { X = i, Y = 0 });
                liveWorld.AddComponent(e, new Velocity { X = 1, Y = 0 });
            }
            
            // Create event accumulator
            var eventAccumulator = new EventAccumulator(maxHistoryFrames: 10);
            
            // Create ModuleHost
            using var moduleHost = new ModuleHostKernel(liveWorld, eventAccumulator);
            
            // Register modules
            var networkModule = new MockModule("Network", ModuleTier.Fast, 1);
            var aiModule = new MockModule("AI", ModuleTier.Slow, 6);
            
            moduleHost.RegisterModule(networkModule);
            moduleHost.RegisterModule(aiModule);
            
            // ============================================
            // SIMULATION LOOP (Every frame)
            // ============================================
            
            const float deltaTime = 1.0f / 60.0f; // 60 FPS
            
            for (int frame = 0; frame < 20; frame++)
            {
                // Phase 1: Simulation (main thread)
                // - Physics, gameplay logic, etc.
                // - Modifies liveWorld
                SimulatePhysics(liveWorld, deltaTime);
                
                // Phase 2: ModuleHost Update (main thread)
                // - Captures events
                // - Syncs providers
                // - Dispatches modules (async)
                moduleHost.Update(deltaTime);
                
                // Phase 3: Command Processing (main thread)
                // - Process commands from modules
                // - Apply to liveWorld
                // (Not implemented in this example)
            }
            
            // Verify modules ran correct number of times
            Assert.Equal(20, networkModule.TickCount); // Every frame
            Assert.Equal(3, aiModule.TickCount); // Every 6 frames (frames 6, 12, 18)
        }
        
        private void SimulatePhysics(EntityRepository world, float deltaTime)
        {
            // Example: Move all entities
            var query = world.Query()
                .With<Position>()
                .With<Velocity>()
                .Build();
            
            foreach (var entity in query)
            {
                ref var pos = ref world.GetComponentRW<Position>(entity);
                ref readonly var vel = ref world.GetComponentRO<Velocity>(entity);
                
                pos.X += vel.X * deltaTime;
                pos.Y += vel.Y * deltaTime;
            }
        }
    }
    
    // Mock module for testing
    public class MockModule : IModule
    {
        public string Name { get; }
        public ModuleTier Tier { get; }
        public int UpdateFrequency { get; }
        public int TickCount { get; private set; }
        
        public MockModule(string name, ModuleTier tier, int frequency)
        {
            Name = name;
            Tier = tier;
            UpdateFrequency = frequency;
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            TickCount++;
            
            // Example: Count entities
            int count = 0;
            foreach (var e in view.Query().Build())
                count++;
            
            // Module work would go here
            // (e.g., AI decisions, network packets, analytics)
        }
    }
    
    struct Position { public float X, Y; }
    struct Velocity { public float X, Y; }
}
```

**Documentation File:**

Create file: `ModuleHost.Core/INTEGRATION-GUIDE.md`

```markdown
# ModuleHost Integration Guide

## Overview

This guide explains how to integrate ModuleHostKernel with your FDP simulation loop.

## Execution Phases

Each simulation frame consists of three phases:

### Phase 1: Simulation (Main Thread)
- Run gameplay logic, physics, etc.
- Modify live world (EntityRepository)
- Generate events (via FdpEventBus)

### Phase 2: ModuleHost Update (Main Thread + Background Threads)
Main thread:
- Captures event history (EventAccumulator)
- Syncs providers (SyncFrom for replicas/snapshots)

Background threads:
- Modules execute with ISimulationView
- Read-only access to simulation state
- Generate commands (not applied yet)

### Phase 3: Command Processing (Main Thread)
- Collect commands from modules
- Apply to live world
- (Not yet implemented - BATCH-05)

## Performance Considerations

**Main Thread Budget:**
- Provider.Update(): <2ms for GDB, <100μs for SoD
- Event capture: <100μs
- Module dispatch: <1ms overhead

**Module Execution:**
- Runs async, does not block main thread
- Use Task.WaitAll or separate phase for sync point

**Optimization Tips:**
- Fast modules: Use GDB (zero-copy, persistent replica)
- Slow modules: Use SoD with component mask filtering
- Convoy pattern: Group slow modules sharing same data

## Thread Safety

**Safe:**
- Reading from ISimulationView (read-only)
- Multiple modules running concurrently
- Provider.AcquireView() from module threads

**Unsafe:**
- Modifying live world from module threads
- Calling Provider.Update() from module threads

**Solution:**
- Use command buffer pattern (modules queue commands)
- Commands applied on main thread in Phase 3

## Example Code

See `FdpIntegrationExample.cs` for complete working example.
```

**Tests Required (2 tests):**

1. **IntegrationExample_Compiles**
   - Verify: Example code compiles

2. **IntegrationExample_RunsCorrectly**
   - Execute: Example simulation loop
   - Verify: Modules run correct number of times

---

## 🔍 Integration Tests

**After all 3 tasks complete**, verify end-to-end:

**File:** `ModuleHost.Core.Tests/Integration/ModuleHostIntegrationTests.cs`

```csharp
[Fact]
public void EndToEnd_ModuleReceivesCorrectData()
{
    using var live = new EntityRepository();
    live.RegisterComponent<Position>();
    
    var entity = live.CreateEntity();
    live.AddComponent(entity, new Position { X = 10, Y = 20 });
    
    var accumulator = new EventAccumulator();
    using var kernel = new ModuleHostKernel(live, accumulator);
    
    var testModule = new TestModule();
    kernel.RegisterModule(testModule);
    
    // Run one frame
    kernel.Update(1.0f / 60.0f);
    
    // Verify module received correct data
    Assert.True(testModule.DidRun);
    Assert.Equal(10f, testModule.LastSeenX);
}

private class TestModule : IModule
{
    public string Name => "Test";
    public ModuleTier Tier => ModuleTier.Fast;
    public int UpdateFrequency => 1;
    
    public bool DidRun { get; private set; }
    public float LastSeenX { get; private set; }
    
    public void Tick(ISimulationView view, float deltaTime)
    {
        DidRun = true;
        
        foreach (var e in view.Query().Build())
        {
            if (view.TryGetComponent<Position>(e, out var pos))
            {
                LastSeenX = pos.X;
            }
        }
    }
}
```

---

## ⚠️ Critical Rules

**Mandatory Requirements:**

1. ⛔ **Always release views** - Even on exception (use try/finally)
2. ⛔ **Provider.Update() on main thread only** - Not thread-safe
3. ⛔ **Modules must NOT modify live world** - Read-only via ISimulationView
4. ⛔ **Frequency ≥ 1** - UpdateFrequency must be at least 1
5. ⛔ **Task.WaitAll or timeout** - Don't let modules run forever

**Performance Constraints:**

- ModuleHostKernel.Update() overhead: <1ms
- Provider syncs already tested (BATCH-03)
- Module work not counted (async, background)

**Thread Safety:**

- Provider.Update(): Main thread only
- AcquireView/ReleaseView: Thread-safe
- Module.Tick(): Runs on background thread
- ModuleHostKernel.Update(): Not re-entrant (single-threaded)

---

## 📊 Success Metrics

**Batch is DONE when:**

- [x] All 3 tasks complete (TASK-012 through TASK-014)
- [x] All 12 unit tests passing (2 + 8 + 2)
- [x] 2 integration tests passing
- [x] Zero compiler warnings
- [x] Example code runs successfully
- [x] Documentation complete

---

## 🚨 Common Pitfalls

**Watch Out For:**

1. **Forgetting ReleaseView** - Always call in finally block
2. **Module frequency = 0** - Check for Math.Max(1, frequency)
3. **Provider.Update() from module** - Only main thread
4. **Missing Task.WaitAll** - Modules won't complete
5. **Exception in module** - Must still release view

---

## 💡 Implementation Tips

**Best Practices:**

1. **Start with TASK-012** (IModule) - Simple interface
2. **Then TASK-013** (Kernel) - Core orchestration
3. **Finally TASK-014** (Integration) - Documentation + example

**Testing Strategy:**

1. Test module registration (add/list)
2. Test provider assignment (auto vs manual)
3. Test execution frequency (fast vs slow)
4. Test view lifecycle (acquire/release)
5. Integration test validates full pipeline

**Performance:**

- Use Stopwatch to measure Update() overhead
- Profile if dispatch takes >1ms
- Module work is async (doesn't block)

---

## 📋 Deliverables

**When batch complete, submit:**

1. **Batch Report:** `reports/BATCH-04-REPORT.md`
2. **Questions (if any):** `reports/BATCH-04-QUESTIONS.md`
3. **Blockers (if any):** `reports/BLOCKERS-ACTIVE.md`

**Report Must Include:**

- Status of all 3 tasks
- Test results (12 unit + 2 integration)
- Example code execution results
- Files created/modified list
- Performance measurements

---

## 🎯 Next Batch Preview

**BATCH-05** (final batch) will implement:
- Command buffer pattern (modules → live world)
- Final integration tests
- Performance validation
- Documentation cleanup

This depends on ModuleHostKernel working correctly!

---

**Questions? Create:** `reports/BATCH-04-QUESTIONS.md`  
**Blocked? Update:** `reports/BLOCKERS-ACTIVE.md`  
**Done? Submit:** `reports/BATCH-04-REPORT.md`

**Remember: Read `.dev-workstream/README.md` before starting!**

**Good luck! 🚀**
