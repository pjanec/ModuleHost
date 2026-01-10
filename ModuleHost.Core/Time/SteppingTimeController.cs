using System;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Time controller for manual stepping only.
    /// Does not measure wall clock - advances only when Step() is called.
    /// Use for: Paused simulations, frame-by-frame debugging, tools.
    /// </summary>
    public class SteppingTimeController : ITimeController
    {
        private double _totalTime;
        private long _frameNumber;
        private float _timeScale;
        private double _unscaledTotalTime;
        
        /// <summary>
        /// Create a stepping controller with initial state.
        /// </summary>
        public SteppingTimeController(GlobalTime seedState)
        {
            SeedState(seedState);
        }
        
        /// <summary>
        /// Update() does nothing - stepping controller only advances on Step().
        /// Returns current frozen time.
        /// </summary>
        public GlobalTime Update()
        {
            // No wall clock measurement - return frozen time
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = 0.0f,  // No time passes
                TotalTime = _totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = 0.0f,
                UnscaledTotalTime = _unscaledTotalTime
            };
        }
        
        /// <summary>
        /// Manually advance time by fixed deltaTime.
        /// </summary>
        public GlobalTime Step(float fixedDeltaTime)
        {
            float scaledDelta = fixedDeltaTime * _timeScale;
            
            _totalTime += scaledDelta;
            _frameNumber++;
            _unscaledTotalTime += fixedDeltaTime;
            
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = scaledDelta,
                TotalTime = _totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = fixedDeltaTime,
                UnscaledTotalTime = _unscaledTotalTime
            };
        }
        
        public void SetTimeScale(float scale)
        {
            if (scale < 0.0f)
                throw new ArgumentException("TimeScale cannot be negative", nameof(scale));
            
            _timeScale = scale;
        }
        
        public float GetTimeScale()
        {
            return _timeScale;
        }

        public TimeMode GetMode()
        {
            return TimeMode.Continuous; // Or add TimeMode.Stepping? treating as continuous mode compatible
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
            _totalTime = state.TotalTime;
            _frameNumber = state.FrameNumber;
            _timeScale = state.TimeScale;
            _unscaledTotalTime = state.UnscaledTotalTime;
        }
        
        public void Dispose()
        {
            // No resources to clean up
        }
    }
}
