// File: ModuleHost.Core.Tests/ModuleCircuitBreakerTests.cs

using Xunit;
using ModuleHost.Core.Resilience;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ModuleHost.Core.Tests
{
    public class ModuleCircuitBreakerTests
    {
        [Fact]
        public void CircuitBreaker_InitialState_Closed()
        {
            var breaker = new ModuleCircuitBreaker();
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_SingleFailure_StaysClosed()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Test failure");
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(1, breaker.FailureCount);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_ThresholdExceeded_OpensCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            breaker.RecordFailure("Failure 3");
            
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_Open_TransitionsToHalfOpenAfterTimeout()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 100);
            
            // Open the circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
            
            // Wait for reset timeout
            Thread.Sleep(150);
            
            // Should transition to HalfOpen
            Assert.True(breaker.CanRun());
            Assert.Equal(CircuitState.HalfOpen, breaker.State);
        }
        
        [Fact]
        public void CircuitBreaker_HalfOpen_SuccessClosesCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 50);
            
            // Open circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            
            // Wait and transition to HalfOpen
            Thread.Sleep(100);
            breaker.CanRun(); // Triggers transition
            
            // Successful execution
            breaker.RecordSuccess();
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(0, breaker.FailureCount);
        }
        
        [Fact]
        public void CircuitBreaker_HalfOpen_FailureReopensCircuit()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 50);
            
            // Open circuit
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            
            // Wait and transition to HalfOpen
            Thread.Sleep(100);
            breaker.CanRun();
            
            // Failed execution - should reopen immediately
            breaker.RecordFailure("Failure 3");
            
            Assert.Equal(CircuitState.Open, breaker.State);
            Assert.False(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_Success_ResetsFailureCount()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 3);
            
            breaker.RecordFailure("Failure 1");
            breaker.RecordFailure("Failure 2");
            Assert.Equal(2, breaker.FailureCount);
            
            breaker.RecordSuccess();
            
            Assert.Equal(0, breaker.FailureCount);
            Assert.Equal(CircuitState.Closed, breaker.State);
        }
        
        [Fact]
        public void CircuitBreaker_Reset_ClosesCircuitManually()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 1);
            
            breaker.RecordFailure("Failure");
            Assert.Equal(CircuitState.Open, breaker.State);
            
            breaker.Reset();
            
            Assert.Equal(CircuitState.Closed, breaker.State);
            Assert.Equal(0, breaker.FailureCount);
            Assert.True(breaker.CanRun());
        }
        
        [Fact]
        public void CircuitBreaker_InvalidThreshold_Throws()
        {
            Assert.Throws<ArgumentException>(() => 
                new ModuleCircuitBreaker(failureThreshold: 0));
            Assert.Throws<ArgumentException>(() => 
                new ModuleCircuitBreaker(failureThreshold: -1));
        }
        
        [Fact]
        public async Task CircuitBreaker_ThreadSafe_ConcurrentAccess()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 100);
            
            // Multiple threads recording failures concurrently
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        breaker.RecordFailure($"Failure {j}");
                        Thread.Sleep(1);
                        breaker.CanRun();
                    }
                });
            }
            
            await Task.WhenAll(tasks);
            
            // Should have recorded 100 failures total
            Assert.Equal(100, breaker.FailureCount);
        }

        [Fact]
        public async Task CircuitBreaker_ConcurrentStateTransition_ThreadSafe()
        {
            var breaker = new ModuleCircuitBreaker(failureThreshold: 2, resetTimeoutMs: 100);
            
            // Open the circuit
            breaker.RecordFailure("Initial 1");
            breaker.RecordFailure("Initial 2");
            Assert.Equal(CircuitState.Open, breaker.State);
            
            // Wait almost to reset timeout
            Thread.Sleep(90);
            
            // Now spawn 10 threads that simultaneously:
            // - Check CanRun() (might trigger HalfOpen transition)
            // - Record failures
            var barrier = new Barrier(10);
            var tasks = new Task[10];
            
            for (int i = 0; i < 10; i++)
            {
                int threadId = i;
                tasks[i] = Task.Run(() =>
                {
                    barrier.SignalAndWait(); // Synchronize start
                    
                    if (threadId < 5)
                    {
                        // Some threads check if they can run (potentially triggering timeout reset)
                        breaker.CanRun();
                    }
                    else
                    {
                        // Others try to record failure (requires lock)
                        breaker.RecordFailure($"Concurrent {threadId}");
                    }
                });
            }
            
            await Task.WhenAll(tasks);
            
            // Circuit should end in a valid state (not corrupted)
            var finalState = breaker.State;
            Assert.True(
                finalState == CircuitState.Open || 
                finalState == CircuitState.HalfOpen,
                $"Circuit in unexpected state: {finalState}");
        }
    }
}
