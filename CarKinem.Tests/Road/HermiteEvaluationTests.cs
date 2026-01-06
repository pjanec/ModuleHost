using System;
using System.Numerics;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class HermiteEvaluationTests
    {
        [Fact]
        public void EvaluateHermite_AtT0_ReturnsP0()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 result = RoadGraphNavigator.EvaluateHermite(0f, p0, t0, p1, t1);
            
            Assert.Equal(p0, result);
        }
        
        [Fact]
        public void EvaluateHermite_AtT1_ReturnsP1()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 result = RoadGraphNavigator.EvaluateHermite(1f, p0, t0, p1, t1);
            
            Assert.Equal(p1.X, result.X, precision: 2);
            Assert.Equal(p1.Y, result.Y, precision: 2);
        }
        
        [Fact]
        public void EvaluateHermite_StraightLine_InterpolatesLinearly()
        {
            // Straight horizontal segment
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 midpoint = RoadGraphNavigator.EvaluateHermite(0.5f, p0, t0, p1, t1);
            
            // Should be approximately halfway
            Assert.Equal(50f, midpoint.X, precision: 1);
            Assert.Equal(0f, midpoint.Y, precision: 1);
        }
        
        [Fact]
        public void EvaluateHermiteTangent_StraightLine_PointsForward()
        {
            Vector2 p0 = new Vector2(0, 0);
            Vector2 t0 = new Vector2(50, 0);
            Vector2 p1 = new Vector2(100, 0);
            Vector2 t1 = new Vector2(50, 0);
            
            Vector2 tangent = RoadGraphNavigator.EvaluateHermiteTangent(0.5f, p0, t0, p1, t1);
            Vector2 normalized = Vector2.Normalize(tangent);
            
            // Should point right (positive X)
            Assert.True(normalized.X > 0.9f);
        }
    }
}
