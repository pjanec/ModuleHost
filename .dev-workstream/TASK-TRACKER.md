# ModuleHost Implementation Task Tracker

**Project:** ModuleHost Advanced Features Implementation  
**Start Date:** 2026-01-07  
**Target Completion:** 2026-03-07 (8 weeks)  
**Dev Lead:** [Lead Name]

---

## 📊 Overall Progress

| Batch | Phase | Tasks | Status | Start | End | Duration |
|-------|-------|-------|--------|-------|-----|----------|
| 01 | Non-Blocking Execution | 5 | 🔵 Not Started | - | - | - |
| 02 | Reactive Scheduling | 4 | ⚪ Pending | - | - | - |
| 03 | Convoy & Pooling | 4 | ⚪ Pending | - | - | - |
| 04 | Resilience & Safety | 5 | ⚪ Pending | - | - | - |
| 05 | Execution Modes Refactor | 4 | ⚪ Pending | - | - | - |
| 06 | Entity Lifecycle Manager | 5 | ⚪ Pending | - | - | - |
| 07 | Network Gateway Core | 6 | ⚪ Pending | - | - | - |
| 08 | Geographic Transform | 4 | ⚪ Pending | - | - | - |

**Legend:**
- 🔵 Not Started
- 🟡 In Progress
- 🟢 Complete
- 🔴 Blocked
- ⚪ Pending (waiting for prerequisites)

---

## 📋 BATCH 01: Non-Blocking Execution

**Priority:** CRITICAL (P0)  
**Dependencies:** None  
**Design Reference:** [Chapter 1](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-1-non-blocking-execution-world-c)  
**Estimated Effort:** 1 week

### Tasks

#### Task 1.1: Module Entry State Tracking
**Description:** Add async execution state to `ModuleEntry` class  
**Design Ref:** Chapter 1, Section 1.2 - Data Structures  
**Acceptance Criteria:**
- `CurrentTask`, `LeasedView`, `AccumulatedDeltaTime` fields added
- `LastRunTick` field for reactive scheduling prep
- No breaking changes to existing module registration

**Tests Required:**
- Unit test: ModuleEntry field initialization
- Unit test: State transitions (Idle → Running → Completed)

**Status:** 🔵 Not Started

---

#### Task 1.2: Harvest-and-Dispatch Loop
**Description:** Implement non-blocking module execution loop in `ModuleHostKernel.Update()`  
**Design Ref:** Chapter 1, Section 1.2 - Execution Flow  
**Acceptance Criteria:**
- Harvest phase: Check completed tasks, playback commands, release views
- Dispatch phase: Start new tasks without blocking
- FrameSynced modules still wait synchronously
- Async modules skip if still running

**Tests Required:**
- Integration test: Slow module doesn't block main thread
- Integration test: Commands harvested correctly
- Integration test: Accumulated delta time calculated properly
- Performance test: Frame time stable with long-running module

**Status:** 🔵 Not Started

---

#### Task 1.3: Provider Lease/Release Logic
**Description:** Update providers to support view leasing across frames  
**Design Ref:** Chapter 1, Section 1.2 - Provider Implications  
**Acceptance Criteria:**
- `OnDemandProvider` pool size configurable for concurrency
- `SharedSnapshotProvider` ref counting works with multi-frame leases
- Views remain valid until explicitly released

**Tests Required:**
- Unit test: Pool doesn't exhaust with concurrent slow modules
- Unit test: Snapshot stays alive while any module holds it
- Unit test: View released returns to pool correctly

**Status:** 🔵 Not Started

---

#### Task 1.4: HarvestEntry Helper Method
**Description:** Extract command playback logic into reusable method  
**Design Ref:** Chapter 1, Section 1.2 - Execution Flow  
**Acceptance Criteria:**
- DRY: Harvest logic not duplicated
- Handles both async and synced modules
- Proper error handling for faulted tasks

**Tests Required:**
- Unit test: Faulted task logged and cleaned up
- Unit test: Command buffer playback order preserved

