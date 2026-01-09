using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Master time controller for Deterministic (Lockstep) mode.
    /// Waits for all peer ACKs before advancing to next frame.
    /// </summary>
    public class SteppedMasterController : ITimeController
    {
        private readonly FdpEventBus _eventBus;
        private readonly HashSet<int> _allNodeIds;
        private readonly float _fixedDelta;
        private readonly TimeConfig _config;
        
        // Frame state
        private long _currentFrame = 0;
        private HashSet<int> _pendingAcks;
        private bool _waitingForAcks = false;
        private double _totalTime = 0.0;
        
        // Diagnostics
        private DateTime _frameStartTime;
        private Dictionary<int, DateTime> _lastAckTimes = new();
        
        public SteppedMasterController(
            FdpEventBus eventBus,
            HashSet<int> nodeIds, 
            TimeConfig config = null)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _allNodeIds = nodeIds ?? throw new ArgumentNullException(nameof(nodeIds));
            _config = config ?? TimeConfig.Default;
            
            // Validate node set
            if (nodeIds.Count == 0)
                throw new ArgumentException("NodeIds cannot be empty for lockstep mode", nameof(nodeIds));

            _fixedDelta = _config.FixedDeltaSeconds;
            _pendingAcks = new HashSet<int>(_allNodeIds);
            
            // Register event type to ensure stream exists
            _eventBus.Register<FrameOrderDescriptor>();
        }
        
        public GlobalTime Update()
        {
            // Process incoming ACKs
            var acks = _eventBus.Consume<FrameAckDescriptor>();
            foreach (var ack in acks)
            {
                OnFrameAckReceived(ack);
            }

            if (_waitingForAcks)
            {
                // Check if all ACKs received
                if (_pendingAcks.Count == 0)
                {
                    // All nodes finished Frame N-1, advance to Frame N
                    _currentFrame++;
                    _waitingForAcks = false;
                    
                    // Reset pending ACKs
                    _pendingAcks = new HashSet<int>(_allNodeIds);
                    _frameStartTime = DateTime.UtcNow;
                }
                else
                {
                    // Still waiting - don't advance simulation
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
            }
            
            // Publish order for current frame
            _eventBus.Publish(new FrameOrderDescriptor
            {
                FrameID = _currentFrame,
                FixedDelta = _fixedDelta,
                SequenceID = _currentFrame
            });

            //Execute this frame
            _totalTime += _fixedDelta;
            _waitingForAcks = true;
            
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
        
        private void OnFrameAckReceived(FrameAckDescriptor ack)
        {
            if (ack.FrameID == _currentFrame)
            {
                _pendingAcks.Remove(ack.NodeID);
                _lastAckTimes[ack.NodeID] = DateTime.UtcNow;
                
                // Diagnostics
                if (_pendingAcks.Count == 0)
                {
                    var totalWait = (DateTime.UtcNow - _frameStartTime).TotalMilliseconds;
                    if (totalWait > _config.SnapThresholdMs)
                    {
                        Console.WriteLine($"[Lockstep] Frame {_currentFrame} took {totalWait:F1}ms (threshold: {_config.SnapThresholdMs}ms)");
                    }
                }
            }
            else if (ack.FrameID < _currentFrame)
            {
                Console.WriteLine($"[Lockstep] Late ACK from Node {ack.NodeID}: Frame {ack.FrameID} (current: {_currentFrame})");
            }
        }
        
        public void SetTimeScale(float scale)
        {
            throw new InvalidOperationException("TimeScale not supported in Deterministic mode.");
        }
        
        public float GetTimeScale() => 1.0f;
        public TimeMode GetMode() => TimeMode.Deterministic;
        
        public void Dispose()
        {
            // No unsubscription needed for FdpEventBus
        }
    }
}
