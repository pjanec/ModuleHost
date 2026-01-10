# BATCH-CK-12: Parallel Execution & Performance Optimization

**Batch ID:** BATCH-CK-12  
**Phase:** Performance - Parallel Kinematics & Benchmarking  
**Priority:** HIGH (P1) - Critical for 50k vehicle target  
**Estimated Effort:** 1.0 day  
**Dependencies:** None  
**Starting Point:** Current main branch  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading & Workflow

**IMPORTANT: Read these documents before starting:**

### Developer Workflow
- **Workflow Guide**: `d:\Work\ModuleHost\.dev-workstream\README.md`
  - How to work, report, ask questions
  - Definition of Done
  - Communication standards

### Design & Architecture
- **Design Document**: `d:\Work\ModuleHost\docs\car-kinem-implementation-design.md`
  - Full architecture specification
  - Performance requirements (50k @ 60Hz)
  - Parallel execution design

### Source Code Locations
- **CarKinem Core**: `Fdp.Examples.CarKinem\CarKinem\`
  - Systems: `CarKinem\Systems\CarKinematicsSystem.cs`
  - Spatial: `CarKinem\Spatial\`
- **Tests**: `Fdp.Examples.CarKinem\CarKinem.Tests\`
- **Benchmarks**: `ModuleHost.Benchmarks\` (create if needed)

### Reporting Requirements
**When complete, submit:**
- **Report**: `d:\Work\ModuleHost\.dev-workstream\reports\BATCH-CK-12-REPORT.md`
  - Use template: `.dev-workstream\templates\BATCH-REPORT-TEMPLATE.md`
  - **MUST include**: Benchmark results, performance analysis
- **Questions** (if needed): `.dev-workstream\reports\BATCH-CK-12-QUESTIONS.md`
- **Blockers** (if blocked): `.dev-workstream\reports\BATCH-CK-12-BLOCKERS.md`

---

## 📚 Context & Problem Statement

### The Issue

The `CarKinematicsSystem` currently processes vehicles **serially** in a foreach loop, using a deprecated API that generates compiler warnings. The design specification calls for **parallel execution** to meet the 50,000 vehicles @ 60Hz performance target.

**Current Implementation (CarKinematicsSystem.cs:55):**
```csharp
// Serial update to ensure proper versioning (Parallel interferes with Recorder stamps)
query.ForEach((entity) =>  // ⚠️ Deprecated API
{
    UpdateVehicle(entity, dt, spatialGrid);
});
```

**Compiler Warning:**
```
warning CS0618: 'EntityQuery.ForEach(Action<Entity>)' is obsolete: 
'Use foreach loop for zero allocation. query.ForEach allocates closures.'
```

**Design Specification:**
```csharp
// From docs/car-kinem-implementation-design.md:
var entities = query.ToEntityArray();
Parallel.ForEach(entities, (e) => {
    // Process vehicle physics...
});
```

**Performance Budget (from design):**
- 50,000 vehicles @ 60Hz = 833 μs per frame total
- CarKinematicsSystem budget: **600 μs** (72% of total frame time)
- **Serial execution cannot meet this target**

---

## 🎯 Goal

Implement parallel execution in CarKinematicsSystem and validate performance at scale:

1. **Replace deprecated ForEach** with modern foreach loop
2. **Enable parallel execution** using `Parallel.ForEach`
3. **Ensure thread safety** (read-only spatial grid, isolated entity updates)
4. **Create 50k vehicle benchmark** to validate performance targets
5. **Profile GC allocations** and ensure zero allocations on hot path

### Success Criteria

✅ **No compiler warnings:**
```bash
dotnet build CarKinem/CarKinem.csproj --nologo
# Expected: 0 Warning(s)
```

✅ **Parallel execution works:**
```csharp
Parallel.ForEach(entities, entity =>
{
    UpdateVehicle(entity, dt, spatialGrid);
});
```

✅ **50k vehicles @ 60Hz:**
```
Benchmark: 50,000 vehicles
Average frame time: < 16.67 ms (60 FPS)
GC allocations: 0 bytes (steady state)
```

---

## 📋 Implementation Tasks

### **Task 1: Replace Deprecated ForEach API** ⭐⭐

**Objective:** Fix compiler warning and use zero-allocation foreach.

**File to Modify:** `CarKinem/Systems/CarKinematicsSystem.cs`

**Current (DEPRECATED):**
```csharp
protected override void OnUpdate()
{
    // ... setup ...
    
    // Serial update to ensure proper versioning
    query.ForEach((entity) =>
    {
        UpdateVehicle(entity, dt, spatialGrid);
    });
}
```

**New (ZERO-ALLOCATION):**
```csharp
protected override void OnUpdate()
{
    float dt = DeltaTime;
    
    if (!World.HasSingleton<SpatialGridData>()) return;
    
    var gridData = World.GetSingleton<SpatialGridData>();
    var spatialGrid = gridData.Grid;
    
    var query = World.Query()
        .With<VehicleState>()
        .With<VehicleParams>()
        .With<NavState>()
        .Build();
    
    // Zero-allocation foreach
    foreach (var entity in query)
    {
        UpdateVehicle(entity, dt, spatialGrid);
    }
}
```

**Deliverables:**
- [ ] Replace `query.ForEach(...)` with `foreach (var entity in query)`
- [ ] Remove closure allocation
- [ ] Verify build has 0 warnings

---

### **Task 2: Enable Parallel Execution** ⭐⭐⭐⭐

**Objective:** Parallelize vehicle updates using `Parallel.ForEach`.

**File to Modify:** `CarKinem/Systems/CarKinematicsSystem.cs`

**Design Consideration: Thread Safety**

**Read-Only Accesses (Thread-Safe):**
- `SpatialHashGrid` (built in previous system, immutable during kinematics)
- `RoadNetworkBlob` (static data)
- `TrajectoryPoolManager` (thread-safe dictionary with locks)
- `VehicleParams` (read-only for each vehicle)

**Write Accesses (Must Be Isolated):**
- `VehicleState` component (each thread writes to unique entity)
- `NavState` component (each thread writes to unique entity)
- `FormationTarget` component (each thread writes to unique entity)

**Thread-Safe Implementation:**
```csharp
protected override void OnUpdate()
{
    float dt = DeltaTime;
    
    if (!World.HasSingleton<SpatialGridData>()) return;
    
    var gridData = World.GetSingleton<SpatialGridData>();
    var spatialGrid = gridData.Grid;
    
    var query = World.Query()
        .With<VehicleState>()
        .With<VehicleParams>()
        .With<NavState>()
        .Build();
    
    // Option 1: Direct parallel (if EntityQuery supports IEnumerable)
    Parallel.ForEach(query, entity =>
    {
        UpdateVehicle(entity, dt, spatialGrid);
    });
    
    // Option 2: Convert to array first (if query is not thread-safe)
    // var entities = query.ToArray();  // One allocation per frame
    // Parallel.ForEach(entities, entity =>
    // {
    //     UpdateVehicle(entity, dt, spatialGrid);
    // });
}
```

**IMPORTANT: Check EntityQuery Thread Safety**

Before implementing, verify if `EntityQuery` enumeration is thread-safe. If not, use Option 2.

**Deliverables:**
- [ ] Implement `Parallel.ForEach` for vehicle updates
- [ ] Document thread safety assumptions in comments
- [ ] Test with 1000+ vehicles to verify correctness

---

### **Task 3: Verify Thread Safety in UpdateVehicle** ⭐⭐⭐

**Objective:** Audit `UpdateVehicle()` for thread-safety issues.

**File to Audit:** `CarKinem/Systems/CarKinematicsSystem.cs`

**Potential Issues to Check:**

1. **Shared State Access:**
   - `_roadNetwork` (read-only) ✅
   - `_trajectoryPool` (has locks) ✅
   - No mutable static fields ✅

2. **World Access Patterns:**
   - `World.GetComponent<T>(entity)` - Read (thread-safe if no mutation)
   - `World.SetComponent<T>(entity, value)` - Write (must be isolated per entity)
   - `World.HasComponent<T>(entity)` - Read (thread-safe)

3. **Neighbor Query in ApplyCollisionAvoidance:**
```csharp
// Current code (line 205):
var neighborEntity = World.GetEntity(entityId);
if (!neighborEntity.IsNull && World.HasComponent<VehicleState>(neighborEntity))
{
    var neighborState = World.GetComponent<VehicleState>(neighborEntity);
    // ...
}
```

**Potential Race Condition:** Multiple threads reading same neighbor's `VehicleState` is safe (read-only). ✅

**Deliverables:**
- [ ] Audit `UpdateVehicle()` method
- [ ] Audit `ApplyCollisionAvoidance()` method
- [ ] Document any race conditions found
- [ ] Add `// THREAD-SAFE: ...` comments explaining safety

