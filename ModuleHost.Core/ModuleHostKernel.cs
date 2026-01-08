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
            // Modules can manually set provider; only auto-assign if null
            var modulesNeedingProvider = _modules.Where(m => m.Provider == null).ToList();
            
            if (modulesNeedingProvider.Count == 0)
                return;
            
            // Validate policies AND Cache component masks
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
                
                // Cache component mask for optimization
                entry.ComponentMask = GetComponentMask(entry.Module);
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
                        foreach (var entry in moduleList)
                        {
                            entry.Provider = null!; 
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
                            // Use cached mask
                            var mask = entry.ComponentMask;
                            
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
                            // Convoy: SharedSnapshotProvider
                            var unionMaskSoD = CalculateUnionMask(moduleList);
                            
                            var sharedProvider = new SharedSnapshotProvider(
                                _liveWorld,
                                _eventAccumulator,
                                unionMaskSoD,
                                _snapshotPool!
                            );
                            
                            foreach (var entry in moduleList)
                            {
                                entry.Provider = sharedProvider;
                            }
                        }
                        break;
                }
            }
            
            // Final check: Ensure all non-Direct modules have providers
            foreach (var entry in _modules)
            {
                if (entry.Provider == null && entry.Module.Policy.Strategy != DataStrategy.Direct)
                {
                    // Fallback (should not happen if logic covers all cases)
                    // If we missed caching for some reason (e.g. manually added?) - recalculate or use cache?
                    // Safe to call GetComponentMask again if cache is empty, but we set it above.
                    // But if module was NOT in modulesNeedingProvider (already had provider), we skipped loop.
                    // But then provider is not null.
                    
                    // What if provider set manually but we want to optimize?
                    // We only touch modulesNeedingProvider.
                    
                    if (entry.ComponentMask.IsEmpty() && !entry.Module.Policy.Strategy.ToString().Contains("Direct")) 
                    {
                         // If mask not set, compute it. 
                         // Note: IsEmpty() might be expensive or ambiguous (0 components required).
                         // But we can just call GetComponentMask, it's safe.
                         entry.ComponentMask = GetComponentMask(entry.Module);
                    }
                    
                     var mask = entry.ComponentMask;
                     entry.Provider = new OnDemandProvider(_liveWorld, _eventAccumulator, mask, _schemaSetup);
                }
            }
        }

        private BitMask256 CalculateUnionMask(List<ModuleEntry> modules)
        {
            var unionMask = new BitMask256();
            
            foreach (var entry in modules)
            {
                unionMask.BitwiseOr(entry.ComponentMask);
            }
            
            return unionMask;
        }

        private BitMask256 GetComponentMask(IModule module)
        {
            var requiredComponents = module.GetRequiredComponents();
            
            // Default: sync all components (conservative)
            if (requiredComponents == null || !requiredComponents.Any())
            {
                return CreateFullMask();
            }
            
            // Optimized: sync only required components
            var mask = new BitMask256();
            foreach (var componentType in requiredComponents)
            {
                int typeId = ComponentTypeRegistry.GetId(componentType);
                if (typeId >= 0 && typeId < 256)
                {
                    mask.SetBit(typeId);
                }
                else
                {
                    // Log warning: Component type not registered
                    Console.WriteLine($"Warning: Module '{module.Name}' requires unregistered component: {componentType.Name}");
                }
            }
            
            return mask;
        }
        
        private BitMask256 CreateFullMask()
        {
            var mask = new BitMask256();
            for (int i = 0; i < 256; i++)
            {
                mask.SetBit(i);
            }
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
            
            var policy = module.Policy;
            
            // Validate locally to ensure defaults (like FailureThreshold) are set.
            // We suppress exceptions here because specific validation errors (like Mode mismatches)
            // should be handled during Initialize() phase or AutoAssignProviders(), 
            // consistent with previous behavior.
            try { policy.Validate(); } catch { }

            var entry = new ModuleEntry
            {
                Module = module,
                Provider = provider!, 
                FramesSinceLastRun = 0,
                
                // Initialize resilience components from locally validated Policy
                MaxExpectedRuntimeMs = policy.MaxExpectedRuntimeMs,
                FailureThreshold = policy.FailureThreshold,
                CircuitResetTimeoutMs = policy.CircuitResetTimeoutMs,
                
                CircuitBreaker = new ModuleCircuitBreaker(
                    failureThreshold: policy.FailureThreshold,
                    resetTimeoutMs: policy.CircuitResetTimeoutMs
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
                // Only update provider if it exists (Direct strategy has null)
                entry.Provider?.Update();
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
                    ISimulationView view;
                    
                    if (entry.Module.Policy.Strategy == DataStrategy.Direct)
                    {
                        // Direct access to live world (Synchronous only)
                        view = _liveWorld;
                    }
                    else
                    {
                        if (entry.Provider == null)
                        {
                            // Should theoretically not happen if Validate() worked, but safe guard
                             continue;
                        }
                        // Acquire view from provider
                        view = entry.Provider.AcquireView();
                    }
                    
                    entry.LeasedView = view;
                    entry.LastView = view; // Keep for reference if needed
                    
                    // Consume accumulated time for this tick
                    float moduleDelta = entry.AccumulatedDeltaTime;
                    entry.AccumulatedDeltaTime = 0f;
                    
                    // Dispatch execution
                    if (entry.Module.Policy.Mode == RunMode.Synchronous)
                    {
                        // Synchronous run (main thread)
                        try
                        {
                            entry.Module.Tick(view, moduleDelta);
                            System.Threading.Interlocked.Increment(ref entry.ExecutionCount);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[ModuleHost] Sync Module '{entry.Module.Name}' exception: {ex}");
                        }
                        
                        // Release view (no-op for Direct, but valid for completeness)
                        // Actually Direct view is _liveWorld which doesn't need release.
                        if (entry.Module.Policy.Strategy != DataStrategy.Direct)
                        {
                            entry.Provider?.ReleaseView(view);
                        }
                        entry.LeasedView = null;
                        entry.CurrentTask = null; // No task
                    }
                    else
                    {
                        // Safe Execution (Async/FrameSynced)
                        entry.CurrentTask = ExecuteModuleSafe(entry, view, moduleDelta);
                    }
                    
                    entry.FramesSinceLastRun = 0;
                    entry.LastRunTick = _liveWorld.GlobalVersion > 0 ? _liveWorld.GlobalVersion - 1 : 0; 
                    
                    // Check Policy: If FrameSynced, we must wait
                    if (entry.Module.Policy.Mode == RunMode.FrameSynced)
                    {
                        if (entry.CurrentTask != null)
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
                    if (entry.CurrentTask != null && entry.Module.Policy.Mode == RunMode.FrameSynced)
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
                // We must release the view!
                // NOTE: If we return here, HarvestEntry won't be called because CurrentTask terminates early?
                // Actually if ExecuteModuleSafe returns Task, and we await it.
                // But if we return here 'early', the task completes.
                // HarvestEntry checks 'IsCompleted'.
                // AND HarvestEntry releases view.
                // So returning here is Safe IF we ensure HarvestEntry runs.
                // HarvestEntry runs in Update() loop.
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
                entry.Provider?.ReleaseView(entry.LeasedView); // Null check just in case
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
                
                entry.ExecutionCount = 0;
            }
            return stats;
        }
        
        private bool ShouldRunThisFrame(ModuleEntry entry)
        {
            var policy = entry.Module.Policy;
            
            // 1. Reactive Check (Batch-02)
            bool triggered = false;
            
            if (entry.Module.WatchEvents != null && entry.Module.WatchEvents.Count > 0)
            {
                foreach (var evt in entry.Module.WatchEvents)
                {
                    if (_liveWorld.Bus.HasEvent(evt))
                    {
                        triggered = true;
                        break;
                    }
                }
            }
            
            if (!triggered && entry.Module.WatchComponents != null && entry.Module.WatchComponents.Count > 0)
            {
                foreach (var comp in entry.Module.WatchComponents)
                {
                     if (_liveWorld.HasComponentChanged(comp, entry.LastRunTick))
                     {
                         triggered = true;
                         break;
                     }
                }
            }
            
            if (triggered) return true;
            
            // 2. Periodic Check
            int targetHz = policy.TargetFrequencyHz;
            if (targetHz <= 0) targetHz = 60; // 0 means every frame
            
            if (targetHz >= 60) return true;
            
            int framesToSkip = 60 / targetHz;
            if (framesToSkip < 1) framesToSkip = 1;
            
            return (entry.FramesSinceLastRun + 1) >= framesToSkip;
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
            
            // Caching
            public BitMask256 ComponentMask; 
            
            // NEW for BATCH-04: Resilience
            public ModuleCircuitBreaker? CircuitBreaker { get; set; }
            public int MaxExpectedRuntimeMs { get; set; }
            public int FailureThreshold { get; set; }
            public int CircuitResetTimeoutMs { get; set; }
        }
    }
}
