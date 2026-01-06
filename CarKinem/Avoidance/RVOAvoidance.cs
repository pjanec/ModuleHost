using System;
using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Avoidance
{
    /// <summary>
    /// RVO-Lite collision avoidance using velocity-space forces.
    /// </summary>
    public static class RVOAvoidance
    {
        /// <summary>
        /// Apply collision avoidance to preferred velocity.
        /// </summary>
        /// <param name="preferredVel">Desired velocity without avoidance</param>
        /// <param name="selfPos">Vehicle position</param>
        /// <param name="selfVel">Vehicle velocity</param>
        /// <param name="neighbors">Array of neighbor positions and velocities</param>
        /// <param name="avoidanceRadius">Danger zone radius (meters)</param>
        /// <param name="maxSpeed">Maximum allowed speed (m/s)</param>
        /// <returns>Adjusted velocity with avoidance</returns>
        public static Vector2 ApplyAvoidance(
            Vector2 preferredVel,
            Vector2 selfPos,
            Vector2 selfVel,
            ReadOnlySpan<(Vector2 pos, Vector2 vel)> neighbors,
            float avoidanceRadius,
            float maxSpeed)
        {
            Vector2 avoidanceForce = Vector2.Zero;
            float dangerRadius = avoidanceRadius * 2.5f;
            
            foreach (var (neighborPos, neighborVel) in neighbors)
            {
                Vector2 relPos = neighborPos - selfPos;
                float dist = relPos.Length();
                
                // Skip if too far or same position
                if (dist > dangerRadius || dist < 0.01f)
                    continue;
                
                // Calculate relative velocity
                Vector2 relVel = selfVel - neighborVel;
                
                // Time-to-collision heuristic
                float relSpeed = relVel.Length();
                float ttc = dist / MathF.Max(relSpeed, 0.1f);
                
                // Apply repulsion if on collision course
                // Dot > 0 means we are moving towards the neighbor (distance decreasing)
                if (Vector2.Dot(relVel, relPos) > 0f && ttc < 2.0f)
                {
                    // Repulsion inversely proportional to distance
                    Vector2 repulsion = -Vector2.Normalize(relPos) * (5.0f / dist);
                    avoidanceForce += repulsion;
                }
            }
            
            // Blend preferred velocity with avoidance
            Vector2 finalVel = preferredVel + avoidanceForce;
            
            // Clamp to max speed
            if (finalVel.LengthSquared() > maxSpeed * maxSpeed)
            {
                finalVel = Vector2.Normalize(finalVel) * maxSpeed;
            }
            
            return finalVel;
        }
    }
}
