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
## EECS116's code fix (feat-tiles-pingpong, 2026-10-28)

Item 11 asked for a rule and a code fix for a hand-written commit that is a plain copy. The rule shipped (EECS116,
Warning, reported at the end of the compilation on the commit method, naming the step that writes the second buffer);
the fix did not. The rewrite is not local: it changes the step method (its by-value `Tile<TCells, ...>` becomes `ref`,
its `Mut<DynamicBuffer<TNext>>` / `ref DynamicBuffer<TNext>` parameter goes and each write into it becomes
`Write(row)`), deletes the commit method (and its system when that empties it, with every `AddSystem<T>`,
`[UpdateAfter<T>]` / `[UpdateBefore<T>]` naming it), and removes the `TNext` buffer type with every
`GetOrAdd<DynamicBuffer<TNext>>` / `SetCount` that sizes it, in other files and projects. A step that also reads TNext
(a second step, a debug view) or writes it on some rows only cannot follow at all. A fix that is offered only when all
of that is mechanical would almost never be offered; one that is offered otherwise breaks code.

What would change the verdict: users porting many hand-written step + commit pairs (VoxelServer's water and the grids
page's heat are ported by hand in the VoxelServer rewrite and the docs pass), or a fix limited to the step method with
the commit left for the user to delete (the compiler then reports the stale writes of TNext).

## The generated ping-pong step against step + commit by hand (feat-tiles-pingpong, merged 2026-10-29)

**Owner (2026-10-29): merge it, then fix the reads.** After the Tier1 review (LoadPlane, an unrolled plane Find,
nint cursor offsets, a Facing table, parallel dense swaps, a woken walk per color with the `TileWalk.Share` gate) the
`ref Tile` step beats the hand-written form past the last-level cache (Blur9 512x512: 198 against 255-260 ms at 1
thread, 72 against 88-92 ms at 4) and in sparse fields (about 2x at every share of awake tiles), and still loses in
cache (Blur9 32x32 529 against 473-505 us, Laplacian 580 against 436-476 us at 1 thread; one `--dev` round each). The
gap is the Tile API's checked column loads, which the by-value form pays too: backlog "Tile reads checked once".
VoxelServer TickBenchmark base against head, 4 interleaved rounds: within noise, 0 B.

What would change the verdict: nothing left to decide; the read fix is scheduled.

## Tile loads: the Laplacian gap left, and loads the generator cannot prove (feat-tile-reads, 2026-10-29)

**Proposed by** the tile-reads round. The interceptor of a proven column loop leaves the Laplacian's column loop at
48 instructions against the hand-written 33 (Laplacian 32x32 at 1 thread: ping-pong 1.14x hand, every other case at or
below hand). What is left is index arithmetic: the JIT inlines the interceptor's `dx` (x + k) into a temporary because
the folded-away checked branch still names it, so each column computes `x + k`, then `+ r` again, where the hand form
adds one base and folds every k into the displacement. Tried and measured in Tier1 listings: the cursor by value
(an interceptor of an instance method must take `this in`), a span per row (more checks), a 64-bit offset (a
`movsxd` per column again). **Verdict: not done.** What would change it: a JIT that forward-substitutes the single
remaining use after the branch folds (worth rechecking on each .NET release with `tools/jitdisasm.sh`), or an API
that hands the loop's base column to the method (`c.Columns(...)`), which is public surface the owner would decide.

A second proposal, **not built**: an Info rule reporting a `TileCursor` load in a loop the generator cannot prove
(the variable written in the body, a bound it cannot read), with the reason, as EECS055 does for vector loops. Every
such load is still correct (checked); the rule would only tell the user why it is slower. Worth it once a user's
stencil shows up with that shape; the reasons are already the cases `TileLoad.Read` returns null for.

**Reverted: the per-tile frame changes (TileSteps.Begin, Tile.Frame, the static TileRows.Visit, the unchecked
Jacobi row of Tile.Write).** The first measurement of both halves together showed the Laplacian ping-pong at Side 512
5-7 % slower than the base, so the halves were built apart on the base (09622ba) and run in 4 interleaved rounds of the
Laplacian ping-pong. The frame half won nothing anywhere: 1.020x / 1.054x at Side 32, 1.012x / 1.063x at Side 512 (1 /
4 threads). Its Tier1 says why: splitting Enter into Enter and Begin adds a call per tile and returns the Tile (a ref
struct) through a hidden return buffer; Enter and Begin take 158 instructions with 23 stack operands against 116 and 8
for Enter alone, more than the two thread-static reads per tile it saved. What would change the verdict: a design that
reads the frame once without a second call (Enter handing the tile out through an out parameter the job keeps in a
register), measured on TileActive and TileWoken, where the per-tile cost matters more than in a dense stencil.

**Kept: the tile-load interceptor.** Alone against the base, 8 interleaved rounds of the Laplacian ping-pong: 0.987x and
0.988x at Side 32, 0.997x at Side 512 on 4 threads, and at Side 512 on 1 thread the paired per-round ratios 1.029,
1.081, 1.014, 1.032, 1.038, 1.003, 0.987, 0.964 (median 1.022, standard deviation 0.035, the base itself moving 15 %
between rounds). In 3 more rounds of the ping-pong steps: Laplacian 0.92-0.97x, Blur9 0.94-1.01x, Side 512 on 4 threads
0.89x / 0.95x; TileActive PingPong at Side 512 1.03-1.04x, a pass whose time is the walk over sleeping tiles, which the
interceptor does not change. The step's loop is shorter with PGO on and off (52 against 66 and 47 against 61
instructions, no spill, no movsxd; neither loop gets alignment padding), so no mechanism in the generated code explains
a slower out-of-cache case: the swings at Side 512 are the size of what code the change does not touch moves on this
machine (a 4-vCPU VM, no hardware counters, an L3 shared with other tenants). What would change the verdict: a quiet
machine (the owner's M4 or a bare-metal x64) showing Side 512 slower in paired rounds.

