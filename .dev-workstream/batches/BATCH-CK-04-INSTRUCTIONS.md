# BATCH-CK-04: Road Network JSON Loading

**Batch ID:** BATCH-CK-04  
**Phase:** Road Network  
**Prerequisites:** BATCH-CK-01 (Road Network structs) COMPLETE  
**Assigned:** 2026-01-06  

---

## 📋 Objectives

Implement road network loading from JSON files:
1. Define JSON schema classes for deserialization
2. Implement RoadNetworkBuilder for Hermite spline processing
3. Implement RoadNetworkLoader for JSON parsing
4. Distance LUT precomputation for arc-length parameterization
5. Spatial grid rasterization using Bresenham-like algorithm

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Road Network Section:** Lines 271-370, 594-683 in design doc

---

## 📁 Project Structure

Add to existing `CarKinem` project:

```
D:\WORK\ModuleHost\CarKinem\
└── Road\
    ├── RoadSegment.cs              ← EXISTS (from CK-01)
    ├── RoadNode.cs                 ← EXISTS (from CK-01)
    ├── RoadNetworkBlob.cs          ← EXISTS (from CK-01)
    ├── RoadNetworkJson.cs          ← NEW (JSON schema)
    ├── RoadNetworkBuilder.cs       ← NEW
    └── RoadNetworkLoader.cs        ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Road\
    ├── RoadNetworkBuilderTests.cs  ← NEW
    ├── RoadNetworkLoaderTests.cs   ← NEW
    └── TestData\
        └── sample_road.json        ← NEW (test fixture)
```

---

## 🎯 Tasks

### Task CK-04-01: JSON Schema Classes

**File:** `CarKinem/Road/RoadNetworkJson.cs`

Define JSON deserialization classes (design doc lines 594-683):

```csharp
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
```

---

### Task CK-04-02: RoadNetworkBuilder

**File:** `CarKinem/Road/RoadNetworkBuilder.cs`

Implement builder with Hermite LUT generation and spatial grid:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel.Collections;

namespace CarKinem.Road
{
    /// <summary>
    /// Builder for constructing RoadNetworkBlob from components.
    /// Handles Hermite spline LUT precomputation and spatial grid rasterization.
    /// </summary>
    public class RoadNetworkBuilder
    {
        private readonly List<RoadNode> _nodes = new();
        private readonly List<RoadSegment> _segments = new();
        
        /// <summary>
        /// Add a road node (junction/intersection).
        /// </summary>
        public void AddNode(Vector2 position)
        {
            _nodes.Add(new RoadNode
            {
                Position = position,
                ConnectedSegmentCount = 0
            });
        }
        
        /// <summary>
        /// Add a road segment with Hermite control points.
        /// </summary>
        public void AddSegment(
            Vector2 p0, Vector2 t0,
            Vector2 p1, Vector2 t1,
            float speedLimit = 25.0f,
            float laneWidth = 3.5f,
            int laneCount = 1,
            int startNodeIdx = -1,
            int endNodeIdx = -1)
        {
            // Precompute length via sampling
            float length = ComputeHermiteLength(p0, t0, p1, t1);
            
            var segment = new RoadSegment
            {
                P0 = p0,
                T0 = t0,
                P1 = p1,
                T1 = t1,
                Length = length,
                SpeedLimit = speedLimit,
                LaneWidth = laneWidth,
                LaneCount = laneCount,
                StartNodeIndex = startNodeIdx,
                EndNodeIndex = endNodeIdx
            };
            
            // Precompute distance LUT (8 samples)
            ComputeDistanceLUT(ref segment);
            
            _segments.Add(segment);
        }
        
