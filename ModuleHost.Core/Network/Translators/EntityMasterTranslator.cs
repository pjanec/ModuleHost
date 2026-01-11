using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    public class EntityMasterTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.EntityMaster";
        private const long ENTITY_MASTER_DESCRIPTOR_ID = 0;
        
        private readonly int _localNodeId;
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        
        public EntityMasterTranslator(
            int localNodeId,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            // Cast for direct repository access
            var repo = view as EntityRepository;
            if (repo == null)
            {
                throw new InvalidOperationException(
                    "EntityMasterTranslator requires direct EntityRepository access. " +
                    "NetworkGateway must run with ExecutionPolicy.Synchronous().");
            }
            
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.InstanceState == DdsInstanceState.NotAliveDisposed)
                {
                    HandleDisposal(sample, cmd, view);
                    continue;
                }
                
                if (sample.Data is not EntityMasterDescriptor desc)
                {
                    if (sample.InstanceState == DdsInstanceState.Alive)
                        Console.Error.WriteLine($"[EntityMasterTranslator] Unexpected sample type: {sample.Data?.GetType().Name}");
                    continue;
                }
                
                // Check if entity already exists (could be Ghost from EntityState)
                Entity entity;
                bool isNewEntity = false;
                
                if (!_networkIdToEntity.TryGetValue(desc.EntityId, out entity) || !view.IsAlive(entity))
                {
                    // Entity doesn't exist - create it directly (Master-first scenario)
                    entity = repo.CreateEntity();
                    isNewEntity = true;
                    
                    // Set NetworkIdentity
                    repo.AddComponent(entity, new NetworkIdentity { Value = desc.EntityId });
                    
                    // Add to mapping
                    _networkIdToEntity[desc.EntityId] = entity;
                    
                    Console.WriteLine($"[EntityMasterTranslator] Created entity {entity.Index} from EntityMaster (network ID {desc.EntityId})");
                }
                else
                {
                    Console.WriteLine($"[EntityMasterTranslator] Found existing entity {entity.Index} for network ID {desc.EntityId}");
                }
                
                // Set or update NetworkOwnership
                var netOwnership = new NetworkOwnership
                {
                    PrimaryOwnerId = desc.OwnerId,
                    LocalNodeId = _localNodeId
                };

                if (repo.HasComponent<NetworkOwnership>(entity))
                {
                    repo.SetComponent(entity, netOwnership);
                }
                else
                {
                    repo.AddComponent(entity, netOwnership);
                }
                
                // Ensure DescriptorOwnership exists
                if (!repo.HasManagedComponent<DescriptorOwnership>(entity))
                {
                    repo.SetManagedComponent(entity, new DescriptorOwnership());
                }
                
                // ★ KEY PART: Add NetworkSpawnRequest for NetworkSpawnerSystem to process
                // This component tells the spawner:
                // - What DIS type this entity is (for TKB template lookup)
                // - Whether to use reliable init mode
                // - What the primary owner is
                if (!repo.HasComponent<NetworkSpawnRequest>(entity))
                {
                    repo.AddComponent(entity, new NetworkSpawnRequest
                    {
                        DisType = desc.Type,
                        PrimaryOwnerId = desc.OwnerId,
                        Flags = desc.Flags,
                        NetworkEntityId = desc.EntityId
                    });
                    Console.WriteLine($"[EntityMasterTranslator] Added NetworkSpawnRequest for entity {entity.Index} (Type: {desc.Type.Kind}, Flags: {desc.Flags})");
                }
            }
        }
        
        private void HandleDisposal(IDataSample sample, IEntityCommandBuffer cmd, ISimulationView view)
        {
            long entityId = sample.EntityId;
            if (entityId == 0 && sample.Data is EntityMasterDescriptor desc)
            {
                entityId = desc.EntityId;
            }
            
            if (entityId == 0)
            {
                Console.Error.WriteLine("[EntityMasterTranslator] Cannot handle disposal - no entity ID");
                return;
            }
            
            if (_networkIdToEntity.TryGetValue(entityId, out var entity))
            {
                Console.WriteLine($"[EntityMaster] Disposed {entityId}. Destroying entity {entity.Index}.");
                cmd.DestroyEntity(entity);
                _networkIdToEntity.Remove(entityId);
            }
        }

        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Not implementing Egress for Master in this task
        }
    }
}
