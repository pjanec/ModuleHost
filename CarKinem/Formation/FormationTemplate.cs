using System.Numerics;
using CarKinem.Core;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation template defining slot offsets.
    /// </summary>
    public class FormationTemplate
    {
        public FormationType Type { get; set; }
        public Vector2[]? SlotOffsets { get; set; }
        
        /// <summary>
        /// Calculate slot position in world space.
        /// </summary>
        public Vector2 GetSlotPosition(int slotIndex, Vector2 leaderPos, Vector2 leaderForward)
        {
            if (SlotOffsets == null || slotIndex < 0 || slotIndex >= SlotOffsets.Length)
                return leaderPos;
            
            Vector2 offset = SlotOffsets[slotIndex];
            Vector2 right = new Vector2(leaderForward.Y, -leaderForward.X);
            
            return leaderPos + leaderForward * offset.X + right * offset.Y;
        }
    }
}