        /// <summary>
        /// Build final RoadNetworkBlob with spatial grid.
        /// </summary>
        public RoadNetworkBlob Build(float cellSize, int gridWidth, int gridHeight)
        {
            var blob = new RoadNetworkBlob
            {
                Nodes = new NativeArray<RoadNode>(_nodes.Count, Allocator.Persistent),
                Segments = new NativeArray<RoadSegment>(_segments.Count, Allocator.Persistent),
                GridHead = new NativeArray<int>(gridWidth * gridHeight, Allocator.Persistent),
                GridNext = new NativeArray<int>(_segments.Count * 100, Allocator.Persistent), // Estimate
                GridValues = new NativeArray<int>(_segments.Count * 100, Allocator.Persistent),
                CellSize = cellSize,
                Width = gridWidth,
                Height = gridHeight
            };
            
            // Copy nodes
            for (int i = 0; i < _nodes.Count; i++)
                blob.Nodes[i] = _nodes[i];
            
            // Copy segments
            for (int i = 0; i < _segments.Count; i++)
                blob.Segments[i] = _segments[i];
            
            // Build spatial grid
            BuildSpatialGrid(ref blob);
            
            return blob;
        }
        
        /// <summary>
        /// Compute Hermite spline arc length via trapezoidal integration.
        /// </summary>
        private float ComputeHermiteLength(Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            const int SAMPLES = 32;
            float length = 0f;
            Vector2 prevPoint = p0;
            
            for (int i = 1; i <= SAMPLES; i++)
            {
                float t = i / (float)SAMPLES;
                Vector2 point = EvaluateHermite(t, p0, t0, p1, t1);
                length += Vector2.Distance(prevPoint, point);
                prevPoint = point;
            }
            
            return length;
        }
        
        /// <summary>
        /// Precompute distance LUT for constant-speed sampling.
        /// Maps 8 uniformly spaced distances to parameter t.
        /// </summary>
        private unsafe void ComputeDistanceLUT(ref RoadSegment segment)
        {
            const int LUT_SIZE = 8;
            
            for (int i = 0; i < LUT_SIZE; i++)
            {
                float targetDist = (i / (float)(LUT_SIZE - 1)) * segment.Length;
                
                // Binary search for t that gives targetDist
                float t = FindParameterForDistance(segment, targetDist);
                segment.DistanceLUT[i] = t;
            }
        }
        
        /// <summary>
        /// Find parameter t that produces a given arc-length distance.
        /// Uses binary search with numerical integration.
        /// </summary>
        private float FindParameterForDistance(RoadSegment segment, float targetDist)
        {
            float tMin = 0f;
            float tMax = 1f;
            const int MAX_ITERATIONS = 10;
            
            for (int iter = 0; iter < MAX_ITERATIONS; iter++)
            {
                float tMid = (tMin + tMax) * 0.5f;
                float distAtMid = ComputeDistanceAtT(segment, tMid);
                
                if (MathF.Abs(distAtMid - targetDist) < 0.01f)
                    return tMid;
                
                if (distAtMid < targetDist)
                    tMin = tMid;
                else
                    tMax = tMid;
            }
            
            return (tMin + tMax) * 0.5f;
        }
        
        /// <summary>
        /// Compute arc-length distance from start to parameter t.
        /// </summary>
        private float ComputeDistanceAtT(RoadSegment segment, float t)
        {
            const int SAMPLES = 16;
            float dist = 0f;
            Vector2 prevPoint = segment.P0;
            
            for (int i = 1; i <= (int)(t * SAMPLES); i++)
            {
                float tSample = i / (float)SAMPLES;
                Vector2 point = EvaluateHermite(tSample, segment.P0, segment.T0, segment.P1, segment.T1);
                dist += Vector2.Distance(prevPoint, point);
                prevPoint = point;
            }
            
            return dist;
        }
        
        /// <summary>
        /// Evaluate Hermite spline at parameter t.
        /// </summary>
        private Vector2 EvaluateHermite(float t, Vector2 p0, Vector2 t0, Vector2 p1, Vector2 t1)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            
            float h00 = 2 * t3 - 3 * t2 + 1;
            float h10 = t3 - 2 * t2 + t;
            float h01 = -2 * t3 + 3 * t2;
            float h11 = t3 - t2;
            
            return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
        }
        
