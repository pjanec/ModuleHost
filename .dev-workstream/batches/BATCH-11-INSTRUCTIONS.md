# BATCH-11: Component Data Policy and Mutable Class Cloning

**Batch ID:** BATCH-11  
**Phase:** Core - Kernel Hardening & Component System Refinement  
**Priority:** HIGH (P1) - Fixes critical usability issue & enables safe mutable component recording  
**Estimated Effort:** 2.0 days  
**Dependencies:** None (Pure FDP Kernel)  
**Starting Point:** Current main branch  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Context & Problem Statement

### The Issue

Users encounter a runtime exception when trying to record mutable managed components (classes) with the Flight Recorder:

```
CRITICAL ERROR: System.InvalidOperationException: 
Component class 'CombatHistory' must be marked with [TransientComponent] attribute.
Classes are inherently mutable and unsafe for background threads (shallow copy).
```

**Example User Code:**
```csharp
[MessagePackObject]
public class CombatHistory  // Mutable class
{
    [Key(0)] public int TotalDamageTaken { get; set; }
    [Key(1)] public List<string> RecentEvents { get; set; } = new();
    
    public void RecordDamage(float amount, string source)
    {
        TotalDamageTaken += (int)amount;
        RecentEvents.Add($"Took {amount:F0} from {source}");
    }
}

// User wants to RECORD this for replay, but kernel prevents registration
repo.RegisterComponent<CombatHistory>(); // ❌ THROWS!
```

**If user adds `[TransientComponent]`:**
- ✅ No crash
- ❌ Flight Recorder stops recording it
- ❌ Replays don't show combat history

### Root Cause Analysis

The kernel currently conflates two orthogonal concerns using a single `IsSnapshotable` flag:

| System | Operation | Safety for Mutable Classes |
|--------|-----------|----------------------------|
| **ModuleHost** (GDB/SoD) | Shallow reference copy to background threads | ❌ UNSAFE (torn reads, race conditions) |
| **FlightRecorder** | Serialize to byte stream (MessagePack) | ✅ SAFE (deep copy via serialization) |
| **SaveGame** | Serialize to checkpoint file | ✅ SAFE (deep copy via serialization) |

**Current Behavior:**
- `IsSnapshotable=false` → Excluded from EVERYTHING (ModuleHost + FlightRecorder + SaveGame)
- `IsSnapshotable=true` → Included in EVERYTHING (crashes if mutable class)

**The Problem:** FlightRecorder is being blocked unnecessarily to protect ModuleHost.

---

## 🎯 Goal

Enable fine-grained control over component persistence and concurrency behavior:

1. **Allow mutable classes to be recorded** (FlightRecorder + SaveGame) without breaking ModuleHost
2. **Provide explicit opt-in for cloning** mutable classes for background threads (safe but slow)
4. **Clear developer intent** through expressive attribute names

### Success Criteria

✅ **Mutable classes work by default:**
```csharp
repo.RegisterComponent<CombatHistory>();  
// No crash, auto-defaults to: Recordable + Saveable + NoSnapshot
```

✅ **FlightRecorder records it:**
```csharp
recorder.RecordFrame();  // CombatHistory is serialized
```

✅ **Background modules DON'T see it (safe):**
```csharp
// In a background AI module:
var history = view.GetComponent<CombatHistory>(e);  
// Returns null/default (filtered by snapshot mask)
```

✅ **Optional deep cloning for sharing:**
```csharp
[DataPolicy(DataPolicy.SnapshotViaClone)]
public class AIBlackboard { /* mutable state */ }
// Background modules get a deep clone (safe, slower)
```

---

## 🏗️ Architecture Solution

### Decouple Concurrency from Persistence

Replace single `IsSnapshotable` boolean with granular flags:

```csharp
[Flags]
public enum DataPolicy
{
    Default = 0,
    
    // Concurrency (ModuleHost GDB/SoD)
    NoSnapshot       = 1 << 0,  // Exclude from background threads
    SnapshotViaClone = 1 << 1,  // Include via deep clone (safe, slow)
    
    // Persistence (Disk/Network)
    NoRecord = 1 << 2,          // Exclude from FlightRecorder
    NoSave   = 1 << 3,          // Exclude from SaveGame/Checkpoint
    
    // Convenience
    Transient = NoSnapshot | NoRecord | NoSave  // Replaces [TransientComponent]
}
```

### Component Type Classification

| Component Type | Default Policy | Behavior |
|----------------|----------------|----------|
| **Unmanaged (struct)** | `Snapshotable + Recordable + Saveable` | Fast everywhere (memcpy) |
| **Record (immutable)** | `Snapshotable + Recordable + Saveable` | Safe everywhere (ref copy OK) |
| **Class (mutable)** | **`NoSnapshot + Recordable + Saveable`** | ⭐ **THE FIX** ⭐ |
| **`[DataPolicy(Transient)]`** | `NoSnapshot + NoRecord + NoSave` | Nowhere (main thread only) |
| **`[DataPolicy(SnapshotViaClone)]`** | `Snapshotable(clone) + Recordable + Saveable` | Everywhere (expensive) |

### New Registry Flags

`ComponentTypeRegistry` tracks **4 orthogonal properties** per type:

1. **`IsSnapshotable`** - Safe for background thread reference copy (Unmanaged, Records, `SnapshotViaClone`)
2. **`IsRecordable`** - Include in FlightRecorder (everything except `NoRecord`)
3. **`IsSaveable`** - Include in SaveGame (everything except `NoSave`)
4. **`NeedsClone`** - Use deep clone during snapshot (only `SnapshotViaClone`)

### Mask Methods

```csharp
// EntityRepository.Sync.cs
public BitMask256 GetSnapshotableMask()  // For GDB/SoD
public BitMask256 GetRecordableMask()    // For FlightRecorder
public BitMask256 GetSaveableMask()      // For SaveGame
```

