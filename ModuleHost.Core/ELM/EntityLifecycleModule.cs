using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Coordinates entity lifecycle across distributed modules.
    /// Ensures entities are fully initialized before becoming Active,
    /// and properly cleaned up before destruction.
    /// </summary>
    public class EntityLifecycleModule : IModule
    {
        public string Name => "EntityLifecycleManager";
        
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        // Reactive: listen for ACK events
        public IReadOnlyList<Type>? WatchEvents => new[]
        {
            typeof(ConstructionAck),
            typeof(DestructionAck)
        };
        
        public IReadOnlyList<Type>? WatchComponents => null;
        
        // === Configuration ===
        
        /// <summary>
        /// IDs of modules that participate in lifecycle coordination.
        /// Entity becomes Active when all modules ACK.
        /// </summary>
        private HashSet<int> _participatingModuleIds;
        
        /// <summary>
        /// Timeout in frames before giving up on pending entity.
        /// </summary>
        private readonly int _timeoutFrames;
        
        // === State Tracking ===
        
        private readonly Dictionary<Entity, PendingConstruction> _pendingConstruction = new();
        private readonly Dictionary<Entity, PendingDestruction> _pendingDestruction = new();
        
        // === Statistics ===
        
        private int _totalConstructed;
        private int _totalDestructed;
        private int _timeouts;
        
        public EntityLifecycleModule(
            IEnumerable<int> participatingModuleIds,
            int timeoutFrames = 300) // 5 seconds at 60Hz
        {
            _participatingModuleIds = new HashSet<int>(participatingModuleIds);
            _timeoutFrames = timeoutFrames;
        }
        
        public void RegisterSystems(ISystemRegistry registry)
        {
            registry.RegisterSystem(new LifecycleSystem(this));
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            // Main logic in LifecycleSystem
        }
        
        // === Public API ===
        
        public void RegisterModule(int moduleId)
        {
            _participatingModuleIds.Add(moduleId);
        }

        public void UnregisterModule(int moduleId)
        {
            _participatingModuleIds.Remove(moduleId);
        }

        public void AcknowledgeConstruction(Entity entity, int moduleId, uint frame, IEntityCommandBuffer cmd)
        {
            cmd.PublishEvent(new ConstructionAck
            {
                Entity = entity,
                ModuleId = moduleId,
                Success = true
            });
        }
        
        /// <summary>
        /// Begins construction of a new entity.
        /// Publishes ConstructionOrder and tracks pending ACKs.
        /// </summary>
        public void BeginConstruction(Entity entity, int typeId, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (_pendingConstruction.ContainsKey(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity.Index} already in construction");
            }
            
            // Track pending state
            _pendingConstruction[entity] = new PendingConstruction
            {
                Entity = entity,
                TypeId = typeId,
                StartFrame = currentFrame,
                RemainingAcks = new HashSet<int>(_participatingModuleIds)
            };
            
            // Publish order event
            cmd.PublishEvent(new ConstructionOrder
            {
                Entity = entity,
                TypeId = typeId,
                FrameNumber = currentFrame
            });
        }
        
        /// <summary>
        /// Begins teardown of an entity.
        /// Publishes DestructionOrder and tracks pending ACKs.
        /// </summary>
        public void BeginDestruction(Entity entity, uint currentFrame, FixedString64 reason, IEntityCommandBuffer cmd)
        {
            if (_pendingDestruction.ContainsKey(entity))
            {
                return; // Already in teardown
            }
            
            _pendingDestruction[entity] = new PendingDestruction
            {
                Entity = entity,
                StartFrame = currentFrame,
                RemainingAcks = new HashSet<int>(_participatingModuleIds),
                Reason = reason
            };
            
            cmd.PublishEvent(new DestructionOrder
            {
                Entity = entity,
                FrameNumber = currentFrame,
                Reason = reason
            });
        }

        // Overload for string reason (convenience)
        public void BeginDestruction(Entity entity, uint currentFrame, string reason, IEntityCommandBuffer cmd)
        {
             BeginDestruction(entity, currentFrame, new FixedString64(reason), cmd);
        }
        
        // === Internal Logic (called by LifecycleSystem) ===
        
        internal void ProcessConstructionAck(ConstructionAck ack, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (!_pendingConstruction.TryGetValue(ack.Entity, out var pending))
            {
                // ACK for non-pending entity (duplicate or late ACK)
                return;
            }
            
            if (!ack.Success)
            {
                // Module failed to initialize - abort construction
                Console.Error.WriteLine(
                    $"[ELM] Construction failed for {ack.Entity.Index}: {ack.ErrorMessage}");
                
                _pendingConstruction.Remove(ack.Entity);
                cmd.DestroyEntity(ack.Entity);
                return;
            }
            
            // Record ACK
            pending.RemainingAcks.Remove(ack.ModuleId);
            
            if (pending.RemainingAcks.Count == 0)
            {
                // All ACKs received - activate entity
                cmd.SetLifecycleState(ack.Entity, EntityLifecycle.Active);
                _pendingConstruction.Remove(ack.Entity);
                _totalConstructed++;
                
                Console.WriteLine(
                    $"[ELM] Entity {ack.Entity.Index} activated after {currentFrame - pending.StartFrame} frames");
            }
        }
        
        internal void ProcessDestructionAck(DestructionAck ack, uint currentFrame, IEntityCommandBuffer cmd)
        {
            if (!_pendingDestruction.TryGetValue(ack.Entity, out var pending))
            {
                return;
            }
            
            pending.RemainingAcks.Remove(ack.ModuleId);
            
            if (pending.RemainingAcks.Count == 0)
            {
                // All ACKs received - destroy entity
                cmd.DestroyEntity(ack.Entity);
                _pendingDestruction.Remove(ack.Entity);
                _totalDestructed++;
                
                Console.WriteLine(
                    $"[ELM] Entity {ack.Entity.Index} destroyed after {currentFrame - pending.StartFrame} frames");
            }
        }
        
        internal void CheckTimeouts(uint currentFrame, IEntityCommandBuffer cmd)
        {
            // Check construction timeouts
            var timedOutConstruction = new List<Entity>();
            foreach (var kvp in _pendingConstruction)
            {
                if (currentFrame - kvp.Value.StartFrame > _timeoutFrames)
                {
                    timedOutConstruction.Add(kvp.Key);
                }
            }
            
            foreach (var entity in timedOutConstruction)
            {
                var pending = _pendingConstruction[entity];
                Console.Error.WriteLine(
                    $"[ELM] Construction timeout for {entity.Index}. Missing ACKs from modules: {string.Join(", ", pending.RemainingAcks)}");
                
                _pendingConstruction.Remove(entity);
                cmd.DestroyEntity(entity);
                _timeouts++;
            }
            
            // Check destruction timeouts (similar logic)
            var timedOutDestruction = new List<Entity>();
            foreach (var kvp in _pendingDestruction)
            {
                if (currentFrame - kvp.Value.StartFrame > _timeoutFrames)
                {
                    timedOutDestruction.Add(kvp.Key);
                }
            }
            
            foreach (var entity in timedOutDestruction)
            {
                Console.Error.WriteLine(
                    $"[ELM] Destruction timeout for {entity.Index}. Forcing deletion.");
                
                _pendingDestruction.Remove(entity);
                cmd.DestroyEntity(entity);
                _timeouts++;
            }
        }
        
        // === Diagnostics ===
        
        public (int constructed, int destructed, int timeouts, int pending) GetStatistics()
        {
            return (_totalConstructed, _totalDestructed, _timeouts, 
                    _pendingConstruction.Count + _pendingDestruction.Count);
        }
    }
    
    // === Helper Classes ===
    
    internal class PendingConstruction
    {
        public Entity Entity;
        public int TypeId;
        public uint StartFrame;
        public HashSet<int> RemainingAcks = new();
    }
    
    internal class PendingDestruction
    {
        public Entity Entity;
        public uint StartFrame;
        public HashSet<int> RemainingAcks = new();
        public FixedString64 Reason;
    }
}
