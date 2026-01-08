using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using Fdp.Kernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ModuleHost.Core.Tests
{
    public class ProviderAssignmentTests
    {
        private class TestModule : IModule
        {
            public string Name => "TestModule-" + Guid.NewGuid();
            public ExecutionPolicy Policy { get; set; } = ExecutionPolicy.Synchronous();
            public void Tick(ISimulationView view, float deltaTime) { }
        }

        private ModuleHostKernel CreateKernel()
        {
            var liveWorld = new EntityRepository();
            var eventAccum = new EventAccumulator();
            return new ModuleHostKernel(liveWorld, eventAccum);
        }

        private ModuleHostKernel.ModuleEntry GetModuleEntry(ModuleHostKernel kernel, IModule module)
        {
            var field = typeof(ModuleHostKernel).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = (List<ModuleHostKernel.ModuleEntry>)field.GetValue(kernel);
            return list.First(e => e.Module == module);
        }

        [Fact]
        public void ProviderAssignment_SynchronousDirect_NoProvider()
        {
            using var kernel = CreateKernel();
            var module = new TestModule
            {
                Policy = ExecutionPolicy.Synchronous()
            };
            
            kernel.RegisterModule(module);
            kernel.Initialize();
            
            var entry = GetModuleEntry(kernel, module);
            // Direct access means Provider can be null or special.
            // In my implementation I explicitly set it to null in AutoAssignProviders for Direct strategy.
            Assert.Null(entry.Provider); 
        }

        [Fact]
        public void ProviderAssignment_FrameSyncedGDB_SharedReplica()
        {
            using var kernel = CreateKernel();
            var module1 = new TestModule { Policy = ExecutionPolicy.FastReplica() };
            var module2 = new TestModule { Policy = ExecutionPolicy.FastReplica() };
            
            kernel.RegisterModule(module1);
            kernel.RegisterModule(module2);
            kernel.Initialize();
            
            var provider1 = GetModuleEntry(kernel, module1).Provider;
            var provider2 = GetModuleEntry(kernel, module2).Provider;
            
            Assert.NotNull(provider1);
            Assert.Same(provider1, provider2); // Share GDB replica
            Assert.IsType<DoubleBufferProvider>(provider1);
        }

        [Fact]
        public void ProviderAssignment_AsyncSoD_SingleModule_OnDemand()
        {
            using var kernel = CreateKernel();
            var module = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
            
            kernel.RegisterModule(module);
            kernel.Initialize();
            
            var provider = GetModuleEntry(kernel, module).Provider;
            Assert.IsType<OnDemandProvider>(provider);
        }

        [Fact]
        public void ProviderAssignment_AsyncSoD_MultipleModules_Convoy()
        {
            using var kernel = CreateKernel();
            var module1 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
            var module2 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
            var module3 = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
            
            kernel.RegisterModule(module1);
            kernel.RegisterModule(module2);
            kernel.RegisterModule(module3);
            kernel.Initialize();
            
            var provider = GetModuleEntry(kernel, module1).Provider;
            
            Assert.IsType<SharedSnapshotProvider>(provider);
            Assert.Same(provider, GetModuleEntry(kernel, module2).Provider);
            Assert.Same(provider, GetModuleEntry(kernel, module3).Provider);
        }
        
        [Fact]
        public void ProviderAssignment_AsyncSoD_DifferentFrequencies_SeparateConvoys()
        {
            using var kernel = CreateKernel();
            var module10Hz = new TestModule { Policy = ExecutionPolicy.SlowBackground(10) };
            var module5Hz = new TestModule { Policy = ExecutionPolicy.SlowBackground(5) };
            
            kernel.RegisterModule(module10Hz);
            kernel.RegisterModule(module5Hz);
            kernel.Initialize();
            
            var provider1 = GetModuleEntry(kernel, module10Hz).Provider;
            var provider2 = GetModuleEntry(kernel, module5Hz).Provider;
            
            Assert.NotNull(provider1);
            Assert.NotNull(provider2);
            Assert.NotSame(provider1, provider2); // Different frequencies -> Different providers
        }

        [Fact]
        public void ProviderAssignment_InvalidPolicy_ThrowsClearError()
        {
            using var kernel = CreateKernel();
            var module = new TestModule
            {
                // Invalid: Async mode but Direct strategy
                Policy = new ExecutionPolicy
                {
                    Mode = RunMode.Asynchronous,
                    Strategy = DataStrategy.Direct,
                    TargetFrequencyHz = 60,
                    MaxExpectedRuntimeMs = 100
                }
            };
            
            kernel.RegisterModule(module);
            
            var ex = Assert.Throws<InvalidOperationException>(() => kernel.Initialize());
            Assert.Contains("invalid execution policy", ex.Message);
        }
    }
}
