# ModuleHost.Core

## Overview

`ModuleHost.Core` is the module orchestration layer for the FDP (Fast Data Platform). It provides a high-performance environment for executing modules in parallel while maintaining a consistent and safe view of the simulation state.

The system is designed to support a hybrid architecture where the main simulation loop is highly optimized (SIM-centric) while auxiliary features (AI, Analytics, Network code) are decoupled into modular components (Module-centric).

## Key Features

- **Module Orchestration**: Manages the lifecycle and execution of modules.
- **Snapshot Providers**: Provides thread-safe, consistent views of the simulation state to modules.
  - `DoubleBufferProvider` (GDB): Zero-copy, full-state snapshots for high-frequency modules.
  - `OnDemandProvider` (SoD): Memory-efficient, filtered snapshots for low-frequency modules.
  - `SharedSnapshotProvider`: Shared snapshots for groups of modules running at the same frequency.
- **Command Buffer Pattern**: Allows modules to queue mutations (create/destroy entities, add components) which are safely applied to the live world at a specific sync point.
- **Event History**: Captures and provides simulation events to modules, ensuring no events are missed even if modules run at lower frequencies.

## Architecture

### ModuleHostKernel

The `ModuleHostKernel` is the central coordinator. It:
1. Maintains the list of active modules.
2. Captures event history from the live world.
3. Updates snapshot providers.
4. Schedules module execution (typically on a thread pool).
5. Plays back command buffers after modules complete.

### IModule Interface

Modules implement the `IModule` interface:

```csharp
public interface IModule
{
    string Name { get; }
    ModuleTier Tier { get; }
    int UpdateFrequency { get; }
    void Tick(ISimulationView view, float deltaTime);
}
```

- **Tier**: Determines the priority and default provider strategy. `Fast` modules run every frame, `Slow` modules run at a fixed frequency.
- **Tick**: The main entry point. Receives a read-only `ISimulationView`.

### Snapshot Providers

Providers decouple the modules from the live simulation data:

- **DoubleBufferProvider**: Maintains a persistent replica of the entire world. Synchronization is incremental. Best for complex AI or Physics modules.
- **OnDemandProvider**: Creates temporary snapshots containing only requested components. Best for Analytics or UI modules that only need specific data.

## Usage

```csharp
// Setup
var liveWorld = new EntityRepository();
var accumulator = new EventAccumulator();
using var host = new ModuleHostKernel(liveWorld, accumulator);

// Register Modules
host.RegisterModule(new AIModule());
host.RegisterModule(new AnalyticsModule());

// Simulation Loop
while (running)
{
    // 1. Core Simulation (Physics, etc)
    RunCoreSimulation(liveWorld);
    
    // 2. Module System
    host.Update(deltaTime);
    
    // 3. Tick Live World
    liveWorld.Tick();
}
```

## Writing Modules

### Reading State
Use the `ISimulationView` passed to `Tick` to query entities and components.

```csharp
public void Tick(ISimulationView view, float deltaTime)
{
    // Query entities
    view.Query().With<Position>().Build().ForEach(e => 
    {
        ref readonly var pos = ref view.GetComponentRO<Position>(e);
        // ... logic ...
    });
}
```

### Mutating State
Modules cannot modify the view directly. Use `GetCommandBuffer()` to queue changes.

```csharp
public void Tick(ISimulationView view, float deltaTime)
{
    var cmd = view.GetCommandBuffer();
    
    // Create entity
    var e = cmd.CreateEntity();
    cmd.AddComponent(e, new Position { X = 10, Y = 10 });
    
    // Destroy entity
    cmd.DestroyEntity(someEntity);
}
```

## Performance

- **Zero-Copy Views**: GDB provider uses memory mapping to avoid copying component data where possible.
- **Parallel Execution**: Modules run in parallel tasks.
- **Thread-Safety**: All views and command buffers are thread-safe.

## Dependencies

- `Fdp.Kernel`: Core ECS framework.
- `System.Threading.Channels`: For async communication (if used).
