using System;
using System.Collections.Generic;
using Fdp.Kernel;

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
        ExecutionPolicy Policy 
        {
            get
            {
                #pragma warning disable CS0618 // Type or member is obsolete
                // Fallback for legacy modules implementing Tier
                if (Tier == ModuleTier.Fast)
                {
                    return ExecutionPolicy.FastReplica();
                }
                else
                {
                    // Convert UpdateFrequency
                    // UpdateFrequency: 1 = 60Hz. 2 = 30Hz.
                    int freq = UpdateFrequency;
                    if (freq <= 0) freq = 1;
                    int targetHz = 60 / freq;
                    return ExecutionPolicy.SlowBackground(targetHz);
                }
                #pragma warning restore CS0618
            }
        }
        
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
        IReadOnlyList<Type>? WatchComponents => null;
        
        /// <summary>
        /// Event types to watch for firing (reactive scheduling).
        /// </summary>
        IReadOnlyList<Type>? WatchEvents => null;
        
        /// <summary>
        /// Components this module reads. Used for convoy snapshot filtering.
        /// Returning null or empty defaults to ALL components (safe but inefficient).
        /// </summary>
        /// <returns>Component types required by this module, or null for all</returns>
        IEnumerable<Type>? GetRequiredComponents() => null;
        
        // ============================================================
        // DEPRECATED (Kept for backward compatibility)
        // ============================================================
        
        /// <summary>
        /// [OBSOLETE] Use Policy instead.
        /// </summary>
        [Obsolete("Use Policy.Mode and Policy.Strategy instead. Will be removed in v2.0.")]
        ModuleTier Tier 
        {
            get
            {
                return Policy.Mode == RunMode.Synchronous || Policy.Mode == RunMode.FrameSynced
                    ? ModuleTier.Fast
                    : ModuleTier.Slow;
            }
        }
        
        /// <summary>
        /// [OBSOLETE] Use Policy.TargetFrequencyHz instead.
        /// </summary>
        [Obsolete("Use Policy.TargetFrequencyHz instead. Will be removed in v2.0.")]
        int UpdateFrequency
        {
            get
            {
                if (Policy.TargetFrequencyHz == 0 || Policy.TargetFrequencyHz >= 60)
                    return 1; // Every frame
                
                return 60 / Policy.TargetFrequencyHz; // Convert Hz to frame count
            }
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
