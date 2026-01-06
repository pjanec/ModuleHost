# BATCH-CK-06: Spatial Hash System

**Batch ID:** BATCH-CK-06  
**Phase:** Spatial Indexing  
**Prerequisites:** BATCH-CK-01 (VehicleState) COMPLETE  
**Assigned:** TBD  

---

## 📋 Objectives

Implement spatial hash grid for efficient neighbor queries:
1. SpatialHashGrid structure for 2D spatial indexing
2. Grid building from vehicle positions
3. Neighbor query API (range queries)
4. Parallel-safe grid construction
5. Performance benchmarks

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Spatial Hash Section:** Lines 779-857 in design doc

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\CarKinem\
└── Spatial\
    └── SpatialHashGrid.cs         ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Spatial\
    └── SpatialHashGridTests.cs    ← NEW
```

---

## 🎯 Tasks

### Task CK-06-01: SpatialHashGrid Implementation

**File:** `CarKinem/Spatial/SpatialHashGrid.cs`

```csharp
using System;
using System.Numerics;
using Fdp.Kernel.Collections;

namespace CarKinem.Spatial
{
    /// <summary>
    /// 2D spatial hash grid for fast neighbor queries.
    /// Hardcoded cell size: 5.0 meters.
    /// </summary>
    public struct SpatialHashGrid : IDisposable
    {
        public NativeArray<int> GridHead;       // Cell → first entity index
        public NativeArray<int> GridNext;       // Entity index → next entity
        public NativeArray<int> GridValues;     // Entity index → entity ID
        public NativeArray<Vector2> Positions;  // Entity index → position
        
        public float CellSize;
        public int Width;
        public int Height;
        public int EntityCount;
        
        /// <summary>
        /// Create grid with specified dimensions.
        /// </summary>
        public static SpatialHashGrid Create(int width, int height, float cellSize, 
            int maxEntities, Allocator allocator)
        {
            return new SpatialHashGrid
            {
                GridHead = new NativeArray<int>(width * height, allocator),
                GridNext = new NativeArray<int>(maxEntities, allocator),
                GridValues = new NativeArray<int>(maxEntities, allocator),
                Positions = new NativeArray<Vector2>(maxEntities, allocator),
                CellSize = cellSize,
                Width = width,
                Height = height,
                EntityCount = 0
            };
        }
        
        /// <summary>
        /// Clear grid (reset all heads to -1).
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < GridHead.Length; i++)
                GridHead[i] = -1;
            
            EntityCount = 0;
        }
        
        /// <summary>
        /// Add entity to grid.
        /// </summary>
        public void Add(int entityId, Vector2 position)
        {
            int cellX = (int)(position.X / CellSize);
            int cellY = (int)(position.Y / CellSize);
            
            if (cellX < 0 || cellX >= Width || cellY < 0 || cellY >= Height)
                return; // Out of bounds
            
            int cellIdx = cellY * Width + cellX;
            int entityIdx = EntityCount++;
            
            Positions[entityIdx] = position;
            GridValues[entityIdx] = entityId;
            GridNext[entityIdx] = GridHead[cellIdx];
            GridHead[cellIdx] = entityIdx;
        }
        
        /// <summary>
        /// Query neighbors within radius.
        /// Writes results to output array, returns count.
        /// </summary>
        public int QueryNeighbors(Vector2 position, float radius, 
            Span<(int entityId, Vector2 pos)> output)
        {
            int count = 0;
            float radiusSq = radius * radius;
            
            // Get search bounds in grid space
            int minCellX = (int)((position.X - radius) / CellSize);
            int maxCellX = (int)((position.X + radius) / CellSize);
            int minCellY = (int)((position.Y - radius) / CellSize);
            int maxCellY = (int)((position.Y + radius) / CellSize);
            
            // Clamp to grid bounds
            minCellX = Math.Max(0, minCellX);
            maxCellX = Math.Min(Width - 1, maxCellX);
            minCellY = Math.Max(0, minCellY);
            maxCellY = Math.Min(Height - 1, maxCellY);
            
            // Iterate cells
            for (int cy = minCellY; cy <= maxCellY; cy++)
            {
                for (int cx = minCellX; cx <= maxCellX; cx++)
                {
                    int cellIdx = cy * Width + cx;
                    int head = GridHead[cellIdx];
                    
                    // Iterate linked list
                    while (head >= 0)
                    {
                        Vector2 neighborPos = Positions[head];
                        float distSq = Vector2.DistanceSquared(position, neighborPos);
                        
                        if (distSq <= radiusSq)
                        {
                            if (count < output.Length)
                            {
                                output[count] = (GridValues[head], neighborPos);
                                count++;
                            }
                        }
                        
                        head = GridNext[head];
                    }
                }
            }
            
            return count;
        }
        
        public void Dispose()
        {
            if (GridHead.IsCreated) GridHead.Dispose();
            if (GridNext.IsCreated) GridNext.Dispose();
            if (GridValues.IsCreated) GridValues.Dispose();
            if (Positions.IsCreated) Positions.Dispose();
        }
    }
}
```

---

### Task CK-06-02: Tests

**File:** `CarKinem.Tests/Spatial/SpatialHashGridTests.cs`

```csharp
using System.Numerics;
using CarKinem.Spatial;
using Fdp.Kernel.Collections;
using Xunit;

