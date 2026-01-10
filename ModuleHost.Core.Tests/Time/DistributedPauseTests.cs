using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using Fdp.Kernel;
using ModuleHost.Core.Network;
using ModuleHost.Core.Time;
using Xunit;

namespace ModuleHost.Core.Tests.Time
{
    public class DistributedPauseTests
    {
        private FdpEventBus _sharedBus;
        
        public DistributedPauseTests()
        {
            _sharedBus = new FdpEventBus();
            // Assuming FdpEventBus works in-process for pub/sub
        }
        
        [Fact]
        public void FutureBarrier_Pause_SyncsAtScheduledFrame()
        {
            // 1. Setup Master
            var masterRepo = new EntityRepository();
            var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
            var masterConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                SyncConfig = new TimeConfig { PauseBarrierFrames = 5 }
            };
            masterKernel.ConfigureTime(masterConfig);
            masterKernel.Initialize();
            
            var coordinator = new DistributedTimeCoordinator(_sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
            
            // 2. Setup Slave
            var slaveRepo = new EntityRepository();
            var slaveKernel = new ModuleHostKernel(slaveRepo, new EventAccumulator());
            var slaveConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Slave, 
                LocalNodeId = 1,
                TickProvider = () => Stopwatch.GetTimestamp() // Use real time or mock?
            };
            slaveKernel.ConfigureTime(slaveConfig);
            slaveKernel.Initialize();
            
            var listener = new SlaveTimeModeListener(_sharedBus, slaveKernel, slaveConfig);
            
            // 3. Run a bit (Continuous)
            // Need to tick kernel to advance frame count
            masterKernel.Update();
            slaveKernel.Update();
            
            Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
            Assert.Equal(TimeMode.Continuous, slaveKernel.GetTimeController().GetMode());
            
            long startFrame = masterKernel.CurrentTime.FrameNumber;
            
            // 4. Initiate Pause
            coordinator.SwitchToDeterministic(new HashSet<int>{1});
            
            // Verify NOT paused yet
            masterKernel.Update(); // Updates Time, but Coordinator.Update needs explicit call?
            // Coordinator.Update is manual in DemoSimulation. So we must call it.
            coordinator.Update();
            
            // Should still be Continuous (Barrier is +5 frames)
            Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
            
            // Slave Tick
            slaveKernel.Update();
            listener.Update();
            Assert.Equal(TimeMode.Continuous, slaveKernel.GetTimeController().GetMode());
            
            // 5. Advance Time until Barrier
            // Barrier = startFrame + 1 (Master updated once before switch?) + 5 = ~6 frames higher
            
            // Loop until barrier reached
            int safety = 0;
            while(masterKernel.GetTimeController().GetMode() == TimeMode.Continuous && safety++ < 100)
            {
                masterKernel.Update();
                coordinator.Update();
                
                // Swap shared bus to propagate network events
                _sharedBus.SwapBuffers();
                
                // Slave follows (assuming Pulse arrives via shared bus)
                slaveKernel.Update(); 
                listener.Update();
                
                Thread.Sleep(5); // Advance wall clock
            }
            
            // 6. Verify Mode Switched
            Assert.Equal(TimeMode.Deterministic, masterKernel.GetTimeController().GetMode());
            Assert.Equal(TimeMode.Deterministic, slaveKernel.GetTimeController().GetMode());
            
            // 7. Verify Frame Alignment
            // Using reflection or cast to check if SteppedController
            Assert.IsType<SteppedMasterController>(masterKernel.GetTimeController());
            Assert.IsType<SteppedSlaveController>(slaveKernel.GetTimeController());
            
            // 8. Verify No Rewinds (Frame should be >= start)
            Assert.True(masterKernel.CurrentTime.FrameNumber > startFrame);
            Assert.True(slaveKernel.CurrentTime.FrameNumber > startFrame);
            
            // Cleanup
            masterKernel.Dispose();
            slaveKernel.Dispose();
        }
        
