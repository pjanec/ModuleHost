namespace ModuleHost.Core.Geographic
{
    /// <summary>
    /// Represents entity position in WGS84 Geodetic coordinates.
    /// Managed component (uses doubles) for network interoperability.
    /// </summary>
    public class PositionGeodetic
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
    }
}
