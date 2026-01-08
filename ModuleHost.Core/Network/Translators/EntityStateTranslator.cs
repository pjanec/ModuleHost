using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Network.Translators
{
    public class EntityStateTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.EntityState";
        private const long ENTITY_STATE_DESCRIPTOR_ID = 1;
        
        private readonly int _localNodeId;
        private readonly DescriptorOwnershipMap _ownershipMap;
        
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        private readonly Dictionary<Entity, long> _entityToNetworkId;
        
        public EntityStateTranslator(
            int localNodeId, 
            DescriptorOwnershipMap ownershipMap,
            Dictionary<long, Entity> networkIdToEntity = null,
            Dictionary<Entity, long> entityToNetworkId = null)
        {
            _localNodeId = localNodeId;
            _ownershipMap = ownershipMap;
            _networkIdToEntity = networkIdToEntity ?? new Dictionary<long, Entity>();
            _entityToNetworkId = entityToNetworkId ?? new Dictionary<Entity, long>();
        }
        
        public EntityStateTranslator() : this(0, new DescriptorOwnershipMap()) 
        {
            _ownershipMap.RegisterMapping(ENTITY_STATE_DESCRIPTOR_ID, typeof(Position), typeof(Velocity));
        }

        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            var samples = reader.TakeSamples();
            foreach (var sample in samples)
            {
                if (sample.InstanceState == DdsInstanceState.NotAliveDisposed)
                {
                    HandleDescriptorDisposal(sample.EntityId != 0 ? sample.EntityId : TryGetEntityId(sample.Data), cmd, view);
                    continue;
                }
                
                if (sample.Data is not EntityStateDescriptor desc)
                {
                    if (sample.InstanceState == DdsInstanceState.Alive)
                        Console.Error.WriteLine($"[EntityStateTranslator] Unexpected sample type: {sample.Data?.GetType().Name}");
                    continue;
                }
                
                Entity entity = FindEntityByNetworkId(desc.EntityId, view);
                
                if (entity == Entity.Null)
                {
                    entity = CreateEntityFromDescriptor(desc, cmd, view);
                    _networkIdToEntity[desc.EntityId] = entity;
                    _entityToNetworkId[entity] = desc.EntityId;
                }
                
                UpdateEntityFromDescriptor(entity, desc, cmd, view);
            }
        }
        
        private long TryGetEntityId(object? data)
        {
            if (data is EntityStateDescriptor desc) return desc.EntityId;
            return 0;
        }

        private Entity FindEntityByNetworkId(long networkId, ISimulationView view)
        {
             if (_networkIdToEntity.TryGetValue(networkId, out var entity) && view.IsAlive(entity))
             {
                 return entity;
             }
             
             var query = view.Query().With<NetworkIdentity>().IncludeConstructing().Build();
             foreach(var e in query)
             {
                 var comp = view.GetComponentRO<NetworkIdentity>(e);
                 if (comp.Value == networkId)
                 {
                     _networkIdToEntity[networkId] = e;
                     _entityToNetworkId[e] = networkId;
                     return e;
                 }
             }
             
             if (_networkIdToEntity.ContainsKey(networkId))
             {
                 _networkIdToEntity.Remove(networkId);
             }
             
             return Entity.Null;
        }
        
        private Entity CreateEntityFromDescriptor(
            EntityStateDescriptor desc, 
            IEntityCommandBuffer cmd, 
            ISimulationView view)
        {
            var entity = cmd.CreateEntity();
            cmd.SetLifecycleState(entity, EntityLifecycle.Constructing);
            
            cmd.SetComponent(entity, new Position { Value = desc.Location });
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
            cmd.SetComponent(entity, new NetworkIdentity { Value = desc.EntityId });
            
            // Set NetworkOwnership (Unmanaged)
            cmd.SetComponent(entity, new NetworkOwnership
            {
                PrimaryOwnerId = desc.OwnerId,
                LocalNodeId = _localNodeId
            });
            
            // Set DescriptorOwnership (Managed) - Empty initially as we fall back to PrimaryOwner
            // But if we want to be explicit, map ENTITY_STATE -> desc.OwnerId
            // Convention: If not in map, use Primary. So empty is fine.
            cmd.AddManagedComponent(entity, new DescriptorOwnership());
            
            cmd.SetComponent(entity, new NetworkTarget
            {
                Value = desc.Location,
                Timestamp = desc.Timestamp
            });
            
            Console.WriteLine($"[EntityStateTranslator] Created entity {(long)entity.PackedValue} from network ID {desc.EntityId}");
            
            return entity;
        }
        
        private void UpdateEntityFromDescriptor(
            Entity entity,
            EntityStateDescriptor desc,
            IEntityCommandBuffer cmd,
            ISimulationView view)
        {
            // Only update if we check ownership
            if (view.HasComponent<NetworkOwnership>(entity))
            {
                // Extension method handles looking up Managed component if present
                if (view.OwnsDescriptor(entity, ENTITY_STATE_DESCRIPTOR_ID))
                {
                    return;
                }
            }
            
            cmd.SetComponent(entity, new NetworkTarget
            {
                Value = desc.Location,
                Timestamp = desc.Timestamp
            });
            
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
        }
        
        private void HandleDescriptorDisposal(
            long networkEntityId, 
            IEntityCommandBuffer cmd, 
            ISimulationView view)
        {
            if (!_networkIdToEntity.TryGetValue(networkEntityId, out var entity))
                return;
            
            if (!view.HasComponent<NetworkOwnership>(entity))
                return;
            
            int currentOwner = view.GetDescriptorOwner(entity, ENTITY_STATE_DESCRIPTOR_ID);
            var nwOwnership = view.GetComponentRO<NetworkOwnership>(entity);

            if (currentOwner == nwOwnership.PrimaryOwnerId)
            {
                Console.WriteLine($"[Disposal] EntityMaster owner disposed EntityState for {networkEntityId} (entity deletion in progress)");
                return;
            }
            
            Console.WriteLine($"[Disposal] Returning EntityState ownership to EntityMaster owner (Node {nwOwnership.PrimaryOwnerId})");
            
            // Remove from Managed Map
            if (view.HasManagedComponent<DescriptorOwnership>(entity))
            {
                var ro = view.GetManagedComponentRO<DescriptorOwnership>(entity);
                var copy = new DescriptorOwnership { Map = new Dictionary<long, int>(ro.Map) };
                
                if (copy.Map.Remove(ENTITY_STATE_DESCRIPTOR_ID))
                {
                    cmd.SetManagedComponent(entity, copy);
                }
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query unmanaged components
            var query = view.Query()
                .With<Position>()
                .With<Velocity>()
                .With<NetworkOwnership>()
                .Build();
            
            foreach (var entity in query)
            {
                // Check detailed ownership using Extension (checks Managed list)
                if (!view.OwnsDescriptor(entity, ENTITY_STATE_DESCRIPTOR_ID))
                {
                    continue;
                }
                
                if (!_entityToNetworkId.TryGetValue(entity, out var networkId))
                {
                    if (view.HasComponent<NetworkIdentity>(entity))
                    {
                        networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
                    }
                    else
                    {
                        networkId = (long)entity.PackedValue;
                    }
                    _entityToNetworkId[entity] = networkId;
                    _networkIdToEntity[networkId] = entity;
                }
                
                var pos = view.GetComponentRO<Position>(entity);
                var vel = view.GetComponentRO<Velocity>(entity);
                var ownerId = view.GetDescriptorOwner(entity, ENTITY_STATE_DESCRIPTOR_ID);
                
                var descriptor = new EntityStateDescriptor
                {
                    EntityId = networkId,
                    OwnerId = ownerId,
                    Location = pos.Value,
                    Velocity = vel.Value,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                writer.Write(descriptor);
            }
        }
        
        public void OnEntityDestroyed(Entity entity, IDataWriter writer)
        {
            if (_entityToNetworkId.TryGetValue(entity, out var networkId))
            {
                writer.Dispose(networkId);
                _entityToNetworkId.Remove(entity);
                _networkIdToEntity.Remove(networkId);
            }
        }
    }
}
