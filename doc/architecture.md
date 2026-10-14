# Architecture

This document explains how EasyEcs stores data and why, so that changes keep its guarantees:
zero allocation on hot paths, bounded memory under churn and safe structural changes during iteration.

## Storage model: sparse component columns + archetype entity lists

```
Context
├── Entities[id]            Entity { Id, Version, Tag (component bitset), Context }
├── Meta[id]                EntityMeta { Archetype, Version, Slot } = 16 bytes, 4 per 64-byte cache line;
│                           Archetype == null means the id is free. Version is bumped on destroy and
│                           EntityRef/ComponentRef validate it on access
├── Components[bit]         T[] per component type, indexed by entity id (length == Entities.Length)
├── Archetypes              Tag -> Archetype (one per distinct component set)
└── _singletons[index]      SingletonBox<T> per singleton type (per context)
```

- **Component data** lives in one array per component type, indexed directly by entity id. Access is
  `Components[bit][id]`: one indirection, no lookup, stable addresses until the arrays grow.
- **Archetypes** do not hold component data. They hold the list of entity ids that share a component set
  (`Tag`), which is what queries iterate. A query (`GroupOf`, `ForEach`, `CountOf`, `AnyOf`) builds a query
  tag, gets the cached list of matching archetypes (`GetMatchingArchetypes`), and walks their id lists.
- **Tags** are 256-bit SIMD bitsets with overflow for more than 256 component types. A component type gets its bit
  index from the process-wide `TagRegistry` the first time it is used.

Trade-off: iteration is over entity ids, then a gather from each component column. That is not as dense as
chunked
SoA storage, but add/remove only moves an *id* between archetypes (no component copying), which keeps
structural changes cheap. That fits server workloads where entities change state constantly.

## Entity lifecycle and id recycling

- `CreateEntity` pops a recycled id from a LIFO free list, or takes a new one and grows all per-entity
  arrays when needed. All of this happens under the structural lock.
- `DestroyEntity` bumps the version, detaches the entity from its archetype, clears the component slots marked in
  its tag (so managed references can be collected), resets the stored `Entity`, and pushes the id to the free list.
- Every API that receives an `Entity` value or an id validates the version (`ThrowIfStale`). A stale copy can
  never touch the entity that now occupies the recycled id.

Because ids are recycled, all arrays stay bounded by the **peak live entity count**, not by the total number of
entities ever created.

## Archetype membership and transitions

- `Archetype.EntityIds` uses **tombstones** (`-1`) plus a free-slot stack. Removal writes a tombstone in O(1);
  iteration skips tombstones. Slots are never shifted during normal operation, so an in-progress iteration stays
  valid.
- The context tracks `entity -> (archetype, slot)` in `EntityMeta`, so removing from an archetype is a plain array
  write with no hashing, and a structural change touches one cache line of bookkeeping.
- Single-component add/remove follows **transition edges** cached on the archetype (`GetAddEdge(bit)` /
  `GetRemoveEdge(bit)`), so the hot path never hashes a 256-bit tag. Multi-component operations look up
  the target archetype by tag once per call.
- `CompactArchetypes()` removes tombstones and rewrites slots. It is a maintenance operation; never call it
  while iterating.

## Generated API surface

Every T1..T9 API (`AddComponents`, `RemoveComponents`, `ForEach`, `GroupOf`, `CountOf`, `AnyOf`, `GroupResult`,
enumerators, `Entity` wrappers) is emitted by `EasyEcs.Generator/ArityGenerator.cs` from one template per family,
on top of a small shared core: `PrepareColumn<T>`, `AddBits`, `RemoveBits`, `GetMatchingArchetypes`,
`ArchetypeCursor`. User `[Query]` methods are compiled by `QueryGenerator.cs` into a struct `IForEach` job (inlined
by the JIT), a static `QueryFilter` and `Run{Method}(Context)`.

## Query cache

`GetMatchingArchetypes(bits, filter)` keys a `ConcurrentDictionary` by (required tag, excluded tag). Entries hold an
immutable `Archetype[]`; when a new archetype is created (under the structural lock) every matching entry is replaced
copy-on-write and published with `Volatile.Write`. Readers never lock and never observe a list being mutated.

## Iteration safety

Structural changes during iteration are allowed:

- `GroupOf` enumerators re-read `archetype.Count` and `archetype.EntityIds` on every step.
- `ForEach` snapshots the arrays for speed and refreshes them when `Context.StorageVersion` changes. Any code
  path that reallocates storage must bump it: component columns, entity arrays, archetype id arrays and
  compaction all do.
- Entities added during a pass may or may not be visited. Removed entities are skipped through tombstones.

## Systems and scheduling

- Systems are grouped into priority buckets (`PrioritySystemList`), and buckets run in ascending priority.
- `IExecuteSystem.ExecuteFrequency = n` runs a system every n-th `Update()`.
- With `Options.Parallel`, a bucket with more than 4 systems is awaited through `UniTask.WhenAll`. Systems run
  synchronously when they complete synchronously, and completed tasks are never awaited, to avoid state-machine
  costs.
- `IInitSystem` runs on `Init()` and for systems added later on the next `Update()`. `IEndSystem` runs on
  `DisposeAsync()`.

## Threading

- Structural operations take `_structuralLock`. That is `System.Threading.Lock` on .NET 9+ and `Monitor`
  otherwise.
- Component value reads and writes are unsynchronized by design. The recommended server layout is **one context
  per shard or world, owned by one thread**. Then the lock is always uncontended, and singletons are naturally
  per world.
- `TagRegistry` (type → bit) is process-wide and append-only, so contexts in one process agree on component
  bits.
