using System;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Abstraction over time control logic.
    /// Implementations: MasterTimeController, SlaveTimeController, SteppedTimeController.
    /// </summary>
    public interface ITimeController : IDisposable
    {
        /// <summary>
        /// Update clock state and calculate time for this frame.
        /// Called once per frame by ModuleHostKernel.
        /// </summary>
        /// <param name="dt">Output: DeltaTime for this frame (seconds)</param>
        /// <param name="totalTime">Output: Total simulation time (seconds)</param>
        void Update(out float dt, out double totalTime);
        
        /// <summary>
        /// Change simulation speed.
        /// </summary>
        void SetTimeScale(float scale);
        
        /// <summary>
        /// Get current time scale.
        /// </summary>
        float GetTimeScale();
        
        /// <summary>
        /// Get current mode (Continuous or Deterministic).
        /// </summary>
        TimeMode GetMode();
    }
    
    /// <summary>
    /// Time synchronization mode.
    /// </summary>
    public enum TimeMode
    {
        /// <summary>
        /// Continuous (Real-Time/Scaled) mode.
        /// Uses PLL for smooth synchronization.
        /// </summary>
        Continuous,
        
        /// <summary>
        /// Deterministic (Lockstep/Stepped) mode.
        /// Frame-by-frame synchronization via ACKs.
        /// </summary>
        Deterministic
    }
}
