# BATCH-10: Transient Components & Snapshot Filtering

**Status:** Ready to Execute  
**Priority:** HIGH  
**Estimated Effort:** 5-7 hours (includes attribute + per-snapshot overrides)  
**Dependencies:** None (core FDP feature)  
**Batch Type:** Core Feature Implementation  

---

## Objective

Implement **Transient Components** - a mechanism to mark certain components as non-snapshotable, excluding them from all snapshot operations (GDB, SoD, Flight Recorder). This enables safe use of mutable managed components and reduces snapshot overhead by excluding heavy caches and debug data.

---

## Background

### Problem Statement

Currently, **ALL** registered components are copied to snapshots by default. This creates two critical issues:

1. **Thread Safety Violation:** Mutable managed components (e.g., `Dictionary<>`, `List<>`) are shallow-copied to snapshots. Background modules accessing these shared references can cause race conditions.

2. **Performance Overhead:** Large, temporary components (e.g., UI render caches, debug visualization data) are unnecessarily copied to snapshots, wasting memory and CPU.

### Current State

**ComponentTypeRegistry** (`FDP/Fdp.Kernel/ComponentType.cs`):
- ✅ Tracks all registered component types
- ✅ Assigns unique IDs
- ❌ **No snapshotable flag tracking**

**EntityRepository.SyncFrom():**
- ✅ Copies components based on BitMask256
- ❌ **No default "snapshotable-only" mask**

**Snapshot Providers:**
- ✅ DoubleBufferProvider, OnDemandProvider use SyncFrom
- ❌ **No automatic exclusion of transient components**

### Design Goals

From `DESIGN-IMPLEMENTATION-PLAN.md` section 1.3:

> **Safety Rule:** Mutable managed components must **NEVER** be accessed by background modules (World B/C) because shallow copy is not thread-safe.
>
> **Solution:** Components can be marked as "transient" to exclude them from all snapshots.

---

## Scope

### In Scope:

1. **[TransientComponent] Attribute** ✨
   - Create `[TransientComponent]` attribute
   - Auto-detect during component registration
   - Cleaner API: mark component type instead of registration parameter

2. **ComponentTypeRegistry Enhancement**
   - Add `IsSnapshotable` flag tracking per component type
   - Add `SetSnapshotable(int typeId, bool snapshotable)` method
   - Add `IsSnapshotable(int typeId)` query method
   - Add `GetSnapshotableTypeIds()` helper to build arrays
   - **Convention-Based Detection:** ✨
     - Add `IsRecordType(Type)` helper to detect C# records
     - Records → automatically snapshotable (immutable by design)
     - Classes → MUST have `[TransientComponent]` attribute (mutable)

3. **EntityRepository Registration API**
   - Add `snapshotable` parameter to `RegisterComponent<T>(bool snapshotable = true)`
   - Add `snapshotable` parameter to `RegisterManagedComponent<T>(bool snapshotable = true)`
   - Auto-detect `[TransientComponent]` attribute and set snapshotable=false
   - Default: `snapshotable = true` (backward compatible)

4. **EntityRepository.SyncFrom() Enhancement**
   - Change signature: `SyncFrom(EntityRepository source, BitMask256? mask = null, bool? includeTransient = null, Type[]? excludeTypes = null)`
   - If `mask == null`, use `GetSnapshotableMask()` (auto-exclude transient)
   - If `mask != null`, use explicit mask (manual control)
   - **Per-Snapshot Overrides:** ✨
     - `includeTransient = true`: Force include transient components (for debug snapshots)
     - `excludeTypes`: Exclude specific types for this snapshot only (optimization)

5. **Snapshot Provider Updates**
   - **DoubleBufferProvider:** Use default mask (null) in Update()
   - **OnDemandProvider:** Respect module's component mask, intersect with snapshotable
   - **SharedSnapshotProvider:** Use default mask (null)

6. **Flight Recorder Integration**
   - RecorderSystem: Use `GetSnapshotableMask()` for keyframe/delta capture
   - Automatically exclude transient components from recordings

7. **Unit Tests**
   - `TransientComponentAttributeTests.cs`: Test attribute detection
   - `ComponentTypeRegistryTests.cs`: Test snapshotable flag tracking
   - `EntityRepositorySyncTests.cs`: Test transient component exclusion
   - `DoubleBufferProviderTests.cs`: Verify transient components not synced
   - `OnDemandProviderTests.cs`: Verify transient components excluded
   - `FlightRecorderTests.cs`: Verify transient components not recorded

8. **Documentation**
   - Update User Guide section on transient components
   - Add code examples showing both attribute and flag usage

### Out of Scope:

- Nothing! Convention-based detection makes complex field analysis unnecessary ✅

**Note:** Originally planned "automatic mutability detection via field analysis" is replaced by the simpler `record` vs `class` convention, which leverages C# compiler guarantees instead of runtime reflection.

---

## Implementation Plan

### Phase 0: TransientComponent Attribute (NEW! ✨)

**File:** `FDP/Fdp.Kernel/TransientComponentAttribute.cs` (NEW)

**Create:**

