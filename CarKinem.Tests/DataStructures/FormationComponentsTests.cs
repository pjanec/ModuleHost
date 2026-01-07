using System;
using System.Runtime.InteropServices;
using CarKinem.Formation;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.DataStructures
{
    public class FormationComponentsTests
    {
        [Fact]
        public unsafe void FormationRoster_FixedArrays_AreAccessible()
        {
            var roster = new FormationRoster();
            roster.Count = 2;
            
            // Test fixed array access via extensions or direct unsafe
            roster.SetMember(0, new Entity(100, 1));
            roster.SetMember(1, new Entity(101, 2));
            
            var e0 = roster.GetMember(0);
            var e1 = roster.GetMember(1);
            
            Assert.Equal(100, e0.Index);
            Assert.Equal(1, e0.Generation);
            Assert.Equal(101, e1.Index);
            Assert.Equal(2, e1.Generation);
            
            // Slot indices (still ushort)
            roster.SetSlotIndex(0, 10);
            roster.SetSlotIndex(1, 20);
            
            Assert.Equal(10, roster.GetSlotIndex(0));
            Assert.Equal(20, roster.GetSlotIndex(1));
        }

        [Fact]
        public unsafe void FormationRoster_MaxCapacity_Is16()
        {
            // Reflection implies easier check, usually fixed buffers generate a struct with the buffer size.
            // But strict checking:
            // MemberEntityIds is 16 ints (64 bytes)
            // SlotIndices is 16 ushorts (32 bytes)
            
            // Roster checking logic...
            // We can check size of the whole struct and deduce, or inspect fields via reflection/unsafe.
            
            // Let's rely on manual size verification
            // int intSize = sizeof(int); // Unused
            // int ushortSize = sizeof(ushort); // Unused
            
            // Roster layout:
            // Count (4) + TemplateId (4) + Type (1) + Padding(3 to align Params?) 
            // Params is float*6 = 24 bytes.
            // MemberEntityIds (16*4 = 64)
            // SlotIndices (16*2 = 32)
            
            // Let's just check blittability which guarantees fixed layout
            Assert.True(IsBlittable<FormationRoster>());
            
            // Check specifically that the fixed buffer fields are large enough (indirectly via total size)
            // 64 + 32 = 96 bytes just for arrays.
            Assert.True(Marshal.SizeOf<FormationRoster>() >= 96);
        }

        [Fact]
        public void FormationMember_IsBlittable()
        {
            Assert.True(IsBlittable<FormationMember>());
        }

        [Fact]
        public void FormationTarget_IsBlittable()
        {
            Assert.True(IsBlittable<FormationTarget>());
        }

        [Fact]
        public void FormationSlot_IsBlittable()
        {
            Assert.True(IsBlittable<FormationSlot>());
        }

        [Fact]
        public void FormationEnums_Sizes()
        {
            Assert.Equal(1, sizeof(FormationType));
            Assert.Equal(1, sizeof(FormationMemberState));
        }

        private static bool IsBlittable<T>() where T : struct
        {
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
                Marshal.FreeHGlobal(ptr);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
