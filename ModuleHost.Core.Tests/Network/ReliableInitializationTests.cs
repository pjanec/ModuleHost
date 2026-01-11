using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class ReliableInitializationTests
    {
        // === Mocks ===
        
        private class MockTopology : INetworkTopology
        {
            public int LocalNodeId => 1;
            public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
            {
                return new[] { 2, 3 }; // Expecting 2 peers
            }
        }
        
        private class MockEmptyTopology : INetworkTopology
        {
            public int LocalNodeId => 1;
            public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
            {
                return Enumerable.Empty<int>();
            }
        }
        
        // Setup Helper
        private (NetworkGatewayModule, EntityLifecycleModule, TestMockView, MockCommandBuffer) Setup(INetworkTopology topology)
        {
            var participating = new[] { 10 };
            var elm = new EntityLifecycleModule(participating);
            var cmd = new MockCommandBuffer();
            var view = new TestMockView(cmd);
            var module = new NetworkGatewayModule(10, 1, topology, elm);
            
            return (module, elm, view, cmd);
        }

        // ==========================================
        // 1. StaticNetworkTopology Tests
        // ==========================================

        [Fact]
        public void StaticTopology_GetExpectedPeers_ExcludesLocalNode()
        {
            var topo = new StaticNetworkTopology(1, new[] { 1, 2, 3 });
            var peers = topo.GetExpectedPeers(new DISEntityType());
            
            Assert.DoesNotContain(1, peers);
            Assert.Contains(2, peers);
            Assert.Contains(3, peers);
            Assert.Equal(2, peers.Count());
        }

        [Fact]
        public void StaticTopology_SingleNode_ReturnsEmpty()
        {
            var topo = new StaticNetworkTopology(1, new[] { 1 });
            var peers = topo.GetExpectedPeers(new DISEntityType());
            Assert.Empty(peers);
        }

        // ==========================================
        // 2. NetworkGatewayModule Tests
        // ==========================================

        [Fact]
        public void Gateway_FastMode_AcksImmediately()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var entity = new Entity(1, 1);
            
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            Assert.Single(cmd.Acks);
            Assert.Equal(entity, cmd.Acks[0].Entity);
        }

        [Fact]
        public void Gateway_ReliableMode_NoPeers_AcksImmediately()
        {
            var (module, elm, view, cmd) = Setup(new MockEmptyTopology());
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            Assert.Single(cmd.Acks);
            Assert.Contains((entity, typeof(PendingNetworkAck)), cmd.RemovedComponents);
        }

        [Fact]
        public void Gateway_ReliableMode_WithPeers_WaitsForAck()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology()); // Expects 2 peers
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            Assert.Empty(cmd.Acks); // Should wait
        }
        
        [Fact]
        public void Gateway_ReceivesAllAcks_SendsLocalAck()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology()); // Expects 2, 3
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            module.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, 101);
            Assert.Empty(cmd.Acks);
            
            module.ReceiveLifecycleStatus(entity, 3, EntityLifecycle.Active, cmd, 102);
            
            Assert.Single(cmd.Acks);
            Assert.Equal(entity, cmd.Acks[0].Entity);
            Assert.Contains((entity, typeof(PendingNetworkAck)), cmd.RemovedComponents);
        }
        
        [Fact]
        public void Gateway_Timeout_AcksAnyway()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            Assert.Empty(cmd.Acks);
            
            module.Tick(view, 0, frameOverride: 100 + 301);
            
            Assert.Single(cmd.Acks);
            Assert.Contains((entity, typeof(PendingNetworkAck)), cmd.RemovedComponents);
        }

        [Fact]
        public void Gateway_MultipleEntitiesPending_HandlesIndependently()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology()); // Expects 2, 3
            var entity1 = new Entity(1, 1);
            var entity2 = new Entity(2, 1);
            
            view.AddComponent(entity1, new PendingNetworkAck());
            view.AddComponent(entity1, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.AddComponent(entity2, new PendingNetworkAck());
            view.AddComponent(entity2, new NetworkSpawnRequest { DisType = new DISEntityType() });
            
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity1 });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity2 });
            
            module.Tick(view, 0, frameOverride: 100);
            
            Assert.Empty(cmd.Acks);
            
            module.ReceiveLifecycleStatus(entity1, 2, EntityLifecycle.Active, cmd, 101);
            module.ReceiveLifecycleStatus(entity1, 3, EntityLifecycle.Active, cmd, 102);
            
            Assert.Single(cmd.Acks);
            Assert.Equal(entity1, cmd.Acks[0].Entity);
            
            module.ReceiveLifecycleStatus(entity2, 2, EntityLifecycle.Active, cmd, 103);
            Assert.Single(cmd.Acks);
            
            module.ReceiveLifecycleStatus(entity2, 3, EntityLifecycle.Active, cmd, 104);
            Assert.Equal(2, cmd.Acks.Count);
        }

        [Fact]
        public void Gateway_EntityDestroyedWhilePending_CleansUpState()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            view.DestructionOrders.Add(new DestructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 101);
            
            module.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, 101);
            Assert.Empty(cmd.Acks);
        }

        [Fact]
        public void Gateway_DuplicateAckFromPeer_HandledIdempotently()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology()); // Expects 2, 3
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            module.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, 101);
            
            module.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, 102);
            
            Assert.Empty(cmd.Acks);
            
            module.ReceiveLifecycleStatus(entity, 3, EntityLifecycle.Active, cmd, 103);
            
            Assert.Single(cmd.Acks);
        }

        [Fact]
        public void Gateway_PartialAcks_StillWaiting()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology()); // Expects 2, 3
            var entity = new Entity(1, 1);
            
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            
            module.Tick(view, 0, frameOverride: 100);
            
            module.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, 101);
            
            Assert.Empty(cmd.Acks);
            
            Assert.DoesNotContain((entity, typeof(PendingNetworkAck)), cmd.RemovedComponents);
        }

        // ==========================================
        // 3. EntityLifecycleStatusTranslator Tests
        // ==========================================
        
        [Fact]
        public void Translator_Ingress_CallsGateway()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var map = new Dictionary<long, Entity> { { 999, new Entity(1, 1) } };
            var translator = new EntityLifecycleStatusTranslator(1, module, map);
            
            var msg = new EntityLifecycleStatusDescriptor 
            { 
                NodeId = 2, 
                EntityId = 999, 
                State = EntityLifecycle.Active 
            };
            var reader = new MockDataReader(msg);
            
            var entity = new Entity(1, 1);
            view.AddComponent(entity, new PendingNetworkAck());
            view.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity });
            module.Tick(view, 0, frameOverride: 100);
            
            translator.PollIngress(reader, cmd, view);
            
            Assert.Empty(cmd.Acks);
            
             var msg2 = new EntityLifecycleStatusDescriptor 
            { 
                NodeId = 3, 
                EntityId = 999, 
                State = EntityLifecycle.Active 
            };
            var reader2 = new MockDataReader(msg2);
            translator.PollIngress(reader2, cmd, view);
            
            Assert.Single(cmd.Acks);
        }
        
        [Fact]
        public void Translator_Egress_PublishesActiveStatus()
        {
            using var repo = new EntityRepository();
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<PendingNetworkAck>();
            
            var gateway = new NetworkGatewayModule(10, 1, new MockTopology(), new EntityLifecycleModule(new[]{10}));
            var map = new Dictionary<long, Entity>();
            var translator = new EntityLifecycleStatusTranslator(1, gateway, map);
            var writer = new MockDataWriter();
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NetworkIdentity { Value = 999 });
            repo.AddComponent(entity, new PendingNetworkAck());
            repo.SetLifecycleState(entity, EntityLifecycle.Active);
            
            translator.ScanAndPublish(repo, writer);
            
            Assert.Single(writer.WrittenSamples);
            var status = (EntityLifecycleStatusDescriptor)writer.WrittenSamples[0];
            Assert.Equal(999, status.EntityId);
            Assert.Equal(EntityLifecycle.Active, status.State);
            Assert.Equal(1, status.NodeId);
        }

        [Fact]
        public void Translator_Ingress_InvalidState_DoesNotCrash()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var map = new Dictionary<long, Entity> { { 999, new Entity(1, 1) } };
            var translator = new EntityLifecycleStatusTranslator(1, module, map);
            
            var msg = new EntityLifecycleStatusDescriptor 
            { 
                NodeId = 2, 
                EntityId = 999, 
                State = (EntityLifecycle)255 // Invalid value
            };
            var reader = new MockDataReader(msg);
            
            translator.PollIngress(reader, cmd, view);
        }

        [Fact]
        public void Translator_Ingress_MultipleMessages_AllProcessed()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var entity1 = new Entity(1, 1);
            var entity2 = new Entity(2, 1);
            var map = new Dictionary<long, Entity> { { 100, entity1 }, { 200, entity2 } };
            
            var translator = new EntityLifecycleStatusTranslator(1, module, map);
            
            view.AddComponent(entity1, new PendingNetworkAck());
            view.AddComponent(entity1, new NetworkSpawnRequest { DisType = new DISEntityType() });
            view.AddComponent(entity2, new PendingNetworkAck());
            view.AddComponent(entity2, new NetworkSpawnRequest { DisType = new DISEntityType() });
            
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity1 });
            view.ConstructionOrders.Add(new ConstructionOrder { Entity = entity2 });
            module.Tick(view, 0, frameOverride: 100);
            
            var reader = new MockDataReader(
                new EntityLifecycleStatusDescriptor { NodeId = 2, EntityId = 100, State = EntityLifecycle.Active },
                new EntityLifecycleStatusDescriptor { NodeId = 2, EntityId = 200, State = EntityLifecycle.Active }
            );
            
            translator.PollIngress(reader, cmd, view);
            
            module.ReceiveLifecycleStatus(entity1, 3, EntityLifecycle.Active, cmd, 101);
            module.ReceiveLifecycleStatus(entity2, 3, EntityLifecycle.Active, cmd, 102);
            
            Assert.Equal(2, cmd.Acks.Count);
        }

        [Fact]
        public void Translator_Egress_ConstructingEntity_NotPublished()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<PendingNetworkAck>();
            
            var gateway = new NetworkGatewayModule(10, 1, new MockTopology(), new EntityLifecycleModule(new[]{10}));
            var map = new Dictionary<long, Entity>();
            var translator = new EntityLifecycleStatusTranslator(1, gateway, map);
            var writer = new MockDataWriter();
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NetworkIdentity { Value = 999 });
            repo.AddComponent(entity, new PendingNetworkAck());
            repo.SetLifecycleState(entity, EntityLifecycle.Constructing);
            
            translator.ScanAndPublish(repo, writer);
            Assert.Empty(writer.WrittenSamples);
        }

        [Fact]
        public void Translator_Egress_MultipleActiveEntities_AllPublished()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<PendingNetworkAck>();
            
            var gateway = new NetworkGatewayModule(10, 1, new MockTopology(), new EntityLifecycleModule(new[]{10}));
            var map = new Dictionary<long, Entity>();
            var translator = new EntityLifecycleStatusTranslator(1, gateway, map);
            var writer = new MockDataWriter();
            
            var entity1 = repo.CreateEntity();
            repo.AddComponent(entity1, new NetworkIdentity { Value = 100 });
            repo.AddComponent(entity1, new PendingNetworkAck());
            repo.SetLifecycleState(entity1, EntityLifecycle.Active);
            
            var entity2 = repo.CreateEntity();
            repo.AddComponent(entity2, new NetworkIdentity { Value = 200 });
            repo.AddComponent(entity2, new PendingNetworkAck());
            repo.SetLifecycleState(entity2, EntityLifecycle.Active);
            
            translator.ScanAndPublish(repo, writer);
            
            Assert.Equal(2, writer.WrittenSamples.Count);
            var ids = writer.WrittenSamples.Select(s => ((EntityLifecycleStatusDescriptor)s).EntityId).ToList();
            Assert.Contains(100L, ids);
            Assert.Contains(200L, ids);
        }

        [Fact]
        public void Translator_Ingress_IgnoresOwnMessages()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var map = new Dictionary<long, Entity> { { 999, new Entity(1, 1) } };
            var translator = new EntityLifecycleStatusTranslator(1, module, map); 
            
            var msg = new EntityLifecycleStatusDescriptor 
            { 
                NodeId = 1, 
                EntityId = 999, 
                State = EntityLifecycle.Active 
            };
            var reader = new MockDataReader(msg);
            
            translator.PollIngress(reader, cmd, view);
            
            Assert.Empty(cmd.Acks);
        }

        [Fact]
        public void Translator_Ingress_UnknownEntity_LogsAndContinues()
        {
            var (module, elm, view, cmd) = Setup(new MockTopology());
            var map = new Dictionary<long, Entity>();
            var translator = new EntityLifecycleStatusTranslator(1, module, map);
            
            var msg = new EntityLifecycleStatusDescriptor 
            { 
                NodeId = 2, 
                EntityId = 999,
                State = EntityLifecycle.Active 
            };
            var reader = new MockDataReader(msg);
            
            translator.PollIngress(reader, cmd, view);
        }

        // ==========================================
        // 4. NetworkEgressSystem Tests
        // ==========================================

        [Fact]
        public void Egress_ForcePublish_RemovesComponent()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<ForceNetworkPublish>();
            
            // Dummy gateway for test
            var dummyGateway = new NetworkGatewayModule(10, 1, new MockEmptyTopology(), new EntityLifecycleModule(new[]{10}));
            var translator = new EntityLifecycleStatusTranslator(1, dummyGateway, new Dictionary<long, Entity>());
            var writer = new MockDataWriter();
            var system = new NetworkEgressSystem(new[]{translator}, new[]{writer});
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new ForceNetworkPublish());
            
            system.Execute(repo, 0);
            ((EntityCommandBuffer)((ISimulationView)repo).GetCommandBuffer()).Playback(repo);
            
            Assert.False(((ISimulationView)repo).HasComponent<ForceNetworkPublish>(entity));
        }

        [Fact]
        public void Egress_MultipleForcePublish_AllRemoved()
        {
            using var repo = new EntityRepository();
            repo.RegisterComponent<ForceNetworkPublish>();
            
            var dummyGateway = new NetworkGatewayModule(10, 1, new MockEmptyTopology(), new EntityLifecycleModule(new[]{10}));
            var translator = new EntityLifecycleStatusTranslator(1, dummyGateway, new Dictionary<long, Entity>());
            var writer = new MockDataWriter();
            var system = new NetworkEgressSystem(new[]{translator}, new[]{writer});
            
            var entity1 = repo.CreateEntity();
            repo.AddComponent(entity1, new ForceNetworkPublish());
            
            var entity2 = repo.CreateEntity();
            repo.AddComponent(entity2, new ForceNetworkPublish());
            
            var entity3 = repo.CreateEntity();
            repo.AddComponent(entity3, new ForceNetworkPublish());
            
            system.Execute(repo, 0);
            ((EntityCommandBuffer)((ISimulationView)repo).GetCommandBuffer()).Playback(repo);
            
            Assert.False(((ISimulationView)repo).HasComponent<ForceNetworkPublish>(entity1));
            Assert.False(((ISimulationView)repo).HasComponent<ForceNetworkPublish>(entity2));
            Assert.False(((ISimulationView)repo).HasComponent<ForceNetworkPublish>(entity3));
        }

        [Fact]
        public void Egress_NoForcePublish_TranslatorsStillCalled()
        {
            var mockTranslator = new MockDescriptorTranslator();
            var writer = new MockDataWriter();
            var system = new NetworkEgressSystem(new[]{mockTranslator}, new[]{writer});
            
            using var repo = new EntityRepository();
            
            system.Execute(repo, 0);
            
            Assert.True(mockTranslator.ScanAndPublishCalled);
        }

        [Fact]
        public void Egress_TranslatorWriterMismatch_ThrowsException()
        {
            var dummyGateway = new NetworkGatewayModule(10, 1, new MockEmptyTopology(), new EntityLifecycleModule(new[]{10}));
            var translator1 = new EntityLifecycleStatusTranslator(1, dummyGateway, new Dictionary<long, Entity>());
            var translator2 = new EntityLifecycleStatusTranslator(1, dummyGateway, new Dictionary<long, Entity>());
            var writer = new MockDataWriter();
            
            Assert.Throws<ArgumentException>(() => 
                new NetworkEgressSystem(new[]{translator1, translator2}, new[]{writer}));
        }
        
        // Helper Mock
        private class MockDescriptorTranslator : IDescriptorTranslator
        {
            public bool ScanAndPublishCalled = false;
            public string TopicName => "Mock";
            
            public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view) { }
            public void ScanAndPublish(ISimulationView view, IDataWriter writer) 
            {
                ScanAndPublishCalled = true;
            }
        }
    }
}
