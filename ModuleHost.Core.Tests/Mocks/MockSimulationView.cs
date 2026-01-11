using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;

namespace ModuleHost.Core.Tests.Mocks
{
    public class TestMockView : ISimulationView
    {
        // Storage for unmanaged components (supporting ref return)
        public Dictionary<Entity, Dictionary<Type, Array>> ComponentArrays = new();
        
        // Storage for managed components
        public Dictionary<Entity, Dictionary<Type, object>> ManagedComponents = new();

        public List<ConstructionOrder> ConstructionOrders = new();
        public List<DestructionOrder> DestructionOrders = new();
        public MockCommandBuffer CommandBuffer;
        
        // ISimulationView properties
        public uint Tick => 0;
        public float Time => 0f;
        
        public TestMockView(MockCommandBuffer cmd)
        {
            CommandBuffer = cmd;
        }

        public void AddComponent<T>(Entity entity, T value) where T : unmanaged
        {
            if(!ComponentArrays.ContainsKey(entity)) ComponentArrays[entity] = new();
            ComponentArrays[entity][typeof(T)] = new T[] { value };
        }

        public void AddManagedComponent<T>(Entity entity, T value) where T : class
        {
            if(!ManagedComponents.ContainsKey(entity)) ManagedComponents[entity] = new();
            ManagedComponents[entity][typeof(T)] = value;
        }

        public IEntityCommandBuffer GetCommandBuffer() => CommandBuffer;

        public ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged
        {
            if (typeof(T) == typeof(ConstructionOrder))
            {
                var result = new List<T>();
                foreach(var order in ConstructionOrders)
                {
                    result.Add((T)(object)order);
                }
                ConstructionOrders.Clear();
                return CollectionsMarshal.AsSpan(result);
            }
            if (typeof(T) == typeof(DestructionOrder))
            {
                var result = new List<T>();
                foreach(var order in DestructionOrders)
                {
                    result.Add((T)(object)order);
                }
                DestructionOrders.Clear();
                return CollectionsMarshal.AsSpan(result);
            }
            return ReadOnlySpan<T>.Empty;
        }

        public bool HasComponent<T>(Entity entity) where T : unmanaged
        {
            return ComponentArrays.ContainsKey(entity) && ComponentArrays[entity].ContainsKey(typeof(T));
        }
        
        public ref readonly T GetComponentRO<T>(Entity entity) where T : unmanaged
        {
            var arr = (T[])ComponentArrays[entity][typeof(T)];
            return ref arr[0];
        }

        public T GetManagedComponentRO<T>(Entity entity) where T : class
        {
            return (T)ManagedComponents[entity][typeof(T)];
        }
        
        public bool HasManagedComponent<T>(Entity entity) where T : class
        {
            return ManagedComponents.ContainsKey(entity) && ManagedComponents[entity].ContainsKey(typeof(T));
        }

        public QueryBuilder Query() 
        {
            return null!; 
        }

        public bool IsAlive(Entity entity) => ComponentArrays.ContainsKey(entity) || ManagedComponents.ContainsKey(entity);
        
        public IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class => Array.Empty<T>();
    }
    
    public class MockCommandBuffer : IEntityCommandBuffer
    {
        public List<ConstructionAck> Acks = new();
        public List<(Entity, Type)> RemovedComponents = new();
        public List<object> PublishedEvents = new();
        public List<(Entity, object)> AddedManagedComponents = new();

        public void PublishEvent<T>(in T ev) where T : unmanaged
        {
            if (ev is ConstructionAck ack) Acks.Add(ack);
            PublishedEvents.Add(ev);
        }

        public void RemoveComponent<T>(Entity entity) where T : unmanaged
        {
            RemovedComponents.Add((entity, typeof(T)));
        }

        public void DestroyEntity(Entity entity) { }
        public void SetLifecycleState(Entity entity, EntityLifecycle state) { }
        public void AddComponent<T>(Entity entity, in T component) where T : unmanaged { }
        
        public void AddManagedComponent<T>(Entity entity, T? component) where T : class 
        {
            if (component != null)
                AddedManagedComponents.Add((entity, component));
        }
        
        public void RemoveManagedComponent<T>(Entity entity) where T : class { }
        
        // Missing members implemented as stubs
        public Entity CreateEntity() => new Entity();
        public void SetComponent<T>(Entity entity, in T component) where T : unmanaged { }
        public void SetManagedComponent<T>(Entity entity, T? component) where T : class 
        {
             if (component != null)
                AddedManagedComponents.Add((entity, component)); // Treat set as add for mock
        }
    }
}
