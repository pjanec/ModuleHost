using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Time;

namespace ModuleHost.Core.Tests
{
    public class ModuleHostKernelTimeTests
    {
        [Fact]
        public void Update_AdvancesGlobalTime()
        {
            // Arrange
            var repository = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(repository, eventAccumulator);
            
            repository.RegisterComponent<GlobalTime>();
            repository.SetSingletonUnmanaged(new GlobalTime());
            
            var timeConfig = new TimeControllerConfig
            {
                Role = TimeRole.Standalone,
                Mode = TimeMode.Continuous
            };
            
            kernel.ConfigureTime(timeConfig);
            kernel.Initialize();
            
            // Get initial time
            var timeBefore = kernel.CurrentTime;
            
            // Act
            kernel.Update();
            
            // Assert
            var timeAfter = kernel.CurrentTime;
            Assert.True(timeAfter.FrameNumber > timeBefore.FrameNumber);
            Assert.True(timeAfter.TotalTime > timeBefore.TotalTime);
            Assert.True(timeAfter.DeltaTime > 0);
        }
        
        [Fact]
        public void SetTimeScale_AffectsNextFrame()
        {
            // Arrange
            var repository = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(repository, eventAccumulator);
            
            repository.RegisterComponent<GlobalTime>();
            repository.SetSingletonUnmanaged(new GlobalTime());
            
            var timeConfig = new TimeControllerConfig
            {
                Role = TimeRole.Standalone,
                Mode = TimeMode.Continuous
            };
            
            kernel.ConfigureTime(timeConfig);
            kernel.Initialize();
            
            // Act - Pause simulation
            kernel.SetTimeScale(0.0f);
            kernel.Update();
            
            var timePaused = kernel.CurrentTime;
            
            // Assert - Frame advances but delta is zero
            Assert.Equal(0.0f, timePaused.DeltaTime);
            
            // Act - Resume at 2x speed
            kernel.SetTimeScale(2.0f);
            System.Threading.Thread.Sleep(100); // Let some wall time pass
            kernel.Update();
            
            var timeAfter = kernel.CurrentTime;
            
            // Assert - Time is advancing again
            Assert.True(timeAfter.DeltaTime > 0);
            Assert.Equal(2.0f, timeAfter.TimeScale);
        }
        
        [Fact]
        public void ConfigureTime_BeforeInitialize_Succeeds()
        {
            // Arrange
            var repository = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(repository, eventAccumulator);
            
            repository.RegisterComponent<GlobalTime>();
            repository.SetSingletonUnmanaged(new GlobalTime());
            
            var timeConfig = new TimeControllerConfig
            {
                Role = TimeRole.Standalone,
                InitialTimeScale = 0.5f
            };
            
            // Act & Assert - Should not throw
            kernel.ConfigureTime(timeConfig);
            kernel.Initialize();
            kernel.Update();
            
            var time = kernel.CurrentTime;
            Assert.Equal(0.5f, time.TimeScale);
        }
        
        [Fact]
        public void ConfigureTime_AfterInitialize_Throws()
        {
            // Arrange
            var repository = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(repository, eventAccumulator);
            
            repository.RegisterComponent<GlobalTime>();
            repository.SetSingletonUnmanaged(new GlobalTime());
            
            kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Standalone });
            kernel.Initialize();
            
            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                kernel.ConfigureTime(new TimeControllerConfig { Role = TimeRole.Master }));
        }
    }
}
