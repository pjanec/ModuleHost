using System.Runtime.InteropServices;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation member component (attached to follower entities).
    /// Enables "pull" pattern: follower reads leader state.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormationMember
    {
        public int LeaderEntityId;          // Entity ID of formation leader
        public ushort SlotIndex;            // Which slot in template (0-15)
        public FormationMemberState State;  // Current formation state
        public byte IsInFormation;          // 1 = active member, 0 = inactive
        
        // State tracking
        public float SlotDistFiltered;      // Low-pass filtered distance to slot
        public float RejoinTimer;           // Time spent in Rejoining state
    }
}