```csharp
using System;

namespace Fdp.Kernel
{
    /// <summary>
    /// Marks a component type as transient (non-snapshotable).
    /// Transient components are excluded from all snapshot operations (GDB, SoD, Flight Recorder).
    /// 
    /// <para><b>Use Cases:</b></para>
    /// <list type="bullet">
    ///   <item>Mutable managed components (Dictionary, List) that are main-thread only</item>
    ///   <item>Heavy caches (UI render caches, texture caches) that don't need snapshots</item>
    ///   <item>Debug/editor-only data that shouldn't be in recordings</item>
    ///   <item>Temporary calculation buffers</item>
    /// </list>
    /// 
    /// <para><b>Thread Safety:</b></para>
    /// Transient components are ONLY accessible on the main thread (World A).
    /// Background modules (World B/C) will never see transient components.
    /// 
    /// <para><b>Example:</b></para>
    /// <code>
    /// [TransientComponent]
    /// public class UIRenderCache
    /// {
    ///     public Dictionary&lt;int, Texture&gt; Cache; // Safe: main-thread only
    /// }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class TransientComponentAttribute : Attribute
    {
    }
}
```

---

### Phase 1: ComponentTypeRegistry Enhancement

**File:** `FDP/Fdp.Kernel/ComponentType.cs`

**Changes:**

```csharp
public static class ComponentTypeRegistry
{
    private static readonly object _lock = new object();
    private static readonly Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();
    private static readonly List<Type> _idToType = new List<Type>();
    private static readonly List<bool> _isSnapshotable = new List<bool>(); // NEW
    private static int _nextId = 0;
    
    /// <summary>
    /// Sets whether a component type should be included in snapshots.
    /// Must be called AFTER registration.
    /// </summary>
    public static void SetSnapshotable(int typeId, bool snapshotable)
    {
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _isSnapshotable.Count)
                throw new ArgumentOutOfRangeException(nameof(typeId));
            
            _isSnapshotable[typeId] = snapshotable;
        }
    }
    
    /// <summary>
    /// Checks if a component type is snapshotable.
    /// Returns true by default for registered types.
    /// </summary>
    public static bool IsSnapshotable(int typeId)
    {
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _isSnapshotable.Count)
                return false;
            
            return _isSnapshotable[typeId];
        }
    }
    
    /// <summary>
    /// Gets all component type IDs that are snapshotable.
    /// </summary>
    public static int[] GetSnapshotableTypeIds()
    {
        lock (_lock)
        {
            var result = new List<int>();
            for (int i = 0; i < _isSnapshotable.Count; i++)
            {
                if (_isSnapshotable[i])
                    result.Add(i);
            }
            return result.ToArray();
        }
    }
    
    /// <summary>
    /// Checks if a type is a C# record (immutable by design).
    /// Records have compiler-generated EqualityContract property.
    /// </summary>
    public static bool IsRecordType(Type type)
    {
        // C# records (both record class and record struct) have EqualityContract
        return type.GetProperty("EqualityContract", 
            BindingFlags.Instance | BindingFlags.NonPublic) != null;
    }
    
    // Update GetOrRegisterManaged to initialize snapshotable flag:
    internal static int GetOrRegisterManaged(Type type)
    {
        lock (_lock)
        {
            if (_typeToId.TryGetValue(type, out int existingId))
            {
                return existingId;
            }
            
            if (_nextId >= FdpConfig.MAX_COMPONENT_TYPES)
            {
                throw new InvalidOperationException(
                    $"Maximum component types ({FdpConfig.MAX_COMPONENT_TYPES}) exceeded");
            }
            
            int id = _nextId++;
            _typeToId[type] = id;
            _idToType.Add(type);
            _isSnapshotable.Add(true); // Default: snapshotable
            
            return id;
        }
    }
    
    // Update Clear() for testing:
    public static void Clear()
    {
        lock (_lock)
        {
            _typeToId.Clear();
            _idToType.Clear();
            _isSnapshotable.Clear(); // Clear snapshotable flags
            _nextId = 0;
        }
    }
}
```

---

### Phase 2: EntityRepository Registration API (with Convention-Based Detection ✨)

**File:** `FDP/Fdp.Kernel/EntityRepository.cs`

**Find and Update:**

