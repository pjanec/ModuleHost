using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using Fdp.Kernel.Tkb;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class MultiInstanceScenarios
    {
        [Fact]
        public void Scenario_MultiTurretTank_ReplicatesAcrossNodes()
        {
            // --- Node 1 Setup ---
            using var repo1 = new EntityRepository();
            RegisterComponents(repo1);
            
            var strategy1 = new DeterministicOwnershipStrategy();
            var tkb1 = new MockTkb();
            var elm1 = new EntityLifecycleModule(new[] { 1 });
            var spawner1 = new NetworkSpawnerSystem(tkb1, elm1, strategy1, 1);
            
            // Create Tank request
            var entity1 = repo1.CreateEntity();
            // Setup ownership as Local Node 1
            repo1.AddComponent(entity1, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            repo1.AddComponent(entity1, new NetworkIdentity { Value = 100 });
            
            repo1.AddComponent(entity1, new NetworkSpawnRequest 
            { 
                DisType = new DISEntityType { Kind = 1, Category = 1 }, // Tank
                PrimaryOwnerId = 1,
                NetworkEntityId = 100
            });
            repo1.SetLifecycleState(entity1, EntityLifecycle.Ghost);
            
            // Execute Spawner (Node 1)
            spawner1.Execute(repo1, 0);
            
            // Verify Node 1 State
            // Replay command buffer to apply changes!
            ((EntityCommandBuffer)((ISimulationView)repo1).GetCommandBuffer()).Playback(repo1);

            // Populate WeaponStates (simulating game logic)
            var ws1 = ((ISimulationView)repo1).GetManagedComponentRO<WeaponStates>(entity1);
            ws1.Weapons[0] = new WeaponState();
            ws1.Weapons[1] = new WeaponState();

            var ownership1 = ((ISimulationView)repo1).GetManagedComponentRO<DescriptorOwnership>(entity1);
            
            Assert.True(repo1.OwnsDescriptor(entity1, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)); // Instance 0 -> Node 1
            Assert.False(repo1.OwnsDescriptor(entity1, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1)); // Instance 1 -> Node 2
            
            // --- Node 2 Setup ---
            using var repo2 = new EntityRepository();
            RegisterComponents(repo2);
            
            var strategy2 = new DeterministicOwnershipStrategy();
            var tkb2 = new MockTkb();
            var elm2 = new EntityLifecycleModule(new[] { 1 });
            var spawner2 = new NetworkSpawnerSystem(tkb2, elm2, strategy2, 2);
            
            // Node 2 receives EntityMaster
            var networkIdToEntity2 = new Dictionary<long, Entity>();
            var masterTranslator = new EntityMasterTranslator(2, networkIdToEntity2);
            
            var masterMsg = new EntityMasterDescriptor
            {
                EntityId = 100,
                OwnerId = 1,
                Type = new DISEntityType { Kind = 1, Category = 1 }
            };
            
            var reader = new MockDataReader(new MockDataSample { Data = masterMsg, InstanceState = DdsInstanceState.Alive });
            var cmd2 = ((ISimulationView)repo2).GetCommandBuffer();
            
            masterTranslator.PollIngress(reader, cmd2, repo2);
            ((EntityCommandBuffer)cmd2).Playback(repo2);
            
            // Verify Ghost created
            var entity2 = networkIdToEntity2[100];
            Assert.Equal(EntityLifecycle.Ghost, repo2.GetHeader(entity2.Index).LifecycleState);
            
            // Node 2 Spawner processes Ghost (NetworkSpawnRequest added by Translator)
            spawner2.Execute(repo2, 0);
            ((EntityCommandBuffer)((ISimulationView)repo2).GetCommandBuffer()).Playback(repo2);
            
            // Verify Node 2 State
            // Debug checks
            var nw2 = ((ISimulationView)repo2).GetComponentRO<NetworkOwnership>(entity2);
            Assert.Equal(1, nw2.PrimaryOwnerId);
            Assert.Equal(2, nw2.LocalNodeId);
            
            // Verify Map
            var ownMap = ((ISimulationView)repo2).GetManagedComponentRO<DescriptorOwnership>(entity2);
            long k0 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0);
            Assert.False(ownMap.Map.ContainsKey(k0), "Map should not contain key 0 (default)");
            
            long k1 = OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1);
            Assert.True(ownMap.Map.ContainsKey(k1), "Map should contain key 1");
            Assert.Equal(2, ownMap.Map[k1]);
            
            // Original assertions
            Assert.False(repo2.OwnsDescriptor(entity2, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)); // Instance 0 -> Node 1
            Assert.True(repo2.OwnsDescriptor(entity2, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1));  // Instance 1 -> Node 2
            
            // Check WeaponState component created
            Assert.True(((ISimulationView)repo2).HasManagedComponent<WeaponStates>(entity2));
            
            // === Data Replication ===
            // Node 1 sends update for Turret 0
            
            // Force Active so it gets picked up by Translator (which filters for Active entities)
            repo1.SetLifecycleState(entity1, EntityLifecycle.Active);

            var wsTranslator1 = new WeaponStateTranslator(1, new Dictionary<long, Entity> { { 100, entity1 } });
            var writer1 = new MockDataWriter();
            wsTranslator1.ScanAndPublish(repo1, writer1);
            
            // Should contain Turret 0 only
            Assert.Contains(writer1.WrittenSamples, s => ((WeaponStateDescriptor)s).InstanceId == 0);
            Assert.DoesNotContain(writer1.WrittenSamples, s => ((WeaponStateDescriptor)s).InstanceId == 1);
            
            // Verify Node 2 Ingress for Turret 0
            var wsMsg = (WeaponStateDescriptor)writer1.WrittenSamples.First(s => ((WeaponStateDescriptor)s).InstanceId == 0);
            var wsTranslator2 = new WeaponStateTranslator(2, networkIdToEntity2);
            var reader2 = new MockDataReader(new MockDataSample { Data = wsMsg, InstanceState = DdsInstanceState.Alive });
            
            wsTranslator2.PollIngress(reader2, cmd2, repo2);
            ((EntityCommandBuffer)cmd2).Playback(repo2);
            
            // Verify Node 2 has data for Turret 0
            var states2 = ((ISimulationView)repo2).GetManagedComponentRO<WeaponStates>(entity2);
            Assert.True(states2.Weapons.ContainsKey(0));
            // Turret 1 should be empty/default until Node 2 simulates it
            Assert.False(states2.Weapons.ContainsKey(1)); // Or default
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<NetworkOwnership>();
            repo.RegisterManagedComponent<DescriptorOwnership>();
            repo.RegisterManagedComponent<WeaponStates>();
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkTarget>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
            
            repo.RegisterEvent<ConstructionOrder>();
            repo.RegisterEvent<ConstructionAck>();
            repo.RegisterEvent<DestructionOrder>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
        }
        
        private class DeterministicOwnershipStrategy : IOwnershipDistributionStrategy
        {
            public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType, int masterNodeId, long instanceId)
            {
                if (descriptorTypeId == NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID)
                {
                    // Instance 0 -> Master (1)
                    // Instance 1 -> Peer (2)
                    return instanceId == 0 ? masterNodeId : 2;
                }
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
