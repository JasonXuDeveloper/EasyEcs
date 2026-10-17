# Design decisions

[Docs index](README.md) · Related: [ECS principles](ecs-principles.md) · [Architecture](architecture.md) ·
[Roadmap](roadmap.md)

Why EasyEcs is the way it is: each record states what was decided, why, and what was weighed against it (with the
BenchmarkDotNet numbers where a measurement decided). A record is rewritten when the decision changes; a dropped
feature stays here with its reason, so it is not proposed again without new facts.

| # | Decision |
|---|---|
| [D1](#d1-state-changes-are-data-no-events) | State changes are data: no events or callbacks |
| [D2](#d2-structural-changes-apply-at-once-no-command-buffers) | Structural changes apply at once: no command buffers |
| [D3](#d3-no-command-buffer-for-parallel-jobs) | No command buffer for parallel jobs |
| [D4](#d4-no-addedt-filter) | No `Added<T>` filter |
| [D5](#d5-order-by-dependencies-not-priorities) | Order systems by dependencies, not priorities |
| [D6](#d6-removing-a-component-keeps-the-row) | Removing a component keeps the row |
| [D7](#d7-contiguous-columns-not-pages) | Contiguous columns, not pages |
| [D8](#d8-no-whole-entity-disable) | No whole-entity disable |
| [D9](#d9-saves-are-built-in-with-nino) | Saves are built in, with Nino |
| [D10](#d10-moveentities-copies-rows-without-encoding) | `MoveEntities` copies rows without encoding |
| [D11](#d11-ids-in-a-prefab-name-its-members) | Ids in a prefab name its members |
| [D12](#d12-target-policies-are-opt-in-per-index) | Target policies are opt-in, per index |
| [D13](#d13-one-owner-thread-per-world) | One owner thread per world |
| [D14](#d14-no-shared-components) | No shared components |
| [D15](#d15-net100-only) | .NET 10 only |

## D1. State changes are data: no events or callbacks

**Decision.** Nothing calls user code when a component is added, removed or written. A change that others react to is
a component (a `Dead` tag, a message entity) or a tracked write (`[Changed<T>]`, `[Removed<T>]`).

**Why.** A callback runs inside another system: the control flow is hidden, it can re-enter the code that raised it,
and the scheduler cannot see what it reads and writes, so systems could not run in parallel safely. As data, the order
of effects is the schedule's, the generator knows every access, and tests and snapshots see the "events".

**Considered.** Component events (Friflo, Arch, DefaultEcs); EasyEcs had them once and removed them.

## D2. Structural changes apply at once: no command buffers

**Decision.** Adding, removing, creating and destroying take effect immediately, also in the middle of a query: removed
rows become tombstones while a pass runs, the world closes them when the last pass ends, and a pass never sees rows
shift under it.

**Why.** A deferred change is a second, later version of the world that every reader must reason about; an immediate
one reads as it is written. The tombstones cost one id check per visited row and nothing outside passes.

**Considered.** Command buffers played back after the query (Friflo, Arch, DefaultEcs).

## D3. No command buffer for parallel jobs

**Decision.** Parallel `[ForEach]` methods and parallel stages change no structure (EECS010). A parallel job writes
data; a system after it, on the owner thread, makes the structural change from that data:

```csharp
[ForEach(Parallel = true)]
static void Burn(ref Health hp, in Burning burning) => hp.Value -= burning.DamagePerTick;   // data, in parallel

[ForEach, Without<Dead>]
static void Die(Entity entity, in Health hp) { if (hp.Value <= 0) entity.SetDead(); }        // structure, after it
```

**Why.** Per-thread recorders would bring back D2's deferred second path, with an order between threads to define and
a playback whose cost hides behind the stage. The pattern above uses the schedule the user already reads, and
bulk changes (`SetAll`, `RemoveAll`) make the follow-up pass cheap.

**Considered.** Per-thread command buffers played back when the stage ends (on the 4.1 roadmap, dropped before
implementation).

## D4. No `Added<T>` filter

**Decision.** There is no filter for "gained `T` since a tick". `[Changed<T>]` already selects rows that gained `T`
(adding stamps the tick, as a write does). When a system must tell a new component from a written one, the creator adds
a tag that the reacting system removes:

```csharp
world.CreateEntity(new Health(100, 100), new Spawned());              // the creator says it is new

[ForEach, With<Spawned>]
static void Greet(Entity entity, in Health hp) { /* first-time work */ entity.RemoveSpawned(); } // a bit flip
```

**Why.** A tag is explicit, queryable, saved with the world and costs one presence bit flip. A built-in filter would
need a second change tick per tracked row (8 bytes more per row, one more store on every add) and a second companion
column per component type, for a need the tag covers.

**Considered.** Bevy's `Added<T>`, DefaultEcs' `WhenAdded` (on the 4.1 roadmap, dropped before implementation).

## D5. Order systems by dependencies, not priorities

**Decision.** Systems run in registration order. `[UpdateBefore<T>]` / `[UpdateAfter<T>]` move only the system that
declares them, within its group. Stages come from the ordering graph and the read/write sets: two systems run together
when neither is ordered before the other and their access does not conflict. A cycle throws in `AddSystem` (the world
stays unchanged) and is reported by the analyzer when it can see it (EECS019); an ordering across groups does nothing
(EECS020). `Priority` was removed.

**Why.** Integer priorities from several assemblies collide and say nothing about why a system runs where it does; an
ordering names the dependency. Keeping both would give two mechanisms for one job. Moving only the declaring system
keeps the registration order of everything else, so adding one ordering never reshuffles unrelated systems.

**Considered.** Priorities alongside orderings (rejected: overlap); a lexicographic topological sort (rejected: it
reordered unrelated systems). Tick time unchanged: ParallelSystems 292-345 μs before, 278-325 μs after, over four
runs; WorldSimulation 1,774 μs before, 1,731 μs after.

## D6. Removing a component keeps the row

**Decision.** A removal clears the row's presence bit in its column; only adding a component the table has no column
for moves the row.

**Why.** States that come and go (stunned, burning, dead) then cost a bit flip after the first time, queries test 64
rows per word, and `[Removed<T>]` is a row like any other (its tick says when).

**Considered.** Moving the row on every removal (the classic archetype model): a copy per state change.

## D7. Contiguous columns, not pages

**Decision.** Columns are contiguous arrays.

**Why.** Paged columns iterate at the same speed but add an indirection to every handle access; all they save is the
copy when a table grows, which `InitialEntityCapacity` and `EnsureCapacity` remove.

## D8. No whole-entity disable

**Decision.** There is no "disabled entity" that queries skip by default. A tag and `Without<T>` do it explicitly;
`Disable<T>()` keeps one component's value while hiding it.

**Why.** Excluding by default would make every query depend on a state it does not name.

## D9. Saves are built in, with Nino

**Decision.** `World.Save` / `Load` and `SaveEntities` / `LoadEntities` write tables, with Nino for managed values.
Unmanaged rows are written in a canonical layout with a hash of their fields, so a changed struct is detected at load;
the generator records where `EntityId` fields are, so a migration can point them at the new entities.

**Why.** Rewriting references, loading by type name and reusing rows on repeated loads (0 B allocated) need the
world's tables and the generator; user code over a `Query` could not do them. (Until 4.0 saving was left to the user.)

## D10. `MoveEntities` copies rows without encoding

**Decision.** `world.MoveEntities(target, ids)` copies a group's rows into another world of the thread, rewrites the
ids inside the group and destroys the originals. It shares one core with `LoadEntities` and prefab groups, which copies
runs of consecutive rows column by column.

**Why.** It was kept only after measuring that encoding is a real share of a move: 1,000 entities there and back take
150 μs, against 207 μs through `SaveEntities` + `LoadEntities`; without a hierarchy one way takes 32 μs against 71 μs.

## D11. Ids in a prefab name its members

**Decision.** An `EntityId` field in a prefab names a member of its group (`member.Id`) and is rewritten on every
instance; any other id becomes `EntityId.Null`. Relations to a world's entities are set after instantiating.

**Why.** A prefab belongs to no world: an id of a world's entity may equal a member's, so the two cannot be told apart.

## D12. Target policies are opt-in, per index

**Decision.** `WhenTargetDestroyed(TargetPolicy.Remove / Destroy)` on an `EntityId` index or a relation index decides
what destroying an entity does to the links to it; `Keep` is the default (the links stay, findable by the dead id).
`ChildOf` is `Destroy`, and may be changed.

**Why.** Opt-in keeps destroys of worlds without policies as they were. `ChildOf`'s rule is checked inline: through the
generic rule list the cascade benchmark took 650 μs instead of 557 μs; inline, 561-578 μs.

**Considered.** A marker interface on the component type (rejected: the index must be created generically, which needs
reflection; a call on the index is AOT-safe and per world, like `GroupBy<T>()`).

## D13. One owner thread per world

**Decision.** A world's systems, queries and structural changes run on its owner thread; parallel stages and parallel
`[ForEach]` are the only parallelism, and the generator proves them safe. Another thread posts to the owner.

**Why.** No locks on any hot path; the rules fit in three lines ([threading](guide/threading.md)).

## D14. No shared components

**Decision.** No `SetSameAs`-style shared values. Shared data lives in a singleton, components hold an id into it.

**Why.** A shared value is a reference that one write changes for many entities, which change tracking and the
scheduler could not see.

## D15. .NET 10 only

**Decision.** EasyEcs targets `net10.0`, no Unity or older runtimes.

**Why.** It relies on C# 14 extension members, `Vector256`, pinned object heap columns and current JIT behavior that
older runtimes lack.
