# Reusable Diagnostics Library Design

**Date:** January 5, 2026  
**Purpose:** Modular, reusable debug visualization for FDP/ModuleHost  
**Target:** New project `Fdp.Diagnostics.Raylib`

---

## Architecture

### New Project Structure

```
Fdp.Diagnostics.Raylib/
├── Core/
│   ├── DiagnosticsWindow.cs           // Main window orchestrator
│   ├── IPanel.cs                       // Panel interface
│   └── PanelLayout.cs                  // Layout manager
├── Panels/
│   ├── EntityInspectorPanel.cs         // Generic entity/component viewer
│   ├── EventStreamPanel.cs             // Event visualization
│   ├── ModulePerformancePanel.cs       // Module stats
│   ├── WorldViewPanel.cs               // 2D/3D entity renderer
│   └── QueryDebuggerPanel.cs           // EntityQuery analysis
├── Widgets/
│   ├── Sparkline.cs                    // Performance graph widget
│   ├── TreeView.cs                     // Hierarchical data
│   ├── PropertyGrid.cs                 // Component properties
│   └── BarGraph.cs                     // Event counts
└── Abstractions/
    ├── IEntityInspector.cs             // Data provider interface
    ├── IEventStream.cs                 // Event source interface
    ├── IModuleMetrics.cs               // Performance metrics
    └── IWorldRenderer.cs               // Rendering adapter

Examples/
└── Fdp.Examples.BattleRoyale/
    └── Diagnostics/
        └── BattleRoyaleInspector.cs    // Project-specific adapters
```

---

## Core Abstraction Interfaces

### 1. IEntityInspector - Entity Data Provider

**Purpose:** Allow any ECS to expose entity/component data

```csharp
namespace Fdp.Diagnostics.Abstractions;

public interface IEntityInspector
{
    // Get all entities
    IEnumerable<Entity> GetAllEntities();
    
    // Get components on entity
    IEnumerable<ComponentInfo> GetComponents(Entity entity);
    
    // Get component value as inspectable object
    object? GetComponentValue(Entity entity, Type componentType);
    
    // Get entity metadata (name, archetype, etc.)
    EntityMetadata GetMetadata(Entity entity);
}

public record ComponentInfo(
    string Name,
    Type Type,
    bool IsManaged,
    int SizeBytes
);

public record EntityMetadata(
    Entity Id,
    string DisplayName,
    bool IsAlive,
    int Generation,
    string[] Tags
);
```

---

### 2. IEventStream - Event Data Provider

**Purpose:** Generic event inspection

```csharp
public interface IEventStream
{
    // Get registered event types
    IEnumerable<EventTypeInfo> GetEventTypes();
    
    // Get event history (last N frames)
    IReadOnlyList<EventRecord> GetHistory(Type eventType, int frameCount = 60);
    
    // Get event count this frame
    int GetCountThisFrame(Type eventType);
}

public record EventTypeInfo(
    string Name,
    Type Type,
    int TypeId,
    bool IsManaged
);

public record EventRecord(
    uint Tick,
    float Time,
    object EventData
);
```

---

### 3. IModuleMetrics - Module Performance

**Purpose:** Module execution tracking

```csharp
public interface IModuleMetrics
{
    // Get all registered modules
    IReadOnlyList<ModuleInfo> GetModules();
    
    // Get execution stats
    ExecutionStats GetStats(string moduleName);
    
    // Get custom module metrics
    IReadOnlyDictionary<string, object> GetCustomMetrics(string moduleName);
}

public record ModuleInfo(
    string Name,
    ModuleTier Tier,
    int UpdateFrequency,
    Type ModuleType
);

public record ExecutionStats(
    int ExecutionCount,      // This second
    int ExpectedCount,       // Based on frequency
    float AvgTimeMs,         // Average execution time
    float MaxTimeMs,         // Worst case
    float[] TimeHistory      // Last 60 frames
);
```

---

### 4. IWorldRenderer - Custom Rendering

**Purpose:** Project-specific entity visualization

```csharp
public interface IWorldRenderer
{
    // Render all entities in world space
    void RenderWorld(Camera2D camera, IEntityInspector inspector);
    
    // Get bounds of world
    Rectangle GetWorldBounds();
    
    // Get entity at screen position (for selection)
    Entity? GetEntityAtPosition(Vector2 screenPos, Camera2D camera, IEntityInspector inspector);
}
```

---

## Reusable Panels

### 1. EntityInspectorPanel

**Generic entity/component viewer**

