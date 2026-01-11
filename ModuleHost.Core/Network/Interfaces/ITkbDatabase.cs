using Fdp.Kernel;
using Fdp.Kernel.Tkb;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Abstraction for Type-Kit-Bag (TKB) template database access.
    /// Allows network systems to apply templates without direct dependency
    /// on concrete TKB implementation.
    /// </summary>
    public interface ITkbDatabase
    {
        /// <summary>
        /// Retrieves a TKB template by DIS entity type.
        /// </summary>
        /// <param name="entityType">DIS entity type</param>
        /// <returns>Template, or null if no template exists for this type</returns>
        TkbTemplate? GetTemplateByEntityType(DISEntityType entityType);
        
        /// <summary>
        /// Retrieves a TKB template by name.
        /// </summary>
        /// <param name="templateName">Template name</param>
        /// <returns>Template, or null if not found</returns>
        TkbTemplate? GetTemplateByName(string templateName);
    }
}
