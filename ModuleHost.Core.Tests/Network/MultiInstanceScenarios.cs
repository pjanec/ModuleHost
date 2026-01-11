using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Messages;
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
            // Setup: 2-node cluster
            // Node 1: Owns EntityMaster and primary weapon (instance 0)
            // Node 2: Owns secondary weapon (instance 1)
            
            using var repo1 = new EntityRepository();
            using var repo2 = new EntityRepository();
            
            RegisterComponents(repo1);
            RegisterComponents(repo2);
            
            // === Node 1: Create tank entity ===
            var tankEntity1 = repo1.CreateEntity();
            repo1.AddComponent(tankEntity1, new NetworkIdentity { Value = 100 });
            repo1.SetLifecycleState(tankEntity1, EntityLifecycle.Active);
            
            var weaponStates1 = new WeaponStates();
            weaponStates1.Weapons[0] = new WeaponState { AzimuthAngle = 45.0f, AmmoCount = 100 };
            weaponStates1.Weapons[1] = new WeaponState { AzimuthAngle = 90.0f, AmmoCount = 50 };
            repo1.AddManagedComponent(tankEntity1, weaponStates1);
            repo1.AddComponent(tankEntity1, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
            
            // Setup ownership: Node 1 owns weapon 0, Node 2 owns weapon 1
            var ownership1 = new DescriptorOwnership();
            ownership1.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1)] = 2;
            repo1.AddManagedComponent(tankEntity1, ownership1);
            
            // === Node 1: Egress (publishes weapon 0 only) ===
            var writer1 = new MockDataWriter();
            var translator1 = new WeaponStateTranslator(1, new Dictionary<long, Entity> { { 100, tankEntity1 } });
            translator1.ScanAndPublish(repo1, writer1);
            
            Assert.Single(writer1.WrittenSamples); // Only weapon 0
            var pub1 = (WeaponStateDescriptor)writer1.WrittenSamples[0];
            Assert.Equal(0, pub1.InstanceId);
            Assert.Equal(45.0f, pub1.AzimuthAngle);
            
            // === Node 2: Receives weapon 0, publishes weapon 1 ===
            var tankEntity2 = repo2.CreateEntity();
            repo2.AddComponent(tankEntity2, new NetworkIdentity { Value = 100 });
            repo2.SetLifecycleState(tankEntity2, EntityLifecycle.Active);
            
            // Simulate ownership: Node 2 owns weapon 1
            var ownership2 = new DescriptorOwnership();
            ownership2.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)] = 1;
            // Instance 1 implicitly owned by node 2 (not in map = use PrimaryOwnerId logic)
            // Wait, if PrimaryOwner is 1, then Node 2 only owns instance 1 if explicitly mapped?
            // Or if Node 2 is primary?
            // Scenario says Node 1 owns EntityMaster (Primary=1).
            // So on Node 2: PrimaryOwnerId=1.
            // For Node 2 to own instance 1, it MUST be in the map (Map[(Weapon, 1)] = 2).
            repo2.AddComponent(tankEntity2, new NetworkOwnership { LocalNodeId = 2, PrimaryOwnerId = 1 });
            ownership2.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 1)] = 2;
            repo2.AddManagedComponent(tankEntity2, ownership2);
            
            var weaponStates2 = new WeaponStates();
            weaponStates2.Weapons[1] = new WeaponState { AzimuthAngle = 120.0f, AmmoCount = 75 };
            repo2.AddManagedComponent(tankEntity2, weaponStates2);
            
            // Node 2 publishes
            var writer2 = new MockDataWriter();
            var translator2 = new WeaponStateTranslator(2, new Dictionary<long, Entity> { { 100, tankEntity2 } });
            translator2.ScanAndPublish(repo2, writer2);
            
            Assert.Single(writer2.WrittenSamples); // Only weapon 1
            var pub2 = (WeaponStateDescriptor)writer2.WrittenSamples[0];
            Assert.Equal(1, pub2.InstanceId);
            Assert.Equal(120.0f, pub2.AzimuthAngle);
            
            // === Node 1: Ingress weapon 1 from Node 2 ===
            var reader1 = new MockDataReader(new DataSample
            {
                Data = new WeaponStateDescriptor
                {
                    EntityId = 100,
                    InstanceId = 1,
                    AzimuthAngle = 120.0f,
                    AmmoCount = 75
                },
                InstanceState = DdsInstanceState.Alive,
                EntityId = 100,
                InstanceId = 1 // NEW: Carry instance ID in sample
            });
            
            var cmd1 = repo1.GetCommandBuffer();
            translator1.PollIngress(reader1, cmd1, repo1);
            cmd1.Playback();
            
            // Verify: Node 1 now has both weapon instances
            var finalWeapons1 = repo1.GetManagedComponentRO<WeaponStates>(tankEntity1);
            Assert.Equal(2, finalWeapons1.Weapons.Count);
            Assert.Equal(45.0f, finalWeapons1.Weapons[0].AzimuthAngle);  // Local weapon 0
            Assert.Equal(120.0f, finalWeapons1.Weapons[1].AzimuthAngle); // Remote weapon 1
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkOwnership>();
            // WeaponStates and DescriptorOwnership are managed components, no registration needed in some ECS,
            // but Fdp.Kernel usually doesn't require explicit registration for Managed components unless used in queries/serialization setup.
            // But we might need to register them if we use them in queries?
            // In ModuleHost integration tests we saw SetSchemaSetup.
            // For EntityRepository, RegisterComponent<T> is for unmanaged.
            // Managed components work out of the box usually.
        }
    }
}
