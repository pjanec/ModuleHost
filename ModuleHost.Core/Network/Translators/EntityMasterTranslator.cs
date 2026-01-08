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
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.InstanceState == DdsInstanceState.NotAliveDisposed)
                {
                    HandleDisposal(sample, cmd, view);
                    continue;
                }
                
                if (sample.Data is EntityMasterDescriptor desc)
                {
                    // Handle Creation / Update
                    // Logic similar to EntityStateTranslator?
                    // Typically EntityMaster creates the entity if it doesn't exist.
                    // But EntityStateTranslator also does that?
                    // Order is not guaranteed.
                    // If EntityStateTranslator created it, we just update.
                    
                    if (!_networkIdToEntity.TryGetValue(desc.EntityId, out var entity))
                    {
                        // Create entity
                        // But usually EntityState creates it with Position/Velocity.
                        // Master just has Meta.
                        // We'll skip creation logic for now unless required, assuming EntityState handles it.
                        // Or if Master comes first?
                    }
                    else
                    {
                        // Update PrimaryOwnerId
                        if (view.HasComponent<NetworkOwnership>(entity))
                        {
                            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
                            if (ownership.PrimaryOwnerId != desc.OwnerId)
                            {
                                var newOwnership = ownership;
                                newOwnership.PrimaryOwnerId = desc.OwnerId;
                                cmd.SetComponent(entity, newOwnership);
                            }
                        }
                    }
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
            
            if (entityId == 0) return;
            
            if (_networkIdToEntity.TryGetValue(entityId, out var entity))
            {
                Console.WriteLine($"[EntityMaster] Disposed {entityId}. Destroying entity.");
                cmd.DestroyEntity(entity);
                // Note: _networkIdToEntity cleanup happens via hooks or next frame checks usually.
                // We should remove it from local map to prevent further lookups this frame?
                // Shared map management is tricky. Assume higher level system handles map consistency or OnDestroyed.
                _networkIdToEntity.Remove(entityId);
            }
        }

        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Not implementing Egress for Master in this task
        }
    }
}
