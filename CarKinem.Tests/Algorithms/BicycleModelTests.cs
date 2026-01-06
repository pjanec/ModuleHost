using System;
using System.Numerics;
using CarKinem.Controllers;
using CarKinem.Core;
using Xunit;

namespace CarKinem.Tests.Algorithms
{
    public class BicycleModelTests
    {
        [Fact]
        public void BicycleModel_StraightMotion_UpdatesPosition()
        {
            var state = new VehicleState
            {
                Position = Vector2.Zero,
                Forward = new Vector2(1, 0),
                Speed = 10f
            };
            
            BicycleModel.Integrate(
                ref state,
                steerAngle: 0f,
                accel: 0f,
                dt: 1.0f,
                wheelBase: 2.7f
            );
            
            // After 1 second at 10 m/s
            Assert.Equal(10f, state.Position.X, precision: 3);
            Assert.Equal(0f, state.Position.Y, precision: 3);
        }

        [Fact]
        public void BicycleModel_Turning_RotatesHeading()
        {
            var state = new VehicleState
            {
                Position = Vector2.Zero,
                Forward = new Vector2(1, 0),
                Speed = 10f
            };
            
            // Apply left steering for 1 second
            BicycleModel.Integrate(
                ref state,
                steerAngle: 0.3f, // ~17 degrees
                accel: 0f,
                dt: 1.0f,
                wheelBase: 2.7f
            );
            
            // Heading should have rotated
            Assert.True(state.Forward.Y > 0f, "Should turn left (positive Y)");
            
            // Forward should still be normalized
            float length = state.Forward.Length();
            Assert.Equal(1f, length, precision: 4);
        }

        [Fact]
        public void BicycleModel_NegativeSpeed_ClampsToZero()
        {
            var state = new VehicleState
            {
                Position = Vector2.Zero,
                Forward = new Vector2(1, 0),
                Speed = 5f
            };
            
            // Apply extreme braking
            BicycleModel.Integrate(
                ref state,
                steerAngle: 0f,
                accel: -10f, // Heavy deceleration
                dt: 1.0f,
                wheelBase: 2.7f
            );
            
            // Speed should be clamped to zero (no reverse)
            Assert.Equal(0f, state.Speed);
        }
        
        [Fact]
        public void BicycleModel_ZeroDt_NoChange()
        {
             var state = new VehicleState
            {
                Position = Vector2.Zero,
                Forward = new Vector2(1, 0),
                Speed = 10f
            };
            Vector2 initialPos = state.Position;
            Vector2 initialFwd = state.Forward;
            
            BicycleModel.Integrate(
                ref state,
                steerAngle: 0.5f,
                accel: 10f,
                dt: 0f,
                wheelBase: 2.7f
            );
            
            Assert.Equal(initialPos, state.Position);
            Assert.Equal(initialFwd, state.Forward);
            Assert.Equal(10f, state.Speed); // Should speed change? accel*dt = 0
        }
    }
}
