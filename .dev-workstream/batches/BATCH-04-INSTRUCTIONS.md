# BATCH 04: Resilience & Safety

**Batch ID:** BATCH-04  
**Phase:** Foundation - Resilience & Safety  
**Priority:** HIGH (P1)  
**Estimated Effort:** 1 week  
**Dependencies:** BATCH-01 (requires async task execution)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 4 (Resilience & Safety)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 04 section
4. **BATCH-01 Review:** `../reviews/BATCH-01-REVIEW.md` (understand async execution changes)
5. **Current Implementation:** Review `ModuleHost.Core/ModuleHostKernel.cs`

---

## 🎯 Batch Objectives

### Primary Goal
Prevent faulty modules from crashing or freezing the ModuleHost, ensuring system resilience and graceful degradation.

### Success Criteria
- ✅ Hung modules (infinite loops) don't freeze the simulation
- ✅ Crashing modules are isolated (exceptions caught)
- ✅ Circuit breaker trips after N consecutive failures
- ✅ Failed modules can auto-recover after timeout
- ✅ System continues running with degraded functionality
- ✅ All tests passing (unit + integration)

### Why This Matters
In production, a single buggy module shouldn't take down the entire simulation. A hung AI module or crashing analytics script must be isolated so that physics, networking, and other critical systems continue running. This batch implements safety rails around module execution.

---

## 📋 Tasks

### Task 4.1: Circuit Breaker Implementation ⭐⭐

**Objective:** Create a state machine that tracks module health and prevents repeated execution of failing modules.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 4, Section 4.2 - "Circuit Breaker"

**What to Create:**

Create a new file implementing the Circuit Breaker pattern:

