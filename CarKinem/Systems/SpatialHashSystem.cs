using System.Linq;
using CarKinem.Core;
using CarKinem.Spatial;
using Fdp.Kernel;
using Fdp.Kernel.Collections;

namespace CarKinem.Systems
{
    /// <summary>
    /// Builds spatial hash grid from vehicle positions each frame.
    /// Publishes grid as singleton component.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public class SpatialHashSystem : ComponentSystem
    {
        private SpatialHashGrid _grid;
        
        protected override void OnCreate()
        {
            // Hardcoded: 200x200 meter world, 5m cells = 40x40 grid
            // Used a sufficiently large entity capacity to avoid reallocation for now
            _grid = SpatialHashGrid.Create(40, 40, 5.0f, 100000, Allocator.Persistent);
        }
        
        protected override void OnUpdate()
        {
            _grid.Clear();
            
            // Query all vehicles
            var query = World.Query().With<VehicleState>().Build();
            
            foreach (var entity in query)
            {
                var state = World.GetComponent<VehicleState>(entity);
                _grid.Add(entity.Index, state.Position);
            }
            
            // Publish as singleton (Data-Oriented pattern)
            World.SetSingleton(new SpatialGridData { Grid = _grid });
        }
        
        protected override void OnDestroy()
        {
            _grid.Dispose();
        }
    }
}
