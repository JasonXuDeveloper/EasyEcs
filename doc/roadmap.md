# Roadmap

What is planned, in order. An item leaves this page when it ships (the release notes and the guides describe it);
an item dropped after measuring stays one line with the reason and the numbers. Every performance item is proven with
BenchmarkDotNet before and after, and ships only as a net win in time and allocations.

## 4.1

### Shipped on the branch

- Saves: `World.Save` / `Load`, `SaveEntities` / `LoadEntities` (Nino; canonical unmanaged rows with a field hash;
  generated `EntityId` offsets; a repeated `Load` allocates nothing).
- Bulk changes: `SetAll`, `RemoveAll`, `DisableAll`, `EnableAll` over a `QueryFilter`.

### Features

| Item | What | Why |
|---|---|---|
| Ordering by dependencies | `[UpdateBefore<T>]` / `[UpdateAfter<T>]` replace `Priority`. Registration order is the default; parallel stages come from the dependency graph and the read/write sets (unordered, non-conflicting systems run together, across what used to be priorities). Cycles: analyzer at compile time, `AddSystem` at run time. | Integer priorities are fragile when systems come from several assemblies; two ordering mechanisms would overlap. |
| Capacity by prefab | `world.EnsureCapacity(prefab, count)`. | A wave of spawns grows its table once, before the tick. |
| Derived prefabs | `new Prefab(basePrefab)`, then change it. | Variants of a configured NPC; a copy, no run-time inheritance. |
| Prefabs of several entities | A prefab group (boss + minions + mount) instantiated at once, internal `EntityId`s pointing at the new entities. | Shares the remapping of `LoadEntities`. |
| Moving entities between worlds in memory | `world.MoveEntities(target, ids)`: rows copied, references remapped, no encoding. | Measure first: kept only if encoding dominates `SaveEntities` + `LoadEntities`. |
| Relations to destroyed targets | An opt-in policy per relation type: links to a destroyed target are removed (or their sources destroyed). | Today the links stay findable by the dead id and a system cleans them. |
| `Added<T>` | Tell rows that gained a component since a tick from rows that changed it. | Measure the cost of the extra tick per row first. |
| Command buffer for parallel jobs | Per-thread recorders inside parallel `[ForEach]` / parallel stages, played back when the stage ends. | Parallel jobs cannot change structure (EECS010). Check it does not overlap the immediate structural changes. |
| Read-only introspection | Tables with row counts, component types, memory and fragmentation; an entity's components (boxed, cold). | Production diagnostics behind an admin endpoint. |
| Whole-entity disable | A documented pattern (a tag and `Without`), no API: excluding by default would be implicit. | |

### Source generators

- `[ForEach]` on static methods outside systems.
- A cached query entry per system (no lookup per update).
- Code fixes for the analyzer's diagnostics.
- A diagnostic that suggests hoisting a `QueryFilter` built per update into a `static readonly` field.

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
