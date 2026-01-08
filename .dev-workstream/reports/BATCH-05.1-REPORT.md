# BATCH-05.1 Report: Component Mask Optimization

## Status: COMPLETE

### 1. Features Implemented

#### 1.1 `IModule` API Update
- Added `IEnumerable<Type>? GetRequiredComponents()` to `IModule`.
- Default implementation returns `null` (sync all components), preserving backward compatibility.
- This allows modules to explicitly declare their data dependencies for optimization.

#### 1.2 Kernel Optimization
- Implemented `GetComponentMask(IModule)` in `ModuleHostKernel`.
  - Uses `ComponentTypeRegistry.GetId()` to map types to mask bits.
  - Returns full mask if `GetRequiredComponents` returns null/empty.
  - Warns on unregistered components.
- Added **Component Mask Caching** in `ModuleHostKernel`.
  - Masks are computed once during `AutoAssignProviders` (initialization) and stored in `ModuleEntry`.
  - Providers (GDB, SoD) use the cached mask, avoiding per-frame reflection/lookup overhead.

#### 1.3 Provider Integration
- Updated `AutoAssignProviders` to calculate union masks using cached values.
- `DoubleBufferProvider` (GDB) and `SharedSnapshotProvider` (SoD Convoy) now receive optimized masks.
- `OnDemandProvider` (SoD Single) receives optimized mask.
- Modules with specific requirements now trigger significantly smaller data syncs (~98% reduction possible for typical few-component modules vs 256 components).

### 2. Verification

#### 2.1 Automated Tests
- Created `ModuleHost.Core.Tests/ComponentMaskTests.cs`.
- Validated:
  - Modules with declared dependencies return filtered masks.
  - Modules with no declarations return full masks (safety).
  - Component registry IDs are correctly mapped.
  - Integration with `ModuleHostKernel` initialization flow.

#### 2.2 Example Updates
- Updated `Fdp.Examples.BattleRoyale.Modules.PhysicsModule`:
  - Declares usage of `Position`, `Damage`, `Health`.
- Updated `Fdp.Examples.BattleRoyale.Modules.AIModule`:
  - Declares usage of `Position`, `AIState`, `Health`, `Velocity`, `Damage`.

### 3. Documentation
- Updated `docs/API-REFERENCE.md` to reflect `IModule` changes (`GetRequiredComponents`, `Policy`).
- Verified `docs/FDP-ModuleHost-User-Guide.md` contains the section "Optimizing Convoy Performance".

### 4. Next Steps
- Monitor performance impact in large-scale simulation.
- Proceed to BATCH-06 (if applicable).