```csharp
public class EntityInspectorPanel : IPanel
{
    private readonly IEntityInspector _inspector;
    private Entity? _selectedEntity;
    
    public EntityInspectorPanel(IEntityInspector inspector)
    {
        _inspector = inspector;
    }
    
    public void Render(Rectangle bounds)
    {
        // Left: Entity list (filterable, searchable)
        var entities = _inspector.GetAllEntities();
        RenderEntityList(entities, new Rectangle(bounds.X, bounds.Y, bounds.Width * 0.3f, bounds.Height));
        
        // Right: Component details
        if (_selectedEntity != null)
        {
            var components = _inspector.GetComponents(_selectedEntity.Value);
            RenderComponentInspector(components, new Rectangle(bounds.X + bounds.Width * 0.3f, bounds.Y, bounds.Width * 0.7f, bounds.Height));
        }
    }
    
    private void RenderComponentInspector(IEnumerable<ComponentInfo> components, Rectangle bounds)
    {
        foreach (var comp in components)
        {
            // Use PropertyGrid widget to display component fields
            object? value = _inspector.GetComponentValue(_selectedEntity.Value, comp.Type);
            PropertyGrid.Render(comp.Name, value, bounds);
        }
    }
}
```

---

### 2. EventStreamPanel

**Generic event visualization**

```csharp
public class EventStreamPanel : IPanel
{
    private readonly IEventStream _eventStream;
    private readonly int _historyFrames = 60;
    
    public void Render(Rectangle bounds)
    {
        var eventTypes = _eventStream.GetEventTypes();
        
        float yOffset = bounds.Y;
        float barHeight = bounds.Height / eventTypes.Count();
        
        foreach (var eventType in eventTypes)
        {
            // Get event history for visualization
            var history = _eventStream.GetHistory(eventType.Type, _historyFrames);
            int currentCount = _eventStream.GetCountThisFrame(eventType.Type);
            
            // Render bar graph
            var barBounds = new Rectangle(bounds.X, yOffset, bounds.Width, barHeight);
            BarGraph.Render(eventType.Name, history, currentCount, barBounds);
            
            yOffset += barHeight;
        }
    }
}
```

---

### 3. ModulePerformancePanel

**Module execution stats**

```csharp
public class ModulePerformancePanel : IPanel
{
    private readonly IModuleMetrics _metrics;
    
    public void Render(Rectangle bounds)
    {
        var modules = _metrics.GetModules();
        
        float yOffset = bounds.Y;
        float cardHeight = 120;
        
        foreach (var module in modules)
        {
            var stats = _metrics.GetStats(module.Name);
            var customMetrics = _metrics.GetCustomMetrics(module.Name);
            
            var cardBounds = new Rectangle(bounds.X, yOffset, bounds.Width, cardHeight);
            RenderModuleCard(module, stats, customMetrics, cardBounds);
            
            yOffset += cardHeight + 10;
        }
    }
    
    private void RenderModuleCard(ModuleInfo module, ExecutionStats stats, IReadOnlyDictionary<string, object> custom, Rectangle bounds)
    {
        // Card background
        Color tierColor = module.Tier == ModuleTier.Fast ? Color.Green : Color.Orange;
        Raylib.DrawRectangleLinesEx(bounds, 2, tierColor);
        
        // Module name
        Raylib.DrawText(module.Name, (int)bounds.X + 10, (int)bounds.Y + 5, 20, Color.White);
        
        // Stats
        Raylib.DrawText($"Exec: {stats.ExecutionCount}/{stats.ExpectedCount}  {stats.AvgTimeMs:F2}ms", 
            (int)bounds.X + 10, (int)bounds.Y + 30, 14, Color.LightGray);
        
        // Sparkline (reusable widget)
        Sparkline.Render(stats.TimeHistory, new Rectangle(bounds.X + 10, bounds.Y + 50, bounds.Width - 20, 30));
        
        // Custom metrics
        int yOff = 85;
        foreach (var (key, value) in custom)
        {
            Raylib.DrawText($"{key}: {value}", (int)bounds.X + 10, (int)bounds.Y + yOff, 12, Color.Gray);
            yOff += 18;
        }
    }
}
```

---

### 4. WorldViewPanel

**2D world rendering with generic adapter**

