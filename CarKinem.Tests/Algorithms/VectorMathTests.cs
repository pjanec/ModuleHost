using System;
using System.Numerics;
using CarKinem.Core;
using Xunit;

namespace CarKinem.Tests.Algorithms
{
    public class VectorMathTests
    {
        [Fact]
        public void SignedAngle_ForwardToRight_ReturnsNegativePiOver2()
        {
            Vector2 forward = new Vector2(1, 0);
            Vector2 right = new Vector2(0, -1);
            
            float angle = VectorMath.SignedAngle(forward, right);
            
            Assert.Equal(-MathF.PI / 2, angle, precision: 4);
        }

        [Fact]
        public void SignedAngle_ForwardToLeft_ReturnsPositivePiOver2()
        {
            Vector2 forward = new Vector2(1, 0);
            Vector2 left = new Vector2(0, 1);
            
            float angle = VectorMath.SignedAngle(forward, left);
            
            Assert.Equal(MathF.PI / 2, angle, precision: 4);
        }

        [Fact]
        public void Rotate_Vector_By90Degrees()
        {
            Vector2 v = new Vector2(1, 0);
            Vector2 rotated = VectorMath.Rotate(v, MathF.PI / 2);
            
            Assert.Equal(0f, rotated.X, precision: 4);
            Assert.Equal(1f, rotated.Y, precision: 4);
        }

        [Fact]
        public void SafeNormalize_ZeroVector_ReturnsFallback()
        {
            Vector2 zero = Vector2.Zero;
            Vector2 fallback = new Vector2(1, 0);
            
            Vector2 result = VectorMath.SafeNormalize(zero, fallback);
            
            Assert.Equal(fallback, result);
        }

        [Fact]
        public void Perpendicular_ReturnsCounterClockwise()
        {
            Vector2 v = new Vector2(1, 0);
            Vector2 perp = VectorMath.Perpendicular(v);
            
            Assert.Equal(0f, perp.X);
            Assert.Equal(1f, perp.Y);
        }

        [Fact]
        public void Right_ReturnsClockwise()
        {
            Vector2 v = new Vector2(1, 0);
            Vector2 right = VectorMath.Right(v);
            
            Assert.Equal(0f, right.X);
            Assert.Equal(-1f, right.Y);
        }
    }
}
