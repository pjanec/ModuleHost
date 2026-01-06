using System;
using CarKinem.Controllers;
using Xunit;

namespace CarKinem.Tests.Algorithms
{
    public class SpeedControllerTests
    {
        [Fact]
        public void SpeedController_Accelerate_WhenBelowTarget()
        {
            float accel = SpeedController.CalculateAcceleration(
                currentSpeed: 5f,
                targetSpeed: 10f,
                gain: 2.0f,
                maxAccel: 3.0f,
                maxDecel: 6.0f
            );
            
            Assert.True(accel > 0f, "Should accelerate");
            Assert.InRange(accel, 0f, 3.0f);
        }

        [Fact]
        public void SpeedController_Decelerate_WhenAboveTarget()
        {
            float accel = SpeedController.CalculateAcceleration(
                currentSpeed: 15f,
                targetSpeed: 10f,
                gain: 2.0f,
                maxAccel: 3.0f,
                maxDecel: 6.0f
            );
            
            Assert.True(accel < 0f, "Should decelerate");
            Assert.InRange(accel, -6.0f, 0f);
        }

        [Fact]
        public void SpeedController_ClampAcceleration_ToMaxValues()
        {
            float accel = SpeedController.CalculateAcceleration(
                currentSpeed: 0f,
                targetSpeed: 100f, // Extreme difference
                gain: 10.0f,       // High gain
                maxAccel: 3.0f,
                maxDecel: 6.0f
            );
            
            Assert.Equal(3.0f, accel); // Should clamp to max accel
        }

        [Fact]
        public void SpeedController_ClampDeceleration_ToMaxValues()
        {
            float accel = SpeedController.CalculateAcceleration(
                currentSpeed: 100f,
                targetSpeed: 0f,
                gain: 10.0f,
                maxAccel: 3.0f,
                maxDecel: 6.0f
            );
            
            Assert.Equal(-6.0f, accel); // Should clamp to max decel (negative)
        }
    }
}