---

## 📋 Implementation Tasks

### **Task 1: Create DataPolicy Attribute** ⭐⭐

**Objective:** Define the new attribute and enum for component data policies.

**File to Create:** `FDP/Fdp.Kernel/DataPolicyAttribute.cs`

```csharp
using System;

namespace Fdp.Kernel
{
    /// <summary>
    /// Controls how component data is handled by the FDP engine pipeline.
    /// Separate flags for concurrency safety (snapshots) vs persistence (recording/saving).
    /// </summary>
    [Flags]
    public enum DataPolicy
    {
        /// <summary>
        /// Default behavior:
        /// - Structs/Records: Snapshot + Record + Save  
        /// - Mutable Classes: ERROR (must specify policy)
        /// </summary>
        Default = 0,
        
        // ━━━ ModuleHost (Concurrency Safety) ━━━
        
        /// <summary>
        /// Exclude from background snapshots (GDB/SoD).
        /// Accessing this component in background modules returns null/default.
        /// Safe for mutable classes.
        /// </summary>
        NoSnapshot = 1 << 0,
        
        /// <summary>
        /// Include in background snapshots via Deep Clone.
        /// Safe for mutable classes but slower than reference copy.
        /// Use when background modules need to read mutable state.
        /// </summary>
        SnapshotViaClone = 1 << 1,
        
        // ━━━ Persistence (Disk/Network) ━━━
        
        /// <summary>
        /// Exclude from Flight Recorder (.fdp replay files).
        /// Use for debug-only data that shouldn't be in recordings.
        /// </summary>
        NoRecord = 1 << 2,
        
        /// <summary>
        /// Exclude from Save Game / Checkpoints.
        /// Use for runtime-only data that doesn't persist across sessions.
        /// </summary>
        NoSave = 1 << 3,
        
        // ━━━ Convenience Presets ━━━
        
        /// <summary>
        /// Completely transient: excluded from snapshots, recording, and saving.
        /// Replaces [TransientComponent] attribute.
        /// Use for: UI caches, temporary buffers, debug metrics.
        /// </summary>
        Transient = NoSnapshot | NoRecord | NoSave
    }
    
    /// <summary>
    /// Attribute to specify component data policy.
    /// </summary>
    /// <example>
    /// <code>
    /// // Mutable class: Record but don't share with background threads
    /// [DataPolicy(DataPolicy.NoSnapshot)]
    /// public class CombatHistory { /* mutable state */ }
    /// 
    /// // Completely transient
    /// [DataPolicy(DataPolicy.Transient)]
    /// public class UIRenderCache { /* temp data */ }
    /// 
    /// // Shareable via cloning (safe but slow)
    /// [DataPolicy(DataPolicy.SnapshotViaClone)]
    /// public class AIBlackboard { /* mutable AI state */ }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class DataPolicyAttribute : Attribute
    {
        public DataPolicy Policy { get; }
        
        public DataPolicyAttribute(DataPolicy policy)
        {
            Policy = policy;
        }
    }
}
```

**Deliverables:**
- [ ] Create `FDP/Fdp.Kernel/DataPolicyAttribute.cs`
- [ ] Build and verify no compilation errors

---

### **Task 2: Delete TransientComponent** ⭐

**Objective:** Remove the obsolete `TransientComponentAttribute` entirely.

**File to Delete:** `FDP/Fdp.Kernel/TransientComponentAttribute.cs`

**Rationale:** No existing code uses this attribute. Clean removal prevents confusion and simplifies the migration.

**Deliverables:**
- [ ] Delete `FDP/Fdp.Kernel/TransientComponentAttribute.cs`
- [ ] Build and verify no compilation errors

---

### **Task 3: Extend ComponentTypeRegistry** ⭐⭐⭐

**Objective:** Add parallel tracking for `IsRecordable`, `IsSaveable`, `NeedsClone`.

**File to Modify:** `FDP/Fdp.Kernel/ComponentType.cs`

**Changes to `ComponentTypeRegistry` class:**

```csharp
public static class ComponentTypeRegistry
{
    // Existing
    private static readonly List<bool> _isSnapshotable = new List<bool>();
    
    // NEW: Additional policy flags
    private static readonly List<bool> _isRecordable = new List<bool>();
    private static readonly List<bool> _isSaveable = new List<bool>();
    private static readonly List<bool> _needsClone = new List<bool>();
    
    // Existing method (keep as-is)
    public static void SetSnapshotable(int typeId, bool value) { /* ... */ }
    public static bool IsSnapshotable(int typeId) { /* ... */ }
    public static IEnumerable<int> GetSnapshotableTypeIds() { /* ... */ }
    
    // NEW: Recordable (FlightRecorder)
    public static void SetRecordable(int typeId, bool value)
    {
        lock (_lock)
        {
            EnsureCapacity(typeId);
            _isRecordable[typeId] = value;
        }
    }
    
    public static bool IsRecordable(int typeId)
    {
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _isRecordable.Count)
                return false;
            return _isRecordable[typeId];
        }
    }
    
    public static IEnumerable<int> GetRecordableTypeIds()
    {
        lock (_lock)
        {
            for (int i = 0; i < _isRecordable.Count; i++)
            {
                if (_isRecordable[i])
                    yield return i;
            }
        }
    }
    
    // NEW: Saveable (SaveGame/Checkpoint)
    public static void SetSaveable(int typeId, bool value)
    {
        lock (_lock)
        {
            EnsureCapacity(typeId);
            _isSaveable[typeId] = value;
        }
    }
    
    public static bool IsSaveable(int typeId)
    {
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _isSaveable.Count)
                return false;
            return _isSaveable[typeId];
        }
    }
    
    public static IEnumerable<int> GetSaveableTypeIds()
    {
        lock (_lock)
        {
            for (int i = 0; i < _isSaveable.Count; i++)
            {
                if (_isSaveable[i])
                    yield return i;
            }
        }
    }
    
    // NEW: NeedsClone (Deep clone during snapshot)
    public static void SetNeedsClone(int typeId, bool value)
    {
        lock (_lock)
        {
            EnsureCapacity(typeId);
            _needsClone[typeId] = value;
        }
    }
    
    public static bool NeedsClone(int typeId)
    {
        lock (_lock)
        {
            if (typeId < 0 || typeId >= _needsClone.Count)
                return false;
            return _needsClone[typeId];
        }
    }
    
    // UPDATE: EnsureCapacity to include new lists
    private static void EnsureCapacity(int typeId)
    {
        while (_types.Count <= typeId)
        {
            _types.Add(null!);
            _isSnapshotable.Add(true);
            _isRecordable.Add(true);   // NEW
            _isSaveable.Add(true);     // NEW
            _needsClone.Add(false);    // NEW
        }
    }
    
    // UPDATE: Clear to reset new lists
    public static void Clear()
    {
        lock (_lock)
        {
            // ... existing clears ...
            _isRecordable.Clear();
            _isSaveable.Clear();
            _needsClone.Clear();
        }
    }
}
```

