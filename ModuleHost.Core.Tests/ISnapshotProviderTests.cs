using System;
using Xunit;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel; // For EntityRepository used in mocks/fakes/tests generally

namespace ModuleHost.Core.Tests
{
    public class ISnapshotProviderTests
    {
        [Fact]
        public void Interface_HasAllRequiredMembers()
        {
            // Verify ISnapshotProvider has required methods via Reflection or definition
            // We'll rely on compilation and a mock class to ensure methods exist with correct signature
            
            var providerType = typeof(ISnapshotProvider);
            
            var acquireMethod = providerType.GetMethod("AcquireView");
            Assert.NotNull(acquireMethod);
            Assert.Equal(typeof(ISimulationView), acquireMethod.ReturnType);
            
            var releaseMethod = providerType.GetMethod("ReleaseView");
            Assert.NotNull(releaseMethod);
            var updateMethod = providerType.GetMethod("Update");
            Assert.NotNull(updateMethod);
            
            var typeProp = providerType.GetProperty("ProviderType");
            Assert.NotNull(typeProp);
            Assert.Equal(typeof(SnapshotProviderType), typeProp.PropertyType);
        }

        [Fact]
        public void ProviderType_EnumHasAllValues()
        {
            Assert.True(Enum.IsDefined(typeof(SnapshotProviderType), "GDB"));
            Assert.True(Enum.IsDefined(typeof(SnapshotProviderType), "SoD"));
            Assert.True(Enum.IsDefined(typeof(SnapshotProviderType), "Shared"));
        }
        
        // Mock Implementation to verify explicit interface compliance
        class MockProvider : ISnapshotProvider
        {
            public SnapshotProviderType ProviderType => SnapshotProviderType.GDB;

            public ISimulationView AcquireView()
            {
                return null!;
            }

            public void ReleaseView(ISimulationView view)
            {
            }

            public void Update()
            {
            }
        }
        
        [Fact]
        public void MockProvider_ImplementsInterface()
        {
            ISnapshotProvider provider = new MockProvider();
            Assert.Equal(SnapshotProviderType.GDB, provider.ProviderType);
        }
    }
}
