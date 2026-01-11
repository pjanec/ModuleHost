using Fdp.Kernel;

namespace ModuleHost.Core.Network.Messages
{
    /// <summary>
    /// Weapon state descriptor for entities with multiple weapon systems.
    /// Supports multiple instances (e.g., turret 0, turret 1).
    /// </summary>
    public class WeaponStateDescriptor
    {
        public long EntityId { get; set; }
        public long InstanceId { get; set; } // Turret index (0, 1, 2...)
        public float AzimuthAngle { get; set; } // Horizontal rotation
        public float ElevationAngle { get; set; } // Vertical rotation
        public int AmmoCount { get; set; }
        public WeaponStatus Status { get; set; }
    }

    public enum WeaponStatus : byte
    {
        Ready = 0,
        Firing = 1,
        Reloading = 2,
        Jammed = 3,
        Disabled = 4
    }
}
