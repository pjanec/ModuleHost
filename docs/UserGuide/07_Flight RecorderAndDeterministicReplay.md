## Flight Recorder & Deterministic Replay

### What is the Flight Recorder?

The **Flight Recorder** captures a deterministic record of the simulation for:
- **Replay:** Step through history frame-by-frame
- **Debugging:** Identify when bugs occurred
- **Network sync:** Reconcile divergent clients
- **Analytics:** Post-process simulation data

### How It Works

**Delta Compression:**
```csharp
Frame 0: Full Snapshot (baseline)
Frame 1: Delta (only changes since Frame 0)
Frame 2: Delta (only changes since Frame 1)
...
Frame 100: Full Snapshot (new baseline)
```

**Recording Process:**
1. Each frame, recorder queries: "Components changed since last frame?"
2. Uses `EntityRepository.GlobalVersion` to detect changes
3. Writes delta to binary stream
4. Periodically writes full snapshot (baseline)

### Design Implications

**Critical Rules for Replay:**

1. **Tick the Repository:**
   ```csharp
   void Update()
   {
       _repository.Tick(); // MUST call every frame!
       // ... simulation ...
   }
   ```

2. **Managed Components Must Be Immutable:**
   ```csharp
   // ✅ GOOD: Immutable record
   public record BehaviorState
   {
       public required int CurrentNode { get; init; }
   }
   
   // ❌ BAD: Mutable class
   public class BehaviorState
   {
       public int CurrentNode; // Shallow copy breaks replay!
   }
   ```

3. **Mark Transient Components:**
   ```csharp
   ```csharp
   [DataPolicy(DataPolicy.Transient)]
   public class UIRenderCache { } // Never recorded
   ```
   ```

4. **Deterministic Logic:**
   ```csharp
   // ❌ BAD: Non-deterministic
   var random = new Random(); // Different on replay!
   
   // ✅ GOOD: Seeded RNG stored in component
   var rng = World.GetComponent<RandomState>(entity);
   var value = rng.Next();
   World.SetComponent(entity, rng); // Save state
   ```

### Transient Components and Recording

**Transient components are automatically excluded:**

```csharp
```csharp
repository.RegisterComponent<UIRenderCache>(DataPolicy.Transient);

// Flight Recorder uses OnlyRecordable component mask
// UIRenderCache is never serialized
// Replays are smaller and faster
```

### Example Integration

```csharp
var repository = new EntityRepository();
var recorder = new FlightRecorder(repository);

// Main loop
void Update(float deltaTime)
{
    repository.Tick(); // Increment version
    
    // Run simulation...
    
    // Capture frame
    recorder.CaptureFrame(repository.GlobalVersion - 1);
}

// Replay
void Replay(int frameIndex)
{
    recorder.RestoreFrame(frameIndex, repository);
    // Repository now matches historical state
}
```

---

### Advanced Playback with PlaybackController

The `RecordingReader` provides sequential playback. For interactive replay tools (UI scrubbers, debuggers), use `PlaybackController`:

#### Features

- **Seeking:** Jump to any frame instantly
- **Stepping:** Frame-by-frame navigation (forward/backward)
- **Fast Forward:** Skip ahead at high speed
- **Frame Index:** Built automatically for O(1) random access

#### Setup

```csharp
using Fdp.Kernel.FlightRecorder;

// Load recording
var reader = new RecordingReader("simulation.fdr");
var controller = new PlaybackController(reader, world);

// Build frame index (one-time cost)
controller.BuildIndex();  // Scans recording, creates jump table
```

#### Seeking

```csharp
// Jump to frame 1000
controller.SeekToFrame(1000);

// Jump to specific tick
controller.SeekToTick(1234567890UL);

// Result: World state reflects Frame 1000
```

#### Stepping (Frame-by-Frame Debug)

```csharp
// Step forward one frame
controller.StepForward();

// Step backward one frame
controller.StepBackward();  // Rewinds to last keyframe, replays to target

