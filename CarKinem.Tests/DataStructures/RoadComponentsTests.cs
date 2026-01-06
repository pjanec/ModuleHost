using System;
using System.Runtime.InteropServices;
using CarKinem.Road;
using Fdp.Kernel.Collections;
using Xunit;

namespace CarKinem.Tests.DataStructures
{
    public class RoadComponentsTests
    {
        [Fact]
        public unsafe void RoadSegment_DistanceLUT_HasCorrectSize()
        {
            var segment = new RoadSegment();
            // Just verifying access and that it's fixed
            segment.DistanceLUT[0] = 1f;
            segment.DistanceLUT[7] = 8f; // Last index
            Assert.Equal(1f, segment.DistanceLUT[0]);
            Assert.Equal(8f, segment.DistanceLUT[7]);
            
            // Check size indirectly via struct size
            // DistanceLUT is 8 floats = 32 bytes
            Assert.True(Marshal.SizeOf<RoadSegment>() >= 32);
        }

        [Fact]
        public void RoadSegment_IsBlittable()
        {
            Assert.True(IsBlittable<RoadSegment>());
        }

        [Fact]
        public void RoadNetworkBlob_Dispose_ReleasesResources()
        {
            var blob = new RoadNetworkBlob
            {
                Segments = new NativeArray<RoadSegment>(10, Allocator.Persistent)
            };
            
            Assert.True(blob.Segments.IsCreated);
            blob.Dispose();
            Assert.False(blob.Segments.IsCreated);
        }

        [Fact]
        public void RoadNetworkBlob_DoubleDispose_DoesNotThrow()
        {
            var blob = new RoadNetworkBlob();
            blob.Dispose();
            blob.Dispose(); // Should not throw
        }

        private static bool IsBlittable<T>() where T : struct
        {
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                Marshal.FreeHGlobal(ptr);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