```csharp
// BEFORE:
public void RegisterComponent<T>() where T : unmanaged
{
    var typeId = ComponentType<T>.ID; // Triggers registration
    var table = new ComponentTable<T>();
    _tables[typeId] = table;
}

// AFTER:
/// <summary>
/// Register an unmanaged component type.
/// Unmanaged components (structs) are always snapshotable (full value copy).
/// </summary>
/// <param name="snapshotable">Optional explicit override. 
/// If false, excludes from snapshots (rare for structs).</param>
public void RegisterComponent<T>(bool? snapshotable = null) where T : unmanaged
{
    var typeId = ComponentType<T>.ID; // Triggers registration
    
    // Structs are safe by default (value types = full deep copy)
    // But allow explicit override if needed
    bool isSnapshotable = snapshotable ?? true;
    ComponentTypeRegistry.SetSnapshotable(typeId, isSnapshotable);
    
    var table = new ComponentTable<T>();
    _tables[typeId] = table;
}

// BEFORE:
public void RegisterManagedComponent<T>() where T : class
{
    var typeId = ManagedComponentType<T>.ID;
    var table = new ManagedComponentTable<T>();
    _managedTables[typeId] = table;
}

// AFTER:
/// <summary>
/// Register a managed component type with convention-based safety.
/// 
/// <para><b>Convention:</b></para>
/// <list type="bullet">
///   <item><c>record</c> types → Immutable → Automatically snapshotable ✅</item>
///   <item><c>class</c> types → Mutable → Must have [TransientComponent] ❌</item>
/// </list>
/// 
/// <para><b>Examples:</b></para>
/// <code>
/// // Immutable data (auto-snapshotable)
/// public record PlayerStats(int Health, int Score);
/// repo.RegisterManagedComponent&lt;PlayerStats&gt;();  // ✅ OK
/// 
/// // Mutable state (must be marked transient)
/// [TransientComponent]
/// public class UICache { public Dictionary&lt;&gt; Data; }
/// repo.RegisterManagedComponent&lt;UICache&gt;();  // ✅ OK
/// 
/// // ERROR: Class without attribute
/// public class GameState { }
/// repo.RegisterManagedComponent&lt;GameState&gt;();  // ❌ THROWS
/// </code>
/// </summary>
/// <param name="snapshotable">Optional explicit override.
/// If specified, bypasses convention-based detection.</param>
public void RegisterManagedComponent<T>(bool? snapshotable = null) where T : class
{
    var typeId = ManagedComponentType<T>.ID;
    
    // Priority 1: Explicit parameter (highest priority)
    if (snapshotable.HasValue)
    {
        ComponentTypeRegistry.SetSnapshotable(typeId, snapshotable.Value);
    }
    else
    {
        // Priority 2: Attribute check
        bool hasTransientAttr = typeof(T).IsDefined(typeof(TransientComponentAttribute), false);
        
        if (hasTransientAttr)
        {
            // Explicitly marked transient
            ComponentTypeRegistry.SetSnapshotable(typeId, false);
        }
        else
        {
            // Priority 3: Convention-based detection
            bool isRecord = ComponentTypeRegistry.IsRecordType(typeof(T));
            
            if (isRecord)
            {
                // Record → immutable by design → snapshotable
                ComponentTypeRegistry.SetSnapshotable(typeId, true);
            }
            else
            {
                // Class without [TransientComponent] → ERROR
                throw new InvalidOperationException(
                    $"Component class '{typeof(T).Name}' must be marked with [TransientComponent] attribute.\n" +
                    $"Classes are inherently mutable and unsafe for background threads (shallow copy).\n\n" +
                    $"Solutions:\n" +
                    $"  1. Add [TransientComponent] attribute if this is main-thread-only mutable state:\n" +
                    $"       [TransientComponent]\n" +
                    $"       public class {typeof(T).Name} {{ ... }}\n\n" +
                    $"  2. Convert to 'record' if this is immutable data:\n" +
                    $"       public record {typeof(T).Name}(...);\n\n" +
                    $"  3. Pass 'snapshotable: false' explicitly during registration:\n" +
                    $"       RegisterManagedComponent<{typeof(T).Name}>(snapshotable: false);");
            }
        }
    }
    
    var table = new ManagedComponentTable<T>();
    _managedTables[typeId] = table;
}
```

**Key Points:**
- **Structs (`RegisterComponent`)**: Always snapshotable by default (value copy)
- **Records**: Auto-detected as snapshotable (compiler-enforced immutability)
- **Classes**: MUST have `[TransientComponent]` attribute (safety guard)
- **Explicit parameter**: Overrides all detection (escape hatch)
- **Helpful error**: Provides 3 solutions when class lacks attribute

---

### Phase 3: EntityRepository.SyncFrom() Enhancement (with Per-Snapshot Overrides ✨)

**File:** `FDP/Fdp.Kernel/EntityRepository.cs`

**Find and Update:**

```csharp
// BEFORE:
public void SyncFrom(EntityRepository source, BitMask256 mask)
{
    // Sync logic using explicit mask
}

// AFTER:
/// <summary>
/// Synchronize snapshot from source repository.
/// </summary>
/// <param name="source">Source repository to copy from</param>
/// <param name="mask">Component mask to sync. If null, syncs only snapshotable components.</param>
/// <param name="includeTransient">If true, includes transient components even if mask excludes them. 
/// Useful for debug snapshots. Ignored if explicit mask provided.</param>
/// <param name="excludeTypes">Types to exclude from this snapshot, even if normally snapshotable. 
/// Useful for optimization (e.g., network sync). Ignored if explicit mask provided.</param>
public void SyncFrom(
    EntityRepository source, 
    BitMask256? mask = null, 
    bool? includeTransient = null,
    Type[]? excludeTypes = null)
{
    // Build effective mask
    BitMask256 effectiveMask;
    
    if (mask.HasValue)
    {
        // Explicit mask provided - use it directly (ignore includeTransient/excludeTypes)
        effectiveMask = mask.Value;
    }
    else
    {
        // Build mask based on snapshotable components
        effectiveMask = GetSnapshotableMask(includeTransient: includeTransient ?? false);
        
        // Apply per-snapshot exclusions
        if (excludeTypes != null && excludeTypes.Length > 0)
        {
            foreach (var type in excludeTypes)
            {
                var typeId = ComponentTypeRegistry.GetId(type);
                if (typeId >= 0)
                {
                    effectiveMask.ClearBit(typeId);
                }
            }
        }
    }
    
    // Existing sync logic using effectiveMask...
    // (Replace all uses of 'mask' with 'effectiveMask')
}

/// <summary>
/// Builds a component mask containing only snapshotable component types.
/// Used as default mask for SyncFrom when no explicit mask provided.
/// </summary>
/// <param name="includeTransient">If true, includes transient components in the mask</param>
private BitMask256 GetSnapshotableMask(bool includeTransient = false)
{
    var mask = new BitMask256();
    var allTypeIds = ComponentTypeRegistry.GetSnapshotableTypeIds();
    
    foreach (var typeId in allTypeIds)
    {
        // Include if snapshotable OR if includeTransient=true
        bool shouldInclude = ComponentTypeRegistry.IsSnapshotable(typeId) || includeTransient;
        
        if (shouldInclude)
        {
            mask.SetBit(typeId);
        }
    }
    
    return mask;
}
```

