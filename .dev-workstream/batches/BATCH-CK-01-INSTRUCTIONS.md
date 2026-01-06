# BATCH-CK-01: Foundation - Core Data Structures

**Batch ID:** BATCH-CK-01  
**Phase:** Foundation  
**Assigned:** 2026-01-06  
**Developer:** [Your capable developer]  

---

## 📋 Objectives

Establish the project foundation for the Car Kinematics module by:
1. Setting up the library project structure
2. Defining all core data structures (Tier 1 unmanaged components)
3. Validating blittability and memory layout
4. Achieving 100% test coverage of data structures

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`

---

## 📁 Project Structure

Create the following project structure:

```
D:\WORK\ModuleHost\
├── CarKinem\
│   ├── CarKinem.csproj
│   ├── Core\
│   │   ├── VehicleState.cs
│   │   ├── VehicleParams.cs
│   │   └── NavState.cs
│   ├── Formation\
│   │   ├── FormationEnums.cs
│   │   ├── FormationParams.cs
│   │   ├── FormationRoster.cs
│   │   ├── FormationMember.cs
│   │   ├── FormationTarget.cs
│   │   └── FormationSlot.cs
│   ├── Trajectory\
│   │   ├── TrajectoryWaypoint.cs
│   │   └── CustomTrajectory.cs
│   └── Road\
│       ├── RoadSegment.cs
│       ├── RoadNode.cs
│       └── RoadNetworkBlob.cs
└── CarKinem.Tests\
    ├── CarKinem.Tests.csproj
    └── DataStructures\
        ├── VehicleComponentsTests.cs
        ├── FormationComponentsTests.cs
        ├── TrajectoryComponentsTests.cs
        └── RoadComponentsTests.cs
```

---

## 🎯 Tasks

### Task CK-01-01: Project Setup ✅

**Already completed** (infrastructure exists). Verify:
- [ ] `CarKinem.csproj` exists and targets `net8.0`
- [ ] References `Fdp.Kernel` project
- [ ] `CarKinem.Tests.csproj` exists with xUnit reference
- [ ] Both projects build successfully: `dotnet build CarKinem.sln`

**If projects don't exist, create them:**

```xml
<!-- CarKinem.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\FDP\Fdp.Kernel\Fdp.Kernel.csproj" />
  </ItemGroup>
</Project>

<!-- CarKinem.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.6.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\CarKinem\CarKinem.csproj" />
  </ItemGroup>
</Project>
```

---

### Task CK-01-02: Core Enumerations

**File:** `CarKinem/Core/NavigationEnums.cs`

Implement all enums from the design (Section: Data Structures):

```csharp
namespace CarKinem.Core
{
    /// Navigation mode (lines 107-116 in design doc)
    public enum NavigationMode : byte
    {
        None = 0,
        RoadGraph = 1,
        CustomTrajectory = 2,
        Formation = 3
    }
    
    /// Road graph phases (lines 119-126 in design doc)
    public enum RoadGraphPhase : byte
    {
        Approaching = 0,
        Following = 1,
        Leaving = 2,
        Arrived = 3
    }
}
```

**File:** `CarKinem/Formation/FormationEnums.cs`

```csharp
namespace CarKinem.Formation
{
    public enum FormationType : byte { ... }
    public enum FormationMemberState : byte { ... }
    // See design doc lines 172-180, 193-200
}
```

**Verification Tests:**

```csharp
[Fact]
public void NavigationMode_IsOneByte()
{
    Assert.Equal(1, sizeof(NavigationMode));
}

[Fact]
public void AllEnumValues_AreValid()
{
    // Verify all enum values are defined and unique
}
```

---

### Task CK-01-03: Vehicle Components

**File:** `CarKinem/Core/VehicleState.cs`

Implement struct from design doc (lines 60-74):

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace CarKinem.Core
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VehicleState
    {
        public Vector2 Position;
        public Vector2 Forward;
        public float Speed;
        public float SteerAngle;
        public float Accel;
        public float Pitch;
        public float Roll;
        public int CurrentLaneIndex;
    }
}
```

**File:** `CarKinem/Core/VehicleParams.cs` (lines 76-102 in design)

**File:** `CarKinem/Core/NavState.cs` (lines 129-157 in design)

**Verification Tests:**

