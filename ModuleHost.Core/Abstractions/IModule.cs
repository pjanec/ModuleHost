// File: ModuleHost.Core/Abstractions/IModule.cs
using Fdp.Kernel;

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
        /// Register systems for this module.
        /// Called once during initialization.
        /// </summary>
        /// <param name="registry">System registry</param>
        void RegisterSystems(ISystemRegistry registry)
        {
            // Default: no systems (modules can override)
        }

        /// <summary>
        /// Execution policy (FrameSynced vs Async).
        /// Default implementation maps Tier.Fast -> FrameSynced, Tier.Slow -> Async.
        /// </summary>
        ModuleExecutionPolicy Policy => Tier == ModuleTier.Fast 
            ? ModuleExecutionPolicy.DefaultFast 
            : ModuleExecutionPolicy.DefaultSlow;

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
