# Car Kinematics Design Addendum

**Date:** 2026-01-07  
**Author:** Architect  
**Purpose:** Architectural corrections based on BATCH-CK-07/08 review  
**Status:** Approved for implementation  

---

## Background

During implementation of BATCH-CK-07 and BATCH-CK-08, the developer encountered API gaps and implemented workarounds. The architect has reviewed these issues and provided definitive guidance on the correct FDP/Data-Oriented patterns.

---

## Architectural Decisions

### Decision 1: `SetComponent<T>()` - Add as Alias

**Issue:** `EntityRepository` only exposes `AddComponent<T>()`, which semantically reads as "add new" when developers actually mean "update existing."

**Resolution:** Add `SetComponent<T>()` as a **zero-overhead alias** to `AddComponent<T>()`.

**Rationale:**
- In FDP, `AddComponent` is already implemented as "upsert" (update-or-insert) for performance
- Adding a strict "SetComponent" (throws if missing) requires extra `HasComponent` lookup
- Alias provides semantic clarity without performance cost

**Implementation:**
```csharp
// Fdp.Kernel/EntityRepository.cs
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void SetComponent<T>(Entity entity, T component) where T : struct
{
    AddComponent<T>(entity, component); // Already handles upsert efficiently
}
```

**Impact:** Improves code readability in update scenarios.

---

### Decision 2: System Dependencies - **ANTI-PATTERN**

**Issue:** Developer attempted `World.GetSystem<T>()` to allow `CarKinematicsSystem` to access `SpatialHashSystem`.

**Resolution:** **REJECT.** Systems must **never** reference other systems directly.

**Rationale:**
- Violates Data-Oriented Design principles
- Creates tight coupling
- Breaks system independence
- Makes testing difficult

**The FDP Way:** Systems communicate through **Data** (Components, Singletons, Events).

**Correct Pattern - Singleton Components:**

1. Producer system writes data to singleton
2. Consumer system reads data from singleton
3. Execution order managed by attributes

**Implementation:**
```csharp
// Producer: SpatialHashSystem
protected override void OnUpdate()
{
    // Build grid
    _grid.Clear();
    foreach (var entity in query)
    {
        var state = World.GetComponent<VehicleState>(entity);
        _grid.Add(entity.Id, state.Position);
    }
    
    // Publish as singleton
    World.SetSingleton(new SpatialGridData { Grid = _grid });
}

// Consumer: CarKinematicsSystem
protected override void OnUpdate()
{
    // Read singleton (dependency resolved via data)
    var gridData = World.GetSingleton<SpatialGridData>();
    var spatialGrid = gridData.Grid;
    
    // Use grid...
}
```

**Impact:** Major refactor of CK-07 required.

---

### Decision 3: System Attributes - Use FDP Kernel Attributes

**Issue:** Developer used non-existent `[SystemAttributes(Phase, UpdateFrequency)]`.

**Resolution:** Use **FDP Kernel** attributes: `[UpdateInGroup]`, `[UpdateBefore]`, `[UpdateAfter]`.

**Rationale:**
- `CarKinematicsSystem` extends `ComponentSystem` (FDP Kernel)
- ModuleHost attributes (`[UpdateInPhase]`) are for ModuleHost-level systems
- FDP Kernel has its own scheduling system

**Correct Usage:**
```csharp
using Fdp.Kernel;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpatialHashSystem))]
public class CarKinematicsSystem : ComponentSystem
{
    // ...
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(CarKinematicsSystem))]
public class FormationTargetSystem : ComponentSystem
{
    // ...
}
```

**Impact:** Declarative scheduling replaces manual registration order.

---

### Decision 4: Parallel Iteration - Use `ForEachParallel`

**Issue:** Developer manually collected entities to `List<Entity>` then used `Parallel.ForEach`, causing GC allocations.

**Resolution:** Use **FDP's built-in `ForEachParallel`** which uses pooled batches.

**Rationale:**
- `query.ToArray()` allocates managed array (GC pressure)
- Manual collection to list also allocates
- FDP `ForEachParallel` uses internal `BatchListPool` (zero GC)
- Optimized for cache locality