```csharp
[Fact]
public void VehicleState_IsBlittable()
{
    // Use Marshal.SizeOf or RuntimeHelpers to verify
    Assert.True(IsBlittable<VehicleState>());
}

[Fact]
public void VehicleState_HasExpectedSize()
{
    int expected = sizeof(Vector2) * 2 + sizeof(float) * 5 + sizeof(int);
    Assert.Equal(expected, Marshal.SizeOf<VehicleState>());
}

[Fact]
public void VehicleState_DefaultValues_AreCorrect()
{
    var state = new VehicleState();
    Assert.Equal(Vector2.Zero, state.Position);
    Assert.Equal(0f, state.Speed);
}

private static bool IsBlittable<T>() where T : struct
{
    try
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.FreeHGlobal(ptr);
        return true;
    }
    catch { return false; }
}
```

---

### Task CK-01-04: Formation Components

**Files:**
- `CarKinem/Formation/FormationParams.cs` (lines 203-211 in design)
- `CarKinem/Formation/FormationRoster.cs` (lines 224-236 in design) - **UNSAFE FIXED ARRAYS**
- `CarKinem/Formation/FormationMember.cs` (lines 238-249 in design)
- `CarKinem/Formation/FormationTarget.cs` (lines 252-259 in design)
- `CarKinem/Formation/FormationSlot.cs` (lines 262-266 in design)

**CRITICAL for FormationRoster:**

```csharp
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FormationRoster
{
    public int Count;
    public int TemplateId;
    public FormationType Type;
    public FormationParams Params;
    
    public fixed int MemberEntityIds[16];   // UNSAFE FIXED
    public fixed ushort SlotIndices[16];    // UNSAFE FIXED
}
```

**Verification Tests:**

```csharp
[Fact]
public unsafe void FormationRoster_FixedArrays_AreAccessible()
{
    var roster = new FormationRoster();
    roster.Count = 2;
    
    // Test fixed array access
    fixed (int* ptr = roster.MemberEntityIds)
    {
        ptr[0] = 100;
        ptr[1] = 101;
        Assert.Equal(100, ptr[0]);
        Assert.Equal(101, ptr[1]);
    }
}

[Fact]
public void FormationRoster_MaxCapacity_Is16()
{
    // Verify the design constraint
    Assert.Equal(16, GetFixedArraySize<FormationRoster>("MemberEntityIds"));
}
```

---

### Task CK-01-05: Trajectory Components

**Files:**
- `CarKinem/Trajectory/TrajectoryWaypoint.cs` (lines 387-395 in design)
- `CarKinem/Trajectory/CustomTrajectory.cs` (lines 397-405 in design)

**Note:** `CustomTrajectory` contains `NativeArray<TrajectoryWaypoint>` which is NOT blittable. This is correct - it's a managed container.

**Verification Tests:**

```csharp
[Fact]
public void TrajectoryWaypoint_IsBlittable()
{
    Assert.True(IsBlittable<TrajectoryWaypoint>());
}

[Fact]
public void TrajectoryWaypoint_CumulativeDistance_Calculation()
{
    var wp1 = new TrajectoryWaypoint 
    { 
        Position = new Vector2(0, 0), 
        CumulativeDistance = 0 
    };
    var wp2 = new TrajectoryWaypoint 
    { 
        Position = new Vector2(100, 0), 
        CumulativeDistance = 100 
    };
    
    float expected = Vector2.Distance(wp1.Position, wp2.Position);
    Assert.Equal(expected, wp2.CumulativeDistance, precision: 2);
}
```

---

### Task CK-01-06: Road Network Components

**Files:**
- `CarKinem/Road/RoadSegment.cs` (lines 271-289 in design) - **UNSAFE FIXED LUT**
- `CarKinem/Road/RoadNode.cs` (lines 291-296 in design)
- `CarKinem/Road/RoadNetworkBlob.cs` (lines 298-320 in design) - **DISPOSE PATTERN**

**CRITICAL for RoadSegment:**

```csharp
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RoadSegment
{
    public Vector2 P0, T0, P1, T1;
    public float Length;
    public float SpeedLimit;
    public float LaneWidth;
    public int LaneCount;
    public int StartNodeIndex;
    public int EndNodeIndex;
    
    public fixed float DistanceLUT[8];  // UNSAFE FIXED
}
```

