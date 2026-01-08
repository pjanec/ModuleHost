using System.Numerics;

namespace ModuleHost.Core.Geographic
{
    /// <summary>
    /// Transforms between geodetic (Lat/Lon/Alt) and local Cartesian (XYZ) coordinates.
    /// </summary>
    public interface IGeographicTransform
    {
        /// <summary>
        /// Set tangent plane origin for local coordinate system.
        /// </summary>
        void SetOrigin(double latDeg, double lonDeg, double altMeters);
        
        /// <summary>
        /// Convert geodetic position to local Cartesian (meters from origin).
        /// </summary>
        Vector3 ToCartesian(double latDeg, double lonDeg, double altMeters);
        
        /// <summary>
        /// Convert local Cartesian to geodetic position.
        /// </summary>
        (double lat, double lon, double alt) ToGeodetic(Vector3 localPos);
    }
}
