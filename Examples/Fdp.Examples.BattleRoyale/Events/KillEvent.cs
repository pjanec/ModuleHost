using Fdp.Kernel;
using Fdp.Examples.BattleRoyale.Components;

namespace Fdp.Examples.BattleRoyale.Events;

[EventId(1002)]
public struct KillEvent
{
    public Entity Victim;
    public Entity Killer;
    public Position Position;
    public uint Tick;
}
