## Transient Components & Snapshot Filtering

### Overview

**Transient Components** are excluded from all snapshot operations (GDB, DoubleBuffer, Flight Recorder). This ensures:
- **Thread Safety:** Mutable managed components can't cause race conditions
- **Performance:** Heavy caches don't bloat snapshots
- **Memory:** Reduced snapshot size

---

### Component Classification

| Component Type | Snapshotable | Rationale |
|----------------|--------------|-----------|
| **Struct** (unmanaged) | ✅ Yes | Value copy, thread-safe |
| **Record** (class) | ✅ Yes | Immutable, compiler-enforced |
| **Class** + `[DataPolicy(DataPolicy.Transient)]` | ❌ No | Mutable, main-thread only |
| **Class** (no attribute) | ❌ **No** | Default safety fallback (NoSnapshot) |
| **Class** + `[DataPolicy(DataPolicy.SnapshotViaClone)]` | ✅ Yes | Safe Deep Copy |

---

### Marking Components

**Option 1: Attribute** (for mutable classes):
```csharp
[DataPolicy(DataPolicy.Transient)]
public class UIRenderCache
{
    public Dictionary<int, Texture> TextureCache = new();
}
```

**Option 2: Record** (for immutable data):
```csharp
// Auto-snapshotable (no attribute needed)
public record PlayerStats(int Health, int Score, string Name);
```

---

### Registration

```csharp
// Struct - snapshotable by default
repository.RegisterComponent<Position>();

// Record - auto-detected as immutable ✅
repository.RegisterComponent<PlayerStats>();

// Class with attribute - auto-detected as transient ❌
repository.RegisterComponent<UIRenderCache>();

// Class without attribute - Fallback to NoSnapshot
repository.RegisterComponent<GameState>();
// Warning: "Mutable class 'GameState' registered without [DataPolicy]. Defaulting to NoSnapshot."
```

---

### Snapshot Filtering

**Default:** Excludes transient components automatically
```csharp
snapshot.SyncFrom(liveWorld);
// Result:
// ✅ Position, Velocity (snapshotable) → Copied
// ❌ UIRenderCache (transient) → NOT copied
```

**Debug Override:** Force include transient
```csharp
debugSnapshot.SyncFrom(liveWorld, includeTransient: true);
// Result:
// ✅ Position, Velocity → Copied
// ✅ UIRenderCache (transient) → Copied for debugging
```

**Optimization Override:** Exclude specific snapshotable types
```csharp
// Network sync - exclude large data
networkSnapshot.SyncFrom(liveWorld, excludeTypes: new[] 
{ 
    typeof(NavigationMesh),    // Too large for network
    typeof(TerrainHeightMap)   // Static, doesn't change
});
// Result:
// ✅ Position, Velocity → Copied
// ❌ NavigationMesh, TerrainHeightMap → Excluded (optimization)
// ❌ UIRenderCache → Excluded (transient)
```

**Explicit Mask Override:** Full manual control
```csharp
// Build custom mask (only Position and Velocity)
var customMask = new BitMask256();
customMask.SetBit(ComponentType<Position>.ID);
customMask.SetBit(ComponentType<Velocity>.ID);

snapshot.SyncFrom(liveWorld, mask: customMask);
// Result:
// ✅ Position, Velocity → Copied
// ❌ Everything else → Excluded (explicit control)
// Note: Explicit mask STILL filters out transient by default!

// To include transient WITH explicit mask:
snapshot.SyncFrom(liveWorld, mask: customMask, includeTransient: true);
```

**Priority Rules:**
1. **Explicit mask** (if provided) → Intersects with snapshotable mask
2. **includeTransient** (if true) → Overrides default transient exclusion
3. **excludeTypes** (if provided) → Removes specific types from mask
4. **Default** (no parameters) → Auto-builds snapshotable-only mask

---

### Flight Recorder Integration

Flight Recorder automatically excludes transient components:

```csharp
recorder.CaptureKeyframe();  // Excludes transient
recorder.CaptureKeyframe(includeTransient: true);  // Debug mode
```
