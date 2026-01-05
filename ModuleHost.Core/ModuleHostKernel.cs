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
                    entry.LastView = view; // NEW: Track for playback
                    
                    // Calculate delta time for this module
                    float moduleDelta = (entry.FramesSinceLastRun + 1) * deltaTime;
                    
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
            
            _currentFrame++;
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
