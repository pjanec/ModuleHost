using System;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.Trajectory
{
    public class TrajectoryPoolTests
    {
        [Fact]
        public void RegisterTrajectory_ValidInput_ReturnsId()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[]
            {
                new Vector2(0, 0),
                new Vector2(100, 0),
                new Vector2(100, 100)
            };
            
            int id = pool.RegisterTrajectory(positions);
            
            Assert.True(id > 0, "Should return positive ID");
        }
        
        [Fact]
        public void RegisterTrajectory_MultipleTrajectories_HaveUniqueIds()
        {
            using var pool = new TrajectoryPoolManager();
            
            var path1 = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var path2 = new[] { new Vector2(20, 0), new Vector2(30, 0) };
            
            int id1 = pool.RegisterTrajectory(path1);
            int id2 = pool.RegisterTrajectory(path2);
            
            Assert.NotEqual(id1, id2);
        }
        
        [Fact]
        public void RegisterTrajectory_SingleWaypoint_ThrowsException()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0) };
            
            Assert.Throws<ArgumentException>(() => pool.RegisterTrajectory(positions));
        }
        
        [Fact]
        public void RegisterTrajectory_MismatchedSpeedsLength_ThrowsException()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            var speeds = new[] { 10f }; // Too short
            
            Assert.Throws<ArgumentException>(() => pool.RegisterTrajectory(positions, speeds));
        }
        
        [Fact]
        public void TryGetTrajectory_ValidId_ReturnsTrue()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            int id = pool.RegisterTrajectory(positions);
            
            bool found = pool.TryGetTrajectory(id, out var traj);
            
            Assert.True(found);
            Assert.Equal(id, traj.Id);
        }
        
        [Fact]
        public void TryGetTrajectory_InvalidId_ReturnsFalse()
        {
            using var pool = new TrajectoryPoolManager();
            
            bool found = pool.TryGetTrajectory(999, out _);
            
            Assert.False(found);
        }
        
        [Fact]
        public void RemoveTrajectory_ExistingId_ReturnsTrue()
        {
            using var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            int id = pool.RegisterTrajectory(positions);
            
            bool removed = pool.RemoveTrajectory(id);
            
            Assert.True(removed);
            Assert.False(pool.TryGetTrajectory(id, out _));
        }
        
        [Fact]
        public void Dispose_ReleasesAllTrajectories()
        {
            var pool = new TrajectoryPoolManager();
            
            var positions = new[] { new Vector2(0, 0), new Vector2(10, 0) };
            pool.RegisterTrajectory(positions);
            pool.RegisterTrajectory(positions);
            
            Assert.Equal(2, pool.Count);
            
            pool.Dispose();
            
            // After dispose, pool should be empty
            Assert.Equal(0, pool.Count);
        }
    }
}
