using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class IModuleTests
    {
        [Fact]
        public void Interface_Compiles_And_Implemented()
        {
            // Verify IModule can be implemented
            IModule module = new TestModule();
            Assert.NotNull(module);
            Assert.Equal("Test", module.Name);
            Assert.Equal(ModuleTier.Fast, module.Tier);
        }

        [Fact]
        public void ModuleTier_EnumHasValues()
        {
            Assert.Contains(ModuleTier.Fast, Enum.GetValues<ModuleTier>());
            Assert.Contains(ModuleTier.Slow, Enum.GetValues<ModuleTier>());
        }

        private class TestModule : IModule
        {
            public string Name => "Test";
            public ModuleTier Tier => ModuleTier.Fast;
            public int UpdateFrequency => 1;

            public void Tick(ISimulationView view, float deltaTime)
            {
                // No-op
            }
        }
    }
}
