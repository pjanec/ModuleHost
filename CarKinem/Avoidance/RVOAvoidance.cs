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
                
                // Combined radius (assuming neighbor has similar radius)
                // This defines the "hard" shell
                float combinedRadius = avoidanceRadius * 2.0f;
                
                // Static Separation (Anti-Penetration)
                // If we are overlapping, push apart IMMEDIATELY regardless of velocity
                if (dist < combinedRadius)
                {
                     Vector2 dir = dist > 0.001f ? Vector2.Normalize(relPos) : new Vector2(1,0);
                     float penetration = combinedRadius - dist;
                     
                     // Strong spring force to separate
                     // Multiplier needs to be high enough to overcome P-controller drive
                     // Reduced from 20.0f to 10.0f as requested by user revert
                     avoidanceForce += -dir * (penetration * 10.0f); 
                }

                // Calculate relative velocity
                Vector2 relVel = selfVel - neighborVel;
                
                // Time-to-collision heuristic
                float relSpeed = relVel.Length();
                float ttc = dist / MathF.Max(relSpeed, 0.1f);
                
                // Determine if this neighbor is relevant for avoidance
                // "Relevant" means:
                // 1. We are moving towards them (Dot > 0) AND TTC is low
                // 2. OR We are very close (inside hard shell) - already handled above?
                // The hard shell above handles static overlap. Here we handle dynamic collision.
                
                if (Vector2.Dot(relVel, relPos) > 0f && ttc < 4.0f)
                {
                    // Repulsion inversely proportional to distance
                    Vector2 dir = Vector2.Normalize(relPos);
                    
                    // Force strength
                    // If we are stuck (velocity near zero), we need enough force to start moving away.
                    // The "preferred velocity" might be pushing us INTO the neighbor.
                    // Avoidance force must cancel that out + extra.
                    
                    Vector2 repulsion = -dir * (10.0f / (dist + 0.1f));
                    
                    // Lateral bias (steer right)
                    Vector2 lateral = new Vector2(dir.Y, -dir.X) * (4.0f / (dist + 0.1f));
                    
                    avoidanceForce += repulsion + lateral;
                }
            }
            
            // Blend preferred velocity with avoidance
            Vector2 finalVel = preferredVel + avoidanceForce;
            
            // Fix: If avoidance force is huge (due to overlap), it might create huge velocity.
            // But we clamp it below.
            
            // CRITICAL FIX: 
            // If the vehicle is "Stuck" because avoidance cancels preferred velocity perfectly, 
            // it won't move. But avoidance force usually has a lateral component to slide off.
            // However, if we are perfectly head-on, lateral might not be enough if preferred is strong.
            
            // Also check if we are just blocked.
            
            // Clamp to max speed
            if (finalVel.LengthSquared() > maxSpeed * maxSpeed)
            {
                finalVel = Vector2.Normalize(finalVel) * maxSpeed;
            }
            
            return finalVel;
        }
    }
}
