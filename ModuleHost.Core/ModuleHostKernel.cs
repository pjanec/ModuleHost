// File: ModuleHost.Core/ModuleHostKernel.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using ModuleHost.Core.Scheduling;

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ModuleHost.Core.Tests")]

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
        
        // Scheduling
        private readonly SystemScheduler _globalScheduler = new();
        private bool _initialized = false;
        
        // Time accumulation
        private readonly Dictionary<IModule, float> _accumulatedTime = new();
        
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
                    
                    // Dispatch
                    var task = Task.Run(() =>
                    {
                        try
                        {
                            entry.Module.Tick(view, moduleDelta);
                            System.Threading.Interlocked.Increment(ref entry.ExecutionCount);
                        }
                        finally
                        {
                            // View release handled by HarvestEntry
                        }
                    });
                    
                    entry.CurrentTask = task;
                    entry.FramesSinceLastRun = 0;
                    entry.LastRunTick = _liveWorld.GlobalVersion; // Track version we started processing
                    
                    // Check Policy: If FrameSynced, we must wait
                    if (entry.Module.Policy.Mode == ModuleMode.FrameSynced)
                    {
                        tasksToWait.Add(task);
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

        public Dictionary<string, int> GetExecutionStats()
        {
            var stats = new Dictionary<string, int>();
            foreach (var entry in _modules)
            {
                stats[entry.Module.Name] = entry.ExecutionCount;
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

        private ISnapshotProvider CreateDefaultProvider(IModule module)
        {
            // Fast tier → GDB (DoubleBufferProvider)
            if (module.Tier == ModuleTier.Fast)
            {
                return new DoubleBufferProvider(_liveWorld, _eventAccumulator, _schemaSetup);
            }
            
            // Slow tier → SoD (OnDemandProvider)
            // Default: all components (no mask filtering)
            var mask = new BitMask256();
            for (int i = 0; i < 256; i++)
                mask.SetBit(i);
            
            return new OnDemandProvider(_liveWorld, _eventAccumulator, mask, _schemaSetup);
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
        }
    }
}
