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
            public ModuleTier Tier => ModuleTier.Slow;
            public int UpdateFrequency => 1;
            
            public int MaxExpectedRuntimeMs { get; set; } = 500;
            public int FailureThreshold { get; set; } = 10;
            public int CircuitResetTimeoutMs { get; set; } = 1000;
            
            // Explicit interface implementation needed to override default interface property values effectively in some contexts?
            // Actually, for C# 8+ default interface methods, the class implements them implicitly if not defined.
            // If defined in class, they hide the interface default. 
            // However, to access interface default, one casts to interface.
            // To Override, we just implement the property on the class, but we must ensure it implements the interface method.
            // Wait, properties in interfaces.
            // IModule defines: int MaxExpectedRuntimeMs => 100;
            // The class must implement it or use default.
            // If the class defines: public int MaxExpectedRuntimeMs => 500;
            // It implements the interface.
            
            int IModule.MaxExpectedRuntimeMs => MaxExpectedRuntimeMs;
            int IModule.FailureThreshold => FailureThreshold;
            int IModule.CircuitResetTimeoutMs => CircuitResetTimeoutMs;

            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        [Fact]
        public void IModule_ResilienceDefaults_UseStandardValues()
        {
            var module = new BasicTestModule(); // Doesn't override
            var iModule = (IModule)module;
            
            Assert.Equal(100, iModule.MaxExpectedRuntimeMs);
            Assert.Equal(3, iModule.FailureThreshold);
            Assert.Equal(5000, iModule.CircuitResetTimeoutMs);
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
            
            var iModule = (IModule)module;
            
            Assert.Equal(500, iModule.MaxExpectedRuntimeMs);
            Assert.Equal(10, iModule.FailureThreshold);
            Assert.Equal(1000, iModule.CircuitResetTimeoutMs);
        }
    }
}
