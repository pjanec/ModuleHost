using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class RoadGraphNavigatorTests
    {
        [Fact]
        public void FindClosestRoadPoint_FindsNearestSegment()
        {
            // Build simple road network
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            // Point near the road
            Vector2 testPoint = new Vector2(50, 5); // 5m above midpoint
            
            var (segId, nearestPoint, dist) = RoadGraphNavigator.FindClosestRoadPoint(testPoint, blob);
            
            Assert.Equal(0, segId);
            Assert.True(dist < 10f, "Should find point within 10m");
            
            blob.Dispose();
        }
        
        [Fact]
        public void SampleRoadSegment_UsesDistanceLUT()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            // Copy to local variable to pass to unsafe method
            var segment = blob.Segments[0];
            
            // Sample at midpoint
            var (pos, tangent, speed) = RoadGraphNavigator.SampleRoadSegment(segment, segment.Length / 2);
            
            // Position should be approximately halfway
            Assert.InRange(pos.X, 40f, 60f);
            Assert.Equal(1f, tangent.Length(), precision: 2); // Normalized
            Assert.Equal(segment.SpeedLimit, speed);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Approaching_TransitionsToFollowing()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Approaching,
                FinalDestination = new Vector2(200, 0),
                ArrivalRadius = 2.0f
            };
            
            // Position very close to road
            Vector2 currentPos = new Vector2(1, 0.5f);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should have transitioned to Following
            Assert.Equal(RoadGraphPhase.Following, nav.RoadPhase);
            Assert.Equal(0, nav.CurrentSegmentId);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Following_ReturnsRoadTarget()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Following,
                CurrentSegmentId = 0,
                ProgressS = 25f,
                FinalDestination = new Vector2(200, 0),
                ArrivalRadius = 2.0f
            };
            
            Vector2 currentPos = new Vector2(25, 0);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should get target ahead on road
            Assert.True(targetHeading.X > 0.9f, "Should point forward");
            Assert.Equal(blob.Segments[0].SpeedLimit, targetSpeed);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Leaving_GoesToDestination()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Leaving,
                FinalDestination = new Vector2(110, 10),
                ArrivalRadius = 2.0f
            };
            
            // Already near end of road, heading to destination
            Vector2 currentPos = new Vector2(100, 5);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should target final destination directly
            Assert.Equal(nav.FinalDestination, targetPos);
            
            blob.Dispose();
        }
        
        [Fact]
        public void UpdateRoadGraphNavigation_Arrived_StopsAtDestination()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 20, gridHeight: 20);
            
            var nav = new NavState
            {
                Mode = NavigationMode.RoadGraph,
                RoadPhase = RoadGraphPhase.Leaving,
                FinalDestination = new Vector2(101, 0),
                ArrivalRadius = 2.0f
            };
            
            // Very close to destination
            Vector2 currentPos = new Vector2(100.5f, 0);
            
            var (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                ref nav,
                currentPos,
                blob
            );
            
            // Should have arrived
            Assert.Equal(RoadGraphPhase.Arrived, nav.RoadPhase);
            Assert.Equal(1, nav.HasArrived);
            Assert.Equal(0f, targetSpeed);
            
            blob.Dispose();
        }
    }
}
