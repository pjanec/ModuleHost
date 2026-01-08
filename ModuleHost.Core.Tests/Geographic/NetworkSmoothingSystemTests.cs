using System;
using System.Numerics;
using Xunit;
using Moq;
using Fdp.Kernel;
using ModuleHost.Core.Geographic;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Tests.Geographic
{
    public class NetworkSmoothingSystemTests : IDisposable
    {
        private readonly EntityRepository _repo;
        private readonly Mock<IGeographicTransform> _mockGeo;
        private readonly NetworkSmoothingSystem _system;
        
        public NetworkSmoothingSystemTests()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<Position>();
            _repo.RegisterComponent<NetworkOwnership>();
            _repo.RegisterComponent<PositionGeodetic>();
            _repo.RegisterComponent<NetworkTarget>();
            
            _mockGeo = new Mock<IGeographicTransform>();
            _system = new NetworkSmoothingSystem(_mockGeo.Object);
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }
        
        [Fact]
        public void Execute_RemoteEntity_InterpolatesPosition()
        {
            // Setup entity at (0,0,0)
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.Zero });
            _repo.AddComponent(entity, new PositionGeodetic { Latitude = 10, Longitude = 10, Altitude = 100 });
            _repo.AddComponent(entity, new NetworkTarget());
            
            // Remote ownership (Local=1, Primary=2)
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 2 });
            
            // Mock transform returns (10,0,0) as target
            var targetPos = new Vector3(10, 0, 0);
            _mockGeo.Setup(g => g.ToCartesian(10, 10, 100))
                .Returns(targetPos);
                
            // Execute with dt=0.1
            // t = Clamp(0.1 * 10, 0, 1) = 1.0? 
            // Wait, instructions said t = deltaTime * 10.0f.
            // If dt=0.1, t=1.0. This means instant snap.
            // Let's use dt=0.05 -> t=0.5 -> Lerp 50%.
            
            _system.Execute(_repo, 0.05f);
            
            // Verify
            var pos = _repo.GetComponentRO<Position>(entity);
            // Lerp(0, 10, 0.5) = 5
            Assert.Equal(5.0f, pos.Value.X, 0.01f);
        }
        
        [Fact]
        public void Execute_LocalEntity_Ignored()
        {
            // Setup entity at (0,0,0)
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.Zero });
            _repo.AddComponent(entity, new PositionGeodetic { Latitude = 10, Longitude = 10, Altitude = 100 });
            _repo.AddComponent(entity, new NetworkTarget());
            
            // Local ownership (Local=1, Primary=1)
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            
            _mockGeo.Setup(g => g.ToCartesian(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
                .Returns(new Vector3(10, 0, 0));
                
            _system.Execute(_repo, 0.05f);
            
            // Verify UNCHANGED
            var pos = _repo.GetComponentRO<Position>(entity);
            Assert.Equal(0.0f, pos.Value.X);
        }

        [Theory]
        [InlineData(0.01f, 1.0f)]   // t=0.1 -> Lerp(0,10,0.1)=1
        [InlineData(0.05f, 5.0f)]   // t=0.5 -> Lerp(0,10,0.5)=5
        [InlineData(0.1f, 10.0f)]   // t=1.0 -> Lerp(0,10,1.0)=10
        [InlineData(0.2f, 10.0f)]   // t=2.0 -> clamped to 1.0 -> 10
        public void Smoothing_VariousDeltaTimes_InterpolatesCorrectly(float dt, float expectedX)
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.Zero });
            _repo.AddComponent(entity, new PositionGeodetic { Latitude = 10, Longitude = 10, Altitude = 100 });
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 2 });

            _mockGeo.Setup(g => g.ToCartesian(10, 10, 100))
                .Returns(new Vector3(10, 0, 0));

            _system.Execute(_repo, dt);

            var pos = _repo.GetComponentRO<Position>(entity);
            Assert.Equal(expectedX, pos.Value.X, 0.01f);
        }
    }
}
