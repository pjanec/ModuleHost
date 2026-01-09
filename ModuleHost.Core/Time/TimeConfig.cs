using System.Diagnostics;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Configuration for time controllers.
    /// </summary>
    public class TimeConfig
    {
        public static TimeConfig Default => new();
        
        /// <summary>
        /// PLL gain for slave synchronization (0.0 - 1.0).
        /// Higher = faster convergence, lower = smoother.
        /// </summary>
        public double PLLGain { get; set; } = 0.1;
        
        /// <summary>
        /// Maximum frequency deviation for PLL (±5% default).
        /// Prevents physics instability from aggressive corrections.
        /// </summary>
        public double MaxSlew { get; set; } = 0.05;
        
        /// <summary>
        /// Error threshold triggering hard snap (milliseconds).
        /// </summary>
        public double SnapThresholdMs { get; set; } = 500.0;
        
        /// <summary>
        /// Number of samples for jitter filtering.
        /// </summary>
        public int JitterWindowSize { get; set; } = 5;
        
        /// <summary>
        /// Estimated average network latency (ticks).
        /// Used to compensate for transmission delay.
        /// </summary>
        public long AverageLatencyTicks { get; set; } = Stopwatch.Frequency * 2 / 1000; // 2ms default
        
        /// <summary>
        /// Fixed delta time for deterministic lockstep (seconds).
        /// Typically 16.67ms (60Hz) or 33.33ms (30Hz).
        /// </summary>
        public float FixedDeltaSeconds { get; set; } = 1.0f / 60.0f;  // 60 FPS
        
        /// <summary>
        /// Timeout for waiting on ACKs in lockstep mode (milliseconds).
        /// If exceeded, log warning (but still wait).
        /// </summary>
        public double LockstepTimeoutMs { get; set; } = 1000.0;  // 1 second
    }
}
