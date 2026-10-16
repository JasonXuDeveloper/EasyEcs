# Architecture

[Docs index](README.md) · How EasyEcs stores data and why. For using the library, start with
[Getting started](getting-started.md); this page is for contributors and the curious. Changes must keep these guarantees:

- zero allocation on hot paths
- bounded memory under churn
- cache-friendly layouts (64-byte lines) that SIMD can walk
- immediate structural changes, safe during iteration

## Storage model

```mermaid
flowchart TB
    subgraph "World (one per owner thread)"
        Meta["Meta[id] : EntityMeta (16 B)<br/>Archetype · Version · Slot"]
        Arch["Archetype = table<br/>mask · Slots[row]: EntitySlot {Version, Id} (8 B)<br/>one column per component (rows = slots)<br/>presence bits per column · add edges"]
        QC["Query cache: (required, excluded, any, changed) →<br/>archetypes + row filters, copy-on-write"]
        Single["Singletons[index] : SingletonBox&lt;T&gt;"]
        Sys["Systems: one schedule per group<br/>sorted by priority, split into batches"]
    end
    Meta -->|archetype + row| Arch
    QC --> Arch
```

- **Per entity**, the world stores exactly one `EntityMeta`: its archetype, version and row (16 bytes, so 4 fit in a
  64-byte line). The component set is the archetype's mask, so it is not duplicated per entity. (A value index, when
  a world creates one, keeps its own node per entity id; see the value indexes below.)
- **Archetypes are tables** (structure of arrays): one column per component type, a row per entity, plus one
  8-byte `EntitySlot` per row (version and id side by side, so a handle is one load). Iteration walks the columns of a table front to back, which hardware
  prefetchers and SIMD handle best.
  - Unmanaged columns are pinned arrays on the pinned object heap whose row 0 starts on a 64-byte boundary, so a row
    of a power-of-two-sized component never straddles two cache lines and vector loads stay aligned.
  - Columns whose type holds managed references are plain `T[]`; a row's references are cleared when the row loses
    its entity or its component, so the GC can collect what they pointed to.
  - Tables double when full and never shrink: each is bounded by the peak number of entities it held.
- **Presence bits.** Removing a component keeps the entity in its table and clears the row's bit for that component
  (one bit per row, 64 rows per `ulong`). A column gets its bits on the first removal; until then every row has the
  component and the column carries no bits at all. Each column also counts its alive rows lacking the component, so a
  query knows per table whether it can skip the table, walk every row, or test the bits.
- **Masks** (`Tag`) are 256-bit SIMD bitsets with overflow. Up to 65,536 component types are supported (bits are
  `ushort`): the first 256 are inline (one `Vector256` test per archetype match); beyond that a mask carries an
  overflow array (at least 4 × 256 bits, doubled when needed). Overflow masks are allocated when an archetype or a
  transition edge is first built, never on the iteration or structural-change path after warm-up.

- **Change ticks.** A component marked `ITrackChanges` brings a hidden companion column (`ChangeTicks<T>`, 8 bytes
  per row) into every table that has it: the world's change tick of each row's last write. Writes stamp it (a run of
  rows with one vectorized fill) and keep summaries: the latest tick of the column and of each block of 4,096 rows
  (upper bounds), and per 64-row word its latest tick, the rows holding that tick and a bound for the ticks of its
  other rows. Stamps carry the world's current tick, the latest of all, so a write through a handle to a word already
  at it only sets its row's bit. A pass that writes row after row (a query, a per-entity job) notes each word once
  and raises its bound to the tick instead, as rebuilding the rows bit by bit would cost every write a
  read-modify-write; a run of rows sets whole words. A row copied to another row (a move, a filled hole) brings its own tick and updates the target
  word by comparing. A `Changed` filter skips a table older than the pass's tick with one load, then unwritten blocks,
  then unwritten words (4 summaries per SIMD compare); a word whose other rows are older than the tick yields its
  written rows directly, and only words mixing older and newer ticks compare their rows (4 per SIMD instruction).
  The result is 64 bits per word, like the presence bits: it rides on the row-filter machinery.
  Untracked components have no column and no stamping code (the checks are JIT-time constants).
