using System;
using Fdp.Kernel;

namespace CarKinem.Formation
{
    /// <summary>
    /// Helper methods for FormationRoster entity access.
    /// </summary>
    public static class FormationRosterExtensions
    {
        /// <summary>
        /// Set member entity at index.
        /// </summary>
        public static unsafe void SetMember(this ref FormationRoster roster, int index, Entity entity)
        {
            if (index < 0 || index >= 16)
                throw new IndexOutOfRangeException($"Member index {index} out of range [0, 16)");
            
            roster.MemberEntities[index] = *(long*)&entity; // Reinterpret Entity as long
        }
        
        /// <summary>
        /// Get member entity at index.
        /// </summary>
        public static unsafe Entity GetMember(this ref FormationRoster roster, int index)
        {
            if (index < 0 || index >= 16)
                return Entity.Null;
            
            long value = roster.MemberEntities[index];
            return *(Entity*)&value; // Reinterpret long as Entity
        }

        /// <summary>
        /// Get slot index for member at index.
        /// </summary>
        public static unsafe ushort GetSlotIndex(this ref FormationRoster roster, int index)
        {
            if (index < 0 || index >= 16)
                return 0;
            
            return roster.SlotIndices[index];
        }
        
        /// <summary>
        /// Set slot index for member at index.
        /// </summary>
        public static unsafe void SetSlotIndex(this ref FormationRoster roster, int index, ushort slotIndex)
        {
            if (index < 0 || index >= 16)
                throw new IndexOutOfRangeException($"Slot index {index} out of range [0, 16)");
            
            roster.SlotIndices[index] = slotIndex;
        }
    }
}