// Current frame number
int currentFrame = controller.CurrentFrame;
```

#### Fast Forward

```csharp
// Fast forward 100 frames
controller.FastForward(100);

// Fast forward to end
controller.FastForward(controller.TotalFrames - controller.CurrentFrame);
```

#### UI Integration Example

```csharp
public class ReplayDebugger
{
    private PlaybackController _playback;
    private bool _isPaused = true;
    private int _playbackSpeed = 1;  // 1x, 2x, 4x, etc.
    
    public void Update(float deltaTime)
    {
        if (_isPaused)
            return;
        
        // Advance at playback speed
        for (int i = 0; i < _playbackSpeed; i++)
        {
            if (_playback.CurrentFrame < _playback.TotalFrames - 1)
                _playback.StepForward();
        }
    }
    
    public void OnSeekBar(int targetFrame)
    {
        _playback.SeekToFrame(targetFrame);
    }
    
    public void OnStepForward()
    {
        _playback.StepForward();
    }
    
    public void OnStepBackward()
    {
        _playback.StepBackward();
    }
    
    public void OnTogglePause()
    {
        _isPaused = !_isPaused;
    }
    
    public void OnSetSpeed(int speed)
    {
        _playbackSpeed = speed;  // 1x, 2x, 4x
    }
}
```

#### Performance Considerations

- **Seeking Forward:** O(N keyframes) - fast if keyframes are frequent
- **Seeking Backward:** O(N keyframes + M frames) - rewinds to last keyframe, replays forward
- **Frame Index Build:** O(Recording Size) - one-time cost at load
- **Recommendation:** Use keyframes every 60-300 frames for interactive scrubbing

#### Keyframe Strategy

```csharp
var recorder = new RecordingWriter("replay.fdr");

// Configure keyframe frequency
recorder.KeyframeInterval = 120;  // Keyframe every 120 frames

// Trade-off:
// - More keyframes = Faster seeking, Larger file
// - Fewer keyframes = Slower seeking, Smaller file
// Recommended: 60-300 frames (1-5 seconds at 60 FPS)
```

---

### Polymorphic Serialization

When managed components contain **interfaces** or **abstract classes**, the serializer needs type information to deserialize correctly.

#### Problem

```csharp
// Component with interface
public record AIComponent(IAIStrategy Strategy);  // ← Interface!

// Implementation
public class PatrolStrategy : IAIStrategy { ... }
public class AttackStrategy : IAIStrategy { ... }

// Runtime
var entity = world.CreateEntity();
world.AddComponent(entity, new AIComponent(new PatrolStrategy()));

// Serialize → Deserialize
// ❌ ERROR: Serializer doesn't know which concrete type to create!
```

#### Solution: `[FdpPolymorphicType]` Attribute

Tag all concrete implementations with unique IDs:

```csharp
// Define interface
public interface IAIStrategy
{
    void Execute(Entity entity, EntityRepository world);
}

// Tag implementations
[FdpPolymorphicType(1)]  // ← Unique ID
public class PatrolStrategy : IAIStrategy
{
    public Vector3[] WaypointPath { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Patrol logic
    }
}

[FdpPolymorphicType(2)]  // ← Different ID
public class AttackStrategy : IAIStrategy
{
    public Entity Target { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Attack logic
    }
}

[FdpPolymorphicType(3)]
public class FleeStrategy : IAIStrategy
{
    public float FleeDistance { get; init; }
    
    public void Execute(Entity entity, EntityRepository world)
    {
        // Flee logic
    }
}
```

#### Registration (Required)

Before serialization, register all polymorphic types:

```csharp
using Fdp.Kernel.FlightRecorder;

var serializer = new FdpPolymorphicSerializer();

// Register interface + implementations
serializer.RegisterPolymorphicType<IAIStrategy, PatrolStrategy>(1);
serializer.RegisterPolymorphicType<IAIStrategy, AttackStrategy>(2);
serializer.RegisterPolymorphicType<IAIStrategy, FleeStrategy>(3);