**Deliverables:**
- [ ] Add `_isRecordable`, `_isSaveable`, `_needsClone` fields
- [ ] Implement getter/setter methods for each flag
- [ ] Update `EnsureCapacity()` and `Clear()` methods
- [ ] Build and verify no regression

---

### **Task 4: Implement DeepClone Generator** ⭐⭐⭐⭐

**Objective:** Add deep cloning capability using Expression Trees (JIT compilation).

**File to Modify:** `FDP/Fdp.Kernel/FlightRecorder/FdpAutoSerializer.cs`

**Add to `FdpAutoSerializer` class:**

```csharp
// Cache for cloner delegates
private static readonly ConcurrentDictionary<Type, Delegate> _clonerCache = 
    new ConcurrentDictionary<Type, Delegate>();

/// <summary>
/// Deep clone an object via JIT-compiled Expression Tree.
/// Immutable types (string, records) are copied by reference.
/// Circular references are NOT supported (will throw).
/// </summary>
public static T DeepClone<T>(T source) where T : class
{
    if (source == null)
        return null!;
    
    var cloner = (Func<T, T>)GetClonerDelegate<T>();
    return cloner(source);
}

private static Delegate GetClonerDelegate<T>() where T : class
{
    return _clonerCache.GetOrAdd(typeof(T), t => GenerateCloner<T>());
}

private static Func<T, T> GenerateCloner<T>() where T : class
{
    Type type = typeof(T);
    
    // Optimization: Immutable types → reference copy
    if (type == typeof(string) || ComponentTypeRegistry.IsRecordType(type))
    {
        // Return identity function (just returns the input)
        var param = Expression.Parameter(type, "source");
        return Expression.Lambda<Func<T, T>>(param, param).Compile();
    }
    
    var sourceParam = Expression.Parameter(type, "source");
    var variables = new List<ParameterExpression>();
    var statements = new List<Expression>();
    
    // var clone = new T();
    var cloneVar = Expression.Variable(type, "clone");
    variables.Add(cloneVar);
    
    var constructor = type.GetConstructor(Type.EmptyTypes);
    if (constructor == null)
        throw new InvalidOperationException(
            $"Type {type.Name} requires parameterless constructor for cloning.");
    
    statements.Add(Expression.Assign(cloneVar, Expression.New(constructor)));
    
    // Clone each field/property
    var members = GetSortedMembers(type); // Reuse existing helper
    
    foreach (var member in members)
    {
        Type memberType = member switch
        {
            FieldInfo fi => fi.FieldType,
            PropertyInfo pi => pi.PropertyType,
            _ => throw new NotSupportedException()
        };
        
        var sourceMember = Expression.MakeMemberAccess(sourceParam, member);
        var cloneMember = Expression.MakeMemberAccess(cloneVar, member);
        
        Expression cloneExpression;
        
        // Optimization: Immutable member types
        if (memberType == typeof(string) || 
            ComponentTypeRegistry.IsRecordType(memberType) ||
            memberType.IsValueType)
        {
            // Direct assignment (reference or value copy)
            cloneExpression = sourceMember;
        }
        else if (memberType.IsClass)
        {
            // Recursive deep clone for nested classes
            var cloneMethod = typeof(FdpAutoSerializer)
                .GetMethod(nameof(DeepClone), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(memberType);
            
            cloneExpression = Expression.Call(cloneMethod, sourceMember);
        }
        else
        {
            // Fallback
            cloneExpression = sourceMember;
        }
        
        // Handle PropertyInfo vs FieldInfo
        if (member is PropertyInfo pi && !pi.CanWrite)
            continue; // Skip readonly properties
        
        statements.Add(Expression.Assign(cloneMember, cloneExpression));
    }
    
    // Return clone
    statements.Add(cloneVar);
    
    var block = Expression.Block(variables, statements);
    return Expression.Lambda<Func<T, T>>(block, sourceParam).Compile();
}
```

**Note on Circular References:**
- **Decision:** Fail-fast (throw exception) if detected
- **Rationale:** ECS components should be DAG-structured data. Circular graphs are anti-patterns.
- **Detection:** Stack overflow will naturally occur during recursive cloning.
- **Documentation:** Add XML comment warning: "Circular references not supported."

**Deliverables:**
- [ ] Add `DeepClone<T>` method
- [ ] Add `GetClonerDelegate<T>` caching
- [ ] Add `GenerateCloner<T>` Expression Tree builder
- [ ] Add immutability optimizations (string, record, struct)
- [ ] Build and verify compilation

---

### **Task 5: Update RegisterComponent Logic** ⭐⭐⭐⭐

**Objective:** Parse `DataPolicy` attribute and auto-default mutable classes.

