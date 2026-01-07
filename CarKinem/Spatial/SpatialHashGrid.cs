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
        public NativeArray<int> GridHead;       // Cell -> first entity index
        public NativeArray<int> GridNext;       // Entity index -> next entity
        public NativeArray<int> GridValues;     // Entity index -> entity ID
        public NativeArray<Vector2> Positions;  // Entity index -> position
        
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
            
            if (entityIdx >= GridValues.Length)
                return; // Exceeded max entities capacity
            
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
