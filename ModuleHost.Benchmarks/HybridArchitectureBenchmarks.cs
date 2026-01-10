using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Providers;
using System.Linq;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    public class HybridArchitectureBenchmarks
    {
        private EntityRepository _liveWorld = null!;
        private EntityRepository _replica = null!;
        private EventAccumulator _accumulator = null!;
        private DoubleBufferProvider _gdbProvider = null!;
        
        // Setup component
        struct TestComponent { public int Value; }

        [GlobalSetup]
        public void Setup()
        {
            _liveWorld = new EntityRepository();
            _liveWorld.RegisterComponent<TestComponent>();
            
            _replica = new EntityRepository();
            _replica.RegisterComponent<TestComponent>();
            
            _accumulator = new EventAccumulator();
            _gdbProvider = new DoubleBufferProvider(_liveWorld, _accumulator);
            
            // Create 10K entities with components
            for (int i = 0; i < 10000; i++)
            {
                var e = _liveWorld.CreateEntity();
                _liveWorld.AddComponent(e, new TestComponent { Value = i });
            }
            
            // Warmup:
            _replica.SyncFrom(_liveWorld);
            
            // Setup events
            for(int i = 0; i < 100; i++)
                _liveWorld.Bus.Publish(new TestComponent { Value = i });
            _liveWorld.Bus.SwapBuffers();
        }
        
        [GlobalCleanup]
        public void Cleanup()
        {
            _gdbProvider.Dispose();
            _replica.Dispose();
            _liveWorld.Dispose();
        }
        
        [Benchmark]
        public void SyncFrom_GDB_10K_Entities()
        {
            // Full sync (baseline)
            _replica.SyncFrom(_liveWorld);
        }
        
        [Benchmark]
        public void EventAccumulator_CaptureFrame()
        {
            // Capture 100 events
            _accumulator.CaptureFrame(_liveWorld.Bus, 0);
        }
        
        [Benchmark]
        public void DoubleBufferProvider_Update()
        {
            // Includes SyncFrom + Flush events
            _gdbProvider.Update();
        }
    }
    
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<CarKinemPerformance>();
        }
    }
}
