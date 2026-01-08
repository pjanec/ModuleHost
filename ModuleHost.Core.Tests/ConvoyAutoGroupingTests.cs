using System;
using System.Reflection;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using System.Collections.Generic;

namespace ModuleHost.Core.Tests
{
    public class ConvoyAutoGroupingTests
    {
        private class TestModule : IModule
        {
            public string Name { get; set; } = "TestModule";
            public ModuleTier Tier { get; set; } = ModuleTier.Slow;
            public int UpdateFrequency { get; set; } = 1;
            // Uses default Policy implementation
            
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }

        private EntityRepository _liveWorld;
        private EventAccumulator _eventAccum;

        public ConvoyAutoGroupingTests()
        {
            _liveWorld = new EntityRepository();
            _eventAccum = new EventAccumulator();
        }

        private ISnapshotProvider GetProvider(ModuleHostKernel kernel, IModule module)
        {
            // Use reflection to access private _modules list
            var field = typeof(ModuleHostKernel).GetField("_modules", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = field.GetValue(kernel) as System.Collections.IList; // List<ModuleEntry>
            
            // list is generic, but we can iterate it using dynamic or non-generic IList
            foreach (var entry in list)
            {
                // check Module property
                var moduleProp = entry.GetType().GetProperty("Module");
                var m = moduleProp.GetValue(entry) as IModule;
                if (m == module)
                {
                    var providerProp = entry.GetType().GetProperty("Provider");
                    return providerProp.GetValue(entry) as ISnapshotProvider;
                }
            }
            return null;
        }

        [Fact]
        public void AutoGrouping_SameTierAndFreq_SharesProvider()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            
            var module1 = new TestModule { Name = "M1", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            var module2 = new TestModule { Name = "M2", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            var module3 = new TestModule { Name = "M3", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            
            kernel.RegisterModule(module1);
            kernel.RegisterModule(module2);
            kernel.RegisterModule(module3);
            kernel.Initialize();
            
            // Assert: All 3 share same provider
            var provider1 = GetProvider(kernel, module1);
            var provider2 = GetProvider(kernel, module2);
            var provider3 = GetProvider(kernel, module3);
            
            Assert.NotNull(provider1);
            Assert.Same(provider1, provider2);
            Assert.Same(provider2, provider3);
            Assert.IsType<SharedSnapshotProvider>(provider1);
        }

        [Fact]
        public void AutoGrouping_DifferentFreq_SeparateProviders()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);

            var module1 = new TestModule { Name = "M1", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            var module2 = new TestModule { Name = "M2", Tier = ModuleTier.Slow, UpdateFrequency = 10 };
            
            kernel.RegisterModule(module1);
            kernel.RegisterModule(module2);
            kernel.Initialize();
            
            var provider1 = GetProvider(kernel, module1);
            var provider2 = GetProvider(kernel, module2);
            
            Assert.NotNull(provider1);
            Assert.NotNull(provider2);
            Assert.NotSame(provider1, provider2);
            // Since count=1 for each group, they might be OnDemandProvider or Shared?
            // Current Logic: if count == 1 -> OnDemandProvider.
            Assert.IsType<OnDemandProvider>(provider1);
            Assert.IsType<OnDemandProvider>(provider2);
        }

        [Fact]
        public void AutoGrouping_SingleModule_OnDemandProvider()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            var module = new TestModule { Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            
            kernel.RegisterModule(module);
            kernel.Initialize();
            
            var provider = GetProvider(kernel, module);
            Assert.IsType<OnDemandProvider>(provider);
        }

        [Fact]
        public void AutoGrouping_FastTier_SharesDoubleBuffer()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            
            // Fast modules ignore frequency grouping? And share ONE GDB? 
            // Instructions Logic: "if (key.Tier == ModuleTier.Fast) ... foreach(var entry in moduleList) ... gdbProvider"
            // Wait, GroupBy includes Frequency.
            // If I have Fast module Freq=1 and Fast Module Freq=2. They are different groups.
            // So they get DIFFERENT GDBs?
            // "key" is {Tier, Frequency}.
            // So Fast+1 and Fast+2 are separate groups.
            // They will get SEPARATE `DoubleBufferProvider` instances.
            // Is this intended?
            // Instructions said: "// All fast modules can share one GDB"
            // THIS logic suggests they should be grouped ONLY by Tier if Fast?
            // But the code provided GROUPED BY Tier+Frequency first.
            // "var groups = _modules...GroupBy(m => new { Tier, Frequency })"
            // Then loop `foreach (var group in groups)`.
            // Inside loop: `if (key.Tier == ModuleTier.Fast)`
            // This implicitly means Fast modules with DIFFERENT frequencies are in DIFFERENT groups and verify create DIFFERENT GDBs.
            // If the intention was ONE GDB for ALL fast modules, the grouping should be handled differently.
            // But I implemented what was shown.
            // So M1(Fast, 1) and M2(Fast, 1) share GDB.
            // M1(Fast, 1) and M3(Fast, 60) have different providers.
            
            var m1 = new TestModule { Tier = ModuleTier.Fast, UpdateFrequency = 1 };
            var m2 = new TestModule { Tier = ModuleTier.Fast, UpdateFrequency = 1 };
            
            kernel.RegisterModule(m1);
            kernel.RegisterModule(m2);
            kernel.Initialize();
            
            var p1 = GetProvider(kernel, m1);
            var p2 = GetProvider(kernel, m2);
            
            Assert.Same(p1, p2);
            Assert.IsType<DoubleBufferProvider>(p1);
        }

        [Fact]
        public void AutoGrouping_ManualOverride_NotGrouped()
        {
            using var kernel = new ModuleHostKernel(_liveWorld, _eventAccum);
            
            var customProvider = new OnDemandProvider(_liveWorld, _eventAccum, new BitMask256());
            
            var module1 = new TestModule { Name = "M1", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            var module2 = new TestModule { Name = "M2", Tier = ModuleTier.Slow, UpdateFrequency = 6 };
            
            kernel.RegisterModule(module1, customProvider);  // Manual
            kernel.RegisterModule(module2);                  // Auto
            kernel.Initialize();
            
            var p1 = GetProvider(kernel, module1);
            var p2 = GetProvider(kernel, module2);
            
            Assert.Same(customProvider, p1);
            Assert.NotSame(p1, p2);
            
            // module2 is single now in its auto-group (since M1 was filtered out by Where(Provider == null))
            // So M2 gets OnDemandProvider
            Assert.IsType<OnDemandProvider>(p2);
        }
    }
}