**Key Points:**
- `mask != null`: Explicit mask overrides everything (backward compatible)
- `includeTransient = true`: Force include transient components (debug mode)
- `excludeTypes`: Exclude specific types for optimization
- Both `includeTransient` and `excludeTypes` are ignored if explicit mask provided

---

### Phase 4: Snapshot Provider Updates

**File:** `ModuleHost.Core/Providers/DoubleBufferProvider.cs`

**Update:**

```csharp
// BEFORE:
public void Update()
{
    // CRITICAL: SyncFrom diffs
    var currentVersion = _source.GlobalVersion;
    var mask = _componentMask; // Explicit mask
    _replica.SyncFrom(_source, mask);
    _lastSyncVersion = currentVersion;
}

// AFTER:
public void Update()
{
    // CRITICAL: SyncFrom diffs
    var currentVersion = _source.GlobalVersion;
    
    // If no explicit mask, use default (snapshotable only)
    // If explicit mask provided, intersect with snapshotable
    _replica.SyncFrom(_source, _componentMask); // null OK now!
    _lastSyncVersion = currentVersion;
}
```

**File:** `ModuleHost.Core/Providers/OnDemandProvider.cs`

**Update:**

```csharp
// In AcquireView():
public ISimulationView AcquireView()
{
    var view = _pool.Acquire();
    
    // Sync with module's component mask
    // (SyncFrom will automatically intersect with snapshotable)
    view.SyncFrom(_liveWorld, _componentMask); // Respects snapshotable
    
    return view;
}
```

---

### Phase 5: Flight Recorder Integration

**File:** `FDP/Fdp.Kernel/FlightRecorder/RecorderSystem.cs`

**Update:**

```csharp
// In CaptureKeyframe():
private void CaptureKeyframe(EntityRepository repository)
{
    // Create snapshot with only snapshotable components
    var snapshot = _snapshotPool.Acquire();
    snapshot.SyncFrom(repository, mask: null); // Auto-excludes transient
    
    // Serialize snapshot to file...
}

// In CaptureDelta():
private void CaptureDelta(EntityRepository repository, uint sinceTick)
{
    // Capture changed components (snapshotable only)
    var mask = repository.GetSnapshotableMask(); // Explicit for delta
    
    // Delta logic with mask...
}
```

---

## Test Requirements

### Unit Tests to Create/Update

**File:** `FDP/Fdp.Tests/TransientComponentAttributeTests.cs` (NEW)

```csharp
using Fdp.Kernel;
using Xunit;

public class TransientComponentAttributeTests
{
    // Test components
    [TransientComponent]
    public struct TransientStruct
    {
        public int Value;
    }
    
    public struct NormalStruct
    {
        public int Value;
    }
    
    [TransientComponent]
    public class TransientManaged
    {
        public string Data;
    }
    
    public class NormalManaged
    {
        public string Data;
    }
    
    [Fact]
    public void RegisterComponent_WithTransientAttribute_AutoDetected()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Register transient component (no snapshotable parameter)
        repo.RegisterComponent<TransientStruct>();
        
        var typeId = ComponentType<TransientStruct>.ID;
        
        // Should be non-snapshotable (attribute detected)
        Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterComponent_WithoutAttribute_DefaultSnapshotable()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Register normal component (no snapshotable parameter)
        repo.RegisterComponent<NormalStruct>();
        
        var typeId = ComponentType<NormalStruct>.ID;
        
        // Should be snapshotable (default)
        Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterComponent_ExplicitOverridesAttribute()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Register transient component with explicit snapshotable=true
        repo.RegisterComponent<TransientStruct>(snapshotable: true);
        
        var typeId = ComponentType<TransientStruct>.ID;
        
        // Explicit value overrides attribute
        Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_WithTransientAttribute_AutoDetected()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Register transient managed component
        repo.RegisterManagedComponent<TransientManaged>();
        
        var typeId = ManagedComponentType<TransientManaged>.ID;
        
        // Should be non-snapshotable
        Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_ExplicitFalse_MarksTransient()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Register normal managed component as explicit transient
        repo.RegisterManagedComponent<NormalManaged>(snapshotable: false);
        
        var typeId = ManagedComponentType<NormalManaged>.ID;
        
        // Should be non-snapshotable (explicit override)
        Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_Record_AutoSnapshotable()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Define a record type
        // Note: In actual test, use a real record defined in test file:
        // public record TestPlayerStats(int Health, int Score);
        
        repo.RegisterManagedComponent<TestPlayerStats>();
        
        var typeId = ManagedComponentType<TestPlayerStats>.ID;
        
        // Record should be auto-detected as snapshotable
        Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_ClassWithoutAttribute_Throws()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Define a class without [TransientComponent]
        // public class TestGameState { public int Value; }
        
        // Should throw with helpful error message
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            repo.RegisterManagedComponent<TestGameState>();
        });
        
        // Error message should mention all 3 solutions
        Assert.Contains("[TransientComponent]", ex.Message);
        Assert.Contains("Convert to 'record'", ex.Message);
        Assert.Contains("snapshotable: false", ex.Message);
    }
    
    [Fact]
    public void RegisterManagedComponent_ClassWithAttribute_MarksTransient()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Define a class WITH [TransientComponent]
        // [TransientComponent]
        // public class TestUICache { }
        
        repo.RegisterManagedComponent<TestUICache>();
        
        var typeId = ManagedComponentType<TestUICache>.ID;
        
        // Should be non-snapshotable (marked transient)
        Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_RecordWithAttribute_AttributeWins()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Record marked with [TransientComponent] (override)
        // [TransientComponent]
        // public record TestDebugData(string Message);
        
        repo.RegisterManagedComponent<TestDebugData>();
        
        var typeId = ManagedComponentType<TestDebugData>.ID;
        
        // Attribute overrides convention: should be non-snapshotable
        Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
    
    [Fact]
    public void RegisterManagedComponent_ExplicitParameter_OverridesAll()
    {
        ComponentTypeRegistry.Clear();
        var repo = new EntityRepository();
        
        // Class with attribute, but explicit parameter overrides
        repo.RegisterManagedComponent<TestUICache>(snapshotable: true);
        
        var typeId = ManagedComponentType<TestUICache>.ID;
        
        // Explicit parameter wins: should be snapshotable
        Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
    }
}
```

