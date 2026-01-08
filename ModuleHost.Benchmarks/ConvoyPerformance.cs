using System;
using BenchmarkDotNet.Attributes;
using Fdp.Kernel;
using ModuleHost.Core;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Providers;
using System.Collections.Generic;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    public class ConvoyPerformance
    {
        private EntityRepository _liveWorld;
        private EventAccumulator _accumulator;
        private List<IModule> _modules;
        private ModuleHostKernel _kernel;

        [Params(false, true)]
        public bool UseConvoy;

        [Params(5, 20)]
        public int ModuleCount;

        [GlobalSetup]
        public void Setup()
        {
            _liveWorld = new EntityRepository();
            // Populate world
            for (int i = 0; i < 1000; i++)
            {
                _liveWorld.CreateEntity();
            }
            _accumulator = new EventAccumulator();
            
            _kernel = new ModuleHostKernel(_liveWorld, _accumulator);
            
            _modules = new List<IModule>();
            for (int i = 0; i < ModuleCount; i++)
            {
                var m = new BenchModule { Name = $"M{i}", Tier = ModuleTier.Slow, UpdateFrequency = 1 };
                _modules.Add(m);
                
                if (UseConvoy)
                {
                    _kernel.RegisterModule(m); // Auto-assign -> Convoy
                }
                else
                {
                    // Manual individual assignment
                     var p = new OnDemandProvider(_liveWorld, _accumulator, new BitMask256());
                     _kernel.RegisterModule(m, p);
                }
            }
            
            _kernel.Initialize();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _kernel.Dispose();
            _liveWorld.Dispose();
        }

        [Benchmark]
        public void RunFrame()
        {
            _kernel.Update(0.016f);
        }

        private class BenchModule : IModule
        {
            public string Name { get; set; }
            public ModuleTier Tier { get; set; }
            public int UpdateFrequency { get; set; }
            // Uses default Policy implementation
            
            public void Tick(ISimulationView view, float deltaTime) { }
            public void RegisterSystems(ISystemRegistry registry) { }
        }
    }
}
