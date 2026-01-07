// File: ModuleHost.Core/Providers/SharedSnapshotProvider.cs
using System;
using System.Threading;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Providers
{
    /// <summary>
    /// Shared snapshot provider (convoy pattern).
    /// Multiple modules share one snapshot with reference counting.
    /// Supports async execution by detaching busy snapshots.
    /// </summary>
    public sealed class SharedSnapshotProvider : ISnapshotProvider, IDisposable
    {
        private readonly EntityRepository _liveWorld;
        private readonly EventAccumulator _eventAccumulator;
        private readonly BitMask256 _componentMask;
        private readonly int _expectedModuleCount;
        private readonly Action<EntityRepository>? _schemaSetup;
        
        // Active snapshots and their ref counts.
        private readonly Dictionary<ISimulationView, int> _activeSnapshots = new();
        
        // The snapshot currently available for this frame (if any).
        private EntityRepository? _currentRecyclableSnapshot;
        
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
                if (_currentRecyclableSnapshot != null)
                {
                    // Check if it's currently in use (leased by previous frame modules)
                    if (_activeSnapshots.TryGetValue(_currentRecyclableSnapshot, out var count) && count > 0)
                    {
                        // It's busy. Detach it from being "current".
                        // We will create a new one for this frame on next Acquire.
                        // The old one remains in _activeSnapshots until released.
                        _currentRecyclableSnapshot = null;
                        
                        // _lastSeenTick remains valid as the "start of this frame" trigger
                    }
                    else
                    {
                        // It's free. Reuse and update it.
                        _currentRecyclableSnapshot.SyncFrom(_liveWorld, _componentMask);
                        _eventAccumulator.FlushToReplica(_currentRecyclableSnapshot.Bus, _lastSeenTick);
                        _lastSeenTick = _liveWorld.GlobalVersion;
                    }
                }
                else
                {
                    // Update tick reference so new snapshots get correct event range
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
                // Create or reuse
                if (_currentRecyclableSnapshot == null)
                {
                    _currentRecyclableSnapshot = new EntityRepository();
                    _schemaSetup?.Invoke(_currentRecyclableSnapshot);
                    
                    _currentRecyclableSnapshot.SyncFrom(_liveWorld, _componentMask);
                    _eventAccumulator.FlushToReplica(_currentRecyclableSnapshot.Bus, _lastSeenTick);
                    
                    // Track it
                    _activeSnapshots[_currentRecyclableSnapshot] = 0;
                }
                
                _activeSnapshots[_currentRecyclableSnapshot]++;
                return _currentRecyclableSnapshot;
            }
        }
        
        /// <summary>
        /// Releases view (decrements ref count).
        /// When count reaches 0, disposes/soft-clears logic applied.
        /// </summary>
        public void ReleaseView(ISimulationView view)
        {
            lock (_syncLock)
            {
                if (_activeSnapshots.ContainsKey(view))
                {
                    _activeSnapshots[view]--;
                    
                    if (_activeSnapshots[view] <= 0)
                    {
                        if (view != _currentRecyclableSnapshot)
                        {
                            // It was detached (from an old frame) and now everyone is done with it.
                            // Fully dispose it.
                             if (view is EntityRepository repo)
                             {
                                 try { repo.Dispose(); } catch {} // Paramoid safety
                             }
                             _activeSnapshots.Remove(view);
                        }
                        else
                        {
                            // It is the current recyclable snapshot.
                            // Keep it alive, but verify state?
                            // Logic: it is kept in _currentRecyclableSnapshot and _activeSnapshots (count 0).
                            if (view is EntityRepository repo)
                            {
                                repo.SoftClear();
                            }
                        }
                    }
                    else if (_activeSnapshots[view] < 0)
                    {
                         // Should not happen
                         _activeSnapshots[view] = 0;
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                foreach (var kvp in _activeSnapshots)
                {
                    if (kvp.Key is IDisposable d) d.Dispose();
                }
                _activeSnapshots.Clear();
                _currentRecyclableSnapshot = null;
            }
        }
    }
}
