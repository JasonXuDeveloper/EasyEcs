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
| [D16](#d16-no-partial-generated-code-lives-outside-the-users-type) | No `partial`: generated code lives outside the user's type |
| [D17](#d17-unloading-removes-types-from-worlds-bits-are-reused) | Unloading removes types from worlds; bits are reused |
| [D18](#d18-no-interceptors-for-query-calls-filters-carry-no-references) | No interceptors for query calls; filters carry no references |
| [D19](#d19-no-settable-tag-properties) | No settable tag properties |

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
`Disable<T>()` keeps one component's value while hiding it:

```csharp
public struct Inactive : IComponent;                        // the whole entity is off

bullet.SetInactive();                                       // back to the pool: a bit flip, no row moves
bullet.SetPosition(x, y).RemoveInactive();                  // reused

[ForEach, Without<Inactive>]                                // the systems that skip it say so
static void Move(ref Position p, in Velocity v) { /* ... */ }

int pooled = world.Count<Bullet>(QueryFilter.Create().With<Inactive>()); // saves, cleanup, stats see everything
```

Uses: object pools (bullets, effects), players kept in memory while they reconnect, zones without players whose NPCs
sleep.

**Why.** Excluding by default would make every query depend on a state it does not name: a save, a cleanup or a count
that must see every entity has to remember to opt back in, and forgetting it fails silently. The tag is data the
generator and the reader both see, as the ECS principles ask.

**Considered.** Friflo (`entity.Enabled = false`: a hidden tag in the archetype, queries skip it unless
`WithDisabled()`); DefaultEcs (`entity.Disable()`: the entity leaves every entity set). Performance by case:

| | Friflo | DefaultEcs | EasyEcs |
|---|---|---|---|
| Switching an entity off or on | moves its row to another archetype | updates every set | flips a presence bit (after the first time) |
| Iterating past disabled entities | skips their archetype | they are in no set | free while a table has none (counts, no bits read); 64 rows per test when mixed |

Pools and effects switch often, where a bit flip beats a row move. Only many entities disabled for long, mixed with
enabled ones in one table, cost EasyEcs a word test per 64 rows that Friflo skips; `GroupBy` on a sleeping state
splits them into their own tables when that matters. The repetition of `Without<Inactive>` in every method is what
the group-wide filter on the roadmap removes.

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

## D16. No `partial`: generated code lives outside the user's type

**Decision.** The generator writes a `[ForEach]` method's job in a `file` class of its own and calls the method,
private or not, through `[UnsafeAccessor]`. A system's update and access set are a `SystemCode` registered by a module
initializer (held weakly, so a collectible assembly can unload); a runner's entry point is a C# 14 static extension
member of its type. Types the generated code cannot name (a private component, a private runner type) are EECS022.

**Why.** A system is an ordinary class: no `partial` on it or on its outer types, nothing generated in its body, so
generated members cannot collide with the user's. `UnsafeAccessor` compiles to the direct call (the same machine
code, inlined), and the ticks did not move (ForEachSystemWrite, ParallelSystems within noise).

**Considered.** Partial classes (EECS001 when missing, members injected into the user's type); delegates or function
pointers to the methods (an indirect call per row, no inlining); reflection at startup (no AOT).

## D17. Unloading removes types from worlds; bits are reused

**Decision.** `world.RemoveTypes(assembly or context)` takes an assembly's systems, singletons, indexes, groupings and
components out of the world. Entities stay; tables that had one of its columns are dropped with every cache naming
them. When a collectible context unloads, EasyEcs forgets its save names, and its component bits are reused once no
live world mentions them. A reload keeps data through `Save` and `Load`, which map types by name.

**Why.** Game logic reloaded in a long-running server must leave nothing rooting the old assembly (column arrays,
transition edges, query cache keys, registries), and repeated reloads must not grow every mask. Dropping tables is
the one place a table is deleted outside `Dispose`, so steady-state code pays nothing (CreateAddDestroy, Prefab and
WorldSimulation within noise).

**Considered.** Migrating rows from the old types to the new ones in memory (needs a type map for every field; `Save`
/ `Load` already map by name and field hash); never reusing bits (the mask grows by every reloaded component).

## D18. No interceptors for query calls; filters carry no references

**Decision.** Query calls are not intercepted. `QueryFilter` holds its five sets' first 256 bits inline and its bits
past 255 as an interned id, so it holds no object reference, and its builder copies it once per call.

**Why.** An interceptor replaces the method a call binds to, but the call's arguments are still evaluated, so a
filter built at the call is built either way; caching a query entry per call site would save only the memo lookup the
call already makes. The cost was the filter itself: five masks with an overflow reference each made every builder
call two copies through `BulkMoveWithWriteBarrier` and zeroed the caller's frame. Without references, counting 16
entities with `QueryFilter.Create().With<Npc>().Without<Dead>().Without<Frozen>()` built at the call takes 76 ns
against 124 ns, and with the filter in a static field 24 ns as before (`CallSiteBenchmark`). Hoisting stays the
advice for hot calls.

**Considered.** Interceptors with a static filter and a cached entry per site (the arguments make it moot); an
analyzer warning for filters built at the call (most calls are cold; the docs teach the static field).

## D19. No settable tag properties

**Decision.** A tag's generated `IsX` is read-only; `SetX()` adds the tag and `RemoveX()` removes it.

**Why.** `entity.IsDead = true` (Entitas style) cannot be written where ECS code holds its entities: C# 14 rejects
assigning an extension property of a struct on a `foreach` variable (CS1654), an indexer result (CS1612) or a field of
an `in` parameter (CS8332), with a by-value or an `in` receiver alike. A member that compiles on a local but not in
`foreach (var e in hits)` or on `link.Target` teaches the wrong thing; the methods work everywhere, and a structural
change stays a call.

