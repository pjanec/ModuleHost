using Xunit;
using Fdp.Kernel;
using ModuleHost.Core.Providers;
using System.Threading.Tasks;
using System.Collections.Generic;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Tests.Concurrency
{
    public class ProviderConcurrencyTests
    {
        struct Pos { public float X; }
        
        [Fact]
        public async Task OnDemandProvider_ConcurrentAcquire_ThreadSafe()
        {
            using var live = new EntityRepository();
            live.RegisterComponent<Pos>();
            
            for (int i = 0; i < 100; i++)
            {
                var e = live.CreateEntity();
                live.AddComponent(e, new Pos { X = i });
            }
            
            var acc = new EventAccumulator();
            var mask = new BitMask256();
            mask.SetBit(ComponentType<Pos>.ID);
            
            var provider = new OnDemandProvider(live, acc, mask);
            
            // 10 threads concurrently acquiring and releasing
            var tasks = new Task[10];
            var views = new List<ISimulationView>[10];
            
            for (int i = 0; i < 10; i++)
            {
                views[i] = new List<ISimulationView>();
                int threadId = i;
                
                tasks[i] = Task.Run(() =>
                {
                    // Each thread acquires 5 views
                    for (int j = 0; j < 5; j++)
                    {
                        var view = provider.AcquireView();
                        lock (views[threadId]) 
                        {
                            views[threadId].Add(view);
                        }
                        
                        // Verify view has data
                        int count = view.Query().Build().Count();
                        
                        Assert.Equal(100, count);
                    }
                });
            }
            
            await Task.WhenAll(tasks);
            
            // Release all views
            for (int i = 0; i < 10; i++)
            {
                foreach (var view in views[i])
                {
                    provider.ReleaseView(view);
                }
            }
            
            // No exceptions = thread-safe ✅
        }
    }
}
