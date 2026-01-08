using System;
using Xunit;
using Moq;
using Fdp.Kernel;
using ModuleHost.Core.Geographic;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests.Geographic
{
    public class GeographicModuleTests
    {
        [Fact]
        public void Module_RegisterSystems_RegistersSubsystems()
        {
            var module = new GeographicTransformModule(0, 0, 0);
            var mockRegistry = new Mock<ISystemRegistry>();
            
            module.RegisterSystems(mockRegistry.Object);
            
            // Should register 2 systems
            mockRegistry.Verify(r => r.RegisterSystem(It.IsAny<NetworkSmoothingSystem>()), Times.Once);
            mockRegistry.Verify(r => r.RegisterSystem(It.IsAny<CoordinateTransformSystem>()), Times.Once);
        }
        
        [Fact]
        public void Turn_ExecutesSystems()
        {
             var module = new GeographicTransformModule(37, -122, 0);
             var view = new Mock<ISimulationView>();
             
             // We can't easily verify execution unless we mock the systems OR observe side effects.
             // Since we construct systems inside module, we can't mock them.
             // But we can verify no exception is thrown and basic flow works.
             // For meaningful integration, we need EntityRepository.
             
             using var repo = new EntityRepository();
             var mockRegistry = new Mock<ISystemRegistry>();
             module.RegisterSystems(mockRegistry.Object); // Required to populate internal list
             
             module.Tick(repo, 0.1f);
             
             // If we reach here without exception, good.
             // Detailed logic is tested in system unit tests.
        }
    }
}
