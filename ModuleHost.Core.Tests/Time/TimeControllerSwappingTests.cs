using System;
using System.Threading;
using Xunit;
using ModuleHost.Core;
using ModuleHost.Core.Time;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests.Time
{
    public class TimeControllerSwappingTests
    {
        [Fact]
        public void SwapController_PreservesTimeState()
        {
            // Arrange
            var repo = new EntityRepository();
            var eventAcc = new EventAccumulator();
            var kernel = new ModuleHostKernel(repo, eventAcc);
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Standalone });
            kernel.Initialize();
            
            // Run for a bit
            Thread.Sleep(50);
            kernel.Update();
            var timeBefore = kernel.CurrentTime;
            
            // Act: Swap to stepping controller
            var steppingController = new SteppingTimeController(timeBefore);
            kernel.SwapTimeController(steppingController);
            
            var timeAfterSwap = kernel.CurrentTime;
            
            // Assert: Time state preserved
            Assert.Equal(timeBefore.TotalTime, timeAfterSwap.TotalTime);
            Assert.Equal(timeBefore.FrameNumber, timeAfterSwap.FrameNumber);
        }
        
        [Fact]
        public void PauseStepUnpause_NoTimeJump()
        {
            // Arrange
            var repo = new EntityRepository();
            var eventAcc = new EventAccumulator();
            var kernel = new ModuleHostKernel(repo, eventAcc);
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Standalone });
            kernel.Initialize();
            
            // Normal running
            Thread.Sleep(50);
            kernel.Update();
            var timeBeforePause = kernel.CurrentTime;
            
            // PAUSE: Swap to stepping
            var steppingController = new SteppingTimeController(timeBeforePause);
            kernel.SwapTimeController(steppingController);
            
            // Step 3 times
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            kernel.StepFrame(1.0f / 60.0f);
            
            var timeAfterSteps = kernel.CurrentTime;
            double expectedTime = timeBeforePause.TotalTime + (3.0 / 60.0);
            Assert.True(Math.Abs(timeAfterSteps.TotalTime - expectedTime) < 0.001);
            
            // Wait (simulating user pause)
            Thread.Sleep(500);
            
            // UNPAUSE: Swap back to continuous
            var continuousController = new MasterTimeController(
                eventBus: new FdpEventBus(),
                config: TimeConfig.Default
            );
            continuousController.SeedState(timeAfterSteps);
            kernel.SwapTimeController(continuousController);
            
            // First update after unpause
            Thread.Sleep(50);
            kernel.Update();
            var timeAfterUnpause = kernel.CurrentTime;
            
            // Assert: Time advanced by ~50ms, NOT 500ms + 50ms
            double timeSinceUnpause = timeAfterUnpause.TotalTime - timeAfterSteps.TotalTime;
            Assert.True(timeSinceUnpause < 0.2, 
                $"Time should advance by ~50ms, got {timeSinceUnpause}s");
            Assert.True(timeSinceUnpause > 0.01,
                $"Time should advance by >10ms, got {timeSinceUnpause}s");
        }
        
        [Fact]
        public void SteppingController_UpdateReturnsFrozenTime()
        {
            var state = new GlobalTime { TotalTime = 10.0, FrameNumber = 100 };
            var controller = new SteppingTimeController(state);
            
            // Update multiple times
            var time1 = controller.Update();
            Thread.Sleep(100);
            var time2 = controller.Update();
            
            // Assert: Time frozen
            Assert.Equal(10.0, time1.TotalTime);
            Assert.Equal(10.0, time2.TotalTime);
            Assert.Equal(0.0f, time1.DeltaTime);
            Assert.Equal(0.0f, time2.DeltaTime);
        }
        
        [Fact]
        public void SteppingController_StepAdvancesTime()
        {
            var state = new GlobalTime { TotalTime = 10.0, FrameNumber = 100, TimeScale = 1.0f };
            var controller = new SteppingTimeController(state);
            
            var time1 = controller.Step(0.1f);
            
            Assert.Equal(10.1, time1.TotalTime, precision: 5);
            Assert.Equal(101, time1.FrameNumber);
            Assert.Equal(0.1f, time1.DeltaTime, precision: 5);
        }
    }
}
