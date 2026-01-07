using System;
using System.Numerics;
using CarKinem.Formation;
using Xunit;

namespace CarKinem.Tests.Formation
{
    public class FormationTemplateTests
    {
        [Fact]
        public void ColumnTemplate_OffsetsAreBehindLeader()
        {
            var manager = new FormationTemplateManager();
            var template = manager.GetTemplate(FormationType.Column);
            
            // All slots should be behind (negative X)
            for (int i = 0; i < template.SlotOffsets.Length; i++)
            {
                Assert.True(template.SlotOffsets[i].X < 0, 
                    $"Slot {i} should be behind leader");
                Assert.True(Math.Abs(template.SlotOffsets[i].Y) < 0.001f, 
                    $"Slot {i} should be centered");
            }
            
            manager.Dispose();
        }
        
        [Fact]
        public void GetSlotPosition_TransformsOffsetToWorldSpace()
        {
            var manager = new FormationTemplateManager();
            var template = manager.GetTemplate(FormationType.Column);
            
            Vector2 leaderPos = new Vector2(100, 100);
            Vector2 leaderForward = new Vector2(1, 0); // Facing East
            
            // Slot 0 is (-5, 0) relative to leader. World space should be (95, 100).
            Vector2 slotPos = template.GetSlotPosition(0, leaderPos, leaderForward);
            
            Assert.Equal(95f, slotPos.X, 0.1f);
            Assert.Equal(100f, slotPos.Y, 0.1f);
            
            manager.Dispose();
        }

        [Fact]
        public void WedgeTemplate_OffsetsAreCorrect()
        {
            var manager = new FormationTemplateManager();
            var template = manager.GetTemplate(FormationType.Wedge);

            // Slot 0: row 1, side 1 (positive y) -> (-4, 3)
            // Slot 1: row 1, side -1 (negative y) -> (-4, -3)
            
            // Check slot 0
            Assert.Equal(-4f, template.SlotOffsets[0].X);
            Assert.Equal(3f, template.SlotOffsets[0].Y);

            // Check slot 1
            Assert.Equal(-4f, template.SlotOffsets[1].X);
            Assert.Equal(-3f, template.SlotOffsets[1].Y);
            
            manager.Dispose();
        }
        
        [Fact]
        public void GetSlotPosition_RotatesCorrectly()
        {
            var template = new FormationTemplate 
            { 
                SlotOffsets = new[] { new Vector2(0, 5) } // 5 units to right
            };
            
            Vector2 leaderPos = Vector2.Zero;
            Vector2 leaderForward = new Vector2(0, 1); // Facing North
            // Right of North is East (1, 0).
            
            Vector2 slotPos = template.GetSlotPosition(0, leaderPos, leaderForward);
            
            // Expect (5, 0)
            Assert.Equal(5f, slotPos.X, 0.01f);
            Assert.Equal(0f, slotPos.Y, 0.01f);
        }
    }
}
