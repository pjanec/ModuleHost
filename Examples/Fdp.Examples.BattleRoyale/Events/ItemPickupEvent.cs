using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;

namespace Fdp.Examples.BattleRoyale.Events;

[EventId(1003)]
public struct ItemPickupEvent
{
    public Entity Player;
    public Entity Item;
    public ItemTypeEnum ItemType;
    public uint Tick;
}