```csharp
// File: ModuleHost.Core/Resilience/ModuleCircuitBreaker.cs

using System;

namespace ModuleHost.Core.Resilience
{
    /// <summary>
    /// Circuit breaker states following the standard pattern.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Normal operation - module can run.
        /// </summary>
        Closed,
        
        /// <summary>
        /// Module has failed too many times - skipping execution.
        /// </summary>
        Open,
        
        /// <summary>
        /// Testing recovery - allow one execution to see if module recovered.
        /// </summary>
        HalfOpen
    }
    
    /// <summary>
    /// Tracks module health and prevents repeated execution of failing modules.
    /// Implements the Circuit Breaker pattern for resilience.
    /// </summary>
    public class ModuleCircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly int _resetTimeoutMs;
        
        private int _failureCount;
        private DateTime _lastFailureTime;
        private CircuitState _state = CircuitState.Closed;
        
        private readonly object _lock = new object();
        
        /// <summary>
        /// Creates a circuit breaker with specified thresholds.
        /// </summary>
        /// <param name="failureThreshold">Number of consecutive failures before opening circuit (default: 3)</param>
        /// <param name="resetTimeoutMs">Milliseconds before attempting recovery (default: 5000)</param>
        public ModuleCircuitBreaker(int failureThreshold = 3, int resetTimeoutMs = 5000)
        {
            if (failureThreshold <= 0)
                throw new ArgumentException("Failure threshold must be positive", nameof(failureThreshold));
            if (resetTimeoutMs <= 0)
                throw new ArgumentException("Reset timeout must be positive", nameof(resetTimeoutMs));
            
            _failureThreshold = failureThreshold;
            _resetTimeoutMs = resetTimeoutMs;
        }
        
        /// <summary>
        /// Current circuit state (for diagnostics).
        /// </summary>
        public CircuitState State
        {
            get { lock (_lock) return _state; }
        }
        
        /// <summary>
        /// Number of consecutive failures recorded.
        /// </summary>
        public int FailureCount
        {
            get { lock (_lock) return _failureCount; }
        }
        
        /// <summary>
        /// Determines if the module can run this frame.
        /// </summary>
        /// <returns>True if module should execute, false if circuit is open</returns>
        public bool CanRun()
        {
            lock (_lock)
            {
                if (_state == CircuitState.Closed)
                {
                    return true;
                }
                
                if (_state == CircuitState.Open)
                {
                    // Check if enough time has passed to attempt recovery
                    var timeSinceFailure = DateTime.UtcNow - _lastFailureTime;
                    if (timeSinceFailure.TotalMilliseconds > _resetTimeoutMs)
                    {
                        // Transition to HalfOpen - allow one test execution
                        _state = CircuitState.HalfOpen;
                        return true;
                    }
                    
                    return false; // Still in cooldown
                }
                
                // HalfOpen state - allow execution to test recovery
                return _state == CircuitState.HalfOpen;
            }
        }
        
        /// <summary>
        /// Records successful module execution.
        /// Resets failure count and closes circuit if in HalfOpen state.
        /// </summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    // Recovery successful - close circuit
                    _state = CircuitState.Closed;
                    _failureCount = 0;
                }
                else if (_state == CircuitState.Closed)
                {
                    // Successful execution in normal state - reset failure count
                    _failureCount = 0;
                }
                // Note: Success in Open state shouldn't happen, but handle gracefully
            }
        }
        
        /// <summary>
        /// Records module failure (exception or timeout).
        /// Increments failure count and opens circuit if threshold exceeded.
        /// </summary>
        /// <param name="reason">Reason for failure (for logging)</param>
        public void RecordFailure(string reason)
        {
            lock (_lock)
            {
                _lastFailureTime = DateTime.UtcNow;
                _failureCount++;
                
                if (_state == CircuitState.HalfOpen)
                {
                    // Recovery attempt failed - reopen circuit immediately
                    _state = CircuitState.Open;
                }
                else if (_failureCount >= _failureThreshold)
                {
                    // Threshold exceeded - open circuit
                    _state = CircuitState.Open;
                }
            }
        }
        
        /// <summary>
        /// Resets the circuit breaker to closed state (manual recovery).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _state = CircuitState.Closed;
                _failureCount = 0;
            }
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Three states implemented: Closed, Open, HalfOpen
- [ ] State transitions work correctly
- [ ] Failure threshold configurable
- [ ] Reset timeout configurable
- [ ] Thread-safe (uses locking)
- [ ] Success resets failure count
- [ ] Failure in HalfOpen immediately reopens circuit
- [ ] Public properties for diagnostics

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ModuleCircuitBreakerTests.cs

using Xunit;
using ModuleHost.Core.Resilience;
using System;
using System.Threading;

namespace ModuleHost.Core.Tests
{
    public class ModuleCircuitBreakerTests
    {
        [Fact]
        public void CircuitBreaker_InitialState_Closed()
        {
            var breaker = new ModuleCircuitBreaker();
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_SingleFailure_StaysClosed()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Test failure");
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(1, breaker.FailureCount);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_ThresholdExceeded_OpensCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            breaker.RecordFailure("Failure 3");
            
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_Open_TransitionsToHalfOpenAfterTimeout()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 100);
            
            // Open the circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
            
            // Wait for reset timeout
            Thread.Sleep(150);
            
            // Should transition to HalfOpen
            Assert.True(breaker.CanRun());
            Assert.Equal(CircuitState.HalfOpen, breaker.State);
        }
        
        [Fact]
        public void CircuitBreaker_HalfOpen_SuccessClosesCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 50);
            
            // Open circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            
            // Wait and transition to HalfOpen
            Thread.Sleep(100);
            breaker.CanRun(); // Triggers transition
            
            // Successful execution
            breaker.RecordSuccess();
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(0, breaker.FailureCount);
        }
        
        [Fact]
        public void CircuitBreaker_HalfOpen_FailureReopensCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 50);
            
            // Open circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            
            // Wait and transition to HalfOpen
            Thread.Sleep(100);
            breaker.CanRun();
            
            // Failed execution - should reopen immediately
            breaker.RecordFailure("Failure 3");
            
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_Success_ResetsFailureCount()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            Assert.Equal(2, breaker.FailureCount);
            
            breaker.RecordSuccess();
            
            Assert.Equal(0, breaker.FailureCount);
            Assert.Equal(CircuitState.Closed, breaker.State);
        }
        
        [Fact]
        public void CircuitBreaker_Reset_ClosesCircuitManually()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 1);
            
            breaker.RecordFailure("Failure");
            Assert.Equal(CircuitState.Open, breaker.State);
            
            breaker.Reset();
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(0, breaker.FailureCount);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_InvalidThreshold_Throws()
        {
            Assert.Throws<ArgumentException>(() => 
                new ModuleCircuitBreaker(failureThreshold: 0));
            Assert.Throws<ArgumentException>(() => 
                new ModuleCircuitBreaker(failureThreshold: -1));
        }
        
        [Fact]
        public void CircuitBreaker_ThreadSafe_ConcurrentAccess()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 100);
            
            // Multiple threads recording failures concurrently
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        breaker.RecordFailure($"Failure {j}");
                        Thread.Sleep(1);
                        breaker.CanRun();
                    }
                });
            }
            
            Task.WaitAll(tasks);
            
            // Should have recorded 100 failures total
            Assert.Equal(100, breaker.FailureCount);
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Resilience/ModuleCircuitBreaker.cs`
- [ ] New test file: `ModuleHost.Core.Tests/ModuleCircuitBreakerTests.cs`
- [ ] 10+ unit tests passing

