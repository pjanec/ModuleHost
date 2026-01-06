using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace CarKinem.Road
{
    /// <summary>
    /// Root JSON structure for road network files.
    /// </summary>
    public class RoadNetworkJson
    {
        [JsonPropertyName("nodes")]
        public RoadNodeJson[] Nodes { get; set; } = Array.Empty<RoadNodeJson>();
        
        [JsonPropertyName("segments")]
        public RoadSegmentJson[] Segments { get; set; } = Array.Empty<RoadSegmentJson>();
        
        [JsonPropertyName("metadata")]
        public RoadMetadataJson? Metadata { get; set; }
    }
    
    /// <summary>
    /// JSON representation of a road node (junction/intersection).
    /// </summary>
    public class RoadNodeJson
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("position")]
        public Vector2Json Position { get; set; } = new();
    }
    
    /// <summary>
    /// JSON representation of a road segment (Hermite spline).
    /// </summary>
    public class RoadSegmentJson
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("startNodeId")]
        public int StartNodeId { get; set; }
        
        [JsonPropertyName("endNodeId")]
        public int EndNodeId { get; set; }
        
        [JsonPropertyName("controlPoints")]
        public HermiteControlPointsJson ControlPoints { get; set; } = new();
        
        [JsonPropertyName("speedLimit")]
        public float SpeedLimit { get; set; } = 25.0f;
        
        [JsonPropertyName("laneWidth")]
        public float LaneWidth { get; set; } = 3.5f;
        
        [JsonPropertyName("laneCount")]
        public int LaneCount { get; set; } = 1;
    }
    
    /// <summary>
    /// Hermite control points (P0, T0, P1, T1).
    /// </summary>
    public class HermiteControlPointsJson
    {
        [JsonPropertyName("p0")]
        public Vector2Json P0 { get; set; } = new();
        
        [JsonPropertyName("t0")]
        public Vector2Json T0 { get; set; } = new();
        
        [JsonPropertyName("p1")]
        public Vector2Json P1 { get; set; } = new();
        
        [JsonPropertyName("t1")]
        public Vector2Json T1 { get; set; } = new();
    }
    
    /// <summary>
    /// World bounds and spatial grid metadata.
    /// </summary>
    public class RoadMetadataJson
    {
        [JsonPropertyName("worldBounds")]
        public BoundsJson WorldBounds { get; set; } = new();
        
        [JsonPropertyName("gridCellSize")]
        public float GridCellSize { get; set; } = 5.0f;
    }
    
    /// <summary>
    /// 2D bounding box.
    /// </summary>
    public class BoundsJson
    {
        [JsonPropertyName("min")]
        public Vector2Json Min { get; set; } = new();
        
        [JsonPropertyName("max")]
        public Vector2Json Max { get; set; } = new();
    }
    
    /// <summary>
    /// Vector2 JSON representation (System.Numerics.Vector2 doesn't serialize well).
    /// </summary>
    public class Vector2Json
    {
        [JsonPropertyName("x")]
        public float X { get; set; }
        
        [JsonPropertyName("y")]
        public float Y { get; set; }
        
        public Vector2 ToVector2() => new Vector2(X, Y);
    }
}
