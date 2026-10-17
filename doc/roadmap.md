# Roadmap

What is planned, in order. An item leaves this page when it ships (the release notes and the guides describe it);
an item dropped after measuring stays one line with the reason and the numbers. Every performance item is proven with
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

### Features

| Item | What | Why |
|---|---|---|
| `Added<T>` | Tell rows that gained a component since a tick from rows that changed it. | Measure the cost of the extra tick per row first. |
| Command buffer for parallel jobs | Per-thread recorders inside parallel `[ForEach]` / parallel stages, played back when the stage ends. | Parallel jobs cannot change structure (EECS010). Check it does not overlap the immediate structural changes. |
| Whole-entity disable | A documented pattern (a tag and `Without`), no API: excluding by default would be implicit. | |

### Source generators

- `[ForEach]` on static methods outside systems.
- A cached query entry per system (no lookup per update).
- Code fixes for the analyzer's diagnostics.
- A diagnostic that suggests hoisting a `QueryFilter` built per update into a `static readonly` field.

### Less code through the generator (after the items above)

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
  - component enable/disable;
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
- `CLAUDE.md` layout.