**File to Modify:** `FDP/Fdp.Kernel/EntityRepository.cs`

**Replace existing `RegisterComponent<T>` method (lines 397-465):**

```csharp
public void RegisterComponent<T>(DataPolicy? policyOverride = null)
{
    Type type = typeof(T);
    
    if (ComponentTypeHelper.IsUnmanaged<T>())
    {
        // ━━━ UNMANAGED (Struct) ━━━
        UnsafeShim.RegisterUnmanaged<T>(this);
        
        int typeId = ComponentTypeRegistry.GetId(type);
        if (typeId < 0) return; // Registration failed
        
        // Explicit override, or default to all enabled
        DataPolicy policy = policyOverride ?? DataPolicy.Default;
        
        bool snapshot = !policy.HasFlag(DataPolicy.NoSnapshot);
        bool record = !policy.HasFlag(DataPolicy.NoRecord);
        bool save = !policy.HasFlag(DataPolicy.NoSave);
        bool clone = policy.HasFlag(DataPolicy.SnapshotViaClone);
        
        ComponentTypeRegistry.SetSnapshotable(typeId, snapshot);
        ComponentTypeRegistry.SetRecordable(typeId, record);
        ComponentTypeRegistry.SetSaveable(typeId, save);
        ComponentTypeRegistry.SetNeedsClone(typeId, clone);  // Structs never cloned normally
    }
    else
    {
        // ━━━ MANAGED (Class/Record) ━━━
        UnsafeShim.RegisterManaged<T>(this);
        int typeId = ComponentTypeRegistry.GetId(type);
        if (typeId < 0) return;
        
        // Priority 1: Explicit override parameter (highest priority)
        if (policyOverride.HasValue)
        {
            DataPolicy policy = policyOverride.Value;
            
            bool snapshot = !policy.HasFlag(DataPolicy.NoSnapshot);
            bool record = !policy.HasFlag(DataPolicy.NoRecord);
            bool save = !policy.HasFlag(DataPolicy.NoSave);
            bool clone = policy.HasFlag(DataPolicy.SnapshotViaClone);
            
            // If SnapshotViaClone is set, force snapshot=true
            if (clone) snapshot = true;
            
            ComponentTypeRegistry.SetSnapshotable(typeId, snapshot);
            ComponentTypeRegistry.SetRecordable(typeId, record);
            ComponentTypeRegistry.SetSaveable(typeId, save);
            ComponentTypeRegistry.SetNeedsClone(typeId, clone);
            return;
        }
        
        // Priority 2: DataPolicy attribute
        var dataPolicyAttr = type.GetCustomAttribute<DataPolicyAttribute>();
        
        DataPolicy effectivePolicy;
        
        if (dataPolicyAttr != null)
        {
            effectivePolicy = dataPolicyAttr.Policy;
        }
        else
        {
            // Priority 3: Convention-based defaults
            bool isRecord = ComponentTypeRegistry.IsRecordType(type);
            
            if (isRecord)
            {
                // Record → Safe everywhere
                effectivePolicy = DataPolicy.Default;  // All enabled
            }
            else
            {
                // Mutable Class → Auto-default to NoSnapshot
                effectivePolicy = DataPolicy.NoSnapshot;
                
                #if DEBUG
                Console.WriteLine($"WARNING: Mutable class '{type.Name}' registered without [DataPolicy]. " +
                                  $"Defaulting to NoSnapshot (Recordable + Saveable only).");
                #endif
            }
        }
        
        // Apply flags
        bool finalSnapshot = !effectivePolicy.HasFlag(DataPolicy.NoSnapshot);
        bool finalRecord = !effectivePolicy.HasFlag(DataPolicy.NoRecord);
        bool finalSave = !effectivePolicy.HasFlag(DataPolicy.NoSave);
        bool finalClone = effectivePolicy.HasFlag(DataPolicy.SnapshotViaClone);
        
        // If SnapshotViaClone is set, force snapshot=true
        if (finalClone) finalSnapshot = true;
        
        ComponentTypeRegistry.SetSnapshotable(typeId, finalSnapshot);
        ComponentTypeRegistry.SetRecordable(typeId, finalRecord);
        ComponentTypeRegistry.SetSaveable(typeId, finalSave);
        ComponentTypeRegistry.SetNeedsClone(typeId, finalClone);
    }
}
```

**Deliverables:**
- [ ] Replace `RegisterComponent<T>` signature: `bool? snapshotable` → `DataPolicy? policyOverride`
- [ ] Update logic to parse DataPolicy flags
- [ ] Add auto-default for mutable classes
- [ ] Add DEBUG warning for implicit defaults
- [ ] Build and verify compilation

---

### **Task 6: Update ManagedComponentTable for Cloning** ⭐⭐⭐⭐

**Objective:** Modify `SyncDirtyChunks` to use deep clone when `IsCloneable=true`.

**File to Modify:** `FDP/Fdp.Kernel/ManagedComponentTable.cs`

**Replace `SyncDirtyChunks` method (lines 233-281):**

