using Raylib_cs;
using System.Numerics;
using CarKinem.Trajectory;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class TrajectoryRenderer
    {
        private TrajectoryPoolManager _pool;
        
        public TrajectoryRenderer(TrajectoryPoolManager pool)
        {
            _pool = pool;
        }

        public void RenderTrajectory(int trajectoryId, float progressS, Camera2D camera, Color color)
        {
            if (_pool.TryGetTrajectory(trajectoryId, out var trajectory))
            {
                if (!trajectory.Waypoints.IsCreated) return;
                
                // If finished and not looped, draw nothing
                if (trajectory.IsLooped == 0 && progressS >= trajectory.TotalLength - 0.01f)
                     return;

                // If Linear, use simple polyline rendering (efficient)
                if (trajectory.Interpolation == TrajectoryInterpolation.Linear)
                {
                    RenderLinear(trajectory, progressS, color);
                }
                else
                {
                    // If Hermite/Catmull, use high-resolution sampling for smooth curve
                    // Use distinct color (Orange) for Spline as requested
                    RenderHermiteSmooth(trajectory, progressS, new Color(255, 161, 0, 200)); // Orange-ish
                }
            }
        }

        private void RenderLinear(CustomTrajectory trajectory, float progressS, Color color)
        {
            // Find the first waypoint ahead of us
            int nextIndex = -1;
            for (int i = 0; i < trajectory.Waypoints.Length; i++)
            {
                if (trajectory.Waypoints[i].CumulativeDistance > progressS)
                {
                    nextIndex = i;
                    break;
                }
            }
            
            if (nextIndex != -1)
            {
                // Draw from current interpolated pos to next waypoint
                // Note: SampleTrajectory handles linear interpolation correctly for us
                var (currentPos, _, _) = _pool.SampleTrajectory(trajectory.Id, progressS);
                
                Raylib.DrawLineEx(currentPos, trajectory.Waypoints[nextIndex].Position, 0.15f, color);
                
                // Draw remaining segments
                for (int i = nextIndex; i < trajectory.Waypoints.Length - 1; i++)
                {
                    Vector2 p1 = trajectory.Waypoints[i].Position;
                    Vector2 p2 = trajectory.Waypoints[i + 1].Position;
                    Raylib.DrawLineEx(p1, p2, 0.15f, color);
                }
            }
        }

        private void RenderHermiteSmooth(CustomTrajectory trajectory, float progressS, Color color)
        {
            // We sample the curve at fixed intervals to draw a smooth polyline approximation
            // Step size (meters) - smaller = smoother but more expensive
            const float stepSize = 1.0f; 
            
            float currentDist = progressS;
            float totalLen = trajectory.TotalLength;
            
            // Limit lookahead to avoid drawing too much if path is huge? 
            // Or draw all? Let's draw all remaining.
            
            Vector2 prevPos;
            {
                var (pos, _, _) = _pool.SampleTrajectory(trajectory.Id, currentDist);
                prevPos = pos;
            }
            
            while (currentDist < totalLen)
            {
                currentDist += stepSize;
                if (currentDist > totalLen) currentDist = totalLen;
                
                var (nextPos, _, _) = _pool.SampleTrajectory(trajectory.Id, currentDist);
                
                Raylib.DrawLineEx(prevPos, nextPos, 0.15f, color);
                prevPos = nextPos;
                
                if (currentDist >= totalLen) break;
            }
        }
    }
}
