using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Network Gateway Module that participates in EntityLifecycleManagement.
    /// For entities with ReliableInit flag, this module withholds its construction
    /// ACK until peer nodes confirm entity activation.
    /// </summary>
    public class NetworkGatewayModule : IModule
    {
        public string Name => "NetworkGateway";
        
        private readonly int _localNodeId;
        private readonly INetworkTopology _topology;
        private readonly EntityLifecycleModule _elm;
        
        // Track pending network ACKs: EntityId -> Set of node IDs we're waiting for
        private readonly Dictionary<Entity, HashSet<int>> _pendingPeerAcks;
        
        // Track when entities entered pending state (for timeout)
        private readonly Dictionary<Entity, uint> _pendingStartFrame;
        
        public int ModuleId { get; }
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        public NetworkGatewayModule(
            int moduleId,
            int localNodeId,
            INetworkTopology topology,
            EntityLifecycleModule elm)
        {
            ModuleId = moduleId;
            _localNodeId = localNodeId;
            _topology = topology ?? throw new ArgumentNullException(nameof(topology));
            _elm = elm ?? throw new ArgumentNullException(nameof(elm));
            _pendingPeerAcks = new Dictionary<Entity, HashSet<int>>();
            _pendingStartFrame = new Dictionary<Entity, uint>();
            
            // Register with ELM so we receive ConstructionOrder events
            _elm.RegisterModule(ModuleId);
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            Tick(view, deltaTime, null);
        }

        public void Tick(ISimulationView view, float deltaTime, uint? frameOverride)
        {
            uint currentFrame = 0;
            
            if (frameOverride.HasValue)
            {
                currentFrame = frameOverride.Value;
            }
            else if (view is EntityRepository repo)
            {
                currentFrame = repo.GlobalVersion;
            }
            
            var cmd = view.GetCommandBuffer();
            
            // Process ConstructionOrder events from ELM
            ProcessConstructionOrders(view, cmd, currentFrame);
            
            // Handle DestructionOrder to clean up pending state
            ProcessDestructionOrders(view);
            
            // Check for timeouts on pending ACKs
            CheckPendingAckTimeouts(cmd, currentFrame);
        }
        
        private void ProcessConstructionOrders(ISimulationView view, IEntityCommandBuffer cmd, uint currentFrame)
        {
            var events = view.ConsumeEvents<ConstructionOrder>();
            
            foreach (var evt in events)
            {
                // Only handle entities with PendingNetworkAck component
                if (!view.HasComponent<PendingNetworkAck>(evt.Entity))
                {
                    Console.WriteLine($"[Gateway] Entity {evt.Entity.Index} missing PendingNetworkAck. ACKing.");
                    // Fast mode - ACK immediately
                    _elm.AcknowledgeConstruction(evt.Entity, ModuleId, currentFrame, cmd);
                    continue;
                }
                
                // Reliable mode - determine peers and wait for their ACKs
                // NetworkSpawnerSystem populates PendingNetworkAck with ExpectedType
                var pendingInfo = view.GetComponentRO<PendingNetworkAck>(evt.Entity);
                
                var expectedPeers = _topology.GetExpectedPeers(pendingInfo.ExpectedType);
                var peerSet = new HashSet<int>(expectedPeers);
                
                Console.WriteLine($"[Gateway] Entity {evt.Entity.Index}: Reliable mode. Peers: {string.Join(",", peerSet)}");

                if (peerSet.Count == 0)
                {
                    Console.WriteLine($"[Gateway] Entity {evt.Entity.Index}: No peers. ACKing.");
                    // No peers to wait for - ACK immediately
                    _elm.AcknowledgeConstruction(evt.Entity, ModuleId, currentFrame, cmd);
                    cmd.RemoveComponent<PendingNetworkAck>(evt.Entity);
                }
                else
                {
                    Console.WriteLine($"[Gateway] Entity {evt.Entity.Index}: Waiting for ACKs.");
                    // Wait for peer ACKs
                    _pendingPeerAcks[evt.Entity] = peerSet;
                    _pendingStartFrame[evt.Entity] = currentFrame;
                }
            }
        }
        
        /// <summary>
        /// Called by EntityLifecycleStatusTranslator when a peer status message arrives.
        /// </summary>
        public void ReceiveLifecycleStatus(Entity entity, int nodeId, EntityLifecycle state, IEntityCommandBuffer cmd, uint currentFrame)
        {
            if (!_pendingPeerAcks.TryGetValue(entity, out var pendingPeers))
                return; // Not waiting for this entity
            
            if (state != EntityLifecycle.Active)
                return; // Only care about Active confirmations
            
            if (pendingPeers.Remove(nodeId))
            {
                // ACK received from node
            }
            
            // Check if all peers have ACKed
            if (pendingPeers.Count == 0)
            {
                _elm.AcknowledgeConstruction(entity, ModuleId, currentFrame, cmd);
                cmd.RemoveComponent<PendingNetworkAck>(entity);
                
                _pendingPeerAcks.Remove(entity);
                _pendingStartFrame.Remove(entity);
            }
        }
        
        private void CheckPendingAckTimeouts(IEntityCommandBuffer cmd, uint currentFrame)
        {
            var timedOut = new List<Entity>();
            
            foreach (var kvp in _pendingStartFrame)
            {
                var entity = kvp.Key;
                var startFrame = kvp.Value;
                
                if (currentFrame - startFrame > NetworkConstants.RELIABLE_INIT_TIMEOUT_FRAMES)
                {
                    Console.Error.WriteLine($"[NetworkGatewayModule] Entity {entity.Index}: Timeout waiting for peer ACKs");
                    timedOut.Add(entity);
                }
            }
            
            foreach (var entity in timedOut)
            {
                // Timeout - ACK anyway to prevent blocking forever
                _elm.AcknowledgeConstruction(entity, ModuleId, currentFrame, cmd);
                cmd.RemoveComponent<PendingNetworkAck>(entity);
                
                _pendingPeerAcks.Remove(entity);
                _pendingStartFrame.Remove(entity);
            }
        }
        
        private void ProcessDestructionOrders(ISimulationView view)
        {
            var events = view.ConsumeEvents<DestructionOrder>();
            foreach (var evt in events)
            {
                if (_pendingPeerAcks.ContainsKey(evt.Entity))
                {
                    _pendingPeerAcks.Remove(evt.Entity);
                    _pendingStartFrame.Remove(evt.Entity);
                }
            }
        }
    }
}
