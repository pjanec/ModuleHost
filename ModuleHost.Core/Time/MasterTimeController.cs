using System;
using System.Diagnostics;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master time controller for Continuous mode.
    /// Owns authoritative simulation time and publishes TimePulse to network.
    /// </summary>
    public class MasterTimeController : ITimeController
    {
        private readonly Stopwatch _wallClock;
        private readonly IDataWriter _timePulseWriter;
        private readonly TimeConfig _config;
        
        // Time state
        private double _simTimeBase = 0.0;
        private long _scaleChangeWallTicks = 0;
        private float _timeScale = 1.0f;
        private long _frameNumber = 0;
        
        // Network publishing
        private long _lastPulseTicks = 0;
        private static readonly long PulseIntervalTicks = Stopwatch.Frequency; // 1Hz
        private long _lastFrameTicks = 0;

        public MasterTimeController(IDataWriter timePulseWriter, TimeConfig? config = null)
        {
            _wallClock = Stopwatch.StartNew();
            _timePulseWriter = timePulseWriter ?? throw new ArgumentNullException(nameof(timePulseWriter));
            _config = config ?? TimeConfig.Default;
            _scaleChangeWallTicks = _wallClock.ElapsedTicks;
            _lastPulseTicks = _wallClock.ElapsedTicks;
            _lastFrameTicks = _wallClock.ElapsedTicks; // Initialize to avoid huge delta on first frame
        }
        
        public void Update(out float dt, out double totalTime)
        {
            _frameNumber++;
            
            // Calculate wall delta
            long currentWallTicks = _wallClock.ElapsedTicks;
            double wallDelta = (currentWallTicks - _lastFrameTicks) / (double)Stopwatch.Frequency;
            _lastFrameTicks = currentWallTicks;
            
            // Calculate simulation delta (respecting scale)
            dt = (float)(wallDelta * _timeScale);
            
            // Calculate total simulation time
            totalTime = _simTimeBase + 
                       (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            // Publish TimePulse (1Hz or on-change)
            if (ShouldPublishPulse(currentWallTicks))
            {
                PublishTimePulse(currentWallTicks, totalTime);
                _lastPulseTicks = currentWallTicks;
            }
        }
        
        public void SetTimeScale(float scale)
        {
            if (scale < 0.0f)
                throw new ArgumentException("TimeScale cannot be negative", nameof(scale));
            
            // Save current sim time as new base
            long currentWallTicks = _wallClock.ElapsedTicks;
            double accumulatedTimeInSegment = (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            _simTimeBase = _simTimeBase + accumulatedTimeInSegment;
            
            _scaleChangeWallTicks = currentWallTicks;
            _timeScale = scale;
            
            // Immediately publish to slaves
            PublishTimePulse(currentWallTicks, _simTimeBase);
        }
        
        private bool ShouldPublishPulse(long currentTicks)
        {
            // Publish every second OR immediately after scale change
            return (currentTicks - _lastPulseTicks) >= PulseIntervalTicks;
        }
        
        private void PublishTimePulse(long wallTicks, double simTime)
        {
            var pulse = new TimePulseDescriptor
            {
                MasterWallTicks = wallTicks,
                SimTimeSnapshot = simTime,
                TimeScale = _timeScale,
                SequenceId = _frameNumber
            };
            
            _timePulseWriter.Write(pulse);
        }
        
        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Continuous;
        
        public void Dispose()
        {
            // Cleanup if needed
        }
    }
    
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
    }
}
