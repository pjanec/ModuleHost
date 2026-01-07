using System;

namespace Fdp.Examples.CarKinem.Input
{
    /// <summary>
    /// Handles progressive auto-repeat behavior for a single key.
    /// Fires once on initial press, then after a delay starts repeating.
    /// Repeat rate ramps from 10/sec to 100/sec over 5 seconds.
    /// </summary>
    public class KeyAutoRepeat
    {
        private bool _wasPressed;
        private float _pressedTime;
        private float _nextRepeatTime;
        private int _pendingInvocations;
        
        private const float InitialDelay = 0.3f;    // Delay before auto-repeat starts
        private const float MinRate = 10.0f;         // Starting repeat rate (per second)
        private const float MaxRate = 100.0f;        // Maximum repeat rate (per second)
        private const float RampDuration = 5.0f;     // Time to ramp from min to max rate

        /// <summary>
        /// Update the key state. Call this every frame.
        /// </summary>
        /// <param name="isPressed">Whether the key is currently pressed</param>
        /// <param name="currentTime">Current game time in seconds</param>
        public void Update(bool isPressed, float currentTime)
        {
            if (!isPressed)
            {
                // Key released - reset state
                _wasPressed = false;
                _pressedTime = 0;
                _nextRepeatTime = 0;
                return;
            }
            
            if (!_wasPressed)
            {
                // First press - fire immediately and schedule next repeat
                _wasPressed = true;
                _pressedTime = currentTime;
                _nextRepeatTime = currentTime + InitialDelay;
                _pendingInvocations++;
            }
            else
            {
                // Key held - calculate current repeat rate and accumulate invocations
                float holdDuration = currentTime - _pressedTime;
                
                if (holdDuration >= InitialDelay)
                {
                    float repeatRate = CalculateRepeatRate(holdDuration);
                    float repeatInterval = 1.0f / repeatRate;
                    
                    // Accumulate all pending invocations since last frame
                    while (currentTime >= _nextRepeatTime)
                    {
                        _pendingInvocations++;
                        _nextRepeatTime += repeatInterval;
                    }
                }
            }
        }
        
        /// <summary>
        /// Calculate the current repeat rate based on how long the key has been held.
        /// </summary>
        private float CalculateRepeatRate(float holdDuration)
        {
            // Calculate progress through ramp duration (0 to 1)
            float rampProgress = Math.Min((holdDuration - InitialDelay) / RampDuration, 1.0f);
            
            // Interpolate between min and max rate
            return MinRate + (MaxRate - MinRate) * rampProgress;
        }
        
        /// <summary>
        /// Get and clear all pending invocations. Returns the number of times the action should fire.
        /// </summary>
        public int ConsumePendingInvocations()
        {
            int count = _pendingInvocations;
            _pendingInvocations = 0;
            return count;
        }
        
        /// <summary>
        /// Peek at pending invocations without consuming them.
        /// </summary>
        public int PeekPendingInvocations() => _pendingInvocations;
    }
}
