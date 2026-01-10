using System;
using Fdp.Kernel;

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
        /// <returns>GlobalTime struct with current frame time data</returns>
        GlobalTime Update();
        
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
        
        /// <summary>
        /// Get current time state for transfer/save.
        /// </summary>
        GlobalTime GetCurrentState();
        
        /// <summary>
        /// Initialize controller with specific time state.
        /// </summary>
        void SeedState(GlobalTime state);
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
