# BATCH-10.1 Instructions: Test Coverage Improvements

**Assigned:** Developer  
**Date:** January 6, 2026  
**Estimated:** 0.5 SP (Quick fixes)  
**Priority:** 🔥 HIGH - Close test gaps before merge

---

## Overview

Address critical test coverage gaps identified in code review. These are quick additions to verify that core features actually work as designed, not just "don't crash".

**Reference:** `.dev-workstream/reviews/BATCH-10-REVIEW.md`

---

## Gap #1: Verify Execution Order (0.2 SP)

**Problem:** Current test doesn't verify systems execute in correct dependency order.

**File:** `ModuleHost.Core.Tests/SystemSchedulerTests.cs`

**Replace test (lines 16-37):**

```csharp
[Fact]
public void TopologicalSort_SimpleChain_CorrectOrder()
{
    var scheduler = new SystemScheduler();
    var executionLog = new List<string>();
    
    var systemA = new TrackingSystemA(executionLog);
    var systemB = new TrackingSystemB(executionLog);
    var systemC = new TrackingSystemC(executionLog);
    
    scheduler.RegisterSystem(systemA);
    scheduler.RegisterSystem(systemB);
    scheduler.RegisterSystem(systemC);
    
    scheduler.BuildExecutionOrders();
    
    var mockView = new MockSimulationView();
    scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
    
    // CRITICAL: Verify actual execution order
    Assert.Equal(3, executionLog.Count);
    Assert.Equal("A", executionLog[0]);
    Assert.Equal("B", executionLog[1]);
    Assert.Equal("C", executionLog[2]);
}
```

**Add tracking systems (at end of file):**

```csharp
// Tracking systems for execution order verification
[UpdateInPhaseAttribute(SystemPhase.Simulation)]
class TrackingSystemA : IModuleSystem
{
    private readonly List<string> _log;
    public TrackingSystemA(List<string> log) => _log = log;
    public void Execute(ISimulationView view, float deltaTime) => _log.Add("A");
}

[UpdateInPhaseAttribute(SystemPhase.Simulation)]
[UpdateAfterAttribute(typeof(TrackingSystemA))]
class TrackingSystemB : IModuleSystem
{
    private readonly List<string> _log;
    public TrackingSystemB(List<string> log) => _log = log;
    public void Execute(ISimulationView view, float deltaTime) => _log.Add("B");
}

[UpdateInPhaseAttribute(SystemPhase.Simulation)]
[UpdateAfterAttribute(typeof(TrackingSystemB))]
class TrackingSystemC : IModuleSystem
{
    private readonly List<string> _log;
    public TrackingSystemC(List<string> log) => _log = log;
    public void Execute(ISimulationView view, float deltaTime) => _log.Add("C");
}
```

---

## Gap #2: Verify Module Delta Time (0.2 SP)

**Problem:** Critical fix for module delta time has no test.

