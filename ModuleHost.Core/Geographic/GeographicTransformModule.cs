using System;
using System.Collections.Generic;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Geographic
{
    /// <summary>
    /// Module providing geographic coordinate transformation services.
    /// Bridges FDP's local Cartesian physics with global WGS84 geodetic coordinates.
    /// 
    /// <para><b>Systems:</b></para>
    /// <list type="bullet">
    /// <item>NetworkSmoothingSystem (Input phase) - Smooths remote entity positions</item>
    /// <item>CoordinateTransformSystem (PostSimulation phase) - Syncs owned entity geodetic coords</item>
    /// </list>
    /// 
    /// <para><b>Pattern:</b> System-based module - Tick() executes registered systems</para>
    /// </summary>
    public class GeographicTransformModule : IModule
    {
        /// <summary>
        ///Module name for diagnostics
        /// </summary>
        public string Name => "GeographicTransform";
        
        /// <summary>
        /// Runs synchronously every frame (geographic transforms are fast)
        /// </summary>
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        private readonly IGeographicTransform _geo;
        private readonly List<IModuleSystem> _systems = new();
        
        /// <summary>
        /// Creates a new Geographic Transform Module.
        /// </summary>
        /// <param name="originLat">Origin latitude in degrees (-90 to 90)</param>
        /// <param name="originLon">Origin longitude in degrees (-180 to 180)</param>
        /// <param name="originAlt">Origin altitude in meters</param>
        /// <example>
        /// <code>
        /// var geoModule = new GeographicTransformModule(
        ///     originLat: 37.7749,    // San Francisco
        ///     originLon: -122.4194,
        ///     originAlt: 0
        /// );
        /// kernel.RegisterModule(geoModule);
        /// </code>
        /// </example>
        public GeographicTransformModule(double originLat, double originLon, double originAlt)
        {
            _geo = new WGS84Transform();
            _geo.SetOrigin(originLat, originLon, originAlt);
        }
        
        /// <summary>
        /// Registers geographic transform systems with the kernel.
        /// Called once during module initialization.
        /// </summary>
        /// <param name="registry">System registry to register with</param>
        public void RegisterSystems(ISystemRegistry registry)
        {
            // Input phase: Smooths remote entity positions from geodetic updates
            var smoothingSystem = new NetworkSmoothingSystem(_geo);
            registry.RegisterSystem(smoothingSystem);
            _systems.Add(smoothingSystem);
            
            // PostSimulation phase: Updates geodetic from physics for owned entities
            var transformSystem = new CoordinateTransformSystem(_geo);
            registry.RegisterSystem(transformSystem);
            _systems.Add(transformSystem);
        }
        
        /// <summary>
        /// Main module tick - executes registered systems.
        /// 
        /// <para><b>Pattern:</b> System-based module execution</para>
        /// <list type="number">
        /// <item>Kernel already executed systems in phase order (Input, PostSim)</item>
        /// <item>Module.Tick() runs after all phases complete</item>
        /// <item>This implementation: Execute systems again for synchronous modules OR leave empty</item>
        /// </list>
        /// 
        /// <para><b>Note:</b> For synchronous modules with registered systems, the kernel's
        /// system execution mechanism should handle phase-based execution. This Tick()
        /// implementation is for compatibility if kernel delegates execution to module.</para>
        /// </summary>
        /// <param name="view">Simulation view</param>
        /// <param name="deltaTime">Frame delta time in seconds</param>
        public void Tick(ISimulationView view, float deltaTime)
        {
            // PATTERN: System-based module
            // 
            // For synchronous modules, there are two approaches:
            //
            // Approach 1 (Kernel-Driven - PREFERRED):
            //   - Kernel executes systems based on [UpdateInPhase] attributes
            //   - Module.Tick() is empty or used for post-system coordination
            //   - Systems run BEFORE module.Tick() is called
            //
            // Approach 2 (Module-Driven):
            //   - Module.Tick() explicitly executes its own systems
            //   - Gives module full control over execution order
            //   - Systems won't run via kernel's phase mechanism
            //
            // This module uses Approach 1 (empty Tick, kernel-driven).
            // Systems are registered and kernel executes them in phase order.
            //
            // If your ModuleHost kernel doesn't auto-execute registered systems,
            // uncomment the following to use Approach 2:
            
            /*
            foreach (var system in _systems)
            {
                system.Execute(view, deltaTime);
            }
            */
            
            // For now: Empty - kernel executes registered systems in phase order
        }
    }
}
