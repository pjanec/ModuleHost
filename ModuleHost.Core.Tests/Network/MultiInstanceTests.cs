using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel.Tkb;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class MultiInstanceTests
    {
        private void RegisterStandardComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
            repo.RegisterComponent<NetworkOwnership>();
            repo.RegisterManagedComponent<DescriptorOwnership>();
            repo.RegisterManagedComponent<WeaponStates>();
            repo.RegisterEvent<ConstructionOrder>();
            repo.RegisterEvent<ConstructionAck>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
        }

        [Fact]
        public void DataSample_InstanceId_DefaultsToZero()
        {
            var sample = new DataSample();
            Assert.Equal(0, sample.InstanceId);
        }
        
        [Fact]
        public void WeaponStateTranslator_Ingress_MultipleInstances_StoresIndependently()
        {
            var map = new Dictionary<long, Entity>();
            var translator = new WeaponStateTranslator(1, map);
            var cmd = new MockCommandBuffer();
            var repo = new TestMockView(cmd);
            
            var entity = new Entity(1, 1);
            repo.ComponentArrays[entity] = new Dictionary<Type, Array>();
            map[100] = entity;
            
            var samples = new List<IDataSample>
            {
                new DataSample 
                { 
                    Data = new WeaponStateDescriptor { EntityId = 100, InstanceId = 0, AmmoCount = 10 },
                    InstanceId = 0
                },
                new DataSample 
                { 
                    Data = new WeaponStateDescriptor { EntityId = 100, InstanceId = 1, AmmoCount = 20 },
                    InstanceId = 1
                }
            };
            
            var reader = new MockDataReader(samples.ToArray());
            
            translator.PollIngress(reader, cmd, repo);
            
            Assert.Single(cmd.AddedManagedComponents);
            var ws = (WeaponStates)cmd.AddedManagedComponents[0].Item2;
            Assert.True(ws.Weapons.ContainsKey(0));
            Assert.True(ws.Weapons.ContainsKey(1));
            Assert.Equal(10, ws.Weapons[0].AmmoCount);
            Assert.Equal(20, ws.Weapons[1].AmmoCount);
        }
        
        [Fact]
        public void WeaponStateTranslator_Egress_OnlyPublishesOwnedInstances()
        {
            var map = new Dictionary<long, Entity>();
            var translator = new WeaponStateTranslator(1, map);
            
            using var repo = new EntityRepository();
            RegisterStandardComponents(repo);
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NetworkIdentity { Value = 100 });
            repo.SetLifecycleState(entity, EntityLifecycle.Active);
            
            var weaponStates = new WeaponStates();
            weaponStates.Weapons[0] = new WeaponState { AmmoCount = 10 };
            weaponStates.Weapons[1] = new WeaponState { AmmoCount = 20 };
            repo.AddComponent(entity, weaponStates);
            
            repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            var ownership = new DescriptorOwnership();
            ownership.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1)] = 2;
            repo.AddComponent(entity, ownership);
            
            var writer = new MockDataWriter();
            translator.ScanAndPublish(repo, writer);
            
            Assert.Single(writer.WrittenSamples);
            var desc = (WeaponStateDescriptor)writer.WrittenSamples[0];
            Assert.Equal(0, desc.InstanceId);
            Assert.Equal(10, desc.AmmoCount);
        }
        
        [Fact]
        public void NetworkSpawner_MultiTurretTank_DeterminesInstanceOwnership()
        {
            var elm = new EntityLifecycleModule(new[] { 1 });
            var strategy = new MockOwnershipStrategy();
            var tkb = new MockTkb();
            var spawner = new NetworkSpawnerSystem(tkb, elm, strategy, 1);
            
            using var repo = new EntityRepository();
            RegisterStandardComponents(repo);
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            var entity = repo.CreateEntity();
            repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
            
            var request = new NetworkSpawnRequest 
            { 
                DisType = new DISEntityType { Kind = 1, Category = 1 }, // Tank
                PrimaryOwnerId = 1,
                NetworkEntityId = 100
            };
            repo.AddComponent(entity, request);
            
            spawner.Execute(repo, 0);
            
            var ownership = ((ISimulationView)repo).GetManagedComponentRO<DescriptorOwnership>(entity);
            Assert.NotNull(ownership);
            
            long key1 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1);
            Assert.True(ownership.Map.ContainsKey(key1));
            Assert.Equal(2, ownership.Map[key1]);
            
            long key0 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0);
            Assert.False(ownership.Map.ContainsKey(key0));
        }
        
        [Fact]
        public void OwnershipExtensions_PackUnpackKey_WithNonZeroInstance()
        {
            long packed = OwnershipExtensions.PackKey(999, 5);
            var (typeId, instanceId) = OwnershipExtensions.UnpackKey(packed);
            
            Assert.Equal(999, typeId);
            Assert.Equal(5, instanceId);
        }
        
        [Fact]
        public void OwnershipExtensions_OwnsDescriptor_ChecksCompositeKey()
        {
            using var repo = new EntityRepository();
            RegisterStandardComponents(repo);
            var entity = repo.CreateEntity();
            
            repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            var ownership = new DescriptorOwnership();
            ownership.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 2)] = 2;
            repo.AddComponent(entity, ownership);
            
            Assert.True(((ISimulationView)repo).OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0));
            Assert.True(((ISimulationView)repo).OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1));
            Assert.False(((ISimulationView)repo).OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 2));
        }
        
        [Fact]
        public void WeaponStates_MultipleInstances_UpdateIndependently()
        {
            var states = new WeaponStates();
            states.Weapons[0] = new WeaponState { AmmoCount = 10 };
            states.Weapons[1] = new WeaponState { AmmoCount = 20 };
            
            states.Weapons[1] = new WeaponState { AmmoCount = 19 };
            
            Assert.Equal(10, states.Weapons[0].AmmoCount);
            Assert.Equal(19, states.Weapons[1].AmmoCount);
        }
        
        [Fact]
        public void MultiInstance_OwnershipTransfer_UpdatesSpecificInstance()
        {
            var ownership = new DescriptorOwnership();
            long key0 = OwnershipExtensions.PackKey(1, 0);
            long key1 = OwnershipExtensions.PackKey(1, 1);
            
            ownership.Map[key0] = 1;
            ownership.Map[key1] = 1;
            
            ownership.Map[key1] = 2;
            
            Assert.Equal(1, ownership.Map[key0]);
            Assert.Equal(2, ownership.Map[key1]);
        }
        
        private class MockOwnershipStrategy : IOwnershipDistributionStrategy
        {
            public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType, int masterNodeId, long instanceId)
            {
                if (descriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID && instanceId == 1)
                    return 2;
                return null;
            }
        }
        
        private class MockTkb : ITkbDatabase
        {
            public TkbTemplate GetTemplateByEntityType(DISEntityType entityType) => new TkbTemplate("TestTemplate");
            public TkbTemplate GetTemplateByName(string templateName) => new TkbTemplate("TestTemplate");
        }
    }
}