**Test Component Definitions (add to test file):**

```csharp
// Records for testing
public record TestPlayerStats(int Health, int Score);

[TransientComponent]
public record TestDebugData(string Message);

// Classes for testing
public class TestGameState  // No attribute (should error)
{
    public int Value;
}

[TransientComponent]
public class TestUICache  // With attribute (OK)
{
    public Dictionary<int, string> Cache = new();
}
```

**File:** `FDP/Fdp.Tests/ComponentTypeRegistryTests.cs` (NEW)

```csharp
[Fact]
public void SetSnapshotable_ValidTypeId_SetsFlag()
{
    ComponentTypeRegistry.Clear();
    var typeId = ComponentType<Position>.ID;
    
    // Default: snapshotable
    Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
    
    // Set to non-snapshotable
    ComponentTypeRegistry.SetSnapshotable(typeId, false);
    Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
    
    // Set back to snapshotable
    ComponentTypeRegistry.SetSnapshotable(typeId, true);
    Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
}

[Fact]
public void GetSnapshotableTypeIds_MixedFlags_ReturnsOnlySnapshotable()
{
    ComponentTypeRegistry.Clear();
    
    var id1 = ComponentType<Position>.ID;
    var id2 = ComponentType<Velocity>.ID;
    var id3 = ComponentType<Health>.ID;
    
    ComponentTypeRegistry.SetSnapshotable(id2, false); // Velocity: transient
    
    var snapshotableIds = ComponentTypeRegistry.GetSnapshotableTypeIds();
    
    Assert.Contains(id1, snapshotableIds); // Position
    Assert.DoesNotContain(id2, snapshotableIds); // Velocity (transient)
    Assert.Contains(id3, snapshotableIds); // Health
}
```

**File:** `FDP/Fdp.Tests/EntityRepositorySyncTests.cs` (UPDATE)

