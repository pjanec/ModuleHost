using System;
using System.Diagnostics;
using System.Collections.Generic;
using ModuleHost.Core.Time;
using Xunit;

namespace ModuleHost.Core.Tests.Time
{
    public class SlaveTimeControllerTests
    {
        private long _currentTicks = 1000000;
        private readonly long _freq = Stopwatch.Frequency;
        
        private long GetTicks() => _currentTicks;
        
        private void AdvanceTime(double seconds)
        {
            _currentTicks += (long)(seconds * _freq);
        }
        
        [Fact]
        public void Update_AdvancesTimeUsingLocalClock()
        {
            var controller = new SlaveTimeController(TimeConfig.Default, GetTicks);
            
            AdvanceTime(0.1);
            controller.Update(out float dt, out double total);
            
            Assert.Equal(0.1f, dt, precision: 4);
            Assert.Equal(0.1, total, precision: 4);
        }
        
        [Fact]
        public void Update_AdjustsDtWhenBehindMaster()
        {
            var config = new TimeConfig 
            { 
               PLLGain = 1.0, // Aggressive for test
               MaxSlew = 0.5,
               AverageLatencyTicks = 0
            };
            var controller = new SlaveTimeController(config, GetTicks);
            
            AdvanceTime(0.1);
            
            // Change config to simulate latency expectation
            config.AverageLatencyTicks = (long)(0.010 * _freq);
            
            AdvanceTime(0.1); 
            
            // Pulse suggests we should be ahead (due to latency expectation)
            controller.OnTimePulseReceived(new TimePulseDescriptor { MasterWallTicks = 0, TimeScale = 1.0f });
            
            controller.Update(out float dt, out _);
            
            // Expected dt > 0.1 because we speed up
            Assert.True(dt > 0.1f);
        }
        
        [Fact]
        public void Update_CalculatesTotalTimeRespectingScale()
        {
            var controller = new SlaveTimeController(TimeConfig.Default, GetTicks);
            
            AdvanceTime(0.1);
            controller.Update(out _, out double total);
            Assert.Equal(0.1, total, precision: 2);
            
            controller.OnTimePulseReceived(new TimePulseDescriptor { TimeScale = 2.0f });
            
            AdvanceTime(0.1);
            controller.Update(out _, out total);
            
            // 0.1 (first part) + 0.1 * 2.0 (second part) = 0.3
            Assert.Equal(0.3, total, precision: 2);
        }
        
        [Fact]
        public void OnTimePulse_HardSnap_ResetVirtualClock()
        {
            var config = new TimeConfig { SnapThresholdMs = 100 };
            var controller = new SlaveTimeController(config, GetTicks);
            
            AdvanceTime(1.0);
            controller.Update(out _, out _);
            
            AdvanceTime(5.0);
            // Trigger Hard Snap
            controller.OnTimePulseReceived(new TimePulseDescriptor { MasterWallTicks = 0, TimeScale = 1.0f });
            
            AdvanceTime(0.1);
            controller.Update(out float dt, out double total);
            
            // With fix: dt should be 0.1 (normal delta after snap).
            // Without fix: dt would include the gap (5.1).
            Assert.Equal(0.1f, dt, precision: 2);
            Assert.Equal(6.1, total, precision: 1); 
        }
    }
}