```csharp
public class WorldViewPanel : IPanel
{
    private readonly IWorldRenderer _renderer;
    private readonly IEntityInspector _inspector;
    private Camera2D _camera;
    private Entity? _selectedEntity;
    
    public WorldViewPanel(IWorldRenderer renderer, IEntityInspector inspector)
    {
        _renderer = renderer;
        _inspector = inspector;
        
        var worldBounds = _renderer.GetWorldBounds();
        _camera = new Camera2D
        {
            Target = new Vector2(worldBounds.X + worldBounds.Width / 2, worldBounds.Y + worldBounds.Height / 2),
            Offset = new Vector2(0, 0), // Updated on first render
            Zoom = 1.0f
        };
    }
    
    public void Render(Rectangle bounds)
    {
        // Update camera offset to panel bounds
        _camera.Offset = new Vector2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        
        // Handle camera controls (WASD, zoom)
        UpdateCamera();
        
        // Render using Raylib camera
        Raylib.BeginMode2D(_camera);
        
        // Delegate actual rendering to project-specific renderer
        _renderer.RenderWorld(_camera, _inspector);
        
        Raylib.EndMode2D();
        
        // Handle entity selection
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            var mousePos = Raylib.GetMousePosition();
            if (IsInBounds(mousePos, bounds))
            {
                _selectedEntity = _renderer.GetEntityAtPosition(mousePos, _camera, _inspector);
            }
        }
    }
    
    public Entity? SelectedEntity => _selectedEntity;
}
```

---

## Usage Example: BattleRoyale

### Adapter Implementation

```csharp
// File: Examples/Fdp.Examples.BattleRoyale/Diagnostics/BattleRoyaleInspector.cs

public class BattleRoyaleInspector : IEntityInspector, IEventStream, IModuleMetrics, IWorldRenderer
{
    private readonly EntityRepository _world;
    private readonly ModuleHostKernel _moduleHost;
    
    // IEntityInspector implementation
    public IEnumerable<Entity> GetAllEntities()
    {
        var query = _world.Query().Build();
        foreach (var e in query)
            yield return e;
    }
    
    public IEnumerable<ComponentInfo> GetComponents(Entity entity)
    {
        // Use reflection or component masks
        if (_world.HasComponent<Position>(entity))
            yield return new ComponentInfo("Position", typeof(Position), false, 8);
        if (_world.HasComponent<Health>(entity))
            yield return new ComponentInfo("Health", typeof(Health), false, 8);
        if (_world.HasManagedComponent<Team>(entity))
            yield return new ComponentInfo("Team", typeof(Team), true, 0);
        // ... etc
    }
    
    public object? GetComponentValue(Entity entity, Type componentType)
    {
        // Use generic GetComponent method via reflection
        var method = _world.GetType().GetMethod("GetComponentRO")?.MakeGenericMethod(componentType);
        return method?.Invoke(_world, new object[] { entity });
    }
    
    // IWorldRenderer implementation
    public void RenderWorld(Camera2D camera, IEntityInspector inspector)
    {
        // Project-specific rendering
        var query = _world.Query().With<Position>().Build();
        
        foreach (var e in query)
        {
            ref readonly var pos = ref _world.GetComponentRO<Position>(e);
            
            // Render based on entity type
            if (_world.HasComponent<NetworkState>(e))
            {
                // Player: circle
                var team = _world.GetManagedComponentRO<Team>(e);
                Color color = team.TeamName == "Alpha" ? Color.Blue : Color.Red;
                Raylib.DrawCircleV(new Vector2(pos.X, pos.Y), 0.5f, color);
            }
            else if (_world.HasComponent<AIState>(e))
            {
                // Bot: triangle
                DrawTriangle(new Vector2(pos.X, pos.Y), 0.5f, Color.Yellow);
            }
            // ... etc
        }
    }
    
    public Rectangle GetWorldBounds() => new Rectangle(0, 0, 1000, 1000);
    
    public Entity? GetEntityAtPosition(Vector2 screenPos, Camera2D camera, IEntityInspector inspector)
    {
        var worldPos = Raylib.GetScreenToWorld2D(screenPos, camera);
        
        // Find closest entity
        Entity? closest = null;
        float closestDist = float.MaxValue;
        
        foreach (var e in GetAllEntities())
        {
            ref readonly var pos = ref _world.GetComponentRO<Position>(e);
            float dx = pos.X - worldPos.X;
            float dy = pos.Y - worldPos.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            
            if (dist < 1.0f && dist < closestDist)
            {
                closest = e;
                closestDist = dist;
            }
        }
        
        return closest;
    }
}
```

---

### Main Window Setup