```csharp
[Fact]
public void SyncFrom_TransientComponent_NotCopied()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>(snapshotable: false); // Transient!
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>(snapshotable: false);
    
    // SyncFrom without mask (uses snapshotable only)
    snapshot.SyncFrom(source, mask: null);
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // Position copied (snapshotable)
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity));
    ref readonly var pos = ref snapshot.GetComponentRO<Position>(snapshotEntity);
    Assert.Equal(10f, pos.X);
    
    // Velocity NOT copied (transient)
    Assert.False(snapshot.HasComponent<Velocity>(snapshotEntity));
}

[Fact]
public void SyncFrom_ExplicitMask_OverridesSnapshotable()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>(snapshotable: false);
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>(snapshotable: false);
    
    // Explicit mask includes Velocity (override snapshotable)
    var mask = new BitMask256();
    mask.SetBit(ComponentType<Position>.ID);
    mask.SetBit(ComponentType<Velocity>.ID); // Force include transient
    
    snapshot.SyncFrom(source, mask);
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // Both copied (explicit mask overrides)
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity));
    Assert.True(snapshot.HasComponent<Velocity>(snapshotEntity));
}

[Fact]
public void SyncFrom_IncludeTransient_CopiesTransientComponents()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>(snapshotable: false); // Transient
    source.RegisterComponent<Health>();
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    source.AddComponent(entity, new Health { Value = 100 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>(snapshotable: false);
    snapshot.RegisterComponent<Health>();
    
    // SyncFrom with includeTransient=true (debug snapshot)
    snapshot.SyncFrom(source, includeTransient: true);
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // All components copied (includeTransient overrides)
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity));
    Assert.True(snapshot.HasComponent<Velocity>(snapshotEntity)); // Transient included! ✅
    Assert.True(snapshot.HasComponent<Health>(snapshotEntity));
}

[Fact]
public void SyncFrom_ExcludeTypes_ExcludesSpecificComponents()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>();
    source.RegisterComponent<Health>();
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    source.AddComponent(entity, new Health { Value = 100 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>();
    snapshot.RegisterComponent<Health>();
    
    // SyncFrom excluding Velocity (e.g., network optimization)
    snapshot.SyncFrom(source, excludeTypes: new[] { typeof(Velocity) });
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // Position and Health copied, Velocity excluded
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity));
    Assert.False(snapshot.HasComponent<Velocity>(snapshotEntity)); // Excluded! ✅
    Assert.True(snapshot.HasComponent<Health>(snapshotEntity));
}

[Fact]
public void SyncFrom_ExcludeTypes_MultipleExclusions()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>();
    source.RegisterComponent<Health>();
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    source.AddComponent(entity, new Health { Value = 100 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>();
    snapshot.RegisterComponent<Health>();
    
    // Exclude multiple types
    snapshot.SyncFrom(source, excludeTypes: new[] { typeof(Velocity), typeof(Health) });
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // Only Position copied
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity));
    Assert.False(snapshot.HasComponent<Velocity>(snapshotEntity));
    Assert.False(snapshot.HasComponent<Health>(snapshotEntity));
}

[Fact]
public void SyncFrom_ExplicitMask_IgnoresIncludeTransientAndExcludeTypes()
{
    var source = new EntityRepository();
    source.RegisterComponent<Position>();
    source.RegisterComponent<Velocity>(snapshotable: false);
    
    var entity = source.CreateEntity();
    source.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    source.AddComponent(entity, new Velocity { X = 1, Y = 2, Z = 3 });
    
    var snapshot = new EntityRepository();
    snapshot.RegisterComponent<Position>();
    snapshot.RegisterComponent<Velocity>(snapshotable: false);
    
    // Explicit mask (only Position)
    var mask = new BitMask256();
    mask.SetBit(ComponentType<Position>.ID);
    
    // Try to override with includeTransient (should be ignored)
    snapshot.SyncFrom(source, mask, includeTransient: true, excludeTypes: new[] { typeof(Position) });
    
    var query = snapshot.Query().Build();
    var snapshotEntity = query.FirstOrDefault();
    
    // Explicit mask wins: Position copied, Velocity not copied
    Assert.True(snapshot.HasComponent<Position>(snapshotEntity)); // Not excluded (mask overrides)
    Assert.False(snapshot.HasComponent<Velocity>(snapshotEntity)); // Not included (mask overrides)
}
```

**File:** `ModuleHost.Core.Tests/DoubleBufferProviderTests.cs` (UPDATE)

```csharp
[Fact]
public void Update_TransientComponent_NotSyncedToReplica()
{
    var liveWorld = new EntityRepository();
    liveWorld.RegisterComponent<Position>();
    liveWorld.RegisterComponent<DebugVisualization>(snapshotable: false);
    
    var entity = liveWorld.CreateEntity();
    liveWorld.AddComponent(entity, new Position { X = 100 });
    liveWorld.AddComponent(entity, new DebugVisualization { Color = "Red" });
    
    var provider = new DoubleBufferProvider(liveWorld);
    
    // Sync (should exclude transient)
    liveWorld.Tick();
    provider.Update();
    
    var view = provider.AcquireView();
    var query = view.Query().Build();
    var replicaEntity = query.FirstOrDefault();
    
    // Position synced
    Assert.True(view.HasComponent<Position>(replicaEntity));
    
    // DebugVisualization NOT synced (transient)
    Assert.False(view.HasManagedComponent<DebugVisualization>(replicaEntity));
}
```

**File:** `FDP/Fdp.Tests/FlightRecorderTests.cs` (UPDATE)

```csharp
[Fact]
public void CaptureKeyframe_TransientComponent_NotRecorded()
{
    string filePath = "test_transient.fdp";
    
    var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    recordRepo.RegisterComponent<DebugInfo>(snapshotable: false); // Transient
    
    var entity = recordRepo.CreateEntity();
    recordRepo.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    recordRepo.AddComponent(entity, new DebugInfo { Message = "Secret" });
    
    // Record
    using (var recorder = new AsyncRecorder(filePath))
    {
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
    }
    
    // Replay
    var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    replayRepo.RegisterComponent<DebugInfo>(snapshotable: false);
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo);
    }
    
    var query = replayRepo.Query().Build();
    var replayEntity = query.FirstOrDefault();
    
    // Position recorded
    Assert.True(replayRepo.HasComponent<Position>(replayEntity));
    
    // DebugInfo NOT recorded (transient)
    Assert.False(replayRepo.HasComponent<DebugInfo>(replayEntity));
}
```

---

## Acceptance Criteria

### Functional Requirements:

