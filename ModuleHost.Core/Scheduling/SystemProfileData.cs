using System;
using System.Collections.Generic;

namespace ModuleHost.Core.Scheduling
{
    /// <summary>
    /// Performance profiling data for a system.
    /// </summary>
    public class SystemProfileData
    {
        public string SystemName { get; }
        
        public long ExecutionCount { get; private set; }
        public double TotalMs { get; private set; }
        public double AverageMs => ExecutionCount > 0 ? TotalMs / ExecutionCount : 0;
        public double MinMs { get; private set; } = double.MaxValue;
        public double MaxMs { get; private set; }
        public double LastMs { get; private set; }
        
        public int ErrorCount { get; private set; }
        public Exception? LastError { get; private set; }
        
        private readonly Queue<double> _recentExecutions = new();
        private const int MaxRecentSamples = 60; // Last 60 executions
        
        public SystemProfileData(string systemName)
        {
            SystemName = systemName;
        }
        
        public void RecordExecution(double milliseconds)
        {
            ExecutionCount++;
            TotalMs += milliseconds;
            LastMs = milliseconds;
            
            if (milliseconds < MinMs)
                MinMs = milliseconds;
            if (milliseconds > MaxMs)
                MaxMs = milliseconds;
            
            _recentExecutions.Enqueue(milliseconds);
            if (_recentExecutions.Count > MaxRecentSamples)
                _recentExecutions.Dequeue();
        }
        
        public void RecordError(Exception ex)
        {
            ErrorCount++;
            LastError = ex;
        }
        
        /// <summary>
        /// Get average of recent executions (last 60).
        /// </summary>
        public double GetRecentAverageMs()
        {
            if (_recentExecutions.Count == 0)
                return 0;
            
            double sum = 0;
            foreach (var ms in _recentExecutions)
                sum += ms;
            
            return sum / _recentExecutions.Count;
        }
        
        /// <summary>
        /// Reset all statistics.
        /// </summary>
        public void Reset()
        {
            ExecutionCount = 0;
            TotalMs = 0;
            MinMs = double.MaxValue;
            MaxMs = 0;
            LastMs = 0;
            ErrorCount = 0;
            LastError = null;
            _recentExecutions.Clear();
        }
    }
}
