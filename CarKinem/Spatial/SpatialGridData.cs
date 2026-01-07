using CarKinem.Spatial;
using Fdp.Kernel;

namespace CarKinem.Spatial
{
    /// <summary>
    /// Singleton component containing spatial hash grid.
    /// Produced by SpatialHashSystem, consumed by CarKinematicsSystem.
    /// </summary>
    public struct SpatialGridData
    {
        public SpatialHashGrid Grid;
    }
}
