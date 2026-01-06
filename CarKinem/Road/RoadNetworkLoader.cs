using System;
using System.IO;
using System.Text.Json;
using System.Numerics;

namespace CarKinem.Road
{
    /// <summary>
    /// Loader for road networks from JSON files.
    /// </summary>
    public static class RoadNetworkLoader
    {
        /// <summary>
        /// Load road network from JSON file.
        /// </summary>
        public static RoadNetworkBlob LoadFromJson(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Road network file not found: {jsonPath}");
            
            string jsonContent = File.ReadAllText(jsonPath);
            var roadData = JsonSerializer.Deserialize<RoadNetworkJson>(jsonContent);
            
            if (roadData == null)
                throw new InvalidOperationException("Failed to deserialize road network JSON");
            
            var builder = new RoadNetworkBuilder();
            
            // Add nodes
            foreach (var node in roadData.Nodes)
            {
                builder.AddNode(node.Position.ToVector2());
            }
            
            // Add segments
            foreach (var seg in roadData.Segments)
            {
                builder.AddSegment(
                    seg.ControlPoints.P0.ToVector2(),
                    seg.ControlPoints.T0.ToVector2(),
                    seg.ControlPoints.P1.ToVector2(),
                    seg.ControlPoints.T1.ToVector2(),
                    seg.SpeedLimit,
                    seg.LaneWidth,
                    seg.LaneCount,
                    seg.StartNodeId,
                    seg.EndNodeId
                );
            }
            
            // Build with metadata
            float cellSize = roadData.Metadata?.GridCellSize ?? 5.0f;
            var bounds = roadData.Metadata?.WorldBounds;
            
            int width, height;
            if (bounds != null)
            {
                float worldWidth = bounds.Max.X - bounds.Min.X;
                float worldHeight = bounds.Max.Y - bounds.Min.Y;
                width = (int)MathF.Ceiling(worldWidth / cellSize);
                height = (int)MathF.Ceiling(worldHeight / cellSize);
            }
            else
            {
                // Default grid size
                width = 100;
                height = 100;
            }
            
            return builder.Build(cellSize, width, height);
        }
    }
}
