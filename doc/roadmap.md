# Roadmap

What is planned, in order. An item leaves this page when it ships (the release notes and the guides describe it);
a dropped item stays one line here, its reasons in [design decisions](decisions.md). Every performance item is proven with
BenchmarkDotNet before and after, and ships only as a net win in time and allocations.

## 4.1

### Shipped on the branch

- Saves: `World.Save` / `Load`, `SaveEntities` / `LoadEntities` (Nino; canonical unmanaged rows with a field hash;
  generated `EntityId` offsets; a repeated `Load` allocates nothing).
- Bulk changes: `SetAll`, `RemoveAll`, `DisableAll`, `EnableAll` over a `QueryFilter` (100,000 entities: stun and
  lift 91 μs against 1,617 μs entity by entity; disable and enable 21.6 μs against 2,267 μs).
- Ordering by dependencies: `[UpdateBefore<T>]` / `[UpdateAfter<T>]` replace `Priority`; stages from the dependency
  graph and the read/write sets; cycles reported by EECS019 and thrown by `AddSystem`, orderings across groups by
  EECS020. Ticks unchanged (ParallelSystems, WorldSimulation within noise).
- Prefabs: `EnsureCapacity(prefab, count)`, derived prefabs (`new Prefab(basePrefab)`), groups (`AddMember`, ids
  between members rewritten per instance).
- `MoveEntities(target, ids)`: a group to another world in memory (1,000 entities there and back: 150 μs against
  207 μs through `SaveEntities` + `LoadEntities`; without a hierarchy one way 32 μs against 71 μs).
- Target policies: `WhenTargetDestroyed(TargetPolicy.Remove / Destroy)` on `EntityId` indexes and relations
  (`ChildOf` is `Destroy`; cascade benchmark unchanged).
- Introspection: `world.Describe()` (tables, columns, rows, capacity, bytes), `entity.Describe()`.
- Nino 4.0.0-rc.4.
- Static `[ForEach]` methods outside systems: the generator writes the job and an entry point taking the world and
  the caller's values (`Reports.CountAlive(world, ref alive)`); EECS005 / EECS021 / EECS010 guard it. Counting
  100,000 fighters: 38 μs, against 56 μs for the struct job written by hand and 138 μs for a `Query` loop.
