# Hybrid GDB+SoD Architecture - Quick Reference

**Version:** 2.0  
**Date:** January 4, 2026  
**Status:** APPROVED ARCHITECTURE

---

## 📋 Quick Summary

**Old:** Pure Snapshot-on-Demand (SoD) for all modules  
**New:** Hybrid - GDB for fast modules, SoD for slow modules  
**Why:** Simpler + More efficient for mixed-frequency workloads

---

## 🏗️ 3-World Topology

```
┌─────────────────────────────────────────────────────────┐
│  World A (Live) - Main Thread - 60Hz                    │
│  - Physics, Input, Logic                                │
│  - Full read/write access                               │
└─────────────────┬───────────────────────────────────────┘
                  │
        ┌─────────┴────────────┐
        ▼                      ▼
┌──────────────────┐   ┌──────────────────┐
│ World B (Fast)   │   │ World C (Slow)   │
│ GDB - Every Frame│   │ SoD/GDB - On Req │
├──────────────────┤   ├──────────────────┤
│ • Recorder       │   │ • AI (10Hz)      │
│ • Network (60Hz) │   │ • Analytics (5Hz)│
│                  │   │ • UI             │
│ 100% data        │   │ Filtered (~50%)  │
│ <2ms sync        │   │ <500μs sync      │
└──────────────────┘   └──────────────────┘
```

---

## 🔑 Key Interfaces

### 1. `ISimulationView` (replaces `ISimWorldSnapshot`)

**Simpler, cleaner read-only interface:**

```csharp
public interface ISimulationView
{
    uint Tick { get; }
    float Time { get; }
    
    ref readonly T GetComponentRO<T>(Entity e) where T : unmanaged;
    T GetManagedComponentRO<T>(Entity e) where T : class;
    
    bool IsAlive(Entity e);
    ReadOnlySpan<T> ConsumeEvents<T>() where T : unmanaged;
    EntityQueryBuilder Query();
}
```

**Implemented by:** `EntityRepository` (GDB) or `SimSnapshot` (SoD)

---

### 2. `ISnapshotProvider` (Strategy Pattern)

```csharp
public interface ISnapshotProvider : IDisposable
{
    ISimulationView AcquireView(BitMask256 mask, uint lastSeenTick);
    void ReleaseView(ISimulationView view);
}
```

**Implementations:**
- `DoubleBufferProvider` - GDB (persistent replica)
- `OnDemandProvider` - SoD (pooled snapshots)
- `SharedSnapshotProvider` - GDB with convoy pattern (slow shared replica)

---

## 🔧 Core FDP APIs

### 1. `EntityRepository.SyncFrom()` (NEW)

**The heart of both GDB and SoD:**

```csharp
public void SyncFrom(EntityRepository source, BitMask256? mask = null)
{
    // Sync metadata
    _entityIndex.SyncFrom(source._entityIndex);
    
    // Sync components (filtered or full)
    foreach (var typeId in _componentTables.Keys)
    {
        if (mask.HasValue && !mask.Value.IsSet(typeId)) continue;
        
        var myTable = _componentTables[typeId];
        var srcTable = source._componentTables[typeId];
        
        // Tier 1/2 sync
        myTable.SyncDirtyChunks(srcTable);
    }
    
    this._globalVersion = source._globalVersion;
}
```

**Usage:**
- GDB: `replica.SyncFrom(live)` - copies all dirty chunks
- SoD: `snapshot.SyncFrom(live, aiMask)` - copies only filtered chunks

---

### 2. `NativeChunkTable.SyncDirtyChunks()` (NEW)

```csharp
public void SyncDirtyChunks(NativeChunkTable<T> source)
{
    for (int i = 0; i < source.TotalChunks; i++)
    {
        // Optimization: version check
        if (_chunkVersions[i] == source.GetChunkVersion(i)) 
            continue;  // Skip unchanged chunks
        
        // Copy dirty chunk
        Unsafe.CopyBlock(
            this.GetChunkDataPtr(i),
            source.GetChunkDataPtr(i),
            FdpConfig.CHUNK_SIZE_BYTES
        );
        
        _chunkVersions[i] = source.GetChunkVersion(i);
    }
}
```

---

### 3. `EventAccumulator` (NEW)

**Captures events from live bus, flushes to replica buses:**

```csharp
public class EventAccumulator
{
    private Queue<FrameEventData> _history = new();
    
    public void CaptureFrame(FdpEventBus liveBus, ulong frameIndex);
    public void FlushToReplica(FdpEventBus replicaBus, uint lastSeenTick);
}
```

**Usage:**
```csharp
// Capture every frame
_accumulator.CaptureFrame(_liveWorld.Bus, frameNumber);

// Flush when replica syncs
_accumulator.FlushToReplica(_replica.Bus, replicaLastTick);
// Replica now has events from [replicaLastTick+1 ... current]
```

