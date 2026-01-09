using System;
using System.Diagnostics;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Slave time controller for Deterministic (Lockstep) mode.
    /// Waits for FrameOrder from Master before advancing.
    /// </summary>
    public class SteppedSlaveController : ITimeController
    {
        private readonly FdpEventBus _eventBus;
        private readonly int _localNodeId;
        private readonly float _defaultDelta;
        
        // Frame state
        private long _currentFrame = -1;
        private float _fixedDelta = 0.016f;
        private double _totalTime = 0.0;
        private bool _hasFrameOrder = false;
        private bool _needToSendAck = false;
        
        public SteppedSlaveController(
            FdpEventBus eventBus,
            int localNodeId, 
            float defaultDelta = 0.016f)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _localNodeId = localNodeId;
            _defaultDelta = defaultDelta;
            _fixedDelta = defaultDelta;
            
            // Register event type
            _eventBus.Register<FrameAckDescriptor>();
        }
        
        private void OnFrameOrderReceived(FrameOrderDescriptor order)
        {
            if (order.FrameID == _currentFrame + 1)
            {
                _currentFrame = order.FrameID;
                _fixedDelta = order.FixedDelta;
                _hasFrameOrder = true;
            }
            else if (order.FrameID > _currentFrame + 1)
            {
                Console.WriteLine($"[Lockstep Slave] Missed frame! Expected {_currentFrame + 1}, got {order.FrameID}");
                // Snap to new frame (recovery from network hiccup)
                _currentFrame = order.FrameID;
                _totalTime = _currentFrame * _fixedDelta;
                _hasFrameOrder = true;
            }
        }
        
        public GlobalTime Update()
        {
            // Process incoming orders
            var orders = _eventBus.Consume<FrameOrderDescriptor>();
            foreach (var order in orders)
            {
                OnFrameOrderReceived(order);
            }

            // Send ACK from previous frame (if needed)
            if (_needToSendAck)
            {
                SendFrameAck();
                _needToSendAck = false;
            }
            
            if (!_hasFrameOrder)
            {
                // Waiting for master - don't advance
                return new GlobalTime
                {
                    FrameNumber = _currentFrame,
                    DeltaTime = 0.0f,
                    TotalTime = _totalTime,
                    TimeScale = 1.0f,
                    UnscaledDeltaTime = 0,
                    UnscaledTotalTime = (long)(_totalTime * Stopwatch.Frequency)
                };
            }
            
            // Execute frame
            _totalTime += _fixedDelta;
            _hasFrameOrder = false;
            _needToSendAck = true;
            
            return new GlobalTime
            {
                FrameNumber = _currentFrame,
                DeltaTime = _fixedDelta,
                TotalTime = _totalTime,
                TimeScale = 1.0f,
                UnscaledDeltaTime = (long)(_fixedDelta * Stopwatch.Frequency),
                UnscaledTotalTime = (long)(_totalTime * Stopwatch.Frequency)
            };
        }
        
        private void SendFrameAck()
        {
            _eventBus.Publish(new FrameAckDescriptor
            {
                FrameID = _currentFrame,
                NodeID = _localNodeId,
                TotalTime = _totalTime
            });
        }
        
        public void SetTimeScale(float scale)
        {
            throw new InvalidOperationException("TimeScale not supported in Deterministic mode.");
        }
        
        public float GetTimeScale() => 1.0f;
        public TimeMode GetMode() => TimeMode.Deterministic;
        
        public void Dispose()
        {
            // No unsubscription needed
        }
    }
}
