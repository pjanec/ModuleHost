// File: ModuleHost.Core/ModuleHostKernel.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using ModuleHost.Core.Scheduling;
using ModuleHost.Core.Resilience;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("ModuleHost.Core.Tests")]
[assembly: InternalsVisibleTo("ModuleHost.Tests")]

namespace ModuleHost.Core
{
    public struct ModuleStats
    {
        public string ModuleName;
        public int ExecutionCount;
        public CircuitState CircuitState;
        public int FailureCount;
    }

    /// <summary>
    /// Central orchestrator for module execution.
    /// Manages module registration, provider assignment, and execution pipeline.
    /// </summary>
    public sealed class ModuleHostKernel : IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly List<ModuleEntry> _modules = new();
        private SnapshotPool? _snapshotPool;
        
        // Scheduling
        private readonly SystemScheduler _globalScheduler = new();
        private bool _initialized = false;
        
        private uint _currentFrame = 0;
        
        public ModuleHostKernel(EntityRepository liveWorld, EventAccumulator eventAccumulator)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
        }
        
        /// <summary>
        /// Register a global system (runs on main thread).
        /// </summary>
        public void RegisterGlobalSystem<T>(T system) where T : IModuleSystem
        {
            if (_initialized)
                throw new InvalidOperationException("Cannot register systems after Initialize() called");
            
            _globalScheduler.RegisterSystem(system);
        }
        
        /// <summary>
        /// Access to system scheduler for profiling/debugging.
        /// </summary>
        public SystemScheduler SystemScheduler => _globalScheduler;
        
        /// <summary>
        /// Initialize kernel: build execution orders, validate dependencies.
        /// Must be called after all modules/systems registered, before Update().
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
                throw new InvalidOperationException("Already initialized");
            
            // Create global pool
            _snapshotPool = new SnapshotPool(_schemaSetup, warmupCount: 10);
            
            // Auto-assign providers to modules
            AutoAssignProviders();
            
            // Modules register their systems
            foreach (var entry in _modules)
            {
                entry.Module.RegisterSystems(_globalScheduler);
            }
            
            // Build dependency graphs and sort
            _globalScheduler.BuildExecutionOrders();
            
            // Throws CircularDependencyException if cycles detected
            
            _initialized = true;
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
                            initialPoolSize: 5
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
            
            // Final check: Ensure all modules have providers
            foreach (var entry in _modules)
            {
                if (entry.Provider == null)
                {
                    // Fallback (should not happen if logic covers all cases)
                     var mask = GetComponentMask(entry.Module);
                     entry.Provider = new OnDemandProvider(_liveWorld, _eventAccumulator, mask, _schemaSetup);
                }
            }
        }

        private BitMask256 GetComponentMask(IModule module)
        {
            // Helper to get component requirements from module
            // This might need module API enhancement (could return all for now)
            // For BATCH-03, we return ALL components (default conservative behavior)
            var mask = new BitMask256();
            for(int i=0; i<256; i++) mask.SetBit(i);
            return mask; 
        }

        /// <summary>
        /// Registers a module with optional provider override.
        /// If provider is null, default will be assigned during Initialize().
        /// </summary>
        public void RegisterModule(IModule module, ISnapshotProvider? provider = null)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            
            if (_initialized)
                throw new InvalidOperationException("Cannot register modules after initialization");
            
            var entry = new ModuleEntry
            {
                Module = module,
                Provider = provider!, // Can be null initially, assigned in Initialize
                FramesSinceLastRun = 0,
                
                // Initialize resilience components
                MaxExpectedRuntimeMs = module.MaxExpectedRuntimeMs,
                FailureThreshold = module.FailureThreshold,
                CircuitResetTimeoutMs = module.CircuitResetTimeoutMs,
                
                CircuitBreaker = new ModuleCircuitBreaker(
                    failureThreshold: module.FailureThreshold,
                    resetTimeoutMs: module.CircuitResetTimeoutMs
                )
            };
            
            _modules.Add(entry);
        }
        
        /// <summary>
        /// Main update loop (called every simulation frame).
        /// 1. Captures event history
        /// 2. Updates providers (syncs replicas/snapshots)
        /// 3. Harvester: Checks for completed async modules
        /// 4. Dispatcher: Schedules new module execution
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!_initialized)
                throw new InvalidOperationException("Must call Initialize() before Update()");
            
            // 1. ADVANCE TIME
            _liveWorld.Tick();

            // ═══════════ PHASE: Input ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.Input, _liveWorld, deltaTime);
            
            // ═══════════ PHASE: BeforeSync ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.BeforeSync, _liveWorld, deltaTime);
            
            // FLUSH LIVE WORLD BUFFERS
            if (_liveWorld._perThreadCommandBuffer != null)
            {
                foreach (var cmdBuffer in _liveWorld._perThreadCommandBuffer.Values)
                {
                    if (cmdBuffer.HasCommands)
                    {
                        cmdBuffer.Playback(_liveWorld);
                    }
                }
            }
            
            // 3. EVENT SWAP (Critical: Make Input events visible)
            _liveWorld.Bus.SwapBuffers();
            
            // 4. SYNC & CAPTURE
            // Capture event history
            // Use GlobalVersion to align with SnapshotProvider logic which tracks GlobalVersion
            _eventAccumulator.CaptureFrame(_liveWorld.Bus, _liveWorld.GlobalVersion);
            
            // Update Sync-Point Providers
            foreach (var entry in _modules)
            {
                entry.Provider.Update();
            }
            
            // ═══════════ HARVEST PHASE ═══════════
            foreach (var entry in _modules)
            {
                // Harvest completed async tasks
                if (entry.CurrentTask != null && entry.CurrentTask.IsCompleted)
                {
                    HarvestEntry(entry);
                }
            }
            
            // ═══════════ DISPATCH PHASE ═══════════
            var tasksToWait = new List<Task>();
            
            foreach (var entry in _modules)
            {
                // Always accumulate time (logic time)
                entry.AccumulatedDeltaTime += deltaTime;
                
                // If still running, let it continue (accumulating time for next run)
                if (entry.CurrentTask != null)
                {
                    continue;
                }
                
                // If idle, check frequency
                if (ShouldRunThisFrame(entry))
                {
                    // Acquire view
                    var view = entry.Provider.AcquireView();
                    entry.LeasedView = view;
                    entry.LastView = view; // Keep for reference if needed
                    
                    // Consume accumulated time for this tick
                    float moduleDelta = entry.AccumulatedDeltaTime;
                    entry.AccumulatedDeltaTime = 0f;
                    
                    // Dispatch safe execution
                    entry.CurrentTask = ExecuteModuleSafe(entry, view, moduleDelta);
                    
                    entry.FramesSinceLastRun = 0;
                    entry.LastRunTick = _liveWorld.GlobalVersion > 0 ? _liveWorld.GlobalVersion - 1 : 0; // Track version we started processing (Version-1 to catch inclusive)
                    
                    // Check Policy: If FrameSynced, we must wait
                    if (entry.Module.Policy.Mode == ModuleMode.FrameSynced)
                    {
                        tasksToWait.Add(entry.CurrentTask);
                    }
                }
                else
                {
                    entry.FramesSinceLastRun++;
                }
            }
            
            // ═══════════ SYNC WAIT (Fast Modules) ═══════════
            if (tasksToWait.Count > 0)
            {
                Task.WaitAll(tasksToWait.ToArray());
                
                // Harvest immediately
                foreach (var entry in _modules)
                {
                    if (entry.CurrentTask != null && entry.Module.Policy.Mode == ModuleMode.FrameSynced)
                    {
                        HarvestEntry(entry);
                    }
                }
            }
            
            // ═══════════ PHASE: PostSimulation ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.PostSimulation, _liveWorld, deltaTime);
            
            // ═══════════ PHASE: Export ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.Export, _liveWorld, deltaTime);
            
            _currentFrame++;
        }

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
                        System.Threading.Interlocked.Increment(ref entry.ExecutionCount);
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

        private void HarvestEntry(ModuleEntry entry)
        {
            // 1. Playback commands
            if (entry.LeasedView is EntityRepository repo)
            {
                if (repo._perThreadCommandBuffer != null)
                {
                    foreach (var cmdBuffer in repo._perThreadCommandBuffer.Values)
                    {
                        if (cmdBuffer.HasCommands)
                            cmdBuffer.Playback(_liveWorld);
                    }
                }
            }
            
            // 2. Release view
            if (entry.LeasedView != null)
            {
                entry.Provider.ReleaseView(entry.LeasedView);
                entry.LeasedView = null;
            }
            
            // 3. Handle faults
            if (entry.CurrentTask?.IsFaulted == true)
            {
                Console.Error.WriteLine($"Module {entry.Module.Name} failed: {entry.CurrentTask.Exception}");
            }
            
            // 4. Cleanup
            entry.CurrentTask = null;
        }

        public List<ModuleStats> GetExecutionStats()
        {
            var stats = new List<ModuleStats>();
            foreach (var entry in _modules)
            {
                stats.Add(new ModuleStats
                {
                    ModuleName = entry.Module.Name,
                    ExecutionCount = entry.ExecutionCount,
                    CircuitState = entry.CircuitBreaker?.State ?? CircuitState.Closed,
                    FailureCount = entry.CircuitBreaker?.FailureCount ?? 0
                });
                
                // Note: We don't reset ExecutionCount here anymore for better observability, or we should?
                // The original code did: entry.ExecutionCount = 0;
                // If we don't reset, it accumulates. If we reset, it's per-call.
                // Usually GetStats implies current snapshot.
                // But for tests checking "did it run this frame", rest is helpful.
                // However, ExecutionCount is now interlocked incremented.
                // Let's keep reset for compatibility with test expectations ("Did it run in this update?").
                // BUT "GetExecutionStats" usually shouldn't side-effect reset numbers.
                // Use a separate "ResetStats"? Or assume usage pattern.
                // The original implementation reset it. I will keep resetting it to avoid breaking tests logic that assumes per-frame stats.
                entry.ExecutionCount = 0;
            }
            return stats;
        }
        
        private bool ShouldRunThisFrame(ModuleEntry entry)
        {
            var policy = entry.Module.Policy;
            
            // Check Trigger Policy
            switch (policy.Trigger)
            {
                case TriggerType.Always:
                    // Legacy behavior + Frequency throttling
                    if (entry.Module.Tier == ModuleTier.Fast) return true;
                    // For Slow/Async, default to running if frequency allows (Frame-based throttling)
                    int frequency = Math.Max(1, entry.Module.UpdateFrequency);
                    return (entry.FramesSinceLastRun + 1) >= frequency;

                case TriggerType.Interval:
                    // Time-based throttling (IntervalMs)
                    return entry.AccumulatedDeltaTime >= (policy.IntervalMs / 1000f);

                case TriggerType.OnEvent:
                    if (policy.TriggerArg == null) return false;
                    return _liveWorld.Bus.HasEvent(policy.TriggerArg);

                case TriggerType.OnComponentChange:
                    if (policy.TriggerArg == null) return false;
                    return _liveWorld.HasComponentChanged(policy.TriggerArg, entry.LastRunTick);

                default:
                    return false;
            }
        }
        
        private Action<EntityRepository>? _schemaSetup;

        /// <summary>
        /// Sets the schema setup action used to initialize registered component types
        /// on internal repositories (e.g. snapshots for SoD or replicas for GDB).
        /// </summary>
        public void SetSchemaSetup(Action<EntityRepository> setup)
        {
            _schemaSetup = setup;
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
        
        internal class ModuleEntry
        {
            public IModule Module { get; set; } = null!;
            public ISnapshotProvider Provider { get; set; } = null!;
            public int FramesSinceLastRun { get; set; }
            public ISimulationView? LastView { get; set; }
            public int ExecutionCount; // Field for Interlocked
            
            // Async State (NEW - for World C)
            public Task? CurrentTask { get; set; }
            public ISimulationView? LeasedView { get; set; }
            public float AccumulatedDeltaTime { get; set; }
            public uint LastRunTick { get; set; }  // For reactive scheduling prep
            
            // NEW for BATCH-04: Resilience
            public ModuleCircuitBreaker? CircuitBreaker { get; set; }
            public int MaxExpectedRuntimeMs { get; set; }
            public int FailureThreshold { get; set; }
            public int CircuitResetTimeoutMs { get; set; }
        }
    }
}
