using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Tests.Mocks;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 3, targetCount: 10)]
    public class NetworkPerformanceBenchmarks
    {
        private EntityRepository _repo;
        private Dictionary<long, Entity> _networkIdToEntity;
        private EntityStateTranslator _stateTranslator;
        private WeaponStateTranslator _weaponTranslator;
        private NetworkEgressSystem _egressSystem;
        private MockDataWriter _writer;
        
        [Params(100, 500, 1000)]
        public int EntityCount;
        
        [GlobalSetup]
        public void Setup()
        {
            _repo = new EntityRepository();
            RegisterComponents(_repo);
            
            _networkIdToEntity = new Dictionary<long, Entity>();
            
            // Create entities with network components
            for (int i = 0; i < EntityCount; i++)
            {
                var entity = _repo.CreateEntity();
                _repo.AddComponent(entity, new NetworkIdentity { Value = i });
                _repo.AddComponent(entity, new Position { Value = new System.Numerics.Vector3(i, i, 0) });
                _repo.AddComponent(entity, new Velocity { Value = new System.Numerics.Vector3(1, 1, 0) });
                _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = 1, PrimaryOwnerId = 1 });
                _repo.SetLifecycleState(entity, EntityLifecycle.Active);
                
                _networkIdToEntity[i] = entity;
                
                // Add weapons to half the entities
                if (i % 2 == 0)
                {
                    var weaponStates = new WeaponStates();
                    weaponStates.Weapons[0] = new WeaponState { AzimuthAngle = 45, AmmoCount = 100 };
                    _repo.AddManagedComponent(entity, weaponStates);
                }
            }
            
            _stateTranslator = new EntityStateTranslator(1, _networkIdToEntity);
            _weaponTranslator = new WeaponStateTranslator(1, _networkIdToEntity);
            _writer = new MockDataWriter();
            
            _egressSystem = new NetworkEgressSystem(
                new IDescriptorTranslator[] { _stateTranslator, _weaponTranslator },
                new IDataWriter[] { _writer, _writer }
            );
        }
        
        [GlobalCleanup]
        public void Cleanup()
        {
            _repo.Dispose();
        }
        
        [Benchmark]
        public void Egress_PublishAllEntities()
        {
            _writer.WrittenSamples.Clear();
            _egressSystem.Execute(_repo, 0.016f);
        }
        
        [Benchmark]
        public void Ingress_EntityState_BatchUpdate()
        {
            // Simulate receiving updates for 10% of entities
            int updateCount = EntityCount / 10;
            var samples = new List<IDataSample>();
            
            for (int i = 0; i < updateCount; i++)
            {
                samples.Add(new DataSample
                {
                    Data = new EntityStateDescriptor
                    {
                        EntityId = i,
                        Location = new System.Numerics.Vector3(i + 1, i + 1, 0),
                        Velocity = new System.Numerics.Vector3(2, 2, 0)
                    },
                    InstanceState = DdsInstanceState.Alive,
                    EntityId = i
                });
            }
            
            var reader = new MockDataReader(samples.ToArray());
            var cmd = _repo.GetCommandBuffer();
            _stateTranslator.PollIngress(reader, cmd, _repo);
            cmd.Playback();
        }
        
        [Benchmark]
        public void OwnershipLookup_CompositeKey()
        {
            // Benchmark composite key packing/unpacking performance
            long sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                long key = OwnershipExtensions.PackKey(i, i * 2);
                var (typeId, instanceId) = OwnershipExtensions.UnpackKey(key);
                sum += typeId + instanceId;
            }
        }
        
        [Benchmark]
        public void GhostPromotion_BatchProcess()
        {
            // Create 10 Ghost entities
            var ghosts = new List<Entity>();
            for (int i = 0; i < 10; i++)
            {
                var entity = _repo.CreateEntity();
                _repo.SetLifecycleState(entity, EntityLifecycle.Ghost);
                _repo.AddComponent(entity, new NetworkSpawnRequest 
                { 
                    DisType = new DISEntityType { Kind = 1 },
                    PrimaryOwnerId = 1
                });
                ghosts.Add(entity);
            }
            
            // Benchmark NetworkSpawnerSystem processing
            // (Simplified - in real scenario would need full setup)
            foreach (var ghost in ghosts)
            {
                _repo.SetLifecycleState(ghost, EntityLifecycle.Constructing);
            }
        }
        
        [Benchmark]
        public void NetworkGateway_TimeoutCheck()
        {
            // Benchmark timeout checking logic (simplified)
            uint currentFrame = 1000;
            var pendingFrames = new Dictionary<Entity, uint>();
            
            for (int i = 0; i < 100; i++)
            {
                var entity = new Entity(i, 1);
                pendingFrames[entity] = (uint)(currentFrame - i * 3);
            }
            
            var timedOut = new List<Entity>();
            foreach (var kvp in pendingFrames)
            {
                if (currentFrame - kvp.Value > 300)
                {
                    timedOut.Add(kvp.Key);
                }
            }
        }
        
        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
            repo.RegisterComponent<NetworkOwnership>();
        }
    }
}