---

### **Task 4: Add Performance Logging** ⭐⭐

**Objective:** Add optional performance metrics to CarKinematicsSystem.

**File to Modify:** `CarKinem/Systems/CarKinematicsSystem.cs`

**Add performance tracking:**
```csharp
public class CarKinematicsSystem : ComponentSystem
{
    // ... existing fields ...
    
    private System.Diagnostics.Stopwatch? _perfStopwatch;
    private double _totalUpdateTime = 0;
    private int _updateCount = 0;
    
    public bool EnablePerformanceLogging { get; set; } = false;
    
    protected override void OnUpdate()
    {
        _perfStopwatch?.Restart();
        
        float dt = DeltaTime;
        
        // ... existing update logic ...
        
        if (EnablePerformanceLogging && _perfStopwatch != null)
        {
            _perfStopwatch.Stop();
            _totalUpdateTime += _perfStopwatch.Elapsed.TotalMilliseconds;
            _updateCount++;
            
            if (_updateCount % 60 == 0)  // Log every 60 frames
            {
                double avgMs = _totalUpdateTime / _updateCount;
                int vehicleCount = World.Query()
                    .With<VehicleState>()
                    .Build()
                    .Count();
                
                Console.WriteLine($"[CarKinematics] Avg: {avgMs:F2} ms, Vehicles: {vehicleCount}, μs/vehicle: {(avgMs * 1000 / vehicleCount):F2}");
            }
        }
    }
    
    protected override void OnCreate()
    {
        base.OnCreate();
        if (EnablePerformanceLogging)
            _perfStopwatch = new System.Diagnostics.Stopwatch();
    }
}
```