**Verification Tests:**

```csharp
[Fact]
public unsafe void RoadSegment_DistanceLUT_HasCorrectSize()
{
    Assert.Equal(8, GetFixedArraySize<RoadSegment>("DistanceLUT"));
}

[Fact]
public void RoadNetworkBlob_Dispose_ReleasesResources()
{
    var blob = new RoadNetworkBlob
    {
        Segments = new NativeArray<RoadSegment>(10, Allocator.Persistent)
    };
    
    Assert.True(blob.Segments.IsCreated);
    blob.Dispose();
    Assert.False(blob.Segments.IsCreated);
}

[Fact]
public void RoadNetworkBlob_DoubleDispose_DoesNotThrow()
{
    var blob = new RoadNetworkBlob();
    blob.Dispose();
    blob.Dispose(); // Should not throw
}
```

---

## ✅ Acceptance Criteria

Before submitting your report, verify ALL of the following:

### Build & Warnings
- [ ] `dotnet build CarKinem/CarKinem.csproj` succeeds with **zero warnings**
- [ ] `dotnet build CarKinem.Tests/CarKinem.Tests.csproj` succeeds with **zero warnings**

### Test Coverage
- [ ] `dotnet test CarKinem.Tests/CarKinem.Tests.csproj` - **ALL tests pass**
- [ ] Minimum 15 unit tests created (covering all structs)
- [ ] Every data structure has at least:
  - Blittability test
  - Size validation test
  - Default value test (where applicable)

### Code Quality
- [ ] All structs use `[StructLayout(LayoutKind.Sequential)]`
- [ ] All structs are in correct namespaces (`CarKinem.Core`, `CarKinem.Formation`, etc.)
- [ ] No managed references in Tier 1 structs (VehicleState, NavState, etc.)
- [ ] `unsafe` keyword only used where necessary (fixed arrays)
- [ ] XML documentation comments on all public types

### Memory Layout
- [ ] FormationRoster.MemberEntityIds is fixed array of size 16
- [ ] RoadSegment.DistanceLUT is fixed array of size 8
- [ ] All enums are `byte` sized

---

## 📤 Submission Instructions

### When Complete

Create your batch report at:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-01-REPORT.md
```

Use this template structure:

```markdown
# BATCH-CK-01 Report

**Developer:** [Your Name]
**Completed:** [Date and Time]
**Duration:** [Hours spent]

## ✅ Completed Tasks

- [x] CK-01-01: Project setup
- [x] CK-01-02: Core enumerations
- ... (list all)

## 🧪 Test Results

```
dotnet test output here showing all tests passing
```

## 📊 Metrics

- Total tests created: X
- Test coverage: Y%
- Build warnings: 0
- Struct sizes verified: (list sizes)

## 🔧 Implementation Notes

- Any deviations from design doc
- Additional helpers created (if any)
- Performance considerations

## ❓ Questions for Review

(Use if you have any uncertainties)

## 🚀 Ready for Next Batch

Yes/No - (explain if No)
```

### If Blocked

Create:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-01-BLOCKERS.md
```

List specific blockers with context so I can unblock you quickly.

### Questions During Development

Create:
```
D:\WORK\ModuleHost\.dev-workstream\reports\BATCH-CK-01-QUESTIONS.md
```

I'll review and respond with answers file at same location.

---

## 📚 Reference Materials

- **Main Design:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`
- **Design Updates:** `D:\WORK\ModuleHost\docs\car-kinem-design-updates.md`
- **Task Tracker:** `D:\WORK\ModuleHost\.dev-workstream\CARKINEM-TASK-TRACKER.md`
- **FDP Examples:** `D:\WORK\ModuleHost\FDP\Fdp.Kernel\*.cs` (for struct patterns)

---

## 🎯 Success Criteria Summary

**This batch is done when:**
1. All projects build with zero warnings
2. All tests pass (minimum 15 tests)
3. 100% of data structures have blittability validation
4. Code review shows compliance with design document
5. Report submitted with test evidence

**Time Estimate:** 4-6 hours for experienced developer

---

**Good luck! Focus on correctness and test coverage. Don't rush - foundation is critical.**

---

_Batch prepared by: Development Lead_  
_Date: 2026-01-06 23:11_