---

## 🎯 Module API (Minimal Change)

**Old:**
```csharp
JobHandle Tick(FrameTime time, ISimWorldSnapshot snapshot, ICommandBuffer commands);
```

**New:**
```csharp
JobHandle Tick(FrameTime time, ISimulationView view, ICommandBuffer commands);
```

**Change:** Just interface name (`ISimWorldSnapshot` → `ISimulationView`)

**Modules are agnostic to strategy** - they don't know if `view` is GDB replica or SoD snapshot!

---

## 📅 Implementation Phases (Updated)

### Week 1-2: FDP Synchronization Core
```
[⏳] EntityRepository.SyncFrom()
[⏳] NativeChunkTable.SyncDirtyChunks()
[⏳] ManagedComponentTable.SyncDirtyChunks()
[⏳] ISimulationView interface
[⏳] EntityRepository implements ISimulationView
[⏳] EventAccumulator
[⏳] Tests (20 tests)
```

### Week 3: Strategy Implementations
```
[⏳] ISnapshotProvider interface
[⏳] DoubleBufferProvider (GDB)
[⏳] OnDemandProvider (SoD)
[⏳] SharedSnapshotProvider (convoy)
[⏳] Tests (15 tests)
```

### Week 4: ModuleHost Integration
```
[⏳] ModuleHostKernel 3-world topology
[⏳] Module-to-strategy mapping configuration
[⏳] Fast lane (World B) dispatch
[⏳] Slow lane (World C) dispatch
[⏳] Integration tests
```

### Week 5-6: (Unchanged)
Services, ELM, Advanced features

---

## ✅ What Stays the Same

**No changes to these core features:**
1. ✅ Tier 2 immutability enforcement (3 layers)
2. ✅ Event-driven scheduling (HasComponentChanged, HasEvents)
3. ✅ Dynamic buffer expansion
4. ✅ Command buffer pattern
5. ✅ Event filtering per module
6. ✅ Dirty chunk tracking optimization

---

## 📊 Performance Targets

| Operation | Target | Verification |
|-----------|--------|--------------|
| GDB full sync (100% data) | <2ms | 100K entities, 30% dirty |
| SoD filtered sync (50% data) | <500μs | 100K entities, 10% filtered |
| EventAccumulator flush (6 frames) | <100μs | 1K events/frame |
| Fast lane dispatch | <16.67ms total | Recorder + Network parallel |
| Slow lane update check | <200ns | Per-module convoy check |

---

## 🔄 Migration Path

See: [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md)

**Summary:**
1. **Phase 1 (Week 1-2):** Add FDP APIs (non-breaking)
2. **Phase 2 (Week 3):** Add strategy pattern (alongside old code)
3. **Phase 3 (Week 4):** Switch ModuleHost to new architecture

**Breaking Change:** `ISimWorldSnapshot` → `ISimulationView` (interface name only)

---

## 📚 Reference Documents

**Architecture:**
- [reference-archive/FDP-GDB-SoD-unified.md](reference-archive/FDP-GDB-SoD-unified.md) - Full design evolution
- [MIGRATION-PLAN-Hybrid-Architecture.md](MIGRATION-PLAN-Hybrid-Architecture.md) - Migration guide
- [IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md) - Updated master spec

**Original (Archived):**
- [ADR-001-Snapshot-on-Demand.md](ADR-001-Snapshot-on-Demand.md) - Original SoD decision
- [B-One-FDP-Data-Lake.md](B-One-FDP-Data-Lake.md) - SoD rationale

---

## 🎯 Decision Summary

| Aspect | Decision | Rationale |
|--------|----------|-----------|
| **Fast Modules** | GDB | Recorder needs 100%, Network runs 60Hz → full GDB simpler |
| **Slow Modules** | SoD (or GDB convoy) | AI needs <50% data → filtered SoD saves bandwidth |
| **Interface** | ISimulationView | Simpler than ISimWorldSnapshot, repo implements natively |
| **Core API** | SyncFrom() | Unified API for both GDB and SoD |
| **Events** | EventAccumulator | Bridges live→replica event streams |
| **Module API** | Minimal change | Only interface name change |

---

## ✔️ Approval Checklist

Before implementation:
- [✅] Architecture reviewed and approved
- [✅] Migration plan reviewed
- [⏳] IMPLEMENTATION-SPECIFICATION.md fully updated
- [⏳] detailed-design-overview.md updated
- [⏳] Team briefed on strategy pattern

---

**Status:** ✅ ARCHITECTURE APPROVED - READY FOR SPEC UPDATE

**Next Steps:**
1. Finish updating IMPLEMENTATION-SPECIFICATION.md (in progress)
2. Update detailed-design-overview.md
3. Begin Week 1 implementation

---

*Last Updated: January 4, 2026*