- **Value indexes** (`IIndexed<TKey>`, `world.Index<T, TKey>()`) are built on change ticks: a dictionary from key to
  the head and length of a doubly linked list of entity ids (nodes in an array indexed by id); relation heads (keys
  that are entities of the world) sit in an array indexed by the target's id instead, so a relation lookup hashes
  nothing. Writes cannot be seen when they happen (a `ref` is handed out before the value changes), so a lookup first
  re-indexes the rows written since the previous lookup: an inlined check of one summary per table, and when a table
  was written, a walk of its written rows (no pass: re-indexing changes no structure). Losing the component
  is seen when it happens: destroying an entity or removing an indexed component unlinks it at once (one null check on
  those paths while the world has no index), so counts are exact and enumeration needs no validation. Nothing is
  added to the write path.

Trade-off: a table copies an entity's row when the entity gains a component its table has no column for, which is
what makes iteration dense. States that come and go (stunned, burning, dead) only flip presence bits after the first
time, so they cost no copy; a table with many rows lacking a queried component is walked 64 rows per bit word.

Columns are contiguous arrays, not pages. Paged columns (a few thousand rows per page) iterate at the same speed but
put one more indirection on every handle access; all they save is the copy when a table grows, a warm-up cost that
`InitialEntityCapacity` removes, while handle access is paid on every tick.

## Memory layout at a glance

```mermaid
flowchart TB
    subgraph World
        M["Meta[id]: EntityMeta 16 B<br/>{Archetype, Version, Slot}<br/>4 per 64-byte line"]
        subgraph A1["Archetype {Position, Velocity, Stunned}"]
            I1["Slots[]: EntitySlot {Version, Id}"]
            P1["Position[] (64-byte aligned)"]
            V1["Velocity[] (64-byte aligned)"]
            S1["Stunned[] + presence bits"]
        end
        subgraph A2["Archetype {Position}"]
            I2["Slots[]: EntitySlot {Version, Id}"]
            P2["Position[]"]
        end
    end
    M -->|archetype + row| A1
    M -->|archetype + row| A2
```

- `entity.Get<T>()` is a version check on the 16-byte record, a column lookup in the archetype (bit → column, a small
  array) and, when the column has presence bits, one bit test; then an indexed load from the column.
- `CreateEntity(a, b, ...)`, `Set(a, b, ...)` and `Add<T>()` follow transitions cached per archetype (per component
  bit, and per generic signature for several components), so a repeated structural change is one array load plus
  the row copy.

## Entity lifecycle

```mermaid
stateDiagram-v2
    [*] --> Free
    Free --> Alive: CreateEntity (pop LIFO free list or next id)
    Alive --> Alive: Add / Set a new component (row copied to the target table)
    Alive --> Alive: Remove, or re-add a removed one (presence bit, no move)
    Alive --> Free: Destroy (Version++, row removed, id pushed)
```

- `Entity` is a handle: world, id and version. Every operation validates the version, so a handle to a destroyed
  entity can never touch the entity that reuses its id.
- Ids are recycled LIFO. Recently freed ids still have their cache lines warm, and `Meta` stays bounded by the
  **peak** number of live entities.
- `Meta` never shrinks, even on `Dispose`, where it is cleared instead. That invariant lets handles index it without
  bounds checks.

## Archetype transitions and row removal

- An entity moves only when it gains a component its table has no column for. The move copies the components both
  tables have (a copy plan of column pairs, computed once per edge) into a new row of the target table. Unmanaged
  elements are copied as bytes with fixed-width loads; managed ones through their typed copy.
- Removing a component never moves the entity: the row clears its presence bit (and a managed value). Adding that
  component back sets the bit again.
- Single-component adds follow **edges** cached on the archetype per component bit. Multi-component adds
  (`CreateEntity(a, b)`, `Set(a, b)`) follow **set edges** cached per archetype and keyed by the dense index of the
  generic signature (`TypeIndex<ComponentSignature, (T1, …, Tn)>`). The first call per (archetype, signature) builds
  the target mask and does one hash lookup; later calls are one array load.
