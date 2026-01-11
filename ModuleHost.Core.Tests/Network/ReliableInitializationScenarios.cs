using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Translators;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class ReliableInitializationScenarios
    {
        // === Components needed ===
        // PendingNetworkAck, NetworkSpawnRequest, NetworkIdentity, EntityLifecycleStatusDescriptor (msg)
        
        [Fact]
        public void Scenario_FullReliableInit_TwoNodes()
        {
            // Setup Repository
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            // Setup Modules
            var topo = new StaticNetworkTopology(1, new[] { 1, 2 }); // Local=1, Peer=2
            var elm = new EntityLifecycleModule(new[] { 10, 20 });   // Gateway=10, Other=20
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            
            // Other participating module (e.g. Physics)
            // We'll manually ACK for it
            
            // Initialize
            elm.RegisterModule(10);
            
            // === Step 1: Create Entity ===
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NetworkIdentity { Value = 100 });
            repo.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType { Kind = 1 } });
            repo.AddComponent(entity, new PendingNetworkAck { ExpectedType = new DISEntityType { Kind = 1 } }); // Added by Spawner in real flow
            repo.SetLifecycleState(entity, EntityLifecycle.Constructing); // Spawner sets this normally
            Assert.Equal(EntityLifecycle.Constructing, repo.GetHeader(entity.Index).LifecycleState); // Verify immediate set
            
            // ELM begins construction
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            elm.BeginConstruction(entity, 1, repo.GlobalVersion, cmd);
            ((EntityCommandBuffer)cmd).Playback(repo); // Publish ConstructionOrder
            
            // === Step 2: Gateway Processing (Tick 1) ===
            // Gateway should see ConstructionOrder and wait
            gateway.Tick(repo, 0);
            
            // Verify not ACKed yet
            // We can check internal state of ELM? No easy way. 
            // Check EntityLifecycle?
            Assert.Equal(EntityLifecycle.Constructing, repo.GetHeader(entity.Index).LifecycleState);
            
            // === Step 3: Simulate Peer ACK ===
            // Simulate receiving message from Node 2
            gateway.ReceiveLifecycleStatus(entity, 2, EntityLifecycle.Active, cmd, repo.GlobalVersion);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            // Gateway hasn't ACKed yet because it does it in ReceiveLifecycleStatus? 
            // Yes, ReceiveLifecycleStatus calls _elm.AcknowledgeConstruction immediately if all peers ACKed.
            
            // But we also need the "Other" module (20) to ACK.
            elm.AcknowledgeConstruction(entity, 20, repo.GlobalVersion, cmd);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            // ELM should process ACKs. ELM logic is inside LifecycleSystem usually. 
            // But we can call internal methods or Tick ELM if we registered LifecycleSystem?
            // ELM has internal ProcessConstructionAck.
            // We need to run ELM's system.
            // Since we didn't register ELM system to repo, we need to manually process ACKs.
            // Wait, LifecycleSystem is what calls ProcessConstructionAck.
            // Let's manually bridge the event to ELM method for test.
            
            ProcessAcks(repo, elm);
            
            // === Step 4: Verify Active ===
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(entity.Index).LifecycleState);
            Assert.False(repo.HasComponent<PendingNetworkAck>(entity)); // Should be removed by Gateway
        }
        
        [Fact]
        public void Scenario_FastMode_Works()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var topo = new StaticNetworkTopology(1, new[] { 1, 2 });
            var elm = new EntityLifecycleModule(new[] { 10 });
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            
            elm.RegisterModule(10);
            
            var entity = repo.CreateEntity();
            // No PendingNetworkAck
            
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            elm.BeginConstruction(entity, 1, repo.GlobalVersion, cmd);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            gateway.Tick(repo, 0);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            ProcessAcks(repo, elm);
            
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(entity.Index).LifecycleState);
        }
        
        [Fact]
        public void Scenario_Timeout_Works()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var topo = new StaticNetworkTopology(1, new[] { 1, 2 });
            var elm = new EntityLifecycleModule(new[] { 10 });
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NetworkSpawnRequest { DisType = new DISEntityType { Kind = 1 } });
            repo.AddComponent(entity, new PendingNetworkAck { ExpectedType = new DISEntityType { Kind = 1 } });
            
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            elm.BeginConstruction(entity, 1, repo.GlobalVersion, cmd);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            // Start Gateway (starts waiting)
            gateway.Tick(repo, 0);
            
            // Advance time
            for(int i=0; i<305; i++) repo.Tick(); // Advance GlobalVersion
            
            // Gateway check timeout
            gateway.Tick(repo, 0);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            ProcessAcks(repo, elm);
            
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(entity.Index).LifecycleState);
        }

        [Fact]
        public void Scenario_MixedEntityTypes_ReliableAndFast()
        {
            using var repo = new EntityRepository();
            RegisterComponents(repo);
            
            var topo = new StaticNetworkTopology(1, new[] { 1, 2 });
            var elm = new EntityLifecycleModule(new[] { 10 });
            var gateway = new NetworkGatewayModule(10, 1, topo, elm);
            
            // Create 3 entities
            var reliable1 = repo.CreateEntity();
            var fast1 = repo.CreateEntity();
            var fast2 = repo.CreateEntity();
            
            // Reliable entity setup
            repo.AddComponent(reliable1, new NetworkSpawnRequest { DisType = new DISEntityType { Kind = 1 } });
            repo.AddComponent(reliable1, new PendingNetworkAck { ExpectedType = new DISEntityType { Kind = 1 } }); // Reliable mode
            repo.SetLifecycleState(reliable1, EntityLifecycle.Constructing);
            Assert.Equal(EntityLifecycle.Constructing, repo.GetHeader(reliable1.Index).LifecycleState);
            
            // Fast entities - no PendingNetworkAck
            // (no additional components needed for fast mode)
            
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            
            // Begin construction for all
            elm.BeginConstruction(reliable1, 1, repo.GlobalVersion, cmd);
            elm.BeginConstruction(fast1, 2, repo.GlobalVersion, cmd);
            elm.BeginConstruction(fast2, 3, repo.GlobalVersion, cmd);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            // Gateway processes
            gateway.Tick(repo, 0);
            ((EntityCommandBuffer)cmd).Playback(repo);
            
            ProcessAcks(repo, elm);
            
            // Fast entities should be Active
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(fast1.Index).LifecycleState);
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(fast2.Index).LifecycleState);
            
            // Reliable entity still Constructing (waiting for peer)
            Assert.Equal(EntityLifecycle.Constructing, repo.GetHeader(reliable1.Index).LifecycleState);
            
            // Now peer ACKs
            gateway.ReceiveLifecycleStatus(reliable1, 2, EntityLifecycle.Active, cmd, repo.GlobalVersion);
            ((EntityCommandBuffer)cmd).Playback(repo);
            ProcessAcks(repo, elm);
            
            // Now reliable entity is Active
            Assert.Equal(EntityLifecycle.Active, repo.GetHeader(reliable1.Index).LifecycleState);
        }

        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<ForceNetworkPublish>();
            
            // Register Events
            repo.RegisterEvent<ConstructionOrder>();
            repo.RegisterEvent<ConstructionAck>();
            repo.RegisterEvent<DestructionOrder>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
        }
        
        private void ProcessAcks(EntityRepository repo, EntityLifecycleModule elm)
        {
            var cmd = ((ISimulationView)repo).GetCommandBuffer();
            var acks = ((ISimulationView)repo).ConsumeEvents<ConstructionAck>();
            // Console.WriteLine($"[TestDebug] ProcessAcks found {acks.Length} events");
            if (acks.Length == 0) 
            {
                 // Force Active if events missing (hack for debug/fix if event system failing in test)
                 // This confirms if the issue is event delivery vs ELM logic
                 // But ELM needs to clear its internal state!
            }
            
            foreach(var ack in acks)
            {
                elm.ProcessConstructionAck(ack, repo.GlobalVersion, cmd);
            }
            ((EntityCommandBuffer)cmd).Playback(repo);
        }
    }
}
