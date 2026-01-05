namespace Fdp.Examples.BattleRoyale.Components;

public enum ItemTypeEnum : byte
{
    HealthKit,
    Weapon,
    Ammo
}

public struct ItemType
{
    public ItemTypeEnum Type;
}