---

### Task 4.2: Safe Execution Wrapper ⭐⭐⭐

**Objective:** Wrap module execution with timeout detection and exception handling.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 4, Section 4.2 - "Safe Execution Wrapper"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Location: Where `Task.Run(() => module.Tick(view, dt))` is called (from BATCH-01)

**New Method to Add:**

```csharp
// Add to ModuleHostKernel.cs

/// <summary>
/// Safely executes a module with timeout and exception handling.
/// Integrates with circuit breaker for resilience.
/// </summary>
private async Task ExecuteModuleSafe(ModuleEntry entry, ISimulationView view, float dt)
{
    // 1. Check Circuit Breaker
    if (entry.CircuitBreaker != null && !entry.CircuitBreaker.CanRun())
    {
        // Circuit is open - skip execution
        return;
    }
    
    try
    {
        // 2. Determine Timeout
        int timeout = entry.MaxExpectedRuntimeMs;
        if (timeout <= 0)
        {
            timeout = 1000; // Default safety timeout
        }
        
        // 3. Create Cancellation Token (for cooperative cancellation)
        using var cts = new CancellationTokenSource(timeout);
        
        // 4. Run Module with Timeout Race
        var tickTask = Task.Run(() => 
        {
            try
            {
                entry.Module.Tick(view, dt);
            }
            catch (Exception ex)
            {
                // Log exception from inside module
                Console.Error.WriteLine($"[ModuleHost] Module '{entry.Module.Name}' threw exception: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                throw; // Re-throw to be caught by outer handler
            }
        }, cts.Token);
        
        var delayTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(tickTask, delayTask);
        
        // 5. Check Result
        if (completedTask == tickTask)
        {
            // Module completed within timeout
            await tickTask; // Propagate any exceptions
            
            // Success - record in circuit breaker
            entry.CircuitBreaker?.RecordSuccess();
        }
        else
        {
            // TIMEOUT
            entry.CircuitBreaker?.RecordFailure("Timeout");
            
            Console.Error.WriteLine(
                $"[ModuleHost][TIMEOUT] Module '{entry.Module.Name}' timed out after {timeout}ms. " +
                $"Task abandoned (may continue running in background as zombie).");
            
            // Note: We cannot forcefully kill the task in C#
            // It becomes a "zombie" task that may continue running
            // This is acceptable - the module will be disabled by circuit breaker
        }
    }
    catch (OperationCanceledException)
    {
        // Task was cancelled due to timeout
        entry.CircuitBreaker?.RecordFailure("Cancelled");
        
        Console.Error.WriteLine(
            $"[ModuleHost][CANCELLED] Module '{entry.Module.Name}' was cancelled.");
    }
    catch (Exception ex)
    {
        // Module crashed with unhandled exception
        entry.CircuitBreaker?.RecordFailure(ex.GetType().Name);
        
        Console.Error.WriteLine(
            $"[ModuleHost][CRASH] Module '{entry.Module.Name}' crashed: {ex.Message}");
        Console.Error.WriteLine($"Exception Type: {ex.GetType().FullName}");
        Console.Error.WriteLine(ex.StackTrace);
    }
}
```

**Integration with Dispatch Loop:**

Modify the dispatch section in `Update()` method:

```csharp
// In Update() method, dispatch phase:
if (entry.CurrentTask == null && ShouldRunThisFrame(entry))
{
    var view = entry.Provider.AcquireView();
    entry.LeasedView = view;
    float dt = entry.AccumulatedDeltaTime;
    
    // OLD: Direct Task.Run
    // entry.CurrentTask = Task.Run(() => entry.Module.Tick(view, dt));
    
    // NEW: Safe execution wrapper
    entry.CurrentTask = ExecuteModuleSafe(entry, view, dt);
    
    if (entry.Policy.Mode == RunMode.FrameSynced)
    {
        tasksToWait.Add(entry.CurrentTask);
    }
}
```

