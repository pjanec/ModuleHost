using System;
using System.Collections.Generic;
using Fdp.Kernel;
using Fdp.Kernel.Tkb;
using ModuleHost.Core.Network;
using ModuleHost.Core.Network.Interfaces;
using ModuleHost.Core.Network.Messages;
using Xunit;

namespace ModuleHost.Core.Tests.Network
{
    public class NetworkFoundationTests
    {
        // 1. NetworkConstants Tests
        [Fact]
        public void NetworkConstants_HaveCorrectValues()
        {
            Assert.Equal(0, NetworkConstants.ENTITY_MASTER_DESCRIPTOR_ID);
            Assert.Equal(1, NetworkConstants.ENTITY_STATE_DESCRIPTOR_ID);
            Assert.Equal(2, NetworkConstants.WEAPON_STATE_DESCRIPTOR_ID);
            Assert.Equal(900, NetworkConstants.ENTITY_LIFECYCLE_STATUS_ID);
            Assert.Equal(901, NetworkConstants.OWNERSHIP_UPDATE_ID);
            Assert.Equal(300, NetworkConstants.GHOST_TIMEOUT_FRAMES);
            Assert.Equal(300, NetworkConstants.RELIABLE_INIT_TIMEOUT_FRAMES);
        }

        // 2. MasterFlags Tests
        [Fact]
        public void MasterFlags_DefaultIsNone()
        {
            var descriptor = new EntityMasterDescriptor();
            Assert.Equal(MasterFlags.None, descriptor.Flags);
        }

        [Fact]
        public void MasterFlags_CanSetReliableInit()
        {
            var descriptor = new EntityMasterDescriptor();
            descriptor.Flags = MasterFlags.ReliableInit;
            Assert.True(descriptor.Flags.HasFlag(MasterFlags.ReliableInit));
        }

        [Fact]
        public void MasterFlags_CanCombineFlags()
        {
            // Even though we only have one flag now, we test the bitwise capability
            var flags = MasterFlags.None | MasterFlags.ReliableInit;
            Assert.Equal(MasterFlags.ReliableInit, flags);
            
            flags &= ~MasterFlags.ReliableInit;
            Assert.Equal(MasterFlags.None, flags);
        }

        // 3. Composite Key Packing Tests
        [Fact]
        public void PackKey_WithSimpleValues_ReturnsCorrectKey()
        {
            long typeId = 123;
            long instanceId = 456;
            long packed = OwnershipExtensions.PackKey(typeId, instanceId);
            
            // Expected: typeId in upper 32 bits, instanceId in lower 32 bits
            // 123l << 32 = 528280977408
            // 528280977408 | 456 = 528280977864
            long expected = 528280977864;
            
            Assert.Equal(expected, packed);
        }

        [Fact]
        public void UnpackKey_RoundTrip_RestoresOriginalValues()
        {
            long typeId = 98765;
            long instanceId = 12345;
            long packed = OwnershipExtensions.PackKey(typeId, instanceId);
            
            var (unpackedType, unpackedInstance) = OwnershipExtensions.UnpackKey(packed);
            
            Assert.Equal(typeId, unpackedType);
            Assert.Equal(instanceId, unpackedInstance);
        }

        [Fact]
        public void PackKey_WithMaxValues_RoundTripsCorrectly()
        {
            long typeId = 2147483647; 
            long instanceId = 4294967295;

            long packed = OwnershipExtensions.PackKey(typeId, instanceId);
            var (unpackedType, unpackedInstance) = OwnershipExtensions.UnpackKey(packed);
            
            Assert.Equal(typeId, unpackedType);
            Assert.Equal(instanceId, unpackedInstance);
        }

        [Fact]
        public void PackKey_WithZeroValues_ReturnsZero()
        {
            long packed = OwnershipExtensions.PackKey(0, 0);
            Assert.Equal(0, packed);
        }

        // 4. DescriptorAuthorityChanged Event Tests
        [Fact]
        public void DescriptorAuthorityChanged_Construction_StoresValues()
        {
            var evt = new DescriptorAuthorityChanged
            {
                Entity = new Entity(100, 1),
                DescriptorTypeId = 5,
                IsNowOwner = true,
                NewOwnerId = 10
            };
            
            Assert.Equal(100, evt.Entity.Index);
            Assert.Equal(1, evt.Entity.Generation);
            Assert.Equal(5, evt.DescriptorTypeId);
            Assert.True(evt.IsNowOwner);
            Assert.Equal(10, evt.NewOwnerId);
        }
        