        /// <summary>
        /// Build spatial hash grid for fast segment lookup.
        /// Uses Bresenham-like rasterization to find all cells a segment touches.
        /// </summary>
        private void BuildSpatialGrid(ref RoadNetworkBlob blob)
        {
            // Initialize grid heads to -1 (empty)
            for (int i = 0; i < blob.GridHead.Length; i++)
                blob.GridHead[i] = -1;
            
            int nextFreeSlot = 0;
            
            // Rasterize each segment
            for (int segId = 0; segId < blob.Segments.Length; segId++)
            {
                var segment = blob.Segments[segId];
                
                // Sample segment and add to all touched cells
                const int RASTER_SAMPLES = 16;
                for (int i = 0; i <= RASTER_SAMPLES; i++)
                {
                    float t = i / (float)RASTER_SAMPLES;
                    Vector2 point = EvaluateHermite(t, segment.P0, segment.T0, segment.P1, segment.T1);
                    
                    int cellX = (int)(point.X / blob.CellSize);
                    int cellY = (int)(point.Y / blob.CellSize);
                    
                    if (cellX < 0 || cellX >= blob.Width || cellY < 0 || cellY >= blob.Height)
                        continue;
                    
                    int cellIdx = cellY * blob.Width + cellX;
                    
                    // Add to linked list (avoid duplicates)
                    if (!ContainsSegment(blob, cellIdx, segId))
                    {
                        blob.GridValues[nextFreeSlot] = segId;
                        blob.GridNext[nextFreeSlot] = blob.GridHead[cellIdx];
                        blob.GridHead[cellIdx] = nextFreeSlot;
                        nextFreeSlot++;
                    }
                }
            }
        }
        
