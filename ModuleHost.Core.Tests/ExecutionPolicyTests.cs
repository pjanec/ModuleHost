using Xunit;
using ModuleHost.Core.Abstractions;
using System;

namespace ModuleHost.Core.Tests
{
    public class ExecutionPolicyTests
    {
        [Fact]
        public void ExecutionPolicy_Synchronous_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.Synchronous();
            
            Assert.Equal(RunMode.Synchronous, policy.Mode);
            Assert.Equal(DataStrategy.Direct, policy.Strategy);
            Assert.Equal(60, policy.TargetFrequencyHz);
            Assert.True(policy.MaxExpectedRuntimeMs > 0);
        }
        
        [Fact]
        public void ExecutionPolicy_FastReplica_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.FastReplica();
            
            Assert.Equal(RunMode.FrameSynced, policy.Mode);
            Assert.Equal(DataStrategy.GDB, policy.Strategy);
            Assert.Equal(60, policy.TargetFrequencyHz);
        }
        
        [Fact]
        public void ExecutionPolicy_SlowBackground_HasCorrectDefaults()
        {
            var policy = ExecutionPolicy.SlowBackground(10);
            
            Assert.Equal(RunMode.Asynchronous, policy.Mode);
            Assert.Equal(DataStrategy.SoD, policy.Strategy);
            Assert.Equal(10, policy.TargetFrequencyHz);
            Assert.True(policy.MaxExpectedRuntimeMs >= 100);
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsSyncWithNonDirect()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Synchronous,
                Strategy = DataStrategy.GDB // INVALID
            };
            
            Assert.Throws<InvalidOperationException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsDirectWithAsync()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Asynchronous,
                Strategy = DataStrategy.Direct // INVALID
            };
            
            Assert.Throws<InvalidOperationException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_Validate_RejectsInvalidFrequency()
        {
            var policy = new ExecutionPolicy
            {
                Mode = RunMode.Asynchronous,
                Strategy = DataStrategy.SoD,
                TargetFrequencyHz = 100 // >60 Hz
            };
            
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validate());
        }
        
        [Fact]
        public void ExecutionPolicy_FluentAPI_WorksCorrectly()
        {
            var policy = ExecutionPolicy.Custom()
                .WithMode(RunMode.FrameSynced)
                .WithStrategy(DataStrategy.GDB)
                .WithFrequency(30)
                .WithTimeout(50);
            
            Assert.Equal(RunMode.FrameSynced, policy.Mode);
            Assert.Equal(DataStrategy.GDB, policy.Strategy);
            Assert.Equal(30, policy.TargetFrequencyHz);
            Assert.Equal(50, policy.MaxExpectedRuntimeMs);
        }
    }
}