**Status:** 🔵 Not Started

---

#### Task 1.5: Integration Testing & Validation
**Description:** End-to-end testing of non-blocking execution  
**Design Ref:** Chapter 1, entire section  
**Acceptance Criteria:**
- Test case: 10Hz module taking 50ms doesn't drop 60Hz frame rate
- Test case: Multiple slow modules running concurrently
- Test case: Commands from late modules applied in correct frame
- Benchmark: Main thread frame time variance

**Tests Required:**
- Integration test: NonBlockingExecution_SlowModule_DoesntBlockMainThread
- Integration test: NonBlockingExecution_MultipleSlowModules_SharePool
- Performance test: FrameTimeVariance_WithSlowModules (target: <1ms variance)

**Status:** 🔵 Not Started

---

## 📋 BATCH 02: Reactive Scheduling

**Priority:** HIGH (P1)  
**Dependencies:** BATCH-01 (needs LastRunTick tracking)  
**Design Reference:** [Chapter 2](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-2-reactive-scheduling)  
**Estimated Effort:** 1 week

### Tasks

#### Task 2.1: Component Dirty Tracking
**Description:** Add `LastWriteTick` to FDP component tables  
**Design Ref:** Chapter 2, Section 2.2 - Component Dirty Tracking  
**Acceptance Criteria:**
- `IComponentTable` interface updated with `LastWriteTick` property
- `NativeChunkTable` and `ManagedComponentTable` update tick on write
- `EntityRepository.HasComponentChanged()` method implemented
- Atomic updates (no threading issues)

**Tests Required:**
- Unit test: LastWriteTick updates on Set()
- Unit test: LastWriteTick updates on GetRW()
- Unit test: HasComponentChanged detects table modifications
- Unit test: Thread-safe concurrent writes

**Status:** ⚪ Pending

---

#### Task 2.2: Event Bus Active Tracking
**Description:** Track active event types per frame in `FdpEventBus`  
**Design Ref:** Chapter 2, Section 2.2 - Event Tracking  
**Acceptance Criteria:**
- `HashSet<int> _activeEventIds` added to FdpEventBus
- Populated during `Publish()` and `PublishManaged()`
- Cleared during `SwapBuffers()`
- `HasEvent(Type eventType)` method implemented

**Tests Required:**
- Unit test: Event ID added on publish
- Unit test: Event IDs cleared on swap
- Unit test: HasEvent returns true for published events
- Unit test: Managed and unmanaged events both tracked

**Status:** ⚪ Pending

---

#### Task 2.3: IModule Reactive API
**Description:** Extend `IModule` interface with watch lists  
**Design Ref:** Chapter 2, Section 2.2 - API Changes  
**Acceptance Criteria:**
- `WatchComponents` property added (nullable list of Types)
- `WatchEvents` property added (nullable list of Types)
- Existing modules compatible (default null implementations)
- Type→ID cache in kernel for performance

**Tests Required:**
- Unit test: Module with null watch lists works unchanged
- Unit test: Module with watch lists registered correctly
- Unit test: Type→ID cache avoids reflection in hot path

**Status:** ⚪ Pending

---

#### Task 2.4: Trigger Logic in ShouldRunThisFrame
**Description:** Implement reactive trigger checks in scheduler  
**Design Ref:** Chapter 2, Section 2.2 - Trigger Logic  
**Acceptance Criteria:**
- Timer check remains (existing behavior)
- Event trigger check added (O(1) lookup)
- Component trigger check added (O(1) per watched type)
- Triggers override frequency timer

**Tests Required:**
- Integration test: Module wakes on watched event
- Integration test: Module wakes on component change
- Integration test: Module sleeps when no triggers despite timer
- Performance test: Trigger check overhead <0.1ms per module

**Status:** ⚪ Pending

---

## 📋 BATCH 03: Convoy & Pooling

