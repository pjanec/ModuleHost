using System;
using System.Runtime.InteropServices;
using System.Numerics;
using CarKinem.Core;
using Xunit;

namespace CarKinem.Tests.DataStructures
{
    public class VehicleComponentsTests
    {
        [Fact]
        public void VehicleState_IsBlittable()
        {
            Assert.True(IsBlittable<VehicleState>());
        }

        [Fact]
        public void VehicleState_HasExpectedSize()
        {
            // Position (8) + Forward (8) + Speed (4) + SteerAngle (4) + Accel (4) + Pitch (4) + Roll (4) + CurLane (4)
            // = 40 bytes
            int expected = sizeof(float) * 2 * 2 + sizeof(float) * 5 + sizeof(int);
            Assert.Equal(40, Marshal.SizeOf<VehicleState>());
            Assert.Equal(expected, Marshal.SizeOf<VehicleState>());
        }

        [Fact]
        public void VehicleState_DefaultValues_AreCorrect()
        {
            var state = new VehicleState();
            Assert.Equal(Vector2.Zero, state.Position);
            Assert.Equal(0f, state.Speed);
            Assert.Equal(0, state.CurrentLaneIndex);
        }

        [Fact]
        public void NavState_IsBlittable()
        {
            Assert.True(IsBlittable<NavState>());
        }

        [Fact]
        public void VehicleParams_IsBlittable()
        {
            Assert.True(IsBlittable<VehicleParams>());
        }

        [Fact]
        public void NavigationMode_IsOneByte()
        {
            Assert.Equal(1, sizeof(NavigationMode));
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
