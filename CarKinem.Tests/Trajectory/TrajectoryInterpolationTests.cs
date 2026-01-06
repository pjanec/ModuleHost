using System;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.Trajectory
{
    public class TrajectoryInterpolationTests
    {
        [Fact]
        public void SampleTrajectory_AtStart_ReturnsFirstWaypoint()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 0f);
            
            Assert.Equal(new Vector2(0, 0), pos);
            Assert.Equal(10f, speed); // Default speed
        }
        
        [Fact]
        public void SampleTrajectory_AtEnd_ReturnsLastWaypoint()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 1000f); // Beyond end
            
            Assert.Equal(new Vector2(100, 0), pos);
        }
        
        [Fact]
        public void SampleTrajectory_Midpoint_InterpolatesPosition()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // Midpoint
            
            // Should be halfway
            Assert.Equal(50f, pos.X, precision: 1);
            Assert.Equal(0f, pos.Y, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_TangentVector_PointsForward()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),
                new Vector2(100, 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // First segment
            
            // Tangent should point right (positive X)
            Assert.True(tangent.X > 0.9f, "Should point right");
            Assert.Equal(1f, tangent.Length(), precision: 3); // Should be normalized
        }
        
        [Fact]
        public void SampleTrajectory_WithCustomSpeeds_InterpolatesSpeed()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            var speeds = new[] { 5f, 15f };
            
            int id = pool.RegisterTrajectory(positions, speeds);
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 50f); // Midpoint
            
            // Speed should be halfway between 5 and 15
            Assert.Equal(10f, speed, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_LoopedTrajectory_WrapsAround()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0)
            };
            
            int id = pool.RegisterTrajectory(positions, looped: true);
            
            // Sample beyond total length (100m)
            var (pos1, _, _) = pool.SampleTrajectory(id, progressS: 0f);
            var (pos2, _, _) = pool.SampleTrajectory(id, progressS: 100f); // Should wrap to start
            
            Assert.Equal(pos1.X, pos2.X, precision: 1);
            Assert.Equal(pos1.Y, pos2.Y, precision: 1);
        }
        
        [Fact]
        public void SampleTrajectory_InvalidId_ReturnsDefault()
        {
            using var pool = new TrajectoryPoolManager();
            
            var (pos, tangent, speed) = pool.SampleTrajectory(999, progressS: 0f);
            
            Assert.Equal(Vector2.Zero, pos);
            Assert.Equal(0f, speed);
        }
        
        [Fact]
        public void SampleTrajectory_MultiSegment_CorrectCumulativeDistance()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),   // 100m from start
                new Vector2(100, 100)  // 200m from start (100 + 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            
            // Sample at 150m (should be in second segment, halfway)
            var (pos, tangent, speed) = pool.SampleTrajectory(id, progressS: 150f);
            
            Assert.Equal(100f, pos.X, precision: 1);
            Assert.Equal(50f, pos.Y, precision: 1); // Halfway up
        }
    }
}
