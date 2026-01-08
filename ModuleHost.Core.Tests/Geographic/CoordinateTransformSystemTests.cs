using System;
using System.Numerics;
using System.Collections.Generic;
using Xunit;
using Moq;
using Fdp.Kernel;
using ModuleHost.Core.Geographic;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Tests.Geographic
{
    public class CoordinateTransformSystemTests : IDisposable
    {
        private readonly EntityRepository _repo;
        private readonly Mock<IGeographicTransform> _mockGeo;
        private readonly CoordinateTransformSystem _system;
        
        public CoordinateTransformSystemTests()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<Position>();
            _repo.RegisterComponent<NetworkOwnership>(); // For WithOwned
            _repo.RegisterComponent<PositionGeodetic>();
            
            _mockGeo = new Mock<IGeographicTransform>();
            _system = new CoordinateTransformSystem(_mockGeo.Object);
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }
        
        [Fact]
        public void Execute_LocallyOwnedPosition_UpdatesGeodetic()
        {
            // Setup entity
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = new Vector3(10, 0, 0) });
            _repo.AddComponent(entity, new PositionGeodetic { Latitude = 0, Longitude = 0, Altitude = 0 });
            
            // Owned by us
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            
            // Setup mock transform
            _mockGeo.Setup(g => g.ToGeodetic(It.IsAny<Vector3>()))
                .Returns((37, -122, 100));
                
            // Execute
            _system.Execute(_repo, 0.1f);
            
            // Playback commands
            var cmd = (IEntityCommandBuffer)((ISimulationView)_repo).GetCommandBuffer();
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify
            var geo = ((ISimulationView)_repo).GetManagedComponentRO<PositionGeodetic>(entity);
            Assert.Equal(37, geo.Latitude);
            Assert.Equal(-122, geo.Longitude);
            Assert.Equal(100, geo.Altitude);
        }
        
        [Fact]
        public void Execute_RemotePosition_DoesNotUpdateGeodetic()
        {
            // Setup entity
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = new Vector3(10, 0, 0) });
            _repo.AddComponent(entity, new PositionGeodetic { Latitude = 0, Longitude = 0, Altitude = 0 });
            
            // Owned by REMOTE (2)
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 2 });
            
            // Mock returns change (should be ignored)
            _mockGeo.Setup(g => g.ToGeodetic(It.IsAny<Vector3>()))
                .Returns((37, -122, 100));
                
            // Execute
            _system.Execute(_repo, 0.1f);
            
            // Playback
            var cmd = (IEntityCommandBuffer)((ISimulationView)_repo).GetCommandBuffer();
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify UNCHANGED
            var geo = ((ISimulationView)_repo).GetManagedComponentRO<PositionGeodetic>(entity);
            Assert.Equal(0, geo.Latitude);
            Assert.Equal(0, geo.Longitude);
            Assert.Equal(0, geo.Altitude);
        }
    }
}
