using System;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Controllers
{
    /// <summary>
    /// Pure Pursuit steering controller.
    /// Geometric path-following using lookahead point.
    /// </summary>
    public static class PurePursuitController
    {
        /// <summary>
        /// Calculate steering angle for Pure Pursuit.
        /// </summary>
        /// <param name="currentPos">Current vehicle position</param>
        /// <param name="currentForward">Current heading (normalized)</param>
        /// <param name="desiredVelocity">Desired velocity vector</param>
        /// <param name="currentSpeed">Current speed (m/s)</param>
        /// <param name="wheelBase">Distance between axles (meters)</param>
        /// <param name="lookaheadMin">Minimum lookahead distance (meters)</param>
        /// <param name="lookaheadMax">Maximum lookahead distance (meters)</param>
        /// <param name="maxSteerAngle">Maximum steering angle (radians)</param>
        /// <returns>Steering angle (radians)</returns>
        public static float CalculateSteering(
            Vector2 currentPos,
            Vector2 currentForward,
            Vector2 desiredVelocity,
            float currentSpeed,
            float wheelBase,
            float lookaheadMin,
            float lookaheadMax,
            float maxSteerAngle)
        {
            // 1. Calculate dynamic lookahead distance
            float lookaheadTime = 0.5f; // Default: 0.5s
            float lookaheadDist = MathF.Max(
                lookaheadMin,
                MathF.Min(lookaheadMax, currentSpeed * lookaheadTime)
            );
            
            // 2. Calculate lookahead point
            Vector2 lookaheadPoint;
            if (desiredVelocity.LengthSquared() < 0.01f)
            {
                // Stopped: maintain heading
                lookaheadPoint = currentPos + currentForward * lookaheadDist;
            }
            else
            {
                // Moving: follow desired velocity direction
                Vector2 desiredDir = Vector2.Normalize(desiredVelocity);
                lookaheadPoint = currentPos + desiredDir * lookaheadDist;
            }
            
            // 3. Calculate signed angle to lookahead point
            Vector2 toLookahead = lookaheadPoint - currentPos;
            float alpha = VectorMath.SignedAngle(currentForward, toLookahead);
            
            // 4. Compute curvature (bicycle model)
            // Curvature k = 2 * sin(alpha) / Ld
            float kappa = (2.0f * MathF.Sin(alpha)) / lookaheadDist;
            
            // 5. Convert curvature to steering angle
            // tan(delta) = k * L
            float steerAngle = MathF.Atan(kappa * wheelBase);
            
            // 6. Clamp to vehicle limits
            return Math.Clamp(steerAngle, -maxSteerAngle, maxSteerAngle);
        }
    }
}
