# Plan: the directory layout of the whole repository

Owner's request (2026-10-26): "调整整个项目的目录结构" (restructure the directory layout of the whole project).
Backlog item: `docs/plans/backlog.md`, Finish, "Directory restructure of the whole repository": `git mv` only, after
the running agent branches merge; the plan goes to the owner first. This is that plan. Base: master at `2515638`.

## 0. Summary

- **What moves**: the files of four projects go into folders by concern: `src/EasyEcs.Core` (79 files, flat today),
  `src/EasyEcs.Generator` (47), `tests/EasyEcs.UnitTest` (70), `benchmarks/EasyEcs.Benchmark` (67 at its root; `Locality/`
  is already a folder). The folders use the same names across the four projects, so a feature's core, tests and
  benchmarks sit under one name: `Indexes/` in Core, in the tests and in the benchmarks.
- **What does not move**: the projects themselves, the repository's top level, and every file another tool or a page
  reaches by path: `BattleServer.cs`, `WorldSimulation.cs`, `BenchmarkConfig.cs`, `BattleServerTests.cs`,
  `AllocationTests.cs`, `Locality/`, `src/EasyEcs.Core/build/`, `skills/easyecs/SKILL.md`.
- **C#**: `git mv` only. Namespaces stay `EasyEcs` (and `EasyEcs.Benchmark`, ...). No source line changes.
- **Paths that must change**: two tools that read a directory without recursing (`tools/agents.sh:61`,
  `docs/vite.config.ts:148-153`), one skill command (`audit-naming` SKILL.md:30), and the file maps (four AGENTS.md).
  Everything else that names a path was checked and stays valid (section 3).
- **Users**: nothing changes. The package holds the same files at the same package paths; the API is unchanged
  (section 6).
- **Order**: tools first, then one commit per project, each green on its own (section 7).

## 1. Principles of the layout

1. **A feature's files together.** A reader looking for indexes opens `Indexes/` and finds `ValueIndex.cs`,
   `UniqueIndex.cs`, `OrderedIndex.cs` and `World.Indexes.cs`. The `World.*.cs` partials go to the concern they implement,
   not into a `World/` folder: `World.Indexes.cs` is the index feature's entry point.
2. **One vocabulary.** The folder names follow the docs' guide pages and glossary (ordered table, vector loop,
   `[ForEach]` parameters, saves, diagnostics) and are the same in Core, tests and benchmarks.
3. **The entry stays at the root.** `World.cs` (entity lifecycle, the class readers open first) and `WorldOptions.cs`
   stay at the Core root, beside the csproj, `build/`, AGENTS.md and CLAUDE.md.
4. **Files reached by path stay where they are.** Moving them would change csproj links, docs includes and GitHub URLs
   for no reader's benefit (section 3).
5. **Nothing moves in projects of fewer than about 20 files.** `src/EasyEcs.CodeFixes` (7), `tests/EasyEcs.Aot` (1),
   `tests/EasyEcs.Aot.Library` (1), `benchmarks/EasyEcs.Comparison` (15), `benchmarks/EasyEcs.Benchmark.Plugin` (1),
   `tools/*` and `demo/EasyEcs.Demo` (already `Systems/`) stay as they are.

## 2. The proposed folders

### 2.1 `src/EasyEcs.Core` (79 files: 2 at the root, 77 in 16 folders)

| Folder | Concern (guide page) | Files |
|---|---|---|
| (root) | the world: entity lifecycle, options | 2 |
| `Entities/` | handles, ids, per-entity record, prefabs, the destroy log (entities-and-components, prefabs) | 5 |
| `Components/` | the markers and declarations, per-type column operations, singletons, buffers, cleanup, disable (entities-and-components, singletons, dynamic-buffers, cleanup-and-disable) | 9 |
| `Storage/` | tables, masks, the structural gate, bulk changes, marked rows, groups, trim (structural-changes, bulk, groups) | 9 |
| `Queries/` | filters, slices, plans, the run of a batch job (queries, change-tracking) | 4 |
| `Systems/` | `[EcsSystem]`, `[ForEach]` and its attributes, handles, groups, ordering, access, the schedule (systems) | 9 |
| `Parameters/` | the `[ForEach]` parameters: `Maybe`, `Mut`, `Reduce`, `Scratch`, `Emit`, `EntityRandom` (systems, threading) | 6 |
| `Lookups/` | `Lookup<T>`, its lanes and prefetches, the last-level cache gate | 3 |
| `VectorLoops/` | the helpers generated vector loops call: interleaved loads, lane math, compaction | 4 |
| `Parallel/` | the runner, `ForParallel`, helper threads, the spin gate (threading) | 4 |
| `Indexes/` | value, unique and ordered indexes (indexes-and-relations) | 4 |
| `Relations/` | relations, hierarchy, target policies (indexes-and-relations, hierarchy) | 4 |
| `Grids/` | tiles (`Cell<,>` joins it with inner-loops WP5) (grids) | 1 |
| `OrderedTables/` | row order, blocks and boxes (the ordered-tables page of the docs pass) | 3 |
| `Saves/` | snapshots, saves, migration, unload of a plugin's types (saves, snapshots, migration-and-unload) | 8 |
| `Diagnostics/` | introspection, debugger view, schedule description, system stats (diagnostics) | 4 |

Placement calls a reader could argue (each could go either way; the table picks the guide page that teaches it):
`World.Groups.cs` (Storage: groups split tables), `ForEachAttributes.cs` (Systems: the method attributes),
`EntitySpan.cs` (Queries: the run a batch job gets), `Declarations.cs` (Components: the attributes whose code the
generators write), `TypeRegistry.cs` / `ComponentSet.cs` (Saves: they exist for saves), `Prefab.cs` (Entities: a
recipe of an entity), `LastLevelCache.cs` (Lookups: the gate of the prefetched chases).

### 2.2 `src/EasyEcs.Generator` (47 files in 6 folders)

