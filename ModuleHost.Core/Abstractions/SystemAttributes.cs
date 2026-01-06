using System;

namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Specifies which phase a system executes in.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class UpdateInPhaseAttribute : Attribute
    {
        public SystemPhase Phase { get; }
        
        public UpdateInPhaseAttribute(SystemPhase phase)
        {
            Phase = phase;
        }
    }
}
