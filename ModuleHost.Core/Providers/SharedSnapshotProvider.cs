// File: ModuleHost.Core/Providers/SharedSnapshotProvider.cs
using System;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256 _unionMask;  // NEW: Union of all module requirements
        private readonly SnapshotPool _pool;      // NEW: Pool for reuse
        
        private EntityRepository? _currentSnapshot;
        private int _activeReaders;               // NEW: Reference count
        private uint _lastSeenTick;
        private readonly object _lock = new object();
        
        public SharedSnapshotProvider(
            EntityRepository liveWorld,
            EventAccumulator eventAccumulator,
            BitMask256 unionMask,                // NEW parameter
            SnapshotPool pool)                   // NEW parameter
        {
            _liveWorld = liveWorld;
            _eventAccumulator = eventAccumulator;
            _unionMask = unionMask;
            _pool = pool;
        }
        
        public ISimulationView AcquireView()
        {
            lock (_lock)
            {
                if (_currentSnapshot == null)
                {
                    // First reader in convoy: create snapshot
                    _currentSnapshot = _pool.Get();
                    
                    // Sync using UNION MASK (critical)
                    _currentSnapshot.SyncFrom(_liveWorld, _unionMask);
                    
                    // Sync events
                    _eventAccumulator.FlushToReplica(
                        _currentSnapshot.Bus, 
                        _lastSeenTick
                    );
                    
                    _lastSeenTick = _liveWorld.GlobalVersion;
                }
                else 
                {
                    // Console.WriteLine("[Shared] Reusing snapshot");
                }
                
                _activeReaders++;
                return _currentSnapshot;
            }
        }
        
        public void ReleaseView(ISimulationView view)
        {
            lock (_lock)
            {
                _activeReaders--;
                
                if (_activeReaders == 0)
                {
                    // Last reader finished: return to pool
                    if (_currentSnapshot != null)
                    {
                        _pool.Return(_currentSnapshot);
                        _currentSnapshot = null;
                    }
                }
                else if (_activeReaders < 0)
                {
                    throw new InvalidOperationException(
                        "ReleaseView called more than AcquireView");
                }
            }
        }
        
        public void Update()
        {
            // SharedProvider is lazy: sync happens on first AcquireView
            // This method can be empty or used for diagnostics
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.Shared;
        
        public void Dispose()
        {
            lock (_lock)
            {
                if (_currentSnapshot != null && _activeReaders == 0)
                {
                    _pool.Return(_currentSnapshot);
                    _currentSnapshot = null;
                }
            }
        }
    }
}
