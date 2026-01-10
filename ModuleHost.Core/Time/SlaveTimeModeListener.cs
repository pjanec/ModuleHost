using System;
using Fdp.Kernel;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Listens for mode switch events on Slave nodes.
    /// Handles Future Barrier waiting before swapping controllers.
    /// </summary>
    public class SlaveTimeModeListener
    {
        private readonly FdpEventBus _eventBus;
        private readonly ModuleHostKernel _kernel;
        private readonly TimeControllerConfig _config;
        
        // Barrier State
        private long _pendingBarrierFrame = -1;
        private SwitchTimeModeEvent? _pendingEvent;
        
        public SlaveTimeModeListener(FdpEventBus eventBus, ModuleHostKernel kernel, TimeControllerConfig config)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            _eventBus.Register<SwitchTimeModeEvent>();
        }
        
        // Removed direct callback, logic moved to Update
        
        public void Update()
        {
            // Poll for events
            foreach (var evt in _eventBus.Consume<SwitchTimeModeEvent>())
            {
                OnModeSwitchRequested(evt);
            }
            
            if (_pendingBarrierFrame != -1 && _kernel.CurrentTime.FrameNumber >= _pendingBarrierFrame)
            {
                 if (_pendingEvent.HasValue)
                 {
                     ExecuteSwapToDeterministic(_pendingEvent.Value);
                 }
                 _pendingBarrierFrame = -1;
                 _pendingEvent = null;
            }
        }
        
        private void OnModeSwitchRequested(SwitchTimeModeEvent evt)
        {
            if (evt.TargetMode == TimeMode.Deterministic)
            {
                // Pause requested
                _pendingBarrierFrame = evt.BarrierFrame;
                _pendingEvent = evt;
                
                Console.WriteLine($"[Slave] Received Pause Request. Barrier Frame: {evt.BarrierFrame} (Current: {_kernel.CurrentTime.FrameNumber})");
                
                // Safety check: If we are already past the barrier (latency > lookahead), we must snap IMMEDIATELY
                if (_kernel.CurrentTime.FrameNumber >= evt.BarrierFrame)
                {
                     Console.WriteLine($"[Slave] Warning: Already past barrier ({_kernel.CurrentTime.FrameNumber} >= {evt.BarrierFrame}). Swapping immediately.");
                     ExecuteSwapToDeterministic(evt);
                     _pendingBarrierFrame = -1;
                     _pendingEvent = null;
                }
            }
            else if (evt.TargetMode == TimeMode.Continuous)
            {
                // Unpause requested - Immediate
                ExecuteSwapToContinuous(evt);
            }
        }
        
        private void ExecuteSwapToDeterministic(SwitchTimeModeEvent evt)
        {
            Console.WriteLine($"[Slave] Barrier Reached. Swapping to SteppedSlaveController.");
            
            // 1. Create SteppedSlave
            // Use config values or event values?
            float fixedDelta = evt.FixedDeltaSeconds > 0 ? evt.FixedDeltaSeconds : _config.SyncConfig.FixedDeltaSeconds;
            
            var steppedSlave = new SteppedSlaveController(_eventBus, _config.LocalNodeId, fixedDelta);
            
            // 2. Seed it
            // We use our LOCAL state to avoid rewinding.
            // We assume we are at BarrierFrame.
            // If we drifted slightly in SimTime, we accept the local drift and lock it there.
            var localState = _kernel.GetTimeController().GetCurrentState();
            
            // Ensure Frame matches exactly (just in case of weird off-by-one in check)
            // But usually we want to preserve local state continuity.
            // If we are at Frame 101 and Barrier was 100, we technically overshot.
            // But snapping back to 100 might cause replay?
            // "Jitter-Free" goal implies NO Rewinds.
            // So we seed with Current Local State.
            
            steppedSlave.SeedState(localState);
            
            // 3. Swap
            _kernel.SwapTimeController(steppedSlave);
        }
        
        private void ExecuteSwapToContinuous(SwitchTimeModeEvent evt)
        {
            Console.WriteLine($"[Slave] Unpausing to Continuous Mode.");
            
            var slave = new SlaveTimeController(_eventBus, _config.SyncConfig);
            
            // Seed with current state (Stepped state)
            // Master sends evt.TotalTime?
            // Usually we want to continue from where WE are (which should be synced via Lockstep).
            // But Master's Unpause event contains Ref Time.
            // If we use Local State, we are safe from jumps.
            var localState = _kernel.GetTimeController().GetCurrentState();
            
            slave.SeedState(localState);
            
            _kernel.SwapTimeController(slave);
        }
    }
}