        /// <summary>
        /// Check if segment is already in cell's linked list.
        /// </summary>
        private bool ContainsSegment(RoadNetworkBlob blob, int cellIdx, int segId)
        {
            int head = blob.GridHead[cellIdx];
            while (head >= 0)
            {
                if (blob.GridValues[head] == segId)
                    return true;
                head = blob.GridNext[head];
            }
            return false;
        }
    }
}
```

---

### Task CK-04-03: RoadNetworkLoader

**File:** `CarKinem/Road/RoadNetworkLoader.cs`

Implement JSON loading (design doc lines 652-683):

```csharp
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
                width = height = 100;
            }
            
            return builder.Build(cellSize, width, height);
        }
    }
}
```

---

### Task CK-04-04: Test Data

**File:** `CarKinem.Tests/Road/TestData/sample_road.json`

```json
{
  "nodes": [
    { "id": 0, "position": { "x": 0, "y": 0 } },
    { "id": 1, "position": { "x": 100, "y": 0 } },
    { "id": 2, "position": { "x": 100, "y": 100 } }
  ],
  "segments": [
    {
      "id": 0,
      "startNodeId": 0,
      "endNodeId": 1,
      "controlPoints": {
        "p0": { "x": 0, "y": 0 },
        "t0": { "x": 50, "y": 0 },
        "p1": { "x": 100, "y": 0 },
        "t1": { "x": 50, "y": 0 }
      },
      "speedLimit": 25.0,
      "laneWidth": 3.5,
      "laneCount": 2
    },
    {
      "id": 1,
      "startNodeId": 1,
      "endNodeId": 2,
      "controlPoints": {
        "p0": { "x": 100, "y": 0 },
        "t0": { "x": 0, "y": 50 },
        "p1": { "x": 100, "y": 100 },
        "t1": { "x": 0, "y": 50 }
      },
      "speedLimit": 20.0,
      "laneWidth": 3.5,
      "laneCount": 1
    }
  ],
  "metadata": {
    "worldBounds": {
      "min": { "x": -10, "y": -10 },
      "max": { "x": 110, "y": 110 }
    },
    "gridCellSize": 5.0
  }
}
```

---

### Task CK-04-05: Tests

**File:** `CarKinem.Tests/Road/RoadNetworkBuilderTests.cs`

```csharp
using System.Numerics;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class RoadNetworkBuilderTests
    {
        [Fact]
        public void Builder_AddNodes_StoresCorrectly()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddNode(new Vector2(0, 0));
            builder.AddNode(new Vector2(100, 0));
            
            var blob = builder.Build(cellSize: 5f, gridWidth: 50, gridHeight: 50);
            
            Assert.Equal(2, blob.Nodes.Length);
            Assert.Equal(new Vector2(0, 0), blob.Nodes[0].Position);
            Assert.Equal(new Vector2(100, 0), blob.Nodes[1].Position);
            
            blob.Dispose();
        }
        
        [Fact]
        public void Builder_AddSegment_ComputesLength()
        {
            var builder = new RoadNetworkBuilder();
            
            // Straight horizontal segment
            builder.AddSegment(
                p0: new Vector2(0, 0),
                t0: new Vector2(50, 0),
                p1: new Vector2(100, 0),
                t1: new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 5f, gridWidth: 50, gridHeight: 50);
            
            Assert.Single(blob.Segments);
            // Length should be ~100m for straight segment
            Assert.InRange(blob.Segments[0].Length, 95f, 105f);
            
            blob.Dispose();
        }
        
        [Fact]
        public void Builder_DistanceLUT_Has8Entries()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                new Vector2(0, 0), new Vector2(50, 0),
                new Vector2(100, 0), new Vector2(50, 0)
            );
            
            var blob = builder.Build(cellSize: 5f, gridWidth: 50, gridHeight: 50);
            
            unsafe
            {
                // LUT should have values from 0 to 1
                Assert.Equal(0f, blob.Segments[0].DistanceLUT[0], precision: 2);
                Assert.Equal(1f, blob.Segments[0].DistanceLUT[7], precision: 2);
            }
            
            blob.Dispose();
        }
        
        [Fact]
        public void Builder_SpatialGrid_IndexesSegments()
        {
            var builder = new RoadNetworkBuilder();
            builder.AddSegment(
                new Vector2(0, 0), new Vector2(25, 0),
                new Vector2(50, 0), new Vector2(25, 0)
            );
            
            var blob = builder.Build(cellSize: 10f, gridWidth: 10, gridHeight: 10);
            
            // Segment at y=0, x=[0,50] should be in cells along that line
            int cellIdxAtOrigin = 0; // Cell (0,0)
            Assert.NotEqual(-1, blob.GridHead[cellIdxAtOrigin]); // Should have segment
            
            blob.Dispose();
        }
    }
}
```

**File:** `CarKinem.Tests/Road/RoadNetworkLoaderTests.cs`

```csharp
using System;
using System.IO;
using CarKinem.Road;
using Xunit;

namespace CarKinem.Tests.Road
{
    public class RoadNetworkLoaderTests
    {
        private readonly string _testDataPath;
        
        public RoadNetworkLoaderTests()
        {
            // Assume test data is in TestData subfolder
            _testDataPath = Path.Combine(
                Path.GetDirectoryName(typeof(RoadNetworkLoaderTests).Assembly.Location)!,
                "Road", "TestData", "sample_road.json"
            );
        }
        
        [Fact]
        public void LoadFromJson_ValidFile_LoadsSuccessfully()
        {
            if (!File.Exists(_testDataPath))
            {
                // Create test file dynamically if missing
                Directory.CreateDirectory(Path.GetDirectoryName(_testDataPath)!);
                File.WriteAllText(_testDataPath, GetSampleJson());
            }
            
            var blob = RoadNetworkLoader.LoadFromJson(_testDataPath);
            
            Assert.Equal(3, blob.Nodes.Length);
            Assert.Equal(2, blob.Segments.Length);
            
            blob.Dispose();
        }
        
        [Fact]
        public void LoadFromJson_MissingFile_ThrowsException()
        {
            Assert.Throws<FileNotFoundException>(() =>
                RoadNetworkLoader.LoadFromJson("nonexistent.json")
            );
        }
        
