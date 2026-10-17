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

### Dropped

- `Added<T>`: `[Changed<T>]` selects added rows too; a tag the reacting system removes tells new from written
  ([D4](decisions.md#d4-no-addedt-filter)).
- Command buffers for parallel jobs: a parallel job writes data, a system after it changes structure
  ([D3](decisions.md#d3-no-command-buffer-for-parallel-jobs)).

### Source generators

- A cached query entry per system (no lookup per update): measure the lookup first.
- Code fixes for the analyzer's diagnostics.
- A diagnostic that suggests hoisting a `QueryFilter` built per update into a `static readonly` field.

### Less code through the generator (after the items above)

What the generator can write so users write less, Entitas-style (it already writes `entity.Health`, `HasHealth`,
`SetHealth(…)`, `IsDead`, singletons, `world.XIndex`, jobs and access sets). Once everything above has shipped, scan the
API and the examples several times: every line users write twice is a candidate, every generated member that nobody
needs goes. Each round's findings are listed here with what was generated, changed or rejected, and why.

- Round 1: pending. Candidates so far: a settable tag property, Entitas-style (`entity.IsDead = true` adds or
  removes the tag: weigh a structural change hidden in an assignment, and the analyzer must count it as one).
  A filter declared once on a system group (every `[ForEach]` of the group gets `Without<Inactive>`): the
  whole-entity disable pattern without one attribute per method, still explicit where the group is defined.

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
