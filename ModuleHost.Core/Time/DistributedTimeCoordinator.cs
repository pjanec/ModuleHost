using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Network;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Coordinates time mode switching on the Master node.
    /// Handles the "Future Barrier" synchronization to ensure jitter-free transitions.
    /// </summary>
    public class DistributedTimeCoordinator
    {
        private readonly FdpEventBus _eventBus;
        private readonly ModuleHostKernel _kernel;
        private readonly TimeControllerConfig _config;
        private readonly HashSet<int> _slaveNodeIds;
        
        // Barrier State
        private long _pendingBarrierFrame = -1;
        private HashSet<int>? _pendingSlaveIds;
        
        public DistributedTimeCoordinator(FdpEventBus eventBus, ModuleHostKernel kernel, 
                                          TimeControllerConfig config, HashSet<int> slaveNodeIds)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _slaveNodeIds = slaveNodeIds;
            
            _eventBus.Register<SwitchTimeModeEvent>();
        }
        
        /// <summary>
        /// Initiates a switch to Deterministic (Paused/Stepped) mode.
        /// Broadcasts a future barrier frame and waits for it.
        /// </summary>
        public void SwitchToDeterministic(HashSet<int> slaveNodeIds)
        {
            var currentState = _kernel.GetTimeController().GetCurrentState();
            
            // Plan barrier using configured lookahead
            // Use SyncConfig properties
            int lookahead = _config.SyncConfig.PauseBarrierFrames;
            long barrierFrame = currentState.FrameNumber + lookahead;
            
            _pendingBarrierFrame = barrierFrame;
            _pendingSlaveIds = slaveNodeIds;
            
            // Publish mode switch event
            _eventBus.Publish(new SwitchTimeModeEvent
            {
                TargetMode = TimeMode.Deterministic,
                FrameNumber = currentState.FrameNumber, // Ref time
                TotalTime = currentState.TotalTime,     // Ref time
                BarrierFrame = barrierFrame,
                FixedDeltaSeconds = _config.SyncConfig.FixedDeltaSeconds
            });
            
            Console.WriteLine($"[Master] Scheduled Pause at Frame {barrierFrame} (Current: {currentState.FrameNumber}, Lookahead: {lookahead})");
        }
        
        /// <summary>
        /// Initiates a switch to Continuous (Real-time) mode.
        /// Broadcasts switch event and immediately swaps (Atomic change usually safe for unpause).
        /// </summary>
        public void SwitchToContinuous()
        {
            // Cancel pending barrier
            _pendingBarrierFrame = -1;
            
            var currentState = _kernel.GetTimeController().GetCurrentState();
            
            // Publish switch event
            _eventBus.Publish(new SwitchTimeModeEvent
            {
                TargetMode = TimeMode.Continuous,
                FrameNumber = currentState.FrameNumber,
                TotalTime = currentState.TotalTime,
                BarrierFrame = 0 // Immediate
                // No IDs needed for continuous
            });
            
            // Swap immediately
            var master = new MasterTimeController(_eventBus, _config.SyncConfig);
            _kernel.SwapTimeController(master);
            
             Console.WriteLine($"[Master] Switched to Continuous Mode at Frame {currentState.FrameNumber}");
        }
        
        /// <summary>
        /// Checks if barrier frame is reached to execute the pending swap.
        /// Must be called every frame (e.g. from DemoSimulation or Kernel tick).
        /// </summary>
        public void Update()
        {
            if (_pendingBarrierFrame != -1 && _kernel.CurrentTime.FrameNumber >= _pendingBarrierFrame)
            {
                ExecuteSwapToDeterministic();
                _pendingBarrierFrame = -1;
            }
        }
        
        private void ExecuteSwapToDeterministic()
        {
            Console.WriteLine($"[Master] Barrier Reached. Swapping to SteppedMasterController.");
            
            // Create new controller
            // Note: We use the *current* state of kernel (which should be at BarrierFrame) via Swap logic
            var steppedMaster = new SteppedMasterController(_eventBus, _pendingSlaveIds!, _config.SyncConfig);
            
            // Swap
            _kernel.SwapTimeController(steppedMaster);
        }
    }
}
