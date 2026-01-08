using Xunit;
using ModuleHost.Core.Network;
using Moq;
using System.Linq;

namespace ModuleHost.Core.Tests.Network
{
    public class DescriptorTranslatorInterfaceTests
    {
        public class TestDescriptor 
        { 
            public int Id { get; set; } 
        }

        [Fact]
        public void IDataReader_TakeSamples_CanBeEmpty()
        {
            var mockReader = new Mock<IDataReader>();
            mockReader.Setup(r => r.TakeSamples()).Returns(Enumerable.Empty<IDataSample>());
            
            var samples = mockReader.Object.TakeSamples();
            Assert.Empty(samples);
        }
        
        [Fact]
        public void IDataWriter_Write_AcceptsDescriptor()
        {
            var mockWriter = new Mock<IDataWriter>();
            var descriptor = new TestDescriptor { Id = 123 };
            
            mockWriter.Object.Write(descriptor);
            
            mockWriter.Verify(w => w.Write(It.IsAny<object>()), Times.Once);
        }
        
        [Fact]
        public void IDescriptorTranslator_HasTopicName()
        {
            var mockTranslator = new Mock<IDescriptorTranslator>();
            mockTranslator.Setup(t => t.TopicName).Returns("TestTopic");
            
            Assert.Equal("TestTopic", mockTranslator.Object.TopicName);
        }
    }
}