**Priority:** MEDIUM (P2)  
**Dependencies:** None (can run parallel with BATCH-02)  
**Design Reference:** [Chapter 3](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-3-convoy--pooling-patterns)  
**Estimated Effort:** 1 week

### Tasks

#### Task 3.1: Snapshot Pool Implementation
**Description:** Create `SnapshotPool` class for repository reuse  
**Design Ref:** Chapter 3, Section 3.2 - Snapshot Pool  
**Acceptance Criteria:**
- `ConcurrentStack<EntityRepository>` backing store
- `Get()` method returns pooled or new instance
- `Return()` method calls `SoftClear()` before pooling
- Schema setup cached and applied to new instances

**Tests Required:**
- Unit test: Pool returns same instance on Get/Return
- Unit test: Pool creates new when empty
- Unit test: SoftClear called on return
- Performance test: Pool eliminates GC allocations

**Status:** ⚪ Pending

---

#### Task 3.2: SharedSnapshotProvider Enhancements
**Description:** Add reference counting and pool integration  
**Design Ref:** Chapter 3, Section 3.2 - SharedSnapshotProvider  
**Acceptance Criteria:**
- `_activeReaders` counter with lock protection
- Union mask support for multiple modules
- Lazy sync on first `AcquireView()`
- Return to pool when last reader releases

**Tests Required:**
- Unit test: Reference count increments/decrements correctly
- Unit test: Snapshot pooled when count reaches zero
- Unit test: Union mask syncs superset of requirements
- Integration test: 5 modules share single snapshot

**Status:** ⚪ Pending

---

#### Task 3.3: Auto-Grouping Logic
**Description:** Implement convoy detection in kernel initialization  
**Design Ref:** Chapter 3, Section 3.2 - Auto-Grouping  
**Acceptance Criteria:**
- Group modules by `{Mode, Strategy, Frequency}`
- Calculate union component mask per group
- Assign `SharedSnapshotProvider` to groups >1 module
- Single module gets `OnDemandProvider`

**Tests Required:**
- Unit test: Modules with same freq grouped correctly
- Unit test: Union mask combines all requirements
- Unit test: Single module doesn't use shared provider
- Integration test: Memory usage 1/N with convoy

**Status:** ⚪ Pending

---

#### Task 3.4: Integration & Performance Validation
**Description:** Validate convoy memory and sync performance  
**Design Ref:** Chapter 3, entire section  
**Acceptance Criteria:**
- Memory benchmark: 5 modules vs convoy memory usage
- Sync benchmark: 5 individual syncs vs 1 convoy sync
- Stress test: 20 modules in convoys

**Tests Required:**
- Performance test: Memory_ConvoyVsIndividual (target: <20% of individual)
- Performance test: Sync_ConvoyVsIndividual (target: <30% of individual)
- Integration test: Convoy_20Modules_StablePerformance

**Status:** ⚪ Pending

---

## 📋 BATCH 04: Resilience & Safety

**Priority:** HIGH (P1)  
**Dependencies:** BATCH-01 (needs async task execution)  
**Design Reference:** [Chapter 4](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-4-resilience--safety)  
**Estimated Effort:** 1 week

### Tasks

#### Task 4.1: Circuit Breaker Implementation
**Description:** Create `ModuleCircuitBreaker` state machine  
**Design Ref:** Chapter 4, Section 4.2 - Circuit Breaker  
**Acceptance Criteria:**
- States: Closed, Open, HalfOpen
- Configurable failure threshold
- Configurable reset timeout
- Thread-safe state transitions

**Tests Required:**
- Unit test: Circuit opens after N failures
- Unit test: Circuit transitions to HalfOpen after timeout
- Unit test: HalfOpen success closes circuit
- Unit test: HalfOpen failure reopens circuit

**Status:** ⚪ Pending

---

#### Task 4.2: Safe Execution Wrapper
**Description:** Implement `ExecuteModuleSafe` with timeout  
**Design Ref:** Chapter 4, Section 4.2 - Safe Execution  
**Acceptance Criteria:**
- Timeout via `Task.WhenAny`
- Exception catching and logging
- Circuit breaker integration
- Zombie task detection

