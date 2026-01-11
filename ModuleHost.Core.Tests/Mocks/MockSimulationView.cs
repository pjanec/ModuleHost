using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;

namespace ModuleHost.Core.Tests.Mocks
{
    public class MockSimulationView : ISimulationView
    {
        // Storage for unmanaged components (supporting ref return)
        public Dictionary<Entity, Dictionary<Type, Array>> ComponentArrays = new();
        
        // Storage for managed components
        public Dictionary<Entity, Dictionary<Type, object>> ManagedComponents = new();

        public List<ConstructionOrder> ConstructionOrders = new();
        public List<DestructionOrder> DestructionOrders = new(); // Added for BATCH-14.1
        public MockCommandBuffer CommandBuffer;
        
        public MockSimulationView(MockCommandBuffer cmd)
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

        public IEnumerable<T> ConsumeEvents<T>() where T : unmanaged
        {
            if (typeof(T) == typeof(ConstructionOrder))
            {
                var result = new List<T>();
                foreach(var order in ConstructionOrders)
                {
                    result.Add((T)(object)order);
                }
                ConstructionOrders.Clear();
                return result;
            }
            if (typeof(T) == typeof(DestructionOrder))
            {
                var result = new List<T>();
                foreach(var order in DestructionOrders)
                {
                    result.Add((T)(object)order);
                }
                DestructionOrders.Clear();
                return result;
            }
            return Enumerable.Empty<T>();
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

        public IEntityQueryBuilder Query() => new MockQueryBuilder(this);
        public void RegisterSystem(ISystem system) { }
        public void UnregisterSystem(ISystem system) { }
        public void RegisterModule(IModule module) { }
        public void UnregisterModule(IModule module) { }
    }
    
    public class MockCommandBuffer : IEntityCommandBuffer
    {
        public List<ConstructionAck> Acks = new();
        public List<(Entity, Type)> RemovedComponents = new();
        public List<object> PublishedEvents = new();

        public void PublishEvent<T>(T ev) where T : unmanaged
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
        public void AddComponent<T>(Entity entity, T component) where T : unmanaged { }
        public void AddManagedComponent<T>(Entity entity, T component) where T : class { }
        public void RemoveManagedComponent<T>(Entity entity) where T : class { }
    }
    
    public class MockQueryBuilder : IEntityQueryBuilder
    {
        private MockSimulationView _view;
        private List<Type> _withComponents = new();
        private List<EntityLifecycle> _lifecycles = new();
        private EntityLifecycle? _targetLifecycle;

        public MockQueryBuilder(MockSimulationView view) { _view = view; }

        public IEntityQueryBuilder With<T>() { _withComponents.Add(typeof(T)); return this; }
        public IEntityQueryBuilder WithLifecycle(EntityLifecycle state) { _targetLifecycle = state; return this; }
        public IEntityQueryBuilder Without<T>() => this;

        public IEnumerable<Entity> Build()
        {
            var candidates = _view.ComponentArrays.Keys.Union(_view.ManagedComponents.Keys);
            
            return candidates.Where(e => {
                foreach (var type in _withComponents)
                {
                    bool hasUnmanaged = _view.ComponentArrays.ContainsKey(e) && _view.ComponentArrays[e].ContainsKey(type);
                    bool hasManaged = _view.ManagedComponents.ContainsKey(e) && _view.ManagedComponents[e].ContainsKey(type);
                    if (!hasUnmanaged && !hasManaged) return false;
                }
                return true;
            });
        }
    }
}
