using System;
using System.Numerics;
using CarKinem.Avoidance;
using Xunit;

namespace CarKinem.Tests.Algorithms
{
    public class RVOAvoidanceTests
    {
        [Fact]
        public void RVOAvoidance_NoNeighbors_ReturnsPreferredVelocity()
        {
            Vector2 preferredVel = new Vector2(10, 0);
            Vector2 selfPos = Vector2.Zero;
            Vector2 selfVel = preferredVel;
            
            var neighbors = Array.Empty<(Vector2, Vector2)>();
            
            Vector2 result = RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel,
                neighbors,
                avoidanceRadius: 2.5f,
                maxSpeed: 30f
            );
            
            Assert.Equal(preferredVel, result);
        }

        [Fact]
        public void RVOAvoidance_StaticObstacleAhead_AvoidsIt()
        {
            Vector2 preferredVel = new Vector2(10, 0); // Moving right
            Vector2 selfPos = Vector2.Zero;
            Vector2 selfVel = preferredVel;
            
            // Obstacle slightly offset to induce lateral force
            var neighbors = new[] {
                (pos: new Vector2(5, 0.1f), vel: Vector2.Zero)
            };
            
            Vector2 result = RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel,
                neighbors,
                avoidanceRadius: 2.5f,
                maxSpeed: 30f
            );
            
            // Should slow down (X < 10) AND deviate (Y != 0)
            Assert.True(result.X < preferredVel.X, "Should slow down");
            Assert.NotEqual(0f, result.Y); // Must have lateral component
            Assert.True(result.Y < 0f, "Should steer right (negative Y)");
        }
        
        [Fact]
        public void RVOAvoidance_StaticObstacleAheadOffset_AvoidsLat()
        {
            Vector2 preferredVel = new Vector2(10, 0); // Moving right
            Vector2 selfPos = Vector2.Zero;
            Vector2 selfVel = preferredVel;
            
            // Obstacle to the right and slightly up (5, 0.1)
            var neighbors = new[] {
                (pos: new Vector2(5, 0.1f), vel: Vector2.Zero)
            };
            
            Vector2 result = RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel,
                neighbors,
                avoidanceRadius: 2.5f,
                maxSpeed: 30f
            );
            
            // Should definitely avoid
            Assert.NotEqual(preferredVel, result);
            Assert.True(result.Y < 0f, "Should steer right away from obstacle at +Y");
        }

        [Fact]
        public void RVOAvoidance_ClampsToMaxSpeed()
        {
            Vector2 preferredVel = new Vector2(100, 100); // Extreme velocity
            Vector2 selfPos = Vector2.Zero;
            Vector2 selfVel = Vector2.Zero;
            
            var neighbors = Array.Empty<(Vector2, Vector2)>();
            float maxSpeed = 30f;
            
            Vector2 result = RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel,
                neighbors,
                avoidanceRadius: 2.5f,
                maxSpeed: maxSpeed
            );
            
            Assert.True(result.Length() <= maxSpeed + 0.001f);
        }
    }
}