**Tests Required:**
- Unit test: Timeout triggers circuit failure
- Unit test: Exception triggers circuit failure
- Unit test: Success records in circuit breaker
- Integration test: Hanging module doesn't freeze system

**Status:** ⚪ Pending

---

#### Task 4.3: ModuleEntry Circuit Breaker Integration
**Description:** Add circuit breaker to module registration  
**Design Ref:** Chapter 4, Section 4.2 - Configuration  
**Acceptance Criteria:**
- `CircuitBreaker` field in `ModuleEntry`
- Initialized during `RegisterModule()`
- Configuration from module policy
- Null-safe for existing modules

**Tests Required:**
- Unit test: Circuit breaker created with module config
- Unit test: Null circuit breaker doesn't break execution
- Integration test: Module disabled after failures

**Status:** ⚪ Pending

---

#### Task 4.4: ExecutionPolicy Timeout Configuration
**Description:** Add timeout and resilience config to policy  
**Design Ref:** Chapter 4, Section 4.2  
**Acceptance Criteria:**
- `MaxExpectedRuntimeMs` field
- `FailureThreshold` field
- `CircuitResetTimeoutMs` field
- Factory methods updated with sensible defaults

**Tests Required:**
- Unit test: Default policies have timeout configured
- Unit test: Custom policy respects values

**Status:** ⚪ Pending

---

#### Task 4.5: Resilience Integration Testing
**Description:** End-to-end resilience validation  
**Design Ref:** Chapter 4, entire section  
**Acceptance Criteria:**
- Test hung module (infinite loop)
- Test crashing module (exception)
- Test flaky module (intermittent failures)
- Test recovery after circuit opens

**Tests Required:**
- Integration test: HungModule_TimesOut_SystemContinues
- Integration test: CrashingModule_Isolated_OthersRun
- Integration test: FlakyModule_CircuitTrips_ThenRecovers
- Integration test: Multiple Modules Failing_System Degrades Gracefully

**Status:** ⚪ Pending

---

## 📋 BATCH 05: Execution Modes Refactor

**Priority:** MEDIUM (P2)  
**Dependencies:** BATCH-01, BATCH-02, BATCH-03, BATCH-04 (consolidation batch)  
**Design Reference:** [Chapter 5](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-5-flexible-execution-modes)  
**Estimated Effort:** 4 days

### Tasks

#### Task 5.1: ExecutionPolicy Structure
**Description:** Replace `ModuleTier` with explicit `ExecutionPolicy`  
**Design Ref:** Chapter 5, Section 5.2 - API Changes  
**Acceptance Criteria:**
- `RunMode` enum (Synchronous, FrameSynced, Asynchronous)
- `DataStrategy` enum (Direct, GDB, SoD)
- Policy struct with all config fields
- Factory methods for common profiles

**Tests Required:**
- Unit test: Default policies have valid configurations
- Unit test: Custom policy validates constraints

**Status:** ⚪ Pending

---

#### Task 5.2: IModule API Update
**Description:** Update `IModule` to use `ExecutionPolicy`  
**Design Ref:** Chapter 5, Section 5.2  
**Acceptance Criteria:**
- Remove `Tier` property
- Remove `UpdateFrequency` property
- Add `Policy` property
- Backward compatibility shim for existing modules

**Tests Required:**
- Unit test: Existing modules compile with shim
- Integration test: Legacy tier enum still works

**Status:** ⚪ Pending

---

#### Task 5.3: Provider Assignment Refactor
**Description:** Update auto-provider logic to use policy  
**Design Ref:** Chapter 5, Section 5.2 - Grouping Logic  
**Acceptance Criteria:**
- Group by `{Mode, Strategy, Frequency}`
- Handle Direct strategy (no provider)
- Handle GDB strategy (persistent replica)
- Handle SoD strategy (pooled snapshot)

