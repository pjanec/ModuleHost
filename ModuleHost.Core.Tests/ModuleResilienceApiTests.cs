// File: ModuleHost.Core.Tests/ModuleResilienceApiTests.cs
using Xunit;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests
{
    public class ModuleResilienceApiTests
    {
        private class BasicTestModule : IModule
        {
            public string Name => "Basic";
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        private class CustomTimeoutModule : IModule
        {
            public string Name => "Custom";
            
            public int MaxExpectedRuntimeMs { get; set; } = 500;
            public int FailureThreshold { get; set; } = 10;
            public int CircuitResetTimeoutMs { get; set; } = 1000;
            
            // Custom Policy implementation that uses the properties
            public ExecutionPolicy Policy 
            {
                 get
                 {
                     var p = ExecutionPolicy.SlowBackground(60);
                     p.MaxExpectedRuntimeMs = MaxExpectedRuntimeMs;
                     p.FailureThreshold = FailureThreshold;
                     p.CircuitResetTimeoutMs = CircuitResetTimeoutMs;
                     return p;
                 }
            }
            
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        [Fact]
        public void IModule_ResilienceDefaults_UseStandardValues()
        {
            var module = new BasicTestModule(); 
            // Must cast to IModule to use default Policy implementation
            var iModule = (IModule)module;
            
            // SlowBackground defaults:
            // MaxExpectedRuntimeMs: >= 100
            // FailureThreshold: 5
            
            Assert.True(iModule.Policy.MaxExpectedRuntimeMs >= 100);
            Assert.Equal(5, iModule.Policy.FailureThreshold);
        }

        [Fact]
        public void IModule_CustomResilience_CanOverride()
        {
            var module = new CustomTimeoutModule
            {
                MaxExpectedRuntimeMs = 500,
                FailureThreshold = 10,
                CircuitResetTimeoutMs = 1000
            };
            
            // CustomTimeoutModule implements Policy directly, so no cast needed, but IModule cast safe.
            // Using casting for consistency with verification logic if needed.
            // But here 'module.Policy' is valid because class implements it.
            
            Assert.Equal(500, module.Policy.MaxExpectedRuntimeMs);
            Assert.Equal(10, module.Policy.FailureThreshold);
            Assert.Equal(1000, module.Policy.CircuitResetTimeoutMs);
        }
    }
}
