using System;
using System.Threading.Tasks;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Time;
using Xunit;

namespace ModuleHost.Core.Tests.Integration
{
    public class TimeIntegrationTests
    {
        [Fact]
        public void Initialize_DefaultConfig_CreatesStandaloneController()
        {
            // Arrange
            var liveWorld = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(liveWorld, eventAccumulator);
            
            // Act
            kernel.Initialize();
            
            // Assert
            // We can't access _timeController directly as it is private, but we can infer from behavior.
            // Standalone controller tracks time when Update() is called.
            // CurrentTime should default to 0.
            Assert.Equal(0, kernel.CurrentTime.TotalTime);
            
            kernel.Update(); // Should advance slightly (Wall clock)
            
            Assert.True(kernel.CurrentTime.TotalTime >= 0);
            Assert.True(kernel.CurrentTime.FrameNumber == 1);
        }
        
        [Fact]
        public void ConfigureTime_SetsConfig_AndUsesIt()
        {
            // Arrange
            var liveWorld = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(liveWorld, eventAccumulator);
            
            var config = new TimeControllerConfig
            {
                Role = TimeRole.Standalone, 
                // We'll trust Standalone creates a MasterTimeController
            };
            
            // Act
            kernel.ConfigureTime(config);
            kernel.Initialize();
            
            kernel.Update();
            
            // Assert
            Assert.Equal(1, kernel.CurrentTime.FrameNumber);
        }
        
        [Fact]
        public void Update_UpdatesGlobalTime_AndRepositoryTime()
        {
            // Arrange
            var liveWorld = new EntityRepository();
            var eventAccumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(liveWorld, eventAccumulator);
            
            kernel.Initialize();
            
            // Act
            kernel.Update();
            
            // Assert
            // Repository time should match Kernel CurrentTime
            Assert.Equal((float)kernel.CurrentTime.TotalTime, liveWorld.SimulationTime, precision: 5);
        }
        
        [Fact]
        public void UpdateManual_UpdatesRepositoryTime()
        {
             // Test legacy path
             var liveWorld = new EntityRepository();
             var eventAccumulator = new EventAccumulator();
             using var kernel = new ModuleHostKernel(liveWorld, eventAccumulator);
             
             kernel.Initialize();
             
             // Act
             float dt = 0.5f;
             kernel.Update(dt);
             
             // Assert
             Assert.Equal(0.5f, liveWorld.SimulationTime, precision: 5);
        }
    }
}
