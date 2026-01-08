using System;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Defines how a module executes and what data strategy it uses.
    /// Replaces the binary Fast/Slow tier system with composable policies.
    /// </summary>
    public struct ExecutionPolicy
    {
        /// <summary>
        /// How the module runs (main thread, background synced, background async).
        /// </summary>
        public RunMode Mode { get; set; }
        
        /// <summary>
        /// What data structure the module uses (live world, replica, snapshot).
        /// </summary>
        public DataStrategy Strategy { get; set; }
        
        /// <summary>
        /// Target execution frequency in Hz (1-60).
        /// 0 means "every frame" (60Hz).
        /// </summary>
        public int TargetFrequencyHz { get; set; }
        
        /// <summary>
        /// Maximum expected runtime in milliseconds.
        /// Used for timeout detection and circuit breaker.
        /// </summary>
        public int MaxExpectedRuntimeMs { get; set; }
        
        /// <summary>
        /// Number of consecutive failures before circuit breaker opens.
        /// </summary>
        public int FailureThreshold { get; set; }
        
        /// <summary>
        /// Time in milliseconds before attempting recovery after circuit opens.
        /// </summary>
        public int CircuitResetTimeoutMs { get; set; }
        
        // ============================================================
        // FACTORY METHODS: Common Profiles
        // ============================================================
        
        /// <summary>
        /// Synchronous execution on main thread with direct world access.
        /// Use for: Physics, Input, critical systems that must run on main thread.
        /// </summary>
        public static ExecutionPolicy Synchronous() => new()
        {
            Mode = RunMode.Synchronous,
            Strategy = DataStrategy.Direct,
            TargetFrequencyHz = 60, // Every frame
            MaxExpectedRuntimeMs = 16, // Must complete within frame
            FailureThreshold = 1, // Immediate failure = fatal
            CircuitResetTimeoutMs = 1000
        };
        
        /// <summary>
        /// Frame-synced background execution with GDB replica.
        /// Main thread waits for completion.
        /// Use for: Network sync, Flight Recorder, low-latency background tasks.
        /// </summary>
        public static ExecutionPolicy FastReplica() => new()
        {
            Mode = RunMode.FrameSynced,
            Strategy = DataStrategy.GDB,
            TargetFrequencyHz = 60, // Every frame
            MaxExpectedRuntimeMs = 15, // Must complete quickly
            FailureThreshold = 3,
            CircuitResetTimeoutMs = 5000
        };
        
        /// <summary>
        /// Asynchronous background execution with SoD snapshots.
        /// Main thread doesn't wait. Module can span multiple frames.
        /// Use for: AI, Analytics, Pathfinding, slow computation.
        /// </summary>
        public static ExecutionPolicy SlowBackground(int frequencyHz) => new()
        {
            Mode = RunMode.Asynchronous,
            Strategy = DataStrategy.SoD,
            TargetFrequencyHz = frequencyHz,
            MaxExpectedRuntimeMs = Math.Max(100, 1000 / Math.Max(1, frequencyHz)), // At least 1 frame worth
            FailureThreshold = 5, // More tolerant of transient failures
            CircuitResetTimeoutMs = 10000
        };
        
        /// <summary>
        /// Custom policy builder (fluent API).
        /// </summary>
        public static ExecutionPolicy Custom() => new()
        {
            Mode = RunMode.Asynchronous,
            Strategy = DataStrategy.SoD,
            TargetFrequencyHz = 10,
            MaxExpectedRuntimeMs = 100,
            FailureThreshold = 3,
            CircuitResetTimeoutMs = 5000
        };
        
        // ============================================================
        // FLUENT CONFIGURATION
        // ============================================================
        
        public ExecutionPolicy WithMode(RunMode mode)
        {
            Mode = mode;
            return this;
        }
        
        public ExecutionPolicy WithStrategy(DataStrategy strategy)
        {
            Strategy = strategy;
            return this;
        }
        
        public ExecutionPolicy WithFrequency(int hz)
        {
            TargetFrequencyHz = hz;
            return this;
        }
        
        public ExecutionPolicy WithTimeout(int ms)
        {
            MaxExpectedRuntimeMs = ms;
            return this;
        }
        
        // ============================================================
        // VALIDATION
        // ============================================================
        
        /// <summary>
        /// Validates policy configuration for common mistakes.
        /// </summary>
        public void Validate()
        {
            // Apply defaults for uninitialized (0) values
            if (TargetFrequencyHz == 0) TargetFrequencyHz = 60;
            if (MaxExpectedRuntimeMs <= 0) MaxExpectedRuntimeMs = 100;
            if (FailureThreshold <= 0) FailureThreshold = 3;
            if (CircuitResetTimeoutMs <= 0) CircuitResetTimeoutMs = 1000;

            // Logic Validation
            if (Mode == RunMode.Synchronous && Strategy != DataStrategy.Direct)
            {
                throw new InvalidOperationException(
                    "Synchronous mode requires Direct strategy (no snapshot needed on main thread)");
            }
            
            if (Strategy == DataStrategy.Direct && Mode != RunMode.Synchronous)
            {
                throw new InvalidOperationException(
                    "Direct strategy only valid for Synchronous mode (background threads need snapshot)");
            }
            
            if (TargetFrequencyHz < 0 || TargetFrequencyHz > 60)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TargetFrequencyHz), 
                    "Frequency must be 0-60 Hz");
            }
        }
        
        public override string ToString()
        {
            return $"ExecutionPolicy({Mode}, {Strategy}, {TargetFrequencyHz}Hz, {MaxExpectedRuntimeMs}ms timeout)";
        }
    }
    
    /// <summary>
    /// How the module runs (threading model).
    /// </summary>
    public enum RunMode
    {
        /// <summary>
        /// Runs on main thread, blocks frame.
        /// Use for: Physics, critical systems.
        /// </summary>
        Synchronous,
        
        /// <summary>
        /// Runs on background thread, main waits for completion.
        /// Use for: Network, recorder, low-latency tasks.
        /// </summary>
        FrameSynced,
        
        /// <summary>
        /// Runs on background thread, main doesn't wait.
        /// Use for: AI, analytics, slow computation.
        /// </summary>
        Asynchronous
    }
    
    /// <summary>
    /// What data structure the module uses.
    /// </summary>
    public enum DataStrategy
    {
        /// <summary>
        /// Direct access to live world (only valid for Synchronous mode).
        /// No snapshot overhead, but runs on main thread.
        /// </summary>
        Direct,
        
        /// <summary>
        /// Persistent double-buffered replica (GDB).
        /// Low latency, synced every frame.
        /// Use for: Network, recorder.
        /// </summary>
        GDB,
        
        /// <summary>
        /// Pooled snapshot created on-demand (SoD).
        /// Higher latency, memory efficient.
        /// Use for: AI, analytics.
        /// </summary>
        SoD
    }
}
