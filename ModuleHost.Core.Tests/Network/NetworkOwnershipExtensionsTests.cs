using System;
using System.Collections.Generic;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkOwnershipExtensionsTests : IDisposable
    {
        private readonly EntityRepository _repo;
        
        public NetworkOwnershipExtensionsTests()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<NetworkOwnership>();
            _repo.RegisterComponent<DescriptorOwnership>();
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }
        
        [Fact]
        public void OwnsDescriptor_NoMap_FallsToPrimaryOwner()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            // No DescriptorOwnership component
            
            // Should fall back to PrimaryOwnerId
            Assert.True(((ISimulationView)_repo).OwnsDescriptor(entity, 1));
            Assert.True(((ISimulationView)_repo).OwnsDescriptor(entity, 2)); // Any descriptor
        }
        
        [Fact]
        public void OwnsDescriptor_MapExists_ChecksMapFirst()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { 2, 2 }  // Descriptor 2 owned by Node 2
                }
            });
            
            // Descriptor 1: Not in map → fallback to primary (1)
            Assert.True(((ISimulationView)_repo).OwnsDescriptor(entity, 1));
            
            // Descriptor 2: In map, owned by 2 → we (Node 1) don't own
            Assert.False(((ISimulationView)_repo).OwnsDescriptor(entity, 2));
        }
        
        [Fact]
        public void GetDescriptorOwner_NoMap_ReturnsPrimary()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            var owner = ((ISimulationView)_repo).GetDescriptorOwner(entity, 999);
            
            Assert.Equal(1, owner);
        }
        
        [Fact]
        public void GetDescriptorOwner_MapExists_ReturnsMapValue()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { 2, 3 }  // Descriptor 2 owned by Node 3
                }
            });
            
            var owner = ((ISimulationView)_repo).GetDescriptorOwner(entity, 2);
            
            Assert.Equal(3, owner);
        }
    }
}