        [Fact]
        public void Unpause_SwitchesImmediately()
        {
             // 1. Setup Master/Slave in Stepped Mode (Simulating already paused)
             // We can use the logic above to get there, or force it.
             // For speed, let's force swap manually first.
             
             var masterRepo = new EntityRepository();
             var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
             var masterConfig = new TimeControllerConfig { Role = TimeRole.Master };
             masterKernel.ConfigureTime(masterConfig);
             masterKernel.Initialize();
             
             var coordinator = new DistributedTimeCoordinator(_sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
             
            // Force Master to Stepped
             masterKernel.SwapTimeController(new SteppedMasterController(_sharedBus, new HashSet<int>{1}, masterConfig));
             Assert.Equal(TimeMode.Deterministic, masterKernel.GetTimeController().GetMode());

             // Setup Slave Swapped
             var slaveRepo = new EntityRepository();
             var slaveKernel = new ModuleHostKernel(slaveRepo, new EventAccumulator());
             var slaveConfig = new TimeControllerConfig { Role = TimeRole.Slave };
             slaveKernel.ConfigureTime(slaveConfig);
             slaveKernel.Initialize();
             
             var listener = new SlaveTimeModeListener(_sharedBus, slaveKernel, slaveConfig);
             slaveKernel.SwapTimeController(new SteppedSlaveController(_sharedBus, 1, 0.016f));
             Assert.Equal(TimeMode.Deterministic, slaveKernel.GetTimeController().GetMode());

             // 2. Unpause
             coordinator.SwitchToContinuous();
             
             // Swap shared bus to deliver event
             _sharedBus.SwapBuffers();
             
             // Verify Immediately Swapped Master
             Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
             
             // 3. Slave Unpause
             // Slave needs to process the event.
             // Process message bus on slave listener? 
             // Note: SlaveTimeModeListener subscribes to bus. 
             // We need to trigger the subscription callback.
             // FdpEventBus usually requires 'Consume' for polling or handles internal dispatch.
             // The implementation uses `Subscribe<T>`, which in FdpEventBus is usually INSTANT (Observer pattern) or Polled?
             // Checking SlaveTimeModeListener: `_eventBus.Subscribe<SwitchTimeModeEvent>(OnModeSwitchRequested);`
             // Checking FdpEventBus: If it's a simple C# event wrapper, it's instant.
             // If it's a Queue, we might need a 'Dispatch' call? 
             // FdpEventBus usually has a `Publish` that invokes subscribers immediately OR puts in queue.
             // Assuming instant for this test environment (InMemory).
             
             listener.Update(); // Just in case it checks pending (Unpause is immediate in handler)
             
             Assert.Equal(TimeMode.Continuous, slaveKernel.GetTimeController().GetMode());
             
             masterKernel.Dispose();
             slaveKernel.Dispose();
        }

        [Fact]
        public void PausedStepping_AdvancesFrameByFrame()
        {
            // Arrange: Kernel in Deterministic mode
            var repo = new EntityRepository();
            var eventBus = new FdpEventBus();
            var kernel = new ModuleHostKernel(repo, new EventAccumulator());
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            // Start directly in Deterministic Master mode for simplicity
            // (Or swap to it)
            var config = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                Mode = TimeMode.Deterministic,
                AllNodeIds = new HashSet<int> { 1 } // Required for Master
            };
            
            kernel.ConfigureTime(config);
            kernel.Initialize();
            
            // Act: Step 3 times
            // 1/60 = 0.016666...
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            
            var time = kernel.CurrentTime;
            
            // Assert: Time advanced by exactly 3 * (1/60)s
            Assert.Equal(3.0 / 60.0, time.TotalTime, precision: 4);
            Assert.Equal(3, time.FrameNumber);
            
            kernel.Dispose();
        }

        [Fact]
        public void LateArrivingSlave_SwapsImmediately_NoRewind()
        {
            // Arrange: Master and Slave with simulated high latency
            var sharedBus = new FdpEventBus();
            
            // Master Setup
            var masterRepo = new EntityRepository();
            var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
            var masterConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                SyncConfig = new TimeConfig { PauseBarrierFrames = 5 }
            };
            masterKernel.ConfigureTime(masterConfig);
            masterKernel.Initialize();
            
            var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
            