// Now serialization works
var recorder = new RecordingWriter("replay.fdr", serializer);
```

#### Abstract Classes

Works the same way:

```csharp
[FdpPolymorphicType(10)]
public abstract class Weapon
{
    public abstract void Fire();
}

[FdpPolymorphicType(11)]
public class Rifle : Weapon
{
    public override void Fire() { /* Rifle logic */ }
}

[FdpPolymorphicType(12)]
public class Shotgun : Weapon
{
    public override void Fire() { /* Shotgun logic */ }
}

// Registration
serializer.RegisterPolymorphicType<Weapon, Rifle>(11);
serializer.RegisterPolymorphicType<Weapon, Shotgun>(12);
```

#### Type ID Rules

1. **Unique per type:** IDs must be unique within the same interface/abstract class
2. **Stable:** Don't change IDs after shipping (breaks old recordings)
3. **Avoid 0:** Reserve 0 for "null" polymorphic references
4. **Range:** 1-65535 (ushort)

#### Error Handling

```csharp
// ❌ Missing [FdpPolymorphicType]
public class NewStrategy : IAIStrategy { }

// Runtime error during serialization:
// InvalidOperationException: Type 'NewStrategy' is not registered as polymorphic
```

**Solution:** Always tag concrete types.

#### Best Practices

1. **Centralize registration:** Register all polymorphic types at startup
2. **Document IDs:** Keep a master list of type IDs in comments
3. **Avoid complex hierarchies:** Deep inheritance + polymorphism = serialization complexity
4. **Prefer composition:** Use strategy pattern with interfaces over deep class hierarchies

---

### Concurrent Collections Support

The `FdpAutoSerializer` has explicit support for thread-safe collections:

**Supported:**
- `List<T>`, `Dictionary<K,V>`
- `ConcurrentDictionary<K,V>` ✅
- `Queue<T>`, `ConcurrentQueue<T>` ✅
- `Stack<T>`, `ConcurrentStack<T>` ✅
- `ConcurrentBag<T>` ✅

**Example:**
```csharp
public record ThreadSafeCache(
    ConcurrentDictionary<int, string> Data
);

// Serialization works automatically
world.AddComponent(entity, new ThreadSafeCache(new ConcurrentDictionary<int, string>()));
```

---

## Extended Flight Recorder Documentation

### Overview

The **Flight Recorder** system captures simulation state changes to disk for **exact replay** and **deterministic validation**. It enables debugging, testing, and proof-of-correctness for complex distributed simulations.

**What Problems Does Flight Recorder Solve:**
- **Debugging:** Replay exact scenario that caused a bug
- **Determinism Validation:** Verify simulation is deterministic (same inputs → same outputs)
- **Audit Trail:** Compliance and proof-of-execution for critical systems
- **Testing:** Capture production scenarios for regression tests

**When to Use Flight Recorder:**
- Debugging non-deterministic bugs
- Validating distributed synchronization
- Compliance requirements (aerospace, medical, financial)
- Automated testing with real scenarios

---

### Core Concepts

#### Recording Modes

**Two recording strategies:**

1. **Keyframe + Deltas:**
   - Frame 0: Full snapshot (keyframe)
   - Frame 1-99: Only changed components (delta)
   - Frame 100: Full snapshot (new keyframe)
   - Repeat

2. **Keyframe-Only:**
   - Every frame: Full snapshot
   - Simpler but larger files

**Default:** Keyframe every 100 frames + deltas (optimal for most use cases).

---

#### File Format

**Binary format (.fdp file):**

```
┌───────────────────────────────────────┐
│ Header                                │
│ - Magic:   "FDPREC" (6 bytes)        │
│ - Version: uint32 (format version)   │
│ - Timestamp: int64 (recording time)  │
└───────────────────────────────────────┘
┌───────────────────────────────────────┐
│ Frame 0 (Keyframe)                    │
│ - FrameType: byte (0 = keyframe)     │
│ - Tick:     uint32                    │
│ - EntityCount: int                    │
│ - Component Data (all entities)       │
│ - Destruction Log                     │
└───────────────────────────────────────┘
┌───────────────────────────────────────┐
│ Frame 1 (Delta)                       │
│ - FrameType: byte (1 = delta)        │
│ - Tick: uint32                        │
│ - ChangedEntityCount: int             │
│ - Component Data (changed only)       │
│ - Destruction Log                     │
└───────────────────────────────────────┘
...
```

---

#### Change Detection

**How the recorder knows what changed:**

```csharp
// When you call:
repository.Tick(); // GlobalVersion++ (e.g., 42 → 43)

