namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Registry for system registration and scheduling.
    /// </summary>
    public interface ISystemRegistry
    {
        /// <summary>
        /// Register a system for execution.
        /// System's phase and dependencies are determined by attributes.
        /// </summary>
        void RegisterSystem<T>(T system) where T : IModuleSystem;
    }
}
