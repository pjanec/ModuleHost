# Memory Layout Diagrams - Complete 3-World Architecture

**Purpose:** Visual reference for understanding memory structure across all three worlds in the hybrid architecture.

---

## Overview

The hybrid architecture uses **three distinct worlds**, each with different memory characteristics:

- **World A (Live):** Mutable source of truth - Main thread simulation
- **World B (Fast Replica):** Immutable GDB replica - Persistent with stable addresses
- **World C (Slow Snapshot):** Immutable SoD snapshot - Pooled with volatile addresses

**Total Diagrams:** 5 memory layout visualizations

---

## Diagram 0: World A (Live) - FDP Kernel Architecture

**File:** `live_world_memory_layout.png`

### Key Characteristics

**Live World (Mutable Source of Truth):**
- The one and only mutable EntityRepository
- Runs on main thread at 60Hz
- Source of truth for all replicas
- Full read/write access (synchronous systems + physics)
- Dirty chunk tracking enables efficient synchronization

**Memory Structure:**
```
EntityRepository @ Live World (World A)
├─ EntityIndex @ 0x1000
│  ├─ IsAlive flags (bitset)
│  ├─ Generation counters (ushort[])
│  └─ Component masks (BitMask256[])
│
├─ Component Tables (Tier 1 - Unmanaged)
│  ├─ Position @ 0x2000 [Version: 142]
│  ├─ Velocity @ 0x3000 [Version: 145]
│  └─ Health @ 0x3800 [Version: 141]
│
├─ Component Tables (Tier 2 - Managed)
│  ├─ Identity @ 0x4000 → Heap Objects (immutable records)
│  └─ Team @ 0x5000 → Heap Objects (immutable records)
│
└─ FdpEventBus
   ├─ Write Buffer (current frame)
   └─ Read Buffer (previous frame, double-buffered)
```

**Chunk Versioning:**
Each chunk has a version counter that increments on write:
```
Position Chunk 0: Version 142
  ↓ Physics update (entity moved)
Position Chunk 0: Version 143  ← Version incremented
```

**Benefits:**
- ✅ Central source of truth
- ✅ Direct mutation (no command overhead for synchronous systems)
- ✅ Dirty tracking enables efficient replication
- ✅ Version tracking prevents redundant copies to replicas

**Characteristics:**
- **Mutability:** Read/Write ✎ (Main thread only)
- **Frequency:** 60Hz continuous
- **Purpose:** Simulation execution
- **Access:** Synchronous systems (Physics, Input, PostSim)

---

## Diagram 1: Complete 3-World Comparison

**File:** `three_world_complete_comparison.png`

### All Three Worlds Side-by-Side

This diagram shows how the three worlds differ in structure and purpose:

**World A (Live)** - Red
- EntityRepository (Mutable)
- All components @ original addresses
- FdpEventBus (write → read swap)
- **Purpose:** Execute simulation
- **Mutability:** R/W ✎
- **Consumers:** Physics, Input, Synchronous Systems

**World B (Fast Replica)** - Green  
- EntityRepository (Immutable)
- All components @ **same addresses as World A** (stable!)
- FdpEventBus (accumulated history)
- **Purpose:** Record, Network
- **Mutability:** RO 👁
- **Consumers:** Recorder (60Hz), Network (60Hz)

**World C (Slow Snapshot)** - Purple
- SimSnapshot (Immutable)
- **Some** components @ **different addresses** (volatile!)
- FdpEventBus (accumulated history, filtered)
- **Purpose:** AI, Analytics
- **Mutability:** RO 👁
- **Consumers:** AI (10Hz), Analytics (5Hz)

### Data Flow:
```
World A (Live)
  ├─→ World B (SyncFrom • Every Frame • 100%)
  └─→ World C (SyncFrom • On Demand • Filtered)

World B/C → World A (Command Buffers • Async)
```

### Key Observation: Address Stability

**World A → World B:**
```
Position @ 0x2000 (Live)
    ↓ SyncFrom()
Position @ 0x2000 (Fast Replica) ← SAME ADDRESS ✓
```

**World A → World C:**
```
Position @ 0x2000 (Live)
    ↓ SyncFrom(filtered)
Position @ 0x6000 (Slow Snapshot) ← DIFFERENT ADDRESS ⚠
```

