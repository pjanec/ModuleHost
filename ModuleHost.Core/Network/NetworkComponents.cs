using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network
{
    // === DESCRIPTOR DEFINITIONS ===
    
    public class EntityStateDescriptor
    {
        public long EntityId { get; set; }
        public int OwnerId { get; set; } // Primary owner hint
        public Vector3 Location { get; set; }
        public Vector3 Velocity { get; set; }
        public long Timestamp { get; set; }
    }
    
    // === FDP COMPONENTS ===
    
    public struct Position
    {
        public Vector3 Value;
    }
    
    public struct Velocity
    {
        public Vector3 Value;
    }
    
    /// <summary>
    /// Tracks primary network type ownership.
    /// Unmanaged component (can be used in Queries).
    /// </summary>
    public struct NetworkOwnership
    {
        public int PrimaryOwnerId; // Default owner (EntityMaster)
        public int LocalNodeId;    // To verify ownership quickly
        
        // Removed Dictionary to keep this unmanaged for Query support.
        // Partial ownership is stored in DescriptorOwnership (Managed component).
    }
    
    /// <summary>
    /// Managed component to store partial ownership map.
    /// Separate from NetworkOwnership to allow NetworkOwnership to be unmanaged/queryable.
    /// </summary>
    public class DescriptorOwnership
    {
        public Dictionary<long, int> Map { get; set; } = new Dictionary<long, int>();
    }
    
    /// <summary>
    /// Transient component added by EntityMasterTranslator when a new EntityMaster
    /// descriptor arrives. Consumed by NetworkSpawnerSystem in the same frame.
    /// </summary>
    public struct NetworkSpawnRequest
    {
        /// <summary>DIS entity type from EntityMaster descriptor</summary>
        public DISEntityType DisType;
        
        /// <summary>Primary owner node ID (EntityMaster owner)</summary>
        public int PrimaryOwnerId;
        
        /// <summary>Master flags (ReliableInit, etc.)</summary>
        public MasterFlags Flags;
        
        /// <summary>Network entity ID for mapping</summary>
        public long NetworkEntityId;
    }

    /// <summary>
    /// Transient tag component for entities awaiting network acknowledgment
    /// in reliable initialization mode. Removed after publishing lifecycle status.
    /// </summary>
    public struct PendingNetworkAck { }

    /// <summary>
    /// Tag component to force immediate network publication of owned descriptors,
    /// bypassing normal change detection. Used for ownership transfer confirmations.
    /// </summary>
    public struct ForceNetworkPublish { }

    /// <summary>
    /// Weapon state component storing multi-instance weapon data.
    /// Multiple weapons stored as dictionary keyed by instance ID.
    /// </summary>
    public class WeaponStates
    {
        /// <summary>
        /// Maps weapon instance ID -> weapon state.
        /// Instance 0 = primary weapon, Instance 1+ = secondary weapons.
        /// </summary>
        public Dictionary<long, WeaponState> Weapons { get; set; } = new();
    }

    public struct WeaponState
    {
        public float AzimuthAngle;
        public float ElevationAngle;
        public int AmmoCount;
        public WeaponStatus Status;
    }

    /// <summary>
    /// Event emitted when descriptor ownership changes (via OwnershipUpdate message).
    /// Allows modules to react to ownership transfers.
    /// </summary>
    [EventId(9010)]
    public struct DescriptorAuthorityChanged
    {
        public Entity Entity;
        public long DescriptorTypeId;
        
        /// <summary>True if this node acquired ownership, false if lost</summary>
        public bool IsNowOwner;
        
        /// <summary>New owner node ID</summary>
        public int NewOwnerId;
    }

    /// <summary>
    /// Helper extension methods to simplify ownership checks.
    /// </summary>
    public static class OwnershipExtensions
    {
        /// <summary>
        /// Packs descriptor type ID and instance ID into a single long key.
        /// Format: [TypeId: bits 63-32][InstanceId: bits 31-0]
        /// </summary>
        public static long PackKey(long descriptorTypeId, long instanceId)
        {
            return (descriptorTypeId << 32) | (uint)instanceId;
        }

        /// <summary>
        /// Unpacks a composite key into descriptor type ID and instance ID.
        /// </summary>
        public static (long TypeId, long InstanceId) UnpackKey(long packedKey)
        {
            long typeId = packedKey >> 32;
            long instanceId = (uint)(packedKey & 0xFFFFFFFF);
            return (typeId, instanceId);
        }

        /// <summary>
        /// Overload of OwnsDescriptor that accepts separate typeId and instanceId.
        /// Packs them internally before lookup.
        /// </summary>
        public static bool OwnsDescriptor(this ISimulationView view, Entity entity, 
            long descriptorTypeId, long instanceId)
        {
            long packedKey = PackKey(descriptorTypeId, instanceId);
            return OwnsDescriptor(view, entity, packedKey);
        }

        public static bool OwnsDescriptor(this ISimulationView view, Entity entity, long descriptorTypeId)
        {
            if (!view.HasComponent<NetworkOwnership>(entity)) return false;
            
            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
            
            // Check managed map if exists
            if (view.HasManagedComponent<DescriptorOwnership>(entity))
            {
                var descOwnership = view.GetManagedComponentRO<DescriptorOwnership>(entity);
                if (descOwnership.Map.TryGetValue(descriptorTypeId, out var owner))
                {
                    return owner == ownership.LocalNodeId;
                }
            }
            
            // Fallback to Primary
            return ownership.PrimaryOwnerId == ownership.LocalNodeId;
        }

        public static int GetDescriptorOwner(this ISimulationView view, Entity entity, long descriptorTypeId)
        {
             if (!view.HasComponent<NetworkOwnership>(entity)) return 0;
            
            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
            
             // Check managed map
            if (view.HasManagedComponent<DescriptorOwnership>(entity))
            {
                var descOwnership = view.GetManagedComponentRO<DescriptorOwnership>(entity);
                if (descOwnership.Map.TryGetValue(descriptorTypeId, out var owner))
                {
                    return owner;
                }
            }
            
            return ownership.PrimaryOwnerId;
        }
    }
    
    public struct NetworkTarget
    {
        public Vector3 Value;
        public long Timestamp;
    }

    public struct NetworkIdentity
    {
        public long Value;
    }
}
