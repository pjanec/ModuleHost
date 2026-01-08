// File: ModuleHost.Core/Resilience/ModuleCircuitBreaker.cs

using System;

namespace ModuleHost.Core.Resilience
{
    /// <summary>
    /// Circuit breaker states following the standard pattern.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Normal operation - module can run.
        /// </summary>
        Closed,
        
        /// <summary>
        /// Module has failed too many times - skipping execution.
        /// </summary>
        Open,
        
        /// <summary>
        /// Testing recovery - allow one execution to see if module recovered.
        /// </summary>
        HalfOpen
    }
    
    /// <summary>
    /// Tracks module health and prevents repeated execution of failing modules.
    /// Implements the Circuit Breaker pattern for resilience.
    /// </summary>
    public class ModuleCircuitBreaker
    {
        private readonly int _failureThreshold;
        private readonly int _resetTimeoutMs;
        
        private int _failureCount;
        private DateTime _lastFailureTime;
        private CircuitState _state = CircuitState.Closed;
        
        private readonly object _lock = new object();
        
        /// <summary>
        /// Creates a circuit breaker with specified thresholds.
        /// </summary>
        /// <param name="failureThreshold">Number of consecutive failures before opening circuit (default: 3)</param>
        /// <param name="resetTimeoutMs">Milliseconds before attempting recovery (default: 5000)</param>
        public ModuleCircuitBreaker(int failureThreshold = 3, int resetTimeoutMs = 5000)
        {
            if (failureThreshold <= 0)
                throw new ArgumentException("Failure threshold must be positive", nameof(failureThreshold));
            if (resetTimeoutMs <= 0)
                throw new ArgumentException("Reset timeout must be positive", nameof(resetTimeoutMs));
            
            _failureThreshold = failureThreshold;
            _resetTimeoutMs = resetTimeoutMs;
        }
        
        /// <summary>
        /// Current circuit state (for diagnostics).
        /// </summary>
        public CircuitState State
        {
            get { lock (_lock) return _state; }
        }
        
        /// <summary>
        /// Number of consecutive failures recorded.
        /// </summary>
        public int FailureCount
        {
            get { lock (_lock) return _failureCount; }
        }
        
        /// <summary>
        /// Determines if the module can run this frame.
        /// </summary>
        /// <returns>True if module should execute, false if circuit is open</returns>
        public bool CanRun()
        {
            lock (_lock)
            {
                if (_state == CircuitState.Closed)
                {
                    return true;
                }
                
                if (_state == CircuitState.Open)
                {
                    // Check if enough time has passed to attempt recovery
                    var timeSinceFailure = DateTime.UtcNow - _lastFailureTime;
                    if (timeSinceFailure.TotalMilliseconds > _resetTimeoutMs)
                    {
                        // Transition to HalfOpen - allow one test execution
                        _state = CircuitState.HalfOpen;
                        return true;
                    }
                    
                    return false; // Still in cooldown
                }
                
                // HalfOpen state - allow execution to test recovery
                return _state == CircuitState.HalfOpen;
            }
        }
        
        /// <summary>
        /// Records successful module execution.
        /// Resets failure count and closes circuit if in HalfOpen state.
        /// </summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    // Recovery successful - close circuit
                    _state = CircuitState.Closed;
                    _failureCount = 0;
                }
                else if (_state == CircuitState.Closed)
                {
                    // Successful execution in normal state - reset failure count
                    _failureCount = 0;
                }
                // Note: Success in Open state shouldn't happen, but handle gracefully
            }
        }
        
        /// <summary>
        /// Records module failure (exception or timeout).
        /// Increments failure count and opens circuit if threshold exceeded.
        /// </summary>
        /// <param name="reason">Reason for failure (for logging)</param>
        public void RecordFailure(string reason)
        {
            lock (_lock)
            {
                _lastFailureTime = DateTime.UtcNow;
                _failureCount++;
                
                if (_state == CircuitState.HalfOpen)
                {
                    // Recovery attempt failed - reopen circuit immediately
                    _state = CircuitState.Open;
                }
                else if (_failureCount >= _failureThreshold)
                {
                    // Threshold exceeded - open circuit
                    _state = CircuitState.Open;
                }
            }
        }
        
        /// <summary>
        /// Resets the circuit breaker to closed state (manual recovery).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _state = CircuitState.Closed;
                _failureCount = 0;
            }
        }
    }
}
