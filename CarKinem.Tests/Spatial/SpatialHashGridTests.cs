using System;
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
            
            // Add entity at (7.5, 7.5) -> should be in cell (1, 1)
            // CellX = 7.5 / 5 = 1
            // CellY = 7.5 / 5 = 1
            // CellIdx = 1 * 10 + 1 = 11
            grid.Add(entityId: 42, position: new Vector2(7.5f, 7.5f));
            
            Assert.Equal(1, grid.EntityCount);
            
            // Cell (1,1) should have entity
            int cellIdx = 11;
            Assert.NotEqual(-1, grid.GridHead[cellIdx]);
            
            // Verify content
            int entityIdx = grid.GridHead[cellIdx];
            Assert.Equal(42, grid.GridValues[entityIdx]);
            
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
            Span<(int id, Vector2 pos)> results = stackalloc (int, Vector2)[10];
            int count = grid.QueryNeighbors(new Vector2(10, 10), radius: 3f, results);
            
            Assert.Equal(2, count); // Should find entities 1 and 2
            
            // Verify results contain expected IDs
            bool found1 = false;
            bool found2 = false;
            for(int i=0; i<count; i++)
            {
                if (results[i].id == 1) found1 = true;
                if (results[i].id == 2) found2 = true;
            }
            Assert.True(found1, "Should find entity 1");
            Assert.True(found2, "Should find entity 2");
            
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
            
            // Check grid head is reset
            int cellIdx = (int)(5/5)*10 + (int)(5/5);
            Assert.Equal(-1, grid.GridHead[cellIdx]);
            
            grid.Dispose();
        }
    }
}