// And modify a component:
repository.SetComponent(entity, new Position { X = 10 });
// Component stamped with version 43

// Recorder captures delta:
recorder.CaptureFrame(repository, sinceTick: 42);
// Internally queries: "Components with version > 42"
// Result: Only changed components written
```

**Critical:** Must call `repository.Tick()` every frame for change detection to work!

---

### Usage Examples

#### Example 1: Record and Replay Single Entity

From `FlightRecorderTests.cs` lines 34-75:

```csharp
using Fdp.Kernel;
using Fdp.Kernel.FlightRecorder;

[Fact]
public void RecordAndReplay_SingleEntity_RestoresCorrectly()
{
    string filePath = "test_recording.fdp";
    
    // ===== RECORDING =====
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var entity = recordRepo.CreateEntity();
    recordRepo.AddComponent(entity, new Position { X = 10, Y = 20, Z = 30 });
    
    // Record to file
    using (var recorder = new AsyncRecorder(filePath))
    {
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
    }
    
    // ===== REPLAY =====
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>(); // Must match recording!
    
    using (var reader = new RecordingReader(filePath))
    {
        bool hasFrame = reader.ReadNextFrame(replayRepo);
        Assert.True(hasFrame); // Frame read successfully
    }
    
    // Verify restored state
    Assert.Equal(1, replayRepo.EntityCount);
    
    var query = replayRepo.Query().With<Position>().Build();
    foreach (var e in query)
    {
        ref readonly var pos = ref replayRepo.GetComponentRO<Position>(e);
        Assert.Equal(10f, pos.X);
        Assert.Equal(20f, pos.Y);
        Assert.Equal(30f, pos.Z);
    }
}
```

**Expected Output:**
- Entity created with Position component
- State captured to file
- File replayed into new repository
- Position values match exactly

---

#### Example 2: Record Deltas (Only Changed Entities)

From `FlightRecorderTests.cs` lines 111-159:

```csharp
[Fact]
public void RecordDelta_OnlyChangedEntities_RecordsCorrectly()
{
    string filePath = "test_delta.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var e1 = recordRepo.CreateEntity();
    var e2 = recordRepo.CreateEntity();
    recordRepo.AddComponent(e1, new Position { X = 1, Y = 1, Z = 1 });
    recordRepo.AddComponent(e2, new Position { X = 2, Y = 2, Z = 2 });
    
    using (var recorder = new AsyncRecorder(filePath))
    {
        // Frame 0: Keyframe (both entities)
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
        
        // Frame 1: Modify ONLY e1
        recordRepo.Tick();
        ref var pos = ref recordRepo.GetComponentRW<Position>(e1);
        pos.X = 100; // Only e1 changed!
        
        // Capture delta (only e1 written to file)
        recorder.CaptureFrame(recordRepo, recordRepo.GlobalVersion - 1, blocking: true);
    }
    
    // Replay
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo); // Keyframe: both entities
        reader.ReadNextFrame(replayRepo); // Delta: e1 updated
    }
    
    // Verify: e1 has updated position, e2 unchanged
    var query = replayRepo.Query().With<Position>().Build();
    foreach (var e in query)
    {
        ref readonly var pos = ref replayRepo.GetComponentRO<Position>(e);
        if (e.Index == e1.Index)
        {
            Assert.Equal(100f, pos.X); // Updated!
        }
        else if (e.Index == e2.Index)
        {
            Assert.Equal(2f, pos.X); // Unchanged
        }
    }
}
```

**Performance Benefit:** Delta recording reduces file size by ~90% for typical simulations.

---

#### Example 3: Record Entity Destruction

From `FlightRecorderTests.cs` lines 191-229:

```csharp
[Fact]
public void RecordAndReplay_EntityDestruction_RemovesEntity()
{
    string filePath = "test_destruction.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    
    var e1 = recordRepo.CreateEntity();
    var e2 = recordRepo.CreateEntity();
    recordRepo.AddComponent(e1, new Position { X = 1, Y = 1, Z = 1 });
    recordRepo.AddComponent(e2, new Position { X = 2, Y = 2, Z = 2 });
    
    using (var recorder = new AsyncRecorder(filePath))
    {
        // Frame 0: Keyframe (2 entities)
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
        
        // Frame 1: Destroy e1
        recordRepo.Tick();
        recordRepo.DestroyEntity(e1);
        
        recorder.CaptureFrame(recordRepo, recordRepo.GlobalVersion - 1, blocking: true);
    }
    
    // Replay
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo); // Keyframe: 2 entities
        reader.ReadNextFrame(replayRepo); // Delta: e1 destroyed
    }
    
    // Verify: e1 destroyed, e2 alive
    Assert.Equal(1, replayRepo.EntityCount);
    Assert.False(replayRepo.IsAlive(e1)); // Destroyed!
    Assert.True(replayRepo.IsAlive(e2));  // Still alive
}
```

**Key Insight:** Destruction log is part of frame data - replays are **exact**, including entity lifecycles.

---

#### Example 4: Large-Scale Recording (Performance Test)

From `FlightRecorderTests.cs` lines 727-761:

```csharp
[Fact]
public void RecordKeyframe_LargeEntityCount_CompletesSuccessfully()
{
    string filePath = "test_large.fdp";
    
    using var recordRepo = new EntityRepository();
    recordRepo.RegisterComponent<Position>();
    recordRepo.RegisterComponent<Velocity>();
    
    const int entityCount = 1000;
    
    // Create 1000 entities
    for (int i = 0; i < entityCount; i++)
    {
        var e = recordRepo.CreateEntity();
        recordRepo.AddComponent(e, new Position { X = i, Y = i, Z = i });
        recordRepo.AddComponent(e, new Velocity { X = 1, Y = 1, Z = 1 });
    }
    
    // Record
    using (var recorder = new AsyncRecorder(filePath))
    {
        recordRepo.Tick();
        recorder.CaptureKeyframe(recordRepo);
    }
    
    // Replay and verify
    using var replayRepo = new EntityRepository();
    replayRepo.RegisterComponent<Position>();
    replayRepo.RegisterComponent<Velocity>();
    
    using (var reader = new RecordingReader(filePath))
    {
        reader.ReadNextFrame(replayRepo);
    }
    
    Assert.Equal(entityCount, replayRepo.EntityCount);
}
```

**Performance:** 1000 entities with 2 components each records in <10ms.

---

### API Reference

#### AsyncRecorder Class

```csharp
public class AsyncRecorder : IDisposable
{
    /// <summary>
    /// Create recorder writing to file.
    /// </summary>
    public AsyncRecorder(string filePath);
    
