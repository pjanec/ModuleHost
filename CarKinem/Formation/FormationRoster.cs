using System.Runtime.InteropServices;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation roster (attached to leader entity or formation manager).
    /// Fixed capacity of 16 members.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FormationRoster
    {
        public int Count;                 // Number of active members (0-16)
        public int TemplateId;            // Index into formation template blob
        public FormationType Type;        // Formation type
        public FormationParams Params;    // Formation parameters
        
        // Fixed-capacity arrays (zero GC, cache-friendly)
        // Fixed-capacity arrays (zero GC, cache-friendly)
        public fixed long MemberEntities[16];   // Full Entity (8 bytes: ID + Generation)
        public fixed ushort SlotIndices[16];    // Slot index for each member
    }
}
