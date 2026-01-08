using System;
using System.Numerics;
using Xunit;
using ModuleHost.Core.Geographic;

namespace ModuleHost.Core.Tests.Geographic
{
    public class WGS84TransformTests
    {
        [Fact]
        public void WGS84Transform_RoundTrip_PreservesCoordinates()
        {
            var transform = new WGS84Transform();
            transform.SetOrigin(37.7749, -122.4194, 0); // San Francisco
            
            var local = transform.ToCartesian(37.8, -122.4, 100);
            var (lat, lon, alt) = transform.ToGeodetic(local);
            
            Assert.Equal(37.8, lat, precision: 6);
            Assert.Equal(-122.4, lon, precision: 6);
            Assert.Equal(100, alt, precision: 1);
        }
        
        [Fact]
        public void WGS84Transform_Origin_ReturnsZero()
        {
            var transform = new WGS84Transform();
            transform.SetOrigin(0, 0, 0); // Equator, Prime Meridian
            
            var local = transform.ToCartesian(0, 0, 0);
            
            Assert.Equal(Vector3.Zero, local);
        }

        [Fact]
        public void SetOrigin_InvalidLatitude_ThrowsException()
        {
            var transform = new WGS84Transform();
            Assert.Throws<ArgumentOutOfRangeException>(() => transform.SetOrigin(91, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => transform.SetOrigin(-91, 0, 0));
        }

        [Fact]
        public void ToCartesian_InvalidLatitude_ThrowsException()
        {
            var transform = new WGS84Transform();
            transform.SetOrigin(0, 0, 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => transform.ToCartesian(91, 0, 0));
        }
    }
}
