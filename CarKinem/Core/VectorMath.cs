using System;
using System.Numerics;

namespace CarKinem.Core
{
    /// <summary>
    /// 2D vector math utilities for vehicle navigation.
    /// </summary>
    public static class VectorMath
    {
        /// <summary>
        /// Calculate signed angle from vector 'from' to vector 'to' (radians).
        /// Returns positive for counter-clockwise, negative for clockwise.
        /// Range: [-PI, PI]
        /// </summary>
        public static float SignedAngle(Vector2 from, Vector2 to)
        {
            float dot = Vector2.Dot(from, to);
            float det = from.X * to.Y - from.Y * to.X;
            return MathF.Atan2(det, dot);
        }
        
        /// <summary>
        /// Rotate vector by angle (radians).
        /// </summary>
        public static Vector2 Rotate(Vector2 v, float angleRad)
        {
            float cos = MathF.Cos(angleRad);
            float sin = MathF.Sin(angleRad);
            return new Vector2(
                v.X * cos - v.Y * sin,
                v.X * sin + v.Y * cos
            );
        }
        
        /// <summary>
        /// Get perpendicular vector (90 degrees counter-clockwise).
        /// </summary>
        public static Vector2 Perpendicular(Vector2 v)
        {
            return new Vector2(-v.Y, v.X);
        }
        
        /// <summary>
        /// Get right vector (90 degrees clockwise).
        /// </summary>
        public static Vector2 Right(Vector2 forward)
        {
            return new Vector2(forward.Y, -forward.X);
        }
        
        /// <summary>
        /// Safe normalize with fallback for zero-length vectors.
        /// </summary>
        public static Vector2 SafeNormalize(Vector2 v, Vector2 fallback)
        {
            float lengthSq = v.LengthSquared();
            return lengthSq > 1e-6f ? Vector2.Normalize(v) : fallback;
        }
    }
}
