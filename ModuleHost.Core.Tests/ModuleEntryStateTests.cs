using System.Threading.Tasks;
using ModuleHost.Core;
using Xunit;

namespace ModuleHost.Core.Tests
{
    public class ModuleEntryStateTests
    {
        [Fact]
        public void ModuleEntry_InitialState_AllFieldsNull()
        {
            // Arrange
            var entry = new ModuleHostKernel.ModuleEntry();

            // Assert
            Assert.Null(entry.CurrentTask);
            Assert.Null(entry.LeasedView);
            Assert.Equal(0f, entry.AccumulatedDeltaTime);
            Assert.Equal(0u, entry.LastRunTick);
        }

        [Fact]
        public void ModuleEntry_AccumulatedDeltaTime_StartsAtZero()
        {
             var entry = new ModuleHostKernel.ModuleEntry();
             Assert.Equal(0f, entry.AccumulatedDeltaTime);
        }

        [Fact]
        public void ModuleEntry_LastRunTick_TracksCorrectly()
        {
            var entry = new ModuleHostKernel.ModuleEntry();
            entry.LastRunTick = 100;
            Assert.Equal(100u, entry.LastRunTick);
        }
    }
}