        [Fact]
        public void LoadFromJson_LoadsSegmentProperties()
        {
            if (!File.Exists(_testDataPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_testDataPath)!);
                File.WriteAllText(_testDataPath, GetSampleJson());
            }
            
            var blob = RoadNetworkLoader.LoadFromJson(_testDataPath);
            
            // First segment
            Assert.Equal(25f, blob.Segments[0].SpeedLimit);
            Assert.Equal(3.5f, blob.Segments[0].LaneWidth);
            Assert.Equal(2, blob.Segments[0].LaneCount);
            
            blob.Dispose();
        }
        
        private string GetSampleJson()
        {
            return @"{
  ""nodes"": [
    { ""id"": 0, ""position"": { ""x"": 0, ""y"": 0 } },
    { ""id"": 1, ""position"": { ""x"": 100, ""y"": 0 } },
    { ""id"": 2, ""position"": { ""x"": 100, ""y"": 100 } }
  ],
  ""segments"": [
    {
      ""id"": 0,
      ""startNodeId"": 0,
      ""endNodeId"": 1,
      ""controlPoints"": {
        ""p0"": { ""x"": 0, ""y"": 0 },
        ""t0"": { ""x"": 50, ""y"": 0 },
        ""p1"": { ""x"": 100, ""y"": 0 },
        ""t1"": { ""x"": 50, ""y"": 0 }
      },
      ""speedLimit"": 25.0,
      ""laneWidth"": 3.5,
      ""laneCount"": 2
    },
    {
      ""id"": 1,
      ""startNodeId"": 1,
      ""endNodeId"": 2,
      ""controlPoints"": {
        ""p0"": { ""x"": 100, ""y"": 0 },
        ""t0"": { ""x"": 0, ""y"": 50 },
        ""p1"": { ""x"": 100, ""y"": 100 },
        ""t1"": { ""x"": 0, ""y"": 50 }
      },
      ""speedLimit"": 20.0,
      ""laneWidth"": 3.5,
      ""laneCount"": 1
    }
  ],
  ""metadata"": {
    ""worldBounds"": {
      ""min"": { ""x"": -10, ""y"": -10 },
      ""max"": { ""x"": 110, ""y"": 110 }
    },
    ""gridCellSize"": 5.0
  }
}";
        }
    }
}
```

---

## ✅ Acceptance Criteria

### Build & Quality
- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 12 unit tests
- [ ] XML documentation on all public methods

### Functionality
- [ ] JSON deserialization works correctly
- [ ] Hermite spline length computed accurately (within 5%)
- [ ] Distance LUT has 8 entries, ranges from 0.0 to 1.0
- [ ] Spatial grid correctly indexes all segments
- [ ] No duplicate segments in grid cells
- [ ] Loader handles missing files gracefully
- [ ] Builder disposes NativeArrays correctly

### Code Quality
- [ ] JSON schema classes use proper attributes
- [ ] Builder uses numerical integration for arc-length
- [ ] Binary search for LUT computation is efficient
- [ ] Spatial rasterization avoids duplicates
- [ ] Proper error handling (file not found, invalid JSON)

### Test Quality
- [ ] Tests cover:
  - JSON parsing (valid file, missing file)
  - Segment length calculation
  - LUT correctness (boundary values)
  - Spatial grid indexing
  - Node/segment property loading
  - Dispose pattern

---

## 📤 Submission Instructions

Submit your report to:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-04-REPORT.md
```

Include:
- Test results (all 12+ tests passing)
- Sample road network file used for testing
- Any observations on Hermite accuracy
- Questions for review

---

## 📚 Reference Materials

- **Design Doc (Road Network):** Lines 271-370
- **JSON Schema:** Lines 594-683
- **Hermite Splines:** Standard cubic Hermite interpolation
- **Bresenham Rasterization:** Grid cell intersection detection

---

**Time Estimate:** 5-6 hours (JSON + math + testing)

**Focus:** Numerical accuracy (LUT), spatial indexing correctness, proper JSON handling.

---

_Batch prepared by: Development Lead_  
_Date: 2026-01-06 23:50_