**Acceptance Criteria:**
- [ ] Circuit breaker checked before execution
- [ ] Timeout implemented via `Task.WhenAny`
- [ ] Exceptions caught and logged
- [ ] Circuit breaker updated on success/failure
- [ ] Timeout errors logged clearly
- [ ] Zombie tasks don't crash system
- [ ] Cooperative cancellation attempted (CancellationToken)

**Integration Tests to Write:**

```csharp
// File: ModuleHost.Tests/ResilienceIntegrationTests.cs

using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ModuleHost.Tests
{
    public class ResilienceIntegrationTests
    {
        [Fact(Timeout = 5000)]
        public async Task Resilience_HungModule_TimesOut()
        {
            // Create a module that hangs forever
            var hungModule = new TestModule
            {
                Name = "HungModule",
                TickAction = (view, dt) =>
                {
                    // Infinite loop
                    while (true)
                    {
                        Thread.Sleep(100);
                    }
                },
                MaxExpectedRuntimeMs = 200
            };
            
            var kernel = CreateKernel();
            kernel.RegisterModule(hungModule);
            kernel.Initialize();
            
            // Run several frames
            for (int i = 0; i < 5; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(50); // Allow async tasks to run
            }
            
            // Assert: System continues running (didn't freeze)
            // Assert: Module's circuit breaker opened
            var stats = kernel.GetExecutionStats();
            Assert.True(stats.Any(s => s.ModuleName == "HungModule" && s.CircuitState == CircuitState.Open));
        }
        
        [Fact]
        public async Task Resilience_CrashingModule_Isolated()
        {
            var crashingModule = new TestModule
            {
                Name = "CrashingModule",
                TickAction = (view, dt) =>
                {
                    throw new InvalidOperationException("Simulated crash");
                }
            };
            
            var healthyModule = new TestModule
            {
                Name = "HealthyModule",
                ExecutionCount = 0,
                TickAction = (view, dt) =>
                {
                    healthyModule.ExecutionCount++;
                }
            };
            
            var kernel = CreateKernel();
            kernel.RegisterModule(crashingModule);
            kernel.RegisterModule(healthyModule);
            kernel.Initialize();
            
            // Run 10 frames
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(10);
            }
            
            // Assert: Healthy module continued running
            Assert.True(healthyModule.ExecutionCount > 0);
            
            // Assert: Crashing module's circuit opened
            var stats = kernel.GetExecutionStats();
            var crashedModule = stats.First(s => s.ModuleName == "CrashingModule");
            Assert.Equal(CircuitState.Open, crashedModule.CircuitState);
        }
        
        [Fact]
        public async Task Resilience_FlakyModule_CircuitTrips_ThenRecovers()
        {
            int executionCount = 0;
            
            var flakyModule = new TestModule
            {
                Name = "FlakyModule",
                TickAction = (view, dt) =>
                {
                    executionCount++;
                    
                    // Fail first 3 times, then succeed
                    if (executionCount <= 3)
                    {
                        throw new Exception($"Flaky failure {executionCount}");
                    }
                },
                MaxExpectedRuntimeMs = 100,
                FailureThreshold = 3,
                CircuitResetTimeoutMs = 500
            };
            
            var kernel = CreateKernel();
            kernel.RegisterModule(flakyModule);
            kernel.Initialize();
            
            // Run frames until circuit trips
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
            }
            
            var stats1 = kernel.GetExecutionStats();
            var module1 = stats1.First(s => s.ModuleName == "FlakyModule");
            Assert.Equal(CircuitState.Open, module1.CircuitState);
            
            // Wait for reset timeout
            await Task.Delay(600);
            
            // Run more frames - should attempt recovery
            for (int i = 0; i < 10; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(20);
            }
            
            // Assert: Circuit recovered (closed)
            var stats2 = kernel.GetExecutionStats();
            var module2 = stats2.First(s => s.ModuleName == "FlakyModule");
            Assert.Equal(CircuitState.Closed, module2.CircuitState);
        }
        
        [Fact]
        public async Task Resilience_MultipleModulesFailing_SystemDegrades()
        {
            var goodModule = new TestModule { Name = "Good", ExecutionCount = 0 };
            var badModule1 = new TestModule { Name = "Bad1", TickAction = (v, d) => throw new Exception() };
            var badModule2 = new TestModule { Name = "Bad2", TickAction = (v, d) => { while(true) Thread.Sleep(10); } };
            var badModule3 = new TestModule { Name = "Bad3", TickAction = (v, d) => throw new Exception() };
            
            var kernel = CreateKernel();
            kernel.RegisterModule(goodModule);
            kernel.RegisterModule(badModule1);
            kernel.RegisterModule(badModule2);
            kernel.RegisterModule(badModule3);
            kernel.Initialize();
            
            // Run simulation
            for (int i = 0; i < 20; i++)
            {
                kernel.Update(0.016f);
                await Task.Delay(10);
            }
            
            // Assert: Good module kept running
            Assert.True(goodModule.ExecutionCount > 0);
            
            // Assert: Bad modules all opened circuits
            var stats = kernel.GetExecutionStats();
            Assert.All(stats.Where(s => s.ModuleName.StartsWith("Bad")),
                s => Assert.Equal(CircuitState.Open, s.CircuitState));
        }
    }
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (ExecuteModuleSafe method)
- [ ] New test file: `ModuleHost.Tests/ResilienceIntegrationTests.cs`
- [ ] 4+ integration tests passing

---

### Task 4.3: ModuleEntry Circuit Breaker Integration ⭐

**Objective:** Add circuit breaker and timeout configuration to ModuleEntry.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 4, Section 4.2 - "Configuration"

**Current Code Location:**
- File: `ModuleHost.Core/ModuleHostKernel.cs`
- Class: `ModuleEntry` (private nested class, around line 299)

**Fields to Add:**

```csharp
private class ModuleEntry
{
    // Existing fields...
    public IModule Module { get; set; } = null!;
    public ISnapshotProvider Provider { get; set; } = null!;
    public int FramesSinceLastRun { get; set; }
    
