using Xunit;
using ModuleHost.Core.ELM;
using Fdp.Kernel;
using System.Linq;

namespace ModuleHost.Core.Tests
{
    public class LifecycleEventsTests
    {
        [Fact]
        public void ConstructionOrder_EventIdIsUnique()
        {
            // Verify event ID is registered
            var id = EventType<ConstructionOrder>.Id;
            Assert.Equal(9001, id);
        }
        
        [Fact]
        public void ConstructionAck_EventIdIsUnique()
        {
            var id = EventType<ConstructionAck>.Id;
            Assert.Equal(9002, id);
        }
        
        [Fact]
        public void DestructionOrder_EventIdIsUnique()
        {
            var id = EventType<DestructionOrder>.Id;
            Assert.Equal(9003, id);
        }
        
        [Fact]
        public void DestructionAck_EventIdIsUnique()
        {
            var id = EventType<DestructionAck>.Id;
            Assert.Equal(9004, id);
        }
        
        [Fact]
        public void ConstructionOrder_CanBePublished()
        {
            var bus = new FdpEventBus();
            var entity = new Entity(123, 1);
            
            bus.Register<ConstructionOrder>();
            
            bus.Publish(new ConstructionOrder
            {
                Entity = entity,
                TypeId = 1,
                FrameNumber = 100
            });
            
            // Verify event is in current frame buffer
            bus.SwapBuffers();
            var events = bus.Consume<ConstructionOrder>();
            Assert.Contains(events.ToArray(), e => e.Entity.Index == 123);
        }
    }
}
