using System;
using System.Runtime.InteropServices;
using System.Numerics;
using CarKinem.Trajectory;
using Xunit;

namespace CarKinem.Tests.DataStructures
{
    public class TrajectoryComponentsTests
    {
        [Fact]
        public void TrajectoryWaypoint_IsBlittable()
        {
            Assert.True(IsBlittable<TrajectoryWaypoint>());
        }

        [Fact]
        public void TrajectoryWaypoint_Structure()
        {
             var wp1 = new TrajectoryWaypoint 
            { 
                Position = new Vector2(0, 0), 
                CumulativeDistance = 0 
            };
            var wp2 = new TrajectoryWaypoint 
            { 
                Position = new Vector2(100, 0), 
                CumulativeDistance = 100 
            };
            
            float expected = Vector2.Distance(wp1.Position, wp2.Position);
            Assert.Equal(expected, wp2.CumulativeDistance);
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
