# BATCH-CK-09 Instructions Review & Corrections

**Date:** 2026-01-07  
**Reviewed By:** Antigravity  
**Status:** ✅ Corrected and ready for implementation  

---

## Issues Found

The BATCH-CK-09 instructions contained **3 patterns** that were inconsistent with the updated FDP best practices from BATCH-CK-FIX-01:

### Issue 1: Incorrect System Attributes
**Problem:**
```csharp
[SystemAttributes(Phase = Phase.EarlyUpdate, UpdateFrequency = UpdateFrequency.EveryFrame)]
```

**This attribute does not exist in FDP Kernel.**

**Correction:**
```csharp
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(CarKinematicsSystem))]
```

**Reason:** FDP Kernel uses `[UpdateInGroup]`, `[UpdateBefore]`, `[UpdateAfter]` attributes, not custom `[SystemAttributes]`.

---

### Issue 2: Use of `GetComponentRef<T>()`
**Problem:**
```csharp
ref var nav = ref World.GetComponentRef<NavState>(entity);
nav.Mode = NavigationMode.None;
// ...
World.SetComponent(entity, nav);
```

**Potential Issue:** `GetComponentRef` may not be the standard API, and using ref then calling `SetComponent` is redundant.

**Correction:**
```csharp
var nav = World.GetComponent<NavState>(entity);
nav.Mode = NavigationMode.None;
// ...
World.SetComponent(entity, nav);
```

**Reason:** Get-Modify-Set pattern is the standard FDP approach. The component is a struct (value type), so we get a copy, modify it, and set it back.

---

### Issue 3: Missing Using Statement
**Problem:**
```csharp
using CarKinem.Commands;
using CarKinem.Core;
using Fdp.Kernel;
```

**Missing:** `using CarKinem.Formation;` needed for `FormationMember` and `FormationMemberState`.

**Correction:**
```csharp
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;
```

---

## Changes Applied

✅ **Line 124:** Changed attributes to FDP Kernel standard
```diff
- [SystemAttributes(Phase = Phase.EarlyUpdate, UpdateFrequency = UpdateFrequency.EveryFrame)]
+ [UpdateInGroup(typeof(SimulationSystemGroup))]
+ [UpdateBefore(typeof(CarKinematicsSystem))]
```

✅ **Line 116:** Added missing using
```diff
+ using CarKinem.Formation;
```

✅ **All methods:** Replaced `GetComponentRef` with `GetComponent`
```diff
- ref var nav = ref World.GetComponentRef<NavState>(entity);
+ var nav = World.GetComponent<NavState>(entity);
```

---

## Verification

**Patterns Now Align With:**
- ✅ BATCH-CK-FIX-01 corrections
- ✅ FDP Kernel attribute system
- ✅ Standard Get-Modify-Set component pattern
- ✅ FDP ModuleHost User Guide

**No Other Issues Found:**
- ✅ Event definitions are correct (`[Event(EventId = ...)]`)
- ✅ VehicleAPI uses `ISimulationView` correctly
- ✅ Command processing uses `ConsumeEvents<T>()`
- ✅ Tests follow proper setup pattern
- ✅ No system-to-system references
- ✅ Uses `SetComponent` for updates (semantic clarity)

---

## Updated Instructions

The corrected BATCH-CK-09 instructions are now saved at:
```
D:\WORK\ModuleHost\.dev-workstream\batches\BATCH-CK-09-INSTRUCTIONS.md
```

**Developer can proceed with implementation immediately.**

---

## Lessons Learned

**Why This Happened:**
- BATCH-CK-09 instructions were written before BATCH-CK-FIX-01 architectural corrections
- Instructions were based on initial (pre-architect-review) patterns

**How to Prevent:**
- Always validate instructions against latest best practices
- Reference FDP ModuleHost User Guide for current patterns
- Check recent BATCH-FIX reports for updated standards

---

**Status:** ✅ Ready for BATCH-CK-09 implementation with corrected instructions
