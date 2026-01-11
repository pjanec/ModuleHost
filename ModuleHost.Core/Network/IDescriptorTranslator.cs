using System;
using System.Collections.Generic;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;

namespace ModuleHost.Core.Network
{
    public enum DdsInstanceState
    {
        Alive,
        NotAliveDisposed,
        NotAliveNoWriters
    }

    public interface IDataSample
    {
        object Data { get; }
        DdsInstanceState InstanceState { get; }
        long EntityId { get; } // Helper to access EntityId generic-agnostically if possible, or we cast Data
        long InstanceId { get; }
    }

    // Default implementation for simple use cases
    public class DataSample : IDataSample
    {
        public object Data { get; set; }
        public DdsInstanceState InstanceState { get; set; }
        public long EntityId { get; set; } // Populated from Data if available
        public long InstanceId { get; set; } = 0;
    }

    /// <summary>
    /// Abstraction for a DDS DataReader.
    /// Decouples the core logic from specific DDS implementation.
    /// </summary>
    public interface IDataReader : IDisposable
    {
        IEnumerable<IDataSample> TakeSamples();
        string TopicName { get; }
    }

    /// <summary>
    /// Abstraction for a DDS DataWriter.
    /// </summary>
    public interface IDataWriter : IDisposable
    {
        void Write(object sample);
        void Dispose(long networkEntityId);
        string TopicName { get; }
    }

    /// <summary>
    /// Translates between Network Descriptors and FDP Components/Events.
    /// </summary>
    public interface IDescriptorTranslator
    {
        /// <summary>
        /// The DDS Topic Name this translator handles.
        /// </summary>
        string TopicName { get; }

        /// <summary>
        /// Ingress: Read from DDS, update FDP Realm.
        /// </summary>
        void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view);

        /// <summary>
        /// Egress: Scan FDP Realm, write to DDS.
        /// </summary>
        void ScanAndPublish(ISimulationView view, IDataWriter writer);
    }
}
