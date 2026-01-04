# Detailed Design Overview - Module Host

**Date:** January 4, 2026  
**Version:** 2.0 (Hybrid GDB+SoD Architecture)  
**Phase:** Detailed Design  
**Based On:**choice
- [reference-archive/FDP-GDB-SoD-unified.md](reference-archive/FDP-GDB-SoD-unified.md) (Hybrid strategy)
- [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md)

---

## Table of Contents

1. [Architecture Layers](#architecture-layers)
2. [Layer 0: FDP Synchronization Core (NEW)](#layer-0-fdp-synchronization-core-new)
3. [Layer 1: Snapshot Providers (Strategy Pattern)](#layer-1-snapshot-providers-strategy-pattern)
4. [Layer 2: Module Framework](#layer-2-module-framework)
5. [Layer 3: Host Kernel (3-World Topology)](#layer-3-host-kernel-3-world-topology)
6. [Layer 4: Command Buffer System](#layer-4-command-buffer-system)
7. [Layer 5: Coordinate Services](#layer-5-coordinate-services)
8. [Layer 6: DDS Gateway](#layer-6-dds-gateway)
9. [Layer 7: Entity Lifecycle (ELM)](#layer-7-entity-lifecycle-elm)
10. [Layer 8: Resilience & Safety](#layer-8-resilience--safety)
11. [Dependency Graph](#dependency-graph)
12. [Implementation Order](#implementation-order)

---

## Architecture Layers

```
┌─────────────────────────────────────────────────┐
│  Layer 8: Resilience (Watchdogs, Breakers)     │
├─────────────────────────────────────────────────┤
│  Layer 7: ELM (Entity Lifecycle Management)     │
├─────────────────────────────────────────────────┤
│  Layer 6: DDS Gateway (Network Sync)            │
├─────────────────────────────────────────────────┤
│  Layer 5: Coordinate Services (Geo Transform)   │
├─────────────────────────────────────────────────┤
│  Layer 4: Command Buffer (Async Structural)     │
├─────────────────────────────────────────────────┤
│  Layer 3: Host Kernel (3-World Orchestration)   │ ← UPDATED
├─────────────────────────────────────────────────┤
│  Layer 2: Module Framework (Plugin System)      │
├─────────────────────────────────────────────────┤
│  Layer 1: Snapshot Providers (GDB/SoD Strategy) │ ← NEW
├─────────────────────────────────────────────────┤
│  Layer 0: FDP Synchronization Core             │ ← NEW (SyncFrom API)
├─────────────────────────────────────────────────┤
│  FDP EntityRepository (Existing Kernel)         │
└─────────────────────────────────────────────────┘
```

**Key Changes from v1.0:**
- Layer 0 (NEW): FDP kernel synchronization primitives (`SyncFrom`, `EventAccumulator`)
- Layer 1 (UPDATED): Strategy pattern for GDB vs SoD providers
- Layer 3 (UPDATED): 3-world topology (Live + Fast GDB + Slow SoD/GDB)

---

## Layer 0: FDP Synchronization Core (NEW)

**Purpose:** Core synchronization APIs that enable both GDB and SoD strategies.

### 0.1 `EntityRepository.SyncFrom()`

**File:** `Fdp.Kernel/EntityRepository.Sync.cs` (partial class)

```csharp
public sealed partial class EntityRepository : ISimulationView
{
    /// <summary>
    /// Synchronizes this repository to match the source.
    /// Used by both GDB (full sync) and SoD (filtered sync).
    /// </summary>
    /// <param name="source">Source repository (live world)</param>
    /// <param name="mask">Component filter (null = all components)</param>
    public void SyncFrom(EntityRepository source, BitMask256? mask = null)
    {
        // 1. Sync entity metadata
        _entityIndex.SyncFrom(source._entityIndex);
        
        // 2. Sync component tables
        foreach (var typeId in _componentTables.Keys)
        {
            // Skip if filtered out
            if (mask.HasValue && !mask.Value.IsSet(typeId)) continue;
            
            var myTable = _componentTables[typeId];
            var srcTable = source._componentTables[typeId];
            
            // Delegate to table-specific sync
            if (myTable is IUnmanagedComponentTable tier1)
            {
                ((NativeChunkTable)tier1).SyncDirtyChunks(
                    (NativeChunkTable)srcTable);
            }
            else
            {
                ((ManagedComponentTable)myTable).SyncDirtyChunks(
                    (ManagedComponentTable)srcTable);
            }
        }
        
        // 3. Sync global version
        this._globalVersion = source._globalVersion;
    }
}
```

**Design Notes:**
- **GDB usage:** `replica.SyncFrom(live)` - copies all dirty chunks
- **SoD usage:** `snapshot.SyncFrom(live, aiMask)` - copies only filtered chunks
- Dirty tracking prevents copying unchanged chunks (optimization)
- Both Tier 1 and Tier 2 use same pattern

---

### 0.2 `NativeChunkTable.SyncDirtyChunks()`

```csharp
public void SyncDirtyChunks(NativeChunkTable<T> source)
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        // **OPTIMIZATION:** Version check
        uint srcVer = source.GetChunkVersion(i);
        if (_chunkVersions[i] == srcVer)
            continue;  // Chunk unchanged, skip memcpy
        
        // Liveness check
        if (!source.IsChunkAllocated(i))
        {
            if (this.IsChunkAllocated(i)) 
                this.ClearChunk(i);
            continue;
        }
        
        // **THE COPY:** memcpy (Tier 1)
        EnsureChunkAllocated(i);
        Unsafe.CopyBlock(
            this.GetChunkDataPtr(i),
            source.GetChunkDataPtr(i),
            FdpConfig.CHUNK_SIZE_BYTES  // 64KB
        );
        
        // Update version
        _chunkVersions[i] = srcVer;
    }
}
```

**Performance:** <2ms for 100K entities (30% dirty chunks)

---

### 0.3 `EventAccumulator`

**File:** `Fdp.Kernel/EventAccumulator.cs`

```csharp
public class EventAccumulator
{
    private readonly Queue<FrameEventData> _history = new();
    
    /// <summary>
    /// Captures events from live bus (doesn't clear them).
    /// </summary>
    public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex)
    {
        var frameData = liveBus.ExtractAndRetireBuffers();
        frameData.FrameIndex = frameIndex;
        _history.Enqueue(frameData);
    }
    
    /// <summary>
    /// Flushes accumulated history to replica bus.
    /// </summary>
    public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick)
    {
        while (_history.TryDequeue(out var frameData))
        {
            if (frameData.FrameIndex <= lastSeenTick)
                continue;  // Skip old events
            
            // Inject into replica bus (appends to current)
            foreach (var stream in frameData.NativeStreams)
            {
                replicaBus.InjectIntoCurrent(stream.TypeId, stream.GetRawBytes());
                stream.Dispose();
            }
            
            foreach (var stream in frameData.ManagedStreams)
            {
                replicaBus.InjectManagedIntoCurrent(stream.TypeId, stream.GetList());
            }
        }
    }
}
```

**Design Notes:**
- Bridges live event stream → replica event buses
- Used by both GDB and SoD providers
- Enables slow modules to see accumulated event history

---

## Layer 1: Snapshot Providers (Strategy Pattern)

**Purpose:** Abstract how modules acquire simulation views (GDB vs SoD).

### 1.1 `ISimulationView` (Core Abstraction)

**File:** `ModuleHost.Core/Abstractions/ISimulationView.cs`

```csharp
namespace ModuleHost.Core.Abstractions
{
    /// <summary>
    /// Unified read-only view of simulation state.
    /// Implemented by: EntityRepository (GDB) and SimSnapshot (SoD).
    /// </summary>
    public interface ISimulationView
    {
        // Metadata
        uint Tick { get; }
        float Time { get; }
        
        // Component access (unified)
        ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
        T GetManagedComponentRO<T>(Entity e) where T : class;
        
        // Existence
        bool IsAlive(Entity e);
        
        // Events (accumulated history)
        ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
        
        // Query
        EntityQueryBuilder Query();
    }
}
```

**Design Notes:**
- Simpler than `ISimWorldSnapshot` (fewer properties)
- `EntityRepository` implements natively (GDB zero-cost)
- `SimSnapshot` implements for SoD (pooled wrapper)
- No `IDisposable` (GDB replicas don't need disposal)
- Modules are agnostic to implementation

---

#### `ISnapshotManager`
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Root factory for creating snapshots.
    /// Manages shadow buffer lifecycle and dirty tracking.
    /// </summary>
    public interface ISnapshotManager
    {
        // Snapshot creation (must be called at sync point!)
        ISimWorldSnapshot CreateSnapshot(
            Guid consumerId, 
            ComponentMask componentMask,
            EventTypeMask eventMask);
        
        // Union snapshot for multiple consumers
        ISimWorldSnapshot CreateUnionSnapshot(
            IEnumerable<IModule> readyModules);
        
        // Lifecycle
        void ReleaseShadowBuffer(Guid consumerId);
        
        // Diagnostics
        int GetActiveShadowBufferCount();
        long GetTotalShadowBufferBytes();
    }
}
```

---

### 1.2 Core Classes

#### `SnapshotManager`
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Manages shadow buffers and snapshot creation.
    /// One instance per EntityRepository.
    /// </summary>
    public class SnapshotManager : ISnapshotManager
    {
        // Dependencies
        private readonly EntityRepository _repository;
        
        // State
        private readonly Dictionary<Guid, ShadowBuffer> _shadowBuffers;
        private readonly object _lock = new();
        
        public SnapshotManager(EntityRepository repository);
        
        // Implementation of ISnapshotManager
        public ISimWorldSnapshot CreateSnapshot(...);
        
        // Internal helpers
        private ShadowBuffer GetOrCreateShadowBuffer(Guid id, ComponentMask mask);
        private void UpdateShadowBuffer(ShadowBuffer buffer);
    }
}
```

**Key Responsibilities:**
- Allocate persistent shadow buffers
- Update dirty chunks via memcpy
- Create snapshot wrappers
- Track buffer lifecycle

---

#### `ShadowBuffer`
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Persistent memory buffer for Tier 1 component snapshots.
    /// Reused across multiple frames (updated only when dirty).
    /// </summary>
    public sealed class ShadowBuffer : IDisposable
    {
        // Pinned memory (matches FDP layout)
        public unsafe byte* Data { get; }
        
        // Version tracking per chunk
        public ulong[] ChunkVersions { get; }
        
        // Metadata
        public ComponentMask ComponentMask { get; }
        public int ChunkCount { get; }
        public long TotalBytes { get; }
        
        // Lifecycle
        public ShadowBuffer(int chunkCount, ComponentMask mask);
        public void Dispose();
        
        // Update from live state
        public unsafe void UpdateChunk(
            int chunkIndex, 
            byte* sourcePtr, 
            int sizeBytes, 
            ulong newVersion);
    }
}
```

**Design Notes:**
- Unsafe pointers for performance
- `ChunkVersions` array tracks what's been copied
- `IDisposable` releases pinned memory

---

#### `FdpSnapshot` (Tier 1)
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Concrete snapshot implementation for Tier 1 (unmanaged) data.
    /// Reads from shadow buffer.
    /// </summary>
    internal class Tier1Snapshot : ISimWorldSnapshot
    {
        private readonly ShadowBuffer _shadow;
        private readonly EntityRepository _repository;
        private readonly ulong _frameNumber;
        
        public Tier1Snapshot(
            ShadowBuffer shadow, 
            EntityRepository repository, 
            ulong frameNumber);
        
        public T GetStruct<T>(Entity entity) where T : unmanaged
        {
            var offset = CalculateOffset<T>(entity);
            unsafe
            {
                return Unsafe.Read<T>(_shadow.Data + offset);
            }
        }
        
        // ... other ISimWorldSnapshot members
    }
}
```

---

#### `Tier2Snapshot` (Managed)
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Snapshot implementation for Tier 2 (managed) data.
    /// Holds shallow-copied reference arrays (ArrayPool).
    /// </summary>
    internal class Tier2Snapshot : IDisposable
    {
        // Rented arrays from ArrayPool
        private readonly Dictionary<Type, object[]> _referenceArrays;
        
        public Tier2Snapshot(EntityRepository repository);
        
        public T GetRecord<T>(Entity entity) where T : class
        {
            if (!_referenceArrays.TryGetValue(typeof(T), out var array))
                return null;
            
            return (T)array[entity.Index];
        }
        
        public void Dispose()
        {
            // Return arrays to pool
            foreach (var array in _referenceArrays.Values)
            {
                ArrayPool<object>.Shared.Return((object[])array);
            }
        }
    }
}
```

---

#### `HybridSnapshot` (Combines Both)
```csharp
namespace ModuleHost.Core.Snapshots
{
    /// <summary>
    /// Composite snapshot supporting both Tier 1 and Tier 2 access.
    /// This is what modules actually receive.
    /// </summary>
    internal class HybridSnapshot : ISimWorldSnapshot
    {
        private readonly Tier1Snapshot _tier1;
        private readonly Tier2Snapshot _tier2;
        
        public HybridSnapshot(Tier1Snapshot tier1, Tier2Snapshot tier2);
        
        public T GetStruct<T>(Entity entity) where T : unmanaged
            => _tier1.GetStruct<T>(entity);
        
        public T GetRecord<T>(Entity entity) where T : class
            => _tier2.GetRecord<T>(entity);
        
        public void Dispose()
        {
            // Tier1 shadow buffer is persistent (not disposed here)
            _tier2?.Dispose();  // Returns arrays to pool
        }
    }
}
```

---

## Layer 2: Module Framework

**Purpose:** Plugin system for loading and managing modules.

### 2.1 Public Interfaces

#### `IModule`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Core module contract.
    /// All simulation logic implements this.
    /// </summary>
    public interface IModule
    {
        // Identity & Configuration
        ModuleDefinition GetDefinition();
        
        // Snapshot requirements
        ComponentMask GetSnapshotRequirements();
        EventTypeMask GetEventRequirements();
        
        // Lifecycle
        void Initialize(IModuleContext context);
        void Start();
        void Stop();
        
        // System registration (synchronous modules)
        void RegisterSystems(ISystemRegistry registry);
        
        // Execution (background modules with async logic)
        JobHandle Tick(
            FrameTime time, 
            ISimWorldSnapshot snapshot, 
            ICommandBuffer commands);
        
        // Diagnostics
        void DrawDiagnostics();
    }
}
```

**Design Notes:**
- `GetSnapshotRequirements()` declares which components module needs
- `GetEventRequirements()` declares which events module needs
- `RegisterSystems` = synchronous execution path
- `Tick` = asynchronous execution path (can return immediately if no async work)
- Module can do BOTH (hybrid pattern)


---

#### `IModuleContext`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Dependency injection context for modules.
    /// </summary>
    public interface IModuleContext
    {
        // Core services
        EntityRepository Repository { get; }
        ILogger Logger { get; }
        
        // Service locator
        T GetService<T>() where T : class;
        bool TryGetService<T>(out T service) where T : class;
        
        // Module discovery
        IEnumerable<IModule> GetLoadedModules();
        IModule GetModule(string moduleId);
        
        // Configuration
        IConfiguration GetConfiguration(string section);
    }
}
```

---

#### `ISystemRegistry`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Registry for ComponentSystems.
    /// Modules register their systems here during Initialize().
    /// </summary>
    public interface ISystemRegistry
    {
        // Primary registration
        void RegisterSystem(
            ComponentSystem system, 
            Phase phase, 
            int order = 0);
        
        // Lightweight delegate registration (for simple logic)
        void RegisterUpdate(
            string name,
            Action<EntityRepository> update, 
            Phase phase, 
            int order = 0);
        
        // Query what's registered
        IReadOnlyList<ComponentSystem> GetSystemsForPhase(Phase phase);
    }
}
```

---

### 2.2 Data Structures

#### `ModuleDefinition`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Module metadata and scheduling configuration.
    /// </summary>
    public record ModuleDefinition
    {
        public required string Id { get; init; }
        public required string Version { get; init; }
        
        // Dependencies (for topological sort)
        public string[] Dependencies { get; init; } = Array.Empty<string>();
        
        // Execution configuration
        public bool IsSynchronous { get; init; } = true;  // Runs on main thread
        public Phase Phase { get; init; }                  // NetworkIngest/Input/Sim/PostSim/Export
        public int UpdateOrder { get; init; }              // Sort within phase
        
        // Background scheduling
        public int TargetFrequencyHz { get; init; } = 0;          // 0 = sync only
        public int MaxExpectedRuntimeMs { get; init; } = 1000;   // Expected completion time
        public int MaxEventHistoryFrames { get; init; } = 180;   // 3s @ 60Hz
        
        // Event-driven scheduling (NEW)
        public Type[] WatchComponents { get; init; } = Array.Empty<Type>();  // Wake on component change
        public Type[] WatchEvents { get; init; } = Array.Empty<Type>();      // Wake on event (interrupts timer!)
        
        // Resilience
        public int CircuitBreakerThreshold { get; init; } = 3;
        public bool EnableCircuitBreaker { get; init; } = true;
    }
}
```

---

#### `EntityInterestDefinition`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Declares which entity types a module handles.
    /// Used by ELM for routing.
    /// </summary>
    public record EntityInterestDefinition
    {
        public required string ModuleId { get; init; }
        
        // DIS entity type filter
        public DisTypePattern Pattern { get; init; }
        
        // Criticality (does entity need this module to exist?)
        public bool IsCritical { get; init; }
        
        // Component mask (what components does module need?)
        public ComponentMask RequiredComponents { get; init; }
    }
}
```

---

### 2.3 Core Classes

#### `ModuleLoader`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Loads modules from DLLs and resolves dependencies.
    /// </summary>
    public class ModuleLoader
    {
        public ModuleLoader(ILogger logger, string modulesDirectory);
        
        // Discovery
        public IEnumerable<ModuleInfo> DiscoverModules();
        
        // Loading
        public IModule LoadModule(string assemblyPath);
        public IEnumerable<IModule> LoadAllModules();
        
        // Dependency resolution (topological sort)
        public IEnumerable<IModule> SortByDependencies(
            IEnumerable<IModule> modules);
    }
}
```

---

#### `SystemRegistry`
```csharp
namespace ModuleHost.Framework
{
    /// <summary>
    /// Concrete implementation of ISystemRegistry.
    /// </summary>
    public class SystemRegistry : ISystemRegistry
    {
        // Organized by phase
        private readonly Dictionary<Phase, SortedList<int, ComponentSystem>> _systems;
        
        public void RegisterSystem(ComponentSystem system, Phase phase, int order)
        {
            if (!_systems.TryGetValue(phase, out var list))
            {
                list = new SortedList<int, ComponentSystem>();
                _systems[phase] = list;
            }
            
            list.Add(order, system);
        }
        
        public IReadOnlyList<ComponentSystem> GetSystemsForPhase(Phase phase)
        {
            return _systems.TryGetValue(phase, out var list) 
                ? list.Values 
                : Array.Empty<ComponentSystem>();
        }
    }
}
```

---

## Layer 3: Host Kernel

**Purpose:** Main orchestration loop and module lifecycle.

### 3.1 Core Class

#### `ModuleHostKernel`
```csharp
namespace ModuleHost.Core
{
    /// <summary>
    /// Main orchestrator.
    /// Owns EntityRepository, runs frame loop, manages modules.
    /// </summary>
    public class ModuleHostKernel
    {
        // Core components
        private readonly EntityRepository _repository;
        private readonly SnapshotManager _snapshotManager;
        private readonly SystemRegistry _systemRegistry;
        private readonly ModuleLoader _moduleLoader;
        
        // Loaded modules
        private readonly List<IModule> _modules;
        private readonly BackgroundScheduler _backgroundScheduler;
        
        // Resilience
        private readonly SnapshotLeaseManager _leaseManager;
        private readonly Dictionary<string, CircuitBreaker> _circuitBreakers;
        
        // Lifecycle
        public ModuleHostKernel(HostConfiguration config);
        public void Initialize();
        public void Start();
        public void Stop();
        
        // Main loop
        public void RunFrame();
        
        // Internal phases
        private void ExecutePhase(Phase phase);
        private void ExecuteDyncPoint();
        private void ProcessCommandBuffers();
    }
}
```

**Key Method: RunFrame()**
```csharp
public void RunFrame()
{
    _frameNumber++;
    _repository.Tick();
    
    // 1. Critical tier (synchronous)
    ExecutePhase(Phase.NetworkIngest);
    ExecutePhase(Phase.Input);
    ExecutePhase(Phase.Simulation);
    ExecutePhase(Phase.PostSimulation);
    
    // 2. SYNC POINT (create snapshots)
    ExecuteSyncPoint();
    
    // 3. Command buffer playback
    ProcessCommandBuffers();
    
    // 4. Export
    ExecutePhase(Phase.Export);
}
```

---

#### `PhaseExecutor`
```csharp
namespace ModuleHost.Core
{
    /// <summary>
    /// Executes systems registered for a specific phase.
    /// </summary>
    public class PhaseExecutor
    {
        private readonly EntityRepository _repository;
        
        public void ExecutePhase(
            Phase phase, 
            IEnumerable<ComponentSystem> systems)
        {
            _repository.SetPhase(phase);
            
            foreach (var system in systems)
            {
                try
                {
                    system.OnUpdate(_repository);
                }
                catch (Exception ex)
                {
                    _logger.Error($"System {system.GetType().Name} failed", ex);
                    // Circuit breaker logic here
                }
            }
        }
    }
}
```

---

#### `BackgroundScheduler`
```csharp
namespace ModuleHost.Core
{
    /// <summary>
    /// Manages background module execution (async modules).
    /// Determines when modules need to run based on their target frequency.
    /// </summary>
    public class BackgroundScheduler
    {
        private struct ScheduledModule
        {
            public IModule Module;
            public int TargetHz;
            public ulong LastRunFrame;
            public Task CurrentTask;
        }
        
        private readonly List<ScheduledModule> _scheduled;
        
        public void RegisterBackgroundModule(
            IModule module, 
            int targetFrequencyHz);
        
        public IEnumerable<IModule> GetReadyModules(ulong currentFrame);
        
        public void UpdateTaskStatus();  // Called each frame
    }
}
```

---

## Layer 4: Command Buffer System

**Purpose:** Thread-safe deferred structural changes for async modules.

### 4.1 Public Interface

#### `ICommandBuffer`
```csharp
namespace ModuleHost.Core.Commands
{
    /// <summary>
    /// Thread-safe queue for deferred entity operations.
    /// Background modules use this to create/modify/destroy entities.
    /// </summary>
    public interface ICommandBuffer
    {
        // Structural operations (return temp IDs)
        Entity CreateEntity(string debugName = "");
        void DestroyEntity(Entity entity);
        
        // Component operations (generic JIT-optimized)
        void SetComponent<T>(Entity entity, T value);
        void RemoveComponent<T>(Entity entity);
        
        // Playback (called by host on main thread)
        void Playback(EntityRepository repository);
    }
}
```

---

### 4.2 Core Classes

#### `EntityCommandBuffer`
```csharp
namespace ModuleHost.Core.Commands
{
    /// <summary>
    /// Concrete command buffer with temp ID allocation.
    /// </summary>
    public class EntityCommandBuffer : ICommandBuffer
    {
        private readonly ConcurrentQueue<ICommand> _commands = new();
        private long _nextTempId = -1;
        
        public Entity CreateEntity(string debugName = "")
        {
            var tempId = Interlocked.Decrement(ref _nextTempId);
            var tempEntity = new Entity(tempId);
            
            _commands.Enqueue(new CreateEntityCommand
            {
                TempEntity = tempEntity,
                DebugName = debugName
            });
            
            return tempEntity;
        }
        
        public void Playback(EntityRepository repository)
        {
            var idMap = new Dictionary<long, long>();
            
            while (_commands.TryDequeue(out var cmd))
            {
                cmd.Execute(repository, idMap);
            }
        }
    }
}
```

---

#### Command Implementations

```csharp
namespace ModuleHost.Core.Commands
{
    internal interface ICommand
    {
        void Execute(EntityRepository repo, Dictionary<long, long> idMap);
    }
    
    internal class CreateEntityCommand : ICommand
    {
        public Entity TempEntity;
        public string DebugName;
        
        public void Execute(EntityRepository repo, Dictionary<long, long> idMap)
        {
            var realEntity = repo.CreateEntity();
            idMap[TempEntity.Id] = realEntity.Id;
        }
    }
    
    internal class SetComponentCommand<T> : ICommand where T : unmanaged
    {
        public Entity Entity;
        public T Value;
        
        public void Execute(EntityRepository repo, Dictionary<long, long> idMap)
        {
            var resolvedId = idMap.GetValueOrDefault(Entity.Id, Entity.Id);
            var resolvedEntity = new Entity(resolvedId);
            repo.SetComponent(resolvedEntity, Value);
        }
    }
    
    // Similar for DestroyEntityCommand, RemoveComponentCommand, etc.
}
```

---

## Layer 5: Coordinate Services

**Purpose:** Geographic coordinate transformation (Cartesian ↔ Geodetic).

### 5.1 Public Interface

#### `IGeographicTransform`
```csharp
namespace ModuleHost.Services.Geographic
{
    /// <summary>
    /// Coordinate transformation service.
    /// Handles Cartesian (physics) ↔ Geodetic (network) conversion.
    /// </summary>
    public interface IGeographicTransform
    {
        // Tangent plane configuration
        void SetOrigin(double latitudeDeg, double longitudeDeg, double altitudeM);
        (double lat, double lon, double alt) GetOrigin();
        
        // Primary conversions
        PositionGeodetic ToGeodetic(in PositionCartesian local);
        PositionCartesian ToCartesian(in PositionGeodetic geo);
        
        // Utilities
        Vector3 ToECEF(double lat, double lon, double alt);
        (double lat, double lon, double alt) FromECEF(Vector3 ecef);
        
        // Batch conversions (for performance)
        void ToGeodeticBatch(
            ReadOnlySpan<PositionCartesian> source,
            Span<PositionGeodetic> dest);
    }
}
```

---

### 5.2 Implementation

#### `GeographicTransform`
```csharp
namespace ModuleHost.Services.Geographic
{
    /// <summary>
    /// WGS84-based coordinate transformation.
    /// Uses local tangent plane for Cartesian reference frame.
    /// </summary>
    public class GeographicTransform : IGeographicTransform
    {
        // WGS84 constants
        private const double WGS84_A = 6378137.0;        // Semi-major axis
        private const double WGS84_F = 1.0 / 298.257223563;  // Flattening
        
        // Tangent plane origin
        private Vector3 _originECEF;
        private Matrix4x4 _localToECEF;
        private Matrix4x4 _ecefToLocal;
        
        public void SetOrigin(double lat, double lon, double alt)
        {
            _originECEF = ToECEF(lat, lon, alt);
            ComputeTransformMatrices(lat, lon);
        }
        
        public PositionGeodetic ToGeodetic(in PositionCartesian local)
        {
            // Local → ECEF → Geodetic
            var ecef = Vector3.Transform(local.LocalPosition, _localToECEF);
            var (lat, lon, alt) = FromECEF(ecef);
            
            return new PositionGeodetic
            {
                Latitude = lat,
                Longitude = lon,
                Altitude = alt
            };
        }
        
        // Implementation of ECEF conversions...
    }
}
```

---

### 5.3 System

#### `CoordinateTransformSystem`
```csharp
namespace ModuleHost.Systems
{
    /// <summary>
    /// Synchronizes Tier 1 physics (Cartesian) → Tier 2 state (Geodetic).
    /// Runs in PostSimulation phase BEFORE network export.
    /// </summary>
    public class CoordinateTransformSystem : ComponentSystem
    {
        private readonly IGeographicTransform _transform;
        
        public CoordinateTransformSystem(IGeographicTransform transform)
        {
            _transform = transform;
        }
        
        public override void OnUpdate(EntityRepository repo)
        {
            var query = repo.Query()
                .WithComponent<PositionCartesian>()
                .WithComponent<PositionGeodetic>();
            
            foreach (var entity in query)
            {
                // Authority check
                if (!repo.HasAuthority(entity, ComponentType<PositionCartesian>.Id))
                    continue;
                
                // Transform
                ref readonly var cartesian = ref repo.GetComponentRO<PositionCartesian>(entity);
                var geodetic = _transform.ToGeodetic(cartesian);
                
                repo.SetComponent(entity, geodetic);
                repo.MarkDirty(entity, ComponentType<PositionGeodetic>.Id);
            }
        }
    }
}
```

---

## Layer 6: DDS Gateway

**Purpose:** Network synchronization (publish/subscribe SST descriptors).

### 6.1 Systems

#### `NetworkSyncSystem` (Egress)
```csharp
namespace ModuleHost.Systems.Network
{
    /// <summary>
    /// Scans dirty entities and publishes to DDS.
    /// Runs in Export phase (after snapshot,after command buffer playback).
    /// </summary>
    public class NetworkSyncSystem : ComponentSystem
    {
        private readonly IDDSPublisher _publisher;
        
        public override void OnUpdate(EntityRepository repo)
        {
            repo.SetPhase(Phase.Export);
            
            // Query entities with public descriptors
            var query = repo.Query()
                .WithComponent<PositionGeodetic>();
            
            foreach (var entity in query)
            {
                // Authority check
                if (!repo.HasAuthority(entity, ComponentType<PositionGeodetic>.Id))
                    continue;
                
                // Dirty check
                if (!repo.IsDirty(entity, ComponentType<PositionGeodetic>.Id))
                    continue;
                
                // Publish
                var pos = repo.GetComponent<PositionGeodetic>(entity);
                _publisher.WritePosition(pos);
            }
        }
    }
}
```

---

#### `NetworkIngestSystem` (Ingress)
```csharp
namespace ModuleHost.Systems.Network
{
    /// <summary>
    /// Reads DDS samples and updates FDP for non-owned entities.
    /// Runs in NetworkIngest phase (before simulation).
    /// </summary>
    public class NetworkIngestSystem : ComponentSystem
    {
        private readonly IDDSSubscriber _subscriber;
        
        public override void OnUpdate(EntityRepository repo)
        {
            repo.SetPhase(Phase.NetworkIngest);
            
            // Take all available samples
            var samples = _subscriber.TakePosition();
            
            foreach (var sample in samples)
            {
                var entity = new Entity(sample.EntityId);
                
                // Authority check (only accept if we DON'T own it)
                if (repo.HasAuthority(entity, ComponentType<PositionGeodetic>.Id))
                    continue;  // Loopback or conflict
                
                // Create entity if doesn't exist
                if (!repo.IsAlive(entity))
                {
                    repo.CreateEntity(entity);
                }
                
                // Update component
                repo.SetComponent(entity, sample);
            }
        }
    }
}
```

---

### 6.2 DDS Abstraction

#### `IDDSPublisher` / `IDDSSubscriber`
```csharp
namespace ModuleHost.Services.DDS
{
    public interface IDDSPublisher
    {
        void WritePosition(PositionGeodetic pos);
        void WriteIdentity(IdentityDescriptor identity);
        void WriteStatus(StatusDescriptor status);
        // ... other descriptor types
    }
    
    public interface IDDSSubscriber
    {
        ReadOnlySpan<PositionGeodetic> TakePosition();
        ReadOnlySpan<IdentityDescriptor> TakeIdentity();
        ReadOnlySpan<StatusDescriptor> TakeStatus();
        // ... other descriptor types
    }
}
```

---

## Layer 7: Entity Lifecycle (ELM)

**Purpose:** Distributed entity creation with dark construction protocol.

### 7.1 Core Class

#### `EntityLifecycleModule`
```csharp
namespace ModuleHost.Modules.ELM
{
    /// <summary>
    /// Runs in Drill Orchestrator backend.
    /// Coordinates distributed entity creation.
    /// </summary>
    public class EntityLifecycleModule : IModule
    {
        private readonly ModuleInterestRegistry _interestRegistry;
        private readonly PendingTransactionTracker _pending;
        
        // Public API
        public void SpawnEntity(
            DisEntityType type, 
            Vector3 position, 
            object initData);
        
        // Internal
        private void OnConstructionResponse(ConstructionAck ack);
        private void OnTimeout(Guid transactionId);
    }
}
```

---

#### `ModuleInterestRegistry`
```csharp
namespace ModuleHost.Modules.ELM
{
    /// <summary>
    /// Tracks which modules handle which entity types.
    /// Builds routing table for dark construction.
    /// </summary>
    public class ModuleInterestRegistry
    {
        private readonly List<EntityInterestDefinition> _interests = new();
        
        public void RegisterInterest(EntityInterestDefinition interest);
        
        public IEnumerable<string> GetRequiredModules(DisEntityType type);
        
        public ulong BuildModuleMask(DisEntityType type);
    }
}
```

---

## Layer 8: Resilience & Safety

**Purpose:** Watchdogs, circuit breakers, snapshot lease management.

### 8.1 Snapshot Leases

#### `SnapshotLeaseManager`
```csharp
namespace ModuleHost.Core.Resilience
{
    /// <summary>
    /// Enforces snapshot age limits.
    /// Force-expires snapshots held too long.
    /// </summary>
    public class SnapshotLeaseManager
    {
        private readonly Dictionary<Guid, SnapshotLease> _leases = new();
        private const int HARD_EXPIRY_MS = 2000;
        
        public ISimWorldSnapshot CreateLease(
            Guid moduleId,
            ISimWorldSnapshot snapshot,
            int requestedMaxAge);
        
        public void EnforceLeases();  // Called every frame
        
        private void ForceExpire(SnapshotLease lease);
    }
    
    internal class SnapshotLease
    {
        public ISimWorldSnapshot Snapshot { get; }
        public DateTime CreatedAt { get; }
        public int MaxAgeMs { get; }
        public volatile bool IsInvalidated;
        
        public void Invalidate();
    }
}
```

---

### 8.2 Circuit Breakers

#### `ModuleCircuitBreaker`
```csharp
namespace ModuleHost.Core.Resilience
{
    /// <summary>
    /// Prevents repeatedly invoking failing modules.
    /// </summary>
    public class ModuleCircuitBreaker
    {
        public enum State { Closed, Open, HalfOpen }
        
        private int _failureCount;
        private DateTime _lastFailure;
        private State _currentState = State.Closed;
        
        public bool ShouldExecute();
        public void RecordSuccess();
        public void RecordFailure(Exception ex);
    }
}
```

---

### 8.3 Watchdogs

#### `FrameWatchdog`
```csharp
namespace ModuleHost.Core.Resilience
{
    /// <summary>
    /// Detects hung frames (critical path timeout).
    /// </summary>
    public class FrameWatchdog : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _watchdogTask;
        
        public FrameWatchdog(int timeoutMs);
        
        public void Dispose()  // Frame completed successfully
        {
            _cts.Cancel();
        }
    }
}
```

---

## Dependency Graph

```
Layer 8 (Resilience)
  └─> Layer 3 (Host Kernel)

Layer 7 (ELM)
  └─> Layer 6 (DDS Gateway)
  └─> Layer 2 (Module Framework)

Layer 6 (DDS Gateway)
  └─> Layer 5 (Coordinate Services)
  └─> Layer 1 (Snapshot Core)

Layer 5 (Coordinate Services)
  └─> Layer 0 (FDP)

Layer 4 (Command Buffer)
  └─> Layer 0 (FDP)

Layer 3 (Host Kernel)
  └─> Layer 2 (Module Framework)
  └─> Layer 1 (Snapshot Core)
  └─> Layer 4 (Command Buffer)

Layer 2 (Module Framework)
  └─> Layer 0 (FDP)

Layer 1 (Snapshot Core)
  └─> Layer 0 (FDP)

Layer 0 (FDP EntityRepository)
  [Existing - no dependencies on new code]
```

---

## Implementation Order

### Week 1-2: Foundation
1. **Layer 1:** Snapshot Core
   - `ISimWorldSnapshot` interface
   - `ShadowBuffer` class
   - `SnapshotManager` class
   - `Tier1Snapshot`, `Tier2Snapshot`, `HybridSnapshot`

2. **Layer 4:** Command Buffer
   - `ICommandBuffer` interface
   - `EntityCommandBuffer` class
   - Command implementations

### Week 3: Framework
3. **Layer 2:** Module Framework
   - `IModule`, `IModuleContext`, `ISystemRegistry` interfaces
   - `ModuleDefinition` data structure
   - `ModuleLoader` class
   - `SystemRegistry` class

### Week 4: Host
4. **Layer 3:** Host Kernel
   - `ModuleHostKernel` class
   - `PhaseExecutor` class
   - `BackgroundScheduler` class
   - Main loop implementation

### Week 5: Services
5. **Layer 5:** Coordinate Services
   - `IGeographicTransform` interface
   - `GeographicTransform` class
   - `CoordinateTransformSystem` class

6. **Layer 6:** DDS Gateway
   - DDS abstractions (`IDDSPublisher`, `IDDSSubscriber`)
   - `NetworkSyncSystem` class
   - `NetworkIngestSystem` class

### Week 6: Advanced
7. **Layer 7:** ELM
   - `EntityLifecycleModule` class
   - `ModuleInterestRegistry` class
   - DDS protocol implementation

8. **Layer 8:** Resilience
   - `SnapshotLeaseManager` class
   - `ModuleCircuitBreaker` class
   - `FrameWatchdog` class

---

## Summary Statistics

### New Interfaces: 9
1. `ISimWorldSnapshot`
2. `ISnapshotManager`
3. `IModule`
4. `IModuleContext`
5. `ISystemRegistry`
6. `ICommandBuffer`
7. `IGeographicTransform`
8. `IDDSPublisher`
9. `IDDSSubscriber`

### New Classes: ~25
**Layer 1 (Snapshot):** 5 classes
**Layer 2 (Framework):** 3 classes
**Layer 3 (Host):** 3 classes
**Layer 4 (Commands):** 4 classes
**Layer 5 (Geographic):** 2 classes
**Layer 6 (DDS):** 3 classes
**Layer 7 (ELM):** 2 classes
**Layer 8 (Resilience):** 3 classes

### New Data Structures: 3
1. `ModuleDefinition`
2. `EntityInterestDefinition`
3. `SnapshotLease`

---

## Next Steps

1. **Review this design overview**
2. **For each layer, create detailed class design documents**
3. **Define all public APIs with XML docs**
4. **Write interface contracts and unit test stubs**
5. **Begin implementation in dependency order**

**Ready to proceed with Layer 1 (Snapshot Core) detailed design?**