            // Slave Setup (starts delayed)
            var slaveRepo = new EntityRepository();
            var slaveKernel = new ModuleHostKernel(slaveRepo, new EventAccumulator());
            var slaveConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Slave,
                LocalNodeId = 1
            };
            slaveKernel.ConfigureTime(slaveConfig);
            slaveKernel.Initialize();
            
            var listener = new SlaveTimeModeListener(sharedBus, slaveKernel, slaveConfig);
            
            // Advance Master ahead
            for (int i = 0; i < 10; i++)
            {
                masterKernel.Update();
                Thread.Sleep(5);
            }
            
            long masterFrameBeforePause = masterKernel.CurrentTime.FrameNumber;
            
            // Master initiates pause (Barrier = Current + 5)
            coordinator.SwitchToDeterministic(new HashSet<int>{1});
            
            // Master advances to and past barrier
            for (int i = 0; i < 10; i++)
            {
                masterKernel.Update();
                coordinator.Update();
                
                // CRITICAL: Do NOT swap buffers multiple times here.
                // FdpEventBus is transient. If we swap without consuming, the event is lost.
                // We simulate "Network Delay" by holding the event in the bus (swapped once below or before)
                // until the slave is ready.
                // sharedBus.SwapBuffers(); // REMOVED
                
                Thread.Sleep(5);
            }
            
            // Swap ONCE to deliver the event to "Current" buffer
            // (If we swapped before loop, it would be waiting. If we swap after, it arrives now.)
            // Logic: SwitchToDeterministic published to NEXT.
            // We need one swap to make it readable.
            sharedBus.SwapBuffers();
            
            // Verify Master is now paused
            Assert.Equal(TimeMode.Deterministic, masterKernel.GetTimeController().GetMode());
            long masterFrameAfterPause = masterKernel.CurrentTime.FrameNumber;
            
            // Slave is still running normally (simulating network delay - hasn't received event OR hasn't processed it)
            // Advance Slave past the barrier (Barrier was ~15, we go to ~20)
            for (int i = 0; i < 20; i++)
            {
                slaveKernel.Update();
                Thread.Sleep(1);
            }
            
            long slaveFrameBeforeEvent = slaveKernel.CurrentTime.FrameNumber;
            double slaveTimeBeforeEvent = slaveKernel.CurrentTime.TotalTime;
            
            // NOW: Event finally arrives (processed)
            listener.Update();
            
            // Act & Assert
            // Slave should immediately swap (past barrier) using LOCAL state
            Assert.Equal(TimeMode.Deterministic, slaveKernel.GetTimeController().GetMode());
            
            // Verify NO REWIND: Frame should be >= before event
            Assert.True(slaveKernel.CurrentTime.FrameNumber >= slaveFrameBeforeEvent,
                $"Slave rewound! Was {slaveFrameBeforeEvent}, now {slaveKernel.CurrentTime.FrameNumber}");
            
            // Verify Time continuity
            Assert.True(slaveKernel.CurrentTime.TotalTime >= slaveTimeBeforeEvent,
                $"Slave time jumped backwards! Was {slaveTimeBeforeEvent}, now {slaveKernel.CurrentTime.TotalTime}");
            
            // Cleanup
            masterKernel.Dispose();
            slaveKernel.Dispose();
        }

        [Fact]
        public void SwapController_PreservesExactFrameAndTime()
        {
            // Arrange
            var eventBus = new FdpEventBus();
            var repo = new EntityRepository();
            var kernel = new ModuleHostKernel(repo, new EventAccumulator());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Master });
            kernel.Initialize();
            
            // Advance to known state
            for (int i = 0; i < 50; i++)
            {
                kernel.Update();
                Thread.Sleep(10); // ~500ms total
            }
            
            long frameBeforeSwap = kernel.CurrentTime.FrameNumber;
            double timeBeforeSwap = kernel.CurrentTime.TotalTime;
            
            Assert.True(frameBeforeSwap > 0, "Should have advanced some frames");
            Assert.True(timeBeforeSwap > 0, "Should have accumulated time");
            
            // Act: Swap to Deterministic
            var steppedMaster = new SteppedMasterController(
                eventBus,
                new HashSet<int> { 1 },
                new TimeControllerConfig { Role = TimeRole.Master }
            );
            
            kernel.SwapTimeController(steppedMaster);
            
            // Assert: State preserved EXACTLY
            Assert.Equal(frameBeforeSwap, kernel.CurrentTime.FrameNumber);
            Assert.Equal(timeBeforeSwap, kernel.CurrentTime.TotalTime, precision: 6);
            
            // Verify new controller also reports same state
            var newState = kernel.GetTimeController().GetCurrentState();
            Assert.Equal(frameBeforeSwap, newState.FrameNumber);
            Assert.Equal(timeBeforeSwap, newState.TotalTime, precision: 6);
            
            // Cleanup
            kernel.Dispose();
        }

        [Fact]
        public void RapidPauseUnpause_BeforeBarrier_HandlesSafely()
        {
            // Arrange
            var sharedBus = new FdpEventBus();
            var masterRepo = new EntityRepository();
            var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
            
            var masterConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                SyncConfig = new TimeConfig { PauseBarrierFrames = 10 }
            };
            masterKernel.ConfigureTime(masterConfig);
            masterKernel.Initialize();
            
            var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
            
            // Start in Continuous
            masterKernel.Update();
            Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
            
            long frameAtPause = masterKernel.CurrentTime.FrameNumber;
            
            // Act: Rapid Pause
            coordinator.SwitchToDeterministic(new HashSet<int>{1});
            
            // Advance 3 frames (still before barrier of +10)
            for (int i = 0; i < 3; i++)
            {
                masterKernel.Update();
                coordinator.Update();
                Thread.Sleep(5);
            }
            
            // Should still be Continuous (barrier not reached)
            Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
            
            // Act: Rapid Unpause (cancel pending pause)
            coordinator.SwitchToContinuous();
            
            // Advance more frames
            for (int i = 0; i < 15; i++)
            {
                masterKernel.Update();
                coordinator.Update();
                Thread.Sleep(5);
            }
            
            // Assert: Should remain Continuous (barrier canceled)
            Assert.Equal(TimeMode.Continuous, masterKernel.GetTimeController().GetMode());
            
            // Verify frame count advanced normally
            Assert.True(masterKernel.CurrentTime.FrameNumber > frameAtPause + 15,
                "Should have advanced past original barrier without pausing");
            
            // Cleanup
            masterKernel.Dispose();
        }

        [Fact]
        public void StepFrame_OnContinuousController_ThrowsInvalidOperation()
        {
            // Arrange
            var repo = new EntityRepository();
            var kernel = new ModuleHostKernel(repo, new EventAccumulator());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Master });
            kernel.Initialize();
            
            // Verify we're in Continuous mode
            Assert.Equal(TimeMode.Continuous, kernel.GetTimeController().GetMode());
            
            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                kernel.StepFrame(0.016f);
            });
            
            // Verify error message is clear
            Assert.Contains("does not support", ex.Message);
            Assert.Contains("stepping", ex.Message.ToLower());
            
            // Cleanup
            kernel.Dispose();
        }

        [Fact]
        public void SwitchEvent_PropagatesBarrierFrameCorrectly()
        {
            // Arrange
            var sharedBus = new FdpEventBus();
            
            var masterRepo = new EntityRepository();
            var masterKernel = new ModuleHostKernel(masterRepo, new EventAccumulator());
            
            int lookahead = 7; // Custom lookahead for test
            var masterConfig = new TimeControllerConfig 
            { 
                Role = TimeRole.Master,
                SyncConfig = new TimeConfig { PauseBarrierFrames = lookahead }
            };
            masterKernel.ConfigureTime(masterConfig);
            masterKernel.Initialize();
            
            var coordinator = new DistributedTimeCoordinator(sharedBus, masterKernel, masterConfig, new HashSet<int>{1});
            
            // Advance to known frame
            for (int i = 0; i < 10; i++)
            {
                masterKernel.Update();
                Thread.Sleep(5);
            }
            
            long currentFrame = masterKernel.CurrentTime.FrameNumber;
            long expectedBarrier = currentFrame + lookahead;
            
            // Act: Initiate pause
            coordinator.SwitchToDeterministic(new HashSet<int>{1});
            
            // Swap to deliver event
            sharedBus.SwapBuffers();
            
            // Capture event by consuming
            SwitchTimeModeEvent? capturedEvent = null;
            foreach(var evt in sharedBus.Consume<SwitchTimeModeEvent>())
            {
                capturedEvent = evt;
                break;
            }
            
            // Assert: Event captured
            Assert.NotNull(capturedEvent);
            Assert.Equal(TimeMode.Deterministic, capturedEvent.Value.TargetMode);
            
            // Verify Barrier Frame calculation
            Assert.Equal(expectedBarrier, capturedEvent.Value.BarrierFrame);
            
            // Verify reference time is current
            Assert.Equal(currentFrame, capturedEvent.Value.FrameNumber);
            
            // Cleanup
            masterKernel.Dispose();
        }
    }
}
