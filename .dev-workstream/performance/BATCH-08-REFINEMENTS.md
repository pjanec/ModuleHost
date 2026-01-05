# BATCH-08 REFINEMENTS
## Important Context for Implementation

1. **Safety First**: Making `ComponentTable` public exposes raw memory access.
   - Add comments warning that `Get(int entityIndex)` does NOT check if the entity has the component (ComponentMask check skipping).
   - This is "Unsafe" by design (for speed), but "Safe" from memory corruption (bounds checking exists in `NativeChunkTable`).

2. **Wait, Bounds Checking?**
   - `NativeChunkTable` does `EnsureChunkAllocated`.
   - `NativeChunkTable` indexer checks bounds in `FDP_PARANOID_MODE` but standard release builds might skip some checks.
   - We must rely on the user having filtered entities via `EntityQuery` first.

3. **GetSpan Implementation**
   - `NativeChunkTable` holds `NativeChunk<T>[]`.
   - `NativeChunk<T>` wraps a pointer.
   - `Span<T>` can be created from pointer + length.
   - `ComponentTable` should delegate `GetSpan` to `NativeChunkTable`.

4. **API Naming**
   - Use `GetComponentTable<T>` in `EntityRepository`.
   - Use `GetSpan` in `ComponentTable`.
