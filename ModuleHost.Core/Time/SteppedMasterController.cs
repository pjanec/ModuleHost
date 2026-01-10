using System;
using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master controller for Deterministic (Lockstep) mode.
    /// Advances time manually via Step() and coordinates Slaves via FrameOrder/Ack.
    /// </summary>
    public class SteppedMasterController : ITimeController
    {
        private readonly FdpEventBus _eventBus;
        private readonly HashSet<int> _slaveNodeIds;
        private readonly TimeConfig _config; // Using TimeConfig for simplicity (TimeControllerConfig passes this)
        
        // Time state
        private double _totalTime;
        private long _frameNumber;
        private float _timeScale = 1.0f;
        private double _unscaledTotalTime;
        
        // Lockstep state
        private bool _waitingForAcks;
        private HashSet<int> _pendingAcks;
        private long _lastFrameSequence;
        
        public SteppedMasterController(FdpEventBus eventBus, HashSet<int> nodeIds, TimeConfig config) // Changed signature to match usage
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _slaveNodeIds = nodeIds ?? throw new ArgumentNullException(nameof(nodeIds));
            _config = config ?? TimeConfig.Default;
            _pendingAcks = new HashSet<int>();
            
            // Register messaging
            _eventBus.Register<FrameOrderDescriptor>();
        }
        
        // Ctor overload to match Task 6 signature: (bus, ids, TimeControllerConfig) 
        // Note: TimeControllerConfig contains inner SyncConfig
        public SteppedMasterController(FdpEventBus eventBus, HashSet<int> nodeIds, TimeControllerConfig configWrapper)
             : this(eventBus, nodeIds, configWrapper?.SyncConfig ?? TimeConfig.Default)
        {
        }

        public GlobalTime Update()
        {
            // Process any incoming ACKs
            var acks = _eventBus.Consume<FrameAckDescriptor>();
            foreach(var ack in acks) OnAckReceived(ack);
            
            // In lockstep master, Update() just returns the current frozen time.
            // Advancement happens ONLY via Step().
            return GetCurrentTime();
        }
        
        /// <summary>
        /// Manually advance one frame.
        /// </summary>
        public GlobalTime Step(float fixedDeltaTime)
        {
            // Check previous ACKs
            if (_waitingForAcks && _pendingAcks.Count > 0)
            {
                 // Logic: Do we block? Or just warn?
                 // For interactive stepping (User clicks Button), we usually override.
                 Console.WriteLine($"[SteppedMaster] Warning: Stepping frame {_frameNumber+1} before all ACKs received for {_frameNumber}. Missing: {string.Join(",", _pendingAcks)}");
            }
            
            _frameNumber++;
            _totalTime += fixedDeltaTime * _timeScale;
            _unscaledTotalTime += fixedDeltaTime;
            
            _lastFrameSequence = _frameNumber;
            
            // Reset ACKs
            _pendingAcks = new HashSet<int>(_slaveNodeIds);
            _waitingForAcks = true;
            
            // Publish Order
            _eventBus.Publish(new FrameOrderDescriptor
            {
                FrameID = _frameNumber,
                FixedDelta = fixedDeltaTime,
                SequenceID = _frameNumber
            });
            
            return GetCurrentTime(fixedDeltaTime, fixedDeltaTime * _timeScale);
        }
        
        private void OnAckReceived(FrameAckDescriptor ack)
        {
            if (ack.FrameID == _lastFrameSequence)
            {
                if (_pendingAcks.Remove(ack.NodeID))
                {
                    if (_pendingAcks.Count == 0)
                    {
                        _waitingForAcks = false;
                        // Console.WriteLine($"[SteppedMaster] Frame {_lastFrameSequence} confirmed by all slaves.");
                    }
                }
            }
        }
        
        private GlobalTime GetCurrentTime(float unscaledDelta = 0f, float scaledDelta = 0f)
        {
            return new GlobalTime
            {
                FrameNumber = _frameNumber,
                DeltaTime = scaledDelta,
                TotalTime = _totalTime,
                TimeScale = _timeScale,
                UnscaledDeltaTime = unscaledDelta,
                UnscaledTotalTime = _unscaledTotalTime,
                StartWallTicks = 0
            };
        }

        public GlobalTime GetCurrentState() => GetCurrentTime();

        public void SeedState(GlobalTime state)
        {
            _frameNumber = state.FrameNumber;
            _totalTime = state.TotalTime;
            _unscaledTotalTime = state.UnscaledTotalTime;
            _timeScale = state.TimeScale;
            
            _pendingAcks.Clear();
            _waitingForAcks = false;
        }

        public void SetTimeScale(float scale)
        {
            _timeScale = scale;
        }

        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Deterministic;

        public void Dispose()
        {
            // clean up subscriptions? EventBus might hold weak refs or we should unsubscribe?
            // FdpEventBus typical pattern doesn't mandate unsubscribe if transient, but good practice.
        }
    }
}