**Deliverables:**
- [ ] Add optional performance logging
- [ ] Report average frame time, vehicle count, μs per vehicle
- [ ] Disable by default (opt-in via property)

---

### **Task 5: Create 50k Vehicle Benchmark** ⭐⭐⭐⭐

**Objective:** Validate performance at design scale.

**File to Create:** `ModuleHost.Benchmarks/CarKinemPerformance.cs`

```csharp
using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CarKinem.Core;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Kernel;

namespace ModuleHost.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, iterationCount: 10)]
    public class CarKinemPerformance
    {
        private EntityRepository _repo;
        private SpatialHashSystem _spatialSystem;
        private CarKinematicsSystem _kinematicsSystem;
        private RoadNetworkBlob _roadNetwork;
        private TrajectoryPoolManager _trajectoryPool;
        
        [Params(1000, 10000, 50000)]
        public int VehicleCount;
        
        [GlobalSetup]
        public void Setup()
        {
            _repo = new EntityRepository();
            _repo.RegisterComponent<VehicleState>();
            _repo.RegisterComponent<VehicleParams>();
            _repo.RegisterComponent<NavState>();
            _repo.RegisterComponent<SpatialGridData>();
            
            // Register GlobalTime
            _repo.RegisterComponent<GlobalTime>();
            _repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.016f, TimeScale = 1.0f });
            
            // Create minimal road network
            _roadNetwork = new RoadNetworkBuilder().Build(5f, 100, 100);
            _trajectoryPool = new TrajectoryPoolManager();
            
            // Create systems
            _spatialSystem = new SpatialHashSystem();
            _kinematicsSystem = new CarKinematicsSystem(_roadNetwork, _trajectoryPool);
            
            _spatialSystem.Create(_repo);
            _kinematicsSystem.Create(_repo);
            
            // Spawn vehicles in grid pattern
            var random = new Random(42);
            for (int i = 0; i < VehicleCount; i++)
            {
                var entity = _repo.CreateEntity();
                
                // Grid distribution
                int gridSize = (int)Math.Ceiling(Math.Sqrt(VehicleCount));
                int x = (i % gridSize) * 20;
                int y = (i / gridSize) * 20;
                
                _repo.AddComponent(entity, new VehicleState
                {
                    Position = new Vector2(x, y),
                    Forward = new Vector2(1, 0),
                    Speed = (float)(random.NextDouble() * 20 + 5)  // 5-25 m/s
                });
                
                _repo.AddComponent(entity, new VehicleParams
                {
                    WheelBase = 2.7f,
                    MaxSpeedFwd = 30f,
                    MaxAccel = 3f,
                    MaxDecel = 6f,
                    MaxSteerAngle = 0.6f,
                    LookaheadTimeMin = 2f,
                    LookaheadTimeMax = 10f,
                    AccelGain = 2.0f,
                    AvoidanceRadius = 2.5f
                });
                
                _repo.AddComponent(entity, new NavState
                {
                    Mode = NavigationMode.None
                });
            }
        }
        
        [Benchmark]
        public void UpdateKinematics()
        {
            _spatialSystem.Run();
            _kinematicsSystem.Run();
        }
        
        [GlobalCleanup]
        public void Cleanup()
        {
            _spatialSystem.Dispose();
            _kinematicsSystem.Dispose();
            _roadNetwork.Dispose();
            _trajectoryPool.Dispose();
            _repo.Dispose();
        }
    }
    
    public class Program
    {
        public static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<CarKinemPerformance>();
            
            // Print summary
            Console.WriteLine("\n========== PERFORMANCE TARGETS ==========");
            Console.WriteLine("Design Goal: 50,000 vehicles @ 60Hz = 16.67 ms per frame");
            Console.WriteLine("CarKinematics Budget: 600 μs");
            Console.WriteLine("Total Budget: 833 μs (excluding render)");
            Console.WriteLine("=========================================\n");
        }
    }
}
```

