# Commit Message for ModuleHost

## Title
feat(CarKinem): Add road network JSON loading with Hermite LUTs

## Body
Implement complete road network loading pipeline:

**JSON Schema Classes:**
- `RoadNetworkJson`, `RoadNodeJson`, `RoadSegmentJson`
- `HermiteControlPointsJson` for cubic Hermite spline control points
- `Vector2Json` for JSON-compatible vector serialization
- `RoadMetadataJson`, `BoundsJson` for world metadata
- Full System.Text.Json attribute decoration

**RoadNetworkBuilder:**
- `AddNode()`, `AddSegment()` API for programmatic construction
- **Hermite Arc-Length Computation** via trapezoidal integration (32 samples)
- **Distance LUT Precomputation** using binary search (8-element LUT)
  - Maps uniformly spaced arc-length distances to parameter t
  - Enables constant-speed sampling along curved segments
  - Binary search with 10 iterations, 0.01m tolerance
- **Spatial Grid Rasterization** (Bresenham-like sampling)
  - 16 samples per segment
  - Linked-list structure (GridHead, GridNext, GridValues)
  - Duplicate detection via `ContainsSegment()`
- `Build()` method creates final `RoadNetworkBlob` with all data

**RoadNetworkLoader:**
- `LoadFromJson()` static method
- File I/O with proper error handling
- Automatic grid size calculation from world bounds
- Metadata parsing (cell size, bounds)

**Hermite Spline Math:**
- Standard cubic Hermite basis functions (h00, h10, h01, h11)
- Used for both length computation and LUT generation
- `EvaluateHermite()` evaluates position at parameter t

**Test Coverage:** 9 tests validating:
- Node storage correctness
- Segment length accuracy (within 5% for straight segments)
- LUT generation (8 entries, bounds checking)
- Spatial grid indexing (segments in correct cells)
- JSON loading (valid file, missing file exception, property parsing)
- Dispose pattern (resource cleanup)

**Sample Test Data:**
- `sample_road.json` with 3 nodes, 2 segments
- Metadata with bounds and 5.0m grid cell size

**Zero warnings, all 64 tests passing (9 new).**

## Files Changed
- CarKinem/Road/RoadNetworkJson.cs (new)
- CarKinem/Road/RoadNetworkBuilder.cs (new)
- CarKinem/Road/RoadNetworkLoader.cs (new)
- CarKinem.Tests/Road/TestData/sample_road.json (new)
- CarKinem.Tests/Road/RoadNetworkBuilderTests.cs (new)
- CarKinem.Tests/Road/RoadNetworkLoaderTests.cs (new)
