using System;

namespace CarKinem.Controllers
{
    /// <summary>
    /// Proportional speed controller.
    /// </summary>
    public static class SpeedController
    {
        /// <summary>
        /// Calculate acceleration command.
        /// </summary>
        /// <param name="currentSpeed">Current speed (m/s)</param>
        /// <param name="targetSpeed">Desired speed (m/s)</param>
        /// <param name="gain">Proportional gain</param>
        /// <param name="maxAccel">Maximum acceleration (m/s²)</param>
        /// <param name="maxDecel">Maximum deceleration (m/s²)</param>
        /// <returns>Acceleration command (m/s²)</returns>
        public static float CalculateAcceleration(
            float currentSpeed,
            float targetSpeed,
            float gain,
            float maxAccel,
            float maxDecel)
        {
            float speedError = targetSpeed - currentSpeed;
            float rawAccel = speedError * gain;
            
            // Clamp to vehicle limits
            // Note: maxDecel is passed as positive scalar, so we clamp to -maxDecel
            return Math.Clamp(rawAccel, -maxDecel, maxAccel);
        }
    }
}
