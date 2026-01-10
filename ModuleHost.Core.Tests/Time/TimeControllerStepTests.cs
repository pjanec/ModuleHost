using System;
using Xunit;
using ModuleHost.Core.Time;
using Fdp.Kernel;

namespace ModuleHost.Core.Tests.Time
{
    public class TimeControllerStepTests
    {
        [Fact]
        public void MasterController_Step_AdvancesByFixedDelta()
        {
            var eventBus = new FdpEventBus();
            var controller = new SteppedMasterController(eventBus, new System.Collections.Generic.HashSet<int>(), TimeConfig.Default);
            
            // Step with fixed 16.67ms
            var time1 = controller.Step(1.0f / 60.0f);
            
            Assert.Equal(1.0f / 60.0f, time1.DeltaTime, precision: 5);
            Assert.Equal(1, time1.FrameNumber);
            
            // Step again
            var time2 = controller.Step(1.0f / 60.0f);
            
            Assert.Equal(1.0f / 60.0f, time2.DeltaTime, precision: 5);
            Assert.Equal(2, time2.FrameNumber);
            Assert.Equal(2.0f / 60.0f, time2.TotalTime, precision: 5);
        }
        
        [Fact]
        public void MasterController_Step_RespectsTimeScale()
        {
            var eventBus = new FdpEventBus();
            var controller = new SteppedMasterController(eventBus, new System.Collections.Generic.HashSet<int>(), TimeConfig.Default);
            
            controller.SetTimeScale(0.5f);  // Half speed
            
            var time = controller.Step(1.0f / 60.0f);
            
            // Delta should NOT be scaled for manual stepping (per new logic)
            Assert.Equal(1.0f / 60.0f, time.DeltaTime, precision: 5);
        }
        
        [Fact]
        public void MasterController_Step_DoesNotAccumulateWallTime()
        {
            var eventBus = new FdpEventBus();
            var controller = new SteppedMasterController(eventBus, new System.Collections.Generic.HashSet<int>(), TimeConfig.Default);
            
            // Wait some wall time (simulate pause/idling BEFORE step)
            System.Threading.Thread.Sleep(100);
            
            // Step manually - this should reset the frame delta tracking
            var timeStep = controller.Step(1.0f / 60.0f);
            
            Assert.Equal(1.0f / 60.0f, timeStep.DeltaTime, precision: 5);
            
            // Immediate Update() should not see the 100ms gap
            var timeUpdate = controller.Update();
            
            // Delta time should be tiny (time between Step return and Update call)
            Assert.True(timeUpdate.DeltaTime < 0.05f, 
                $"Delta time should be small, got {timeUpdate.DeltaTime} (Unscaled: {timeUpdate.UnscaledDeltaTime})");
        }
        
        [Fact]
        public void KernelStepFrame_AdvancesGlobalTime()
        {
            var repo = new EntityRepository();
            var eventAcc = new EventAccumulator();
            var kernel = new ModuleHostKernel(repo, eventAcc);
            
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Standalone });
            kernel.Initialize();
            
            // Swap to SteppedMasterController to enable StepFrame
            kernel.SwapTimeController(new SteppedMasterController(repo.Bus, new System.Collections.Generic.HashSet<int>(), new TimeConfig()));
            
            // Step manually
            kernel.StepFrame(0.1f);
            
            var time = kernel.CurrentTime;
            Assert.Equal(0.1f, time.DeltaTime, precision: 5);
            Assert.Equal(1, time.FrameNumber);
            
            // Verify Singleton updated
            var globalTimeComp = repo.GetSingletonUnmanaged<GlobalTime>();
            Assert.Equal(1, globalTimeComp.FrameNumber);
        }
    }
}