**Correct Usage:**
```csharp
// BAD (allocates):
var entities = new List<Entity>();
foreach (var e in query) entities.Add(e);
Parallel.ForEach(entities, entity => { ... });

// GOOD (zero GC):
query.ForEachParallel((entity, index) => 
{
    UpdateVehicle(entity, dt, spatialGrid);
});
```

**Impact:** Performance improvement, zero GC in hot path.

---

### Decision 5: FormationRoster Entity Storage - Use Full Entity Handles

**Issue:** `FormationRoster` stores `fixed int MemberEntityIds[16]` (only IDs, no generation).

**Resolution:** Change to store **full `Entity` handles** (ID + generation).

**Rationale:**
- Entity IDs alone are not safe (generation prevents stale references)
- Current workaround uses `World.GetHeader(id)` to reconstruct Entity
- Storing full Entity is cleaner and safer

**Struct Change:**
```csharp
// OLD (unsafe):
public unsafe struct FormationRoster
{
    public fixed int MemberEntityIds[16];  // Only IDs
    // ...
}

// NEW (safe):
[StructLayout(LayoutKind.Sequential)]
public struct FormationRoster
{
    public unsafe fixed long MemberEntities[16];  // Full Entity (8 bytes each: 4B ID + 4B gen)
    // OR use array if fixed long is problematic:
    // [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    // public Entity[] Members;
    // ...
}
```

**Helper Methods:**
```csharp
public unsafe void SetMember(int index, Entity entity)
{
    if (index < 0 || index >= 16) return;
    MemberEntities[index] = *(long*)&entity; // Reinterpret Entity as long
}

public unsafe Entity GetMember(int index)
{
    if (index < 0 || index >= 16) return Entity.Null;
    long value = MemberEntities[index];
    return *(Entity*)&value; // Reinterpret long as Entity
}
```

**Impact:** `FormationTargetSystem` becomes much cleaner, no more `GetHeader` workarounds.

---

## Summary of Changes Required

### Tier 1: Kernel API Updates (Foundation)
1. Add `SetComponent<T>()` alias to `EntityRepository`
2. Ensure `ForEachParallel` is exposed on `EntityQuery`
3. Document that `AddComponent` is upsert behavior

### Tier 2: CarKinem Refactors (Corrective Batch)
1. **SpatialHashSystem:**
   - Create `SpatialGridData` singleton component
   - Remove exposed `Grid` property
   - Write grid to singleton in `OnUpdate`

2. **CarKinematicsSystem:**
   - Remove `SpatialHashSystem` dependency
   - Read `SpatialGridData` singleton
   - Replace manual entity collection with `ForEachParallel`
   - Add `[UpdateInGroup]` and `[UpdateAfter]` attributes

3. **FormationRoster:**
   - Change `fixed int MemberEntityIds[16]` to `fixed long MemberEntities[16]`
   - Add helper methods: `SetMember()`, `GetMember()`
   - Update all usage sites

4. **FormationTargetSystem:**
   - Use new `FormationRoster.GetMember()` helper
   - Remove `GetHeader` workaround
   - Add `[UpdateInGroup]` and `[UpdateBefore]` attributes

5. **All Systems:**
   - Replace `AddComponent` with `SetComponent` where semantically correct

---

## Migration Strategy

**Corrective Batch:** BATCH-CK-FIX-01

**Scope:**
- Fdp.Kernel API additions (SetComponent, ForEachParallel documentation)
- CarKinem system refactors (singletons, attributes, FormationRoster)
- Test updates

**Priority:** High - Fixes architectural violations before demo integration

**Estimated Time:** 2-3 hours

---

## Documentation Updates

**README.md additions:**
1. Data-Oriented System Communication (Singleton pattern)
2. Parallel iteration best practices (`ForEachParallel`)
3. Entity handle safety (why generation matters)

---

**Document Version:** 1.0  
**Status:** Approved  
**Next Action:** Implement BATCH-CK-FIX-01
