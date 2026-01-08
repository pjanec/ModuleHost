using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.ELM
{
    /// <summary>
    /// Processes lifecycle events (ACKs) and manages entity state transitions.
    /// Runs in BeforeSync phase to ensure changes are visible to all modules.
    /// </summary>
    [UpdateInPhase(SystemPhase.BeforeSync)]
    public class LifecycleSystem : IModuleSystem
    {
        private readonly EntityLifecycleModule _manager;
        
        public LifecycleSystem(EntityLifecycleModule manager)
        {
            _manager = manager;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            var cmd = view.GetCommandBuffer();
            uint currentFrame = view.Tick; // view.Tick matches ISimulationView interface
            
            // Process construction ACKs
            var constructionAcks = view.ConsumeEvents<ConstructionAck>();
            foreach (var ack in constructionAcks)
            {
                _manager.ProcessConstructionAck(ack, currentFrame, cmd);
            }
            
            // Process destruction ACKs
            var destructionAcks = view.ConsumeEvents<DestructionAck>();
            foreach (var ack in destructionAcks)
            {
                _manager.ProcessDestructionAck(ack, currentFrame, cmd);
            }
            
            // Check for timeouts
            _manager.CheckTimeouts(currentFrame, cmd);
        }
    }
}
