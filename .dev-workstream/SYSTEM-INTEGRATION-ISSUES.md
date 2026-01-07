# System Integration Issues - Architect Review Required

**Date:** 2026-01-07  
**Context:** BATCH-CK-07 (Car Kinematics System) implementation  
**Status:** Functional with workarounds, needs architectural review  

---

## Issue Summary

During BATCH-CK-07 implementation, the developer encountered several missing or non-standard APIs in the FDP/ModuleHost framework. The system is functional with workarounds, but these should be reviewed for proper architectural solutions.

---

## Issue 1: Missing `EntityRepository.SetComponent<T>()`

### Expected Behavior
```csharp
// From BATCH-CK-07 instructions (line 175-176):
World.SetComponent(entity, state);
World.SetComponent(entity, nav);
```

### Actual Behavior
- `EntityRepository` does not expose `SetComponent<T>()` method
- Only `AddComponent<T>()` is available

### Current Workaround
```csharp
// Developer used AddComponent as upsert:
World.AddComponent(entity, state);
World.AddComponent(entity, nav);
```

### Impact
- ✅ **Functional:** `AddComponent` acts as upsert (overwrites existing)
- ⚠️ **Semantic mismatch:** Code reads as "add" when it means "update"
- ⚠️ **Performance:** Unknown if `AddComponent` has overhead for existing components

### Recommended Solution
**Option A:** Add `SetComponent<T>()` method to `EntityRepository`
```csharp
public void SetComponent<T>(Entity entity, T component) where T : struct
{
    // Update existing component, throw if not exists
}
```

**Option B:** Add `AddOrUpdateComponent<T>()` for clarity
```csharp
public void AddOrUpdateComponent<T>(Entity entity, T component) where T : struct
{
    // Explicit upsert semantics
}
```

**Option C:** Document that `AddComponent` is intended as upsert

---

## Issue 2: Missing `ComponentSystem.World.GetSystem<T>()`

### Expected Behavior
```csharp
// From BATCH-CK-07 instructions (line 42-43):
protected override void OnCreate()
{
    _spatialHashSystem = World.GetSystem<SpatialHashSystem>();
}
```

### Actual Behavior
- `EntityRepository` (accessed via `World` property) does not have `GetSystem<T>()` method
- No standard way for one system to reference another

### Current Workaround
```csharp
// Developer added override property for testing:
internal SpatialHashSystem SpatialSystemOverride { get; set; }

protected override void OnCreate()
{
    if (SpatialSystemOverride != null)
    {
        _spatialHashSystem = SpatialSystemOverride;
        return;
    }
    throw new InvalidOperationException("World.GetSystem not implemented");
}
```

### Impact
- ❌ **Not functional in production:** Requires manual injection
- ✅ **Tests work:** Can inject via override property
- ⚠️ **Architecture smell:** Systems should be able to locate dependencies

### Recommended Solution
**Option A:** Add `GetSystem<T>()` to `EntityRepository`
```csharp
public class EntityRepository
{
    private readonly Dictionary<Type, ComponentSystem> _systems = new();
    
    public T GetSystem<T>() where T : ComponentSystem
    {
        if (_systems.TryGetValue(typeof(T), out var system))
            return (T)system;
        throw new InvalidOperationException($"System {typeof(T).Name} not registered");
    }
    
    internal void RegisterSystem(ComponentSystem system)
    {
        _systems[system.GetType()] = system;
    }
}
```

**Option B:** Use dependency injection via constructor
```csharp
// Pass dependencies explicitly:
var spatialSystem = new SpatialHashSystem();
var kinematicsSystem = new CarKinematicsSystem(roadNetwork, trajectoryPool, spatialSystem);
```

**Option C:** Use `ISystemRegistry` if it exists
```csharp
// If registry pattern is already implemented:
_spatialHashSystem = World.GetSystemRegistry().GetSystem<SpatialHashSystem>();
```

---

## Issue 3: Missing `[SystemAttributes]` Class in Fdp.Kernel

### Expected Behavior
```csharp
// From BATCH-CK-07 instructions (line 19):
[SystemAttributes(Phase = Phase.Update, UpdateFrequency = UpdateFrequency.EveryFrame)]
public class CarKinematicsSystem : ComponentSystem
{
    // ...
}
```

### Actual Behavior
- `SystemAttributes` class not found in `Fdp.Kernel`
- `Phase` enum not found
- `UpdateFrequency` enum not found

### Current Workaround
```csharp
// Developer commented out attribute:
// [SystemAttributes(Phase = Phase.Update, UpdateFrequency = UpdateFrequency.EveryFrame)]
public class CarKinematicsSystem : ComponentSystem
{
    // ...
}
```

### Impact
- ⚠️ **No automatic scheduling:** System order must be managed manually
- ⚠️ **Missing metadata:** No declarative phase/frequency information
- ✅ **Functional:** System still executes when manually called