```csharp
/// <summary>
/// Synchronizes dirty chunks from a source table.
/// Uses shallow copy (Array.Copy) for normal components.
/// Uses deep clone for components marked with DataPolicy.SnapshotViaClone.
/// </summary>
public void SyncDirtyChunks(ManagedComponentTable<T> source)
{
    // Check if this type requires cloning
    int typeId = ComponentTypeRegistry.GetId(typeof(T));
    bool needsClone = ComponentTypeRegistry.NeedsClone(typeId);
    
    int loopMax = Math.Min(_chunks.Length, source._chunks.Length);
    
    for (int i = 0; i < loopMax; i++)
    {
        // Version check (skip unchanged chunks)
        uint srcVer = source.GetChunkVersion(i);
        if (_chunkVersions[i] == srcVer)
            continue;
        
        var srcChunk = source._chunks[i];
        
        if (srcChunk == null)
        {
            // Source is empty
            if (_chunks[i] != null)
            {
                _chunks[i] = null!;
            }
            _chunkVersions[i] = srcVer;
            continue;
        }
        
        // Ensure destination storage exists
        if (_chunks[i] == null)
        {
            _chunks[i] = new T[_chunkSize];
        }
        
        if (needsClone)
        {
            // ━━━ SLOW PATH: Deep Clone ━━━
            for (int k = 0; k < _chunkSize; k++)
            {
                var srcVal = srcChunk[k];
                if (srcVal != null)
                {
                    _chunks[i][k] = FdpAutoSerializer.DeepClone(srcVal);
                }
                else
                {
                    _chunks[i][k] = null!;
                }
            }
        }
        else
        {
            // ━━━ FAST PATH: Shallow Copy ━━━
            // This relies on components being immutable records or safe by design.
            Array.Copy(srcChunk, _chunks[i], _chunkSize);
        }
        
        _chunkVersions[i] = srcVer;
    }
}
```

**Deliverables:**
- [ ] Update `SyncDirtyChunks` method
- [ ] Add `NeedsClone` check
- [ ] Implement deep clone loop
- [ ] Build and verify compilation

---

### **Task 7: Add GetRecordableMask and GetSaveableMask** ⭐⭐

**Objective:** Create separate mask methods for different subsystems.

**File to Modify:** `FDP/Fdp.Kernel/EntityRepository.Sync.cs`

**Add new methods after existing `GetSnapshotableMask` (after line 131):**

```csharp
/// <summary>
/// Builds a component mask containing only recordable component types.
/// Used by FlightRecorder to determine which components to serialize to .fdp files.
/// </summary>
public BitMask256 GetRecordableMask()
{
    var mask = new BitMask256();
    var recordableIds = ComponentTypeRegistry.GetRecordableTypeIds();
    foreach (var id in recordableIds)
        mask.SetBit(id);
    return mask;
}

/// <summary>
/// Builds a component mask containing only saveable component types.
/// Used by SaveGame/Checkpoint system to determine which components to persist.
/// </summary>
public BitMask256 GetSaveableMask()
{
    var mask = new BitMask256();
    var saveableIds = ComponentTypeRegistry.GetSaveableTypeIds();
    foreach (var id in saveableIds)
        mask.SetBit(id);
    return mask;
}
```

**Deliverables:**
- [ ] Add `GetRecordableMask()` method
- [ ] Add `GetSaveableMask()` method
- [ ] Update XML doc comments
- [ ] Build and verify compilation

---

### **Task 8: Update RecorderSystem to Use Recordable Mask** ⭐⭐

**Objective:** Make FlightRecorder use `GetRecordableMask()` instead of `GetSnapshotableMask()`.

**File to Modify:** `FDP/Fdp.Kernel/FlightRecorder/RecorderSystem.cs`

**Replace `GetSnapshotableMask` method (line 465-471):**

```csharp
/// <summary>
/// Gets the component mask for recordable components.
/// This respects DataPolicy.NoRecord to exclude debug/transient data.
/// </summary>
private BitMask256 GetRecordableMask()
{
    var mask = new BitMask256();
    var ids = ComponentTypeRegistry.GetRecordableTypeIds();
    foreach (var id in ids) mask.SetBit(id);
    return mask;
}
```

**Update usages (lines 128, 325):**

```csharp
// OLD:
var snapshotableMask = GetSnapshotableMask();

// NEW:
var recordableMask = GetRecordableMask();
```

**Deliverables:**
- [ ] Replace `GetSnapshotableMask()` with `GetRecordableMask()`
- [ ] Update all call sites (2 locations)
- [ ] Update XML doc comments
- [ ] Build and verify compilation

---

### **Task 9: Update RepositorySerializer to Use Saveable Mask** ⭐⭐

**Objective:** Make SaveGame system use `GetSaveableMask()`.

**File to Modify:** `FDP/Fdp.Kernel/Serialization/RepositorySerializer.cs`

**Find usages of `GetSnapshotableMask` and replace with `GetSaveableMask`:**

```bash
# Search for GetSnapshotableMask in RepositorySerializer.cs
# Replace with GetSaveableMask where appropriate
```

**Example change:**

```csharp
// OLD:
var mask = repo.GetSnapshotableMask();

// NEW:
var mask = repo.GetSaveableMask();
```

**Deliverables:**
- [ ] Search for `GetSnapshotableMask` in `RepositorySerializer.cs`
- [ ] Replace with `GetSaveableMask()` for checkpoint operations
- [ ] Build and verify compilation

---

### **Task 10: Verify ModuleHost Integration** ⭐

**Objective:** Ensure `SharedSnapshotProvider` still works correctly.

**File to Check:** `ModuleHost.Core/Providers/SharedSnapshotProvider.cs`

**Verification:**
- Does `SharedSnapshotProvider` call `EntityRepository.SyncFrom`?
- If yes, no changes needed (`SyncFrom` → `SyncDirtyChunks` → uses `NeedsClone` automatically)
- If no, verify what mask method it uses

**If it uses a mask directly:**
```csharp
// Should use:
var mask = repo.GetSnapshotableMask();  // ✅ Correct for background threads

// Should NOT use:
var mask = repo.GetRecordableMask();    // ❌ Wrong (too permissive)
```

**Deliverables:**
- [ ] Review `SharedSnapshotProvider.cs`
- [ ] Verify it uses `GetSnapshotableMask()` or `SyncFrom()`
- [ ] Document findings

---

## 🧪 Testing Strategy

### **Task 11: Unit Tests - ComponentTypeRegistry** ⭐⭐

**File to Create:** `FDP/Fdp.Tests/ComponentTypeRegistryPolicyTests.cs`

