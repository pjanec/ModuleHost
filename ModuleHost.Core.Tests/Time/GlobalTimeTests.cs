using Xunit;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests.Time
{
    public class GlobalTimeTests
    {
        [Fact]
        public void GlobalTime_IsPaused_ReturnsTrueWhenScaleIsZero()
        {
            var time = new GlobalTime { TimeScale = 0.0f };
            Assert.True(time.IsPaused);
        }
        
        [Fact]
        public void GlobalTime_IsPaused_ReturnsFalseWhenScaleIsNonZero()
        {
            var time = new GlobalTime { TimeScale = 1.0f };
            Assert.False(time.IsPaused);
        }
    }
}