    /// <summary>
    /// Capture full keyframe (all entities).
    /// </summary>
    public void CaptureKeyframe(EntityRepository repository);
    
    /// <summary>
    /// Capture delta frame (only changed entities since sinceTick).
    /// </summary>
    /// <param name="blocking">If true, waits for write before returning</param>
    public void CaptureFrame(EntityRepository repository, uint sinceTick, bool blocking = false);
    
    /// <summary>
    /// Number of successfully recorded frames.
    /// </summary>
    public int RecordedFrames { get; }
    
    /// <summary>
    /// Number of dropped frames (async buffer full).
    /// </summary>
    public int DroppedFrames { get; }
    
    /// <summary>
    /// Flush pending writes and close file.
    /// </summary>
    public void Dispose();
}
```

---

#### RecordingReader Class

```csharp
public class RecordingReader : IDisposable
{
    /// <summary>
    /// Open recording file for replay.
    /// Throws InvalidDataException if file corrupt.
    /// </summary>
    public RecordingReader(string filePath);
    
    /// <summary>
    /// File format version.
    /// </summary>
    public uint FormatVersion { get; }
    
    /// <summary>
    /// Recording timestamp (Unix epoch).
    /// </summary>
    public long RecordingTimestamp { get; }
    
    /// <summary>
    /// Read next frame into repository.
    /// Returns false if end of file reached.
    /// </summary>
    public bool ReadNextFrame(EntityRepository repository);
    