| Folder | Concern | Files |
|---|---|---|
| `Arity/` | `ArityGenerator` (the T1..T9 families of the core) | 1 |
| `Declarations/` | the declaration generators and the models they share with the analyzer: Component, Part (+ `Parts.cs`), Prefab (+ `Prefabs.cs`), View (+ `Views.cs`) | 7 |
| `Systems/` | `SystemGenerator*.cs`, fusion (`Fusion.cs`), lambda passes (`LambdaPass.cs`), chases (`Chases.cs`) | 8 |
| `Kernel/` | `Kernel*.cs`: the vector loops | 9 |
| `Analyzer/` | `EasyEcsAnalyzer*.cs`, the suppressor, `Uses`, `Threads`, `BodyFacts`, `HandSimd` (EECS103's detection) | 18 |
| `Shared/` | `Symbols`, `Code`, `EquatableArray`, `Polyfills` | 4 |

`docs/plans/docs-todos.md:67-70` sketched `Generators/ Kernel/ Analyzer/ Shared/`. This plan splits `Generators/` into
`Arity/`, `Declarations/` and `Systems/`: the system generator alone is 8 files and owns fusion, lambda passes and
the chases, and `audit-generator` / `audit` already treat the declaration surface and the system generator apart. The
models read once for the generator and the analyzer (`Parts.cs`, `Prefabs.cs`, `Views.cs`, `Fusion.cs`,
`LambdaPass.cs`) sit with their generator; the analyzer reads them from there.

### 2.3 `tests/EasyEcs.UnitTest` (70 files: 4 at the root, 66 in 17 folders)

The Core's folder names, plus `Generator/` (generated text, parts, views, prefab declarations) and `Analyzer/`
(the rules, the code fixes, the suppressor). The root keeps `Fixtures.cs` (shared by all), `AllocationTests.cs`
(linked from `guide/prefabs.mdx:342` / `.zh.mdx:315`, and cross-cutting), `BattleServerTests.cs` (linked from
`example.mdx:16` / `.zh.mdx:15`) and `LocalityTests.cs` (goes with `benchmarks/.../Locality/` when L2 ships).

### 2.4 `benchmarks/EasyEcs.Benchmark` (67 files at the root today: 8 stay, 59 in 16 folders)

The Core's folder names plus `Generator/`. The root keeps the harness and the headline worlds: `Program.cs`,
`BenchmarkConfig.cs` (linked by EasyEcs.Comparison), `IsaColumn.cs`, `BattleServer.cs` (linked by three projects,
embedded, included by `example.mdx` 21 times), `BattleServerBenchmark.cs`, `WorldSimulation.cs` (linked by
EasyEcs.Comparison, linked from 8 pages), `WorldSimulationBenchmark.cs`, `MicroBenchmarks.cs`; `Locality/` stays (linked
into the unit tests by `Locality\*.cs`; no benchmark class may go into it, or the tests would compile BenchmarkDotNet
code). Benchmark class names do not change, so `docs/src/data/bench.json` keys, `tools/bench.sh` filters,
`tools/Inlining` and the BenchmarkDotNet report names stay valid.

## 3. Path references: what must change and what was checked

### 3.1 Must change (with the move)

| File:line | Today | Change |
|---|---|---|
| `tools/agents.sh:57-63` | `glob.glob(f"{d}/*.cs")`: only the files directly in `src/EasyEcs.Core` / `Generator` / `CodeFixes`; after the move it finds 2 Core files and 0 Generator files and passes while checking nothing | take the files from the `git ls-files` list it already builds (line 15), `f.startswith(d + "/") and f.endswith(".cs")`, which skips `bin/` and `obj/`; check the path relative to the project (`Storage/Archetype.cs`) instead of the basename; also fail on a map line naming a file that does not exist (stale entries, which the check misses today) |
| `docs/vite.config.ts:148-153` (`analyzerRules`) | `readdirSync(dir)` over `src/EasyEcs.Generator`, not recursive: after the move it finds no `.cs` file and throws "No analyzer rules found" (`:156`), so `bun run build` fails | `readdirSync(dir, { recursive: true })` (Node 22, `.node-version`) and `path.join(dir, file)`; the count must equal today's (compare `analyzerRules()` before and after) |
| `.claude/skills/audit-naming/SKILL.md:30` | `grep -n "^\s*public " src/EasyEcs.Core/*.cs` | `grep -rn --include='*.cs' --exclude-dir=obj --exclude-dir=bin "^\s*public " src/EasyEcs.Core` |
| `src/EasyEcs.Core/AGENTS.md:10-185` | flat file map | sections per folder, each file named with its folder path (section 4) |
| `src/EasyEcs.Generator/AGENTS.md:3, 196-363` | flat map; `> Read when:` names "`Kernel*.cs`" | sections per folder; Read-when: "the generators, the vector loops (`Kernel/`) or the analyzer (`Analyzer/`)" |
| `AGENTS.md:40` | generated from the Generator map's Read-when line | rerun `tools/agents.sh` |
| `AGENTS.md:48-52` (Layout) | "src/EasyEcs.Core/ ... (file map: ...)" | name the folders in one line each project (section 4) |
| `tests/AGENTS.md:12-38` | "tests/EasyEcs.UnitTest/ NUnit, one file per feature: ..." prose | sections per folder (section 4) |
| `benchmarks/AGENTS.md:10-56` | prose list of classes | sections per folder (section 4) |
| `.claude/skills/audit/SKILL.md:16-20` | scopes by file name (`EasyEcsAnalyzer*.cs`, `Code.cs`, ...) | add the folders: `audit-core` `src/EasyEcs.Core/**`; `audit-generator` `Generator/{Arity,Declarations,Systems,Shared}`; `audit-vector-loops` `Generator/Kernel/`, `Core/VectorLoops/`, `Core/Lookups/`; `audit-analyzer` `Generator/Analyzer/` |
| `.claude/skills/audit-analyzer/SKILL.md:10` | "Files in `src/EasyEcs.Generator`: `EasyEcsAnalyzer.cs` ..." | "Files in `src/EasyEcs.Generator/Analyzer`: ..." |
| `.claude/skills/audit-generator/SKILL.md:10` | "Files: `src/EasyEcs.Generator` ... `ArityGenerator`, ..." | name the folders |
| `docs/plans/borrowed-ideas.md:133, 149` | `src/EasyEcs.Core/LastLevelCache.cs`, `src/EasyEcs.Generator/Chases.cs` | `.../Lookups/LastLevelCache.cs`, `.../Systems/Chases.cs` (internal plans, "nothing stale") |
| `docs/plans/inner-loops.md:366, 489-493` | `src/EasyEcs.Generator/HandSimd.cs`, `Kernel.cs`, `SystemGenerator.cs`, `LambdaPass.cs`, `SystemGenerator.Lambdas.cs`, `src/EasyEcs.Core/Tile.cs`, `World.For.cs`, `EasyEcsAnalyzer.Forms.cs` | the new paths |
| `docs/plans/docs-todos.md:67-73` | the old sketch, `tools/filemaps.sh` (never existed: it is `tools/agents.sh`) | replace by a pointer to this plan once done, then drop |

`docs/src/lib/site.ts:30` ("the EECS ids in src/EasyEcs.Generator") stays true. `docs/src/lib/site.ts:58` says
"src/EasyEcs.Core.csproj", already wrong today (the file is `src/EasyEcs.Core/EasyEcs.Core.csproj`): fix it in the
same tools commit.

### 3.2 Checked: no change needed

| What | Where | Why it holds |
|---|---|---|
| csproj globs | all projects (SDK default `**/*.cs`) | recursive by default; no project lists Core or Generator files |
| Linked sources | `tests/EasyEcs.UnitTest/EasyEcs.UnitTest.csproj:35,37` (`BattleServer.cs`, `Locality\*.cs`), `tests/EasyEcs.Aot/EasyEcs.Aot.csproj:24`, `benchmarks/EasyEcs.Comparison/EasyEcs.Comparison.csproj:26-27` (`WorldSimulation.cs`, `BenchmarkConfig.cs`), `benchmarks/EasyEcs.Benchmark/EasyEcs.Benchmark.csproj:31` (EmbeddedResource `BattleServer.cs`) | those files stay at the benchmark root |
| Project paths | `EasyEcs.slnx`, every `ProjectReference`, `.github/workflows/ci.yml:29,44,64-68,92`, `release.yml:96-98`, `templates/smoke.sh:12-13`, `tools/Inlining/Inlining.csproj:14`, `tools/jitdisasm.sh`, `samples/*/*.csproj` | projects do not move |
| Package build file | `Directory.Build.props:10`, `EasyEcs.Core.csproj` `<None Include="build\EasyEcs.props">` | `build/` stays at the Core root |
| `InternalsVisibleTo` | `EasyEcs.Core.csproj:39` (`EasyEcs.UnitTest`) | by assembly name |
| Generator hint names | `AddSource` in ArityGenerator.cs:28-32, ComponentGenerator.cs:60-150, PartGenerator.cs:39, PrefabGenerator.cs:27, ViewGenerator.cs:25, SystemGenerator*.cs | built from symbol names; no generator reads its own source or `AdditionalFiles` (none exist). The generated trees' paths carry the generator's type name, not its folder, so the tests matching `.Fusion.g.cs`, `__EasyEcsSystems.g.cs`, `EasyEcs.TypeRegistry.g.cs` (FusionTests.cs:578-581, GeneratorTests.cs:17-943, AnalyzerTests.Parts.cs:26) still match |
| Paths of user code in the generator | SystemGenerator.Lambdas.cs:35 (hint name from the user file's name + FNV of its path), SystemGenerator.Fusion.cs:358 (a system's place: file path + type) | the user's path: moving a test or benchmark file renames its `*.Lambdas.g.cs` (asserted nowhere); systems paired by fusion share a file, and whole files move |
| DocCheck | `tools/DocCheck/Program.cs:17` | reads `docs/content` and `README.md`; compiles against the built assemblies |
| ApiDocs | `tools/ApiDocs/Program.cs:18-20` | reads EasyEcs.Core's assembly and XML docs; zh entries are keyed by doc id (`tools/ApiDocs/zh/*.xml`) |
| Docs includes | `docs/content/**` `<include>`: `benchmarks/EasyEcs.Benchmark/BattleServer.cs` (example.mdx:97-296, 21 regions, and .zh), `samples/VoxelServer/**`, `demo/EasyEcs.Demo/**` | none of them moves |
| GitHub URLs in pages | example(.zh).mdx:14,16 (BattleServer.cs, BattleServerTests.cs), migrate-from-{arch,friflo,defaultecs}(.zh).mdx:11-16 (WorldSimulation.cs, *Simulation.cs), comparison(.zh).mdx:24-26, guide/prefabs.mdx:342 / .zh.mdx:315 (AllocationTests.cs) | those files stay |
| Docs naming files without a path | decisions.mdx:1977 (`Flow.cs`), :3575 (`HandSimd.cs`), why-fast.mdx:75 (`EntityMeta.cs`) | names unchanged |
| Scripts | `tools/bench.sh` (class names, project dirs), `tools/inlining.sh` + `tools/Inlining/Program.cs:11-14` (types), `docs/scripts/sync-benchmarks.ts:235` (Comparison csproj), `docs/scripts/build-demo.sh:14`, `.github/scripts/check-sample.sh` (sample dirs), `lint-links.ts` (does not check GitHub URLs) | no path into the moved folders |
| Templates | `templates/EasyEcs.Templates.csproj` (`content\**`) | unrelated |
| Editor config | no `.editorconfig`, `.globalconfig` or committed `.DotSettings` | no IDE0130 (namespace must match folder) warning in the build. Rider shows a hint "namespace does not match the file location" in the IDE only; a committed `EasyEcs.Core.csproj.DotSettings` marking the folders as not namespace providers removes it (optional, owner's call) |

## 4. The file maps (AGENTS.md)

Today the rule is one AGENTS.md per indexed directory; `tools/agents.sh` indexes them in the root AGENTS.md and checks
every source file of `src/` is in its map.

**Proposal: no new AGENTS.md.** The folders stay in their project's map, as sections:

```
## Entities/
Entities/Entity.cs           the entity handle (id, version, world): ...
Entities/EntityId.cs         ...
## Storage/
Storage/Archetype.cs         a table: ...
```

Why not one AGENTS.md per folder: 16 Core folders and 6 Generator folders would mean 22 more AGENTS.md + CLAUDE.md
pairs of 3-18 lines each, and an agent working across folders (most core changes touch Storage and Queries together)
would load several. Claude Code loads the CLAUDE.md of every directory above a file it reads, so
`src/EasyEcs.Core/AGENTS.md` still loads for `Storage/Archetype.cs`; Codex loads only from the root to its start
directory, which the root AGENTS.md's instruction ("read its AGENTS.md in full") already covers. The `> Read when:`
lines and the root index stay as they are, except the Generator line (section 3.1).

The maps' text does not change beyond the paths and section headers, and each section gets a one-line purpose (the
"Concern" column of section 2). The lines that describe two files today (`EcsSystem.cs` with `SystemHandle.cs` and
`SystemCode.cs`; `World.Parallel.cs` with `World.For.cs`; `Hierarchy.cs` with `World.Hierarchy.cs`;
`World.Serialization.cs` with `TypeRegistry.cs`; `World.Types.cs` with `Collectible.cs`; `Interleaved.cs` with
`Interleaved.Wide.cs`; `PartGenerator.cs` with `Parts.cs`) are split where their files now sit in different folders
(`World.Serialization.cs` and `TypeRegistry.cs` stay together in `Saves/`).

`tests/AGENTS.md` and `benchmarks/AGENTS.md` become the same kind of sectioned list. `tools/agents.sh` checks only
`src/` maps today; extending the check to `tests/` and `benchmarks/` (every `.cs` named) is cheap once their maps list
paths, and would have caught the stale lines these maps collect (optional, owner's call).

Root AGENTS.md Layout lines 48-52 become:

```
src/EasyEcs.Core/              net10.0 library, namespace EasyEcs, by concern: Entities/ Components/ Storage/ Queries/
                               Systems/ Parameters/ Lookups/ VectorLoops/ Parallel/ Indexes/ Relations/ Grids/
                               OrderedTables/ Saves/ Diagnostics/ (file map: src/EasyEcs.Core/AGENTS.md)
src/EasyEcs.Generator/         netstandard2.0: Arity/ Declarations/ Systems/ (generators), Kernel/ (vector loops),
                               Analyzer/, Shared/ (file map: src/EasyEcs.Generator/AGENTS.md)
tests/                         EasyEcs.UnitTest (NUnit, folders as Core's + Generator/ Analyzer/) ...
```

## 5. The top level: judged, not moved

| Path | Verdict | Why |
|---|---|---|
| `samples/` | stays | runnable programs in learning order; `ci.yml:34-40,111-112`, `check-sample.sh` (slug from the folder name), `samples/README.md`, 70+ includes in `docs/content/samples/*.mdx` (`../../../samples/...`) and GitHub links in guide pages name these paths |
| `demo/` | stays, apart from `samples/` | not a lesson: the docs site's WebAssembly runtime (Mono AOT, single-threaded), built by `docs/scripts/build-demo.sh:14` into `docs/public/demo`, included by `demo.mdx`, linked from `README.md:23`, published by `ci.yml:43-44`. It cannot go under `docs/`: CI's path filter (`ci.yml:6,9`: `"!docs/**", "docs/content/**"`) would stop building and testing it, and Cloudflare's watch path (`docs/*`) would rebuild the site on every demo edit |
| `templates/` | stays | a NuGet package of its own (`release.yml:98`, `templates/smoke.sh`), not a sample |
| `skills/easyecs/SKILL.md` | stays | an external fixed path: `npx skills add JasonXuDeveloper/EasyEcs` discovers `skills/<name>/SKILL.md` (`docs/content/ai.mdx:32`); `docs/public/_redirects:7` and `ai.mdx:46-47` point at its raw URL, which users' scripts copy |
| `.claude/skills/`, `.agents/skills` (a symlink to it) | stay | where Claude Code and the other agents look |
| `.github/`, `global.json`, `Directory.Build.props`, `EasyEcs.slnx`, `README.md`, `LICENSE` | stay | fixed by GitHub, the SDK and NuGet (the csproj packs `..\..\README.md`) |
| `docs/` | stays | Cloudflare Pages' root directory (docs/AGENTS.md, "Cloudflare Pages") |
| `tools/` | stays | small, each tool its own folder |
| `benchmarks/EasyEcs.Benchmark/Locality/` | stays now, goes with L2 | `docs-todos.md` ("Locality phase 0 merged"): the prototypes and their link into the unit tests go when L1 / L2 ship |

## 6. What changes for users

Nothing. The package (`dotnet pack src/EasyEcs.Core`) holds `lib/net10.0/EasyEcs.Core.dll` (+ XML),
`analyzers/dotnet/cs/EasyEcs.Generator.dll` and `EasyEcs.CodeFixes.dll`, `build/` and `buildTransitive/EasyEcs.props`
and `README.md`, all at the same package paths. Namespaces, types, members, generated members and hint names are
unchanged. Only the Source Link paths inside the symbol package change, which debuggers resolve per commit. Proof
before merging: `dotnet pack` before and after, the two `.nupkg` file lists equal, and the public API equal (the
ApiDocs pages regenerate with no diff).

## 7. Order of the move

Preconditions: every running agent branch merged (backlog "Running": feat-call-inliner, feat-locality-core's L2
follow-up, feat-prefetch-gate, and any worktree under `.claude/worktrees/`); a branch started before the move and merged
after it brings its new files at the old flat path, and git's rename detection carries only edits to moved files.
The move runs when no branch is open, in one sitting.

| # | Commit | Content | Gate |
|---|---|---|---|
| 1 | CI/tools: maps and rule count read folders | `tools/agents.sh` (recursive, relative paths, stale lines), `docs/vite.config.ts` (recursive), `docs/src/lib/site.ts:58`, `audit-naming` SKILL.md:30 | works on today's flat layout: `tools/agents.sh --check` passes, `analyzerRules()` gives today's count |
| 2 | Move src/EasyEcs.Core into folders by concern | `git mv` of 77 files + its AGENTS.md map (the map is what `agents.sh --check` reads, so it goes with the move or CI fails between the commits) | `dotnet build -c Release` 0 warnings, `dotnet test`, `tools/agents.sh --check`, `dotnet run --project tools/ApiDocs` with no diff, DocCheck count unchanged |
| 3 | Move src/EasyEcs.Generator into folders | `git mv` of 47 files + map + `tools/agents.sh` (root index line) | the same, plus `bun run check` (vite's analyzerRules) and `tools/inlining.sh` |
| 4 | Move the unit tests into folders | `git mv` of 66 files + `tests/AGENTS.md` | `dotnet test` (same test count as before) |
| 5 | Move the benchmarks into folders | `git mv` of 59 files + `benchmarks/AGENTS.md` | `dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --list flat` lists the same classes; `tools/bench.sh --site` resolves every class |
| 6 | Docs: paths in skills and plans | `.claude/skills/{audit,audit-analyzer,audit-generator}`, `docs/plans/*`, the root AGENTS.md Layout | `tools/agents.sh --check` |

Then the full `verify` gate (build, tests, doc fences, API reference, docs site, samples, templates, AOT, live demo)
and the pack comparison of section 6. Steps 2-5 are pure renames (`git diff -M --stat` shows 100 % similarity for every
`.cs`); a reviewer checks that and the maps only.

Whether the maps go in the move commits (green at every commit) or in docs commits after them (the repository's
"logic, docs and CI apart" rule, with `agents.sh --check` red in between): owner's call. This plan proposes the former,
since a `git mv` is not logic and the map is its index.

## 8. Open questions for the owner

1. **Folder names** (`audit-naming`): `Parameters/` (the docs say "[ForEach] parameters"), `VectorLoops/` (Core) vs
   `Kernel/` (Generator), `OrderedTables/` (glossary "ordered table", 有序表), `Saves/` vs `Persistence/` (the earlier
   sketch), `Grids/` (one file until `Cell<,>`).
2. **Placement calls** listed under section 2.1 (`World.Groups.cs`, `ForEachAttributes.cs`, `EntitySpan.cs`, ...).
3. **Tests and benchmarks too**, or Core and Generator only? The two test-side moves (136 files) cost a rename-only
   diff and the maps; they buy one vocabulary across the repository.
4. **Maps in the move commits** (section 7) or apart.
5. **Extend `agents.sh --check` to tests and benchmarks** (section 4).
6. **A `.DotSettings`** to keep Rider from hinting that namespaces do not match folders (section 3.2), or accept the hint.

## 9. The full mapping, every file

Every `.cs` file of the four projects, checked against `git ls-files` at `2515638` (79 + 47 + 70 + 67, none missing,
none extra). Lines are today's line counts. Files marked "stays" keep their path.

### 9.1 `src/EasyEcs.Core` (79 files)

| Old (`src/EasyEcs.Core/`) | New | Lines |
|---|---|---:|
| `World.cs` | stays | 644 |
| `WorldOptions.cs` | stays | 64 |
| `Cleanup.cs` | `Components/Cleanup.cs` | 14 |
| `ComponentInfo.cs` | `Components/ComponentInfo.cs` | 464 |
| `Contracts.cs` | `Components/Contracts.cs` | 54 |
| `Declarations.cs` | `Components/Declarations.cs` | 187 |
| `Disabled.cs` | `Components/Disabled.cs` | 30 |
| `DynamicBuffer.cs` | `Components/DynamicBuffer.cs` | 599 |
| `TypeIndex.cs` | `Components/TypeIndex.cs` | 217 |
| `World.Defaults.cs` | `Components/World.Defaults.cs` | 111 |
| `World.Singletons.cs` | `Components/World.Singletons.cs` | 171 |
| `EntityDebugView.cs` | `Diagnostics/EntityDebugView.cs` | 17 |
| `ScheduleInfo.cs` | `Diagnostics/ScheduleInfo.cs` | 184 |
| `SystemStats.cs` | `Diagnostics/SystemStats.cs` | 61 |
| `World.Introspection.cs` | `Diagnostics/World.Introspection.cs` | 193 |
| `Entity.cs` | `Entities/Entity.cs` | 426 |
| `EntityId.cs` | `Entities/EntityId.cs` | 55 |
| `EntityMeta.cs` | `Entities/EntityMeta.cs` | 21 |
| `Prefab.cs` | `Entities/Prefab.cs` | 95 |
| `World.DestroyLog.cs` | `Entities/World.DestroyLog.cs` | 101 |
| `Tile.cs` | `Grids/Tile.cs` | 1211 |
| `OrderedIndex.cs` | `Indexes/OrderedIndex.cs` | 27 |
| `UniqueIndex.cs` | `Indexes/UniqueIndex.cs` | 118 |
| `ValueIndex.cs` | `Indexes/ValueIndex.cs` | 957 |
| `World.Indexes.cs` | `Indexes/World.Indexes.cs` | 352 |
| `LastLevelCache.cs` | `Lookups/LastLevelCache.cs` | 141 |
| `Lookup.cs` | `Lookups/Lookup.cs` | 221 |
| `LookupLanes.cs` | `Lookups/LookupLanes.cs` | 224 |
| `RowBlocks.cs` | `OrderedTables/RowBlocks.cs` | 261 |
| `RowOrder.cs` | `OrderedTables/RowOrder.cs` | 449 |
| `World.Order.cs` | `OrderedTables/World.Order.cs` | 181 |
| `SpinGate.cs` | `Parallel/SpinGate.cs` | 53 |
| `Workers.cs` | `Parallel/Workers.cs` | 303 |
| `World.For.cs` | `Parallel/World.For.cs` | 128 |
| `World.Parallel.cs` | `Parallel/World.Parallel.cs` | 601 |
| `Emit.cs` | `Parameters/Emit.cs` | 228 |
| `EntityRandom.cs` | `Parameters/EntityRandom.cs` | 112 |
| `Maybe.cs` | `Parameters/Maybe.cs` | 220 |
| `Mut.cs` | `Parameters/Mut.cs` | 171 |
| `Reduce.cs` | `Parameters/Reduce.cs` | 54 |
| `Scratch.cs` | `Parameters/Scratch.cs` | 60 |
| `EntitySpan.cs` | `Queries/EntitySpan.cs` | 87 |
| `FilterOverflow.cs` | `Queries/FilterOverflow.cs` | 116 |
| `Query.cs` | `Queries/Query.cs` | 1513 |
| `QuerySlice.cs` | `Queries/QuerySlice.cs` | 68 |
| `Entity.Relations.cs` | `Relations/Entity.Relations.cs` | 54 |
| `Hierarchy.cs` | `Relations/Hierarchy.cs` | 27 |
| `Relations.cs` | `Relations/Relations.cs` | 286 |
| `World.Hierarchy.cs` | `Relations/World.Hierarchy.cs` | 328 |
| `Collectible.cs` | `Saves/Collectible.cs` | 72 |
| `ComponentSet.cs` | `Saves/ComponentSet.cs` | 77 |
| `TypeRegistry.cs` | `Saves/TypeRegistry.cs` | 356 |
| `World.Migration.cs` | `Saves/World.Migration.cs` | 438 |
| `World.Serialization.cs` | `Saves/World.Serialization.cs` | 690 |
| `World.Snapshots.cs` | `Saves/World.Snapshots.cs` | 163 |
| `World.Types.cs` | `Saves/World.Types.cs` | 173 |
| `WorldSnapshot.cs` | `Saves/WorldSnapshot.cs` | 25 |
| `Archetype.cs` | `Storage/Archetype.cs` | 1676 |
| `BitMask.cs` | `Storage/BitMask.cs` | 295 |
| `MarkedRows.cs` | `Storage/MarkedRows.cs` | 126 |
| `StructuralGate.cs` | `Storage/StructuralGate.cs` | 155 |
| `World.Archetypes.cs` | `Storage/World.Archetypes.cs` | 422 |
| `World.Bulk.cs` | `Storage/World.Bulk.cs` | 521 |
| `World.Components.cs` | `Storage/World.Components.cs` | 458 |
| `World.Groups.cs` | `Storage/World.Groups.cs` | 136 |
| `World.Trim.cs` | `Storage/World.Trim.cs` | 174 |
| `Annotations.cs` | `Systems/Annotations.cs` | 33 |
| `EcsSystem.cs` | `Systems/EcsSystem.cs` | 55 |
| `ForEachAttributes.cs` | `Systems/ForEachAttributes.cs` | 211 |
| `SystemAccess.cs` | `Systems/SystemAccess.cs` | 65 |
| `SystemCode.cs` | `Systems/SystemCode.cs` | 322 |
| `SystemGroups.cs` | `Systems/SystemGroups.cs` | 60 |
| `SystemHandle.cs` | `Systems/SystemHandle.cs` | 156 |
| `SystemOrder.cs` | `Systems/SystemOrder.cs` | 34 |
| `World.Systems.cs` | `Systems/World.Systems.cs` | 746 |
| `Interleaved.Wide.cs` | `VectorLoops/Interleaved.Wide.cs` | 121 |
| `Interleaved.cs` | `VectorLoops/Interleaved.cs` | 802 |
| `LaneMath.cs` | `VectorLoops/LaneMath.cs` | 185 |
| `Lanes.cs` | `VectorLoops/Lanes.cs` | 401 |

### 9.82 `src/EasyEcs.Generator` (47 files)

| Old (`src/EasyEcs.Generator/`) | New | Lines |
|---|---|---:|
| `BodyFacts.cs` | `Analyzer/BodyFacts.cs` | 72 |
| `EasyEcsAnalyzer.Forms.cs` | `Analyzer/EasyEcsAnalyzer.Forms.cs` | 470 |
| `EasyEcsAnalyzer.Fusion.cs` | `Analyzer/EasyEcsAnalyzer.Fusion.cs` | 106 |
| `EasyEcsAnalyzer.Lambdas.cs` | `Analyzer/EasyEcsAnalyzer.Lambdas.cs` | 115 |
| `EasyEcsAnalyzer.Layout.cs` | `Analyzer/EasyEcsAnalyzer.Layout.cs` | 240 |
| `EasyEcsAnalyzer.Parallel.cs` | `Analyzer/EasyEcsAnalyzer.Parallel.cs` | 406 |
| `EasyEcsAnalyzer.Parameters.cs` | `Analyzer/EasyEcsAnalyzer.Parameters.cs` | 417 |
| `EasyEcsAnalyzer.Parts.cs` | `Analyzer/EasyEcsAnalyzer.Parts.cs` | 612 |
| `EasyEcsAnalyzer.Performance.cs` | `Analyzer/EasyEcsAnalyzer.Performance.cs` | 2110 |
| `EasyEcsAnalyzer.Records.cs` | `Analyzer/EasyEcsAnalyzer.Records.cs` | 92 |
| `EasyEcsAnalyzer.Systems.cs` | `Analyzer/EasyEcsAnalyzer.Systems.cs` | 278 |
| `EasyEcsAnalyzer.Usage.cs` | `Analyzer/EasyEcsAnalyzer.Usage.cs` | 988 |
| `EasyEcsAnalyzer.Views.cs` | `Analyzer/EasyEcsAnalyzer.Views.cs` | 308 |
| `EasyEcsAnalyzer.cs` | `Analyzer/EasyEcsAnalyzer.cs` | 905 |
| `HandSimd.cs` | `Analyzer/HandSimd.cs` | 1054 |
| `Threads.cs` | `Analyzer/Threads.cs` | 266 |
| `UnusedSuppressor.cs` | `Analyzer/UnusedSuppressor.cs` | 37 |
| `Uses.cs` | `Analyzer/Uses.cs` | 229 |
| `ArityGenerator.cs` | `Arity/ArityGenerator.cs` | 1756 |
| `ComponentGenerator.cs` | `Declarations/ComponentGenerator.cs` | 960 |
| `PartGenerator.cs` | `Declarations/PartGenerator.cs` | 133 |
| `Parts.cs` | `Declarations/Parts.cs` | 517 |
| `PrefabGenerator.cs` | `Declarations/PrefabGenerator.cs` | 73 |
| `Prefabs.cs` | `Declarations/Prefabs.cs` | 78 |
| `ViewGenerator.cs` | `Declarations/ViewGenerator.cs` | 72 |
| `Views.cs` | `Declarations/Views.cs` | 137 |
| `Kernel.Inlining.cs` | `Kernel/Kernel.Inlining.cs` | 1287 |
| `Kernel.Lookups.cs` | `Kernel/Kernel.Lookups.cs` | 203 |
| `Kernel.Narrow.cs` | `Kernel/Kernel.Narrow.cs` | 398 |
| `Kernel.Pairs.cs` | `Kernel/Kernel.Pairs.cs` | 338 |
| `Kernel.Struct.cs` | `Kernel/Kernel.Struct.cs` | 372 |
| `Kernel.Structural.cs` | `Kernel/Kernel.Structural.cs` | 222 |
| `Kernel.Views.cs` | `Kernel/Kernel.Views.cs` | 89 |
| `Kernel.Wide.cs` | `Kernel/Kernel.Wide.cs` | 610 |
| `Kernel.cs` | `Kernel/Kernel.cs` | 2229 |
| `Code.cs` | `Shared/Code.cs` | 71 |
| `EquatableArray.cs` | `Shared/EquatableArray.cs` | 42 |
| `Polyfills.cs` | `Shared/Polyfills.cs` | 10 |
| `Symbols.cs` | `Shared/Symbols.cs` | 1771 |
| `Chases.cs` | `Systems/Chases.cs` | 84 |
| `Fusion.cs` | `Systems/Fusion.cs` | 249 |
| `LambdaPass.cs` | `Systems/LambdaPass.cs` | 393 |
| `SystemGenerator.Fusion.cs` | `Systems/SystemGenerator.Fusion.cs` | 490 |
| `SystemGenerator.Kernels.cs` | `Systems/SystemGenerator.Kernels.cs` | 56 |
| `SystemGenerator.Lambdas.cs` | `Systems/SystemGenerator.Lambdas.cs` | 198 |
| `SystemGenerator.Loops.cs` | `Systems/SystemGenerator.Loops.cs` | 324 |
| `SystemGenerator.cs` | `Systems/SystemGenerator.cs` | 1352 |

### 9.131 `tests/EasyEcs.UnitTest` (70 files)

| Old (`tests/EasyEcs.UnitTest/`) | New | Lines |
|---|---|---:|
| `AllocationTests.cs` | stays | 515 |
| `BattleServerTests.cs` | stays | 580 |
| `Fixtures.cs` | stays | 95 |
| `LocalityTests.cs` | stays | 256 |
| `AnalyzerTests.Declarations.cs` | `Analyzer/AnalyzerTests.Declarations.cs` | 426 |
| `AnalyzerTests.Forms.cs` | `Analyzer/AnalyzerTests.Forms.cs` | 303 |
| `AnalyzerTests.Parallel.cs` | `Analyzer/AnalyzerTests.Parallel.cs` | 104 |
| `AnalyzerTests.Parts.cs` | `Analyzer/AnalyzerTests.Parts.cs` | 45 |
| `AnalyzerTests.Performance.cs` | `Analyzer/AnalyzerTests.Performance.cs` | 291 |
| `AnalyzerTests.Systems.cs` | `Analyzer/AnalyzerTests.Systems.cs` | 66 |
| `AnalyzerTests.Usage.cs` | `Analyzer/AnalyzerTests.Usage.cs` | 353 |
| `AnalyzerTests.cs` | `Analyzer/AnalyzerTests.cs` | 75 |
| `CodeFixTests.cs` | `Analyzer/CodeFixTests.cs` | 837 |
| `FormTests.cs` | `Analyzer/FormTests.cs` | 196 |
| `HotColdTests.cs` | `Analyzer/HotColdTests.cs` | 98 |
| `UnusedTests.cs` | `Analyzer/UnusedTests.cs` | 76 |
| `CleanupTests.cs` | `Components/CleanupTests.cs` | 125 |
| `ComponentTests.cs` | `Components/ComponentTests.cs` | 302 |
| `DisableTests.cs` | `Components/DisableTests.cs` | 148 |
| `DynamicBufferTests.cs` | `Components/DynamicBufferTests.cs` | 826 |
| `PresenceTests.cs` | `Components/PresenceTests.cs` | 301 |
| `SingletonTests.cs` | `Components/SingletonTests.cs` | 71 |
| `IntrospectionTests.cs` | `Diagnostics/IntrospectionTests.cs` | 354 |
| `EntityTests.cs` | `Entities/EntityTests.cs` | 389 |
| `InstantiateTests.cs` | `Entities/InstantiateTests.cs` | 398 |
| `GeneratorTests.cs` | `Generator/GeneratorTests.cs` | 1978 |
| `PartTests.cs` | `Generator/PartTests.cs` | 346 |
| `PrefabDeclarationTests.cs` | `Generator/PrefabDeclarationTests.cs` | 179 |
| `ViewTests.cs` | `Generator/ViewTests.cs` | 534 |
| `TileTests.cs` | `Grids/TileTests.cs` | 1281 |
| `IndexTests.cs` | `Indexes/IndexTests.cs` | 986 |
| `OrderedIndexTests.cs` | `Indexes/OrderedIndexTests.cs` | 126 |
| `LookupTests.cs` | `Lookups/LookupTests.cs` | 328 |
| `PrefetchTests.cs` | `Lookups/PrefetchTests.cs` | 222 |
| `OrderTests.cs` | `OrderedTables/OrderTests.cs` | 760 |
| `ConcurrencyTests.cs` | `Parallel/ConcurrencyTests.cs` | 184 |
| `ForParallelTests.cs` | `Parallel/ForParallelTests.cs` | 144 |
| `ParallelTests.cs` | `Parallel/ParallelTests.cs` | 687 |
| `RunnerTests.cs` | `Parallel/RunnerTests.cs` | 188 |
| `WorkersTests.cs` | `Parallel/WorkersTests.cs` | 201 |
| `EmitTests.cs` | `Parameters/EmitTests.cs` | 353 |
| `EntityRandomTests.cs` | `Parameters/EntityRandomTests.cs` | 189 |
| `ReduceTests.cs` | `Parameters/ReduceTests.cs` | 465 |
| `ScratchTests.cs` | `Parameters/ScratchTests.cs` | 201 |
| `ChangedTests.cs` | `Queries/ChangedTests.cs` | 1044 |
| `QueryTests.cs` | `Queries/QueryTests.cs` | 404 |
| `RemovedTests.cs` | `Queries/RemovedTests.cs` | 444 |
| `RunTests.cs` | `Queries/RunTests.cs` | 143 |
| `HierarchyTests.cs` | `Relations/HierarchyTests.cs` | 150 |
| `RelationTests.cs` | `Relations/RelationTests.cs` | 115 |
| `TargetPolicyTests.cs` | `Relations/TargetPolicyTests.cs` | 133 |
| `MoveTests.cs` | `Saves/MoveTests.cs` | 341 |
| `SerializationTests.cs` | `Saves/SerializationTests.cs` | 638 |
| `SnapshotTests.cs` | `Saves/SnapshotTests.cs` | 128 |
| `UnloadTests.cs` | `Saves/UnloadTests.cs` | 167 |
| `BulkTests.cs` | `Storage/BulkTests.cs` | 107 |
| `GroupTests.cs` | `Storage/GroupTests.cs` | 129 |
| `ConsumeTests.cs` | `Systems/ConsumeTests.cs` | 231 |
| `ForEachSystemTests.cs` | `Systems/ForEachSystemTests.cs` | 683 |
| `ForEntityTests.cs` | `Systems/ForEntityTests.cs` | 161 |
| `FusionTests.cs` | `Systems/FusionTests.cs` | 648 |
| `GenericTests.cs` | `Systems/GenericTests.cs` | 181 |
| `LambdaPassTests.cs` | `Systems/LambdaPassTests.cs` | 371 |
| `SystemTests.cs` | `Systems/SystemTests.cs` | 477 |
| `HelperLoopTests.cs` | `VectorLoops/HelperLoopTests.cs` | 272 |
| `KernelLookupTests.cs` | `VectorLoops/KernelLookupTests.cs` | 287 |
| `KernelTests.cs` | `VectorLoops/KernelTests.cs` | 2362 |
| `LaneMathTests.cs` | `VectorLoops/LaneMathTests.cs` | 525 |
| `LanesTests.cs` | `VectorLoops/LanesTests.cs` | 127 |
| `StructuralLoopTests.cs` | `VectorLoops/StructuralLoopTests.cs` | 338 |

### 9.203 `benchmarks/EasyEcs.Benchmark (root files; Locality/ unchanged)` (67 files)

| Old (`benchmarks/EasyEcs.Benchmark/`) | New | Lines |
|---|---|---:|
| `BattleServer.cs` | stays | 767 |
| `BattleServerBenchmark.cs` | stays | 35 |
| `BenchmarkConfig.cs` | stays | 35 |
| `IsaColumn.cs` | stays | 29 |
| `MicroBenchmarks.cs` | stays | 356 |
| `Program.cs` | stays | 12 |
| `WorldSimulation.cs` | stays | 255 |
| `WorldSimulationBenchmark.cs` | stays | 96 |
| `BufferSharingBenchmark.cs` | `Components/BufferSharingBenchmark.cs` | 123 |
| `CleanupBenchmark.cs` | `Components/CleanupBenchmark.cs` | 85 |
| `DynamicBufferBenchmark.cs` | `Components/DynamicBufferBenchmark.cs` | 352 |
| `HotColdBenchmark.cs` | `Components/HotColdBenchmark.cs` | 72 |
| `SparseBenchmark.cs` | `Components/SparseBenchmark.cs` | 71 |
| `IntrospectionBenchmark.cs` | `Diagnostics/IntrospectionBenchmark.cs` | 37 |
| `DestroyLogBenchmark.cs` | `Entities/DestroyLogBenchmark.cs` | 90 |
| `PrefabBenchmark.cs` | `Entities/PrefabBenchmark.cs` | 87 |
| `SingleEntityBenchmark.cs` | `Entities/SingleEntityBenchmark.cs` | 236 |
| `GeneratorBenchmark.cs` | `Generator/GeneratorBenchmark.cs` | 252 |
| `TileBenchmark.cs` | `Grids/TileBenchmark.cs` | 604 |
| `BatchProbeBenchmark.cs` | `Indexes/BatchProbeBenchmark.cs` | 72 |
| `IndexBenchmark.cs` | `Indexes/IndexBenchmark.cs` | 228 |
| `OrderedIndexBenchmark.cs` | `Indexes/OrderedIndexBenchmark.cs` | 100 |
| `LookupBenchmark.cs` | `Lookups/LookupBenchmark.cs` | 104 |
| `LookupChaseBenchmark.cs` | `Lookups/LookupChaseBenchmark.cs` | 186 |
| `LookupGatherBenchmark.cs` | `Lookups/LookupGatherBenchmark.cs` | 226 |
| `LookupOrderBenchmark.cs` | `Lookups/LookupOrderBenchmark.cs` | 109 |
| `NearestPlayerBenchmark.cs` | `OrderedTables/NearestPlayerBenchmark.cs` | 63 |
| `NearestZombieBenchmark.cs` | `OrderedTables/NearestZombieBenchmark.cs` | 55 |
| `PairsBenchmark.cs` | `OrderedTables/PairsBenchmark.cs` | 171 |
| `RowOrderBenchmark.cs` | `OrderedTables/RowOrderBenchmark.cs` | 100 |
| `ChunkRowsBenchmark.cs` | `Parallel/ChunkRowsBenchmark.cs` | 101 |
| `ForParallelBenchmark.cs` | `Parallel/ForParallelBenchmark.cs` | 101 |
| `ParallelForEachBenchmark.cs` | `Parallel/ParallelForEachBenchmark.cs` | 114 |
| `ParallelRunnerBenchmark.cs` | `Parallel/ParallelRunnerBenchmark.cs` | 103 |
| `ParallelSystemsBenchmark.cs` | `Parallel/ParallelSystemsBenchmark.cs` | 79 |
| `EmitBenchmark.cs` | `Parameters/EmitBenchmark.cs` | 153 |
| `EntityRandomBenchmark.cs` | `Parameters/EntityRandomBenchmark.cs` | 123 |
| `HistogramBenchmark.cs` | `Parameters/HistogramBenchmark.cs` | 188 |
| `MaybeBenchmark.cs` | `Parameters/MaybeBenchmark.cs` | 56 |
| `ReduceBenchmark.cs` | `Parameters/ReduceBenchmark.cs` | 400 |
| `ScratchBenchmark.cs` | `Parameters/ScratchBenchmark.cs` | 145 |
| `ViewBenchmark.cs` | `Parameters/ViewBenchmark.cs` | 116 |
| `AccessBenchmark.cs` | `Queries/AccessBenchmark.cs` | 324 |
| `CallSiteBenchmark.cs` | `Queries/CallSiteBenchmark.cs` | 62 |
| `ChangeTrackingBenchmark.cs` | `Queries/ChangeTrackingBenchmark.cs` | 348 |
| `CountBenchmark.cs` | `Queries/CountBenchmark.cs` | 41 |
| `SlicesBenchmark.cs` | `Queries/SlicesBenchmark.cs` | 116 |
| `TimerWheelBenchmark.cs` | `Queries/TimerWheelBenchmark.cs` | 71 |
| `HierarchyBenchmark.cs` | `Relations/HierarchyBenchmark.cs` | 50 |
| `RelationBenchmark.cs` | `Relations/RelationBenchmark.cs` | 63 |
| `MigrationBenchmark.cs` | `Saves/MigrationBenchmark.cs` | 115 |
| `SnapshotBenchmark.cs` | `Saves/SnapshotBenchmark.cs` | 75 |
| `UnloadBenchmark.cs` | `Saves/UnloadBenchmark.cs` | 42 |
| `BulkBenchmark.cs` | `Storage/BulkBenchmark.cs` | 59 |
| `GroupBenchmark.cs` | `Storage/GroupBenchmark.cs` | 107 |
| `TrimBenchmark.cs` | `Storage/TrimBenchmark.cs` | 99 |
| `ConsumeBenchmark.cs` | `Systems/ConsumeBenchmark.cs` | 85 |
| `ForEntityBenchmark.cs` | `Systems/ForEntityBenchmark.cs` | 98 |
| `FusionBenchmark.cs` | `Systems/FusionBenchmark.cs` | 224 |
| `LambdaPassBenchmark.cs` | `Systems/LambdaPassBenchmark.cs` | 91 |
| `OrderingBenchmark.cs` | `Systems/OrderingBenchmark.cs` | 66 |
| `RunnerBenchmark.cs` | `Systems/RunnerBenchmark.cs` | 78 |
| `SystemDispatchBenchmark.cs` | `Systems/SystemDispatchBenchmark.cs` | 164 |
| `CompactBenchmark.cs` | `VectorLoops/CompactBenchmark.cs` | 133 |
| `CompressBenchmark.cs` | `VectorLoops/CompressBenchmark.cs` | 61 |
| `KernelBenchmark.cs` | `VectorLoops/KernelBenchmark.cs` | 1170 |
| `StructuralLoopBenchmark.cs` | `VectorLoops/StructuralLoopBenchmark.cs` | 107 |


## Coordinator's decisions (2026-10-26)

- Tests and benchmarks move too (owner: the whole project); the folder names and placements are as proposed here,
  settled by the `audit-naming` pass at move time.
- Each project's map is updated in its own move commit, so every commit is green; `tools/agents.sh --check` is
  extended to tests and benchmarks and made recursive first (commit 1).
- A Rider `.DotSettings` that turns off the namespace-vs-folder hint is committed with the move (namespaces stay
  `EasyEcs`).
- Runs in the finish phase, after every code branch has merged.