---

## Diagram 2: GDB Memory Layout

**File:** `gdb_memory_layout.png`

### Key Characteristics

**Persistent Replica (World B):**
- Memory allocated once at initialization
- Pointers remain stable across all frames
- Full topology (all component tables present)
- Updates via **overwrite strategy** (memcpy to same address)
- Zero allocations per frame

**Memory Addresses:**
```
EntityIndex:    0x1000 (stable)
Position[0]:    0x2000 (stable)
Position[1]:    0x2040 (stable)
Health[0]:      0x3000 (stable)
Identity[0]:    0x4000 (stable, points to heap objects)
```

**Frame-to-Frame Behavior:**
```
Frame 1:   Position[0] @ 0x2000  ← Initial
Frame 6:   Position[0] @ 0x2000  ← Same address (memcpy overwrites)
Frame 100: Position[0] @ 0x2000  ← Still same address
```

**Benefits:**
- ✅ Excellent CPU cache locality
- ✅ Predictable memory layout
- ✅ Zero allocation overhead
- ✅ Simple to debug (addresses don't move)

**Trade-offs:**
- ⚠️ High memory usage (full replica = 2x RAM)
- ⚠️ Copies 100% of data (even if module needs only 50%)

---

## Diagram 3: SoD Memory Layout

**File:** `sod_memory_layout.png`

### Key Characteristics

**Pooled Snapshot (World C):**
- Memory pooled and reused across frames
- Pointers change with each snapshot
- Sparse topology (only requested component tables present)
- Updates via **swap strategy** (new buffers each time)
- Minimal allocations (from pools)

**Memory Addresses:**
```
Frame 1:
  EntityIndex:    0x5000 (transient)
  Position[0]:    0x6000 (from pool)
  Health[0]:      null   (not requested - filtered out)
  Team[0]:        0x7000 (from pool)

Frame 6:
  EntityIndex:    0x5100 (different snapshot)
  Position[0]:    0x9000 (different buffer from pool)
  Health[0]:      null   (still not requested)
  Team[0]:        0xA000 (different buffer from pool)
```

**Frame-to-Frame Behavior:**
```
Frame 1:   Position[0] @ 0x6000  ← Buffer A from pool
           (snapshot released back to pool)
Frame 6:   Position[0] @ 0x9000  ← Buffer B from pool (different address!)
           (snapshot released back to pool)
Frame 100: Position[0] @ 0xC000  ← Buffer C from pool (different again)
```

**Benefits:**
- ✅ Low memory usage (only requested components)
- ✅ Bandwidth efficient (filtered sync)
- ✅ Decoupled timing (slow modules don't affect each other)
- ✅ Flexible (different modules request different masks)

**Trade-offs:**
- ⚠️ Pointers volatile (harder to debug)
- ⚠️ Variable cache locality
- ⚠️ Pool management complexity

---

## Diagram 4: GDB vs SoD Side-by-Side Comparison

**File:** `memory_comparison_side_by_side.png`

### The Overwrite vs The Swap

**GDB (The Overwrite):**
```
0x2000: [Old Position Data]
        ↓ memcpy
0x2000: [New Position Data]  ← Same address, data replaced
```

**SoD (The Swap):**
```
Frame 1:  0x6000: [Position Data] ← Buffer A
          Release to pool
Frame 6:  0x9000: [Position Data] ← Buffer B (different address!)
          Release to pool
```

### Comparison Table

| Feature | GDB (World B) | SoD (World C) |
|---------|---------------|---------------|
| **Memory Addresses** | Stable ✓ | Volatile ⚠ |
| **Full Topology** | Yes (all tables) | Sparse (filtered tables) |
| **Bandwidth** | High (100% data) | Low (filtered data) |
| **Per-Frame Allocation** | Zero | Minimal (pool overhead) |
| **Cache Locality** | Excellent | Variable |
| **Memory Usage** | High (2x full world) | Low (~50% for filtered) |
| **Best For** | High-frequency modules | Low-frequency modules |
| **Example Modules** | Recorder, Network (60Hz) | AI, Analytics (10Hz) |

---

## Usage Guidelines

### When to Use GDB

**Indicators:**
- ✅ Module runs at high frequency (≥30Hz)
- ✅ Module needs 100% of component data (e.g., Flight Recorder)
- ✅ Module needs all events (no filtering)
- ✅ RAM is available (can afford 2x world size)
- ✅ Predictable performance is critical

**Example:**
```csharp
// Flight Recorder needs EVERYTHING
var fastProvider = new DoubleBufferProvider(liveWorld);
host.RegisterModule(new FlightRecorderModule(), fastProvider);
// World B: 100% replica, stable addresses, memcpy updates
```

---

### When to Use SoD

**Indicators:**
- ✅ Module runs at low frequency (<30Hz)
- ✅ Module needs subset of components (e.g., AI needs Position/Team only)
- ✅ Module filtered events (e.g., only Explosion events)
- ✅ RAM is constrained
- ✅ Decoupled timing is important

**Example:**
```csharp
// AI needs only logic data (50% of components)
var aiMask = new BitMask256();
aiMask.Set(typeof(Position));
aiMask.Set(typeof(Team));
aiMask.Set(typeof(Health));

var slowProvider = new OnDemandProvider(liveWorld);
host.RegisterModule(new AiModule(), slowProvider);
// World C: 50% sparse snapshot, volatile addresses, filtered sync
```

---

## Memory Lifecycle

### GDB Lifecycle

```
Initialization:
  ┌─────────────────────────────────────┐
  │ Allocate World B (EntityRepository) │
  │ • EntityIndex: VirtualAlloc         │
  │ • Tier 1 Tables: VirtualAlloc       │
  │ • Tier 2 Tables: new T[]            │
  │ • Total: ~200-500MB                 │
  └─────────────────────────────────────┘
           ↓
Every Frame (60x per second):
  ┌─────────────────────────────────────┐
  │ SyncFrom(LiveWorld)                 │
  │ • memcpy dirty chunks (Tier 1)     │
  │ • Array.Copy (Tier 2 refs)         │
  │ • Same addresses reused             │
  │ • Cost: <2ms                        │
  └─────────────────────────────────────┘
           ↓
Shutdown:
  ┌─────────────────────────────────────┐
  │ Dispose World B                     │
  │ • VirtualFree                       │
  └─────────────────────────────────────┘
```

**Total Allocations:** 1 at initialization, 0 per frame

---

### SoD Lifecycle

```
Initialization:
  ┌─────────────────────────────────────┐
  │ Create Pool (empty initially)       │
  │ • ConcurrentStack<EntityRepository> │
  └─────────────────────────────────────┘
           ↓
Frame 1 (first AI tick):
  ┌─────────────────────────────────────┐
  │ AcquireView()                       │
  │ • Pool.TryPop() → miss              │
  │ • new EntityRepository()            │
  │ • SyncFrom(live, aiMask)            │
  │ • Return snapshot @ 0x6000          │
  └─────────────────────────────────────┘
           ↓
  ┌─────────────────────────────────────┐
  │ AI Module Tick                      │
  │ • Read Position @ 0x6000            │
  │ • Read Team @ 0x7000                │
  └─────────────────────────────────────┘
           ↓
  ┌─────────────────────────────────────┐
  │ ReleaseView()                       │
  │ • SoftClear()                       │
  │ • Pool.Push(snapshot)               │
  └─────────────────────────────────────┘
           ↓
Frame 6 (second AI tick):
  ┌─────────────────────────────────────┐
  │ AcquireView()                       │
  │ • Pool.TryPop() → hit! (reuse)      │
  │ • SyncFrom(live, aiMask)            │
  │ • Return snapshot @ 0x9000          │
  │   (different buffer from pool!)     │
  └─────────────────────────────────────┘
           ↓
Shutdown:
  ┌─────────────────────────────────────┐
  │ Dispose pool                        │
  │ • Dispose all pooled snapshots      │
  └─────────────────────────────────────┘
```

**Total Allocations:** 2-3 snapshots (pool grows as needed), reused indefinitely

---

## Performance Characteristics

### GDB Performance

**Sync Cost:**
```
100% data, 30% dirty chunks:
  Position Table:   300 chunks × 64KB = 19.2 MB  → ~800μs  memcpy
  Health Table:     300 chunks × 64KB = 19.2 MB  → ~800μs  memcpy
  Velocity Table:   300 chunks × 64KB = 19.2 MB  → ~800μs  memcpy
  (Tier 2 tables...)                             → ~200μs  Array.Copy
  ─────────────────────────────────────────────────────────
  Total:                                         → ~2ms    ✓ Hits target
```

**Cache Behavior:**
- First access: Cache miss (data just memcpy'd)
- Subsequent accesses: Cache hit (stable addresses)
- Module iteration: Excellent spatial locality

---

### SoD Performance

**Sync Cost:**
```
50% data (filtered), 30% dirty chunks:
  Position Table:   300 chunks × 64KB = 19.2 MB  → ~800μs  memcpy
  Team Table:       300 chunks × sizeof = 9.6 MB → ~400μs  Array.Copy
  Health Table:     SKIPPED (not in mask)        → 0μs     ✗ Not copied
  Velocity Table:   SKIPPED (not in mask)        → 0μs     ✗ Not copied
  ─────────────────────────────────────────────────────────
  Total:                                         → ~500μs  ✓ Bandwidth savings!
```

**Cache Behavior:**
- First access: Cache miss (new buffer from pool)
- Subsequent accesses: Cache hit (same buffer during module run)
- Module iteration: Good spatial locality (within snapshot)

---

## Debug Considerations

### GDB Debugging

**Advantages:**
- ✅ Stable addresses make breakpoints reliable
- ✅ Same addresses in Visual Studio memory window
- ✅ Easy to track data changes over time

**Example:**
```
Breakpoint at 0x2000 (Position[0]):
  Frame 1:   {X: 10, Y: 20, Z: 30}
  Frame 6:   {X: 15, Y: 22, Z: 31}  ← Same address, data updated
  Frame 100: {X: 50, Y: 60, Z: 70}  ← Still same address
```

---

### SoD Debugging

**Challenges:**
- ⚠️ Addresses change each snapshot
- ⚠️ Breakpoints by address won't work reliably
- ⚠️ Memory window shows different buffer each time

**Solutions:**
- Use data breakpoints (break on value change, not address)
- Use entity ID tracking instead of pointers
- Log snapshot buffer addresses for correlation

**Example:**
```
Entity ID 42 Position:
  Frame 1:   0x6000: {X: 10, Y: 20, Z: 30}  ← Buffer A
  Frame 6:   0x9000: {X: 15, Y: 22, Z: 31}  ← Buffer B (different address!)
  Frame 100: 0xC000: {X: 50, Y: 60, Z: 70}  ← Buffer C (different again)
```

---

## Summary

### World A (Live - Source of Truth)
- **Strategy:** Direct mutation with dirty tracking
- **Memory:** Mutable, version-tracked chunks
- **Purpose:** Execute simulation
- **Access:** Synchronous systems (Main thread R/W)

### World B (GDB - Fast Replica)
- **Strategy:** The Overwrite (memcpy to same address)
- **Memory:** Stable, predictable, high usage
- **Performance:** Excellent cache locality
- **Use For:** High-frequency, dense-data modules (Recorder, Network)

### World C (SoD - Slow Snapshot)
- **Strategy:** The Swap (new buffers from pool)
- **Memory:** Volatile, efficient, low usage
- **Performance:** Bandwidth savings from filtering
- **Use For:** Low-frequency, sparse-data modules (AI, Analytics)

### Hybrid Architecture: Best of All Worlds
- **World A (Live):** Simulate at 60Hz (Main thread)
- **World B (Fast GDB):** Recorder, Network (60Hz, stable addresses)
- **World C (Slow SoD):** AI, Analytics (10Hz, filtered data)
- **Same Interface:** Modules agnostic to strategy (ISimulationView)

### All 5 Diagrams

0. **live_world_memory_layout.png** - World A structure (source of truth)
1. **three_world_complete_comparison.png** - All 3 worlds side-by-side
2. **gdb_memory_layout.png** - World B (GDB persistent replica)
3. **sod_memory_layout.png** - World C (SoD pooled snapshot)
4. **memory_comparison_side_by_side.png** - GDB vs SoD behavior over time

---

- [reference-archive/FDP-GDB-SoD-unified.md](reference-archive/FDP-GDB-SoD-unified.md) - Design evolution

---

*Last Updated: January 4, 2026*