    /// <summary>
    /// Close file.
    /// </summary>
    public void Dispose();
}
```

---

### Best Practices

#### ✅ DO: Call Tick() Every Frame

```csharp
// ✅ GOOD: Tick called before modifications
void Update()
{
    _repository.Tick(); // GlobalVersion++
    
    // Modify components
    SetComponent(entity, new Position { X = 10 });
    // Component stamped with current version
    
    // Record delta
    _recorder.CaptureFrame(_repository, _repository.GlobalVersion - 1);
}

// ❌ BAD: Forgot Tick()
void Update()
{
    // Missing: _repository.Tick();
    
    SetComponent(entity, new Position { X = 10 });
    // Component stamped with STALE version
    
    _recorder.CaptureFrame(...); // Records nothing (no changes detected)!
}
```

---

#### ✅ DO: Use Keyframe + Delta for Production

```csharp
// ✅ GOOD: Hybrid recording
for (int frame = 0; frame < 1000; frame++)
{
    _repository.Tick();
    
    if (frame % 100 == 0)
    {
        _recorder.CaptureKeyframe(_repository); // Every 100 frames
    }
    else
    {
        _recorder.CaptureFrame(_repository, sinceTick: frame - 1);
    }
}

// Result: 90% smaller file, fast seeking (jump to nearest keyframe)
```

---

#### ✅ DO: Sanitize Dead Entities Before Recording

The Flight Recorder automatically **sanitizes** dead entities (zeros their memory) to prevent leaking deleted data into recordings.

**Why This Matters:**
```csharp
// Frame 1: Create entity with secret data
var entity = repo.CreateEntity();
repo.AddComponent(entity, new SecretData { Password = "hunter2" });

// Frame 2: Destroy entity
repo.DestroyEntity(entity);

// Without sanitization:
// - Entity data still in memory
// - Recorded to file
// - Replay shows deleted secrets!

// With sanitization (automatic):
// - Destroy marks entity dead
// - Recorder zeros the memory slot
// - Recording contains only zeros
// - Replays are clean
```

This is handled automatically by the recorder.

---

#### ⚠️ DON'T: Forget to Register Components Before Replay

```csharp
// ❌ BAD: Component not registered
using var replayRepo = new EntityRepository();
// Missing: replayRepo.RegisterComponent<Position>();

using var reader = new RecordingReader("recording.fdp");
reader.ReadNextFrame(replayRepo); // EXCEPTION - component type unknown!

// ✅ GOOD: Register all components
using var replayRepo = new EntityRepository();
replayRepo.RegisterComponent<Position>();
replayRepo.RegisterComponent<Velocity>();

using var reader = new RecordingReader("recording.fdp");
reader.ReadNextFrame(replayRepo); // Works!
```

---

#### ⚠️ DON'T: Use Managed Components with Mutable State

```csharp
// ❌ BAD: Mutable managed component
public class BadAIState
{
    public List<Vector3> Waypoints; // Mutable!
}

