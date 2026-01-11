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
            
            // Get current owner for logging and logic
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
            
            // descOwnership.Map[update.DescrTypeId] = update.NewOwner; // BUG?
            // Fix: Use PackKey
            long key = OwnershipExtensions.PackKey(update.DescrTypeId, update.InstanceId);
            descOwnership.Map[key] = update.NewOwner;
            cmd.SetManagedComponent(entity, descOwnership);
            
            // Update primary owner if EntityMaster
            if (update.DescrTypeId == 0) // EntityMaster
            {
                var nw = view.GetComponentRO<NetworkOwnership>(entity);
                var newNw = nw; // struct copy
                newNw.PrimaryOwnerId = update.NewOwner;
                cmd.SetComponent(entity, newNw);
            }
            
            // ★ NEW: Emit DescriptorAuthorityChanged event
            bool isNowOwner = (update.NewOwner == _localNodeId);
            bool wasOwner = (previousOwner == _localNodeId);
            
            if (isNowOwner != wasOwner)
            {
                // Ownership transition occurred
                var evt = new DescriptorAuthorityChanged
                {
                    Entity = entity,
                    DescriptorTypeId = update.DescrTypeId,
                    IsNowOwner = isNowOwner,
                    NewOwnerId = update.NewOwner
                };
                
                cmd.PublishEvent(evt);
                
                Console.WriteLine($"[Ownership] Entity {entity.Index} descriptor {update.DescrTypeId}: " +
                    $"{(isNowOwner ? "ACQUIRED" : "LOST")} ownership (new owner: {update.NewOwner})");
            }
            
            // ★ NEW: Add ForceNetworkPublish if we became owner (SST confirmation write)
            if (isNowOwner)
            {
                cmd.SetComponent(entity, new ForceNetworkPublish());
                Console.WriteLine($"[Ownership] Entity {entity.Index}: Force publish scheduled for confirmation");
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer) { }
    }
}
