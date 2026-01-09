using System;
using System.Diagnostics;
using ModuleHost.Core.Network;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master time controller for Continuous mode.
    /// Owns authoritative simulation time and publishes TimePulse to network.
    /// </summary>
    public class MasterTimeController : ITimeController
    {
        private readonly Stopwatch _wallClock;
        private readonly FdpEventBus _eventBus;
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

        public MasterTimeController(FdpEventBus eventBus, TimeConfig? config = null)
        {
            _wallClock = Stopwatch.StartNew();
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _config = config ?? TimeConfig.Default;
            _scaleChangeWallTicks = _wallClock.ElapsedTicks;
            _lastPulseTicks = _wallClock.ElapsedTicks;
            _lastFrameTicks = _wallClock.ElapsedTicks; // Initialize to avoid huge delta on first frame
            
            // Register event type
            _eventBus.Register<TimePulseDescriptor>();
        }
        
        public GlobalTime Update()
        {
            _frameNumber++;
            
            // Calculate wall delta
            long currentWallTicks = _wallClock.ElapsedTicks;
            double wallDelta = (currentWallTicks - _lastFrameTicks) / (double)Stopwatch.Frequency;
            _lastFrameTicks = currentWallTicks;
            
            // Calculate simulation delta (respecting scale)
            float dt = (float)(wallDelta * _timeScale);
            
            // Calculate total simulation time
            double totalTime = _simTimeBase + 
                       (currentWallTicks - _scaleChangeWallTicks) / (double)Stopwatch.Frequency * _timeScale;
            
            // Publish TimePulse (1Hz or on-change)
            if (ShouldPublishPulse(currentWallTicks))
            {
                PublishTimePulse(currentWallTicks, totalTime);
                _lastPulseTicks = currentWallTicks;
            }
            
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = dt,
                TotalTime = totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = (float)wallDelta,
                UnscaledTotalTime = _wallClock.Elapsed.TotalSeconds,
                StartWallTicks = 0 
            };
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
            
            _eventBus.Publish(pulse);
        }
        
        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Continuous;
        
        public void Dispose()
        {
            // Cleanup if needed
        }
    }
    

}