- [ ] `[TransientComponent]` attribute detection works
- [ ] ComponentTypeRegistry tracks `IsSnapshotable` flag per component type
- [ ] `RegisterComponent<T>(bool? snapshotable = null)` API works with auto-detection
- [ ] `RegisterManagedComponent<T>(bool? snapshotable = null)` API works with auto-detection
- [ ] `EntityRepository.SyncFrom(source, mask: null)` excludes transient components
- [ ] `EntityRepository.SyncFrom(source, explicitMask)` respects explicit mask
- [ ] `EntityRepository.SyncFrom(source, includeTransient: true)` includes transient components ✨
- [ ] `EntityRepository.SyncFrom(source, excludeTypes: [...])` excludes specified types ✨
- [ ] Explicit mask overrides `includeTransient` and `excludeTypes` parameters
- [ ] DoubleBufferProvider excludes transient components in Update()
- [ ] OnDemandProvider excludes transient components in AcquireView()
- [ ] Flight Recorder excludes transient components from recordings

### Test Requirements:

- [ ] All new tests pass (15+ new tests including per-snapshot override tests)
- [ ] No existing tests broken
- [ ] Code coverage >= 80% for new code

### Code Quality:

- [ ] XML documentation on all public APIs
- [ ] Thread-safety verified (ComponentTypeRegistry uses locks)
- [ ] Backward compatible (default `snapshotable = true`)

---

## Usage Examples

### Example 1: Immutable Data vs Mutable State (Convention-Based ✨)

```csharp
using Fdp.Kernel;

// ============================================================
// IMMUTABLE DATA: Use 'record' (Clean, Recommended!)
// ============================================================

// Component definition (record = immutable)
public record PlayerStats(
    int Health,
    int MaxHealth,
    int Score,
    string PlayerName
);

// Registration (auto-detected as snapshotable)
repository.RegisterManagedComponent<PlayerStats>();  // ✅ No attribute needed!

// ============================================================
// MUTABLE STATE: Use 'class' + [TransientComponent]
// ============================================================

// Component definition (class = mutable, needs attribute)
[TransientComponent]
public class UIRenderCache
{
    public Dictionary<int, Texture> TextureCache = new();
    public List<Mesh> MeshCache = new();
    public Material CurrentMaterial;
}

// Registration (marked as transient)
repository.RegisterManagedComponent<UIRenderCache>();  // ✅ Attribute detected

// Usage in snapshots:
var snapshot = new EntityRepository();
snapshot.RegisterManagedComponent<PlayerStats>();  // Auto-snapshotable
snapshot.RegisterManagedComponent<UIRenderCache>();  // Auto-transient

snapshot.SyncFrom(liveWorld);
// PlayerStats ✅ Copied (record = immutable)
// UIRenderCache ❌ NOT copied (class with attribute = transient)
```

**Why This Works:**
- `record` → Compiler enforces immutability → Safe to snapshot
- `class` + `[TransientComponent]` → Explicit mutable → Main-thread only
- Convention makes intent crystal clear!

---

**❌ Common Mistake (Will Error):**

```csharp
// Forgot to mark class as transient
public class GameState  // ← Missing [TransientComponent]
{
    public Dictionary<Entity, Data> StateMap;
}

repository.RegisterManagedComponent<GameState>();
// ❌ THROWS InvalidOperationException:
// "Component class 'GameState' must be marked with [TransientComponent] attribute.
//  Solutions:
//    1. Add [TransientComponent] attribute...
//    2. Convert to 'record'...
//    3. Pass 'snapshotable: false' explicitly..."
```

---

### Example 2: Temporary Calculation Buffer (Attribute Approach ✨)

```csharp
using Fdp.Kernel;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
[TransientComponent]  // Mark as transient
public struct TempCalculationBuffer // 1KB of temp data
{
    public unsafe fixed float Data[256];
}

// Registration (auto-detects attribute)
repository.RegisterComponent<TempCalculationBuffer>();

// Snapshots exclude this large buffer automatically ✅
```

**Alternative: Flag Approach**

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct TempCalculationBuffer // 1KB of temp data
{
    public unsafe fixed float Data[256];
}

// Mark as transient (don't waste snapshot memory)
repository.RegisterComponent<TempCalculationBuffer>(snapshotable: false);
```

---

### Example 3: Flight Recorder Excludes Transient (Attribute Approach ✨)

```csharp
using Fdp.Kernel;

// Components
public struct Position  // Normal component (snapshotable by default)
{
    public float X, Y, Z;
}

[TransientComponent]  // Debug-only, don't record
public struct DebugVisualization
{
    public int Color;
    public int LineCount;
}

// Registration (auto-detects attributes)
repository.RegisterComponent<Position>();
repository.RegisterComponent<DebugVisualization>();

using (var recorder = new AsyncRecorder("recording.fdp"))
{
    repository.Tick();
    recorder.CaptureKeyframe(repository);
}

// File contains Position, NOT DebugVisualization
// Smaller file, faster recording, no leaked debug data ✅
```

---

### Example 4: Per-Snapshot Overrides (Debug & Optimization) ✨

```csharp
using Fdp.Kernel;

// Components
public struct Position { public float X, Y, Z; }
public struct Velocity { public float X, Y, Z; }

[TransientComponent]
public struct DebugInfo { public string Message; }

public struct HeavyPhysicsCache 
{ 
    public unsafe fixed float KdTree[10000]; // Large buffer
}

// Registration
repository.RegisterComponent<Position>();
repository.RegisterComponent<Velocity>();
repository.RegisterComponent<DebugInfo>();  // Transient
repository.RegisterComponent<HeavyPhysicsCache>();  // Snapshotable but large

