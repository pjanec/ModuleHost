# ModuleHost Integration Guide

## Overview

This guide explains how to integrate ModuleHostKernel with your FDP simulation loop.

## Execution Phases

Each simulation frame consists of three phases:

### Phase 1: Simulation (Main Thread)
- Run gameplay logic, physics, etc.
- Modify live world (EntityRepository)
- Generate events (via FdpEventBus)

### Phase 2: ModuleHost Update (Main Thread + Background Threads)
Main thread:
- Captures event history (EventAccumulator)
- Syncs providers (SyncFrom for replicas/snapshots)

Background threads:
- Modules execute with ISimulationView
- Read-only access to simulation state
- Generate commands (not applied yet)

### Phase 3: Command Processing (Main Thread)
- Collect commands from modules
- Apply to live world
- (Not yet implemented - BATCH-05)

## Performance Considerations

**Main Thread Budget:**
- Provider.Update(): <2ms for GDB, <100μs for SoD
- Event capture: <100μs
- Module dispatch: <1ms overhead

**Module Execution:**
- Runs async, does not block main thread
- Use Task.WaitAll or separate phase for sync point

**Optimization Tips:**
- Fast modules: Use GDB (zero-copy, persistent replica)
- Slow modules: Use SoD with component mask filtering
- Convoy pattern: Group slow modules sharing same data

## Thread Safety

**Safe:**
- Reading from ISimulationView (read-only)
- Multiple modules running concurrently
- Provider.AcquireView() from module threads

**Unsafe:**
- Modifying live world from module threads
- Calling Provider.Update() from module threads

**Solution:**
- Use command buffer pattern (modules queue commands)
- Commands applied on main thread in Phase 3

## Example Code

See `FdpIntegrationExample.cs` for complete working example.