**Tests Required:**
- Unit test: Synchronous+Direct gets no provider
- Unit test: FrameSynced+GDB gets DoubleBufferProvider
- Unit test: Async+SoD gets convoy or OnDemand
- Integration test: All combinations work

**Status:** ⚪ Pending

---

#### Task 5.4: Migration & Documentation
**Description:** Update examples and create migration guide  
**Design Ref:** Chapter 5, entire section  
**Acceptance Criteria:**
- Update `Fdp.Examples.CarKinem` to use new API
- Create MIGRATION.md guide
- Update README examples
- Deprecation warnings for old API

**Tests Required:**
- Integration test: CarKinem example runs with new API
- Build test: Deprecation warnings appear for old usage

**Status:** ⚪ Pending

---

## 📋 BATCH 06: Entity Lifecycle Manager

**Priority:** MEDIUM (P2)  
**Dependencies:** BATCH-05 (needs ExecutionPolicy)  
**Design Reference:** [Chapter 6](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-6-entity-lifecycle-manager-elm)  
**Estimated Effort:** 1 week

### Tasks

#### Task 6.1: Lifecycle Event Definitions
**Description:** Define ELM event protocol  
**Design Ref:** Chapter 6, Section 6.2 - Data Structures  
**Acceptance Criteria:**
- `ConstructionOrder` event struct
- `ConstructionAck` event struct
- `DestructionOrder` event struct
- `DestructionAck` event struct
- Event IDs registered in FDP

**Tests Required:**
- Unit test: Events serialize/deserialize correctly
- Unit test: Event IDs don't conflict

**Status:** ⚪ Pending

---

#### Task 6.2: EntityLifecycleModule Core
**Description:** Implement ELM coordination module  
**Design Ref:** Chapter 6, Section 6.2 - ELM Module  
**Acceptance Criteria:**
- Track pending construction/destruction
- Maintain ACK bitmasks
- Publish orders on spawn/destroy commands
- Flip lifecycle states when all ACKs received

**Tests Required:**
- Unit test: Construction order published on spawn
- Unit test: ACK mask updates correctly
- Unit test: Entity activated when all ACKs in
- Unit test: Entity destroyed when all ACKs in

**Status:** ⚪ Pending

---

#### Task 6.3: LifecycleSystem Implementation
**Description:** Implement system that processes lifecycle events  
**Design Ref:** Chapter 6, Section 6.2 - LifecycleSystem  
**Acceptance Criteria:**
- Runs in BeforeSync phase
- Processes ConstructionAck events
- Processes DestructionAck events
- Updates entity lifecycle states
- Queues destruction commands

**Tests Required:**
- Integration test: Dark construction workflow
- Integration test: Coordinated teardown workflow
- Integration test: Multi-module ACKs

**Status:** ⚪ Pending

---

#### Task 6.4: Query Lifecycle Filtering
**Description:** Update EntityQuery to filter by lifecycle  
**Design Ref:** Chapter 6, Section 6.2 - Integration  
**Acceptance Criteria:**
- Default queries only see Active entities
- `.WithLifecycle(state)` override method
- `.IncludeConstructing()` helper
- `.IncludeTearDown()` helper

**Tests Required:**
- Unit test: Default query excludes Constructing
- Unit test: WithLifecycle override works
- Integration test: Physics sees staging entities

**Status:** ⚪ Pending

---

#### Task 6.5: ELM Integration Testing
**Description:** Full lifecycle coordination tests  
**Design Ref:** Chapter 6, entire section  
**Acceptance Criteria:**
- Multi-module spawn scenario
- Multi-module destroy scenario
- Partial ACK timeout handling
- Cross-module entity consistency

**Tests Required:**
- Integration test: 3Module_CoordinatedSpawn
- Integration test: 3Module_CoordinatedDestroy
- Integration test: Module_Crash_During_Construction
- Integration test: Lifecycle_State_Consistency

**Status:** ⚪ Pending

---

## 📋 BATCH 07: Network Gateway Core

