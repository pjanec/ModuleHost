using System;
using Fdp.Kernel;

namespace ModuleHost.Core.Time
{
    /// <summary>
    /// Factory for creating time controllers based on configuration.
    /// Handles role/mode combinations: Standalone, Master/Slave x Continuous/Deterministic.
    /// </summary>
    public static class TimeControllerFactory
    {
        /// <summary>
        /// Create a time controller based on configuration.
        /// </summary>
        public static ITimeController Create(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            if (eventBus == null)
                throw new ArgumentNullException(nameof(eventBus));
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            
            return config.Role switch
            {
                TimeRole.Standalone => CreateStandalone(config),
                TimeRole.Master => CreateMaster(eventBus, config),
                TimeRole.Slave => CreateSlave(eventBus, config),
                _ => throw new ArgumentException($"Unknown TimeRole: {config.Role}")
            };
        }
        
        private static ITimeController CreateStandalone(TimeControllerConfig config)
        {
            // Standalone uses local wall clock (no network sync, no custom tick provider)
            var controller = new MasterTimeController(
                eventBus: new FdpEventBus(),  // Dummy bus (no publishing)
                config: config.SyncConfig
            );
            
            controller.SetTimeScale(config.InitialTimeScale);
            return controller;
        }
        
        private static ITimeController CreateMaster(
            FdpEventBus eventBus, 
            TimeControllerConfig config)
        {
            return config.Mode switch
            {
                TimeMode.Continuous => CreateContinuousMaster(eventBus, config),
                TimeMode.Deterministic => CreateDeterministicMaster(eventBus, config),
                _ => throw new ArgumentException($"Unknown TimeMode: {config.Mode}")
            };
        }
        
        private static ITimeController CreateSlave(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            return config.Mode switch
            {
                TimeMode.Continuous => CreateContinuousSlave(eventBus, config),
                TimeMode.Deterministic => CreateDeterministicSlave(eventBus, config),
                _ => throw new ArgumentException($"Unknown TimeMode: {config.Mode}")
            };
        }
        
        // Continuous Mode
        
        private static ITimeController CreateContinuousMaster(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            var controller = new MasterTimeController(eventBus, config.SyncConfig);
            controller.SetTimeScale(config.InitialTimeScale);
            return controller;
        }
        
        private static ITimeController CreateContinuousSlave(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            return new SlaveTimeController(eventBus, config.SyncConfig, config.TickProvider);
        }
        
        // Deterministic Mode
        
        private static ITimeController CreateDeterministicMaster(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            if (config.AllNodeIds == null || config.AllNodeIds.Count == 0)
            {
                throw new ArgumentException(
                    "Deterministic Master requires AllNodeIds (set of peer IDs)");
            }
            
            return new SteppedMasterController(
                eventBus,
                config.AllNodeIds,
                config.SyncConfig
            );
        }
        
        private static ITimeController CreateDeterministicSlave(
            FdpEventBus eventBus,
            TimeControllerConfig config)
        {
            return new SteppedSlaveController(
                eventBus,
                config.LocalNodeId,
                config.SyncConfig.FixedDeltaSeconds
            );
        }
    }
}
