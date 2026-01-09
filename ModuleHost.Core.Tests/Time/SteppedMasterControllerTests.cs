using Xunit;
using ModuleHost.Core.Time;
using Fdp.Kernel;
using System.Collections.Generic;

namespace ModuleHost.Core.Tests.Time
{
    public class SteppedMasterControllerTests
    {
        [Fact]
        public void Update_WaitsForAllAcks_BeforeAdvancing()
        {
             var bus = new FdpEventBus();
             var master = new SteppedMasterController(bus, new HashSet<int>{1, 2}, new TimeConfig { FixedDeltaSeconds = 0.016f });
             
             // Frame 0 start
             master.Update();
             
             // Publish only ACK 1
             bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 1 });
             bus.SwapBuffers();
             
             // Should wait
             var time = master.Update();
             Assert.Equal(0.0f, time.DeltaTime);
             
             // Publish ACK 2
             bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 2 });
             bus.SwapBuffers();
             
             // Should advance
             time = master.Update();
             Assert.Equal(1, time.FrameNumber);
             Assert.Equal(0.016f, time.DeltaTime, precision: 3);
        }

        [Fact]
        public void OnFrameAck_IgnoresOldFrames()
        {
             var bus = new FdpEventBus();
             var master = new SteppedMasterController(bus, new HashSet<int>{1}, new TimeConfig { FixedDeltaSeconds = 0.016f });
             
             master.Update(); // Frame 0
             
             // Publish ACK for Frame -1 (impossible but old) or future?
             // Current is 0. Waiting for ACK 0.
             // If I send ACK -1?
             
             bus.Publish(new FrameAckDescriptor { FrameID = -1, NodeID = 1 });
             bus.SwapBuffers();
             
             var time = master.Update();
             Assert.Equal(0.0f, time.DeltaTime); // Still waiting for ACK 0
             
             // Send ACK 0
             bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 1 });
             bus.SwapBuffers();
             
             time = master.Update();
             Assert.Equal(1, time.FrameNumber);
        }

        [Fact]
        public void Master_HandlesMultipleConcurrentAcks()
        {
            var bus = new FdpEventBus();
            var master = new SteppedMasterController(
                bus, 
                new HashSet<int> { 1, 2, 3 }, 
                new TimeConfig { FixedDeltaSeconds = 0.016f });
            
            // Frame 0 start
            master.Update();
            
            // All 3 slaves send ACKs in same batch
            bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 1 });
            bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 2 });
            bus.Publish(new FrameAckDescriptor { FrameID = 0, NodeID = 3 });
            bus.SwapBuffers();
            
            // Should advance to Frame 1 (all ACKs received)
            var time = master.Update();
            Assert.Equal(1, time.FrameNumber);
            Assert.Equal(0.016f, time.DeltaTime, precision: 3);
        }
    }
}