```csharp
using Xunit;
using Fdp.Kernel;

namespace Fdp.Tests
{
    public class ComponentTypeRegistryPolicyTests
    {
        [Fact]
        public void SetRecordable_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetRecordable(0, true);
            Assert.True(ComponentTypeRegistry.IsRecordable(0));
            
            ComponentTypeRegistry.SetRecordable(0, false);
            Assert.False(ComponentTypeRegistry.IsRecordable(0));
        }
        
        [Fact]
        public void SetSaveable_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetSaveable(0, true);
            Assert.True(ComponentTypeRegistry.IsSaveable(0));
        }
        
        [Fact]
        public void SetNeedsClone_StoresValue()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetNeedsClone(0, true);
            Assert.True(ComponentTypeRegistry.NeedsClone(0));
        }
        
        [Fact]
        public void GetRecordableTypeIds_ReturnsOnlyRecordable()
        {
            ComponentTypeRegistry.Clear();
            ComponentTypeRegistry.SetRecordable(0, true);
            ComponentTypeRegistry.SetRecordable(1, false);
            ComponentTypeRegistry.SetRecordable(2, true);
            
            var ids = ComponentTypeRegistry.GetRecordableTypeIds();
            Assert.Equal(new[] { 0, 2 }, ids);
        }
    }
}
```

**Deliverables:**
- [ ] Create unit tests for new registry methods
- [ ] Run tests and ensure 100% pass

---

### **Task 12: Integration Tests - Mutable Class Recording** ⭐⭐⭐⭐

**File to Create:** `FDP/Fdp.Tests/MutableClassRecordingTests.cs`

```csharp
using System.Collections.Generic;
using MessagePack;
using Xunit;
using Fdp.Kernel;

namespace Fdp.Tests
{
    // Test component: Mutable class WITHOUT DataPolicy attribute
    [MessagePackObject]
    public class MutableHistory
    {
        [Key(0)] public int Counter { get; set; }
        [Key(1)] public List<string> Events { get; set; } = new();
        
        public void AddEvent(string evt)
        {
            Counter++;
            Events.Add(evt);
        }
    }
    
    public class MutableClassRecordingTests
    {
        [Fact]
        public void MutableClass_NoAttribute_DoesNotCrash()
        {
            // THE FIX: This should NOT throw
            var repo = new EntityRepository();
            repo.RegisterComponent<MutableHistory>();
            
            var e = repo.CreateEntity();
            repo.AddComponent(e, new MutableHistory());
            
            Assert.True(repo.HasComponent<MutableHistory>(e));
        }
        
        [Fact]
        public void MutableClass_DefaultsToRecordable()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<MutableHistory>();
            
            int typeId = ComponentTypeRegistry.GetId(typeof(MutableHistory));
            
            Assert.True(ComponentTypeRegistry.IsRecordable(typeId));
            Assert.True(ComponentTypeRegistry.IsSaveable(typeId));
        }
        
        [Fact]
        public void MutableClass_DefaultsToNoSnapshot()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<MutableHistory>();
            
            int typeId = ComponentTypeRegistry.GetId(typeof(MutableHistory));
            
            // Should NOT be snapshotable (unsafe for background threads)
            Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
        }
        
        [Fact]
        public void GetRecordableMask_IncludesMutableClass()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<MutableHistory>();
            
            var mask = repo.GetRecordableMask();
            int typeId = ComponentTypeRegistry.GetId(typeof(MutableHistory));
            
            Assert.True(mask.IsSet(typeId));
        }
        
        [Fact]
        public void GetSnapshotableMask_ExcludesMutableClass()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<MutableHistory>();
            
            var mask = repo.GetSnapshotableMask();
            int typeId = ComponentTypeRegistry.GetId(typeof(MutableHistory));
            
            Assert.False(mask.IsSet(typeId));
        }
    }
}
```

**Deliverables:**
- [ ] Create `MutableClassRecordingTests.cs`
- [ ] Add 5 core tests
- [ ] Run tests and ensure 100% pass

---

### **Task 13: Integration Tests - Clone Correctness** ⭐⭐⭐⭐

**File to Create:** `FDP/Fdp.Tests/ComponentCloningTests.cs`

```csharp
using System.Collections.Generic;
using MessagePack;
using Xunit;
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;

namespace Fdp.Tests
{
    [MessagePackObject]
    [DataPolicy(DataPolicy.SnapshotViaClone)]
    public class CloneableComponent
    {
        [Key(0)] public int Value { get; set; }
        [Key(1)] public List<string> Items { get; set; } = new();
    }
    
    public class ComponentCloningTests
    {
        [Fact]
        public void DeepClone_SimpleClass_CreatesIndependentCopy()
        {
            var original = new CloneableComponent 
            { 
                Value = 42, 
                Items = new List<string> { "A", "B" } 
            };
            
            var clone = FdpAutoSerializer.DeepClone(original);
            
            // Verify clone has same data
            Assert.Equal(42, clone.Value);
            Assert.Equal(new[] { "A", "B" }, clone.Items);
            
            // Verify independence: mutating original doesn't affect clone
            original.Value = 99;
            original.Items.Add("C");
            
            Assert.Equal(42, clone.Value);  // Clone unchanged
            Assert.Equal(new[] { "A", "B" }, clone.Items);  // Clone unchanged
        }
        
        [Fact]
        public void DeepClone_String_ReturnsReference()
        {
            string original = "test";
            string clone = FdpAutoSerializer.DeepClone(original);
            
            // Strings are immutable, so reference copy is safe
            Assert.Same(original, clone);
        }
        
        [Fact]
        public void SyncDirtyChunks_CloneableComponent_CreatesIndependentCopies()
        {
            var repo1 = new EntityRepository();
            var repo2 = new EntityRepository();
            
            repo1.RegisterComponent<CloneableComponent>();
            repo2.RegisterComponent<CloneableComponent>();
            
            var e = repo1.CreateEntity();
            var original = new CloneableComponent { Value = 100 };
            repo1.AddComponent(e, original);
            
            // Simulate snapshot sync
            repo2.SyncFrom(repo1);
            
            // Mutate original
            original.Value = 999;
            
            // Verify snapshot is isolated
            var snapshotCopy = repo2.GetComponent<CloneableComponent>(e);
            Assert.Equal(100, snapshotCopy.Value);  // Clone unchanged
        }
    }
}
```