// Recording:
var state = new BadAIState { Waypoints = new() { vec1, vec2 } };
repo.AddManagedComponent(entity, state);
// Shallow copy to snapshot
// Later, code modifies state.Waypoints
// Replay is corrupted!

// ✅ GOOD: Immutable record
public record GoodAIState
{
    public required ImmutableList<Vector3> Waypoints { get; init; }
}

// Recording:
var state = new GoodAIState { Waypoints = ImmutableList.Create(vec1, vec2) };
repo.AddManagedComponent(entity, state);
// Shallow copy is safe (immutable)
// Replay is exact!
```

---

### Troubleshooting

#### Problem: InvalidDataException on ReadNextFrame

**Symptoms:**
```
InvalidDataException: Invalid magic bytes. Expected 'FDPREC', got '...'
```

**Cause:** File corrupted or not a valid FDP recording.

**Solution:**
```csharp
// Validate file before reading
try
{
    using var reader = new RecordingReader(filePath);
    Console.WriteLine($"Format version: {reader.FormatVersion}");
    Console.WriteLine($"Recorded: {DateTimeOffset.FromUnixTimeSeconds(reader.RecordingTimestamp)}");
}
catch (InvalidDataException ex)
{
    Console.Error($"Invalid recording file: {ex.Message}");
}
```

---

#### Problem: Replay Diverges from Original

**Symptoms:**
- Replay starts identically but diverges after N frames
- Position values differ by small amounts

**Causes:**
1. **Non-determinism:** Random number generator, DateTime.Now, etc.
2. **Missing Tick():** Change detection broken
3. **Floating-point precision:** Different CPU architectures

**Solutions:**

**1. Use Deterministic Random:**
```csharp
// ❌ BAD: Non-deterministic
float angle = Random.Shared.NextSingle(); // Different every replay!

// ✅ GOOD: Seeded random
var rng = new Random(seed: 42);
float angle = rng.NextSingle(); // Same every replay
```

**2. Verify Tick() Called:**
```csharp
void Update()
{
    _repository.Tick(); // MUST call!
    // ... simulation ...
}
```

**3. Accept Floating-Point Variance:**
```csharp
// ✅ GOOD: Epsilon comparison
const float epsilon = 0.0001f;
bool positionsMatch = Math.Abs(recorded.X - replayed.X) < epsilon;
```

---

#### Problem: RecordedFrames vs DroppedFrames Mismatch

**Symptoms:**
```
Warning: Recorder reported 95 frames recorded, 5 frames dropped
```

**Cause:** Async recorder buffer full (recording thread slower than game thread).

**Solutions:**

**1. Use Blocking Mode:**
```csharp
// Frame will wait for write to complete
_recorder.CaptureFrame(_repository, sinceTick, blocking: true);
```

**2. Reduce Frame Rate:**
```csharp
// Record every 2nd frame instead of every frame
if (_frameCount % 2 == 0)
{
    _recorder.CaptureFrame(...);
}
```

**3. Use Faster Storage (SSD vs HDD):**
- Async mode can drop frames on slow HDDs
- SSDs typically have no drops

---

### Performance Characteristics

#### Recording Overhead

| Operation | Time (1000 entities) | File Size |
|-----------|----------------------|-----------|
| **Keyframe** | 5-10ms | ~500 KB |
| **Delta (10% changed)** | 0.5-1ms | ~50 KB |
| **Delta (50% changed)** | 2-3ms | ~250 KB |

**Optimizations:**
- Binary format (no JSON overhead)
- Delta compression (only changes)
- Async writes (doesn't block game thread)
- Sparse entity support (skips empty chunks)

---

#### File Size Examples

**1000 frames, 100 entities, 2 components:**
- Keyframe-only: ~50 MB
- Keyframe + Delta (every 100 frames): ~5 MB (10× smaller!)

**1000 frames, 10,000 entities, 5 components:**
- Keyframe-only: ~5 GB
- Keyframe + Delta: ~500 MB (10× smaller!)

---
