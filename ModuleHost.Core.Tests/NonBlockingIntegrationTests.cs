using System;
using System.Threading;
using System.Threading.Tasks;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using Fdp.Kernel;
using Xunit;

namespace ModuleHost.Core.Tests
{
    public class NonBlockingIntegrationTests : IDisposable
    {
        private ModuleHostKernel _kernel;
        private EntityRepository _liveWorld;
        private EventAccumulator _evtAcc;

        public NonBlockingIntegrationTests()
        {
            _liveWorld = new EntityRepository();
            _evtAcc = new EventAccumulator();
            _kernel = new ModuleHostKernel(_liveWorld, _evtAcc);
        }

        public void Dispose()
        {
            _kernel.Dispose();
            _liveWorld.Dispose();
        }

        class SlowModule : IModule
        {
            public string Name => "SlowModule";
            public ModuleTier Tier => ModuleTier.Slow; // Async
            public int UpdateFrequency => 1;
            public int SleepMs;
            public int TickCount = 0;
            public float LastDt = 0;

            public SlowModule(int sleepMs) { SleepMs = sleepMs; }

            public void Tick(ISimulationView view, float deltaTime)
            {
                LastDt = deltaTime;
                Thread.Sleep(SleepMs);
                TickCount++;
            }
        }
        
        class FastModule : IModule
        {
            public string Name => "FastModule";
            public ModuleTier Tier => ModuleTier.Fast; // FrameSynced
            public int UpdateFrequency => 1;
            public int TickCount = 0;

            public void Tick(ISimulationView view, float deltaTime)
            {
                TickCount++;
            }
        }

        [Fact(Timeout = 5000)]
        public async Task Integration_SlowModule_DoesntBlockMainThread()
        {
            var slowMod = new SlowModule(50); // 50ms sleep
            
            _kernel.RegisterModule(slowMod);
            _kernel.Initialize();

            // Run 10 frames
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                _kernel.Update(0.016f);
            }
            sw.Stop();
            
            // 10 frames should correspond to execution time of main thread only.
            // Since module is async, it doesn't block.
            // 10 frames * minimal overhead < 100ms
            Assert.True(sw.ElapsedMilliseconds < 100, $"Took {sw.ElapsedMilliseconds}ms, expected < 100ms");
            
            await Task.Delay(1); // Silence async warning
        }

        [Fact]
        public void Integration_FrameSyncedModule_BlocksUntilComplete()
        {
            var fastMod = new FastModule();
            
            _kernel.RegisterModule(fastMod);
            _kernel.Initialize();
            
            _kernel.Update(0.016f);
            
            // Should be done immediately because we wait
            Assert.Equal(1, fastMod.TickCount);
        }
        
        [Fact]
        public async Task Integration_AccumulatedTime_Correct()
        {
             // Verify that time accumulates while module runs
             var slowMod = new SlowModule(100); 
             _kernel.RegisterModule(slowMod);
             _kernel.Initialize();
             
             // Frame 1: Dispatch (dt=1)
             _kernel.Update(1.0f); 
             
             // Wait for it to start but not finish? Hard to sync.
             // Just run loop.
             
             // Frame 2: (dt=1) - Running
             _kernel.Update(1.0f);
             
             // Frame 3: (dt=1) - Running
             _kernel.Update(1.0f);
             
             // Wait for module to finish tick 1
             await Task.Delay(150);
             
             // Frame 4: Harvest (Tick 1 done). Dispatch Tick 2?
             // Tick 1 used dt=1.0.
             // While running, we added Frame 2 (1.0) and Frame 3 (1.0).
             // Frame 4 (1.0).
             // Dispatch Tick 2 should have dt = 1+1+1 = 3.0?
             
             _kernel.Update(1.0f);
             
             // Wait for Tick 2
             await Task.Delay(150);
             
             // Check slowMod.LastDt
             // Tick 1: LastDt = 1.0
             // Tick 2: LastDt = 3.0 (Frame 2 + Frame 3 + Frame 4)
             
             // Since we can't easily inspect history, we check final LastDt
             Assert.Equal(3.0f, slowMod.LastDt, 0.001f);
        }
    }
}
