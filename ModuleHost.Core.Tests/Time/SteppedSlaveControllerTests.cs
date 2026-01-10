using Xunit;
using ModuleHost.Core.Time;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests.Time
{
    public class SteppedSlaveControllerTests
    {
        [Fact]
        public void Update_WaitsForFrameOrder_BeforeAdvancing()
        {
            var bus = new FdpEventBus();
            var slave = new SteppedSlaveController(bus, 1, 0.016f);
            
            // No order published
            bus.SwapBuffers();
            
            var time = slave.Update();
            Assert.Equal(0.0f, time.DeltaTime); // Waiting
        }
        
        [Fact]
        public void Update_ExecutesFrame_WhenOrderReceived()
        {
            var bus = new FdpEventBus();
            var slave = new SteppedSlaveController(bus, 1, 0.016f);
            
            // Publish Order 0
            bus.Publish(new FrameOrderDescriptor { FrameID = 0, FixedDelta = 0.016f });
            bus.SwapBuffers();
            
            var time = slave.Update();
            Assert.Equal(0, time.FrameNumber);
            Assert.Equal(0.016f, time.DeltaTime, precision: 3);
        }
        
        [Fact]
        public void Update_SendsAck_AfterFrameComplete()
        {
            var bus = new FdpEventBus();
            var slave = new SteppedSlaveController(bus, 1, 0.016f);
            
            // Execute Frame 0
            bus.Publish(new FrameOrderDescriptor { FrameID = 0, FixedDelta = 0.016f });
            bus.SwapBuffers();
            
            slave.Update();
            
            // Next update should send ACK
            slave.Update();
            
            // Check bus for ACK (it is now in Pending buffer)
            bus.SwapBuffers();
            
            var acks = bus.Consume<FrameAckDescriptor>();
            Assert.Single(acks.ToArray());
            Assert.Equal(0, acks[0].FrameID);
            Assert.Equal(1, acks[0].NodeID);
        }
    }
}
