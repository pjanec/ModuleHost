# Fast Data Plane (FDP)

**Fast Data Plane (FDP)** is a high-performance, deterministic Entity–Component–System (ECS) engine designed for large-scale, real-time simulations and data-intensive runtime environments.
It prioritizes **predictable performance**, **zero allocations on the hot path**, **determinism**, and **deep introspection** over convenience abstractions.

FDP is not a game engine in the traditional sense. It is a **simulation core** and **data execution engine** that can serve as the backbone for games, training simulators, digital twins, large-scale AI simulations, or distributed real-time systems.

---

## Design Philosophy

FDP is built around several non-negotiable principles:

* **Performance must be predictable**, not just fast on average
* **Hot paths must be allocation-free**
* **Execution must be deterministic**
* **Data layout matters more than abstraction elegance**
* **Debugging and replay are first-class concerns**
* **Flexibility must not compromise the hot path**

These principles shape every architectural decision in the engine.

---

## Core Features Overview

### 1. High-Performance ECS Core

FDP implements a **data-oriented ECS** optimized for modern CPUs:

* Components stored in **contiguous memory pools**
* Fixed-size memory chunks for cache efficiency
* SIMD-friendly iteration and filtering
* Extremely fast entity queries via bitmask matching
* No per-entity object overhead
* Constant-time component access by entity ID

Unlike archetype-based ECS designs, FDP uses **component-centric storage**, trading some per-entity contiguity for simpler, more flexible data management and faster random access.

---

### 2. Hybrid Managed / Unmanaged Data Model

A defining feature of FDP is its **explicit two-tier data model**:

#### Tier 1 – Unmanaged Components (Hot Data)

* Plain value types
* Stored in native memory
* Contiguous layout
* Zero garbage collection
* Intended for high-frequency updates (physics, simulation state, AI)

#### Tier 2 – Managed Components (Cold Data)

* Reference types
* Stored on the managed heap
* Accessed indirectly
* Intended for complex or infrequently changing data

  * Strings
  * Object graphs
  * Metadata
  * Configuration state

This separation allows FDP to:

* Keep the hot path extremely fast
* Still support rich, expressive data where needed
* Avoid forcing everything into unsafe or unnatural data representations

Most ECS engines force a single model. FDP embraces **controlled heterogeneity**.

---

### 3. Zero-Allocation Hot Path

FDP is designed so that **steady-state simulation produces no managed allocations**:

* No heap allocation during frame updates
* No iterator allocations
* No hidden boxing
* No LINQ
* No captured lambdas
* No implicit enumerators

All critical iteration is performed via:

* Value-type iterators
* Direct memory access
* Pre-allocated buffers
* Explicit pooling where needed

The result:

* No GC spikes
* Stable frame times
* Predictable latency
* Suitable for real-time and soft real-time systems

---

### 4. SIMD-Accelerated Entity Queries

Entity matching in FDP is based on **wide bitmasks**:

* Each entity carries a fixed-width component signature
* Queries are expressed as bitmask predicates
* Matching is performed using vectorized CPU instructions

This allows:

* Extremely fast filtering
* Branch-free inner loops
* Efficient scanning of very large entity sets

In practice, millions of entity checks per frame are feasible without query overhead becoming dominant.

---

### 5. Deterministic Phase-Based Execution Model

FDP enforces a **strict, explicit execution model** based on phases:

* Each frame is divided into well-defined phases
* Each phase defines:

  * What can be read
  * What can be written
  * What structural changes are allowed
* Illegal access patterns are detected and rejected (in debug builds)

Benefits:

* Deterministic execution order
* No hidden data races
* Lock-free parallelism
* Clear mental model for system interactions

This model replaces ad-hoc synchronization and implicit ordering with **architectural guarantees**.

---

### 6. Built-In Multithreading Without Locks

Parallelism in FDP is achieved through **structural guarantees**, not fine-grained locks:

* Read-only phases can run fully in parallel
* Write phases are isolated and synchronized
* Structural changes are deferred and applied at safe points
* No locking on component access in hot loops

This approach:

* Scales well across cores
* Avoids lock contention
* Preserves determinism
* Keeps systems simple and analyzable

---

### 7. Integrated Event System (Managed & Unmanaged)

FDP provides a **high-performance event bus** aligned with its data model:

* Unmanaged events for high-frequency signaling
* Managed events for complex or low-frequency communication
* Events are phase-aware
* Events can be recorded and replayed

This allows:

* Zero-allocation event processing where needed
* Expressive event payloads where performance is less critical
* Unified event handling across the engine

---

### 8. Flight Data Recorder (Deterministic Replay)

One of FDP’s most distinctive features is its **Flight Data Recorder**.

It provides:

* Frame-by-frame recording of the entire ECS state
* Deterministic replay of simulation runs
* Event replay synchronized with state
* Delta compression between frames
* Optional keyframes
* Asynchronous recording with minimal runtime impact

