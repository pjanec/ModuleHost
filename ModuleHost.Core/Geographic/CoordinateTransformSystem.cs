using System;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network; // For Position

namespace ModuleHost.Core.Geographic
{
    [UpdateInPhase(SystemPhase.PostSimulation)]
    public class CoordinateTransformSystem : IModuleSystem
    {
        private readonly IGeographicTransform _geo;
        
        public CoordinateTransformSystem(IGeographicTransform geo)
        {
            _geo = geo;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            var cmd = view.GetCommandBuffer();
            
            // Outbound: Physics → Geodetic (for locally owned entities)
            // Using WithManaged since PositionGeodetic is a class
            var outbound = view.Query()
                .With<Position>()
                .With<NetworkOwnership>() // explicit check relying on our component
                .WithManaged<PositionGeodetic>()
                .Build();
            
            foreach (var entity in outbound)
            {
                var ownership = view.GetComponentRO<NetworkOwnership>(entity);
                // Simple authority check: Primary Owner = Local Node
                // Improvements: Could use OwningDescriptor check if we knew the DescriptorID for Position
                if (ownership.PrimaryOwnerId != ownership.LocalNodeId)
                    continue;

                var localPos = view.GetComponentRO<Position>(entity);
                var geoPos = view.GetManagedComponentRO<PositionGeodetic>(entity);
                
                var (lat, lon, alt) = _geo.ToGeodetic(localPos.Value);
                
                // Only update if changed significantly
                if (Math.Abs(geoPos.Latitude - lat) > 1e-6 ||
                    Math.Abs(geoPos.Longitude - lon) > 1e-6 ||
                    Math.Abs(geoPos.Altitude - alt) > 0.1)
                {
                    var newGeo = new PositionGeodetic
                    {
                        Latitude = lat,
                        Longitude = lon,
                        Altitude = alt
                    };
                    cmd.SetManagedComponent(entity, newGeo);
                }
            }
        }
    }
}
