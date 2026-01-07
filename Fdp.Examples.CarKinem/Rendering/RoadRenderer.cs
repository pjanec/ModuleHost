using System.Numerics;
using Raylib_cs;

namespace Fdp.Examples.CarKinem.Rendering
{
    public class RoadRenderer
    {
        public void RenderRoadNetwork(global::CarKinem.Road.RoadNetworkBlob network, Camera2D camera)
        {
            if (!network.Nodes.IsCreated || !network.Segments.IsCreated) return;
            
            // Draw segments (roads)
            for (int i = 0; i < network.Segments.Length; i++)
            {
                var segment = network.Segments[i];
                DrawSegment(segment);
            }
            
            // Draw nodes (intersections)
            for (int i = 0; i < network.Nodes.Length; i++)
            {
                var node = network.Nodes[i];
                Raylib.DrawCircleV(node.Position, 2.0f, Color.Blue);
            }
        }
        
        private void DrawSegment(global::CarKinem.Road.RoadSegment segment)
        {
            // Fallback to simple line for now to fix build error with Spline function
            Vector2 start = segment.P0;
            Vector2 end = segment.P1;
            
            Raylib.DrawLineEx(start, end, segment.LaneWidth * segment.LaneCount, Color.Gray);
            Raylib.DrawLineEx(start, end, 1.0f, Color.Yellow);
        }
    }
}
