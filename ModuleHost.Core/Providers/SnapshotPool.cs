using System.Collections.Concurrent;
using Fdp.Kernel;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Thread-safe pool of EntityRepository instances for snapshot reuse.
    /// Eliminates GC allocations by recycling repositories.
    /// </summary>
    public class SnapshotPool
    {
        private readonly ConcurrentStack<EntityRepository> _pool = new();
        private readonly Action<EntityRepository>? _schemaSetup;
        private readonly int _warmupCount;
        
        public SnapshotPool(Action<EntityRepository>? schemaSetup, int warmupCount = 0)
        {
            _schemaSetup = schemaSetup;
            _warmupCount = warmupCount;
            
            // Pre-populate pool
            for (int i = 0; i < warmupCount; i++)
            {
                var repo = CreateNew();
                _pool.Push(repo);
            }
        }
        
        /// <summary>
        /// Get a repository from pool or create new if empty.
        /// </summary>
        public EntityRepository Get()
        {
            if (_pool.TryPop(out var repo))
            {
                return repo;
            }
            
            return CreateNew();
        }
        
        /// <summary>
        /// Return repository to pool after clearing.
        /// </summary>
        public void Return(EntityRepository repo)
        {
            // CRITICAL: Clear state but keep buffer capacity
            // Assuming SoftClear is a method on EntityRepository. 
            // If it doesn't exist, we might need to use Clear() or implement it.
            // The instructions say "SoftClear()", so I will use that.
            // If compilation fails, I'll check EntityRepository.
            repo.SoftClear();
            
            _pool.Push(repo);
        }
        
        private EntityRepository CreateNew()
        {
            var repo = new EntityRepository();
            _schemaSetup?.Invoke(repo);
            return repo;
        }
        
        /// <summary>
        /// Statistics for monitoring
        /// </summary>
        public int PooledCount => _pool.Count;
    }
}