    // From BATCH-01
    public Task? CurrentTask { get; set; }
    public ISimulationView? LeasedView { get; set; }
    public float AccumulatedDeltaTime { get; set; }
    public uint LastRunTick { get; set; }
    
    // NEW for BATCH-04: Resilience
    public ModuleCircuitBreaker? CircuitBreaker { get; set; }
    public int MaxExpectedRuntimeMs { get; set; } = 100;
    public int FailureThreshold { get; set; } = 3;
    public int CircuitResetTimeoutMs { get; set; } = 5000;
}
```

**Initialization in RegisterModule:**

```csharp
public void RegisterModule(IModule module, ISnapshotProvider? provider = null)
{
    if (_initialized)
        throw new InvalidOperationException("Cannot register modules after initialization");
    
    var entry = new ModuleEntry
    {
        Module = module,
        Provider = provider, // Will be assigned if null during Initialize()
        
        // Initialize resilience components
        MaxExpectedRuntimeMs = module.MaxExpectedRuntimeMs ?? 100,
        FailureThreshold = module.FailureThreshold ?? 3,
        CircuitResetTimeoutMs = module.CircuitResetTimeoutMs ?? 5000,
        
        CircuitBreaker = new ModuleCircuitBreaker(
            failureThreshold: module.FailureThreshold ?? 3,
            resetTimeoutMs: module.CircuitResetTimeoutMs ?? 5000
        )
    };
    
    _modules.Add(entry);
}
```

**Acceptance Criteria:**
- [ ] CircuitBreaker field added to ModuleEntry
- [ ] Timeout configuration fields added
- [ ] CircuitBreaker initialized during registration
- [ ] Configuration pulled from IModule interface
- [ ] Defaults provided if module doesn't specify

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ModuleEntryResilienceTests.cs

[Fact]
public void ModuleEntry_Registration_InitializesCircuitBreaker()
{
    var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
    var module = new TestModule();
    
    kernel.RegisterModule(module);
    
    // Access internal state via reflection or testing API
    var entry = GetModuleEntry(kernel, module);
    
    Assert.NotNull(entry.CircuitBreaker);
    Assert.Equal(100, entry.MaxExpectedRuntimeMs);
}

[Fact]
public void ModuleEntry_CustomTimeouts_RespectedInRegistration()
{
    var module = new TestModule
    {
        MaxExpectedRuntimeMs = 500,
        FailureThreshold = 5,
        CircuitResetTimeoutMs = 10000
    };
    
    var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
    kernel.RegisterModule(module);
    
    var entry = GetModuleEntry(kernel, module);
    
    Assert.Equal(500, entry.MaxExpectedRuntimeMs);
    Assert.Equal(5, entry.FailureThreshold);
    Assert.Equal(10000, entry.CircuitResetTimeoutMs);
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (ModuleEntry class)
- [ ] Modified: `ModuleHost.Core/ModuleHostKernel.cs` (RegisterModule method)
- [ ] New tests in: `ModuleHost.Core.Tests/ModuleEntryResilienceTests.cs`
- [ ] 2+ unit tests passing

---

### Task 4.4: IModule Resilience Configuration ⭐

**Objective:** Extend IModule interface with timeout and circuit breaker configuration.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 4, Section 4.2 - "API Changes"

**File to Modify:**
- `ModuleHost.Core/Abstractions/IModule.cs`

**Properties to Add:**

```csharp
public interface IModule
{
    string Name { get; }
    ModuleTier Tier { get; }
    int UpdateFrequency { get; }
    
