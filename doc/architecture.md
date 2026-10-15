# Architecture

[Docs index](README.md) · How EasyEcs stores data and why. For using the library, start with
[Getting started](getting-started.md); this page is for contributors and the curious. Changes must keep these guarantees:

- zero allocation on hot paths
- bounded memory under churn
- cache-friendly layouts
- safe structural changes during iteration

## Storage model

```mermaid
flowchart TB
    subgraph "World (one per shard/thread)"
        Meta["Meta[id] : EntityMeta (16 B)<br/>Archetype · Version · Slot"]
        Cols["Components[bit] : T[] per component type<br/>indexed by entity id"]
        Arch["Archetypes: mask → Archetype<br/>EntityIds (tombstones + free slots)<br/>add/remove edges"]
        QC["Query cache: (all, none) → Archetype[]<br/>copy-on-write"]
        Single["Singletons[index] : SingletonBox&lt;T&gt;"]
        Sys["Systems: sorted by priority, buckets"]
    end
    Meta --> Arch
    Arch --> Cols
    QC --> Arch
```

- **Per entity**, the world stores exactly one `EntityMeta`: 16 bytes, so 4 fit in a 64-byte line.
  - The component set is the archetype's mask, so it is not duplicated per entity.
  - `entity.Has<T>()` / `Get<T>()` touch the entity's meta record and its archetype's mask. Archetypes are few and
    hot, so the mask is almost always cached.
- **Component data** lives in one array per component type, indexed by entity id: `Components[bit][id]`.
  - Lookups are a single indirection.
  - Columns grow with the entity capacity (doubling), and every growth bumps `StorageVersion`.
  - A column records whether `T` contains managed references. Only those columns are cleared on remove and destroy,
    so references don't outlive the entity. Unmanaged data is simply overwritten by the next `Add` or `Set`.
- **Archetypes** hold entity ids, not data. Moving an entity between archetypes moves an id; component data never
  moves. This keeps structural changes O(1), which suits servers, where state flips constantly (buffs, combat, AOI).
- **Masks** (`Tag`) are 256-bit SIMD bitsets. There is no limit on component types: the first 256 are inline (one
  `Vector256` test per archetype match), each further 256 add one overflow vector, allocated once when an archetype
  is created (never on the iteration or add/remove path after warm-up).

Trade-off: iteration reads archetype ids and then gathers from each column. That is not as dense as chunked SoA
storage, but structural changes are much cheaper. `CompactArchetypes()` removes tombstones and **sorts ids
ascending**, so after compaction the gathers walk each column forward, which hardware prefetchers handle well.

## Memory layout at a glance

```mermaid
flowchart TB
    subgraph World
        M["Meta[id]: EntityMeta 16 B<br/>{Archetype, Version, Slot}<br/>4 per 64-byte line"]
        C1["Position[] (indexed by id)"]
        C2["Velocity[] (indexed by id)"]
        A1["Archetype {Position, Velocity}<br/>mask + entity ids"]
        A2["Archetype {Position}<br/>mask + entity ids"]
    end
    M -->|archetype + slot| A1
    A1 -->|ids| C1
    A1 -->|ids| C2
    A2 -->|ids| C1
```

- An entity's component set is its archetype's mask, so it is not stored per entity. A version check plus a `Has`
  touches one 16-byte record and one shared, hot archetype.
- Archetypes hold entity ids, not component data. Adding or removing a component moves an *id*; component data never
  moves.
- Columns are indexed by entity id. `CompactArchetypes()` removes tombstones and sorts ids, which turns iteration
  into a forward, prefetch-friendly walk. Call it between updates after heavy churn.
- Columns whose type contains managed references are cleared on remove and destroy, so the GC can collect what they
  pointed to. Unmanaged columns are not cleared: `Add` and `Set` overwrite them anyway.

## Entity lifecycle

```mermaid
stateDiagram-v2
    [*] --> Free
    Free --> Alive: CreateEntity (pop LIFO free list or next id)
    Alive --> Alive: Add / Set / Remove (archetype transition)
    Alive --> Free: Destroy (Version++, archetype slot → tombstone, managed columns cleared, id pushed)
```

- `Entity` is a handle: world, id and version. Every operation validates the version, so a handle to a destroyed
  entity can never touch the entity that reuses its id.
- Ids are recycled LIFO. Recently freed ids still have their cache lines warm, and every array stays bounded by the
  **peak** number of live entities.