- `CreateEntity(in T1 … in Tn)` places the entity in its final table directly, without intermediate moves.
- A row that loses its entity (destroyed or moved away) is filled at once by the table's last row when no pass is
  iterating the world, so tables stay dense. While a pass iterates, the row becomes a **tombstone** (id `-1`) instead,
  so rows never shift under the pass; the last pass to end closes the tombstones (moving the last rows into them,
  found with vectorized scans of the rows read as `long`s: a tombstone's id, the high half, is negative).

## Query cache

`GetMatchingArchetypes(bits, filter)` keys a `ConcurrentDictionary` by the query's components (required, excluded,
`WithAny`, `Changed`). Each entry holds an immutable snapshot: the archetypes whose mask contains every required
component (and, with `WithAny`, at least one of those columns), and one row filter per archetype (the columns whose
presence bits and change ticks the query tests). A value index holds its entry (`GetQueryEntry`), so it sees new
tables without a lookup. An excluded component does not rule a table out, since its rows may lack it; the row filter
does. When a new archetype is created, under the structural gate, every matching entry is replaced copy-on-write and
published with `Volatile.Write`. Readers never lock and never observe a list being mutated.

Per pass and table, the row filter picks one of three modes from the column counts: skip the table (no row can
pass), walk every row (no row lacks a required component or has an excluded one), or test presence bits. A filter
with `Changed<T>` always tests: its words also AND in the rows whose tick reaches the pass's tick (the cached plan is
shared by every pass; the tick travels with the pass).

## Iteration and structural changes

Every iteration API walks tables front to back. What differs is how much bookkeeping a pass needs:

- **Jobs that make no structural change** (`INoStructuralChanges`, added by the generator to static `[ForEach]`
  methods that take no `World` and use their `Entity` parameter, if any, only to read: `Id`, `Version`, `IsAlive`,
  `Has`, `Get`, `TryGet`) walk dense tables with no per-row check, and walk tables with presence bits 64 rows per bit
  word. `ForEachBatch` and `[ForEach]` methods taking spans hand whole runs of consecutive passing rows to the job;
  such jobs must not make structural changes at all.
- **Other passes** register with the world (`BeginPass` / `EndPass`), so removed rows become tombstones instead of
  being filled. Before walking, a pass records each matching table's row count; rows appended during the pass
  (entities created or moved into a matching table) are past that count and not visited. Each entity is therefore
  visited at most once, and an entity that loses a queried component or is destroyed before its turn is skipped.
- After each entity, a pass compares `StorageVersion` with the value it last saw. Every change that can reallocate
  storage, move rows or flip presence bits bumps it: the row loop then returns the next row to the pass, which
  re-reads the column refs and presence bits and enters a row loop again from there (so the loop keeps only its row
  state live across the job, in registers). A table whose rows all pass is walked by a lean loop; once a change makes
  some rows fail, by a loop that tests bits once per run of passing rows.
- When a job changes the archetype of the entity it is visiting, its writes through the refs land in the old row
  (now a tombstone). After the job returns, the pass copies the components the job writes (`Writes`, generated from
  its `ref` parameters) to the entity's new row, so the job's writes are never lost.
- `Query` follows the same visiting rules. Its enumerator keeps the hot state (row, row count, entity rows, column
  refs) in its own fields and is never passed by reference: every member
  a loop calls is inlined, so the JIT keeps those fields in registers. The cold state (each table's row count at the
  start, the row filter, change-stamp bookkeeping) lives in a `QueryWalk`, pooled per thread (one per nesting level
  once warmed up: no allocation in steady state), reached by value once per table and per structural change (and once per
  passing row of a table whose rows must be tested). Keeping a word's passing bits in the enumerator as well was
  measured and rejected: the two extra values pushed the column refs of every loop out of registers. The slot and the
  row count are native ints, so row addressing needs no sign extension on x64 or arm64. A ticket tells a walk's current use from a copy of an enumerator that already ended.

## Systems

- Each system group has one schedule: its systems sorted by `Priority`, with registration order breaking ties, split
  into batches of consecutive, pairwise non-conflicting systems of one priority (`SystemAccess`). Both arrays are
  copy-on-write.
- `Update()` / `Update<TGroup>()` are synchronous. For each due, enabled system they call `OnUpdate()` and the
  generated `GeneratedUpdate()` (`[ForEach]` methods). If a pass was interrupted by an exception, the update first
  closes the tombstones it left.
