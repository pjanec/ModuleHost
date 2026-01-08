using System;
using Fdp.Kernel;

namespace ModuleHost.Core.Network
{
    public class EntityMasterDescriptor
    {
        public long EntityId { get; set; }
        public int OwnerId { get; set; }
        public DISEntityType Type { get; set; }
        public string Name { get; set; }
    }
}
