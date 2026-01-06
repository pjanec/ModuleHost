namespace CarKinem.Formation
{
    /// <summary>
    /// Formation type enumeration.
    /// </summary>
    public enum FormationType : byte
    {
        Column = 0,  // Single file, vehicles behind leader
        Wedge = 1,   // V-formation, vehicles spread left/right/back
        Line = 2,    // Abreast, vehicles left/right
        Custom = 3   // User-defined slot offsets
    }

    /// <summary>
    /// Formation member state enum.
    /// </summary>
    public enum FormationMemberState : byte
    {
        InSlot = 0,      // Within tolerance of assigned slot
        CatchingUp = 1,  // Behind slot, accelerating to catch up
        Rejoining = 2,   // Far from slot, executing rejoin maneuver
        Waiting = 3,     // Leader stopped, maintaining spacing
        Broken = 4       // Too far from formation, independent control
    }
}
