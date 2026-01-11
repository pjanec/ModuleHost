using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Moq;

namespace ModuleHost.Core.Tests.Network
{
    public class EntityStateTranslatorTests : IDisposable
    {
        private EntityRepository _repo;
        private EntityStateTranslator _translator;
        
        public EntityStateTranslatorTests()
        {
            _repo = new EntityRepository();
            // Register components we use
            _repo.RegisterComponent<Position>();
            _repo.RegisterComponent<Velocity>();
            _repo.RegisterComponent<NetworkOwnership>();
            _repo.RegisterComponent<NetworkTarget>();
            _repo.RegisterComponent<NetworkIdentity>();
            _repo.RegisterComponent<DescriptorOwnership>();
            
            // Assume we are Node 1
            _translator = new EntityStateTranslator(1, new DescriptorOwnershipMap());
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }

        private ISimulationView View => _repo;
        
        [Fact]
        public void EntityStateTranslator_Ingress_CreatesEntity()
        {
            var mockReader = new MockDataReader(new EntityStateDescriptor
            {
                EntityId = 100,
                OwnerId = 2,
                Location = new Vector3(10, 20, 30),
                Velocity = new Vector3(1, 2, 3),
                Timestamp = 12345
            });
            
            var cmd = View.GetCommandBuffer();
            
            _translator.PollIngress(mockReader, cmd, View);
            
            // Playback command buffer to apply changes to repo
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify entity created
            var query = View.Query().With<Position>().IncludeAll().Build();
            var entities = new List<Entity>();
            foreach (var e in query) entities.Add(e);
            
            Assert.Single(entities);
            var entity = entities[0];
            
            Console.WriteLine($"Created entity {(long)entity.PackedValue}");
            
            var pos = View.GetComponentRO<Position>(entity);
            Assert.Equal(new Vector3(10, 20, 30), pos.Value);
            
            var vel = View.GetComponentRO<Velocity>(entity);
            Assert.Equal(new Vector3(1, 2, 3), vel.Value);
            
            var ownership = View.GetComponentRO<NetworkOwnership>(entity);
            Assert.Equal(2, ownership.PrimaryOwnerId);
            Assert.Equal(1, ownership.LocalNodeId); // We are Node 1
            // We shouldn't own it
            Assert.False(View.OwnsDescriptor(entity, 1)); // EntityState (ID 1)
        }
        
        [Fact]
        public void EntityStateTranslator_Ingress_IgnoresOwnedEntities()
        {
            // 1. Create local entity
            var cmd = View.GetCommandBuffer();
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = new Vector3(1, 2, 3) });
            _repo.AddComponent(entity, new Velocity { Value = Vector3.Zero });
            _repo.AddComponent(entity, new NetworkOwnership { PrimaryOwnerId = 1, LocalNodeId = 1 });
            
            // 2. Egress to establish mapping
            var mockWriter = new MockDataWriter();
            _translator.ScanAndPublish(View, mockWriter);
            
            // 3. Receive update for same network ID
            // Note: ScanAndPublish generates an ID. We need to grab it to make the update target the same entity.
            // But we don't have access to the internal map unless we shared it.
            // However, _translator manages its internal map.
            // If ScanAndPublish ran, ID is mapped.
            Assert.Single(mockWriter.WrittenSamples);
            var sentDesc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
            
            var mockReader = new MockDataReader(new EntityStateDescriptor
            {
                EntityId = sentDesc.EntityId,
                Location = new Vector3(999, 999, 999),
                Velocity = Vector3.Zero,
                Timestamp = 12346
            });
            
            _translator.PollIngress(mockReader, cmd, View);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // 4. Verify position NOT changed (because we own it)
            var pos = View.GetComponentRO<Position>(entity);
            Assert.Equal(new Vector3(1, 2, 3), pos.Value);
        }
        
        [Fact]
        public void EntityStateTranslator_Egress_PublishesOwnedOnly()
        {
            // Create owned entity
            var owned = _repo.CreateEntity();
            _repo.AddComponent(owned, new Position { Value = Vector3.One });
            _repo.AddComponent(owned, new Velocity { Value = Vector3.Zero });
            // We are Node 1, so OwnerId=1, LocalNodeId=1 -> Owned
            _repo.AddComponent(owned, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            
            // Create remote entity
            var remote = _repo.CreateEntity();
            _repo.AddComponent(remote, new Position { Value = Vector3.Zero });
            _repo.AddComponent(remote, new Velocity { Value = Vector3.Zero });
            // Remote: Owner=2, Local=1
            _repo.AddComponent(remote, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 2 });
            
            var mockWriter = new MockDataWriter();
            
            _translator.ScanAndPublish(View, mockWriter);
            
            // Only owned entity published
            Assert.Single(mockWriter.WrittenSamples);
            var desc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
            Assert.Equal(Vector3.One, desc.Location);
        }
        
        [Fact]
        public void EntityStateTranslator_RoundTrip_PreservesData()
        {
            // Ingress: Create entity from descriptor
            var originalDesc = new EntityStateDescriptor
            {
                EntityId = 100, // Remote ID
                OwnerId = 2,
                Location = new Vector3(10, 20, 30),
                Velocity = new Vector3(1, 2, 3),
                Timestamp = 12345
            };
            
            var mockReader = new MockDataReader(originalDesc);
            
            var cmd = View.GetCommandBuffer();
            _translator.PollIngress(mockReader, cmd, View);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Query the created entity
            var query = View.Query().With<Position>().IncludeAll().Build();
            Entity entity = Entity.Null;
            foreach(var e in query) entity = e;
            
            Assert.True(View.IsAlive(entity));
            
            // Simulate transfer of ownership: Make it ours
            _repo.SetComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            _repo.SetLifecycleState(entity, EntityLifecycle.Active);
            
            // Egress: Publish back
            var mockWriter = new MockDataWriter();
            
            _translator.ScanAndPublish(View, mockWriter);
            
            // Verify data preserved
            Assert.Single(mockWriter.WrittenSamples);
            var publishedDesc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
            
            Assert.Equal(originalDesc.Location, publishedDesc.Location);
            Assert.Equal(originalDesc.Velocity, publishedDesc.Velocity);
            Assert.Equal(100, publishedDesc.EntityId);
        }
    }
}