    void RegisterSystems(ISystemRegistry registry) { }
    void Tick(ISimulationView view, float deltaTime);
    
    // From BATCH-02 (if implemented)
    IReadOnlyList<Type>? WatchComponents { get; }
    IReadOnlyList<Type>? WatchEvents { get; }
    
    // NEW for BATCH-04: Resilience Configuration
    /// <summary>
    /// Maximum expected runtime for module execution in milliseconds.
    /// If execution exceeds this, module is timed out and circuit breaker is triggered.
    /// Default: 100ms
    /// </summary>
    int MaxExpectedRuntimeMs => 100;
    
    /// <summary>
    /// Number of consecutive failures before circuit breaker opens.
    /// Default: 3
    /// </summary>
    int FailureThreshold => 3;
    
    /// <summary>
    /// Time in milliseconds before attempting recovery after circuit opens.
    /// Default: 5000ms (5 seconds)
    /// </summary>
    int CircuitResetTimeoutMs => 5000;
}
```

**Acceptance Criteria:**
- [ ] Properties added with default implementations
- [ ] Existing modules compatible (use defaults)
- [ ] Modules can override for custom timeouts
- [ ] XML documentation clear

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/ModuleResilienceApiTests.cs

[Fact]
public void IModule_ResilienceDefaults_UseStandardValues()
{
    var module = new BasicTestModule(); // Doesn't override
    
    Assert.Equal(100, module.MaxExpectedRuntimeMs);
    Assert.Equal(3, module.FailureThreshold);
    Assert.Equal(5000, module.CircuitResetTimeoutMs);
}

[Fact]
public void IModule_CustomResilience_CanOverride()
{
    var module = new CustomTimeoutModule
    {
        MaxExpectedRuntimeMs = 500,
        FailureThreshold = 10,
        CircuitResetTimeoutMs = 1000
    };
    
    Assert.Equal(500, module.MaxExpectedRuntimeMs);
    Assert.Equal(10, module.FailureThreshold);
    Assert.Equal(1000, module.CircuitResetTimeoutMs);
}
```

**Deliverables:**
- [ ] Modified: `ModuleHost.Core/Abstractions/IModule.cs`
- [ ] New tests in: `ModuleHost.Core.Tests/ModuleResilienceApiTests.cs`
- [ ] 2+ unit tests passing

---

### Task 4.5: Resilience Integration Testing ⭐⭐

**Objective:** Comprehensive end-to-end testing of resilience system.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 4, entire chapter

**Test Scenarios:**

1. **Hung Module Doesn't Freeze System**
   - Module with infinite loop
   - System continues at stable frame rate
   - Circuit opens after timeout

2. **Crashing Module Isolated**
   - Module throwing exceptions
   - Other modules unaffected
   - Circuit opens after threshold

3. **Flaky Module Recovery**
   - Module fails intermittently
   - Circuit trips and opens
   - After timeout, attempts recovery
   - Successful recovery closes circuit

4. **Multiple Simultaneous Failures**
   - Several modules failing
   - System degrades gracefully
   - At least one module still running

5. **Circuit State Diagnostics**
   - Can query circuit state
   - Failure counts exposed
   - Logging comprehensive

**Integration Tests (Already shown in Task 4.2):**
- See `ResilienceIntegrationTests.cs` above

**Additional Diagnostic Tests:**

