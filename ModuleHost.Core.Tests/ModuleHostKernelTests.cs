using System;
using System.Collections.Generic;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests
{
    public class ModuleHostKernelTests
    {
        [Fact]
        public void ModuleDeltaTime_AccumulatesCorrectly()
        {
            // Arrange
            var world = new EntityRepository();
            var accumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(world, accumulator);
            
            var testModule = new DeltaTimeTrackingModule();
            kernel.RegisterModule(testModule);
            kernel.Initialize();
            
            // Act: Run 6 frames at 60 FPS (0.016s each)
            // Module runs every 6 frames (10Hz)
            for (int i = 0; i < 6; i++)
            {
                kernel.Update(0.016f);
            }
            
            // Wait for async execution
            System.Threading.SpinWait.SpinUntil(() => testModule.WasExecuted, 2000);
            
            // Assert: Module should have received delta time of ~0.1s, not 0.016s
            Assert.True(testModule.WasExecuted, "Module should have executed");
            Assert.InRange(testModule.LastDeltaTime, 0.095f, 0.105f);
            
            // Reset for next check
            testModule.Reset();

            // Verify it resets after execution
            kernel.Update(0.016f); // Frame 7 - no execution
            kernel.Update(0.016f); // Frame 8 - no execution
            
            // After 6 more frames, delta should be ~0.1s again, not 0.2s
            for (int i = 0; i < 4; i++)
            {
                kernel.Update(0.016f);
            }
            
            // Wait again
            System.Threading.SpinWait.SpinUntil(() => testModule.WasExecuted, 2000);
            
            // Should have executed twice now
            Assert.InRange(testModule.LastDeltaTime, 0.095f, 0.105f);
        }

        [Fact]
        public void PhaseExecution_FollowsCorrectOrder()
        {
            var world = new EntityRepository();
            var accumulator = new EventAccumulator();
            var kernel = new ModuleHostKernel(world, accumulator);
            
            var log = ExecutionOrderLog.Instance;
            log.Clear();
            
            kernel.RegisterGlobalSystem(new InputPhaseSystem());
            kernel.RegisterGlobalSystem(new BeforeSyncPhaseSystem());
            kernel.RegisterGlobalSystem(new PostSimPhaseSystem());
            kernel.RegisterGlobalSystem(new ExportPhaseSystem());
            
            kernel.Initialize();
            kernel.Update(0.016f);
            
            Assert.Equal(new[] { "Input", "BeforeSync", "PostSim", "Export" }, log.Entries);
        }
    }
    
    // Test module that tracks delta time
    class DeltaTimeTrackingModule : IModule
    {
        public string Name => "DeltaTimeTracker";
        public ModuleTier Tier => ModuleTier.Slow;
        public int UpdateFrequency => 6; // 10 Hz
        
        public bool WasExecuted { get; private set; }
        public float LastDeltaTime { get; private set; }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            WasExecuted = true;
            LastDeltaTime = deltaTime;
        }
        
        public void Reset() => WasExecuted = false;
        
        public void RegisterSystems(ISystemRegistry registry) { }
    }

    // Singleton log for tracking
    class ExecutionOrderLog
    {
        public static ExecutionOrderLog Instance { get; } = new();
        public List<string> Entries { get; } = new();
        public void Clear() => Entries.Clear();
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Input)]
    class InputPhaseSystem : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) 
            => ExecutionOrderLog.Instance.Entries.Add("Input");
    }
    
    [UpdateInPhaseAttribute(SystemPhase.BeforeSync)]
    class BeforeSyncPhaseSystem : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) 
            => ExecutionOrderLog.Instance.Entries.Add("BeforeSync");
    }
    
    [UpdateInPhaseAttribute(SystemPhase.PostSimulation)]
    class PostSimPhaseSystem : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) 
            => ExecutionOrderLog.Instance.Entries.Add("PostSim");
    }
    
    [UpdateInPhaseAttribute(SystemPhase.Export)]
    class ExportPhaseSystem : IModuleSystem
    {
        public void Execute(ISimulationView view, float deltaTime) 
            => ExecutionOrderLog.Instance.Entries.Add("Export");
    }
}