- `AddSystem` calls `OnCreate()`; `RemoveSystem` and `Dispose` call `OnDestroy()`, `Dispose` in reverse order.
- Systems cooperate through data only, so all behavior runs in schedule order (see
  [ecs-principles.md](ecs-principles.md#4-state-changes-are-data)).
- Errors go to `World.SystemError`. Without a subscriber they propagate.
- System groups (`[UpdateInGroup<TGroup>]`) each have their own schedule; `Update()` runs group 0 (the default),
  `Update<TGroup>()` runs that group. User-facing details: [Systems](guide/systems.md).

## Source generators and analyzer (`EasyEcs.Generator`)

| Component | What it does |
|---|---|
| `ArityGenerator` | Emits every T1..T9 family of `EasyEcs.Core` from one template each: `Entity.Set` / `Remove`, `World.CreateEntity` / `CreateEntities` / `DestroyEntities`, `IForEach` / `IForEachEntity` / `IForEachBatch`, `ForEach` / `ForEachEntity` / `ForEachBatch` / `ForEachParallel` / `ForEachEntityParallel`, `Query`, `Count`, `Any`, `QueryItem` / `QueryEnumerator`. Runs only for `EasyEcs.Core`. |
| `SystemGenerator` | Turns `[ForEach]` methods into struct jobs (per entity, or per run of rows for span parameters), static filters, a `GeneratedUpdate` override and a `GeneratedAccess` override (reads and writes, or `RunsAlone`) used for `Parallel` batching. `[Changed<T>]` becomes `ChangedSince(LastRunTick + 1)` on the method's filter and a declared read of `T`; `[ForEach(Parallel = true)]` runs the job over chunks of rows. Jobs that cannot change structure get `INoStructuralChanges`; jobs record which components they write (`Writes`) and whether they read the entity id. |
| `EasyEcsAnalyzer` | Reports EECS001–EECS012, all errors: invalid input the generator skips, and code that breaks the ECS principles. Every rule with its fix: [Analyzer rules](guide/analyzer.md). |

Following the incremental-generator guidelines:

- Entry points are `ForAttributeWithMetadataName`, or a syntax-only predicate before any semantic work.
- Models are equatable records of strings. No `ISymbol`, `SyntaxNode` or `Location` survives the transform.
- Generators never report diagnostics. They skip invalid input, and the analyzer reports it using the same shared
  checks (`Symbols`).
- Every stage has a tracking name. `GeneratorTests.UnrelatedEditsHitTheCache` asserts that an unrelated edit leaves
  the `SystemGenerator` stages (`ForEachMethods`, `SystemUpdates`) `Cached` or `Unchanged`.
- Sources are added as UTF-8 `SourceText`.

## Type indices

`TypeIndex<TFamily, T>` assigns each type a dense, process-wide index within a family on first use. It is a static
readonly field, so the JIT folds it to a constant. The families:

- `ComponentType<T>.Bit`: mask bit (a `ushort`, so up to 65,536 component types). Assigning it registers the type's
  `ComponentInfo` (element size, managed or not, typed column operations), which tables use without knowing `T`.
- `SingletonType<T>.Index`: singleton slot.
- `SystemGroup<T>.Index`: system group (0 is the default group, so group indices start at 1).
- `ComponentSignature`: one index per generic signature `(T1, …, Tn)` of a multi-component `CreateEntity` or `Set`,
  keying the set edges of each archetype.
- `ValueIndexFamily`: the slot of a component's value index in its world (`world.Index<T, TKey>()`).

An `ITrackChanges` component also registers its hidden `ChangeTicks<T>` column, paired with the component's own
column in `ComponentInfo`, so every copy, move and growth of a row carries its change tick.

Every world uses the same indices, so masks are comparable across worlds.

## Threading

- Structural operations, system changes and singleton creation enter the world's **structural gate**: a
  compare-and-swap flag on its own cache line, with a spinning wait under contention. It is not reentrant (no
  structural operation calls another one under the gate; debug builds assert it).
- Queries, `Get` and `Has` are lock-free. The first use of a query shape enters the gate once to build its cache
  entry.
- Component values are unsynchronized by design. The recommended layout is **one world per owner thread**, where the
  gate is always uncontended. User-facing rules: [Threading](guide/threading.md).
- `WorldOptions.Parallel`: every system carries a `SystemAccess` (component and singleton read/write bitsets, or
  "runs alone"), generated from its `[ForEach]` parameters and read once by `AddSystem`. The schedule splits each
  priority into batches of consecutive, pairwise non-conflicting systems and publishes them as one copy-on-write
  array. A batch runs on the calling thread plus reusable `IThreadPoolWorkItem` helpers that claim systems through
  one `Interlocked` cursor (epoch in the high 32 bits, so a helper that starts late never touches the next batch);
  nothing is allocated per update.
