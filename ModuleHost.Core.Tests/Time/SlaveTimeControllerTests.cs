using System;
using System.Diagnostics;
using System.Collections.Generic;
using ModuleHost.Core.Time;
using ModuleHost.Core.Network; // For TimePulseDescriptor
using Fdp.Kernel;
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
            var bus = new FdpEventBus();
            var controller = new SlaveTimeController(bus, TimeConfig.Default, GetTicks);
            
            AdvanceTime(0.1);
            var t = controller.Update();
            float dt = t.DeltaTime;
            double total = t.TotalTime;
            
            Assert.Equal(0.1f, dt, precision: 4);
            Assert.Equal(0.1, total, precision: 4);
        }
        
        [Fact]
        public void Update_AdjustsDtWhenBehindMaster()
        {
            var config = new TimeConfig 
            { 
               PLLGain = 1.0, // Aggressive for test
               MaxSlew = 0.5f, // Was 0.5 double, float required? TimeConfig def uses float.
               AverageLatencyTicks = 0
            };

            var bus = new FdpEventBus();
            var controller = new SlaveTimeController(bus, config, GetTicks);
            
            AdvanceTime(0.1);
            
            // Change config to simulate latency expectation
            config.AverageLatencyTicks = (long)(0.010 * _freq);
            
            AdvanceTime(0.1); 
            
            // Pulse suggests we should be ahead (due to latency expectation)
            bus.Publish(new TimePulseDescriptor { MasterWallTicks = 0, TimeScale = 1.0f });
            bus.SwapBuffers();
            
            float dt = controller.Update().DeltaTime;
            
            // Expected dt > 0.1 because we speed up
            Assert.True(dt > 0.1f, $"dt {dt} should be > 0.1");
        }
        
        [Fact]
        public void Update_CalculatesTotalTimeRespectingScale()
        {
            var bus = new FdpEventBus();
            var controller = new SlaveTimeController(bus, TimeConfig.Default, GetTicks);
            
            AdvanceTime(0.1);
            double total = controller.Update().TotalTime;
            Assert.Equal(0.1, total, precision: 2);
            
            bus.Publish(new TimePulseDescriptor { TimeScale = 2.0f });
            bus.SwapBuffers();
            
            AdvanceTime(0.1);
            total = controller.Update().TotalTime;
            
            // 0.1 (first part) + 0.1 * 2.0 (second part) = 0.3
            Assert.Equal(0.3, total, precision: 2);
        }
        
        [Fact]
        public void OnTimePulse_HardSnap_ResetVirtualClock()
        {
            var config = new TimeConfig { SnapThresholdMs = 100 };
            var bus = new FdpEventBus();
            var controller = new SlaveTimeController(bus, config, GetTicks);
            
            AdvanceTime(1.0);
            controller.Update();
            
            AdvanceTime(5.0);
            // Trigger Hard Snap
            bus.Publish(new TimePulseDescriptor { MasterWallTicks = 0, TimeScale = 1.0f });
            bus.SwapBuffers();
            
            AdvanceTime(0.1);
            var t = controller.Update();
            float dt = t.DeltaTime;
            double total = t.TotalTime;
            
            // With fix: dt should be 0.1 (normal delta after snap).
            // Without fix: dt would include the gap (5.1).
            Assert.Equal(0.1f, dt, precision: 2);
            Assert.Equal(6.1, total, precision: 1); 
        }
    }
}
