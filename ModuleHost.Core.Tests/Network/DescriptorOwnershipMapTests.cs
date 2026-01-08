using System;
using Xunit;
using ModuleHost.Core.Network;
using Fdp.Kernel; // For component types

namespace ModuleHost.Core.Tests.Network
{
    public class DescriptorOwnershipMapTests
    {
        [Fact]
        public void RegisterMapping_StoresComponentsCorrectly()
        {
            var map = new DescriptorOwnershipMap();
            
            map.RegisterMapping(
                descriptorTypeId: 1,
                typeof(Position),
                typeof(Velocity)
            );
            
            var components = map.GetComponentsForDescriptor(1);
            
            Assert.Equal(2, components.Length);
            Assert.Contains(typeof(Position), components);
            Assert.Contains(typeof(Velocity), components);
        }
        
        [Fact]
        public void GetComponentsForDescriptor_UnknownId_ReturnsEmpty()
        {
            var map = new DescriptorOwnershipMap();
            
            var components = map.GetComponentsForDescriptor(999);
            
            Assert.Empty(components);
        }
        
        [Fact]
        public void GetDescriptorForComponent_ReverseLookup_ReturnsCorrectId()
        {
            var map = new DescriptorOwnershipMap();
            
            map.RegisterMapping(
                descriptorTypeId: 1,
                typeof(Position)
            );
            
            var descriptorId = map.GetDescriptorForComponent(typeof(Position));
            
            Assert.Equal(1, descriptorId);
        }
        
        [Fact]
        public void GetDescriptorForComponent_UnknownType_ReturnsZero()
        {
            var map = new DescriptorOwnershipMap();
            
            var descriptorId = map.GetDescriptorForComponent(typeof(Position));
            
            Assert.Equal(0, descriptorId);
        }
        
        [Fact]
        public void RegisterMapping_DuplicateId_Overwrites()
        {
            // Note: The instruction implementation of RegisterMapping blindly adds to internal dict.
            // If the implementation keeps appending components, this test might need adjustment based on actual implementation.
            // Let's assume standard dictionary behavior or replacement.
            // Checking implementation via viewing source might be prudent, but I'll write the test as instructed first.
            
            var map = new DescriptorOwnershipMap();
            
            map.RegisterMapping(1, typeof(Position));
            map.RegisterMapping(1, typeof(Velocity)); // Same ID
            
            var components = map.GetComponentsForDescriptor(1);
            
            // Should have only Velocity (overwritten)
            Assert.Single(components);
            Assert.Contains(typeof(Velocity), components);
        }
    }
}
