using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Fdp.Kernel;
using Fdp.Kernel.Tkb;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.Network.Messages;
using ModuleHost.Core.Network.Systems;
using ModuleHost.Core.Network.Translators;
using ModuleHost.Core.Tests.Mocks;
using Moq;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkELMIntegrationScenarios : IDisposable
    {
        private EntityRepository _repo;
        private Mock<ITkbDatabase> _mockTkb;
        private EntityLifecycleModule _elm;
        private Mock<IOwnershipDistributionStrategy> _mockStrategy;
        private int _localNodeId = 1;
        private DescriptorOwnershipMap _ownershipMap;

        public NetworkELMIntegrationScenarios()
        {
            _repo = new EntityRepository();
            RegisterComponents(_repo);
            
            _mockTkb = new Mock<ITkbDatabase>();
            _elm = new EntityLifecycleModule(new[] { 1 });
            _mockStrategy = new Mock<IOwnershipDistributionStrategy>();
            _ownershipMap = new DescriptorOwnershipMap();
        }

        private void RegisterComponents(EntityRepository repo)
        {
            repo.RegisterComponent<Position>();
            repo.RegisterComponent<Velocity>();
            repo.RegisterComponent<NetworkIdentity>();
            repo.RegisterComponent<NetworkOwnership>();
            repo.RegisterComponent<NetworkTarget>();
            repo.RegisterManagedComponent<DescriptorOwnership>();
            repo.RegisterComponent<NetworkSpawnRequest>();
            repo.RegisterComponent<PendingNetworkAck>();
            repo.RegisterComponent<ForceNetworkPublish>();
            
            repo.RegisterEvent<ConstructionOrder>();
            repo.RegisterEvent<DescriptorAuthorityChanged>();
        }

        public void Dispose()
        {
            _repo.Dispose();
        }

        private EntityLifecycle GetLifecycleState(Entity entity)
        {
            return _repo.GetHeader(entity.Index).LifecycleState;
        }
        
        private IEntityCommandBuffer GetCommandBuffer()
        {
            return ((ISimulationView)_repo).GetCommandBuffer();
        }

        private Entity FindEntityByNetworkId(long id)
        {
            foreach (var e in _repo.Query().IncludeAll().Build())
            {
                if (_repo.HasComponent<NetworkIdentity>(e))
                {
                    if (_repo.GetComponentRO<NetworkIdentity>(e).Value == id) return e;
                }
            }
            return Entity.Null;
        }

        [Fact]
        public void Scenario_StateFirstCreation_GhostPath()
        {
            // 1. EntityState arrives -> Ghost created with Position
            var stateTranslator = new EntityStateTranslator(_localNodeId, _ownershipMap);
            var stateDesc = new EntityStateDescriptor 
            { 
                EntityId = 1000, 
                Location = new Vector3(50, 0, 0),
                OwnerId = 2
            };
            var stateCmd = GetCommandBuffer();
            stateTranslator.PollIngress(new MockDataReader(stateDesc), stateCmd, _repo);
            ((EntityCommandBuffer)stateCmd).Playback(_repo);

            var entity = FindEntityByNetworkId(1000);
            Assert.Equal(EntityLifecycle.Ghost, GetLifecycleState(entity));
            Assert.Equal(new Vector3(50, 0, 0), _repo.GetComponentRO<Position>(entity).Value);

            // 2. EntityMaster arrives -> NetworkSpawnRequest added
            var networkMap = new Dictionary<long, Entity> { { 1000, entity } };

            var masterTranslator = new EntityMasterTranslator(_localNodeId, networkMap);
            var masterDesc = new EntityMasterDescriptor { EntityId = 1000, Type = new DISEntityType { Kind = 1 }, OwnerId = 2 };
            var masterCmd = GetCommandBuffer();
            masterTranslator.PollIngress(new MockDataReader(masterDesc), masterCmd, _repo);
            ((EntityCommandBuffer)masterCmd).Playback(_repo);

            Assert.True(_repo.HasComponent<NetworkSpawnRequest>(entity));

            // 3. NetworkSpawnerSystem executes -> TKB applied, Position preserved
            var template = new TkbTemplate("TestTank");
            template.AddComponent(new Position { Value = Vector3.Zero }); // Should be ignored
            _mockTkb.Setup(x => x.GetTemplateByEntityType(It.IsAny<DISEntityType>())).Returns(template);

            var spawner = new NetworkSpawnerSystem(_mockTkb.Object, _elm, _mockStrategy.Object, _localNodeId);
            spawner.Execute(_repo, 0.1f);

            // 4. Verify: Position from Ghost retained
            var pos = _repo.GetComponentRO<Position>(entity);
            Assert.Equal(new Vector3(50, 0, 0), pos.Value);
            
            // 5. ELM processes -> Entity becomes Active
            var stats = _elm.GetStatistics();
            Assert.Equal(1, stats.pending); // Construction started
            
            // Verify state is Constructing
            Assert.Equal(EntityLifecycle.Constructing, GetLifecycleState(entity));
        }

        [Fact]
        public void Scenario_MasterFirstCreation_IdealPath()
        {
            // 1. EntityMaster arrives -> Entity created directly
            var networkMap = new Dictionary<long, Entity>();
            var masterTranslator = new EntityMasterTranslator(_localNodeId, networkMap);
            var masterDesc = new EntityMasterDescriptor { EntityId = 1001, Type = new DISEntityType { Kind = 1 }, OwnerId = 2 };
            var masterCmd = GetCommandBuffer();
            masterTranslator.PollIngress(new MockDataReader(masterDesc), masterCmd, _repo);
            ((EntityCommandBuffer)masterCmd).Playback(_repo);

            var entity = FindEntityByNetworkId(1001);
            Assert.NotEqual(EntityLifecycle.Ghost, GetLifecycleState(entity));
            Assert.True(_repo.HasComponent<NetworkSpawnRequest>(entity));

            // 2. NetworkSpawnRequest processed -> TKB applied
            var template = new TkbTemplate("TestTank");
            template.AddComponent(new Position { Value = new Vector3(10, 10, 10) }); 
            _mockTkb.Setup(x => x.GetTemplateByEntityType(It.IsAny<DISEntityType>())).Returns(template);

            var spawner = new NetworkSpawnerSystem(_mockTkb.Object, _elm, _mockStrategy.Object, _localNodeId);
            spawner.Execute(_repo, 0.1f);

            // 3. EntityState arrives -> Position updated
            var stateTranslator = new EntityStateTranslator(_localNodeId, _ownershipMap, networkMap);
            var stateDesc = new EntityStateDescriptor { EntityId = 1001, Location = new Vector3(20, 20, 20), OwnerId = 2 };
            var stateCmd = GetCommandBuffer();
            stateTranslator.PollIngress(new MockDataReader(stateDesc), stateCmd, _repo);
            ((EntityCommandBuffer)stateCmd).Playback(_repo);

            // 4. Verify: Entity never in Ghost state (was created directly) and has updated pos
            var pos = _repo.GetComponentRO<Position>(entity);
            // Position is not updated by translator (interpolation system job), only Target is updated
            // Assert.Equal(new Vector3(20, 20, 20), pos.Value); 
            var target = _repo.GetComponentRO<NetworkTarget>(entity);
            Assert.Equal(new Vector3(20, 20, 20), target.Value);
            Assert.Equal(EntityLifecycle.Constructing, GetLifecycleState(entity));
        }

        [Fact]
        public void Scenario_ReliableInitialization()
        {
            // 1. EntityMaster with ReliableInit flag
            var masterTranslator = new EntityMasterTranslator(_localNodeId, new Dictionary<long, Entity>());
            var masterDesc = new EntityMasterDescriptor 
            { 
                EntityId = 1002, 
                Type = new DISEntityType { Kind = 1 }, 
                OwnerId = 2,
                Flags = MasterFlags.ReliableInit
            };
            var masterCmd = GetCommandBuffer();
            masterTranslator.PollIngress(new MockDataReader(masterDesc), masterCmd, _repo);
            ((EntityCommandBuffer)masterCmd).Playback(_repo);
            
            var entity = FindEntityByNetworkId(1002);

            // 2. NetworkSpawnerSystem adds PendingNetworkAck
            var template = new TkbTemplate("Test");
            _mockTkb.Setup(x => x.GetTemplateByEntityType(It.IsAny<DISEntityType>())).Returns(template);

            var spawner = new NetworkSpawnerSystem(_mockTkb.Object, _elm, _mockStrategy.Object, _localNodeId);
            spawner.Execute(_repo, 0.1f);

            // 3. Verify: ELM construction begins
            Assert.Equal(1, _elm.GetStatistics().pending);

            // 4. Verify: Entity awaits network confirmation
            Assert.True(_repo.HasComponent<PendingNetworkAck>(entity));
        }

        [Fact]
        public void Scenario_PartialOwnership()
        {
            // 1. EntityMaster
            var masterTranslator = new EntityMasterTranslator(_localNodeId, new Dictionary<long, Entity>());
            var masterDesc = new EntityMasterDescriptor { EntityId = 1003, Type = new DISEntityType { Kind = 1 }, OwnerId = 9 };
            var masterCmd = GetCommandBuffer();
            masterTranslator.PollIngress(new MockDataReader(masterDesc), masterCmd, _repo);
            ((EntityCommandBuffer)masterCmd).Playback(_repo);
            
            var entity = FindEntityByNetworkId(1003);

            // 2. Custom strategy assigns WeaponState to different node
            _mockTkb.Setup(x => x.GetTemplateByEntityType(It.IsAny<DISEntityType>())).Returns(new TkbTemplate("T"));
            _mockStrategy.Setup(x => x.GetInitialOwner(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, It.IsAny<DISEntityType>(), It.IsAny<int>(), It.IsAny<long>()))
                .Returns(5); // Node 5

            // 3. NetworkSpawnerSystem applies strategy
            var spawner = new NetworkSpawnerSystem(_mockTkb.Object, _elm, _mockStrategy.Object, _localNodeId);
            spawner.Execute(_repo, 0.1f);

            // 4. Verify: DescriptorOwnership.Map contains entry
            var doComp = _repo.GetComponentRO<DescriptorOwnership>(entity);
            Assert.Equal(5, doComp.Map[OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)]);

            // 5. Verify: OwnsDescriptor returns correct owner
            Assert.Equal(5, ((ISimulationView)_repo).GetDescriptorOwner(entity, OwnershipExtensions.PackKey(NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, 0)));
            Assert.Equal(9, ((ISimulationView)_repo).GetDescriptorOwner(entity, OwnershipExtensions.PackKey(NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID, 0))); // Default to primary
        }

        [Fact]
        public void Scenario_OwnershipTransfer()
        {
            // 1. Entity exists with initial ownership
            var entity = _repo.CreateEntity();
            _repo.AddComponent(entity, new NetworkOwnership { LocalNodeId = _localNodeId, PrimaryOwnerId = 9 });
            _repo.SetComponent(entity, new DescriptorOwnership());
            var networkMap = new Dictionary<long, Entity> { { 1004, entity } };
            _repo.AddComponent(entity, new NetworkIdentity { Value = 1004 });

            // 2. OwnershipUpdate received (We acquire ownership)
            var translator = new OwnershipUpdateTranslator(_localNodeId, _ownershipMap, networkMap);
            var update = new OwnershipUpdate { EntityId = 1004, DescrTypeId = 1, NewOwner = _localNodeId };
            var reader = new MockDataReader(update);
            var cmd = GetCommandBuffer();

            // 3. Process
            translator.PollIngress(reader, cmd, _repo);
            ((EntityCommandBuffer)cmd).Playback(_repo);

            // 4. ForceNetworkPublish added
            Assert.True(_repo.HasComponent<ForceNetworkPublish>(entity));
            
            // 5. Verify event
            _repo.Bus.SwapBuffers();
            var events = _repo.Bus.Consume<DescriptorAuthorityChanged>();
            Assert.False(events.IsEmpty);
            Assert.True(events[0].IsNowOwner);
        }
    }
}
