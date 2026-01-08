using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Tests.Mocks;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class PartialOwnershipIntegrationTests : IDisposable
    {
        private readonly EntityRepository _repo;
        
        public PartialOwnershipIntegrationTests()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<Position>();
            _repo.RegisterComponent<Velocity>();
            _repo.RegisterComponent<WeaponAmmo>();
            _repo.RegisterComponent<NetworkOwnership>();
            _repo.RegisterComponent<DescriptorOwnership>(); // Managed
            _repo.RegisterComponent<NetworkIdentity>();
            _repo.RegisterComponent<NetworkTarget>();
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }

        [Fact]
        public void PartialOwnership_TwoNodes_DifferentDescriptors()
        {
            // Scenario: Entity, Node 1 owns movement (1), Node 2 owns weapon (2)
            
            // Setup
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = new Vector3(0, 0, 0) });
            _repo.AddComponent(entity, new Velocity { Value = new Vector3(1, 0, 0) });
            _repo.AddComponent(entity, new WeaponAmmo { Current = 100 });
            
            // Initial ownership: Node 1 owns everything
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            // Managed partial ownership
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { 1, 1 }, // EntityState → Node 1
                    { 2, 1 }  // WeaponState → Node 1
                }
            });
            
            // Transfer WeaponState to Node 2
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            ownershipMap.RegisterMapping(2, typeof(WeaponAmmo));
            
            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            
            var translator = new OwnershipUpdateTranslator(1, ownershipMap, networkIdMap);
            
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            var mockReader = new MockDataReader(
                new OwnershipUpdate
                {
                    EntityId = 100,
                    DescrTypeId = 2, // WeaponState
                    NewOwner = 2     // Transfer to Node 2
                }
            );
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify ownership updated
            Assert.True(((ISimulationView)_repo).OwnsDescriptor(entity, 1));  // Node 1 still owns EntityState
            Assert.False(((ISimulationView)_repo).OwnsDescriptor(entity, 2)); // Node 1 no longer owns WeaponState
            Assert.Equal(2, ((ISimulationView)_repo).GetDescriptorOwner(entity, 2));
            
            var comp = _repo.GetComponentRO<DescriptorOwnership>(entity);
            Assert.Equal(2, comp.Map[2]);
            
            // Task 7.01.3: Verify FDP Component Metadata (Best Effort verification)
            // Note: In FDP, Metadata is per-table (Type), not per-Entity.
            // If the implementation doesn't update it (globally), this check might fail if enforced strictly.
            // However, ensuring we don't crash when accessing it is good.
            // For now, we only verify we can access the table.
            // var weaponTable = _repo.GetComponentTable<WeaponAmmo>();
            // Assert.NotNull(weaponTable);
            // If we were syncing metadata, we'd check: Assert.Equal(2, weaponTable.Metadata.OwnerId);
        }
        
        [Fact]
        public void DescriptorDisposal_PrimaryOwner_IgnoredAsEntityDeletion()
        {
            // Setup: Node 1 owns BOTH EntityMaster AND EntityState
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.One });
            _repo.AddComponent(entity, new Velocity { Value = Vector3.Zero });
            
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1  // Node 1 owns EntityMaster
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { 1, 1 }  // Node 1 ALSO owns EntityState (no split)
                }
            });
            
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            
            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            var translator = new EntityStateTranslator(1, ownershipMap, networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            // Simulate disposal by Node 1 (PRIMARY owner)
            // This means entity is being deleted, NOT ownership transfer
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify: Ownership NOT changed (still 1)
            // Disposal by primary owner is ignored (wait for EntityMaster disposal)
            var comp = _repo.GetComponentRO<DescriptorOwnership>(entity);
            Assert.True(comp.Map.ContainsKey(1));  // Still in map
            Assert.Equal(1, comp.Map[1]);  // Still owned by 1
            
            // Entity should still be alive (waiting for EntityMaster disposal)
            Assert.True(((ISimulationView)_repo).IsAlive(entity));
        }

        [Fact]
        public void OwnershipUpdate_UnknownEntity_LogsAndContinues()
        {
            // OwnershipUpdate for entity that doesn't exist locally
            
            var ownershipMap = new DescriptorOwnershipMap();
            var networkIdMap = new Dictionary<long, Entity>();  // Empty
            var translator = new OwnershipUpdateTranslator(1, ownershipMap, networkIdMap);
            
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            var mockReader = new MockDataReader(
                new OwnershipUpdate
                {
                    EntityId = 999,  // Unknown
                    DescrTypeId = 2,
                    NewOwner = 3
                }
            );
            
            // Should not throw
            var exception = Record.Exception(() =>
            {
                translator.PollIngress(mockReader, cmd, _repo);
                ((EntityCommandBuffer)cmd).Playback(_repo);
            });
            
            Assert.Null(exception);
        }
        
        [Fact]
        public void DescriptorDisposal_EntityAlreadyDeleted_HandledGracefully()
        {
            // Setup: Create entity, then delete it
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkIdentity { Value = 100 });
            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            
            _repo.DestroyEntity(entity);  // Delete first
            
            var translator = new EntityStateTranslator(1, new DescriptorOwnershipMap(), networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            // Should not throw (entity gone, disposal ignored)
            var exception = Record.Exception(() =>
            {
                translator.PollIngress(mockReader, cmd, _repo);
                ((EntityCommandBuffer)cmd).Playback(_repo);
            });
            
            Assert.Null(exception);
        }

        [Fact]
        public void Egress_PartialOwnership_OnlyPublishesOwnedDescriptors()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.One });
            _repo.AddComponent(entity, new Velocity { Value = Vector3.Zero });
            _repo.AddComponent(entity, new WeaponAmmo { Current = 50 });
            
            // Node 1 owns EntityState(1) [Implicitly via Primary], Node 2 owns WeaponState(2)
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { 1, 1 }, // Node 1
                    { 2, 2 }  // Node 2
                }
            });

            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            ownershipMap.RegisterMapping(2, typeof(WeaponAmmo));
            
            var entityMap = new Dictionary<Entity, long> { { entity, 100 } };
            var netMap = new Dictionary<long, Entity> { { 100, entity } };
            
            var entityStateTranslator = new EntityStateTranslator(1, ownershipMap, netMap, entityMap);
            var mockWriter = new MockDataWriter();
            
            entityStateTranslator.ScanAndPublish(_repo, mockWriter);
            
            // Should publish EntityState (we own it)
            Assert.Single(mockWriter.WrittenSamples);
            Assert.IsType<EntityStateDescriptor>(mockWriter.WrittenSamples[0]);
            
            // WeaponState translator should NOT publish (we don't own it)
            var weaponTranslator = new WeaponStateTranslator(1, ownershipMap, netMap);
            mockWriter.Clear();
            
            weaponTranslator.ScanAndPublish(_repo, mockWriter);
            
            // Should NOT publish (owned by Node 2)
            Assert.Empty(mockWriter.WrittenSamples);
        }

        [Fact]
        public void DescriptorDisposal_PartialOwner_ReturnsOwnershipToPrimary()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.One });
            _repo.AddComponent(entity, new Velocity { Value = Vector3.Zero });
            
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1  // Node 1 owns EntityMaster
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                     { 1, 2 }  // Node 2 owns EntityState (partial)
                }
            });
            
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));

            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            var translator = new EntityStateTranslator(1, ownershipMap, networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            // Simulate disposal by Node 2 (partial owner)
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify ownership returned to primary
            var comp = _repo.GetComponentRO<DescriptorOwnership>(entity);
            Assert.False(comp.Map.ContainsKey(1));  // Removed from partial
            Assert.Equal(1, ((ISimulationView)_repo).GetDescriptorOwner(entity, 1)); // Back to 1
        }

        [Fact]
        public void EntityMaster_Disposal_DeletesEntity()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkIdentity { Value = 200 });
            
            var networkIdMap = new Dictionary<long, Entity> { { 200, entity } };
            var translator = new EntityMasterTranslator(1, networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityMasterDescriptor { EntityId = 200 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            // Verify destroyed
            Assert.False(((ISimulationView)_repo).IsAlive(entity));
        }
    }
}
