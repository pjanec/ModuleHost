using System;
using Xunit;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests.Integration
{
    public class ModuleHostIntegrationTests
    {
        struct Position { public int X; public int Y; }

        [Fact]
        public void EndToEnd_ModuleReceivesCorrectData()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<Position>();
            
            var entity = live.CreateEntity();
            live.AddComponent(entity, new Position { X = 10, Y = 20 });
            
            var accumulator = new EventAccumulator();
            using var kernel = new ModuleHostKernel(live, accumulator);
            
            var testModule = new TestModule();
            kernel.RegisterModule(testModule);
            
            // Sync initial state is needed?
            // Update logic: SyncFrom happens inside Update.
            // But Live should be Ticked usually to increment version?
            // SyncFrom checks version.
            live.Tick(); 
            
            // Run one frame
            kernel.Update(1.0f / 60.0f);
            
            // Verify module received correct data
            Assert.True(testModule.DidRun);
            Assert.Equal(1, testModule.EntityCount);
            Assert.Equal(10, testModule.LastSeenX);
        }

        private class TestModule : IModule
        {
            public string Name => "Test";
            public ModuleTier Tier => ModuleTier.Fast;
            public int UpdateFrequency => 1;
            
            public bool DidRun { get; private set; }
            public int LastSeenX { get; private set; }
            public int EntityCount { get; private set; }
            
            public void Tick(ISimulationView view, float deltaTime)
            {
                DidRun = true;
                
                view.Query().With<Position>().Build().ForEach(e => 
                {
                    EntityCount++;
                    var pos = view.GetComponentRO<Position>(e);
                    LastSeenX = pos.X;
                });
            }
        }
    }
}
