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

    public struct ModuleExecutionPolicy
    {
        public ModuleMode Mode { get; set; }
        
        public static ModuleExecutionPolicy DefaultFast => new ModuleExecutionPolicy { Mode = ModuleMode.FrameSynced };
        public static ModuleExecutionPolicy DefaultSlow => new ModuleExecutionPolicy { Mode = ModuleMode.Async };
    }
}