#### Key Characteristics

* Records raw component memory, not high-level objects
* Sanitizes unused memory to ensure determinism
* Highly compressible snapshots
* Suitable for continuous recording in production builds

Use cases:

* Debugging hard-to-reproduce bugs
* Simulation validation
* Offline analysis
* Regression testing
* Networking and synchronization research

Few ECS engines treat replay as a **first-class architectural concern**. FDP does.

---

### 9. Change Tracking and Delta Propagation

FDP tracks state changes at multiple levels:

* Per-entity
* Per-chunk
* Per-frame

This enables:

* Efficient delta snapshots
* Selective replication
* Fast detection of modified data
* Reduced I/O and serialization overhead

Change tracking is deeply integrated into the ECS core, not bolted on afterward.

---

### 10. Determinism by Construction

FDP is designed so that:

* Given the same inputs
* In the same order
* On the same architecture

…it produces the same outputs.

Determinism is supported by:

* Fixed update ordering
* Explicit phases
* Controlled parallelism
* Explicit event sequencing
* Deterministic memory snapshots

This makes FDP suitable for:

* Replays
* Lockstep simulations
* Validation and certification environments
* Scientific and industrial simulations

---

## What Makes FDP Unique

Compared to other ECS engines, FDP stands out in several areas:

### Compared to Unity DOTS

* Less restrictive data model
* Built-in replay
* Explicit phase safety instead of implicit job contracts
* No dependency on a specific engine ecosystem

### Compared to Traditional C# ECS Frameworks

* True zero-allocation hot path
* Native memory usage
* SIMD-accelerated queries
* Deterministic execution guarantees

### Compared to Custom Simulation Cores

* ECS-based structure with strong architectural rules
* Integrated tooling for replay and debugging
* Balanced support for both performance and expressiveness

FDP occupies a niche where **raw performance, determinism, and debuggability** are equally important.

---

## Intended Use Cases

FDP is particularly well-suited for:

* Large-scale simulations
* Training and military simulators
* AI and agent-based modeling
* Digital twins
* Deterministic multiplayer research
* Performance-critical game cores
* Real-time data processing pipelines

It is less suitable when:

* Rapid prototyping is the primary goal
* Maximum convenience is preferred over predictability
* Determinism is not required



---

Below is a **condensed, promotional-style summary section** suitable for an overview README.
It keeps the architectural intent and differentiators, but trims detail in favor of **clarity, impact, and scannability**.

---

## Diagnostics & Inspectors (Overview)

Fast Data Plane includes a **built-in diagnostic and introspection layer** designed for data-oriented, high-performance systems.

Diagnostics are not external tools or ad-hoc debug helpers—they are **architecturally integrated** and operate directly on ECS data and event streams, without breaking determinism or performance guarantees.

---

### Component Inspector

The Component Inspector provides structured visibility into ECS state:

* Inspect entities and their components
* View unmanaged (hot) and managed (cold) data distinctly
* Observe component values and structural composition
* Track state changes across frames

Inspection is phase-aware and safe, ensuring that runtime invariants are never violated.

---

### Event Inspector

The Event Inspector exposes the engine’s event-driven behavior:

* Observe both unmanaged and managed events
* Inspect event payloads and ordering
* Correlate events with simulation frames
* Trace cause–effect relationships between events and state changes

This makes complex, event-driven logic transparent and debuggable.

---

### Replay-Aware Diagnostics

Diagnostics integrate seamlessly with the Flight Data Recorder:

* Inspect live simulations or recorded replays
* Step through historical state deterministically
* Analyze rare or timing-sensitive issues offline

This enables **time-travel debugging** without special instrumentation.

---

### Designed for Production Use

FDP diagnostics are:

* Allocation-free in read-only mode
* Deterministic and reproducible
* Phase-safe and non-intrusive
* Suitable for long-running and headless systems

They can be left enabled in performance-sensitive environments and form a foundation for monitoring, validation, and operational analysis.

---

### Why It Matters

In data-oriented, parallel systems, understanding *what the data is doing* matters more than inspecting call stacks.

FDP’s diagnostic tools provide:

* Deep observability
* Deterministic insight
* Confidence in correctness at scale

They complete FDP’s vision of a simulation engine that is not only fast and deterministic, but also **transparent, debuggable, and operable**.


---

## Summary

Fast Data Plane is an ECS engine built for engineers who care deeply about:

* Data layout
* Performance guarantees
* Deterministic behavior
* Debuggability
* Architectural clarity

It deliberately trades some convenience and abstraction for:

* Predictable execution
* Zero-GC hot paths
* Powerful introspection
* Long-term maintainability at scale

If your system needs to be **fast, correct, reproducible, and analyzable**, FDP provides a foundation designed precisely for that purpose.

