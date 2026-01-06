// File: ModuleHost.Core/ModuleHostKernel.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using ModuleHost.Core.Scheduling;

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
        /// 3. Dispatches modules (async execution)
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!_initialized)
                throw new InvalidOperationException("Must call Initialize() before Update()");
            
            // ═══════════ PHASE: Input ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.Input, _liveWorld, deltaTime);
            
            // ═══════════ PHASE: BeforeSync ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.BeforeSync, _liveWorld, deltaTime);
            
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
                // ⚠️ CRITICAL: Accumulate time for ALL modules
                if (!_accumulatedTime.ContainsKey(entry.Module))
                    _accumulatedTime[entry.Module] = 0f;
                
                _accumulatedTime[entry.Module] += deltaTime;
                
                // Check if module should run this frame
                if (ShouldRunThisFrame(entry))
                {
                    // Acquire view
                    var view = entry.Provider.AcquireView();
                    entry.LastView = view; // NEW: Track for playback
                    
                    // ⚠️ CRITICAL: Pass accumulated time, not frame time
                    float moduleDelta = _accumulatedTime[entry.Module];
                    
                    // Dispatch async
                    var task = Task.Run(() =>
                    {
                        try
                        {
                            entry.Module.Tick(view, moduleDelta);
                            System.Threading.Interlocked.Increment(ref entry.ExecutionCount);
                        }
                        finally
                        {
                            // Always release view (even on exception)
                            entry.Provider.ReleaseView(view);
                        }
                    });
                    
                    tasks.Add(task);
                    
                    // ⚠️ CRITICAL: Reset accumulator after execution
                    _accumulatedTime[entry.Module] = 0f;
                    
                    entry.FramesSinceLastRun = 0;
                }
                else
                {
                    entry.FramesSinceLastRun++;
                }
            }
            
            // Wait for all modules to complete
            // (In production, might use timeout or separate phase)
            // Note: This blocks the main thread, which is fine for this phase as per design.
            // BATCH-05 might move this to a separate phase if needed.
            Task.WaitAll(tasks.ToArray());
            
            // NEW: Playback commands from modules
            foreach (var entry in _modules)
            {
                // Get command buffer from provider's view
                if (entry.LastView is EntityRepository repo)
                {
                    // Iterate ALL values tracked by ThreadLocal (from all threads that used this repo)
                    foreach (var cmdBuffer in repo._perThreadCommandBuffer.Values)
                    {
                        if (cmdBuffer.HasCommands)
                        {
                            cmdBuffer.Playback(_liveWorld);
                            // Clear is called inside Playback automatically? 
                            // EntityCommandBuffer.Playback says: "Clear buffer after playback -> Clear();"
                            // So we don't need to call Clear() explicitly if Playback does it.
                            // Let's check EntityCommandBuffer.Playback implementation.
                        }
                    }
                }
                entry.LastView = null;
            }
            
            // ═══════════ PHASE: PostSimulation ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.PostSimulation, _liveWorld, deltaTime);
            
            // ═══════════ PHASE: Export ═══════════
            _globalScheduler.ExecutePhase(SystemPhase.Export, _liveWorld, deltaTime);
            
            _currentFrame++;
        }

        public Dictionary<string, int> GetExecutionStats()
        {
            var stats = new Dictionary<string, int>();
            foreach (var entry in _modules)
            {
                stats[entry.Module.Name] = entry.ExecutionCount;
                // Reset count after reading (as per 1 Hz display logic usually, or keep it?)
                // Instructions say "Module Executions (last second)".
                // Usually this means we should clear it or return rate.
                // But the Renderer says: "Module Executions (last second)". 
                // If I reset here, the renderer gets 0 if it calls it multiple times? 
                // Renderer calls it every 60 frames.
                // So I should probably reset it here.
                entry.ExecutionCount = 0;
            }
            return stats;
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
        
        private class ModuleEntry
        {
            public IModule Module { get; set; } = null!;
            public ISnapshotProvider Provider { get; set; } = null!;
            public int FramesSinceLastRun { get; set; }
            public ISimulationView? LastView { get; set; }
            public int ExecutionCount; // Field for Interlocked
        }
    }
}
