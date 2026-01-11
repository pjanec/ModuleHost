using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    /// <summary>
    /// Translates EntityLifecycleStatusDescriptor messages for reliable initialization.
    /// Ingress: Receives peer status updates and notifies NetworkGatewayModule.
    /// Egress: Publishes our lifecycle status when entities become Active.
    /// </summary>
    public class EntityLifecycleStatusTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.EntityLifecycleStatus";
        
        private readonly int _localNodeId;
        private readonly NetworkGatewayModule _gateway;
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        
        public EntityLifecycleStatusTranslator(
            int localNodeId,
            NetworkGatewayModule gateway,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            var repo = view as EntityRepository;
            if (repo == null) return;
            
            uint currentFrame = repo.GlobalVersion;
            
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.InstanceState != DdsInstanceState.Alive)
                    continue;
                
                if (sample.Data is not EntityLifecycleStatusDescriptor status)
                    continue;
                
                // Ignore our own messages
                if (status.NodeId == _localNodeId)
                    continue;
                
                // Find entity by network ID
                if (!_networkIdToEntity.TryGetValue(status.EntityId, out var entity))
                {
                    continue;
                }
                
                // Forward to gateway
                _gateway.ReceiveLifecycleStatus(entity, status.NodeId, status.State, cmd, currentFrame);
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query entities that:
            // 1. Have NetworkIdentity (networked entities)
            // 2. Have PendingNetworkAck (were in reliable init mode)
            // 3. Are now Active (just finished construction)
            
            var query = view.Query()
                .With<NetworkIdentity>()
                .With<PendingNetworkAck>()
                .WithLifecycle(EntityLifecycle.Active)
                .Build();
            
            foreach (var entity in query)
            {
                var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
                
                var status = new EntityLifecycleStatusDescriptor
                {
                    EntityId = networkId,
                    NodeId = _localNodeId,
                    State = EntityLifecycle.Active,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                writer.Write(status);
            }
        }
    }
}