**Priority:** LOW (P3)  
**Dependencies:** BATCH-06 (can use ELM for entity sync)  
**Design Reference:** [Chapter 7](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-7-network-gateway-ddsst-integration)  
**Estimated Effort:** 1.5 weeks

### Tasks

#### Task 7.1: Translator Interfaces
**Description:** Define abstraction layer for DDS  
**Design Ref:** Chapter 7, Section 7.2 - Translator Interface  
**Acceptance Criteria:**
- `IDescriptorTranslator` interface
- `IDataReader` abstraction
- `IDataWriter` abstraction
- Topic name registration

**Tests Required:**
- Unit test: Translator interface contract
- Unit test: Mock implementations work

**Status:** ⚪ Pending

---

#### Task 7.2: Example EntityState Translator
**Description:** Implement translator for EntityState descriptor  
**Design Ref:** Chapter 7, Section 7.2 - Example Translator  
**Acceptance Criteria:**
- Ingress: Descriptor → Position, Velocity, NetworkTarget components
- Egress: Components → Descriptor
- Network-to-local entity ID mapping
- Ownership checks for egress

**Tests Required:**
- Unit test: Descriptor maps to components
- Unit test: Components map to descriptor
- Integration test: Round-trip preserves data

**Status:** ⚪ Pending

---

#### Task 7.3: NetworkIngestSystem
**Description:** Polling-based ingress system  
**Design Ref:** Chapter 7, Section 7.2 - NetworkIngestSystem  
**Acceptance Criteria:**
- Runs in Input phase
- Iterates all registered translators
- Calls `TakeSamples()` on readers
- Batches updates via command buffer

**Tests Required:**
- Integration test: Ingress processes multiple topics
- Integration test: Command batching works
- Performance test: Ingress latency <1ms for 100 updates

**Status:** ⚪ Pending

---

#### Task 7.4: NetworkSyncSystem
**Description:** Egress system for owned entities  
**Design Ref:** Chapter 7, Section 7.2 - NetworkSyncSystem  
**Acceptance Criteria:**
- Runs in Export phase
- Queries locally-owned entities
- Calls translators to build descriptors
- Publishes to DDS writers

**Tests Required:**
- Integration test: Owned entities published
- Integration test: Remote entities not published
- Performance test: Egress bandwidth

**Status:** ⚪ Pending

---

#### Task 7.5: SSTModule Implementation
**Description:** Complete network gateway module  
**Design Ref:** Chapter 7, Section 7.2 - SSTModule  
**Acceptance Criteria:**
- Registers ingress and egress systems
- Manages translator list
- Configurable execution policy (FastReplica)
- Mock DDS implementation for testing

**Tests Required:**
- Integration test: Full network round-trip
- Integration test: EntityMaster lifecycle sync

**Status:** ⚪ Pending

---

#### Task 7.6: Network Integration Testing
**Description:** End-to-end federation scenarios  
**Design Ref:** Chapter 7, entire section  
**Acceptance Criteria:**
- Multi-node entity creation
- Ownership handoff
- Network latency simulation
- Descriptor version compatibility

**Tests Required:**
- Integration test: TwoNode_EntityCreation
- Integration test: OwnershipHandoff_GhostUpdates
- Performance test: NetworkLatency_Simulation

**Status:** ⚪ Pending

---

## 📋 BATCH 08: Geographic Transform

