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
            _repo.RegisterManagedComponent<WeaponStates>();
            _repo.RegisterComponent<NetworkOwnership>();
            _repo.RegisterComponent<DescriptorOwnership>(DataPolicy.Transient); 
            _repo.RegisterComponent<NetworkIdentity>();
            _repo.RegisterComponent<NetworkTarget>();
            _repo.RegisterComponent<ForceNetworkPublish>();
            _repo.RegisterEvent<DescriptorAuthorityChanged>();
        }
        
        public void Dispose()
        {
            _repo.Dispose();
        }

        [Fact]
        public void PartialOwnership_TwoNodes_DifferentDescriptors()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = new Vector3(0, 0, 0) });
            _repo.AddComponent(entity, new Velocity { Value = new Vector3(1, 0, 0) });
            
            var weapons = new WeaponStates();
            weapons.Weapons[0] = new WeaponState { AmmoCount = 100 };
            _repo.AddComponent(entity, weapons);
            
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { OwnershipExtensions.PackKey(1, 0), 1 }, // EntityState → Node 1
                    { OwnershipExtensions.PackKey(2, 0), 1 }  // WeaponState → Node 1
                }
            });
            
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            ownershipMap.RegisterMapping(2, typeof(WeaponStates));
            
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
            
            Assert.True(((ISimulationView)_repo).OwnsDescriptor(entity, 1));
            Assert.False(((ISimulationView)_repo).OwnsDescriptor(entity, 2));
            Assert.Equal(2, ((ISimulationView)_repo).GetDescriptorOwner(entity, 2));
            
            var comp = ((ISimulationView)_repo).GetManagedComponentRO<DescriptorOwnership>(entity);
            Assert.Equal(2, comp.Map[OwnershipExtensions.PackKey(2, 0)]);
        }
        
        [Fact]
        public void DescriptorDisposal_PrimaryOwner_IgnoredAsEntityDeletion()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new Position { Value = Vector3.One });
            _repo.AddComponent(entity, new Velocity { Value = Vector3.Zero });
            
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { OwnershipExtensions.PackKey(1, 0), 1 }
                }
            });
            
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            
            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            var translator = new EntityStateTranslator(1, ownershipMap, networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            var comp = ((ISimulationView)_repo).GetManagedComponentRO<DescriptorOwnership>(entity);
            Assert.True(comp.Map.ContainsKey(OwnershipExtensions.PackKey(1, 0)));
            Assert.Equal(1, comp.Map[OwnershipExtensions.PackKey(1, 0)]);
            
            Assert.True(((ISimulationView)_repo).IsAlive(entity));
        }

        [Fact]
        public void OwnershipUpdate_UnknownEntity_LogsAndContinues()
        {
            var ownershipMap = new DescriptorOwnershipMap();
            var networkIdMap = new Dictionary<long, Entity>();
            var translator = new OwnershipUpdateTranslator(1, ownershipMap, networkIdMap);
            
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            var mockReader = new MockDataReader(
                new OwnershipUpdate
                {
                    EntityId = 999,
                    DescrTypeId = 2,
                    NewOwner = 3
                }
            );
            
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
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkIdentity { Value = 100 });
            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            
            _repo.DestroyEntity(entity);
            
            var translator = new EntityStateTranslator(1, new DescriptorOwnershipMap(), networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
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
            
            var weapons = new WeaponStates();
            weapons.Weapons[0] = new WeaponState { AmmoCount = 50 };
            _repo.AddComponent(entity, weapons);
            
            _repo.AddComponent(entity, new NetworkOwnership
            {
                LocalNodeId = 1,
                PrimaryOwnerId = 1
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                    { OwnershipExtensions.PackKey(1, 0), 1 },
                    { OwnershipExtensions.PackKey(2, 0), 2 }
                }
            });

            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));
            ownershipMap.RegisterMapping(2, typeof(WeaponStates));
            
            var entityMap = new Dictionary<Entity, long> { { entity, 100 } };
            var netMap = new Dictionary<long, Entity> { { 100, entity } };
            
            var entityStateTranslator = new EntityStateTranslator(1, ownershipMap, netMap, entityMap);
            var mockWriter = new MockDataWriter();
            
            entityStateTranslator.ScanAndPublish(_repo, mockWriter);
            
            Assert.Single(mockWriter.WrittenSamples);
            Assert.IsType<EntityStateDescriptor>(mockWriter.WrittenSamples[0]);
            
            var weaponTranslator = new WeaponStateTranslator(1, netMap);
            mockWriter.Clear();
            
            weaponTranslator.ScanAndPublish(_repo, mockWriter);
            
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
                PrimaryOwnerId = 1
            });
            
            _repo.AddComponent(entity, new DescriptorOwnership
            {
                Map = new Dictionary<long, int>
                {
                     { OwnershipExtensions.PackKey(1, 0), 2 }
                }
            });
            
            var ownershipMap = new DescriptorOwnershipMap();
            ownershipMap.RegisterMapping(1, typeof(Position), typeof(Velocity));

            var networkIdMap = new Dictionary<long, Entity> { { 100, entity } };
            var translator = new EntityStateTranslator(1, ownershipMap, networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityStateDescriptor { EntityId = 100 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            var comp = ((ISimulationView)_repo).GetManagedComponentRO<DescriptorOwnership>(entity);
            Assert.False(comp.Map.ContainsKey(OwnershipExtensions.PackKey(1, 0)));
            Assert.Equal(1, ((ISimulationView)_repo).GetDescriptorOwner(entity, 1));
        }

        [Fact]
        public void EntityMaster_Disposal_DeletesEntity()
        {
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkIdentity { Value = 200 });
            
            var networkIdMap = new Dictionary<long, Entity> { { 200, entity } };
            var translator = new EntityMasterTranslator(1, networkIdToEntity: networkIdMap);
            var cmd = ((ISimulationView)_repo).GetCommandBuffer();
            
            var mockReader = new MockDataReader(new MockDataSample
            {
                Data = new EntityMasterDescriptor { EntityId = 200 },
                InstanceState = DdsInstanceState.NotAliveDisposed
            });
            
            translator.PollIngress(mockReader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);
            
            Assert.False(((ISimulationView)_repo).IsAlive(entity));
        }
    }
}