**Deliverables:**
- [ ] Create `ComponentCloningTests.cs`
- [ ] Add 3 clone correctness tests
- [ ] Run tests and ensure 100% pass

---

### **Task 14: Integration Tests - DataPolicy Combinations** ⭐⭐⭐

**File to Create:** `FDP/Fdp.Tests/DataPolicyAttributeTests.cs`

```csharp
using MessagePack;
using Xunit;
using Fdp.Kernel;

namespace Fdp.Tests
{
    [MessagePackObject]
    [DataPolicy(DataPolicy.Transient)]
    public class TransientComponent
    {
        [Key(0)] public int Value { get; set; }
    }
    
    [MessagePackObject]
    [DataPolicy(DataPolicy.NoSnapshot | DataPolicy.NoSave)]
    public class RecordOnlyComponent
    {
        [Key(0)] public int Value { get; set; }
    }
    
    public class DataPolicyAttributeTests
    {
        [Fact]
        public void Transient_ExcludedFromEverything()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<TransientComponent>();
            
            int typeId = ComponentTypeRegistry.GetId(typeof(TransientComponent));
            
            Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
            Assert.False(ComponentTypeRegistry.IsRecordable(typeId));
            Assert.False(ComponentTypeRegistry.IsSaveable(typeId));
        }
        
        [Fact]
        public void RecordOnly_IncludedInRecorder_ExcludedElsewhere()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<RecordOnlyComponent>();
            
            int typeId = ComponentTypeRegistry.GetId(typeof(RecordOnlyComponent));
            
            Assert.False(ComponentTypeRegistry.IsSnapshotable(typeId));
            Assert.True(ComponentTypeRegistry.IsRecordable(typeId));  // ✅ Recorded
            Assert.False(ComponentTypeRegistry.IsSaveable(typeId));
        }
        
        [Fact]
        public void SnapshotViaClone_SetsNeedsCloneFlag()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<CloneableComponent>();
            
            int typeId = ComponentTypeRegistry.GetId(typeof(CloneableComponent));
            
            Assert.True(ComponentTypeRegistry.IsSnapshotable(typeId));
            Assert.True(ComponentTypeRegistry.NeedsClone(typeId));  // ✅
        }
    }
}
```

**Deliverables:**
- [ ] Create `DataPolicyAttributeTests.cs`
- [ ] Add tests for all policy combinations
- [ ] Run tests and ensure 100% pass

---

## ✅ Validation Criteria

### Build Verification
```powershell
dotnet build FDP/Fdp.Kernel/Fdp.Kernel.csproj --nologo
# Expected: Build succeeded. 0 Warning(s)
```

### Test Verification
```powershell
dotnet test FDP/Fdp.Tests/Fdp.Tests.csproj --filter "FullyQualifiedName~DataPolicy|MutableClass|Cloning" --nologo
# Expected: All tests passed
```

### Functional Verification

**Scenario 1: Mutable Class Auto-Default**
```csharp
[MessagePackObject]
public class CombatHistory { /* mutable */ }

var repo = new EntityRepository();
repo.RegisterComponent<CombatHistory>();  // ✅ No crash

var e = repo.CreateEntity();
repo.AddComponent(e, new CombatHistory());

// Recordable ✅
var recordMask = repo.GetRecordableMask();
Assert.True(recordMask.IsSet(ComponentTypeRegistry.GetId(typeof(CombatHistory))));

// NOT Snapshotable ✅
var snapMask = repo.GetSnapshotableMask();
Assert.False(snapMask.IsSet(ComponentTypeRegistry.GetId(typeof(CombatHistory))));
```

**Scenario 2: Cloneable Component**
```csharp
[DataPolicy(DataPolicy.SnapshotViaClone)]
public class AIBlackboard { /* mutable */ }

var repo1 = new EntityRepository();
var repo2 = new EntityRepository();
repo1.RegisterComponent<AIBlackboard>();
repo2.RegisterComponent<AIBlackboard>();

var e = repo1.CreateEntity();
var original = new AIBlackboard { State = "thinking" };
repo1.AddComponent(e, original);

repo2.SyncFrom(repo1);  // Deep clone ✅

original.State = "acting";
var clone = repo2.GetComponent<AIBlackboard>(e);
Assert.Equal("thinking", clone.State);  // Clone isolated ✅
```

---

## 📝 Documentation Updates

### **Task 15: Update User Guide** ⭐

**File to Update:** `docs/UserGuide/FDP-ModuleHost-UserGuide.md`

**Add section after "Component Registration":**