### Recommended Solution
**Option A:** Implement `SystemAttributes` in Fdp.Kernel
```csharp
namespace Fdp.Kernel
{
    [AttributeUsage(AttributeTargets.Class)]
    public class SystemAttributes : Attribute
    {
        public Phase Phase { get; set; }
        public UpdateFrequency UpdateFrequency { get; set; }
    }
    
    public enum Phase
    {
        EarlyUpdate,
        Update,
        LateUpdate
    }
    
    public enum UpdateFrequency
    {
        EveryFrame,
        FixedTimestep
    }
}
```

**Option B:** Use existing ModuleHost.Core attributes
```csharp
// If attributes exist in ModuleHost.Core, move to Fdp.Kernel or make accessible
using ModuleHost.Core;

[SystemAttributes(Phase = Phase.Update)]
public class CarKinematicsSystem : ComponentSystem
```

**Option C:** Drop attributes, use explicit registration
```csharp
// Manual registration with phase/frequency:
scheduler.RegisterSystem(kinematicsSystem, Phase.Update, UpdateFrequency.EveryFrame);
```

---

## Issue 4: Missing `EntityQuery.ToArray()`

### Expected Behavior
```csharp
// From BATCH-CK-07 instructions (line 85):
var query = World.Query<VehicleState, VehicleParams, NavState>();
var entities = query.ToArray();
```

### Actual Behavior
- `EntityQuery` (result of `.Build()`) does not have `ToArray()` method
- Only supports `foreach` enumeration

### Current Workaround
```csharp
// Developer manually collected to list:
var query = World.Query()
    .With<VehicleState>()
    .With<VehicleParams>()
    .With<NavState>()
    .Build();

var entityList = new List<Entity>();
foreach (var e in query) entityList.Add(e);
var entities = entityList;

Parallel.ForEach(entities, entity => { ... });
```

### Impact
- ✅ **Functional:** Achieves same result
- ⚠️ **Extra allocation:** Creates intermediate list
- ⚠️ **Verbose:** More code than intended

### Recommended Solution
**Option A:** Add `ToArray()` extension method
```csharp
public static class EntityQueryExtensions
{
    public static Entity[] ToArray(this EntityQuery query)
    {
        var list = new List<Entity>();
        foreach (var entity in query)
            list.Add(entity);
        return list.ToArray();
    }
}
```

**Option B:** Make EntityQuery implement `IEnumerable<Entity>` fully
```csharp
// If not already, ensure .ToArray() works via LINQ:
using System.Linq;
var entities = query.ToArray(); // Uses LINQ ToArray()
```

---

## Issue 5: VehicleParams Field Naming Inconsistency

### Expected vs Actual
```csharp
// BATCH-CK-07 instructions used:
@params.LookaheadMin    // Expected
@params.LookaheadMax    // Expected
@params.SpeedGain       // Expected

// Actual VehicleParams fields from BATCH-CK-01:
@params.LookaheadTimeMin   // Actual
@params.LookaheadTimeMax   // Actual
@params.AccelGain          // Actual
```

### Impact
- ⚠️ **Semantic confusion:** "Time" vs "Distance" - these are used as distances (meters)
- ✅ **Developer adapted:** Used correct field names

### Recommended Solution
**Clarify naming convention:**
- If these are **time** values (seconds), document that PurePursuit converts to distance
- If these are **distance** values (meters), rename to `LookaheadDistMin/Max`

**Proposed fix:**
```csharp
public struct VehicleParams
{
    // Rename for clarity:
    public float LookaheadDistMin;  // Was: LookaheadTimeMin
    public float LookaheadDistMax;  // Was: LookaheadTimeMax
    
    // Or add documentation:
    /// <summary>Lookahead distance in meters (despite name)</summary>
    public float LookaheadTimeMin;
}
```

---

## Summary Table

| Issue | Severity | Workaround | Blocks Production? |
|-------|----------|------------|-------------------|
| Missing SetComponent | Low | Use AddComponent | No |
| Missing GetSystem | **High** | Manual injection | **Yes** |
| Missing SystemAttributes | Medium | Manual scheduling | No |
| Missing ToArray | Low | Manual collection | No |
| Field naming | Low | Use actual names | No |

---

## Recommended Architect Actions

### Priority 1 (Blocks Production)
1. **Implement `GetSystem<T>()` or define dependency injection pattern**
   - Systems need to reference each other
   - Current workaround only viable for tests

### Priority 2 (Improves Developer Experience)
2. **Clarify `SetComponent` vs `AddComponent` semantics**
   - Add documentation or new method
3. **Implement `SystemAttributes` or document scheduling approach**
   - Needed for declarative system ordering
4. **Add `ToArray()` to `EntityQuery` or document LINQ usage**
   - Common pattern for parallel processing

### Priority 3 (Polish)
5. **Review VehicleParams field naming**
   - Avoid "Time" for distance values

---

## Current System Status

✅ **Functional:** All tests pass, system works with workarounds  
⚠️ **Production-ready:** Requires manual system wiring  
📋 **Technical debt:** 5 API gaps documented above  

**Recommendation:** Consult architect on dependency injection strategy (Issue #2) before proceeding to demo application (BATCH-CK-10).