**Deliverables:**
- [ ] Create `CarKinemPerformance.cs` benchmark
- [ ] Test with 1k, 10k, 50k vehicles
- [ ] Report frame time and memory allocations
- [ ] Compare against 16.67 ms target (60 FPS)

---

### **Task 6: Run Benchmark & Analyze Results** ⭐⭐⭐

**Objective:** Validate performance targets and identify bottlenecks.

**Commands:**
```bash
cd ModuleHost.Benchmarks
dotnet run -c Release --framework net8.0
```

**Expected Output:**
```
| Method          | VehicleCount | Mean        | Error     | StdDev    | Gen0   | Allocated |
|---------------- |------------- |------------:|----------:|----------:|-------:|----------:|
| UpdateKinematics| 1000         | 0.XXX ms    | X.XXX ms  | X.XXX ms  | -      | X KB      |
| UpdateKinematics| 10000        | X.XXX ms    | X.XXX ms  | X.XXX ms  | -      | X KB      |
| UpdateKinematics| 50000        | XX.XX ms    | X.XXX ms  | X.XXX ms  | -      | X KB      |
```

**Analysis Checklist:**
- [ ] Is 50k frame time < 16.67 ms? (Target: YES)
- [ ] Are allocations near zero in steady state? (Target: < 1 KB)
- [ ] Does parallel execution scale linearly with CPU cores?
- [ ] Identify top 3 hotspots with profiler (dotTrace, PerfView)

**Deliverables:**
- [ ] Run benchmark on release build
- [ ] Document results in `benchmark_results_carkinem.txt`
- [ ] Create performance report with analysis

---

## 🧪 Testing Strategy

### **Task 7: Parallel Correctness Test** ⭐⭐⭐

**Objective:** Verify parallel execution produces same results as serial.

**File to Create:** `CarKinem.Tests/Systems/ParallelCorrectnessTests.cs`

