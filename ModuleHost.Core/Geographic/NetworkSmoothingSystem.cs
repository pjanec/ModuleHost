using System;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Geographic
{
    [UpdateInPhase(SystemPhase.Input)]
    public class NetworkSmoothingSystem : IModuleSystem
    {
        private readonly IGeographicTransform _geo;
        
        public NetworkSmoothingSystem(IGeographicTransform geo)
        {
            _geo = geo;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Inbound: Geodetic → Physics (for remote entities)
            var inbound = view.Query()
                .With<Position>()
                .WithManaged<PositionGeodetic>()
                // .With<NetworkTarget>() // TODO: Re-enable when implementing full Dead Reckoning (BATCH-08.1)
                .With<NetworkOwnership>() // Manual ownership check
                .Build();
            
            foreach (var entity in inbound)
            {
                var ownership = view.GetComponentRO<NetworkOwnership>(entity);
                // Must be REMOTE (PrimaryOwner != LocalNode)
                if (ownership.PrimaryOwnerId == ownership.LocalNodeId)
                    continue;

                var geoPos = view.GetManagedComponentRO<PositionGeodetic>(entity);
                // var target = view.GetComponentRO<NetworkTarget>(entity); // Unused
                var currentPos = view.GetComponentRO<Position>(entity);
                
                // Convert latest geodetic to Cartesian target
                // Note: We might use NetworkTarget timestamp for actual DR, but instructions use simplistic approach here.
                var targetCartesian = _geo.ToCartesian(
                    geoPos.Latitude, 
                    geoPos.Longitude, 
                    geoPos.Altitude);
                
                // Smooth interpolation (dead reckoning)
                float t = Math.Clamp(deltaTime * 10.0f, 0f, 1f);
                Vector3 newPos = Vector3.Lerp(currentPos.Value, targetCartesian, t);
                
                if (view is EntityRepository repo)
                {
                    // Direct write optimization (main thread)
                    ref var pos = ref repo.GetComponentRW<Position>(entity);
                    pos.Value = newPos;
                }
                else
                {
                    // Fallback for strict view
                    var cmd = view.GetCommandBuffer();
                    cmd.SetComponent(entity, new Position { Value = newPos });
                }
            }
        }
    }
}