**Priority:** LOW (P3)  
**Dependencies:** BATCH-07 (needs network components)  
**Design Reference:** [Chapter 8](../docs/DESIGN-IMPLEMENTATION-PLAN.md#chapter-8-geographic-transform-services)  
**Estimated Effort:** 4 days

### Tasks

#### Task 8.1: IGeographicTransform Service
**Description:** Define coordinate transformation interface  
**Design Ref:** Chapter 8, Section 8.2  
**Acceptance Criteria:**
- `SetOrigin(lat, lon, alt)` method
- `ToCartesian()` conversion
- `ToGeodetic()` conversion
- Velocity transformations

**Tests Required:**
- Unit test: Origin setting
- Unit test: Known coordinate conversions
- Unit test: Round-trip accuracy

**Status:** ⚪ Pending

---

#### Task 8.2: CoordinateTransformSystem
**Description:** Sync physics and geodetic representations  
**Design Ref:** Chapter 8, Section 8.2 - CoordinateTransformSystem  
**Acceptance Criteria:**
- Runs in PostSimulation phase
- Outbound: Position → PositionGeodetic for owned
- Inbound: PositionGeodetic → Position for remote
- Delta checking to avoid unnecessary updates

**Tests Required:**
- Integration test: Physics changes sync to geodetic
- Integration test: Ownership check enforced
- Performance test: Transform overhead

**Status:** ⚪ Pending

---

#### Task 8.3: NetworkSmoothingSystem
**Description:** Dead reckoning for remote entities  
**Design Ref:** Chapter 8, Section 8.2 - NetworkSmoothingSystem  
**Acceptance Criteria:**
- Runs in Input phase
- LERP from current to target position
- Only affects non-owned entities
- Configurable smoothing factor

**Tests Required:**
- Integration test: Jittery updates smoothed
- Integration test: Owned entities not affected
- Performance test: Smoothing overhead

**Status:** ⚪ Pending

---

#### Task 8.4: Geographic Integration Testing
**Description:** Validate coordinate system integration  
**Design Ref:** Chapter 8, entire section  
**Acceptance Criteria:**
- Test with real-world coordinates
- Test with extreme latitudes
- Test with velocity transformations
- Test round-trip accuracy

**Tests Required:**
- Integration test: RealWorld_Coordinates
- Integration test: Polar_Regions
- Integration test: HighVelocity_Accuracy
- Performance test: Transform_Throughput

**Status:** ⚪ Pending

---

## 📈 Metrics & Targets

### Performance Targets

| Metric | Target | Critical Threshold |
|--------|--------|-------------------|
| Main Thread Frame Time | <16ms @ 60Hz | <20ms |
| Module Dispatch Overhead | <0.5ms | <1ms |
| Reactive Trigger Check | <0.1ms per module | <0.5ms |
| Convoy Memory Reduction | >80% vs individual | >50% |
| Network Ingress Latency | <1ms per 100 updates | <5ms |
| Coordinate Transform | <0.1ms per entity | <0.5ms |

### Quality Targets

| Metric | Target | Critical Threshold |
|--------|--------|-------------------|
| Test Coverage | >90% | >80% |
| Unit Tests Passing | 100% | 100% |
| Integration Tests Passing | 100% | 100% |
| Compiler Warnings | 0 | 0 |
| Code Review Approval | Required | Required |

---

## 🔄 Dependencies Graph

```
BATCH-01 (Non-Blocking)
    ↓
    ├──→ BATCH-02 (Reactive)
    ├──→ BATCH-04 (Resilience)
    └──→ BATCH-05 (Execution Modes)
         ↓
         └──→ BATCH-06 (ELM)
              ↓
              └──→ BATCH-07 (Network)
                   ↓
                   └──→ BATCH-08 (Geographic)

BATCH-03 (Convoy) ────┘ (parallel, joins at BATCH-05)
```

---

## 📝 Notes

### Critical Path
BATCH-01 → BATCH-05 → BATCH-06 → BATCH-07 → BATCH-08

### Parallel Opportunities
- BATCH-02 and BATCH-03 can run in parallel after BATCH-01
- BATCH-04 can run in parallel with BATCH-03

### Risk Areas
- **BATCH-01:** Fundamental change to execution model, high risk
- **BATCH-04:** Timeout handling is tricky, zombie tasks
- **BATCH-07:** Network integration complexity, mock DDS needed

### Success Criteria
- All batches completed
- All tests passing (100%)
- Performance targets met
- No architectural violations
- Production-ready code quality

---

**Last Updated:** 2026-01-07  
**Next Review:** After each batch completion
