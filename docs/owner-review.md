# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## Ghost rows between worlds: `CopyEntities` with a component set (borrowed-ideas item 4, backlog item 7)

**Proposed by** the ghost-rows round (2026-10-28), after writing the halo exchange in `samples/MultiWorld` with the
current API (`Border.cs`). **Verdict: not a feature yet**; one small primitive is worth a measured prototype.

What a user writes for ghosts, and whether they would write it again in another project:

| Lines in `Border.cs` | Again elsewhere? |
| --- | --- |
| `DestroyAll<Ghost>()` per zone | yes, but it is one line |
| crossers: a query over the positions, the owner of each (`Map.ZoneOf`), a list of ids per neighbor, `MoveEntities` per neighbor (~12 lines) | the shape yes (partition by a key, move each part), the key no: a strip, a grid, a quadtree or a room graph each decide differently |
| border rows: a query of the owned rows, the halo band test, `CreateEntity(position, new Ghost())` per neighbor (~8 lines) | yes: copying rows into another world with a subset of their components plus a tag has no API, so the copy is written per row and per component by hand, and each component a ghost should carry is listed again there |
| the `Barrier` whose post-phase action runs the exchange on one thread | yes, but it is host threading, not ECS: a framework cannot pick the sync (barrier, tick message, fixed-step host) |
| `Ghost` without `Velocity`, so writers skip it; `[Without<Ghost>]` on the method whose rows must be owned | data design; a richer ghost (health for targeting) needs `[Without<Ghost>]` on every system that writes what it carries: per project |

Costs the core could remove (not measured; a BenchmarkDotNet case is the first step before any claim):
- `CreateEntity` per ghost looks the table up and writes a row per call, and `DestroyAll` empties the tables again next
  tick. The core already copies runs of rows between worlds (`ArriveRows`, what `MoveEntities` uses); a copy that
  keeps the source would do the same with a column copy per run, and the pair method keeps the tables in space order,
  so the border rows tend to be runs.
- Ghost ids change at every exchange, so nothing can point at a ghost across ticks. Stable ghosts would need an index
  from the source id and in-place writes: more user code, not less.
- The exchange is serial because each world receives from two neighbors; a parallel exchange (each zone packs its
  border rows into a buffer of its own, then each zone unpacks its neighbors' buffers) needs the copy to read from
  bytes or a buffer, which `SaveEntitiesWith` / `LoadEntities` already do through Nino.

API sketch, if a prototype pays:

```csharp
// Copies the entities into target as new entities holding only the set's components (no move, the originals stay),
// with the tag T added; returns how many. O(entities × components of the set), a column copy per run of rows.
int CopyEntities<TTag>(World target, ReadOnlySpan<EntityId> ids, in ComponentSet set, Span<Entity> copied = default)
    where TTag : struct, IComponent;

// Border.CopyBorderRows would collect ids instead of creating per row:
//   zones[z].CopyEntities<Ghost>(zones[z + 1], CollectionsMarshal.AsSpan(_rightBand), GhostRows);
// with GhostRows = ComponentSet.Create().With<Position>(), the components a ghost carries named once.
```

**Prototype measured (backlog 8h, 2026-10-28): not shipped.** Branch `feat-copy-entities` keeps the code: the API
above on `ArriveRows` (a gather per source table, links into the set remapped, links out of it nulled, ticks
stamped like `CreateEntity`, unique keys claimed before anything is copied), `CopyEntitiesTests`, the AOT check and
`GhostCopyBenchmark` (20,000 units, 500 / 1,000 / 5,000 border rows a tick, 1 or 3 components, scattered or sorted
ids, `DestroyAll<Ghost>` every tick; 0 B in all three ways). Copy / `CreateEntity` loop over five rounds:

| Rows | 1 component, scattered | 1 component, sorted | 3 components, scattered | 3 components, sorted |
| --- | --- | --- | --- | --- |
| 500 | 0.96-1.09 | 0.82-0.99 | 0.82-0.90 | 0.72-0.80 |
| 1,000 | 0.96-1.07 | 0.86-0.92 | 0.81-0.92 | 0.71-0.88 |
| 5,000 | 0.92-1.08 | 0.85-1.00 | 0.75-0.87 | 0.63-0.79 |

`SaveEntitiesWith` + `LoadEntities` costs 1.4-4.3x of the loop. With one component on scattered rows the copy reads
the source's metadata, slots and column at strided addresses where the loop reads its query's rows in order, so the
gather saves nothing; MultiWorld's ghosts are that case (one `Position`, rows in query order but the band picked by
a test), and its `Border.cs` grows from 9 to 14 lines (an id list per neighbor plus the set). What would change the
verdict: ghosts that carry several components in a sample (VoxelServer's region worlds), a form that also picks the
band (a filter or a query instead of an id list, so the copy reads rows in order and the user writes no list), or
a measured sequential path for ids that come from one query in order. Before any merge of the branch, two classes
that do not call the copy need a look: `WorldSimulationBenchmark` Tick(10,000) ran 1.01-1.06x of the base in four
rounds and `MigrationBenchmark.SaveRecord` 1.03x (`MoveEntities` gains from the shared `ArriveRows` changes: 0.82-0.86x).

What it removes: the per-component copy lines (the ghost's components named once in a set) and the per-row create.
What it costs: a new public method (an addition, no migration), the `ComponentSet` path in `ArriveRows` (today it
copies every column), EntityId fields of copied rows that point outside the copy (rewrite to null, as `LoadEntities`
does for a missing parent), and tests and docs. It is general (read replicas of a world, an area of interest copied to
a per-client world, previews), as the feature checklist asks (servers, games, simulations).

**Owner (2026-10-28): build the prototype and the benchmark** (backlog item 8h).

What would change the verdict: a BenchmarkDotNet case (exchange of 500 to 5,000 border rows a tick: `CreateEntity`
loop vs `SaveEntitiesWith` + `LoadEntities` vs a prototype `CopyEntities`) showing the per-row path costing a
noticeable share of a tick, or a second sample (VoxelServer's two region worlds) writing the same copy loop.
