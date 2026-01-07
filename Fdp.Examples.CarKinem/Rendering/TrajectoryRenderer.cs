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
                
                // Determine start position for drawing
                Vector2 startDrawPos;
                
                if (nextIndex != -1)
                {
                    // effectively sample at progressS
                    var (currentPos, _, _) = _pool.SampleTrajectory(trajectoryId, progressS);
                    startDrawPos = currentPos;
                    
                    // Draw line from current position to next waypoint
                    Raylib.DrawLineEx(startDrawPos, trajectory.Waypoints[nextIndex].Position, 2.0f, color);
                    
                    // Draw remaining segments
                    for (int i = nextIndex; i < trajectory.Waypoints.Length - 1; i++)
                    {
                        Vector2 p1 = trajectory.Waypoints[i].Position;
                        Vector2 p2 = trajectory.Waypoints[i + 1].Position;
                        Raylib.DrawLineEx(p1, p2, 2.0f, color);
                    }
                }
                else
                {
                    // Might be on the last segment or looped
                    // Simplified: if looped handling is complex, just draw all if looped?
                    // For this demo, looping is false.
                }
                
                if (trajectory.IsLooped == 1)
                {
                     // Simple loop rendering for now
                }
            }
        }
    }
}