**File:** `ModuleHost.Core.Tests/ModuleHostKernelTests.cs` (create if doesn't exist)

**Add test:**

```csharp
using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class ModuleHostKernelTests
    {
        [Fact]
        public void ModuleDeltaTime_AccumulatesCorrectly()
        {
            // Arrange
            var world = new EntityRepository();
            var accumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(world, accumulator);
            
            var testModule = new DeltaTimeTrackingModule();
            kernel.RegisterModule(testModule);
            kernel.Initialize();
            
            // Act: Run 6 frames at 60 FPS (0.016s each)
            // Module runs every 6 frames (10Hz)
            for (int i = 0; i < 6; i++)
            {
                kernel.Update(0.016f);
            }
            
            // Assert: Module should have received delta time of ~0.1s, not 0.016s
            Assert.True(testModule.WasExecuted, "Module should have executed");
            Assert.InRange(testModule.LastDeltaTime, 0.095f, 0.105f);
            
            // Verify it resets after execution
            kernel.Update(0.016f); // Frame 7 - no execution
            kernel.Update(0.016f); // Frame 8 - no execution
            
            // After 6 more frames, delta should be ~0.1s again, not 0.2s
            for (int i = 0; i < 4; i++)
            {
                kernel.Update(0.016f);
            }
            
            // Should have executed twice now
            Assert.InRange(testModule.LastDeltaTime, 0.095f, 0.105f);
        }
    }
    
    // Test module that tracks delta time
    class DeltaTimeTrackingModule : IModule
    {
        public string Name => "DeltaTimeTracker";
        public ModuleTier Tier => ModuleTier.Slow;
        public int UpdateFrequency => 6; // 10 Hz
        
        public bool WasExecuted { get; private set; }
        public float LastDeltaTime { get; private set; }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            WasExecuted = true;
            LastDeltaTime = deltaTime;
        }
        
        public void RegisterSystems(ISystemRegistry registry) { }
    }
}
```

---

## Gap #3: Verify ConsumeManagedEvents Implementation (0.05 SP)

**File:** `Fdp.Kernel/EntityRepository.View.cs`

**Check if method exists (around line 65-75):**

```csharp
IReadOnlyList<T> ISimulationView.ConsumeManagedEvents<T>()
{
    return Bus.ConsumeManaged<T>();
}
```

**If missing, add it.**

**Test it:**

**File:** `ModuleHost.Core.Tests/ISimulationViewTests.cs` (create if doesn't exist)

```csharp
using System.Collections.Generic;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class ISimulationViewTests
    {
        [Fact]
        public void ConsumeManagedEvents_ReturnsEvents()
        {
            // Arrange
            var repo = new EntityRepository();
            ISimulationView view = repo;
            
            // Act: Publish managed event
            repo.Bus.PublishManaged(new TestManagedEvent { Message = "Test" });
            
            // Consume via interface
            var events = view.ConsumeManagedEvents<TestManagedEvent>();
            
            // Assert
            Assert.NotNull(events);
            Assert.Single(events);
            Assert.Equal("Test", events[0].Message);
        }
    }
    
    class TestManagedEvent
    {
        public string Message { get; set; } = "";
    }
}
```

---

## Gap #4: Verify Phase Execution Order (0.05 SP)

**File:** `ModuleHost.Core.Tests/ModuleHostKernelTests.cs`

**Add test:**

```csharp
[Fact]
public void PhaseExecution_FollowsCorrectOrder()
{
    // Arrange
    var world = new EntityRepository();
    var accumulator = new EventAccumulator();
    var kernel = new ModuleHostKernel(world, accumulator);
    
    var executionLog = new List<string>();
    
    kernel.RegisterGlobalSystem(new LoggingSystem(executionLog, "Input", SystemPhase.Input));
    kernel.RegisterGlobalSystem(new LoggingSystem(executionLog, "BeforeSync", SystemPhase.BeforeSync));
    kernel.RegisterGlobalSystem(new LoggingSystem(executionLog, "PostSim", SystemPhase.PostSimulation));
    kernel.RegisterGlobalSystem(new LoggingSystem(executionLog, "Export", SystemPhase.Export));
    
    kernel.Initialize();
    
    // Act
    kernel.Update(0.016f);
    
    // Assert: Verify phase execution order
    Assert.Equal(4, executionLog.Count);
    Assert.Equal("Input", executionLog[0]);
    Assert.Equal("BeforeSync", executionLog[1]);
    Assert.Equal("PostSim", executionLog[2]);
    Assert.Equal("Export", executionLog[3]);
}

// Logging system for phase order verification
class LoggingSystem : IModuleSystem
{
    private readonly List<string> _log;
    private readonly string _name;
    private readonly SystemPhase _phase;
    
    public LoggingSystem(List<string> log, string name, SystemPhase phase)
    {
        _log = log;
        _name = name;
        _phase = phase;
    }
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        _log.Add(_name);
    }
}

// Need to add UpdateInPhaseAttribute dynamically or via reflection
// Alternative: Use separate classes per phase
```

**Alternative (using separate classes):**

```csharp
[Fact]
public void PhaseExecution_FollowsCorrectOrder()
{
    var world = new EntityRepository();
    var accumulator = new EventAccumulator();
    var kernel = new ModuleHostKernel(world, accumulator);
    
    var log = ExecutionOrderLog.Instance;
    log.Clear();
    
    kernel.RegisterGlobalSystem(new InputPhaseSystem());
    kernel.RegisterGlobalSystem(new BeforeSyncPhaseSystem());
    kernel.RegisterGlobalSystem(new PostSimPhaseSystem());
    kernel.RegisterGlobalSystem(new ExportPhaseSystem());
    
    kernel.Initialize();
    kernel.Update(0.016f);
    
    Assert.Equal(new[] { "Input", "BeforeSync", "PostSim", "Export" }, log.Entries);
}

// Singleton log for tracking
class ExecutionOrderLog
{
    public static ExecutionOrderLog Instance { get; } = new();
    public List<string> Entries { get; } = new();
    public void Clear() => Entries.Clear();
}

[UpdateInPhaseAttribute(SystemPhase.Input)]
class InputPhaseSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime) 
        => ExecutionOrderLog.Instance.Entries.Add("Input");
}

[UpdateInPhaseAttribute(SystemPhase.BeforeSync)]
class BeforeSyncPhaseSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime) 
        => ExecutionOrderLog.Instance.Entries.Add("BeforeSync");
}

[UpdateInPhaseAttribute(SystemPhase.PostSimulation)]
class PostSimPhaseSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime) 
        => ExecutionOrderLog.Instance.Entries.Add("PostSim");
}

[UpdateInPhaseAttribute(SystemPhase.Export)]
class ExportPhaseSystem : IModuleSystem
{
    public void Execute(ISimulationView view, float deltaTime) 
        => ExecutionOrderLog.Instance.Entries.Add("Export");
}
```

---

## Deliverables

**Files to modify:**
1. `ModuleHost.Core.Tests/SystemSchedulerTests.cs` (execution order tracking)
2. `ModuleHost.Core.Tests/ModuleHostKernelTests.cs` (create - delta time + phase order tests)
3. `ModuleHost.Core.Tests/ISimulationViewTests.cs` (create - managed events test)
4. `Fdp.Kernel/EntityRepository.View.cs` (verify ConsumeManagedEvents exists)

---

## Verification

**Build:**
```powershell
dotnet build ModuleHost.Core.Tests --nologo
```

**Run tests:**
```powershell
dotnet test ModuleHost.Core.Tests --nologo
```

**Expected:**
- All tests pass (9 tests total: 5 original + 4 new)
- 0 errors, 0 warnings
- Execution order verified
- Module delta time verified
- Phase order verified
- Managed events verified

---

## Success Criteria

- ✅ `TopologicalSort_SimpleChain_CorrectOrder` verifies A → B → C execution
- ✅ `ModuleDeltaTime_AccumulatesCorrectly` verifies ~0.1s for 10Hz module
- ✅ `PhaseExecution_FollowsCorrectOrder` verifies Input → BeforeSync → Post → Export
- ✅ `ConsumeManagedEvents_ReturnsEvents` verifies managed event consumption
- ✅ All tests pass
- ✅ 0 warnings

---

**This closes all critical test gaps!** ✅🎯