```csharp
// File: Examples/Fdp.Examples.BattleRoyale/Program.cs

var world = new EntityRepository();
var moduleHost = new ModuleHostKernel(world, accumulator);

// Create diagnostics inspector (implements all interfaces)
var inspector = new BattleRoyaleInspector(world, moduleHost);

// Create diagnostics window with reusable panels
var diagnostics = new DiagnosticsWindow(1920, 1080, "BattleRoyale Diagnostics");

// Add panels (order determines layout)
diagnostics.AddPanel("World", new WorldViewPanel(inspector, inspector), new Rectangle(0, 30, 1400, 870));
diagnostics.AddPanel("Entities", new EntityInspectorPanel(inspector), new Rectangle(1400, 30, 520, 300));
diagnostics.AddPanel("Modules", new ModulePerformancePanel(inspector), new Rectangle(1400, 330, 520, 540));
diagnostics.AddPanel("Events", new EventStreamPanel(inspector), new Rectangle(0, 900, 1920, 180));

// Render loop
while (!Raylib.WindowShouldClose())
{
    moduleHost.Update(deltaTime);
    diagnostics.Render(deltaTime);
}
```

---

## Reusable Widgets

### Sparkline Widget

```csharp
public static class Sparkline
{
    public static void Render(float[] values, Rectangle bounds)
    {
        if (values.Length < 2) return;
        
        float max = values.Max();
        float min = values.Min();
        float range = max - min;
        if (range < 0.001f) range = 1.0f;
        
        float xStep = bounds.Width / (values.Length - 1);
        
        for (int i = 0; i < values.Length - 1; i++)
        {
            float y1 = bounds.Y + bounds.Height - (values[i] - min) / range * bounds.Height;
            float y2 = bounds.Y + bounds.Height - (values[i + 1] - min) / range * bounds.Height;
            
            Raylib.DrawLineEx(
                new Vector2(bounds.X + i * xStep, y1),
                new Vector2(bounds.X + (i + 1) * xStep, y2),
                2.0f,
                Color.Lime
            );
        }
    }
}
```

---

### PropertyGrid Widget

```csharp
public static class PropertyGrid
{
    public static void Render(string name, object? value, Rectangle bounds)
    {
        // Render property name
        Raylib.DrawText(name, (int)bounds.X, (int)bounds.Y, 14, Color.White);
        
        // Render value based on type
        if (value == null)
        {
            Raylib.DrawText("null", (int)bounds.X + 150, (int)bounds.Y, 14, Color.Gray);
        }
        else if (value is IFormattable formattable)
        {
            Raylib.DrawText(formattable.ToString(), (int)bounds.X + 150, (int)bounds.Y, 14, Color.LightGray);
        }
        else
        {
            // Recursive rendering for complex types
            RenderObjectProperties(value, new Rectangle(bounds.X + 20, bounds.Y + 20, bounds.Width - 20, bounds.Height - 20));
        }
    }
    
    private static void RenderObjectProperties(object obj, Rectangle bounds)
    {
        var properties = obj.GetType().GetProperties();
        float yOffset = bounds.Y;
        
        foreach (var prop in properties)
        {
            var value = prop.GetValue(obj);
            Raylib.DrawText($"{prop.Name}: {value}", (int)bounds.X, (int)yOffset, 12, Color.Gray);
            yOffset += 18;
        }
    }
}
```

---

## Benefits

**Reusability:**
- ✅ Use in any FDP/ModuleHost project
- ✅ Swap out rendering backend (Raylib → ImGui → Web)
- ✅ Standard widgets for common visualizations

**Separation of Concerns:**
- ✅ Diagnostics library = generic panels
- ✅ Project = data adapters (implements interfaces)
- ✅ Clean abstraction layer

**Development Speed:**
- ✅ New projects get diagnostics "for free"
- ✅ Focus on project-specific rendering only
- ✅ Reuse EntityInspector, EventStream, etc.

**Maintainability:**
- ✅ Single place to fix bugs
- ✅ Improvements benefit all projects
- ✅ Well-tested generic components

---

## Project Dependencies

```
Fdp.Diagnostics.Raylib.csproj
├── Raylib-cs (6.0.0)
└── (No FDP-specific dependencies - uses abstractions only)

Fdp.Examples.BattleRoyale.csproj
├── Fdp.Kernel
├── ModuleHost.Core
└── Fdp.Diagnostics.Raylib  (new)
```

---

## DEMO-05 Updated Plan

**TASK-012:** Create `Fdp.Diagnostics.Raylib` project (3 SP)
- Core interfaces
- Generic panels
- Reusable widgets

**TASK-013:** BattleRoyaleInspector adapter (2 SP)
- Implement all 4 interfaces
- Project-specific rendering

**TASK-014:** Integration (2 SP)
- Update Program.cs
- Panel layout configuration

**Total:** 7 SP (reduced from 13 due to reusable design!)

---

**This creates a valuable, reusable diagnostics framework!** 🛠️🎨🚀