- `Meta` never shrinks, even on `Dispose`, where it is cleared instead. That invariant lets handles index it without
  bounds checks.

## Archetype transitions

- `Archetype.EntityIds` uses tombstones (`-1`) plus a free-slot stack. Removal is O(1), and slots never shift, so an
  iteration in progress stays valid.
- Adding or removing a single component follows **edges** cached on the archetype. The edges are created once and
  linked in both directions, so the hot path never hashes a mask.
- Adding or removing several components (`Set(a, b)`, `Remove<A, B>()`) builds the target mask once and does one
  hash lookup.
- `CreateEntity(in T1 … in Tn)` places the entity in its final archetype directly, without intermediate moves.

## Query cache

`GetMatchingArchetypes(bits, filter)` keys a `ConcurrentDictionary` by (required mask, excluded mask). Each entry holds
an immutable `Archetype[]`. When a new archetype is created, under the structural lock, every matching entry is
replaced copy-on-write and published with `Volatile.Write`. Readers never lock and never observe a list being
mutated.

## Iteration and structural changes

- `ForEach` caches the column and id arrays and refreshes them whenever `StorageVersion` changes. Any code path that
  reallocates storage must bump it: columns, `Meta`, archetype id arrays and compaction.
- `Query` re-reads the archetype count and ids on every step.
- Removed entities are skipped through their tombstones. Entities added during a pass may or may not be visited.
- An entity moved into a *later* matching archetype during a pass may be visited again. Systems make their methods
  idempotent with filters. This is documented for users in
  [Structural changes during iteration](guide/structural-changes.md).

## Systems

- Systems are kept in an array sorted by `Priority`, with registration order breaking ties, and are grouped into
  buckets of equal priority. The array is copy-on-write.
- `Update()` is synchronous. For each due, enabled system it calls `OnUpdate()` and then the generated
  `GeneratedUpdate()` ([ForEach] methods).
- `AddSystem` calls `OnCreate()`; `RemoveSystem` and `Dispose` call `OnDestroy()`, `Dispose` in reverse order.
- Systems cooperate through data only, so all behavior runs in schedule order (see
  [ecs-principles.md](ecs-principles.md#4-state-changes-are-data)).
- Errors go to `World.SystemError`. Without a subscriber they propagate.
- System groups (`[UpdateInGroup<TGroup>]`) each have their own schedule; `Update()` runs group 0 (the default),
  `Update<TGroup>()` runs that group. User-facing details: [Systems](guide/systems.md).

## Source generators and analyzer (`EasyEcs.Generator`)

| Component | What it does |
|---|---|
| `ArityGenerator` | Emits every T1..T9 family of `EasyEcs.Core` from one template each: `Entity.Set` / `Remove`, `World.CreateEntity`, `IForEach`, `ForEach`, `Query`, `Count`, `Any`, `QueryItem` / `QueryEnumerator`. Runs only for `EasyEcs.Core`. |
| `SystemGenerator` | Turns `[ForEach]` methods into struct jobs, static filters and a `GeneratedUpdate` override. |
| `EasyEcsAnalyzer` | Reports EECS001–EECS006 (errors) and EECS007–EECS008 (ECS-principle warnings). Every rule with its fix: [Analyzer rules](guide/analyzer.md). |

Following the incremental-generator guidelines:

- Entry points are `ForAttributeWithMetadataName`, or a syntax-only predicate before any semantic work.
- Models are equatable records of strings. No `ISymbol`, `SyntaxNode` or `Location` survives the transform.
- Generators never report diagnostics. They skip invalid input, and the analyzer reports it using the same shared
  checks (`Symbols`).
- Every stage has a tracking name. `GeneratorTests.UnrelatedEditsHitTheCache` asserts that an unrelated edit leaves
  every stage `Cached` or `Unchanged`.
- Sources are added as UTF-8 `SourceText`.

## Type indices

`TypeIndex<TFamily, T>` assigns each type a dense, process-wide index within a family on first use. It is a static
readonly field, so the JIT folds it to a constant. There are two families (plus system groups):

- `ComponentType<T>.Bit`: mask bit and column index.
- `SingletonType<T>.Index`

Every world uses the same indices, so masks are comparable across worlds.

## Threading

- Structural operations, system changes and singleton creation take the world's `System.Threading.Lock`.
- Queries, `Get` and `Has` are lock-free.
- Component values are unsynchronized by design. The recommended layout is **one world per shard, owned by one
  thread**, where the lock is always uncontended. User-facing rules: [Threading](guide/threading.md).
