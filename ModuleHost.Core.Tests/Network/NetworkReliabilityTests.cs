using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkReliabilityTests
    {
        [Fact]
        public void Reliability_PacketLoss_10Percent_EntityEventuallyComplete()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityStateTranslator(1, networkIdToEntity);
            
            // Simulate 100 packets, 10% loss
            var random = new Random(42); // Seeded for reproducibility
            var packets = new List<IDataSample>();
            
            for (int i = 0; i < 100; i++)
            {
                if (random.NextDouble() > 0.10) // 90% delivery rate
                {
                    packets.Add(new DataSample
                    {
                        Data = new EntityStateDescriptor
                        {
                            EntityId = i,
                            Location = new System.Numerics.Vector3(i, i, 0),
                            Velocity = new System.Numerics.Vector3(1, 1, 0)
                        },
                        InstanceState = DdsInstanceState.Alive,
                        EntityId = i
                    });
                }
            }
            
            var reader = new MockDataReader(packets.ToArray());
            var cmd = repo.GetCommandBuffer();
            translator.PollIngress(reader, cmd, repo);
            cmd.Playback();
            
            // Verify ~90 entities created (with some variance)
            Assert.InRange(networkIdToEntity.Count, 85, 95);
        }
        
        [Fact]
        public void Reliability_DuplicatePackets_Idempotency()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var translator = new EntityStateTranslator(1, networkIdToEntity);
            
            // Send same EntityState packet 5 times (duplicate due to retransmission)
            var samples = new List<IDataSample>();
            for (int i = 0; i < 5; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityStateDescriptor
                    {
                        EntityId = 100,
                        Location = new System.Numerics.Vector3(10, 10, 0),
                        Velocity = new System.Numerics.Vector3(1, 1, 0)
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = 100
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = repo.GetCommandBuffer();
            translator.PollIngress(reader, cmd, repo);
            cmd.Playback();
            
            // Verify only 1 entity created, not 5
            Assert.Single(networkIdToEntity);
            
            var entity = networkIdToEntity[100];
            var pos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(10, pos.Value.X);
        }
        
        [Fact]
        public void Reliability_OutOfOrderPackets_EventualConsistency()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var networkIdToEntity = new Dictionary<long, Entity>();
            var stateTranslator = new EntityStateTranslator(1, networkIdToEntity);
            var masterTranslator = new EntityMasterTranslator(1, networkIdToEntity, null!);
            
            // Scenario: EntityState arrives BEFORE EntityMaster (out of order)
            
            // Step 1: EntityState arrives (should create Ghost)
            var stateReader = new MockDataReader(new DataSample
            {
                Data = new EntityStateDescriptor
                {
                    EntityId = 200,
                    Location = new System.Numerics.Vector3(50, 50, 0),
                    Velocity = new System.Numerics.Vector3(2, 2, 0)
                },
                InstanceState = DdsInstanceState.Alive,
                EntityId = 200
            });
            
            var cmd = repo.GetCommandBuffer();
            stateTranslator.PollIngress(stateReader, cmd, repo);
            cmd.Playback();
            
            Assert.Single(networkIdToEntity);
            var entity = networkIdToEntity[200];
            Assert.Equal(EntityLifecycle.Ghost, repo.GetLifecycleState(entity));
            
            var ghostPos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(50, ghostPos.Value.X);
            
            // Step 2: EntityMaster arrives late (should promote Ghost)
            var masterReader = new MockDataReader(new DataSample
            {
                Data = new EntityMasterDescriptor
                {
                    EntityId = 200,
                    OwnerId = 1,
                    Type = new DISEntityType { Kind = 1 },
                    Name = "LateArrival"
                },
                InstanceState = DdsInstanceState.Alive,
                EntityId = 200
            });
            
            cmd = repo.GetCommandBuffer();
            masterTranslator.PollIngress(masterReader, cmd, repo);
            cmd.Playback();
            
            // Verify: Same entity, Ghost position preserved, now has NetworkSpawnRequest
            Assert.Single(networkIdToEntity);
            Assert.True(repo.HasComponent<NetworkSpawnRequest>(entity));
            
            var finalPos = repo.GetComponentRO<Position>(entity);
            Assert.Equal(50, finalPos.Value.X); // Position from Ghost preserved
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
        }
    }
}
