using System;
using System.Collections.Generic;
using System.Linq;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Tests.Mocks
{
    public class MockDataSample : IDataSample
    {
        public object Data { get; set; }
        public DdsInstanceState InstanceState { get; set; } = DdsInstanceState.Alive;
        
        public long EntityId
        {
            get
            {
                if (Data is EntityStateDescriptor esd) return esd.EntityId;
                if (Data is ModuleHost.Core.Network.Messages.OwnershipUpdate ou) return ou.EntityId;
                // Add other types as needed
                return 0;
            }
        }

		public long InstanceId => throw new NotImplementedException();
	}

    public class MockDataReader : IDataReader
    {
        private readonly List<IDataSample> _samples;
        
        public string TopicName => "MockTopic";

        public MockDataReader(params object[] samples)
        {
            _samples = samples.Select(s => 
            {
                if (s is IDataSample ds) return ds;
                return (IDataSample)new MockDataSample 
                { 
                    Data = s, 
                    InstanceState = DdsInstanceState.Alive 
                };
            }).ToList();
        }
        
        public IEnumerable<IDataSample> TakeSamples()
        {
            var result = _samples.ToList();
            _samples.Clear();
            return result;
        }
        
        public void Dispose() { }
    }
    
    public class MockDataWriter : IDataWriter
    {
        public List<object> WrittenSamples { get; } = new List<object>();
        public List<long> DisposedIds { get; } = new List<long>();
        
        public string TopicName => "MockTopic";

        public void Write(object sample)
        {
            WrittenSamples.Add(sample);
        }

        public void Dispose(long networkEntityId)
        {
            DisposedIds.Add(networkEntityId);
        }
        
        public void Dispose() { }
        
        public void Clear()
        {
            WrittenSamples.Clear();
            DisposedIds.Clear();
        }
    }
}
