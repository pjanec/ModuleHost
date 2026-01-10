namespace CarKinem.Trajectory
{
    /// <summary>
    /// Trajectory interpolation modes.
    /// </summary>
    public enum TrajectoryInterpolation : byte
    {
        /// <summary>
        /// Linear interpolation between waypoints (sharp corners).
        /// Fast and simple, but robotic-looking paths.
        /// </summary>
        Linear = 0,
        
        /// <summary>
        /// Cubic Hermite spline interpolation with automatic tangents (Catmull-Rom).
        /// Smooth curves passing through all waypoints.
        /// Tangents computed automatically from neighboring waypoints.
        /// <para>Performance: Sampling is ~2x more expensive than Linear due to cubic evaluation.</para>
        /// <para>Use for: AI patrol paths, organic movement, cinematic cameras.</para>
        /// </summary>
        CatmullRom = 1,
        
        /// <summary>
        /// Cubic Hermite spline interpolation with explicit tangents.
        /// Maximum control over curve shape.
        /// Requires user-provided tangent vectors.
        /// <para>Performance: Same sampling cost as CatmullRom.</para>
        /// <para>Use for: Fixed racing lines, loops (manually matching start/end tangents).</para>
        /// </summary>
        HermiteExplicit = 2
    }
}
