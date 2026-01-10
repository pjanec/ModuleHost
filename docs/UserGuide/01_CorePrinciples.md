## Core Principles

### Data-Oriented Design (DOD)

FDP is built on **Data-Oriented Design**, not Object-Oriented Design. This means:

1. **Data and behavior are separate**
   - Components hold data (structs)
   - Systems hold behavior (classes with logic)

2. **Systems operate on data, not instances**
   - Systems query components
   - Systems never reference other systems
   - Communication happens through data (components, singletons, events)

3. **Cache-friendly access patterns**
   - Components stored in contiguous arrays
   - Parallel iteration over entities
   - Minimal pointer chasing

**Example - The Wrong Way (OOP):**
```csharp
// ❌ ANTI-PATTERN: Systems referencing systems
public class CarKinematicsSystem
{
    private SpatialHashSystem _spatialSystem; // DON'T DO THIS!
    
    void OnUpdate()
    {
        var grid = _spatialSystem.Grid; // Tight coupling!
    }
}
```

**Example - The Right Way (DOD):**
```csharp
// ✅ CORRECT: Systems communicate via data
public class SpatialHashSystem
{
    void OnUpdate()
    {
        // Build grid, publish as singleton
        World.SetSingleton(new SpatialGridData { Grid = _grid });
    }
}

public class CarKinematicsSystem
{
    void OnUpdate()
    {
        // Read singleton (data-driven dependency)
        var gridData = World.GetSingleton<SpatialGridData>();
        // Use gridData.Grid...
    }
}
```
