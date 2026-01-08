using System;
using System.Numerics;

namespace ModuleHost.Core.Geographic
{
    /// <summary>
    /// WGS84 ellipsoid implementation using East-North-Up tangent plane.
    /// Accurate for distances < 100km from origin.
    /// </summary>
    public class WGS84Transform : IGeographicTransform
    {
        private const double WGS84_A = 6378137.0; // Semi-major axis (m)
        private const double WGS84_F = 1.0 / 298.257223563; // Flattening
        private const double WGS84_E2 = WGS84_F * (2.0 - WGS84_F); // Eccentricity²
        
        private double _originLat;
        private double _originLon;
        private double _originAlt;
        private Matrix4x4 _ecefToLocal;
        private Matrix4x4 _localToEcef;
        
        public void SetOrigin(double latDeg, double lonDeg, double altMeters)
        {
            if (latDeg < -90.0 || latDeg > 90.0)
                throw new ArgumentOutOfRangeException(nameof(latDeg), "Latitude must be between -90 and 90 degrees.");

            _originLat = latDeg * Math.PI / 180.0;
            _originLon = lonDeg * Math.PI / 180.0;
            _originAlt = altMeters;
            
            // Compute ECEF origin
            var originEcef = GeodeticToECEF(_originLat, _originLon, _originAlt);
            
            // Build rotation matrix (ENU)
            double sinLat = Math.Sin(_originLat);
            double cosLat = Math.Cos(_originLat);
            double sinLon = Math.Sin(_originLon);
            double cosLon = Math.Cos(_originLon);
            
            // ECEF → Local (ENU)
            _ecefToLocal = new Matrix4x4(
                (float)-sinLon,              (float)cosLon,             0, 0,
                (float)(-sinLat * cosLon),    (float)(-sinLat * sinLon),   (float)cosLat, 0,
                 (float)(cosLat * cosLon),     (float)(cosLat * sinLon),   (float)sinLat, 0,
                0, 0, 0, 1
            );
            
            Matrix4x4.Invert(_ecefToLocal, out _localToEcef);
        }
        
        public Vector3 ToCartesian(double latDeg, double lonDeg, double altMeters)
        {
            if (latDeg < -90.0 || latDeg > 90.0)
                throw new ArgumentOutOfRangeException(nameof(latDeg), "Latitude must be between -90 and 90 degrees.");

            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            
            var (x, y, z) = GeodeticToECEF(lat, lon, altMeters);
            var (ox, oy, oz) = GeodeticToECEF(_originLat, _originLon, _originAlt);
            
            // Difference in doubles (high precision delta)
            double dx = x - ox;
            double dy = y - oy;
            double dz = z - oz;
            
            // Now safe to cast to float for local rotation (since delta is relatively small)
            var delta = new Vector3((float)dx, (float)dy, (float)dz);
            return Vector3.Transform(delta, _ecefToLocal);
        }
        
        public (double lat, double lon, double alt) ToGeodetic(Vector3 localPos)
        {
            var (ox, oy, oz) = GeodeticToECEF(_originLat, _originLon, _originAlt);
            
            // Rotate back to ECEF delta
            var delta = Vector3.Transform(localPos, _localToEcef);
            
            // Add delta to origin (in doubles)
            double x = ox + delta.X;
            double y = oy + delta.Y;
            double z = oz + delta.Z;
            
            return ECEFToGeodetic(x, y, z);
        }
        
        // WGS84 conversion helpers
        private (double x, double y, double z) GeodeticToECEF(double lat, double lon, double alt)
        {
            double N = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
            double x = (N + alt) * Math.Cos(lat) * Math.Cos(lon);
            double y = (N + alt) * Math.Cos(lat) * Math.Sin(lon);
            double z = (N * (1.0 - WGS84_E2) + alt) * Math.Sin(lat);
            return (x, y, z);
        }
        
        private (double, double, double) ECEFToGeodetic(double x, double y, double z)
        {
            // Iterative solution
            
            double lon = Math.Atan2(y, x);
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1.0 - WGS84_E2));
            
            for (int i = 0; i < 5; i++)
            {
                double N = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
                double alt = p / Math.Cos(lat) - N;
                lat = Math.Atan2(z, p * (1.0 - WGS84_E2 * N / (N + alt)));
            }
            
            double N_final = WGS84_A / Math.Sqrt(1.0 - WGS84_E2 * Math.Sin(lat) * Math.Sin(lat));
            double alt_final = p / Math.Cos(lat) - N_final;
            
            return (lat * 180.0 / Math.PI, lon * 180.0 / Math.PI, alt_final);
        }
    }
}
