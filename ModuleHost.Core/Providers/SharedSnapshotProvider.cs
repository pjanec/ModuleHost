// File: ModuleHost.Core/Providers/SharedSnapshotProvider.cs
using System;
using System.Threading;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Shared snapshot provider (convoy pattern).
    /// Multiple modules share one snapshot with reference counting.
    /// </summary>
    public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256 _componentMask;
        private readonly int _expectedModuleCount;
        private readonly Action<EntityRepository>? _schemaSetup;
        
        private EntityRepository? _sharedSnapshot;
        private int _referenceCount;
        private uint _lastSeenTick;
        private readonly object _syncLock = new object();
        
        public SharedSnapshotProvider(
            EntityRepository liveWorld,
            EventAccumulator eventAccumulator,
            BitMask256 componentMask,
            int expectedModuleCount,
            Action<EntityRepository>? schemaSetup = null)
        {
            _liveWorld = liveWorld ?? throw new ArgumentNullException(nameof(liveWorld));
            _eventAccumulator = eventAccumulator ?? throw new ArgumentNullException(nameof(eventAccumulator));
            _componentMask = componentMask;
            _expectedModuleCount = expectedModuleCount;
            _schemaSetup = schemaSetup;
        }
        
        public SnapshotProviderType ProviderType => SnapshotProviderType.Shared;
        
        /// <summary>
        /// Update syncs shared snapshot (if active).
        /// </summary>
        public void Update()
        {
            lock (_syncLock)
            {
                if (_sharedSnapshot != null)
                {
                    // Sync shared snapshot
                    _sharedSnapshot.SyncFrom(_liveWorld, _componentMask);
                    _eventAccumulator.FlushToReplica(_sharedSnapshot.Bus, _lastSeenTick);
                    _lastSeenTick = _liveWorld.GlobalVersion;
                }
            }
        }
        
        /// <summary>
        /// Acquires shared snapshot (increments ref count).
        /// First acquire syncs, subsequent acquires reuse.
        /// </summary>
        public ISimulationView AcquireView()
        {
            lock (_syncLock)
            {
                // First acquire? Create and sync
                if (_sharedSnapshot == null)
                {
                    _sharedSnapshot = new EntityRepository();
                    _schemaSetup?.Invoke(_sharedSnapshot);
                    
                    _sharedSnapshot.SyncFrom(_liveWorld, _componentMask);
                    _eventAccumulator.FlushToReplica(_sharedSnapshot.Bus, _lastSeenTick);
                    _lastSeenTick = _liveWorld.GlobalVersion;
                }
                
                // Increment ref count (thread-safe)
                Interlocked.Increment(ref _referenceCount);
                
                return _sharedSnapshot;
            }
        }
        
        /// <summary>
        /// Releases view (decrements ref count).
        /// When count reaches 0, disposes shared snapshot.
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            lock (_syncLock)
            {
                _referenceCount--;
                
                if (_referenceCount == 0)
                {
                    _sharedSnapshot?.SoftClear();
                    _sharedSnapshot?.Dispose();
                    _sharedSnapshot = null;
                }
                else if (_referenceCount < 0)
                {
                     throw new InvalidOperationException("ReleaseView called more times than AcquireView");
                }
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                _sharedSnapshot?.Dispose();
                _sharedSnapshot = null;
            }
        }
    }
}
