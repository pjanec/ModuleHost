using System;
using Fdp.Kernel;
using Fdp.Kernel.Tkb;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network.Systems
{
    /// <summary>
    /// System that processes NetworkSpawnRequest components and coordinates
    /// entity spawning between the network layer and Entity Lifecycle Manager (ELM).
    /// 
    /// Responsibilities:
    /// - Apply TKB templates based on DIS entity type
    /// - Determine descriptor ownership via strategy pattern
    /// - Promote Ghost entities to Constructing state
    /// - Call ELM.BeginConstruction() to start distributed construction
    /// - Handle reliable vs fast initialization modes
    /// </summary>
    public class NetworkSpawnerSystem
    {
        private readonly ITkbDatabase _tkbDatabase;
        private readonly EntityLifecycleModule _elm;
        private readonly IOwnershipDistributionStrategy _ownershipStrategy;
        private readonly int _localNodeId;
        
        public NetworkSpawnerSystem(
            ITkbDatabase tkbDatabase,
            EntityLifecycleModule elm,
            IOwnershipDistributionStrategy ownershipStrategy,
            int localNodeId)
        {
            _tkbDatabase = tkbDatabase ?? throw new ArgumentNullException(nameof(tkbDatabase));
            _elm = elm ?? throw new ArgumentNullException(nameof(elm));
            _ownershipStrategy = ownershipStrategy ?? throw new ArgumentNullException(nameof(ownershipStrategy));
            _localNodeId = localNodeId;
        }
        
        /// <summary>
        /// Execute the spawner system. Should run in INPUT or BEFORESYNC phase,
        /// after network ingress but before ELM lifecycle processing.
        /// </summary>
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Cast for direct repository access (spawner needs immediate mutations)
            var repo = view as EntityRepository;
            if (repo == null)
            {
                throw new InvalidOperationException(
                    "NetworkSpawnerSystem requires EntityRepository access.");
            }
            
            // Query entities with NetworkSpawnRequest (transient component)
            var query = repo.Query()
                .With<NetworkSpawnRequest>()
                .IncludeAll()  // Include Ghost entities
                .Build();
            
            // Get command buffer and current frame for ELM interactions
            var cmd = view.GetCommandBuffer();
            uint currentFrame = repo.GlobalVersion;
            
            foreach (var entity in query)
            {
                var request = repo.GetComponentRO<NetworkSpawnRequest>(entity);
                
                try
                {
                    ProcessSpawnRequest(repo, entity, request, cmd, currentFrame);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[NetworkSpawnerSystem] Error processing entity {entity.Index}: {ex.Message}");
                }
                finally
                {
                    // Remove transient component (consumed)
                    repo.RemoveComponent<NetworkSpawnRequest>(entity);
                }
            }
        }
        
        private void ProcessSpawnRequest(
            EntityRepository repo, 
            Entity entity, 
            NetworkSpawnRequest request,
            IEntityCommandBuffer cmd,
            uint currentFrame)
        {
            // Use GetHeader to access lifecycle state directly
            ref var header = ref repo.GetHeader(entity.Index);
            var currentState = header.LifecycleState;
            bool wasGhost = (currentState == EntityLifecycle.Ghost);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Processing entity {entity.Index} (State: {currentState}, Type: {request.DisType.Kind})");
            
            // 1. Get TKB template
            var template = _tkbDatabase.GetTemplateByEntityType(request.DisType);
            if (template == null)
            {
                Console.Error.WriteLine($"[NetworkSpawnerSystem] No TKB template found for entity type {request.DisType.Kind}. Skipping entity {entity.Index}.");
                return;
            }
            
            // 2. Apply TKB template
            // If entity was Ghost, preserve existing components (Position from EntityState)
            // If entity is new (Master-first), apply template normally
            bool preserveExisting = wasGhost;
            template.ApplyTo(repo, entity, preserveExisting);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Applied TKB template '{template.Name}' to entity {entity.Index} (preserveExisting: {preserveExisting})");
            
            // 3. Determine partial ownership using strategy
            DetermineDescriptorOwnership(repo, entity, request);
            
            // 4. Promote to Constructing state (if Ghost or new)
            if (currentState != EntityLifecycle.Constructing)
            {
                repo.SetLifecycleState(entity, EntityLifecycle.Constructing);
                Console.WriteLine($"[NetworkSpawnerSystem] Promoted entity {entity.Index} from {currentState} to Constructing");
            }
            
            // 5. Begin ELM construction
            // The TypeId for ELM should be derived from template name (hash) or DIS type
            int elmTypeId = GetTemplateId(template.Name);
            
            _elm.BeginConstruction(entity, elmTypeId, currentFrame, cmd);
            
            Console.WriteLine($"[NetworkSpawnerSystem] Called ELM.BeginConstruction for entity {entity.Index} (TypeId: {elmTypeId})");
            
            // 6. Handle reliable init mode
            if (request.Flags.HasFlag(MasterFlags.ReliableInit))
            {
                // Add PendingNetworkAck tag - NetworkGateway will wait for peer ACKs
                repo.AddComponent(entity, new PendingNetworkAck());
                Console.WriteLine($"[NetworkSpawnerSystem] Entity {entity.Index} marked for reliable init");
            }
        }
        
        private void DetermineDescriptorOwnership(EntityRepository repo, Entity entity, NetworkSpawnRequest request)
        {
            // Get or create DescriptorOwnership component
            DescriptorOwnership descOwnership;
            if (repo.HasManagedComponent<DescriptorOwnership>(entity))
            {
                // Clone for mutation
                var existing = repo.GetManagedComponentRO<DescriptorOwnership>(entity);
                descOwnership = new DescriptorOwnership 
                { 
                    Map = new System.Collections.Generic.Dictionary<long, int>(existing.Map) 
                };
            }
            else
            {
                descOwnership = new DescriptorOwnership();
            }
            
            // Determine ownership for each descriptor type
            // For now, we handle the standard descriptors: EntityState, EntityMaster, WeaponState
            
            // 1. EntityMaster (always single instance 0)
            AssignDescriptorOwnership(descOwnership, NetworkConstants.ENTITY_MASTER_DESCRIPTOR_ID, request, 0);
            
            // 2. EntityState (always single instance 0)
            AssignDescriptorOwnership(descOwnership, NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID, request, 0);
            
            // 3. WeaponState (multi-instance)
            int weaponInstanceCount = GetWeaponInstanceCount(request.DisType);
            for (int i = 0; i < weaponInstanceCount; i++)
            {
                AssignDescriptorOwnership(descOwnership, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID, request, i);
            }
            
            // Update component
            repo.SetManagedComponent(entity, descOwnership);
        }
        
        private void AssignDescriptorOwnership(DescriptorOwnership descOwnership, long descriptorTypeId, NetworkSpawnRequest request, int instanceId)
        {
            // Ask strategy for initial owner
            int? strategyOwner = _ownershipStrategy.GetInitialOwner(
                descriptorTypeId,
                request.DisType,
                request.PrimaryOwnerId,
                instanceId: instanceId
            );
            
            int owner = strategyOwner ?? request.PrimaryOwnerId;
            
            // Only populate map if different from primary owner (saves memory)
            if (owner != request.PrimaryOwnerId)
            {
                long key = OwnershipExtensions.PackKey(descriptorTypeId, instanceId);
                descOwnership.Map[key] = owner;
                
                Console.WriteLine($"[NetworkSpawnerSystem] Descriptor {descriptorTypeId}:{instanceId} owned by node {owner} (partial ownership)");
            }
        }

        private int GetWeaponInstanceCount(DISEntityType type)
        {
            // Simple heuristic: Could be configured via TKB template metadata
            // For now, hardcode based on entity kind
            switch (type.Kind)
            {
                case 1: // Platform/Tank
                    return type.Category == 1 ? 2 : 1; // Main battle tank = 2 weapons, others = 1
                default:
                    return 0; // No weapons
            }
        }
        
        private int GetTemplateId(string templateName)
        {
            // Simple hash - in production, use stable hash function
            // For now, use GetHashCode (sufficient for demo)
            return templateName.GetHashCode();
        }
    }
}