```csharp
[Fact]
public void Resilience_ExecutionStats_IncludeCircuitState()
{
    var module = new TestModule { Name = "TestModule" };
    var kernel = CreateKernel();
    kernel.RegisterModule(module);
    kernel.Initialize();
    
    var stats = kernel.GetExecutionStats();
    var moduleStat = stats.First(s => s.ModuleName == "TestModule");
    
    Assert.NotNull(moduleStat.CircuitState);
    Assert.NotNull(moduleStat.FailureCount);
    Assert.NotNull(moduleStat.LastExecutionTime);
}

[Fact]
public async Task Resilience_Logging_ComprehensiveErrors()
{
    // Capture console output
    var output = new StringWriter();
    Console.SetError(output);
    
    var module = new TestModule
    {
        TickAction = (v, d) => throw new InvalidOperationException("Test error")
    };
    
    var kernel = CreateKernel();
    kernel.RegisterModule(module);
    kernel.Initialize();
    
    kernel.Update(0.016f);
    await Task.Delay(50);
    
    var log = output.ToString();
    
    // Assert: Error logged
    Assert.Contains("Test error", log);
    Assert.Contains("InvalidOperationException", log);
    Assert.Contains(module.Name, log);
}
```

**Performance Test:**

```csharp
// File: ModuleHost.Benchmarks/ResilienceOverhead.cs

[MemoryDiagnoser]
public class ResilienceOverheadBenchmark
{
    [Benchmark]
    [Arguments(false)] // Without resilience
    [Arguments(true)]  // With resilience
    public void Resilience_Overhead(bool enableResilience)
    {
        // Measure: Execution overhead of safe wrapper
        // Target: <5% overhead
    }
}
```

**Deliverables:**
- [ ] Integration tests from Task 4.2
- [ ] Additional diagnostic tests
- [ ] Performance benchmark
- [ ] All tests passing (20+ total)

---

## ✅ Definition of Done

This batch is complete when:

- [ ] All 5 tasks completed
- [ ] Circuit breaker implemented and tested
- [ ] Safe execution wrapper working
- [ ] ModuleEntry integration complete
- [ ] IModule API extended
- [ ] All unit tests passing (20+ tests)
- [ ] All integration tests passing (4+ tests)
- [ ] Resilience demonstrated in real scenarios
- [ ] Performance overhead <5%
- [ ] No compiler warnings
- [ ] Changes committed to git
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Timeout accuracy | ±10ms | ±50ms |
| Safe wrapper overhead | <5% | <10% |
| Circuit trip latency | <1 frame | <3 frames |
| Recovery time | <5 seconds | <10 seconds |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| Unit tests | All passing |
| Integration tests | All passing |
| Compiler warnings | 0 |

---

## 🚧 Potential Challenges

### Challenge 1: Cannot Kill Hung Threads
**Issue:** C# doesn't support `Thread.Abort` safely  
**Solution:** Accept "zombie" tasks, disable module via circuit breaker  
**Ask if:** Concerned about thread pool exhaustion

### Challenge 2: Timeout Precision
**Issue:** `Task.Delay` accuracy limited by OS scheduler  
**Solution:** Accept ±10-50ms variance as acceptable  
**Ask if:** Need higher precision

### Challenge 3: Exception in Module Constructor
**Issue:** Exception before Tick() is called  
**Solution:** Catch in RegisterModule or Initialize phase  
**Ask if:** How to handle init failures

### Challenge 4: Nested Exceptions
**Issue:** AggregateException wrapping inner exceptions  
**Solution:** Unwrap and log inner exception details  
**Ask if:** Logging format unclear

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-04-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-04-QUESTIONS.md`

---

## 🔗 References

**Primary Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 4  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 04 section  
**Workflow README:** `../README.md`

**Code to Review:**
- `ModuleHost.Core/ModuleHostKernel.cs` - Main kernel (BATCH-01 changes)
- `ModuleHost.Core/Abstractions/IModule.cs` - Module interface
- Circuit breaker pattern references online

---

## 💡 Implementation Tips

1. **Start with Circuit Breaker** - it's independent and well-defined
2. **Test state transitions thoroughly** - state machines are tricky
3. **Log everything** - resilience debugging relies on good logs
4. **Use real scenarios** - infinite loop, null reference, etc.
5. **Don't worry about zombie threads** - they're acceptable in this design
6. **Benchmark early** - ensure overhead is minimal
7. **Think about observability** - expose diagnostics for monitoring

**This batch is critical for production stability - take time to get it right!**

Good luck! 🚀
