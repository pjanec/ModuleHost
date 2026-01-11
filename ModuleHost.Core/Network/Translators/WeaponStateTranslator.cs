using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    public class WeaponStateTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.WeaponState";
        
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        private readonly int _localNodeId;
        
        public WeaponStateTranslator(
            int localNodeId,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            // Cache components locally for batch processing in this frame
            var batchCache = new Dictionary<Entity, WeaponStates>();

            foreach (var sample in reader.TakeSamples())
            {
                if (sample.Data is not WeaponStateDescriptor desc)
                    continue;
                
                if (!_networkIdToEntity.TryGetValue(desc.EntityId, out var entity))
                    continue; // Entity doesn't exist yet
                
                // Get or create WeaponStates component
                WeaponStates weaponStates;
                
                // Check local batch cache first
                if (batchCache.TryGetValue(entity, out var cachedStates))
                {
                    weaponStates = cachedStates;
                }
                else if (view.HasManagedComponent<WeaponStates>(entity))
                {
                    weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
                    batchCache[entity] = weaponStates;
                }
                else
                {
                    weaponStates = new WeaponStates();
                    batchCache[entity] = weaponStates;
                    // Note: We only Add to CMD once per entity at the end, or safely add here knowing we operate on ref
                    cmd.AddManagedComponent(entity, weaponStates);
                }
                
                // Update specific weapon instance
                weaponStates.Weapons[desc.InstanceId] = new WeaponState
                {
                    AzimuthAngle = desc.AzimuthAngle,
                    ElevationAngle = desc.ElevationAngle,
                    AmmoCount = desc.AmmoCount,
                    Status = desc.Status
                };
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query entities with weapon states
            var query = view.Query()
                .WithManaged<WeaponStates>()
                .WithLifecycle(EntityLifecycle.Active)
                .Build();
            
            foreach (var entity in query)
            {
                if (!view.HasComponent<NetworkIdentity>(entity))
                    continue;
                
                var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
                var weaponStates = view.GetManagedComponentRO<WeaponStates>(entity);
                
                // Publish each weapon instance we own
                foreach (var kvp in weaponStates.Weapons)
                {
                    long instanceId = kvp.Key;
                    
            // If not found (new instance)
            if (instanceId == -1) // Use a flag if found
            {
                // Just use the provided instanceId
            }
            
            // Check if we own this weapon instance
            if (!view.OwnsDescriptor(entity, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, instanceId))
                continue;
            
            var weaponState = kvp.Value;
                    
                    var desc = new WeaponStateDescriptor
                    {
                        EntityId = networkId,
                        InstanceId = instanceId,
                        AzimuthAngle = weaponState.AzimuthAngle,
                        ElevationAngle = weaponState.ElevationAngle,
                        AmmoCount = weaponState.AmmoCount,
                        Status = weaponState.Status
                    };
                    
                    writer.Write(desc);
                }
            }
        }
    }
}
