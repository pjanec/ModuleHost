# BATCH-07: Zero-Allocation Query Iteration

## 1. Overview
The current `EntityQuery.ForEach(Action<Entity>)` implementation causes heap allocations for closures in every frame for every system. This generates significant GC pressure (estimated ~1.4 TB/year for a dedicated server) and prevents JIT inlining of tight loops.

**Objective**: Implement a `ref struct` enumerator for `EntityQuery` to enable zero-allocation `foreach` iteration.

**Priority**: P1 (Critical Optimization)

## 2. Implementation Tasks

### 2.1 Implement `EntityEnumerator`
Modify `d:/WORK/ModuleHost/FDP/Fdp.Kernel/EntityQuery.cs` to add the `GetEnumerator` method and the `EntityEnumerator` ref struct.

**Code Specification:**

```csharp
namespace Fdp.Kernel
{
    public sealed class EntityQuery
    {
        // ... existing code ...

        /// <summary>
        /// Gets an enumerator for zero-allocation iteration.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityEnumerator GetEnumerator() => new EntityEnumerator(this);

        /// <summary>
        /// Zero-allocation enumerator for EntityQuery.
        /// </summary>
        public ref struct EntityEnumerator
        {
            // Fields to cache for performance (avoid referencing EntityQuery object in loop)
            private readonly BitMask256 _includeMask;
            private readonly BitMask256 _excludeMask;
            private readonly BitMask256 _authorityIncludeMask;
            private readonly BitMask256 _authorityExcludeMask;
            private readonly bool _hasDisFilter;
            private readonly ulong _disFilterValue;
            private readonly ulong _disFilterMask;
            private readonly EntityIndex _entityIndex;
            
            private int _currentIndex;
            private readonly int _maxIndex;

            internal EntityEnumerator(EntityQuery query)
            {
                _includeMask = query.IncludeMask;
                _excludeMask = query.ExcludeMask;
                _authorityIncludeMask = query._authorityIncludeMask;
                _authorityExcludeMask = query._authorityExcludeMask;
                _hasDisFilter = query._hasDisFilter;
                _disFilterValue = query._disFilterValue;
                _disFilterMask = query._disFilterMask;
                
                // Direct access to index for maximum speed
                _entityIndex = query._repository.GetEntityIndex();
                _maxIndex = _entityIndex.MaxIssuedIndex;
                _currentIndex = -1; 
            }

            public Entity Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new Entity(_currentIndex, _entityIndex.GetHeaderUnsafe(_currentIndex).Generation);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                // Tight loop with inlined checks
                while (++_currentIndex <= _maxIndex)
                {
                    ref var header = ref _entityIndex.GetHeaderUnsafe(_currentIndex);

                    if (!header.IsActive)
                        continue;

                    // INLINED MATCH LOGIC (Critical for perf)
                    
                    // 1. Component Mask
                    if (!BitMask256.HasAll(header.ComponentMask, _includeMask)) continue;
                    if (BitMask256.HasAny(header.ComponentMask, _excludeMask)) continue;

                    // 2. Authority Mask
                    if (!BitMask256.HasAll(header.AuthorityMask, _authorityIncludeMask)) continue;
                    if (BitMask256.HasAny(header.AuthorityMask, _authorityExcludeMask)) continue;

                    // 3. DIS Filter (Single instruction check)
                    if (_hasDisFilter)
                    {
                        if ((header.DisType.Value & _disFilterMask) != _disFilterValue)
                            continue;
                    }

                    return true;
                }

                return false;
            }
        }
    }
}
```

*Note: Access modifiers for `_includeMask` etc. in `EntityQuery` might need to be made `internal` if they are `private`.*

### 2.2 Deprecate `ForEach`
Mark the existing `ForEach` method as obsolete to encourage migration.

```csharp
[Obsolete("Use foreach loop for zero allocation. query.ForEach allocates closures.")]
public void ForEach(Action<Entity> action) { ... }
```

## 3. Test Specifications

Create `d:/WORK/ModuleHost/FDP/Fdp.Tests/EntityQueryEnumeratorTests.cs`.

### Test 1: `Enumerator_IteratesAllMatches`
- **Setup**: Create 100 entities. 50 match `With<Pos>`, 50 do not.
- **Action**: Iterate using `foreach (var e in query)`.
- **Assert**: Count is exactly 50. All entities match filter criteria.

### Test 2: `Enumerator_SkipsNonMatches`
- **Setup**: Create entities with mixed components. `With<A>`, `Without<B>`.
- **Action**: Iterate.
- **Assert**: Entities with `B` are NOT visited. Entities without `A` are NOT visited.

### Test 3: `Enumerator_EmptyQuery_ReturnsFalseImmediately`
- **Setup**: Empty repository.
- **Action**: `foreach` loop.
- **Assert**: Loop body never executes.

### Test 4: `Enumerator_HandlesGaps`
- **Setup**: Create 10 entities. Destroy indexes 2, 5, 8.
- **Action**: Iterate.
- **Assert**: Count is 7. Destroyed entities are skipped.

## 4. Benchmark Specifications

Create `d:/WORK/ModuleHost/FDP/Fdp.Tests/Benchmarks/QueryIterationBenchmarks.cs` (or similar location).

Using `BenchmarkDotNet` (or manual timing loop if full suite not available):

| Benchmark | Description | Iterations |
|-----------|-------------|------------|
| `ForEach_Lambda` | Current method with `Action<Entity>` | 100,000 |
| `Foreach_Enumerator` | New `ref struct` enumerator | 100,000 |

**Success Criteria**:
- `Foreach_Enumerator` allocated bytes == 0.
- `Foreach_Enumerator` time < `ForEach_Lambda` time (Target: >2x speedup).

## 5. Documentation Update

Update `d:/WORK/ModuleHost/docs/PERFORMANCE.md`:
- Add section "Iteration Best Practices".
- Add "Avoid `ForEach(lambda)`" rule.
- Show code example comparison (Lambda vs Foreach).
