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
- Added rows are indexed per column (a value index reuses the key's list head across rows of one parent): 1,000
  entities with a hierarchy there and back, `SaveEntities` + `LoadEntities` 205 → 195 μs, `MoveEntities` 146 →
  139 μs; what remains is linking each child, which the index needs.
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
- The battle server uses every feature (`benchmarks/EasyEcs.Benchmark/BattleServer.cs` = `docs/content/example.md`): a `Rules`
  singleton, combat split into damage and statuses, freezing through `Disable`, NPC respawn through a `Spawn` cleanup
  component, projectiles as `ChildOf` children, zones as groups, a kill-streak leaderboard (ordered index), `Friend`
  relations with a target policy, `Maybe<T>`, update phases, `[ForEach]` runners, bulk changes, saves, migrations and
  introspection; a test per feature. Its ticks allocate nothing once its tables reach their peak populations (1,200
  ticks).
- `Maybe<T>` in a method that changes no structure finds its column once per run of rows and tests a presence bit
  per row (100,000 units, half with the optional component: 238 → 114 μs; split into a With and a Without method
  40 μs); ticks unchanged (WorldSimulation, Runner within noise).
- Benchmarks for groups (100,000 players, 4 zones of 16: group 46 μs, index 682 μs, scan 710 μs), ordered indexes
  (top 10 and a range: 69 μs against a 308 μs scan), relations, `Maybe<T>`, cleanup components, introspection and
  ordering; peer comparisons for hierarchies, relations, whole-entity disable, structural changes during iteration,
  value queries, parallel queries and saves (each checked in setup).
- .NET 10 features where they measure: counting tested rows by their set bits, 8 words per step with AVX-512
  (100,000 rows 48.9-54.5 μs -> 1.21-1.27 μs), the query cache's alternate lookup (a filter past the memo 92-106 ns
  -> 86-90 ns), the memo's ways as structs in one array, `Lock` everywhere, `params ReadOnlySpan`, generators on
  implementation output and C# 14 ([D21](decisions.md#d21-net-10-features-only-where-they-measure)).

- Performance with BDN proof: parallel runs on the thread pool, helpers woken only when they pay
  ([D22](decisions.md#d22-parallel-runs-use-the-thread-pool-and-wake-helpers-only-when-they-pay)); change summaries per
  block and removals summarized apart (10 removals 1.09 -> 0.87 μs, 10 changed rows 609 -> 514 ns,
  [D23](decisions.md#d23-change-summaries-per-block-removals-summarized-apart)); index keys as id arrays (an index lookup
  710 -> 156 μs, [D24](decisions.md#d24-an-indexs-key-holds-its-entities-in-one-array)); structural row loops without
  the tombstone test for tables without tombstones (10 kills per tick 85.7 -> 80.9 μs,
  [D25](decisions.md#d25-structural-row-loops-are-compiled-for-tables-without-tombstones)); `SetAll` / `EnableAll` note a
  word of rows once.
- Peer comparisons for bulk changes (a stun on 100,000 fighters and lifted: 91 μs against Arch 1,169 μs, DefaultEcs
  9,412 μs, Friflo 11,051 μs), bulk create and destroy (10,000 shots: 142 μs against Friflo 378 μs, Arch 392 μs,
  DefaultEcs 1,279 μs) and parallel systems (DefaultEcs' `ParallelSystem`: 373 against 419 μs); a benchmark of
  `RemoveTypes` (EasyEcs.Benchmark.Plugin).

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
- A flat `[InlineArray]` memo, `FrozenDictionary` for saved types, `OverloadResolutionPriority`, `field`: measured or
  weighed, no net win ([D21](decisions.md#d21-net-10-features-only-where-they-measure)).

### Toolchain and runtime

The toolchain floor is the .NET 10 SDK (C# 14, Roslyn 5.0), which the generator builds against.

### Less code through the generator

What the generator can write so users write less, Entitas-style (it already writes `entity.Health`, `HasHealth`,
`SetHealth(…)`, `IsDead`, singletons, `world.XIndex`, jobs and access sets). Once everything above has shipped, scan the
API and the examples several times: every line users write twice is a candidate, every generated member that nobody
needs goes. Each round's findings are listed here with what was generated, changed or rejected, and why.

- Round 1 (BattleServer, WorldSimulation, README and guide samples):
  - generated: buffer members (`entity.Loot`, `HasLoot`, `WriteLoot()`, `GetOrAddLoot()`, `RemoveLoot()` for a struct
    the compilation names in `DynamicBuffer<Loot>`; 10 generic calls in the battle server) and `WriteHealth()` for a
    tracked component (its ref write, read by the analyzer like `Get`);
  - changed in the examples: 21 hand-written jobs (17 with `Writes => 0`) become static `[ForEach]` runners, which
    the generator already writes (with the battle server and the docs below);
  - rejected: a generated `world.AddSystems()` (systems run in registration order: the list is the tick's order and
    should be read where the world is built), singletons added when a `[ForEach]` names them (a missing singleton is a
    forgotten setup, and its default is rarely right: a clock with no step), cached filters from attributes (saves no
    lines);
  - kept: every generated member is used by an example or a test.
- Round 2 (the battle server, tests, guides; EasyEcs.Core for code written twice):
  - generated: `world.GetOrAddClock()` for a singleton (`ref`, adds it if missing; 2 generic calls in the battle server,
    4 in the guides);
  - changed in EasyEcs.Core: single-component `Set`, `Enable` and the bulk ops share `SetRow` / `EnableRow`, the
    `QueryFilter` builders one copy step, the stamps one tick-column lookup;
  - rejected: counting and testing members per component (`world.CountPlayer()`: no line saved, twice the surface); a
    named `TryGetXRef` (one call site); one enumerator for value and relation indexes (two public types renamed for
    about 20 lines); one copy core for `CloneRows` and `CopyRun` (`Instantiate` would test presence per row); one scan
    for `Count` and `Any` (a query hot path for about 15 lines).
- Round 3 (a review of the generator, analyzer and code fixes for correctness, caching and missed members):
  - generated: named query items (`item.Position` per `Query<…>` shape; 2 in the battle server, 6 in tests and guides)
    and prefab buffer writers (`prefab.SetLoot(…)` on the new `Prefab.SetBuffer`: a prefab could only hold an empty
    buffer before);
  - changed: generated names unique per method and type, each buffer element and query shape cached on its own, code
    that compiles for every valid input; analyzer reports on structural calls it missed and none on valid code;
    EECS024 for `[ForEach]` on a local function; code fixes only where the result compiles;
  - rejected ([D26](decisions.md#d26-generated-members-where-code-would-repeat-a-type-not-for-every-call)):
    multi-type `Disable` / `Enable` and bulk calls, and a "use the generated member" diagnostic.

### Documentation (last, all at once)

- `docs/content/performance.md`: a coverage table (feature → test file → benchmark → peer comparison) and current numbers; the
  peer rows not won (10 removals, [D23](decisions.md#d23-change-summaries-per-block-removals-summarized-apart)) with
  their reasons.
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
- Last: every mermaid diagram (architecture.md, performance.md, the guides, README) redrawn to match the code.
