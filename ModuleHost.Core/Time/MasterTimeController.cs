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
        private double _totalTime = 0.0;
        private double _unscaledTotalTime = 0.0;
        private float _timeScale = 1.0f;
        private long _frameNumber = 0;
        
        // Network publishing
        private long _lastEventsTicks = 0;
        private static readonly long PulseIntervalTicks = Stopwatch.Frequency; // 1Hz

        public MasterTimeController(FdpEventBus eventBus, TimeConfig? config = null)
        {
            _wallClock = Stopwatch.StartNew();
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _config = config ?? TimeConfig.Default;
            
            // Register event type
            _eventBus.Register<TimePulseDescriptor>();
        }
        
        public GlobalTime Update()
        {
            // Calculate wall delta
            double elapsedSeconds = _wallClock.Elapsed.TotalSeconds;
            
            // FIX: Reset stopwatch so next Update() measures fresh interval
            _wallClock.Restart();
            
            _frameNumber++;
            
            // Accumulate manually
            float scaledDelta = (float)(elapsedSeconds * _timeScale);
            
            _totalTime += scaledDelta;
            _unscaledTotalTime += elapsedSeconds;
            
            // Publish TimePulse (1Hz or on-change)
            long currentTicks = Stopwatch.GetTimestamp();
            if (ShouldPublishPulse(currentTicks))
            {
                PublishTimePulse(currentTicks, _totalTime);
                _lastEventsTicks = currentTicks;
            }
            
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = scaledDelta,
                TotalTime = _totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = (float)elapsedSeconds,
                UnscaledTotalTime = _unscaledTotalTime,
                StartWallTicks = 0 
            };
        }
        
        public void SetTimeScale(float scale)
        {
            if (scale < 0.0f)
                throw new ArgumentException("TimeScale cannot be negative", nameof(scale));
            
            _timeScale = scale;
            
            // Immediately publish to slaves
            PublishTimePulse(Stopwatch.GetTimestamp(), _totalTime);
        }
        
        private bool ShouldPublishPulse(long currentTicks)
        {
            // Publish every second
            return (currentTicks - _lastEventsTicks) >= PulseIntervalTicks;
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
        
        public GlobalTime GetCurrentState()
        {
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = 0.0f,
                TotalTime = _totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = 0.0f,
                UnscaledTotalTime = _unscaledTotalTime
            };
        }

        public void SeedState(GlobalTime state)
        {
            _frameNumber = state.FrameNumber;
            _totalTime = state.TotalTime;
            _unscaledTotalTime = state.UnscaledTotalTime;
            _timeScale = state.TimeScale;
            
            _wallClock.Restart();
            
            // FORCE PULSE on next update to lock slaves immediately
            // By setting last ticks to 'long ago'
            _lastEventsTicks = Stopwatch.GetTimestamp() - (PulseIntervalTicks * 2); 
        }

        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Continuous;
        
        public void Dispose()
        {
            // Cleanup if needed
        }
    }
    

}
