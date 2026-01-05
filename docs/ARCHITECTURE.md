# Architecture: Module Host & Hybrid Flow

## Overview
The **ModuleHost** system enables the FDP engine to run a "Hybrid Architecture" where:
1.  **SIM-Centric Core**: The main simulation uses highly optimized ECS data (unmanaged structs) for physics and core logic.
2.  **Module-Centric Features**: Complex features (AI, Analytics, Network) run as decoupled modules, possibly on different threads or frequencies.

## Core Concepts

### 1. Module Lifecycle
Modules define their execution frequency (`UpdateFrequency`) and importance (`Tier`).
- **Fast Tier**: Runs every frame (synchronized with simulation).
- **Slow Tier**: Runs at a lower frequency (e.g., 10Hz), skipping frames.

### 2. Snapshot Providers (Read Path)
Modules never read live data directly (to avoid lock contention and tearing). Instead, they receive a **Snapshot**:
- **DoubleBufferProvider (GDB)**:
    - Best for: Fast Tier, heavy read access (AI).
    - Mechanism: Maintains a full replica. Syncs changes incrementally.
    - Benefit: Zero-copy views after sync.
- **OnDemandProvider (SoD)**:
    - Best for: Slow Tier, sparse read access (Analytics).
    - Mechanism: Creates a temporary snapshot containing only requested components.
    - Benefit: Low memory overhead, filtering.
- **SharedSnapshotProvider**:
    - Best for: Groups of modules (Convoy) running at same frequency.
    - Mechanism: One snapshot shared across multiple modules.

### 3. Command Buffer (Write Path)
Modules cannot modify the snapshot or live world directly.
- **Pattern**: Deferred Command Buffer.
- **Process**:
    1.  Module calls `view.GetCommandBuffer()`.
    2.  Records commands (`CreateEntity`, `AddComponent`).
    3.  Commands are thread-local (lock-free recording).
    4.  **Sync Point**: `ModuleHostKernel.Update` plays back all commands onto the Live World on the main thread.

### 4. Event History
Since slow modules skip frames, they might miss one-off events.
- **EventAccumulator** buffers events for N frames.
- When a module updates, it receives all events that occurred since its last run.

## Data Flow
```mermaid
graph TD
    Live[Live World] -->|Sync| Provider[Snapshot Provider]
    Provider -->|View| Module[Module.Tick()]
    Module -->|Record| Cmd[Command Buffer]
    Cmd -->|Playback (Main Thread)| Live
    Live -->|Events| Accumulator[Event History]
    Accumulator -->|catch-up| Module
```

## Performance Considerations
- **Zero-Copy**: GDB uses memory mapping where applicable.
- **Parallelism**: Modules execute in `Task.Run()` (Thread Pool).
- **Pooling**: `OnDemandProvider` pools `EntityRepository` instances to minimize GC.
