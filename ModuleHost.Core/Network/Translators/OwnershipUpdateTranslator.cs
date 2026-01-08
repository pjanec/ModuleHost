using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    public class OwnershipUpdateTranslator : IDescriptorTranslator
    {
        private readonly int _localNodeId;
        private readonly DescriptorOwnershipMap _ownershipMap;
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        
        public string TopicName => "SST.OwnershipUpdate";
        
        public OwnershipUpdateTranslator(
            int localNodeId,
            DescriptorOwnershipMap ownershipMap,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _ownershipMap = ownershipMap;
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.InstanceState != DdsInstanceState.Alive)
                    continue;

                if (sample.Data is not OwnershipUpdate update)
                    continue;
                
                ProcessOwnershipUpdate(update, cmd, view);
            }
        }
        
        private void ProcessOwnershipUpdate(
            OwnershipUpdate update,
            IEntityCommandBuffer cmd,
            ISimulationView view)
        {
            if (!_networkIdToEntity.TryGetValue(update.EntityId, out var entity))
            {
                Console.WriteLine($"[Ownership] Entity {update.EntityId} not found locally");
                return;
            }
            
            if (!view.HasComponent<NetworkOwnership>(entity))
            {
                Console.WriteLine($"[Ownership] Entity {entity} has no NetworkOwnership component");
                return;
            }
            
            // Get current owner for logging
            var previousOwner = view.GetDescriptorOwner(entity, update.DescrTypeId);
            
            // Update ownership
            // Must get or create DescriptorOwnership managed component
            DescriptorOwnership descOwnership;
            if (view.HasManagedComponent<DescriptorOwnership>(entity))
            {
                var ro = view.GetManagedComponentRO<DescriptorOwnership>(entity);
                // Shallow copy the map
                descOwnership = new DescriptorOwnership { Map = new Dictionary<long, int>(ro.Map) };
            }
            else
            {
                descOwnership = new DescriptorOwnership();
            }
            
            descOwnership.Map[update.DescrTypeId] = update.NewOwner;
            cmd.SetManagedComponent(entity, descOwnership);
            
            // Update primary owner if EntityMaster
            if (update.DescrTypeId == 0) // EntityMaster
            {
                var nw = view.GetComponentRO<NetworkOwnership>(entity);
                var newNw = nw; // struct copy
                newNw.PrimaryOwnerId = update.NewOwner;
                cmd.SetComponent(entity, newNw);
            }
            
            /* 
            // Update FDP component metadata
            // Note: ISimulationView does not expose GetComponentTable, and Metadata is per-type, not per-entity.
            // Skipping as per architectural review.
            var componentTypes = _ownershipMap.GetComponentsForDescriptor(update.DescrTypeId);
            foreach (var componentType in componentTypes)
            {
               // logic removed
            } 
            */
            
            var isBecomingOwner = update.NewOwner == _localNodeId;
            var isLosingOwnership = previousOwner == _localNodeId && update.NewOwner != _localNodeId;
            
            if (isBecomingOwner)
            {
                Console.WriteLine($"[Ownership] Acquired descriptor {update.DescrTypeId} for entity {update.EntityId}");
            }
            else if (isLosingOwnership)
            {
                Console.WriteLine($"[Ownership] Lost descriptor {update.DescrTypeId} for entity {update.EntityId}");
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer) { }
    }
}
