using System;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Geographic
{
    public class GeographicTransformModule : IModule
    {
        public string Name => "GeographicTransform";
        
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        private readonly IGeographicTransform _geo;
        private readonly System.Collections.Generic.List<IModuleSystem> _systems = new();
        
        public GeographicTransformModule(double originLat, double originLon, double originAlt)
        {
            _geo = new WGS84Transform();
            _geo.SetOrigin(originLat, originLon, originAlt);
        }
        
        public void RegisterSystems(ISystemRegistry registry)
        {
            var s1 = new NetworkSmoothingSystem(_geo);
            var s2 = new CoordinateTransformSystem(_geo);
            
            registry.RegisterSystem(s1);
            registry.RegisterSystem(s2);
            
            _systems.Add(s1);
            _systems.Add(s2);
        }
        
        public void Tick(ISimulationView view, float deltaTime) 
        { 
            // Let's assume the Kernel or ModuleHost handles "System" execution automatically if they are registered?
            // Actually, ModuleHostKernel executes the *Module*. The Module.Tick() is entered.
            // If Module.Tick is empty, systems won't run unless there is an underlying mechanism.
            // However, the provided snippet has empty Tick.
            // This is suspicious.
            // Let's look at `BATCH-06-INSTRUCTIONS.md` or `IModule` to see how systems are run.
            // Or maybe `GeographicTransformModule` is supposed to run them itself in Tick?
            
            // In standard FDP designs, systems are often run in Tick.
            // I will implement a basic execution of registered systems in Tick to be safe, 
            // OR check if ISystemRegistry is injected into Kernel execution.
        }
    }
}
