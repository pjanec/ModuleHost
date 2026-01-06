using System;
using System.Collections.Generic;
using Xunit;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Scheduling;
using Fdp.Kernel; // For Entity, CommandBuffer etc.

using UpdateAfterAttribute = Fdp.Kernel.UpdateAfterAttribute;
using UpdateBeforeAttribute = Fdp.Kernel.UpdateBeforeAttribute;
using UpdateInPhaseAttribute = ModuleHost.Core.Abstractions.UpdateInPhaseAttribute;

namespace ModuleHost.Core.Tests
{
    public class SystemSchedulerTests
    {
        [Fact]
        public void TopologicalSort_SimpleChain_CorrectOrder()
        {
            var scheduler = new SystemScheduler();
            var executionLog = new List<string>();
            
            var systemA = new TrackingSystemA(executionLog);
            var systemB = new TrackingSystemB(executionLog);
            var systemC = new TrackingSystemC(executionLog);
            
            scheduler.RegisterSystem(systemA);
            scheduler.RegisterSystem(systemB);
            scheduler.RegisterSystem(systemC);
            
            scheduler.BuildExecutionOrders();
            
            var mockView = new MockSimulationView();
            scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
            
            // CRITICAL: Verify actual execution order
            Assert.Equal(3, executionLog.Count);
            Assert.Equal("A", executionLog[0]);
            Assert.Equal("B", executionLog[1]);
            Assert.Equal("C", executionLog[2]);
        }
        
        [Fact]
        public void CircularDependency_ThrowsException()
        {
            var scheduler = new SystemScheduler();
            
            scheduler.RegisterSystem(new CircularSystemA());
            scheduler.RegisterSystem(new CircularSystemB());
            
            Assert.Throws<CircularDependencyException>(() => 
                scheduler.BuildExecutionOrders());
        }
        
        [Fact]
        public void CrossPhaseDependency_Ignored()
        {
            var scheduler = new SystemScheduler();
            
            scheduler.RegisterSystem(new InputSystem());
            scheduler.RegisterSystem(new ExportSystemDependingOnInput());
            
            // Should not throw - cross-phase deps ignored
            scheduler.BuildExecutionOrders();
        }
        
        [Fact]
        public void SystemGroup_ExecutesNestedSystems()
        {
            var scheduler = new SystemScheduler();
            var group = new TestSystemGroup();
            
            scheduler.RegisterSystem(group);
            scheduler.BuildExecutionOrders();
            
            var mockView = new MockSimulationView();
            scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
            
            // Verify all nested systems executed by checking profiling data
            var child = group.GetSystems()[0];
            var profile = scheduler.GetProfileData(child);
            Assert.NotNull(profile);
            Assert.Equal(1, profile.ExecutionCount);
            
            // Note: group.WasExecuted will be false because SystemScheduler handles iteration, 
            // not the group itself. This is by design for granular profiling.
        }
        
        [Fact]
        public void Profiling_TracksExecutionTime()
        {
            var scheduler = new SystemScheduler();
            var system = new TestSystemA();
            
            scheduler.RegisterSystem(system);
            scheduler.BuildExecutionOrders();
            
            var mockView = new MockSimulationView();
            scheduler.ExecutePhase(SystemPhase.Simulation, mockView, 0.016f);
            
            var profile = scheduler.GetProfileData(system);
            Assert.NotNull(profile);
            Assert.Equal(1, profile.ExecutionCount);
        }
    }

    // Mock ISimulationView for tests
    class MockSimulationView : ISimulationView
    {
        public uint Tick => 0;
        public float Time => 0;
        public IEntityCommandBuffer GetCommandBuffer() => null!;
        public ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged => throw new NotImplementedException();
        public T GetManagedComponentRO<T>(Entity e) where T : class => throw new NotImplementedException();
        public bool IsAlive(Entity e) => true;
        public bool HasComponent<T>(Entity e) where T : unmanaged => false;
        public bool HasManagedComponent<T>(Entity e) where T : class => false;
        public ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged => ReadOnlySpan<T>.Empty;
        public IReadOnlyList<T> ConsumeManagedEvents<T>() where T : class => new List<T>();
        public QueryBuilder Query() => throw new NotImplementedException();
    }
    
    // Test systems
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    class TestSystemA : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(TestSystemA))]
    class TestSystemB : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(TestSystemB))]
    class TestSystemC : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(CircularSystemB))]
    class CircularSystemA : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(CircularSystemA))]
    class CircularSystemB : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Input)]
    class InputSystem : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Export)]
    [UpdateAfterAttribute(typeof(InputSystem))] // Cross-phase - should be ignored
    class ExportSystemDependingOnInput : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) { }
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    class TestSystemGroup : ISystemGroup
    {
        public string Name => "TestGroup";
        public bool WasExecuted { get; private set; }
        
        private readonly List<IModuleSystem> _systems = new()
        {
            new TestSystemA(),
            new TestSystemB()
        };
        
        public IReadOnlyList<IModuleSystem> GetSystems() => _systems;
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            WasExecuted = true;
            foreach (var system in _systems)
                system.Execute(view, deltaTime);
        }
    }

    // Tracking systems for execution order verification
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    class TrackingSystemA : IModuleSystem
    {
        private readonly List<string> _log;
        public TrackingSystemA(List<string> log) => _log = log;
        public void Execute(ISimulationView view, float deltaTime) => _log.Add("A");
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(TrackingSystemA))]
    class TrackingSystemB : IModuleSystem
    {
        private readonly List<string> _log;
        public TrackingSystemB(List<string> log) => _log = log;
        public void Execute(ISimulationView view, float deltaTime) => _log.Add("B");
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Simulation)]
    [UpdateAfterAttribute(typeof(TrackingSystemB))]
    class TrackingSystemC : IModuleSystem
    {
        private readonly List<string> _log;
        public TrackingSystemC(List<string> log) => _log = log;
        public void Execute(ISimulationView view, float deltaTime) => _log.Add("C");
    }
}