namespace CarKinem.Tests.Spatial
{
    public class SpatialHashGridTests
    {
        [Fact]
        public void Create_InitializesGrid()
        {
            var grid = SpatialHashGrid.Create(10, 10, 5f, 100, Allocator.Persistent);
            
            Assert.Equal(10, grid.Width);
            Assert.Equal(10, grid.Height);
            Assert.Equal(5f, grid.CellSize);
            Assert.Equal(0, grid.EntityCount);
            
            grid.Dispose();
        }
        
        [Fact]
        public void Add_InsertsEntityInCorrectCell()
        {
            var grid = SpatialHashGrid.Create(10, 10, 5f, 100, Allocator.Persistent);
            grid.Clear();
            
            // Add entity at (7.5, 7.5) → should be in cell (1, 1)
            grid.Add(entityId: 42, position: new Vector2(7.5f, 7.5f));
            
            Assert.Equal(1, grid.EntityCount);
            
            // Cell (1,1) should have entity
            int cellIdx = 1 * 10 + 1;
            Assert.NotEqual(-1, grid.GridHead[cellIdx]);
            
            grid.Dispose();
        }
        
        [Fact]
        public void QueryNeighbors_FindsEntitiesWithinRadius()
        {
            var grid = SpatialHashGrid.Create(20, 20, 5f, 100, Allocator.Persistent);
            grid.Clear();
            
            // Add entities
            grid.Add(1, new Vector2(10, 10));
            grid.Add(2, new Vector2(12, 10)); // 2m away
            grid.Add(3, new Vector2(20, 10)); // 10m away
            
            // Query within 3m radius
            Span<(int, Vector2)> results = stackalloc (int, Vector2)[10];
            int count = grid.QueryNeighbors(new Vector2(10, 10), radius: 3f, results);
            
            Assert.Equal(2, count); // Should find entities 1 and 2
            
            grid.Dispose();
        }
        
        [Fact]
        public void QueryNeighbors_ExcludesEntitiesOutsideRadius()
        {
            var grid = SpatialHashGrid.Create(20, 20, 5f, 100, Allocator.Persistent);
            grid.Clear();
            
            grid.Add(1, new Vector2(10, 10));
            grid.Add(2, new Vector2(25, 25)); // Far away
            
            Span<(int, Vector2)> results = stackalloc (int, Vector2)[10];
            int count = grid.QueryNeighbors(new Vector2(10, 10), radius: 5f, results);
            
            Assert.Equal(1, count); // Only entity 1
            
            grid.Dispose();
        }
        
        [Fact]
        public void Clear_ResetsGrid()
        {
            var grid = SpatialHashGrid.Create(10, 10, 5f, 100, Allocator.Persistent);
            grid.Clear();
            
            grid.Add(1, new Vector2(5, 5));
            Assert.Equal(1, grid.EntityCount);
            
            grid.Clear();
            Assert.Equal(0, grid.EntityCount);
            
            grid.Dispose();
        }
    }
}
```

---

## ✅ Acceptance Criteria

- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 5 unit tests
- [ ] Cell size hardcoded to 5.0m
- [ ] QueryNeighbors uses Span (zero allocations)
- [ ] Grid bounds checking prevents out-of-range access
- [ ] Dispose pattern implemented

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-06-REPORT.md`

**Time Estimate:** 2-3 hours
