using System;
using System.Collections.Generic;
using ModuleHost.Core.Time;
using Fdp.Kernel;
using Xunit;

namespace ModuleHost.Core.Tests.Time
{
    public class TimeControllerFactoryTests
    {
        [Fact]
        public void Create_Standalone_ReturnsMasterController_LocalClock()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Standalone,
                Mode = TimeMode.Continuous
            };

            var controller = TimeControllerFactory.Create(new FdpEventBus(), config);

            Assert.IsType<MasterTimeController>(controller);
            Assert.Equal(TimeMode.Continuous, controller.GetMode());
        }

        [Fact]
        public void Create_ContinuousMaster_ReturnsMasterController()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Master,
                Mode = TimeMode.Continuous
            };

            var controller = TimeControllerFactory.Create(new FdpEventBus(), config);

            Assert.IsType<MasterTimeController>(controller);
        }

        [Fact]
        public void Create_ContinuousSlave_ReturnsSlaveController()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Slave,
                Mode = TimeMode.Continuous
            };

            var controller = TimeControllerFactory.Create(new FdpEventBus(), config);

            Assert.IsType<SlaveTimeController>(controller);
        }

        [Fact]
        public void Create_DeterministicMaster_RequiresNodeIds()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Master,
                Mode = TimeMode.Deterministic,
                AllNodeIds = null // Should fail
            };

            Assert.Throws<ArgumentException>(() =>
                TimeControllerFactory.Create(new FdpEventBus(), config));
        }

        [Fact]
        public void Create_DeterministicMaster_ReturnsSteppedMaster()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Master,
                Mode = TimeMode.Deterministic,
                AllNodeIds = new HashSet<int> { 1, 2 }
            };

            var controller = TimeControllerFactory.Create(new FdpEventBus(), config);

            Assert.IsType<SteppedMasterController>(controller);
        }

        [Fact]
        public void Create_DeterministicSlave_ReturnsSteppedSlave()
        {
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Slave,
                Mode = TimeMode.Deterministic,
                LocalNodeId = 1
            };

            var controller = TimeControllerFactory.Create(new FdpEventBus(), config);

            Assert.IsType<SteppedSlaveController>(controller);
        }
    }
}