        [Fact]
        public void DescriptorAuthorityChanged_DefaultIsInvalid()
        {
            var evt = new DescriptorAuthorityChanged();
            Assert.Equal(Entity.Null, evt.Entity);
            Assert.False(evt.IsNowOwner);
            Assert.Equal(0, evt.DescriptorTypeId);
        }

        // 5. Interface Mock Tests
        private class MockStrategy : IOwnershipDistributionStrategy
        {
            public int? ReturnValue { get; set; }
            public int? GetInitialOwner(long descriptorTypeId, DISEntityType entityType, int masterNodeId, long instanceId)
            {
                return ReturnValue;
            }
        }

        private class MockTopology : INetworkTopology
        {
            public int LocalNodeId => 1;
            public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
            {
                return new[] { 2, 3 };
            }
        }

        private class MockTkb : ITkbDatabase
        {
            public TkbTemplate? GetTemplateByEntityType(DISEntityType entityType) => null;
            public TkbTemplate? GetTemplateByName(string templateName) => null;
        }

        [Fact]
        public void Interfaces_CanBeImplemented()
        {
            var strategy = new MockStrategy { ReturnValue = 5 };
            Assert.Equal(5, strategy.GetInitialOwner(0, new DISEntityType(), 1, 0));
            
            var topology = new MockTopology();
            Assert.Equal(1, topology.LocalNodeId);
            Assert.Equal(new[] { 2, 3 }, topology.GetExpectedPeers(new DISEntityType()));
            
            var tkb = new MockTkb();
            Assert.Null(tkb.GetTemplateByName("test"));
        }

        // 6. EntityLifecycleStatusDescriptor Tests
        [Fact]
        public void EntityLifecycleStatusDescriptor_PropertiesCanBeSet()
        {
            var msg = new EntityLifecycleStatusDescriptor
            {
                EntityId = 999,
                NodeId = 2,
                State = EntityLifecycle.Active,
                Timestamp = 123456789
            };
            
            Assert.Equal(999, msg.EntityId);
            Assert.Equal(2, msg.NodeId);
            Assert.Equal(EntityLifecycle.Active, msg.State);
            Assert.Equal(123456789, msg.Timestamp);
        }
        
        [Fact]
        public void EntityLifecycleStatusDescriptor_DefaultValues()
        {
            var msg = new EntityLifecycleStatusDescriptor();
            Assert.Equal(0, msg.EntityId);
            Assert.Equal(0, msg.NodeId);
            // Default enum value is typically 0, which corresponds to first enum member.
            // Assuming EntityLifecycle.Uninitialized or similar is 0.
            // We should just check it is default
            Assert.Equal(default(EntityLifecycle), msg.State);
        }

        // 7. DISEntityType Tests
        [Fact]
        public void DISEntityType_Equality_WorksCorrectly()
        {
            var t1 = new DISEntityType { Kind = 1, Domain = 2, Country = 3 };
            var t2 = new DISEntityType { Kind = 1, Domain = 2, Country = 3 };
            var t3 = new DISEntityType { Kind = 1, Domain = 2, Country = 4 };
            
            // Default struct equality (ValueType.Equals) usually matches fields
            Assert.Equal(t1, t2);
            Assert.NotEqual(t1, t3);
        }

        [Fact]
        public void DISEntityType_HashCode_IsConsistent()
        {
            var t1 = new DISEntityType { Kind = 1, Domain = 2 };
            var t2 = new DISEntityType { Kind = 1, Domain = 2 };
            
            Assert.Equal(t1.GetHashCode(), t2.GetHashCode());
        }

        // 8. DefaultOwnershipStrategy Tests
        [Fact]
        public void DefaultOwnershipStrategy_AlwaysReturnsNull()
        {
            var strategy = new DefaultOwnershipStrategy();
            var result = strategy.GetInitialOwner(1, new DISEntityType(), 10, 0);
            
            Assert.Null(result);
        }
        
        [Fact]
        public void DefaultOwnershipStrategy_ReturnsNullWithDifferentInputs()
        {
            var strategy = new DefaultOwnershipStrategy();
            // Try with max values to ensure no overflow or weird logic
            var result = strategy.GetInitialOwner(long.MaxValue, new DISEntityType(), int.MaxValue, long.MaxValue);
            Assert.Null(result);
        }
    }
}
