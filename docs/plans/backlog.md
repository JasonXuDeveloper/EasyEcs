# Backlog: what is left before 4.8 (internal, not published)

The single list of open work, kept in the repository so a new session finds it (AGENTS.md, "Plans that span sessions
are committed"). Update it when an item starts, merges or is dropped. Designs live next to it (`inner-loops.md`,
`generated-text.md`, `native-columns.md`, `voxel-rewrite.md`, `finish-scan.md`); docs to-dos collected while code
still changes live in `docs-todos.md`; the owner's hardware runs in `owner-m4.md`. Every decision of the owner, with
where it is applied and its record, is in `owner-decisions.md` (the ledger): read it before asking the owner again.

Rewritten on 2026-11-07 from the audit sweep of f9faa23 (reports: plans, core, generator, analyzer, code, naming,
perf, docs; kept in the session scratchpad, cited below as `core.md §1`, `perf.md P1`, ...). Every open item of the
eight reports appears once below; the history of finished items is in git (this file before 2026-11-07) and their
records in `docs-todos.md`.

## Owner actions (dashboard and repository settings; agents cannot do these)

- [x] Cloudflare Pages: `docs/plans/*` and `docs/owner-review.md` excluded from the build watch paths (done by the
      owner, 2026-11-07).
- [ ] Delete the remote branch `research-copy-asm` (the proxy refused it from a session), together with every other
      finished branch, at the very end of the Finish chain, after master (Finish 6).
- [ ] The hardware runs of `owner-m4.md`, after all code changes (marked "owner, after code" below).

## Order (set by the owner)

Every code change before any docs (2026-10-26: "先把代码全改完"); every open item done, nothing left over; each item
runs through the skill that owns its work, and its agent's prompt names it:

| Item | Skills |
|---|---|
| any change before it merges | `feature` (definition of done), `audit-naming` on every new name |
| vector loops, generated forms, generated text | `feature`, `audit-vector-loops`, `audit-generator`; `audit-analyzer` for a rule or code fix |
| analyzer rules, code fixes | `audit-analyzer` |
| core fixes and performance | `audit-core`, `audit-perf` (two rounds, 0 B, nothing slower) |
| tools, CI, tests, samples, exemplar pass, directory move | `audit-code` |
| merging an agent's branch | `audit` (triage and merge checks) |
| docs pass | `audit-docs` |
| bench sync, master | `verify` |

At most 3 code agents at once; benchmarks serialize on `/tmp/bench.lock`. Lanes below are cut by the files they
touch, so three can run side by side; within a lane, top-down. A lane marked "after X" starts when X has merged.
Every SIMD path covers ARM64 (`audit-vector-loops`, "Every vector form runs on ARM64 too").

## Running (worktrees under `.claude/worktrees/`; merge with the `audit` triage checks)