```csharp
using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Systems;
using CarKinem.Trajectory;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Systems
{
    public class ParallelCorrectnessTests
    {
        [Fact]
        public void ParallelExecution_ProducesSameResults_AsSerial()
        {
            // Setup two identical repos
            var repoSerial = CreateTestRepo(100);
            var repoParallel = CreateTestRepo(100);
            
            // Run 10 frames on both (serial disabled for test)
            for (int frame = 0; frame < 10; frame++)
            {
                // Update both
                repoSerial.Tick();
                repoParallel.Tick();
            }
            
            // Compare final states
            var querySerial = repoSerial.Query().With<VehicleState>().Build();
            var queryParallel = repoParallel.Query().With<VehicleState>().Build();
            
            var serialStates = querySerial.ToArray();
            var parallelStates = queryParallel.ToArray();
            
            Assert.Equal(serialStates.Length, parallelStates.Length);
            
            for (int i = 0; i < serialStates.Length; i++)
            {
                var stateSerial = repoSerial.GetComponent<VehicleState>(serialStates[i]);
                var stateParallel = repoParallel.GetComponent<VehicleState>(parallelStates[i]);
                
                // Positions should match within float precision
                float posDiff = Vector2.Distance(stateSerial.Position, stateParallel.Position);
                Assert.True(posDiff < 0.001f, 
                    $"Position mismatch: {stateSerial.Position} vs {stateParallel.Position}");
                
                float speedDiff = Math.Abs(stateSerial.Speed - stateParallel.Speed);
                Assert.True(speedDiff < 0.001f,
                    $"Speed mismatch: {stateSerial.Speed} vs {stateParallel.Speed}");
            }
            
            repoSerial.Dispose();
            repoParallel.Dispose();
        }
        
        private EntityRepository CreateTestRepo(int vehicleCount)
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<SpatialGridData>();
            repo.RegisterComponent<GlobalTime>();
            repo.SetSingletonUnmanaged(new GlobalTime { DeltaTime = 0.1f, TimeScale = 1.0f });
            
            // Spawn vehicles
            var random = new Random(42);  // Deterministic seed
            for (int i = 0; i < vehicleCount; i++)
            {
                var entity = repo.CreateEntity();
                repo.AddComponent(entity, new VehicleState 
                { 
                    Position = new Vector2(i * 10, i * 10),
                    Forward = new Vector2(1, 0),
                    Speed = 10f
                });
                repo.AddComponent(entity, new VehicleParams 
                { 
                    WheelBase = 2.7f, 
                    MaxSpeedFwd = 30f,
                    MaxAccel = 3f,
                    MaxDecel = 6f,
                    MaxSteerAngle = 0.6f,
                    LookaheadTimeMin = 2f,
                    LookaheadTimeMax = 10f,
                    AccelGain = 2.0f,
                    AvoidanceRadius = 2.5f
                });
                repo.AddComponent(entity, new NavState { Mode = NavigationMode.None });
            }
            
            return repo;
        }
    }
}
```

**Deliverables:**
- [ ] Create parallel correctness test
- [ ] Verify results match serial execution
- [ ] Test with 100+ vehicles

---

## ✅ Validation Criteria

### Build Verification
```powershell
dotnet build CarKinem/CarKinem.csproj --nologo
# Expected: 0 Warning(s), 0 Error(s)
```

### Benchmark Results
```powershell
cd ModuleHost.Benchmarks
dotnet run -c Release
# Expected: 50k vehicles < 16.67 ms per frame
```

### Test Verification
```powershell
dotnet test CarKinem.Tests/CarKinem.Tests.csproj --filter "Parallel" --nologo
# Expected: All tests passed
```

---

## 🎓 Developer Notes

### Parallel Execution Safety

**Thread-Safe Reads:**
- `SpatialHashGrid`: Built before CarKinematicsSystem, immutable during update
- `RoadNetworkBlob`: Static data loaded at startup
- `TrajectoryPoolManager`: Has internal locks for dictionary access

**Isolated Writes:**
- Each thread processes unique entities
- No shared mutable state between threads
- Entity component writes are isolated by entity index

### Performance Targets

| Metric | Target | Current | Status |
|--------|--------|---------|--------|
| 50k vehicles frame time | < 16.67 ms | TBD | ⏳ |
| Allocations (steady state) | 0 bytes | TBD | ⏳ |
| CPU scaling | Near-linear | TBD | ⏳ |

### Optimization Levers (If Needed)

1. **SIMD Vectorization**: Use Vector2 operations more aggressively
2. **Spatial Hash LOD**: Reduce avoidance checks for distant vehicles
3. **Formation Update Throttling**: Update formations every N frames
4. **Chunked Parallel**: Process in chunks to reduce scheduling overhead

---

## 🚀 Completion Checklist

### Implementation
- [ ] Task 1: Replace deprecated ForEach
- [ ] Task 2: Enable parallel execution
- [ ] Task 3: Verify thread safety
- [ ] Task 4: Add performance logging
- [ ] Task 5: Create 50k vehicle benchmark
- [ ] Task 6: Run benchmark & analyze

### Testing
- [ ] Task 7: Parallel correctness test

### Final Validation
- [ ] Build clean (0 warnings)
- [ ] All tests pass
- [ ] Benchmark meets 60 FPS target at 50k vehicles
- [ ] Zero allocations confirmed

---

## 📊 Success Metrics

**Before BATCH-CK-12:**
```
⚠️ Compiler Warning: CS0618 (ForEach deprecated)
🐌 Serial Execution: Cannot scale to 50k vehicles
❓ No benchmark data
```

**After BATCH-CK-12:**
```
✅ 0 Compiler Warnings
⚡ Parallel Execution: Utilizes all CPU cores
📊 Benchmark: 50k vehicles @ 60+ FPS
🎯 Performance Validated: Meets design targets
```

---

**END OF BATCH-CK-12 INSTRUCTIONS**
