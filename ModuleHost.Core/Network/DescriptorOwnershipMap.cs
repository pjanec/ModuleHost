using System;
using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Maps SST descriptor types to FDP component types for ownership tracking.
    /// Bridges SST's descriptor-level ownership with FDP's component-level ownership.
    /// </summary>
    public class DescriptorOwnershipMap
    {
        private readonly Dictionary<long, Type[]> _descriptorToComponents = new();
        private readonly Dictionary<Type, long> _componentToDescriptor = new();
        
        /// <summary>
        /// Register which FDP components correspond to a descriptor type.
        /// </summary>
        /// <param name="descriptorTypeId">SST descriptor type ID</param>
        /// <param name="componentTypes">FDP components affected by this descriptor</param>
        public void RegisterMapping(long descriptorTypeId, params Type[] componentTypes)
        {
            _descriptorToComponents[descriptorTypeId] = componentTypes;
            foreach (var type in componentTypes)
            {
                _componentToDescriptor[type] = descriptorTypeId;
            }
        }
        
        /// <summary>
        /// Get FDP component types for a descriptor.
        /// </summary>
        public Type[] GetComponentsForDescriptor(long descriptorTypeId)
        {
            return _descriptorToComponents.TryGetValue(descriptorTypeId, out var types) 
                ? types 
                : Array.Empty<Type>();
        }
        
        /// <summary>
        /// Get descriptor type ID for a component (if it's network-replicated).
        /// </summary>
        public long GetDescriptorForComponent(Type componentType)
        {
            return _componentToDescriptor.TryGetValue(componentType, out var id) ? id : 0;
        }
    }
}