- No `partial`: the generated jobs live outside the user's type and call private `[ForEach]` methods through
  `[UnsafeAccessor]` (the same machine code as a direct call). Systems register a `SystemCode` from a module initializer
  (held weakly; base types' code runs first, across assemblies). Runner entry points are static extension members.
  EECS001 is gone, and EECS022 reports types the generated code cannot name. Ticks unchanged (ParallelSystems,
  ForEachSystemWrite within noise); WorldSimulation at 100,000 entities 1,687-1,701 μs against 1,838-1,850 μs.
- Unloading game logic: `world.RemoveTypes(assembly or context)` removes an assembly's systems, singletons, indexes,
  groupings and its components from every entity (the entities stay; tables with such a column are dropped with their
  edges and cache entries). When a collectible context unloads, its save names are forgotten and its component bits
  are reused once no live world mentions them. A reload keeps the data: `Save`, remove, unload, load the new version,
  `Load`. Steady state unchanged (CreateAddDestroy 1.02-1.03 ms against 1.06-1.08 ms, Prefab and WorldSimulation
  within noise, same allocations).
- Generator and analyzer on Roslyn 5.0: the analyzer knows generated writers by their extension-block symbols (a
  component named `Settings` is read through `entity.Settings`, which counted as a write and hid an EECS013); no build
  warnings.
- `QueryFilter` without object references (bits past 255 interned): a filter built at the call costs 76 ns instead of
  124 ns (counting 16 entities; 24 ns from a static field) ([D18](decisions.md#d18-no-interceptors-for-query-calls-filters-carry-no-references)).
- Generic systems (`class CountOf<T> : EcsSystem`) and generic runners (`Census.CountAlive<Health>(world, ref n)`,
  `Census<Health>.Positions(world, ref x)`); EECS004 only for a generic method of a system
  ([D20](decisions.md#d20-generic-systems-register-the-closed-types-their-assembly-names)).
- Filters on system groups: `[Without<Inactive>] public struct Gameplay : ISystemGroup;` joins every `[ForEach]` of
  the group's systems (EECS023 on any other struct).
- Code fixes (EasyEcs.CodeFixes, in the package beside the generator): EECS002 / EECS012 take the parameter by `in`
  or `ref`, EECS005 makes the method static, EECS006 makes the type a struct, EECS010 drops `Parallel = true`, EECS011
  takes it by `ref` (or `Span`), EECS022 makes the type internal, EECS023 removes the filter.
- .NET 10 features where they measure: the query memo's ways as structs in one array (a static filter 21.6-21.9 ns ->
  19.0-19.8 ns, no filter 36.8-39.1 ns -> 35.3-35.5 ns), `Lock` everywhere, `params ReadOnlySpan` for
  `SavedTypes.EntityIds` ([D21](decisions.md#d21-net-10-features-only-where-they-measure)).

### Dropped

- `Added<T>`: `[Changed<T>]` selects added rows too; a tag the reacting system removes tells new from written
  ([D4](decisions.md#d4-no-addedt-filter)).
- Command buffers for parallel jobs: a parallel job writes data, a system after it changes structure
  ([D3](decisions.md#d3-no-command-buffer-for-parallel-jobs)).
- Interceptors for query call sites: the call's arguments are still evaluated, so a filter built at the call is built
  anyway ([D18](decisions.md#d18-no-interceptors-for-query-calls-filters-carry-no-references)). `AddEmbeddedAttributeDefinition`:
  the generators write no attribute types (the attributes are EasyEcs.Core's).
- Settable tag properties (`entity.IsDead = true`): C# 14 rejects assigning an extension property of a struct on a
  `foreach` variable, an indexer result or a field of an `in` parameter, where ECS code holds its entities
  ([D19](decisions.md#d19-no-settable-tag-properties)).
- `[InlineArray]`, `GetAlternateLookup` for the query cache, `FrozenDictionary` for saved types,
  `OverloadResolutionPriority`, `field`, `Vector512`: no place where they pay
  ([D21](decisions.md#d21-net-10-features-only-where-they-measure)).

### Toolchain and runtime

The toolchain floor is the .NET 10 SDK (C# 14, Roslyn 5.0), which the generator builds against.

### Less code through the generator

What the generator can write so users write less, Entitas-style (it already writes `entity.Health`, `HasHealth`,
`SetHealth(…)`, `IsDead`, singletons, `world.XIndex`, jobs and access sets). Once everything above has shipped, scan the
API and the examples several times: every line users write twice is a candidate, every generated member that nobody
needs goes. Each round's findings are listed here with what was generated, changed or rejected, and why.

- Round 1: pending.

### Example

- The battle server (`EasyEcs.Benchmark/BattleServer.cs` = `doc/example.md`) uses every feature:
  - a rules singleton;
  - `Fight` split into damage and status systems;
  - `Frozen` through `Disable`;
  - respawn through cleanup components;
  - `ChildOf`;
  - an ordered index of kills;
  - a friend relation;
  - zones as groups;
  - `Maybe<T>`;
  - update phases;
  - saves;
  - bulk changes;
  - ordering by dependencies.

### Performance

Each item: profile (asm on x64 and arm64 where it matters), optimize, BDN before and after.

- Moves and loads with a hierarchy: keeping the `ChildOf` index doubles their cost (1,000 entities one way: 66 μs
  against 32 μs without); index runs of rows at once instead of row by row.
- New-feature benchmarks: groups, cascades, relations, ordered walks, `Disable`, `Maybe<T>`, bulk changes, saves,
  migrations, prefabs, introspection, ordering.
- `EasyEcs.Comparison`, against Friflo, Arch and DefaultEcs where they have the feature:
  - hierarchy with cascade;
  - multi-target relations;
  - structural changes during iteration vs. command buffers;
  - component enable/disable, and whole-entity disable against Friflo's `Enabled` and DefaultEcs' `Disable` (frequent
    switching; many entities disabled for long);
  - ordered range queries;
  - groups vs. value indexes;
  - serialization and migration (formats labelled);
  - clone / instantiate;
  - bulk create / destroy / add / remove;
  - parallel queries;
  - parallel systems;
  - the Removal rows still marked TBD.
- Every comparison row is won, or the gap is explained with numbers.

### Documentation (last, all at once)

- Every page checked against the code:
  - every feature with its usage and a best-practice example;
  - current numbers;
  - stale text fixed, redundant text removed.
- `MIGRATION_GUIDE.md` and the README "What's new in 4.1".
- `analyzer.md` with every diagnostic.
- `api.md` complexities.
- A saves guide: Nino versioning, `NinoArrayBufferWriter`, components in non-collectible assemblies.
- `comparison.md` rewritten.
- `faq.md`: stale answers (no serialization, `Priority`) replaced by links into `decisions.md`.
- The whole-entity disable pattern (a tag and `Without<T>`, [D8](decisions.md#d8-no-whole-entity-disable): what it
  does, where it is used, how Friflo and DefaultEcs do it and what each costs, with measured numbers), and the
  `Spawned` tag pattern ([D4](decisions.md#d4-no-addedt-filter)) as best-practice examples.
- `CLAUDE.md` layout.