- [ ] **`feat-inner-loops`** (`inner-loops.md`; Kernel*.cs, SystemGenerator.Loops/Elements/Cells/Lambdas.cs,
      LambdaPass.cs, HandSimd.cs, the analyzer's Lambdas / Views / Forms areas). 25 commits past f9faa23: WP1 lanes and
      unrolling, EECS099 for two parameters, counted sums as popcount, lambda + structural, lambda + view, WP4's
      `ParallelFor` lambda over a buffer (aef32bb, LightLambda / HeavyLambda). Left on it: WP4's rest (then the
      `audit-vector-loops` line "a ParallelFor job's Execute is compiled only in lambda form"), WP6b (EECS103 v2: tile
      `Load128` -> cell form) and WP6c (v3: `VectorN.Create(span.Slice(i, n))` -> plain loop), each with its code fix;
      WP8 (fixable-reason audit: scalar methods without a Warning in samples, benchmarks, templates, demo, fences;
      `generator.md` "WP8" lists the candidates: `--x`, a singleton field written, narrow integer division by a
      constant, lane sizes that differ); lambda passes fused (Fusion.cs, SystemGenerator.Fusion.cs); and the extra
      items:
      a. a cell method over a `Ground<TTile>` tile (was "a `Columns<TTile>` cell", inner-loops.md WP5);
      b. masked AVX-512 tails for element loops (inner-loops.md section 6: try where runs are mostly tail);
      c. WP7: HeightMap done; FireDamage per row (drop the `#pragma EECS103`, Zombies.cs:193), TileBenchmark cell
         cases, and the demo: the owner chose (2026-11-07) to rewrite `MovementBatchSystem.Move` (and
         `DragBatchSystem`) in the plain form; the page then compares the generated loop with `SimdMode.Off`
         (docs-todos "Sweep 2026-11-07"); D132 / D133 drafts and D77's rewrite are in docs-todos;
      d. the ranged blank fill (one fill per gap run, not `SpanHelpers.Fill` per gap) with WP5b (5): VoxelServer's
         `Flow.cs` as cells, shipped only when not slower than as written (1.62-1.77 against 1.41-1.46 ms before);
      e. EECS055's "a bool among N fields" reason fixable with the split fix (`analyzer.md` 1d, Kernel.cs:704-705;
         a method using only the other fields gets the used-fields split);
      f. one shared wide permute table (Kernel.Wide.cs `Table` memoized: `generated-text.md` 5's duplicate tables,
         `copy-asm.md` 7's table CSE, Move3 18 against 12 table loads per vector);
      g. the roadmap's vector-loop forms (D90): masked words in parallel methods (a `ForEachMaskedParallel` runner),
         `Maybe<T>` in parallel methods and with narrow lanes, masked words of narrow lanes and of wide rows,
         Atan2 / Asin / Pow lowerings, `Vector2.Clamp` (`generator.md` 8); each ships only where it measures faster;
      h. `Interleaved.LoadTail`'s `Tail<T>.Rows` created in ThreadSetup, not lazily (docs-todos "fix-parallel-alloc").
      At its merge: `Kernel.Unrolled.cs` (deleted at f9faa23) must not come back (`generator.md` 11).
- [ ] **`feat-generated-members`** (Finish 1a; `finish-scan.md` Report 1, names approved 2026-11-07; PartGenerator.cs,
      Parts.cs, Declarations.cs, Tile.cs, EasyEcsAnalyzer.Parts / Tiles.cs, the code fixes): `[Tile]` writing
      `ITile<TKey>`, the shape and `Beside`, `Beside` inferred from an `int` key or `X` / `Z` (/ `Y`) members, an Error
      for a 2D key named `(X, Y)`; `[UniqueKey(SlotsPerAxis = N)]` writing `Slots` / `SlotOf` (its doc comment says the
      slots wrap); `[MinBy]` / `[MaxBy]` in the scalar form (doc comments and EECS064's message contrast them with
      `[Min]` / `[Max]`); their rules (Info + fix for a hand-written `Beside` / shape, EECS068 extended to `[Tile]`,
      Info + fix for hand `Slots` / `SlotOf`, an Error for a non-power-of-two, EECS064 / EECS065's fix for MinBy);
      the EECS086 `GeneratedFor` fix (resets matched by type, not by name: `analyzer.md` 5); caching assertions and
      GeneratorBenchmark.
- [ ] **`feat-later-halos`** (Tile.cs, TileSteps.cs, SystemGenerator.Cells.cs, Kernel.Wide.cs, Interleaved.Wide.cs):
      push halos for grid neighbors (D73, roadmap #push-halo: a chunk hands its changed edges to its neighbors'
      halos), and #160 wide rows on AVX2 (heavy math only, by the widelanes numbers).

## Lanes (not started)

### Lane 1: core (src/EasyEcs.Core runtime; the query enumerator templates of ArityGenerator.cs) — 20 items

Starts now; items touching `TileSteps.cs` or `Pairs.cs` wait for feat-later-halos. C12 / C13 / C14 edit
ArityGenerator.cs: not beside lane 4's generated-text steps.

- [ ] **C0. Re-measure the stale bench.json rows before any A/B** (`perf.md` §0 and §2): SparseBenchmark
      PassOverTheFew (fixed by fa2770f the same day), TileStencil Blur9 / Laplacian, ParallelFor Light, Reduce
      Sum / Mass, Emit 10k, ParallelQueryComparison (then `tools/inlining.sh` and jitdisasm of `Waypoints.Steer`'s
      job if still behind Arch / Friflo), OperationBenchmark EntityBatch (batch form slower than its own per-row form:
      count runs per pass, read the batch job), SingleEntityBenchmark Structural + Filtered (`--launchCount 6`).
      Proof: the numbers; a case still losing becomes its own item here.
- [ ] **C1. `RemoveTypes` roots the removed types** (`core.md` §1): `_orderRequests`, `_tileSteps`, `PairState`'s
      clusters and lists, `_columnSpares` survive `ForgetTypes`; `Mentions` ignores `_orderRequests`, so a reused bit
      can get another type's `RowOrder`. Files: World.Order.cs, World.Types.cs, TileSteps.cs (after halos), Pairs.cs,
      Archetype.cs. Proof: `UnloadTests` with an ordered component, a pair method and a tile method (the
      `WeakReference` dies), a reused bit gets its own order.
- [ ] **C2. `PairState` keeps a record per table forever, found by a linear scan** (`core.md` §5): drop entries of
      tables no longer matched, on `TrimExcess` / `RemoveTypes` (shared with C1); a slot per table instead of the scan.
      Files: Pairs.cs. Proof: a `NeighborTests` case cycling 100 `GroupBy` values; NeighborBenchmark with 64 groups.
- [ ] **C3. Bulk walks bump `StorageVersion` when nothing moved** (`core.md` §2): `WalkDown` (World.Bulk.cs:171)
      bumps only when a word op flipped presence. Proof: a `SnapshotTests` case (the second `Capture(into)` after a
      `SetAll` stays incremental); a `SetAll` + `Capture(into)` per tick benchmark, 0 B.
- [ ] **C4. The free-id list not sized to the initial capacity** (`core.md` §3): `_freeIds = new int[Meta.Length]`,
      `Math.Max(free, Meta.Length)` in `Restore`. Files: World.cs, World.Snapshots.cs. Proof: a 0 B test destroying
      1,000 of a reserved default world, and after a `Restore`.
- [ ] **C5. Key reservations for a missing index add up without bound** (`core.md` §4): keep the largest, reset in
      `RemoveTypes` and `TrimExcess`. Files: World.Indexes.cs. Proof: `Allocations.Of` around the first index after
      100 `Reserve(prefab, 1000)`.
- [ ] **C6. `Reserve(prefab, count)` takes a negative count and overflows** (`core.md` §6): throw like
      `ReserveBuffers`, compute in `long`. Files: World.cs. Proof: `Assert.Throws`; changelog Fixes line (docs pass).
- [ ] **C7. `World.Entities` yields dead handles after the records grow** (`core.md` §7): read `_world.Meta` in
      `MoveNext`. Files: World.cs. Proof: a test creating past capacity in the loop, then destroying ahead.
- [ ] **C8. `Dispose` does not return buffer lists to their pools** (`core.md` §8): `ReleaseBuffers` per table.
      Files: World.Systems.cs, World.Archetypes.cs. Proof: 0 B first writes in the next world.
- [ ] **C9. ThreadSetup's step list never shrinks** (`core.md` §9): compact in `Forget`. Files: Workers.cs. Proof:
      `WorkersTests`, 50 load / unload cycles bounded.
- [ ] **C10. `EmitPool` allocates under its spin gate** (`core.md` §10): allocate after leaving it. Files: Emit.cs.
      Proof: the Emit 0 B tests.
- [ ] **C11. P1: helpers chosen on inflated work** (`perf.md` P1): a probe run alone every period while wide,
      `perItem` learned from alone runs; P1b: a merge pass under the floor runs its 16 chunks on the caller. Files:
      World.Parallel.cs. Proof: ParallelFor, Reduce, ChunkRows, Emit, ParallelPass, ParallelRunner, ParallelSystems,
      SystemDispatch, PairsBenchmark GeneratedParallel, TileActive Sparse 4 threads, BattleServer, WorldSimulation,
      MultiWorld (`--launchCount 6` for the parallel classes); a decision record extending D22 / D130.
- [ ] **C12. P3: a query over tracked components stamps a word ahead, not a call per row** (`perf.md` P3). Files:
      ArityGenerator.cs (enumerator template), Query.cs. Proof: ChangeTrackingBenchmark QueryTick / Tick,
      MicroBenchmarks QueryIterate / QueryWrite (controls), tests of `[Changed<T>]` after a loop (full, break,
      structural change).
- [ ] **C13. P4: the query enumerator skips the id load and tombstone test on tables without holes** (`perf.md` P4),
      one branch with C12. Proof: MicroBenchmarks Query*, ToggleComponent, SparseBenchmark QueryTheFew; Tier1 listing.
- [ ] **C14. The frame split of `World.Masked` / `Batches`** (#157's last row: the per-table preamble apart from the
      loop; ceiling about 5 ns of a 20 ns system): jitdisasm of `Masked` on Plain first. Files: ArityGenerator.cs
      templates. Proof: SystemDispatchBenchmark, the #157 classes; keep or record.
- [ ] **C15. P5: a retired entity flips bits in place** (`perf.md` P5): rows of a cleanup mask created in the table
      with the `Retired` column. Files: World.cs, World.Archetypes.cs, World.Indexes.cs; D57 updated. Proof:
      CleanupBenchmark (106 -> ~60 us), DestroyLogBenchmark, a `Without<Retired>` reader case, the headline worlds equal.
- [ ] **C16. P6: unsized resizes uninitialized** (`perf.md` P6), after `[PageFaultDiagnoser]` and Tier1 of `Resize`
      show the clear costs. Files: ComponentInfo.cs, Archetype.cs. Proof: ColdCreateComparison PerEntity,
      MicroBenchmarks CreateAddDestroy, PageFaultBenchmark Grow, FullCopyBenchmark Grow.
- [ ] **C17. P7: `Removed<T>` from a log of removals** (`perf.md` P7, low). Files: Archetype.cs, a log file, Query.cs.
      Proof: RemovalComparison, removal writers' cost.
- [ ] **C18. P8: integer tuple keys hashed by bytes** (`perf.md` P8, low). Files: ValueIndex.cs. Proof:
      IndexBenchmark LookupUnique, ValueQueryComparison, IndexComparison.
- [ ] **C19. The core's unmanaged columns on mimalloc** (owner, 2026-11-07: option (b); `native-columns.md`), last in
      the lane and after Finish 1b's move: columns off `Array`, Dispose and a finalizer, `GC.AddMemoryPressure`, the
      NuGet `runtimes/` binaries and the pinned-heap fallback. Proof: `native-columns.md` section 4 (nothing slower,
      gen2 collections during creation counted); the M4 part is "owner, after code".

Not to build here: a generated countdown timer (`[Expires]`): the owner kept timers hand-written (2026-11-07,
owner-review "Countdown timers"); its lesson is a docs to-do.

### Lane 2: analyzer (EasyEcsAnalyzer*.cs, Symbols.cs checks, src/EasyEcs.CodeFixes, AnalyzerTests / CodeFixTests, GeneratorCorpus's filter) — 15 items

Starts now; Forms / Views / Lambdas areas wait for feat-inner-loops, Parts / Tiles for feat-generated-members.

- [ ] **A1. A test runs the analyzer over the samples, templates and demo** (`analyzer.md` 1a, `code.md` 3.2):
      `SamplesAreClean` (sources + generated output, Info included, a negative control); GeneratorCorpus's filter
      narrowed from all EECS055 / EECS086 to the inherent EECS055 reasons (the EECS086 exception goes with Finish 2c).
      Files: tests/GeneratorTests.cs, benchmarks/GeneratorCorpus.cs. The gate for every severity raise.
- [ ] **A2. Finish 2b: EECS086's non-reducing total, EECS109 and EECS055's bool reason become Warnings** (owner,
      2026-10-27; `analyzer.md` 1b-1d), after the exemplar pass removed their blockers (BattleServer `Battle` ->
      `[Accumulate]`, VoxelServer's `SendPackets` -> `[Drain]` and its bools split, Actors' `Storage.Save`): EECS086
      always Warning, the `[Accumulate]` fix extended to a plain numeric struct (a positional record warns without a
      fix); EECS109 Warning only when the walk could be a static `[Drain]` method (Info with its reason otherwise,
      `samples/Actors/Host/Storage.cs`), the indexed `for` and the non-ref `foreach` detected too; EECS055's bool
      reason (code in feat-inner-loops item e). Proof: `analyzer.md` 1b-1d tests, A1 green.
- [ ] **A3. Rule: `Reduce<T>.Value` read as the total** (`analyzer.md` 3a; common-mistakes #reduce-value), Warning,
      the accumulation reader shared with Kernel / `Accumulations`. Proof: positives and negatives listed there.
- [ ] **A4. Rule: `AddSingleton<T>` on a world that has T** (`analyzer.md` 3b; D58), Error in one block, Warning in
      a hook or `[ForEach]` body, fix to `SetX` / `GetOrAddSingleton`. Proof: tests and a code-fix test.
- [ ] **A5. Rule: a raw entity index stored in a component** (`analyzer.md` 3c), Warning, EECS017's sibling.
- [ ] **A6. Rule: a `Scratch<T>` added to and never cleared** (`finish-scan.md` item 7; threading.mdx:1068 says
      "Code review" today), Warning + a fix inserting `Clear()`; the rule recorded in `audit-analyzer`.
- [ ] **A7. Locality pitfalls** (docs-todos "Locality phase 0"): `Lookup.Find` from a helper ("not prepared") as a
      rule; EECS036 / CS0315's message for a `[LookupKey]` member named `Key`; a qualified `[ForEach]` call inside its
      type. Build or record each.
- [ ] **A8. Views follow-ups** (docs-todos "feat-views"): EECS053 through views in lambdas (verify first), EECS025 for
      a never-written view ref field, EECS049 for view locals; EECS049 for a `[WithAny<Q, T>]` whose every type is in
      `[Without<>]`. Build or record each. After feat-inner-loops.
- [ ] **A9. EECS021 false positive on a partial static runner** (`analyzer.md` 4): skip the method itself unless its
      entry point is reachable. Files: Symbols.cs. Proof: the snippet, private and public.
- [ ] **A10. Build cost: syntax rejection before `GetDeclaredSymbol`** (`analyzer.md` 6) in `AnalyzeVectorLoop` /
      `AnalyzeParallel`; the Tiles, Views, Records, Pairs, Forms and Fusion areas behind `Core.Create`; the two
      `ToDisplayString() ==` comparisons (Performance.cs:308, Tiles.cs:315; `generator.md` "In areas owned") by
      `Symbols.Named`. Proof: GeneratorBenchmark CompileAndAnalyze minus Compile, diagnostics diffed.
- [ ] **A11. Messages and fixes** (`analyzer.md` 7): EECS118's reasons end with what to write; the drop-the-argument
      fix for EECS035 / EECS118; EECS024's move-to-a-static-method fix. Proof: CodeFixTests per fix.
- [ ] **A12. Thin tests and the suppressor** (`analyzer.md` 8): EECS004, 068, 104, 058, 108, 118, 099 / 100, 057 /
      063 / 070; `UnusedSuppressor` cases for `[Drain]`, hooks, IDE0052, and its justification text.
- [ ] **A13. Overlap pins** (`analyzer.md` 9) in `AnalyzerTests.OneMistakeIsReportedOnce`: EECS086 / 055, EECS103 /
      055, EECS109 / 092; IDE0060 suppressed on `[ForEach]` / `[EntityMethod]` parameters (EECS026 owns them).
- [ ] **A14. Smaller candidates** (`analyzer.md` 3, end): "removing a component to pause it", "a flag where a tag
      would do" (overlaps A2's bool split): weigh, then build or record in decisions / owner-review.
- [ ] **A15. Stale instructions** (`analyzer.md` 10, docs-todos S1): `audit-analyzer` SKILL's file list (`.Members`,
      `.Layout`, `.Lambdas`, `.Views`, `.Records`, `.Pairs`, `.Tiles`, `.Fusion`), its range (EECS002-EECS123) and the
      overlap test's name; `src/EasyEcs.Generator/AGENTS.md` 135-136 and root `AGENTS.md`'s range; the
      `GeneratorCorpus.cs` comment; the code comment `EasyEcsAnalyzer.Performance.cs:17` ("the generated member is a
      suggestion"); an `IForSpan` Execute that is one element loop -> "write it as a lambda pass" (inner-loops.md
      section 6): build after WP4 merges or record.

### Lane 3: tooling and CI (tools/, .github/, docs/scripts, test and benchmark harness, sample hosts) — 18 items

Starts now. T1 comes before Finish 1b (the move).

- [ ] **T1. `tools/agents.sh` recursive, and maps for tests and benchmarks** (`code.md` 2.2, 2.3, 3.8):
      `glob("**/*.cs", recursive=True)` before the directory move (otherwise it passes vacuously); `tests/` and
      `benchmarks/` in the maps (a file's base name or its stem without Tests / Benchmark); the missing files listed
      (tests: CopyEntitiesTests, KernelTests, ScratchTests, UnusedTests, WorkersTests; benchmarks: ChunkRowsBenchmark,
      KernelBenchmark, ParallelRunnerBenchmark, ScratchBenchmark, Program.cs, the Comparison project's files); the two
      file maps one line per file. Proof: `tools/agents.sh --check` fails on a missing file, passes after.
- [ ] **T2. GameLoop prints "SystemHandle" for every system** (`code.md` 1.1): `system.Name` in
      `samples/GameLoop/Host/Report.cs:32`; CI checks GameLoop's names and counts (`code.md` 2.5: timing lines
      strippable by `check-sample.sh`). Proof: CI step fails before the fix.
- [ ] **T3. Raw allocation baselines and fixed warm-ups in tests** (`code.md` 1.2): ConcurrencyTests:178,
      LaneMathTests:488 through `Allocations.Baseline` / `Of` / `WarmUp`; the fixed warm-ups of KernelTests,
      KernelLookupTests, HelperLoopTests, AllocationTests to `Allocations.WarmUp` (KernelTests after
      feat-inner-loops), and the sentence in `tests/AGENTS.md` goes.
- [ ] **T4. One generator list for tests, DocCheck and GeneratorCorpus** (`code.md` 3.1): DocCheck lacks View and
      Prefab generators and the interceptors feature, GeneratorCorpus lacks Prefab; one shared list, parse options and
      references. Before the docs pass (fences with views and lambda passes). Proof: a DocCheck fence with `[View]`
      and a lambda pass compiles.
- [ ] **T5. CI runs `bun run check`** (`code.md` 2.1): a `docs` job on `docs/**` (setup-bun 1.4.2).
- [ ] **T6. CI checks the public skills list** (`code.md` 2.4): `npx -y skills add . --list` lists only `easyecs`.
- [ ] **T7. VoxelServer's TickBenchmark built by CI** (`code.md` 2.6): in the slnx or the build job; its job from
      `BenchmarkConfig`; a README line.
- [ ] **T8. The committed live demo checked for staleness** (`code.md` 2.7): a source hash beside the bundle.
- [ ] **T9. Tool scripts: one build helper** (`code.md` 3.4): errors printed, `TargetPath` read, `trap rm` in
      `loopbound.sh`; `inlining.sh --no-build` in CI (`code.md` 2.8).
- [ ] **T10. One reflective BenchmarkDotNet runner for tools/Inlining and tools/LoopBound** (`code.md` 3.3): short
      or full class names, every `[Params]` / `[ParamsSource]` type.
- [ ] **T11. `tools/bench.sh` resolves a class's project by listing** (`code.md` 3.6), and fails on "No benchmarks
      to choose from" instead of marking it done.
- [ ] **T12. `check-sample.sh`'s two copies of the block extraction** (`code.md` 3.7): one function; the README
      heading convention unified with the page's in the docs pass.
- [ ] **T13. Stale comments and maps** (`code.md` 3.5, 5): `tools/inlining.sh:4`'s class names; `docs/AGENTS.md`'s
      scripts line (`lint-terms.ts`, `chromium.ts`, `after-build.ts`, `first-commit-dates.mjs`,
      `search-expectations.json`); `lint-terms.ts`'s empty `pending` (delete or say why); `benchmarks/AGENTS.md`: a
      benchmark nests its components, only the headline worlds declare at namespace level.
- [ ] **T14. The `Locality/` prototypes** (docs-todos "Locality phase 0", `locality.md` status; borrowed-ideas keeps
      them for the owner's reruns): delete the shipped prototypes and their link into the unit tests, keep the plain
      baselines the owner's runs need, or record why each stays.
- [ ] **T15. Benchmark rows**: the EECS096 delegate fallback in LambdaPassBenchmark (docs-todos "feat-inline").
- [ ] **T16. The arm64 job's first run** is the owner's dispatch (Finish 4; `owner-m4.md` 6).
- [x] **T17. The owner's working rules in `AGENTS.md`** (ledger P10, P11; owner 2026-10-27 / 2026-10-30: "用中文对话";
      2026-10-28: "需要我选的给我弹窗，需要我做的给我命令"): a bullet in Workflows: talk to the owner in Chinese (code,
      comments, commits and docs stay English); a choice for the owner is asked as a popup, an action only the owner can take
      is given as the exact command. Proof: `tools/agents.sh --check`.
- [x] **T18. `docs/AGENTS.md`'s Cloudflare table** (ledger L7): the build watch paths include `docs/*` and exclude
      `docs/plans/*` and `docs/owner-review.md` (set by the owner in the dashboard, 2026-11-07).

### Lane 4: vector-loop generator (after feat-inner-loops merges; Kernel*.cs, SystemGenerator*.cs, Recorder.cs, LaneMath.cs, ArityGenerator.cs text) — 18 items

- [ ] **G1. `++` / `--` as a value** (`generator.md` 1): `Expr` lowers it, masked by `_active`; Struct and Pairs paths
      too. Proof: StructuralLoop / Kernel tests (prefix, postfix, `&&` / `||`, early return, narrow lanes), a
      KernelBenchmark Expire case, BattleServer and WorldSimulation (their Burn / Thaw / Expire / Fight / Recover).
- [ ] **G2. A structural change of the method's own component, nothing reading it after** (`generator.md` 2):
      allowed; the `audit-vector-loops` rule narrowed. Proof: Remove / Disable / Set against the scalar twin, D122's
      order, the two headline worlds.
- [ ] **G3. A tag as a parameter** (`generator.md` 3): `in Tag` is a filter with no lanes, `Maybe<Tag>.HasValue` the
      presence mask. Proof: KernelTests, AccessBenchmark's two cases.
- [ ] **G4. A `ref` singleton written only as a running sum, count, Min or Max** (`generator.md` 4): the element
      loops' reduction core, folded into the singleton's field. Proof: KernelTests (non-zero start, bail), a
      KernelBenchmark sum case.
- [ ] **G5. `EntityRandom` draws as lanes** (`generator.md` 5): `Bits` / `Float` / `Chance` / `Range(float)` as 8-byte
      lanes, gated per width; the reason text fixed now and the `audit-vector-loops` line corrected. Proof:
      EntityRandomTests bit for bit, EntityRandomBenchmark at three widths; ARM64 "owner, after code".
- [ ] **G6. Reason texts** (`generator.md` 6): an `Entity` receiver call, `DynamicBuffer<T>` parameters, narrow
      division by a constant (build the power-of-two form), a `Tile<,>` parameter ("write it as a cell method"), the
      tile cursor's indexer read at the loop's counter (Settle's loops, `generator.md` "In areas owned"; after
      feat-later-halos: the reason now, then the element-loop form for tile cursors built or recorded). Proof: reason
      pins, ElementLoopTests for the cursor form.
- [ ] **G7. Math with no vector form called per lane inside the loop** (`generator.md` 7): `LaneMath.PerLane.*`,
      gated by operations against calls; D127's Considered. Coordinate with feat-inner-loops' item g (Atan2 / Asin /
      Pow lowerings). Proof: KernelBenchmark Atan2 / Sin bodies, a light body that must not gate on.
- [ ] **G8. P2: a uniform table read at a lane index** (`perf.md` P2): a permute (`vpermps`, AVX2 `PermuteVar8x32`,
      NEON TBL) for N <= 16, a gather past it; BattleServer's Steer back to `SimdMode.Auto`. Proof: KernelBenchmark
      SteerTable pair, BattleServerBenchmark, tools/loopbound.sh; ARM64 "owner, after code".
- [ ] **G9. `[MinBy]` / `[MaxBy]`'s vector form** (`finish-scan.md` item 5), after feat-generated-members: proved with
      ReduceBenchmark (FindWeakest), ties keep the earlier row.
- [ ] **G10. The ARM gate's rejected `Brake` shape: a benchmark pair forced open** (backlog history, ARM64 parity),
      then "owner, after code" (`owner-m4.md` 4). Files: KernelBenchmark.cs, Kernel.Wide.cs's gate.
- [ ] **G11. Incremental rules** (`generator.md` 9): Recorder's SemanticModel per member (9a, use the transform's
      model), `Symbols.ModelOf` cached (9b), `ClosedSystems` measured before any change (9c). Proof: GeneratorBenchmark
      EditMethodBody / Ide.
- [ ] **G12. Dead code** (`generator.md` 11): `PairForm.WritesNeighbor`.
- [ ] **G13. Generated text cleanup, step 1: the `GeneratedTextTests` guard and output bytes in GeneratorBenchmark**
      (`generated-text.md` 8; `generator.md` 10).
- [ ] **G14. Step 2: the file-level alias block** (38,597 `global::`; IL identical).
- [ ] **G15. Step 3: fusion files reference the systems' jobs** (-76 % fusion text) — after feat-inner-loops' lambda
      fusion.
- [ ] **G16. Step 4: printer fixes** (404 `((0))`, names, Arity indentation; IL identical) **and empty hooks as
      `ISystemCode` defaults** (about 400 `{ }`; SystemCode.cs).
- [ ] **G17. Step 5: one Tier1-gated round of IL-changing cleanups** (`__d0 = Zero` marks, hoisted scalar setup,
      `this.v0`, pair broadcasts, struct-local aliases, dead narrow temps, unused masks, `ids, 0`, discards for unused
      fields, 50 identity copies) with #172's rest (one body per width where Tier1 is equal) and the WP0b follow-ups
      (GeneratorBenchmark A/B, a locals check over the samples, `.Zero` / `.One` text); the D126 `__s -> __c` copy
      stays, commented. Proof: Tier1 of the six runners, PGO off, else BenchmarkDotNet.
- [ ] **G18. Generated members' text** (`generated-text.md`, small): ComponentGenerator's AggressiveInlining text,
      doc comments per arity (`inheritdoc`), the view's `TryGetX` double lookup.
- [ ] **G19. A bare `[StructLayout(LayoutKind.Sequential)]` keeps the vector loop** (`interop.md` 1, finding): today
      any `[StructLayout]` makes a component's method scalar ("sets its layout"); `Sequential` with no `Pack` and no
      `Size` is the default layout of a C# struct, so treat it as none. Proof: a GeneratorTests case and the
      vector-loop test for it; `Explicit`, `Pack` and `Size` keep the current reason.

### Lane 5: renames (after feat-later-halos and feat-generated-members merge; owner, 2026-11-07) — 6 items

- [ ] **R1. `tile.Restart(value)` -> `tile.ColdStart(value)`** (`naming.md` B2; `tile.Sweep` stays): Tile.cs,
      EECS115, the generator, VoxelServer, tests, benchmarks (TileRestartBenchmark -> TileColdStartBenchmark, site keys
      with it), ApiDocs zh `tile.xml`.
- [ ] **R2. The shape `Columns<TTile>` -> `Ground<TTile>`, the column walk `TileColumnWalk<…>` -> `TileColumns<…>`**
      (`c.Columns(step)` stays; `naming.md` B2): Tile.cs, the generator's tile-load interceptor, tests, samples,
      ApiDocs zh.
- [ ] **R3. `World.MaxChunkRows` -> `World.MaxChunkSize`** (`naming.md` B3, A1), with a code fix on CS0117 from
      the released `World.ParallelChunkRows` (the first released rename: the renamed-member mechanism stays for it).
- [ ] **R4. Delete the code fixes for names that never shipped** (`naming.md` B4): `RenamedAttributes`
      (`[IndexKey]` -> `[LookupKey]`) and `RenamedArguments` (`ChunkRows` -> `ChunkSize`) in
      EasyEcsCodeFixes.Declarations.cs and their CodeFixTests; owner (2026-11-07): also the other migrations from
      forms that never shipped (the hook attributes, `[GroupBy]` on the struct, EntityRandom's tick written by hand),
      keeping only the fixes from released forms (`WithAny<T>`, the class systems, `ParallelChunkRows`); AGENTS.md's
      CodeFixes line follows; D129 amended in the docs pass (docs-todos S3).
- [ ] **R5. Internal and hidden names** (`naming.md` A3-A5, approved as "clearly right"): `BadRecordPass` /
      `HandWrittenRecordPass` / `AnalyzeRecordPass` / `AnalyzeHandWrittenRecordPass` -> `BadDrain` /
      `HandWrittenDrain` / `AnalyzeDrain` / `AnalyzeHandWrittenDrain`; tracking names `RecordMethods` / `RecordBodies` /
      `RecordCode` -> `Drain*` (GeneratorTests' list and the generator's AGENTS.md); the fix `RecordMethod` ->
      `DrainMethod`; the `DrainTests` / `EmitTests` names; `Emitted<T>.Consume` -> `Drain`; `TileSteps.Sweep(Entity)` ->
      `NextSweep`; `TileRows.First` / `Last` -> `FirstLayer` / `LastLayer`; `CellRow` -> `PlaneLoads`; `TilePlane.Own` ->
      `RowBeside`; `TileCells.Pool` -> `SizePool`; `CellRows.cs` -> `TilePlane.cs`; `ChunkRowsBenchmark` ->
      `ChunkSizeBenchmark` and `GhostCopyBenchmark` -> `CopyEntitiesBenchmark` (site keys and bench.json mapping
      kept). Run the `StartsWith` / `EndsWith` grep of `naming.md` first.
- [ ] **R6. `audit-naming` step 4 amended**: "a `ForEach*` name is a pass over rows, except `ForEachCollected`, the
      collected form of a `[ForEach]` method" (`naming.md` B1). Not approved and kept: `{Owner}ForEach` (not
      `{Owner}Passes`) and `CopyEntities<TTag>` (not `CopyEntitiesWith`); their reasons go into the naming record
      (docs-todos).

## Finish (strictly in order; nothing of a later step starts before the earlier one is done)

Starts when every lane above has merged (owner, 2026-10-27: the refactor before docs and benchmarks, so docs and
numbers are written once about the final code).

- [ ] **1. Directory restructure** (layout approved 2026-11-07: `finish-scan.md` Report 2 plus `tools/perf/`; it
      supersedes `layout.md`): `git mv` only, alone, after every branch merged and after lane 3's T1; its six commits,
      `docs/vite.config.ts` recursive, `site.ts:58`, skills' paths, `.DotSettings`, the 107 `tools/perf/` references;
      `inner-loops.md`'s `CellTests.cs` reference dropped.
- [ ] **2. The exemplar pass** (#169, `audit-code`; owner, 2026-10-27): every piece of code users read or copy in the
      most fitting form, new features included. Proof: outputs and README / page output blocks match, 0 B steady
      state, tests three ways, A/B no slower.
      a. **VoxelServer** (`voxel-rewrite.md` steps 1-6, 8, 9, owner answers in its section 9): bool splits
         (`Body.OnGround`, `Exposure.Sunlit`, Input, Visible), Viewers long, SetAbsent, FireDamage per row; `[Accumulate,
         Sum] Stats`, `SendPackets` -> `[Drain]`, `ReserveBuffers`; AOI without the World; pairs (Keep + Acquire,
         ChunkRing deleted, Pickup with `[Neighbor] ref`, item merging through symmetric pairs); water in the ping-pong
         form (Commit and NextFluid deleted, the cell form if feat-inner-loops item d shipped it); the scent as one
         `[GaussSeidel]` step with `ColdStart` / `Sweep` (Ground / Red / Black / NextScent / Settle deleted); Footing
         and Terrain as element / cell methods; two region worlds with `MoveEntities` at the border (ghost rows); the
         34 `UpdateInGroup<Simulation>` -> the default group (`finish-scan.md` 3); line count and `--schedule` before /
         after; README, the voxel-server page's output and `check-sample.sh`.
      b. **The other samples, templates and demo**: the three `Checks` copies read the same; ScoringPlugin
         `GetOrAddBonus`; HotReload `Status` as `[ForEach]` + a reduction; Actors' `Storage.Save` -> a `[Drain]` method
         (`code.md` 4); MultiWorld `Border.cs` comment (why `CopyEntities` is not used there); GameLoop's counters
         (comment or `[Accumulate]` like BattleServer); one `Option(args, ...)` shape for the hosts' parsers and
         FakeClients alike (`code.md` 5); the template's `SpawnSystem` through `Instantiate(prefab, stackalloc
         Entity[n])`; the demo's Bounce / Fade `SimdMode.Off` (plain `[ForEach]` now that conditional structural
         changes vectorize, or a comment naming the reason) and `Groups.cs`' comment.
      c. **Benchmark worlds**: BattleServer (`Battle` -> `[Accumulate]`, then GeneratorCorpus's EECS086 exception
         deleted; `Weakest` -> `[MinBy]`; NearestPlayer / LootValue host loops -> `[ForEach]` + reductions;
         `PlanningSystem.Plan` per row after WP1; KillFeed; give `[View]`, `[Prefab]`, `[Drain]`, `[Neighbor]` and a
         lambda pass a place; keep the `// #region` blocks); WorldSimulation; an A/B per rewritten world.
      d. Unit tests that show usage (not the ones pinning an old form on purpose).
      e. Every code example of the docs (fences en + zh, the guides' full examples, the public skill, README),
         DocCheck at 0; L4 of `locality.md` (ZombieTarget, NearestPlayer, AOI).
- [ ] **2b.** Lane 2's A2 lands here if it has not (its blockers are 2a / 2c).
- [ ] **3. Docs pass** (#141, `audit-docs`): everything in `docs-todos.md` ("Sweep 2026-11-07" first), with the
      analyzer page's guard test (docs-todos: every id anchored, severities equal to the descriptors') committed with
      the page; DocCheck and ApiDocs at 0.
- [ ] **4.** The `arm64` CI job's first run (owner's dispatch, `owner-m4.md` 6): a wrong runner label fails at once
      and is fixed then.
- [ ] **5.** "Owner, after code": the runs of `owner-m4.md` (1-5; the x86 prefetch run is not made, owner 2026-11-07: the gates' decision
      record says they were proven on this VM's 260 MiB LLC only and that a 32 MiB-LLC x86 run is what would move them), each result
      recorded where the file says.
- [ ] **6.** `tools/bench.sh --site --sync` on the final code (perf's stale rows re-read, the classes missing from
      bench.json), README numbers, `verify`, master; then the owner deletes `research-copy-asm` and every finished
      branch. The owner cuts the release.

## Done this round (for the record)

Merged before 2026-11-07, details in git and in docs-todos: the pair form and symmetric pairs (L2, 8e), ARM64 parity,
the call inliner, G1, microarch M0-M3, the prefetch gates, borrowed ideas, the distance field, the generator's stage
catches and reason texts (8b, 8d, 8f), foreign bodies (8g), MoveEntities' `ArriveRows` (8i), switch masks (8j),
`[ForEach(MaxRows = N)]` (G10), streaming stores, ping-pong steps and fewer copies (11, 12a-e), the generator gaps
(G0, G4-G6), the inliner's limit checked, the owner's calls (a)-(f), huge pages and page faults, #153 / #157 / #159,
the flaky allocation tests, the Native AOT tables on x64, WP0 / WP0b / WP2 / WP3 / WP5 / WP5b (1)-(4) / WP1 step 1.
Settled by the owner on 2026-11-07 (records in docs-todos "Sweep 2026-11-07"): the Enter out-tile, the lane-shift
helper, `Rows()` turning, the page-fault attempts not kept, `[Accumulate]` sharing a stage, the ping-pong read entry
(the fix shipped), the countdown timers (owner-review); mimalloc chosen for native columns (C19).
