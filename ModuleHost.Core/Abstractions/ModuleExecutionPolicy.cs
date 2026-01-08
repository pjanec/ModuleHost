namespace ModuleHost.Core.Abstractions
{
    public enum ModuleMode
    {
        /// <summary>
        /// Execution blocks the main simulation loop.
        /// Ensure the module completes within the frame budget.
        /// </summary>
        FrameSynced,
        
        /// <summary>
        /// Execution runs in background and can span multiple frames.
        /// The module loop is decoupled from simulation frame rate.
        /// </summary>
        Async
    }

    public enum TriggerType
    {
        Always,
        Interval,
        OnEvent,
        OnComponentChange
    }

    public struct ModuleExecutionPolicy
    {
        public ModuleMode Mode { get; set; }
        
        public TriggerType Trigger { get; set; }
        public int IntervalMs { get; set; }
        public System.Type TriggerArg { get; set; } // Type for Event or Component trigger
        
        public static ModuleExecutionPolicy DefaultFast => new ModuleExecutionPolicy { Mode = ModuleMode.FrameSynced, Trigger = TriggerType.Always };
        public static ModuleExecutionPolicy DefaultSlow => new ModuleExecutionPolicy { Mode = ModuleMode.Async, Trigger = TriggerType.Always };
        
        public static ModuleExecutionPolicy OnEvent<T>(ModuleMode mode = ModuleMode.FrameSynced) => new ModuleExecutionPolicy 
        { 
            Mode = mode, 
            Trigger = TriggerType.OnEvent, 
            TriggerArg = typeof(T) 
        };
        
        public static ModuleExecutionPolicy OnComponentChange<T>(ModuleMode mode = ModuleMode.FrameSynced) => new ModuleExecutionPolicy 
        { 
            Mode = mode, 
            Trigger = TriggerType.OnComponentChange, 
            TriggerArg = typeof(T) 
        };

        public static ModuleExecutionPolicy FixedInterval(int ms, ModuleMode mode = ModuleMode.Async) => new ModuleExecutionPolicy
        {
            Mode = mode,
            Trigger = TriggerType.Interval,
            IntervalMs = ms
        };
    }
}
