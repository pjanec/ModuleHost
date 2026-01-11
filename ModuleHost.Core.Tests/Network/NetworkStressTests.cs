using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Tests.Mocks;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Abstractions;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkStressTests
    {
        [Fact]
        public void Stress_1000Entities_MasterFirstCreation()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityMasterTranslator(1, networkIdToEntity);
            
            var samples = new List<IDataSample>();
            for (int i = 0; i < 1000; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityMasterDescriptor
                    {
                        EntityId = i,
                        OwnerId = 1,
                        Type = new DISEntityType { Kind = 1 },
                        Name = $"Entity_{i}"
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            var startTime = DateTime.UtcNow;
            translator.PollIngress(reader, cmd, repo);
            ((EntityCommandBuffer)cmd).Playback(repo);
            var duration = DateTime.UtcNow - startTime;
            
            Assert.Equal(1000, networkIdToEntity.Count);
            Assert.True(duration.TotalMilliseconds < 2000, $"Creation took {duration.TotalMilliseconds}ms (expected <2000ms)");
        }
        
        [Fact]
        public void Stress_1000Entities_GhostPromotion()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var entities = new List<Entity>();
            for (int i = 0; i < 1000; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkIdentity { Value = i });
                repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
                entities.Add(entity);
            }
            
            var startTime = DateTime.UtcNow;
            foreach (var entity in entities)
            {
                repo.SetLifecycleState(entity, EntityLifecycle.Constructing);
            }
            var duration = DateTime.UtcNow - startTime;
            
            foreach (var entity in entities)
            {
                Assert.Equal(EntityLifecycle.Constructing, repo.GetHeader(entity.Index).LifecycleState);
            }
            
            Assert.True(duration.TotalMilliseconds < 500, $"Promotion took {duration.TotalMilliseconds}ms (expected <500ms)");
        }
        
        [Fact]
        public void Stress_ConcurrentOwnershipUpdates_1000Entities()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            
            for (int i = 0; i < 1000; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkIdentity { Value = i });
                repo.SetLifecycleState(entity, EntityLifecycle.Active);
                repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
                
                var ownership = new DescriptorOwnership();
                ownership.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)] = 1;
                repo.AddComponent(entity, ownership);
                
                networkIdToEntity[i] = entity;
            }
            
            var descriptorMap = new DescriptorOwnershipMap();
            var translator = new OwnershipUpdateTranslator(2, descriptorMap, networkIdToEntity);
            var samples = new List<IDataSample>();
            
            for (int i = 0; i < 1000; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new OwnershipUpdate
                    {
                        EntityId = i,
                        DescrTypeId = NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID,
                        InstanceId = 0,
                        NewOwner = 2,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            var startTime = DateTime.UtcNow;
            translator.PollIngress(reader, cmd, repo);
            ((EntityCommandBuffer)cmd).Playback(repo);
            var duration = DateTime.UtcNow - startTime;
            
            int updatedCount = 0;
            foreach (var kvp in networkIdToEntity)
            {
                var entity = kvp.Value;
                var ownership = ((ISimulationView)repo).GetManagedComponentRO<DescriptorOwnership>(entity);
                long key = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0);
                
                if (ownership.Map[key] == 2)
                    updatedCount++;
            }
            
            Assert.Equal(1000, updatedCount);
            Assert.True(duration.TotalMilliseconds < 2000, $"Updates took {duration.TotalMilliseconds}ms (expected <2000ms)");
        }
        
        [Fact]
        public void Stress_ReliableInit_100EntitiesWithTimeout()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var topo = new StaticNetworkTopology(1, new[] { 1, 2, 3 });
            var elm = new EntityLifecycleModule(new[] { 10 });
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            
            var entities = new List<Entity>();
            for (int i = 0; i < 100; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType { Kind = 1 } });
                repo.AddComponent(entity, new PendingNetworkAck());
                entities.Add(entity);
                
                var cmd = ((ISimulationView)repo).GetCommandBuffer();
                elm.BeginConstruction(entity, 1, repo.GlobalVersion, cmd);
                ((EntityCommandBuffer)cmd).Playback(repo);
            }
            
            gateway.Tick(repo, 0);
            
            for (int i = 0; i < 305; i++)
            {
                repo.Tick();
            }
            
            var startTime = DateTime.UtcNow;
            gateway.Tick(repo, 0);
            var duration = DateTime.UtcNow - startTime;
            
            Assert.True(duration.TotalMilliseconds < 1000, $"Timeout check took {duration.TotalMilliseconds}ms (expected <1000ms)");
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
            repo.RegisterComponent<NetworkOwnership>();
            repo.RegisterManagedComponent<DescriptorOwnership>();
            
            repo.RegisterEvent<ConstructionOrder>();
            repo.RegisterEvent<ConstructionAck>();
            repo.RegisterEvent<DestructionOrder>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
        }
    }
}
