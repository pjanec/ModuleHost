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

        public void RenderTrajectory(int trajectoryId, Camera2D camera, Color color)
        {
            if (_pool.TryGetTrajectory(trajectoryId, out var trajectory))
            {
                if (!trajectory.Waypoints.IsCreated) return;
                
                for (int i = 0; i < trajectory.Waypoints.Length - 1; i++)
                {
                    Vector2 start = trajectory.Waypoints[i].Position;
                    Vector2 end = trajectory.Waypoints[i + 1].Position;
                    Raylib.DrawLineEx(start, end, 2.0f, color);
                }
                
                if (trajectory.IsLooped == 1 && trajectory.Waypoints.Length > 0)
                {
                    Vector2 start = trajectory.Waypoints[trajectory.Waypoints.Length - 1].Position;
                    Vector2 end = trajectory.Waypoints[0].Position;
                    Raylib.DrawLineEx(start, end, 2.0f, color);
                }
            }
        }
    }
}
