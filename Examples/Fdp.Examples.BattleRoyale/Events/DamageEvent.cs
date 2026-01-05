using Fdp.Kernel;

namespace Fdp.Examples.BattleRoyale.Events;

[EventId(1001)]
public struct DamageEvent
{
    public Entity Victim;
    public Entity Attacker;
    public float Amount;
    public uint Tick;
}
