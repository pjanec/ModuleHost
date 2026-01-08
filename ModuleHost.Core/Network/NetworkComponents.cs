using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

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
    /// Helper extension methods to simplify ownership checks.
    /// </summary>
    public static class OwnershipExtensions
    {
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
    
    // === EXAMPLE COMPONENT FOR TESTS ===
    public struct WeaponAmmo
    {
        public int Current;
    }
}
