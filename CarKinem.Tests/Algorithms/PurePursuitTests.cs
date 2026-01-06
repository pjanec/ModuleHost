using System;
using System.Numerics;
using CarKinem.Controllers;
using Xunit;

namespace CarKinem.Tests.Algorithms
{
    public class PurePursuitTests
    {
        [Fact]
        public void PurePursuit_StraightPath_ReturnsZeroSteering()
        {
            Vector2 pos = Vector2.Zero;
            Vector2 forward = new Vector2(1, 0);
            Vector2 desiredVel = new Vector2(10, 0); // Straight ahead
            
            float steer = PurePursuitController.CalculateSteering(
                pos, forward, desiredVel,
                currentSpeed: 10f,
                wheelBase: 2.7f,
                lookaheadMin: 2f,
                lookaheadMax: 10f,
                maxSteerAngle: 0.6f
            );
            
            Assert.Equal(0f, steer, precision: 3);
        }

        [Fact]
        public void PurePursuit_LeftTurn_ReturnsPositiveSteering()
        {
            Vector2 pos = Vector2.Zero;
            Vector2 forward = new Vector2(1, 0);
            Vector2 desiredVel = new Vector2(10, 10); // 45° left
            
            float steer = PurePursuitController.CalculateSteering(
                pos, forward, desiredVel,
                currentSpeed: 10f,
                wheelBase: 2.7f,
                lookaheadMin: 2f,
                lookaheadMax: 10f,
                maxSteerAngle: 0.6f
            );
            
            Assert.True(steer > 0f, "Left turn should produce positive steering");
        }

        [Fact]
        public void PurePursuit_ClampsSteering_ToMaxAngle()
        {
            Vector2 pos = Vector2.Zero;
            Vector2 forward = new Vector2(1, 0);
            Vector2 desiredVel = new Vector2(0, 10); // 90° left (extreme)
            
            float maxSteer = 0.6f;
            float steer = PurePursuitController.CalculateSteering(
                pos, forward, desiredVel,
                currentSpeed: 10f,
                wheelBase: 2.7f,
                lookaheadMin: 2f,
                lookaheadMax: 10f,
                maxSteerAngle: maxSteer
            );
            
            Assert.InRange(steer, -maxSteer, maxSteer);
        }

        [Fact]
        public void PurePursuit_RightTurn_ReturnsNegativeSteering()
        {
            Vector2 pos = Vector2.Zero;
            Vector2 forward = new Vector2(1, 0);
            Vector2 desiredVel = new Vector2(10, -10); // 45° right
            
            float steer = PurePursuitController.CalculateSteering(
                pos, forward, desiredVel,
                currentSpeed: 10f,
                wheelBase: 2.7f,
                lookaheadMin: 2f,
                lookaheadMax: 10f,
                maxSteerAngle: 0.6f
            );
             Assert.True(steer < 0f, "Right turn should produce negative steering");
        }
        
        [Fact]
        public void PurePursuit_Stopped_MaintainsZeroIter()
        {
            // If desired velocity is zero, it should just maintain heading
            // lookahead point becomes directly in front
            Vector2 pos = Vector2.Zero;
            Vector2 forward = new Vector2(1, 0);
            Vector2 desiredVel = Vector2.Zero; 
            
            float steer = PurePursuitController.CalculateSteering(
                pos, forward, desiredVel,
                currentSpeed: 0f,
                wheelBase: 2.7f,
                lookaheadMin: 2f,
                lookaheadMax: 10f,
                maxSteerAngle: 0.6f
            );

             Assert.Equal(0f, steer, precision: 3);
        }
    }
}