```markdown
### Component Data Policies

FDP provides fine-grained control over how components are handled by different engine subsystems using the `[DataPolicy]` attribute.

#### The DataPolicy Attribute

```csharp
[Flags]
public enum DataPolicy
{
    NoSnapshot       = 1 << 0,  // Exclude from background snapshots
    SnapshotViaClone = 1 << 1,  // Include via deep clone (safe, slow)
    NoRecord         = 1 << 2,  // Exclude from FlightRecorder
    NoSave           = 1 << 3,  // Exclude from SaveGame
    Transient        = NoSnapshot | NoRecord | NoSave  // Nowhere
}
```

#### Common Scenarios

**Scenario 1: Mutable Gameplay Data (Auto-Default)**
```csharp
// No attribute needed! Auto-defaults to: Recordable + Saveable + NoSnapshot
public class CombatHistory
{
    public int TotalDamage { get; set; }
    public List<string> Events { get; set; } = new();
}
```
✅ Recorded by FlightRecorder  
✅ Saved in checkpoints  
❌ NOT visible to background modules (safe)

**Scenario 2: Debug Metrics (Record but Don't Save)**
```csharp
[DataPolicy(DataPolicy.NoSnapshot | DataPolicy.NoSave)]
public class PerformanceMetrics
{
    public double AvgFrameTime { get; set; }
}
```
✅ Recorded for analysis  
❌ NOT saved in checkpoints  
❌ NOT visible to background modules

**Scenario 3: Transient Cache**
```csharp
[DataPolicy(DataPolicy.Transient)]
public class UIRenderCache
{
    public Dictionary<int, Texture> Cache { get; set; }
}
```
❌ NOT recorded, NOT saved, NOT snapshotted (main thread only)

**Scenario 4: Shared Mutable State (Advanced)**
```csharp
[DataPolicy(DataPolicy.SnapshotViaClone)]
public class AIBlackboard
{
    public string CurrentGoal { get; set; }
    public List<Vector3> Waypoints { get; set; }
}
```
✅ Background AI module gets a **deep clone** (safe but slower)  
✅ Recorded and saved

#### Performance Notes

- **Default (NoSnapshot)**: Zero overhead
- **SnapshotViaClone**: ~10-100x slower than struct copy (depends on complexity)
- **Immutable Records**: Zero overhead (reference copy is safe)

Use `SnapshotViaClone` only when background modules truly need mutable state.
```

**Deliverables:**
- [ ] Add DataPolicy section to User Guide
- [ ] Include code examples
- [ ] Add performance notes

---

## 🎓 Developer Notes

### Design Decisions Summary

1. **Auto-Default for Mutable Classes**: `NoSnapshot + Recordable + Saveable`
   - Rationale: Solves user's immediate problem ("My demo crashed!") without code changes
   - Safe by default (no concurrency issues)
   - Useful by default (recording still works)

2. **Cloning via Expression Trees**
   - Rationale: Highest performance (JIT-compiled)
   - Immutability optimizations built-in (string, record)
   - Consistent with existing `FdpAutoSerializer` architecture

3. **Fail-Fast on Circular References**
   - Rationale: ECS components should be DAG-structured
   - Simplifies implementation
   - Document limitation clearly

4. **Separate Mask Methods**
   - `GetSnapshotableMask()` - For GDB/SoD
   - `GetRecordableMask()` - For FlightRecorder
   - `GetSaveableMask()` - For SaveGame
   - Rationale: Clear separation of concerns, easier to reason about

### Backward Compatibility

- **BREAKING CHANGE**: `[TransientComponent]` attribute has been removed
  - No migration needed (no existing usage)
  - Clean break prevents confusion
- `RegisterComponent<T>` signature changed: `bool? snapshotable` → `DataPolicy? policyOverride`
  - More expressive API
  - Clearer intent

### Future Enhancements (Out of Scope)

- **Circular Reference Support**: Add object graph tracking
- **Partial Cloning**: Clone only specific fields
- **Clone Pooling**: Reuse cloned objects for performance

---

## 🚀 Completion Checklist

### Phase 1: Core Infrastructure
- [ ] Task 1: Create `DataPolicyAttribute.cs`
- [ ] Task 2: Delete `TransientComponentAttribute`
- [ ] Task 3: Extend `ComponentTypeRegistry`

### Phase 2: Cloning
- [ ] Task 4: Implement `DeepClone` in `FdpAutoSerializer`

### Phase 3: Registration
- [ ] Task 5: Update `RegisterComponent` logic

### Phase 4: Sync & Tables
- [ ] Task 6: Update `ManagedComponentTable.SyncDirtyChunks`
- [ ] Task 7: Add `GetRecordableMask` and `GetSaveableMask`

### Phase 5: Subsystem Integration
- [ ] Task 8: Update `RecorderSystem`
- [ ] Task 9: Update `RepositorySerializer`
- [ ] Task 10: Verify `SharedSnapshotProvider`

### Phase 6: Testing
- [ ] Task 11: Unit tests - `ComponentTypeRegistry`
- [ ] Task 12: Integration tests - Mutable class recording
- [ ] Task 13: Integration tests - Clone correctness
- [ ] Task 14: Integration tests - DataPolicy combinations

### Phase 7: Documentation
- [ ] Task 15: Update User Guide

### Final Validation
- [ ] Build clean (0 warnings)
- [ ] All tests pass (100%)
- [ ] User scenario verified (CombatHistory works)
- [ ] Performance check (no regression on struct snapshots)

---

## 📊 Success Metrics

**Before BATCH-11:**
```csharp
repo.RegisterComponent<CombatHistory>();  
// ❌ CRASH: InvalidOperationException
```

**After BATCH-11:**
```csharp
repo.RegisterComponent<CombatHistory>();  
// ✅ Works! Auto-defaults to recordable
var e = repo.CreateEntity();
repo.AddComponent(e, new CombatHistory());

// ✅ Flight Recorder serializes it
recorder.RecordFrame();

// ✅ Background modules don't see it (safe)
var bg = backgroundView.GetComponent<CombatHistory>(e);
// Returns null (filtered by snapshot mask)
```

**Developer Experience:**
- 🎯 Clear intent through `[DataPolicy]` attribute
- 🛡️ Safe by default (no concurrency bugs)
- 📊 Useful by default (recording works)
- 🚀 Performance-conscious (cloning is opt-in)

---

## 🔗 References

- **Design Document**: `docs/FDP-DataPolicyAndMutableClassCloning.md`
- **Related Issues**: 
  - User reported: "Flight Recorder not recording mutable classes"
  - Related to: BATCH-09.x (Time Controller work - different subsystem)
- **Architecture**: FDP Kernel Component System (Tier 1/2 storage)

---

**END OF BATCH-11 INSTRUCTIONS**