// USE CASE 1: Normal Snapshot (exclude transient)
var normalSnapshot = new EntityRepository();
normalSnapshot.RegisterComponent<Position>();
normalSnapshot.RegisterComponent<Velocity>();
normalSnapshot.RegisterComponent<DebugInfo>();
normalSnapshot.RegisterComponent<HeavyPhysicsCache>();

normalSnapshot.SyncFrom(liveWorld);  // Default: excludes DebugInfo ✅

// USE CASE 2: Debug Snapshot (include transient for bug reproduction)
var debugSnapshot = new EntityRepository();
debugSnapshot.RegisterComponent<Position>();
debugSnapshot.RegisterComponent<Velocity>();
debugSnapshot.RegisterComponent<DebugInfo>();
debugSnapshot.RegisterComponent<HeavyPhysicsCache>();

debugSnapshot.SyncFrom(liveWorld, includeTransient: true);  // Includes DebugInfo! ✅

// USE CASE 3: Network Snapshot (exclude heavy data to save bandwidth)
var networkSnapshot = new EntityRepository();
networkSnapshot.RegisterComponent<Position>();
networkSnapshot.RegisterComponent<Velocity>();
networkSnapshot.RegisterComponent<DebugInfo>();
networkSnapshot.RegisterComponent<HeavyPhysicsCache>();

networkSnapshot.SyncFrom(liveWorld, excludeTypes: new[] 
{ 
    typeof(HeavyPhysicsCache),  // Exclude large buffer
    typeof(DebugInfo)           // Also exclude debug (redundant, but explicit)
});
// Minimal snapshot for network sync! ✅

// USE CASE 4: Combined - Debug snapshot without heavy physics
var debugNoPhysics = new EntityRepository();
debugNoPhysics.RegisterComponent<Position>();
debugNoPhysics.RegisterComponent<Velocity>();
debugNoPhysics.RegisterComponent<DebugInfo>();
debugNoPhysics.RegisterComponent<HeavyPhysicsCache>();

debugNoPhysics.SyncFrom(liveWorld, 
    includeTransient: true,  // Include DebugInfo
    excludeTypes: new[] { typeof(HeavyPhysicsCache) });  // But exclude heavy cache
// Debug data without performance hit! ✅
```

---

## Expected Outcomes

### Performance Improvements:

- **Snapshot Size:** 10-30% reduction (typical, depends on transient component size)
- **Snapshot Speed:** 5-15% faster SyncFrom (fewer components to copy)
- **Flight Recorder:** Smaller files, faster recording

### Safety Improvements:

- **Thread Safety:** Mutable managed components can be main-thread-only
- **Data Leak Prevention:** Sensitive debug data not in recordings

### API Improvements:

- **Explicit Contract:** `snapshotable: false` documents thread-safety intent
- **Flexibility:** Can override with explicit mask if needed

---

## Risks & Mitigation

### Risk: Breaking Change for Existing Code

**Mitigation:** Default `snapshotable = true` ensures backward compatibility.

### Risk: Forgetting to Mark Transient on Replica

**Problem:**
```csharp
// Source
source.RegisterManagedComponent<UICache>(snapshotable: false);

// Replica (WRONG - forgot flag)
replica.RegisterManagedComponent<UICache>(); // snapshotable = true (default)

// SyncFrom will skip UICache (source says non-snapshotable)
// But replica expects it → types mismatch!
```

**Mitigation:** Document requirement to match snapshotable flags across repositories.

### Risk: Performance Regression from Extra Lookup

**Concern:** `GetSnapshotableMask()` builds mask every call.

**Mitigation:**
- Cache mask in EntityRepository if performance-critical
- Measure first, optimize later

---

## Related Batches

- **BATCH-03:** Snapshot on Demand (uses SyncFrom)
- **BATCH-04:** Generalized Double Buffer (uses SyncFrom)
- **BATCH-FDP-01:** Component operation optimizations (related to ComponentTypeRegistry)

---

## Notes

- **Attribute Support:** ✅ Included! `[TransientComponent]` attribute for explicit marking
- **Convention-Based Safety:** ✅ Included! `record` = immutable, `class` = must have attribute ✨
- **Per-Snapshot Overrides:** ✅ Included! `includeTransient` and `excludeTypes` parameters
- **Compiler Enforcement:** Records provide compile-time immutability guarantees (no runtime analysis needed)
- **Backward Compatibility:** Existing code works (structs default snapshotable, explicit parameters still work)
- **Three-Tier Safety System:**
  1. **Compiler** (Records): Enforces immutability at compile-time
  2. **Attribute** (Classes): Requires explicit developer intent
  3. **Runtime** (Error on missing attribute): Prevents accidental misuse
- **Control Levels:**
  1. `record` types → Auto-snapshotable (cleanest, recommended for data)
  2. `class` + `[TransientComponent]` → Explicit transient (for mutable state)
  3. `snapshotable: true/false` parameter → Runtime override (rare cases)
- **Why No Complex Field Analysis:** The `record` vs `class` convention leverages C# compiler guarantees, making complex runtime reflection unnecessary and error-prone field analysis obsolete

---

**Status:** Ready to Execute  
**Created:** 2026-01-09  
**Target Completion:** Single session (5-7 hours)  
**Assignee:** Developer / AI Assistant
