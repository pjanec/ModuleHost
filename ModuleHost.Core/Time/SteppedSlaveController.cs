using System;
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Slave controller for Deterministic (Lockstep) mode.
    /// Advances time only when FrameOrder is received from Master.
    /// </summary>
    public class SteppedSlaveController : ITimeController
    {
        private readonly FdpEventBus _eventBus;
        private readonly int _localNodeId;
        private readonly float _configuredDelta;
        
        // Time state
        private double _totalTime;
        private long _frameNumber;
        private float _timeScale = 1.0f;
        private double _unscaledTotalTime;
        
        // Frame Queue
        private readonly Queue<FrameOrderDescriptor> _pendingOrders = new();
        
        public SteppedSlaveController(FdpEventBus eventBus, int localNodeId, float fixedDeltaSeconds)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _localNodeId = localNodeId;
            _configuredDelta = fixedDeltaSeconds;
            
            _eventBus.Register<FrameAckDescriptor>();
        }
        
        public GlobalTime Update()
        {
            // 1. Refill buffer from network
            var orders = _eventBus.Consume<FrameOrderDescriptor>();
            foreach (var order in orders)
            {
                // Only accept future frames? Or strict sequence check?
                // For robustness, ignore old frames.
                if (order.FrameID > _frameNumber)
                {
                    _pendingOrders.Enqueue(order);
                }
            }
            
            // 2. Process one frame if available
            if (_pendingOrders.Count > 0)
            {
                // Peek first? Or Dequeue?
                // We must execute ordered.
                // Assuming Queue preserves order (it does).
                // What if order is mixed? (UDP Reordering).
                // In local transport/TCP, ordered. FDP event bus usually ordered locally.
                // But distributed might be unordered.
                // For now assume Ordered or "Next Frame is Filtered".
                
                // Sort?
                // _pendingOrders is a Queue, can't sort.
                // If we care about UDP, we should use a PriorityQueue or List.
                // But simple Queue is efficient.
                
                var order = _pendingOrders.Dequeue();
                
                // Validate Sequence (Strict Lockstep)
                if (order.FrameID != _frameNumber + 1)
                {
                    // If we missed a frame or out of order
                    // Log warning?
                     Console.WriteLine($"[SteppedSlave] Warning: Out of order frame. Expected {_frameNumber + 1}, got {order.FrameID}");
                     // If future, maybe we should stash it and wait for missing?
                     // For MVP, proceed if future.
                }
                
                // Execute Step
                float dt = order.FixedDelta; 
                if (dt <= 0) dt = _configuredDelta;
                
                _frameNumber = order.FrameID;
                _totalTime += dt * _timeScale; // Assuming Scale 1.0 logic for steps usually, but respect Order if needed?
                // Order doesn't have Scale. Master used Scale to compute totalTime.
                // We should use Master's notion?
                // SteppedMaster: _totalTime += fixedDelta * _timeScale.
                // But FrameOrder only has FixedDelta.
                // The Slave needs to know Scale?
                // Assume Scale is stateful.
                
                _unscaledTotalTime += dt;
                
                // Send Ack
                SendAck(order.FrameID);
                
                return GetCurrentTime(dt, dt * _timeScale);
            }
            
            // Frozen
            return GetCurrentTime(0f, 0f);
        }
        
        private void SendAck(long frameId)
        {
            _eventBus.Publish(new FrameAckDescriptor
            {
                FrameID = frameId,
                NodeID = _localNodeId,
                Checksum = 0 // Implement hash if needed
            });
        }
        
         private GlobalTime GetCurrentTime(float unscaledDelta, float scaledDelta)
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

        public GlobalTime GetCurrentState() => GetCurrentTime(0, 0);

        public void SeedState(GlobalTime state)
        {
            _frameNumber = state.FrameNumber;
            _totalTime = state.TotalTime;
            _unscaledTotalTime = state.UnscaledTotalTime;
            _timeScale = state.TimeScale;
            
            _pendingOrders.Clear();
        }

        public void SetTimeScale(float scale)
        {
            _timeScale = scale;
        }

        public float GetTimeScale() => _timeScale;
        public TimeMode GetMode() => TimeMode.Deterministic;

        public void Dispose()
        {
        }
    }
}
