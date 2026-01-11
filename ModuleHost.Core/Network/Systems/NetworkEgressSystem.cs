using System;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Translators;

namespace ModuleHost.Core.Network.Systems
{
    /// <summary>
    /// System responsible for publishing owned descriptors to the network.
    /// Handles normal periodic publishing and force-publish requests.
    /// </summary>
    public class NetworkEgressSystem
    {
        private readonly IDescriptorTranslator[] _translators;
        private readonly IDataWriter[] _writers;
        
        public NetworkEgressSystem(
            IDescriptorTranslator[] translators,
            IDataWriter[] writers)
        {
            _translators = translators ?? throw new ArgumentNullException(nameof(translators));
            _writers = writers ?? throw new ArgumentNullException(nameof(writers));
            
            if (_translators.Length != _writers.Length)
                throw new ArgumentException("Translators and writers arrays must have same length");
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Process force-publish requests first
            ProcessForcePublish(view);
            
            // Normal periodic publishing
            for (int i = 0; i < _translators.Length; i++)
            {
                _translators[i].ScanAndPublish(view, _writers[i]);
            }
        }
        
        private void ProcessForcePublish(ISimulationView view)
        {
            var cmd = view.GetCommandBuffer();
            
            // Query entities with ForceNetworkPublish
            var query = view.Query()
                .With<ForceNetworkPublish>()
                .Build();
            
            foreach (var entity in query)
            {
                // Remove the component - it's one-time
                cmd.RemoveComponent<ForceNetworkPublish>(entity);
                
                // Force publish happens implicitly in next ScanAndPublish
                // The translators will see this entity and publish it
            }
        }
    }
}
