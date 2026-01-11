// File: ModuleHost.Core.Tests/ModuleEntryResilienceTests.cs

using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;
using ModuleHost.Core.Resilience;
using System.Reflection;
using System.Linq;

namespace ModuleHost.Core.Tests
{
    public class ModuleEntryResilienceTests
    {
        private class TestModule : IModule
        {
            public string Name => "TestModule";
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        private class CustomTimeoutModule : IModule
        {
            public string Name => "CustomModule";
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;
            
            public int MaxExpectedRuntimeMs => 500;
            public int FailureThreshold => 5;
            public int CircuitResetTimeoutMs => 10000;
            
            public ExecutionPolicy Policy 
            {
                get
                {
                    // Base policy
                    var p = Tier == ModuleTier.Fast 
                        ? ExecutionPolicy.FastReplica() 
                        : ExecutionPolicy.SlowBackground(UpdateFrequency <= 1 ? 60 : 60/UpdateFrequency);
                    
                    // Apply overrides
                    p.MaxExpectedRuntimeMs = MaxExpectedRuntimeMs;
                    p.FailureThreshold = FailureThreshold;
                    p.CircuitResetTimeoutMs = CircuitResetTimeoutMs;
                    
                    return p;
                }
            }
            
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccum;

        public ModuleEntryResilienceTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccum = new EventAccumulator();
        }

        [Fact]
        public void ModuleEntry_Registration_InitializesCircuitBreaker()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            var module = new TestModule();
            
            kernel.RegisterModule(module);
            
            // To test internal state, we can use reflection or inspect via GetExecutionStats() 
            // GetExecutionStats returns ModuleStats which exposes circuit state, but not the configuration values.
            // But we can check if ModuleEntry was created correctly by checking if it behaves correctly?
            // Or access internals. ModuleEntry is internal.
            // Tests assembly has InternalsVisibleTo.
            
            // Access _modules list via reflection
            var modulesField = typeof(ModuleHostKernel).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance);
            var modules = modulesField.GetValue(kernel) as System.Collections.IEnumerable;
            
            // Iterate to find the entry
            object entry = null;
            foreach (var item in modules)
            {
                var m = item.GetType().GetProperty("Module").GetValue(item) as IModule;
                if (m == module)
                {
                    entry = item;
                    break;
                }
            }
            
            Assert.NotNull(entry);
            
            var circuitBreaker = entry.GetType().GetProperty("CircuitBreaker").GetValue(entry);
            Assert.NotNull(circuitBreaker);
            
            var maxRuntime = (int)entry.GetType().GetProperty("MaxExpectedRuntimeMs").GetValue(entry);
            Assert.Equal(100, maxRuntime); // Default
        }

        [Fact]
        public void ModuleEntry_CustomTimeouts_RespectedInRegistration()
        {
            var module = new CustomTimeoutModule(); // Values: 500, 5, 10000
            
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            kernel.RegisterModule(module);
            
            // Reflection to verify
             var modulesField = typeof(ModuleHostKernel).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance);
            var modules = modulesField.GetValue(kernel) as System.Collections.IEnumerable;
            
            object entry = null;
            foreach (var item in modules)
            {
                var m = item.GetType().GetProperty("Module").GetValue(item) as IModule;
                if (m == module)
                {
                    entry = item;
                    break;
                }
            }
            
            Assert.NotNull(entry);
            
            var maxRuntime = (int)entry.GetType().GetProperty("MaxExpectedRuntimeMs").GetValue(entry);
            var failures = (int)entry.GetType().GetProperty("FailureThreshold").GetValue(entry);
            var resetTimeout = (int)entry.GetType().GetProperty("CircuitResetTimeoutMs").GetValue(entry);
            
            Assert.Equal(500, maxRuntime);
            Assert.Equal(5, failures);
            Assert.Equal(10000, resetTimeout);
        }
    }
}
