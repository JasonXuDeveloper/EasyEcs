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

- [x] **`feat-lane4-rest`** merged 2026-11-09 (691ae22): the inlining fix (`MaskedRuns` out of line), G5, G7, G8, G15,
      G17, G18, G11 9c measured; G7's NaN sign is the JIT's choice (evidence in the test's comment).
- [ ] **`feat-lane-kernel`** (owner, L29; `designs/lane-kernel.md`), three packages, merged in order D -> E -> F:
      **D** (`feat-lane-kernel` 05f91b47: flat jobs through `LaneKernel`, one NoInlining walk per run). Final A/B
      2026-11-16 (scratchpad kernelD2/cmp2/D.txt; head2 started under load 13.5): 56 level, 3 faster (Pow -10 %,
      FragmentsMove2 1,000 -6 %, EditMethodBodyIde -23 %), 4 slower: FragmentsIntegrateScalar 100k +10.6 %, VectorForms
      MarkNarrow +9.7 %, MaskedParallel +9.7 %, MaybeNarrow +5 %; LaneKernelBenchmark has no base side. Those rows are
      re-run in three interleaved rounds (scratchpad rerunDF/D.txt): all four level (every round within the base's own
      spread), all 0 B. **Merged 2026-11-17 (227f62d9), gated.**
      **E** (`feat-sse-path` 3498fd4f, rebased onto main 89090767 without D's Lane128 hunk): every slower SSE row has its
      cause (lane-kernel.md package E): the blend's XMM0 mask made a store/reload chain where one select sits in a loop
      (the wins), while xor-and-xor pushed loops of several selects past the JIT's constant hoisting (Gate) or lengthened
      nested chains (Grade, Clamp, Step's masked loop). The select form is now written only for loops with one select;
      Count, Energy, Move4, Survey and WorldSimulation differ by layout only. **Merged 2026-11-17 (fe99f0e3)**, gated; the A/B is in
      the entry below. Open risk: MaybeParallel keeps the xor form.
      **F** (`feat-lane-walk` 99293792, on D; owner L36: the loop lives in each job's generated Walk): HolesHaul's cause
      was D's call per masked word (now inlined into `MaskedRuns`); Tally's was the JIT blending the conditional sum
      (`ILane.AddWhereEqual`). A/B 2026-11-16 (kernelF2/cmp): default 55 level, 3 faster, 5 slower (StepKernel 100k
      +21 % noisy, TallyTyped 1,000 +20 %, StepWalk 37 +14 %, HolesHaulGenerated 100k +7.8 %, BattleServer Tick +4.3 %);
      SSE 49 level, 8 faster, 3 slower (StepWalk 100k +15 %, HaulGenerated 1,000 +13 %, HolesHaulGenerated 1,000 +4 %).
      TallyTyped / TallyGenerated at 100k did not move (1.10-1.23 base, 1.21-1.29 head): the reduction is not level yet.
      Re-run, three interleaved rounds (rerunDF/F_default.txt, F_sse.txt): every row level except **HaulGenerated
      SSE4.1 1,000, slower in all three (+10.5 / +5.9 / +12.1 %, base spread 1.8 %)**; HolesHaulGenerated AVX-512 100k
      +3.2 / +2.1 / +3.2 % sits inside its spread (4.4 %) but leans the same way. Tally's ratio still 1.15-1.18 at 100k.
      **Haul's cause** (2026-11-17, lane-kernel.md "Constants"): the walk's constant was CSE'd into a register in the
      emitted loop but left in memory in the walk (`mulps xmm, [RWD16]`, a fourth load a vector under SSE4.1); fixed by
      declaring each constant of a step before the walk's loop (`__kN`, Generator 8f2ec2d3). A/B, three rounds
      (`$S/haulF/ab.txt`): every row level at 0 B under both settings, HaulGenerated SSE 1,000 +5.2 / +1.9 / +4.7 %
      (was +10.5 / +5.9 / +12.1), IntegrateGenerated SSE 7-27 % faster. The rest is the call per run with a 28-byte job
      passed in memory; inlining the walk into the runner recovers it (130.8 ns against the emitted 131) but exhausts
      the stress world's inlining budget, so it needs a size rule: next form. Gate green (net10.0 / net11.0 full
      suite, the vector classes under the width settings, inlining.sh). Not merged. Left for F: select under one comparison, Min/Max against
      constants, 8-byte lanes, reductions, the masked walk's passing-bits constant.
      **The size rule** (2026-11-17, branch `feat-lane-walk-inline` on 62218aef; lane-kernel.md "The call per run"):
      `LaneKernel.RunInlined` / `RunFlatInlined` inline a whole run's walk of at most 16 lane operations over at most 6
      components (`SystemGenerator.InlinesWalk`); larger walks keep the call. Edge from a sweep of 1-9 components at
      Tier1 / FullOpts and three widths (`$S/walkI/out/g1-*.sum`): the runner's three call sites x three widths reach
      the JIT's 512 locals, first lane call at 21 operations for 5-6 components, 17 for 7-9 (whose scalar tail leaves
      from 9); under the rule every Tier1 runner holds base's calls less the walk's (`g2-*.sum`); FullOpts leaves one
      site's scalar tail a call (profitability falls with the root's locals). A/B three rounds (`$S/walkI/ab/ab.txt`):
      SSE 58 level, HaulGenerated 1,000 -2.7 / -4.1 / -1.6 % (131 ns, the emitted loop's); default 60 level, BattleServer
      +4.1-4.8 % rerun alone in six rounds level (-4.1 / +8.7 / -4.3 / -1.9 / +3.1 / +27.8 %, spread 23 %; only
      Move's runner changed, its walk calls gone). inlining.sh now reports lane operations left as calls. Not merged.
- [x] **`fix-no-simd-structural`** merged 2026-11-16 (5ddc511a): a lambda calling a generated writer (`e.SetDaze`) was
      marked free of structural changes, so its row-by-row job (no SIMD) froze the gate; `Symbols.UnboundEntityWriter`
      reads such calls by name.
- [x] **`fix-no-simd-lambda-end`** closed 2026-11-17, no code change: already fixed by fix-no-simd-structural
      (5ddc511a). Every failing log (hwcheck, kernelD2, kernelE2, kernelF2) ran on 89090767 or a package branch forked
      from it, before that merge: the throw was its frozen-gate `InvalidOperationException` (the `Rows` job carrying
      `INoStructuralChanges`), so the pass stopped at the first change and ended short of the method; the schedule
      failure was the test's old expectation, before the fix made it expect "; rows on this hardware". At 227f62d9 the
      lambda's `LambdaJob0Rows` has no `INoStructuralChanges`, and under `DOTNET_EnableHWIntrinsic=0` the vector-loop
      classes (EntityRandomTests added) pass on net10.0 and net11.0 (1472 + 18 skipped each), the full suite on both
      (4484 + 1 skipped). Gate: scratchpad `nosimd-end/gate.sh`. A package branch forked before 5ddc511a still fails
      these three until it merges the work branch.
- [x] **`fix-view-alloc-under-load`** (branch `fix-view-alloc-under-load`): `ViewTests.APassThroughAViewAllocatesNothing`
      allocated 98,352 B in its 10 updates in feat-ground-layer's gate (scratchpad gate.log, layer-test.log: 4 of 4
      runs, both runtimes). Not load and not the parallel pass (the test's world is serial): those runs were the Debug
      build (`dotnet test`'s default; CI tests Release), and the bytes are the runtime's cast cache. Cause, proven: the
      runtime drops its cast cache whenever a collectible assembly's loader allocator is collected
      (`loaderallocator.cpp`, `CastCache::FlushCurrentCache`), and the next cast that misses, on any thread,
      allocates the table again at its old size, `int[(size + 1) * 6]` (`castcache.cpp`): 98,352 B at 4,096 entries,
      49,200 B at 2,048. The generator / analyzer tests leave collectible assemblies, which `Allocations`' settling
      collection collects; in a Debug build `ForEach`'s `typeof(INoStructuralChanges).IsAssignableFrom(typeof(TJob))`
      (a JIT-time constant once optimized) is a real cast, so the first update in the window paid another test's
      unload. Evidence: the full Debug suite reproduced it every run with the old form (Int32[] of 49,200 B in the GC's
      allocation tick; a probe inside `World.Run` put all of it in Poisoning's `ForEach`), only with the Roslyn tests
      and an EasyEcs class before it (bisected); a RunAndCollect assembly collected before a cast gives exactly 98,352 B
      in Release too; nothing of it in Release runs or under `yes` / niced load (9 full suites, 0 B). Fix (tests only;
      optimized Core does not cast there): `Allocations.Settle` ends with a cast through the cache
      (`Allocations.RebuildCastCache`), so the table is rebuilt before the window; regression test
      `AllocationTests.AMeasurementDoesNotPayTheCastCacheAnUnloadedAssemblyDropped` (98,352 B without the cast, 0 with
      it, both runtimes); the old form of the view test passes in the full Debug suite with it; tests/AGENTS.md
      records the rule. The Steady form passed only because a cast before the window (the warm-up's, a finalizer's)
      happened to rebuild the table first.
- [x] **`research-voxel-pairs`** (branch research-voxel-pairs): causes in voxel-rewrite.md section 11. Core fixed (a
      copied buffer list gets the room of an append; the pair margin is not grown by a lone jump; the sweep runs along
      the widest axis; a neighbor list is sized once), A/B level at 0 B (PairsBenchmark Both / Parallel, PairListsBenchmark,
      PairGroups, PairHalves, BufferSharing / Snapshot), the old sample's tick level. Step 4 not kept: the form searches
      every zombie every tick (99.4 % keep their target) and the item pairs pay for the churned item table.
- [x] **`research-pair-churn`** closed 2026-11-19 (branch `research-pair-churn`, not merged; commits: Benchmarks
      PairChurnBenchmark, Core grid draw, Core early sort, Tests). Two causes, both fixed in core, pairs bit for bit.
      (1) Rows out of place: rows that join wait past `SortedRows`, a row moved into a removed one's place widens its
      cluster's box across the map, so until the cadence's sort (every 10 updates) the pairs grew 10-90x (PairChurn
      base: 5.7 ms a tick at 0.1 % of 10,000 rows, 84 ms at 10 %, 249 ms at 0.1 % of 100,000, 2.0 s at 1 %). Fix: each
      pair pass counts the row pairs its list pays past the least since its tables' sort, per row out of place; an update
      sorts a table early once what was paid plus what the next update's passes would pay reaches the sort's cost
      (`RowOrder.SortCost` = 100 row pairs a row, ski rental; `RowOrder.Disturbed`). Rows that only move never ask.
      Screened 30 / 100 / 300 / 1,000 and a sort every update (dev): 100 best or level everywhere; at 100,000 rows with
      0.1 % churn a sort every update is the cheapest rule measured (c100 23.7 ms, every 24.9, c300 31.2, c1000 36.4):
      the "100,000-row sort every tick" costs 5 ms where the spread clusters cost 225, so it is right there, and the
      model stops it wherever it does not pay. Dropped (dev, still with the sweep): the probe (one cluster in 32 disturbed: 123 ms at 100k / 0.1 %
      against 98 for the first cost model, 84 for a sort every update); feedback only after the pass (the first cost model:
      a sort every other tick at high churn, 24.6 ms at 10k / 10 % against 3.4 for a sort every update); sorting only the disturbed rows into
      the holes (any permutation within the holes moves a row n/sqrt(k) places of the order on average: the clusters
      stay wide) and an order-keeping repair (moves O(n) rows as a sort does). (2) The draw: churn redraws a list every
      pass, and the sweep along one axis met every box of its strip across the map, O(boxes^1.5): 27.8 ms a draw at
      100,000 rows (a phase probe outside BenchmarkDotNet, for attribution only: 0.70 ms of a static 10,000-row crowd's
      1.42 ms pass, 3.1 of 3.9 ms churned and sorted every tick). Fix: a grid over the two axes the boxes
      spread the most along (cells about a box wide; boxes wider than four cells walk the other side's grid; one-axis
      boxes keep the sweep), the lists by two counting sorts (the per-cluster sort was 6 ms of a draw): draw 12 ms at
      100k (grid 9, lists 3), 0.24 ms for the static 10k crowd (the same probe). Wide threshold 1 / 2 / 3 / 4 cells: 160 / 14 / 9 / 9 ms.
      A/B (default job, two rounds, base 402ab80e + the benchmark, grid 1e71c4de, head): head / base per round: PairChurn (ms a tick, base -> head, round 1 / 2)
      Generated 10k 0.1 % 5.72 / 5.64 -> 2.08 / 2.12, 1 % 23.6 / 22.4 -> 2.31 / 2.17, 10 % 84.1 / 81.7 -> 2.38 / 2.39,
      100k 0.1 % 249 / 256 -> 23.4 / 23.5, 1 % 2,035 / 2,072 -> 26.3 / 23.8; Both 10k 7.19 / 7.40 -> 4.19 / 4.06,
      22 -> 3.9, 77-79 -> 3.7-3.8, 100k 243 / 242 -> 43.6 / 41.1, 1,796 / 1,804 -> 41.8 / 40.6 (the grid alone: 0.42-0.71
      of base); PairLists 0.52-0.98 of base in both rounds (100k Crowd Auto 0.61 / 0.62, Sparse Auto 0.52 / 0.54); PairHalves,
      PairsBenchmark Generated* / PairGroups (static passes: no draw after the first), RowOrder (the sort, unchanged) and
      VoxelServer TickBenchmark (no ordered table) level: no row above max(3 %, base spread) in both rounds; the rows above
      3 % in both (PairHalves Full 10k Sparse 4t 1.147 / 1.145, Pairs GeneratedParallelResult 1k Crowd 1.124 / 1.138,
      GeneratedRows 10k Sparse, Generated 100k Sparse, RowOrder Sorted 100k Narrow 1.093 / 1.068) were run again in two
      more interleaved rounds: none was above in all four (1.088 / 0.759, 1.103 / 0.973, 1.014 / 0.973, 1.007 / 1.031,
      1.009 / 1.015), level with the control CoreClusters (unchanged code: 0.85-1.05). Their code differs from base by an
      O(1) count per pass (static passes draw once). 0 B everywhere but one run of seven of PairHalves Full 100k Sparse 1
      thread on head (8 B a 20-tick op, rounds 1-3 and the other trees 0); base showed the same kind once (TickBenchmark
      2,000 players, round 2: 624 B on unchanged code), as `research-tile-bdn-1b` describes; the unit tests' Steady
      checks of the parallel and churned passes are 0 B.
      Open: the 354 of 12,500 boxes wider than four cells at 100,000 rows (Morton jumps: a cluster across a quadrant
      boundary) carry most of the grid's 9 ms and of their pairs (`research-pair-wide-clusters`); VoxelServer's sample
      has no pair method (step 4 not kept), so its Pickup / merge as pairs is for `research-pair-filtered-rows`'s re-try.
- [ ] **`research-pair-wide-clusters`** (from research-pair-churn; Opus): after a sort, about 3 % of a crowd's clusters
      of 8 rows span a Morton jump (a box over two quadrants, up to half the map): at 100,000 rows 354 boxes wider than
      four grid cells, most of the draw's 9 ms in the grid and a share of the pass's pairs. Try splitting a cluster where
      the key jumps (variable clusters break the lanes: measure first how many pairs they cost), a Hilbert key (no
      jumps across the map), or boxes of two halves for the draw.
- [ ] **`research-pair-filtered-rows`** (from research-voxel-pairs; Opus): a pair method whose row filter passes few
      rows (`With<Seeking>`: ~11 of 4,000 zombies) still draws boxes, ranges and lists for every row: 0.52 ms where the
      old sliced search cost 0.17. Draw only the clusters holding a passing row, then re-try step 4's Keep + Acquire
      with `Seeking` (the form in voxel-rewrite.md section 11).
- [x] **`research-voxel-pingpong`** closed 2026-11-18 (branch `research-voxel-pingpong`, not merged): steps 5 and 6
      kept (`voxel-rewrite.md` section 12). Causes: a Jacobi pass that does not sleep took every stepped tile as changed
      (swapped and stamped all: 101,949 block updates), fixed in core (only tiles that changed are stamped); a tile
      step's state per entity index, then row sets taken in the pass at new peaks of the grid (16,496 B, then 39,672 B
      after warm-up), fixed (the peak of the tiles; a row set for every row the tables have room for). The scent's
      `[GaussSeidel]` step is level with the red-black systems in TickBenchmark; its higher time in the sample's
      `MeasureSystems` is Tier0 code early in a short run (level with tiering off).
- [ ] **`research-tile-step-tiering`** (from research-voxel-pingpong; Opus): a `[GaussSeidel]` method of Vector128 code
      (the voxel scent) runs at Tier0 for a part of a short run (13 us a sweep against 4 us), where the hand-written form
      with `stackalloc` was optimized at once (no OSR): the sample's `MeasureSystems` shows 0.40 against 0.17 ms. Find
      whether the generated job can get the method optimized sooner (tiering of the method behind its UnsafeAccessor,
      or a hint) without losing PGO, measured over the sample's first 300 ticks.
- [ ] **`research-tile-bdn-1b`** (from research-voxel-pingpong; Sonnet to reproduce, Opus for the cause): three
      BenchmarkDotNet runs of the tile classes on research-voxel-pingpong's head showed 1 B a run (TileColdStart
      ColdStart 4 threads 256 springs 1 hop and 1 / 4 / 1, TileStencil Laplacian_PingPong 4 threads 32), none on its base
      and none in the unit tests' 0 B checks of the same passes. Find whether it is a late tier-up or OSR in the run
      (the JIT's allocations) or a real allocation: run those classes with an allocation listener over the measured
      iterations.
- [ ] **`research-parts-peak`** (from research-voxel-pingpong; Opus): a parallel pass of a merging job (a `Reduce<T>`,
      Emit) keeps a box per chunk (ArityGenerator's ForEachEntityChunks `_parts`), grown when the pass is cut into more
      chunks than ever: the voxel server at 2,000 players allocates 4,088 B after its warm-up in WaterFlowSystem, and in
      FloatSystem and SendEntitiesSystem, as the loaded chunks reach a new peak. Size the boxes from the tables' room (as
      the tile step's row sets, 73a56430) or the reserve, so a world that reserved its peak allocates nothing.
- [ ] **`docs-grids-red-black`** (docs pass; from research-voxel-pingpong): `guide/performance/grids.mdx` (+ zh)
      still teaches red-black ordering by hand (`Red` / `Black` tags, two systems) and says plain Gauss-Seidel is not
      parallel; `[GaussSeidel]` does it (colors from the keys, sweeps in place, sleeping tiles): rewrite the bullet
      around it, the voxel server's scent as the example.
- [x] **`fix-generated-dead-lines`** (exemplar 2c; branch `fix-generated-dead-lines`, ready to merge): `IPairJob.Stamp` and
      `CutoffsSquared` are `static virtual` members with empty defaults (a ref struct job implements every instance member,
      CS9245), written only when the pass reads them (a ref component of the row; a cutoff per row); an element loop's
      span names a plain collection without parentheses. The battle server has its `Sightings` pair pass and `LootValue`
      entity method back (page en + zh); GeneratedTextTests: empty methods 1 (an interceptor of LambdaPassTests), both
      parentheses kinds 0. A/B (PairLists, PairHalves, BattleServer Tick, two rounds; PairLists' Generated three more): no row slower
      in every round, 0 B; ForPairs and Reaches of the PairLists jobs compile to the same machine code as base (FullOpts).
- [ ] **`feat-lane1-core`**: the first run (from 3315196) was **lost with its container on 2026-11-09** before any
      push; only its interim reports survive. Rule since then: a lane hands back per item, each item is gated,
      merged and pushed before the next. What the lost run found (to rebuild or to record, not to re-research):
      - C11 dropped: both variants lose (Runner 1000 10.3 -> 16.4 us, ChunkSize Light 100k 18.1 -> 22.2 us,
        SumParallelReduce 6.0 -> 8.1 us): noise in "summed thread time x the last probe's ratio" sends passes to runs
        alone, and costs taken from runs alone cut light passes into fewer, longer chunks. One variant (base chunk
        sizing, probes alone decide) was never measured.
      - C12 keep: QueryTick 0.46-0.52x of base; untracked queries equal base's generated code. Rebuild.
      - C13 dropped: the Tier1 listing shows the walk state round-tripping through the stack per row in base and
        head; the id load is not on that chain, and the mode test lengthens the loop (QueryWrite 1.21x).
      - C14 keep: LateHoles Move2 0.79-0.84x, Move3 0.82-0.92x, Step 100k 0.86-0.89x, 0 B. Rebuild.
      - C15 dropped: CleanupBenchmark 105.6 -> 133.5 us; the release pass tests all 10k rows' bits and every freed
        entity leaves a hole the pass end fills from the tail (destroy 53 -> 29 us, release 32 -> 76 us).
      - C16 unconfirmed: store step fine (FullCopy Grow 0.95-0.97x), ColdCreate PerEntity 1.14-1.33x on a noisy
        round; needs a quiet round and, if real, the cause (the explicit tail clear touching pages).
      - C17 not built: the cost is per visited row (21-37 ns), which a log still pays; the summary scan it would
        replace costs about 28 ns.
      - C18: Fibonacci top-half hashing under the dictionary's prime modulus put 600 lattice keys into 266 of 631
        buckets (LookupUnique 2.07x); folding the product's halves gives about 380. The fold was never measured.
      - C19: built (NativeColumns + NativeBlocks, `tools/native/mimalloc.sh` pinned to v2.1.7 / 8c532c32, CI on
        runner labels already in use: the package carries linux-x64's library, other runtimes keep the pinned heap);
        prototype 0.14-0.70x of pinned for allocate-fill-free; `GC.AddMemoryPressure` dropped (it starts gen2
        collections). Rebuild and measure.
- [x] **`feat-net11`** merged (f2da0801) (owner, 2026-11-14, ledger L14): `net11.0` beside `net10.0` in the package, tests, benchmarks,
      CI and docs; .NET 11's performance features tried one by one, each kept only as a measured win.
      Running on branch `feat-net11` (from e83188c). Package A (compatibility) handed back: both targets build at 0
      warnings and pass every test; two generator fixes .NET 11 needed (Kernel.Tables' `[..]`, DegreesToRadians as the
      runtime's vector form). Next: package B, the research list `docs/plans/designs/net11.md` (runtime-alone numbers,
      then B1 Unzip/Zip). Docs for the docs pass: docs-todos `feat-net11`. After the merges: rebuild the live demo
      (its hash covers Core's csproj).
      **Package S (owner, L16)**: the site shows both runtimes. `bench.json` gains a runtime dimension
      (`sync-benchmarks.ts` merges a report into its runtime's numbers and refuses only a mismatch within one
      runtime), the tables and charts show .NET 10 and .NET 11 side by side, `bench.sh --site` takes the runtime;
      CI's per-runtime jobs become a matrix (parallel), and a `workflow_dispatch` benchmark workflow runs
      `bench.sh --site` once per runtime as a matrix and uploads the reports for the sync. Package S handed back on branch
      `feat-net11`: done as described; open: the .NET 11 numbers themselves and where published numbers come from
      (docs-todos `feat-net11`). Package S2 (L17, L21, L22) and package B handed back: the site's numbers from the
      Benchmarks workflow (`tools/perf/sync-ci.sh`), .NET 11 as the headline runtime with .NET 10 as fallback,
      BenchmarkDotNet 0.15 made to run on .NET 11; net11.0 equals net10.0 within error on the headline worlds; no .NET
      11 API ships code (net11.md section 3). Open: the first CI `--site` run and its sync; D21's .NET 11 part.
- [ ] **`feat-generated-text-2`** (owner, 2026-11-14, ledger L15): running on branch `feat-generated-text-2` (from
      b95fb459 + feat-ground-cells). **Package A handed back** (a-h and what a read of the output added): `Tidy` (the
      last pass of `Code.Aliased`) aliases every type a file names twice or more and drops the parentheses precedence
      does not need (the benchmark assembly's 16,999 method bodies IL-identical); Kernel / Loops: no temp before a
      store, `__passF` once per vector (not where it would split the job), single-use setup locals read in place, the
      first loop from 0, the schedule line's repeated hardware term, `AllBitsSet & m`, `var __m = __has1`, a write-only
      lane declared by its first write, `SetAll(default(T))` with no `v0` line, no `jN = new Job()` in fused jobs,
      `this.` only where a column ref shares the name. Benchmark project's generated files 5,048,414 -> 4,509,703 bytes,
      73,843 -> 77,236 lines (alias lines), 39,358 -> 18,687 `global::`; GeneratedTextTests corpus 12,310,172 ->
      11,532,883 bytes, `global::` 85,849 -> 17,810, parentheses 333 -> 0, identity copies 108 -> 40. FullOpts listings
      of 7 benchmark classes: 16 runners differ (instruction order, spills, the dropped `v0` store), BenchmarkDotNet
      decides (docs-todos `feat-generated-text-2`). A/B (two rounds, net10.0): runtime classes within noise; KernelBenchmark
      Share 10 % slower at 1,000 rows without `AllBitsSet` in the bail test, so that text is back (6 counted in
      GeneratedTextTests). GeneratorBenchmark Cold: the masked jobs translated twice more for the pass masks (+60 MB),
      fixed (PassMasks over the loops written once); then the alias passes made cheap (no regex, one buffer, collections
      per thread, one read per line): Cold 0.91-0.99x of base at 336 MB against 416 MB, the Edit rows level (0.85-1.11x
      across two rounds, both directions), Compile level at lower allocation. Merged claude/relaxed-ramanujan-j4j9k4 (a1eeebb5).
      **Package B handed back**: item i as the merge of an if / else's two writes of one lane (x = Select(m, a, b), a
      masked word selecting by __pass once; KernelBenchmark Step / StepHelpers / Holes / Halves 0.75-1.01x, two rounds,
      5 runners shorter in FullOpts, none longer); item p, aliases of one simple name qualified by the outer names that
      tell them apart (AliasTests), and no alias taking a __ name the user's code declares (Code.Reserved: a user type
      __Vec or __Heat broke the generated code before).
      **Package C (study) handed back**: docs/plans/designs/lane-kernel.md, the prototype in Core
      (VectorLoops/LaneKernel.cs) and LaneKernelBenchmark: one body per job over ILane<TSelf, T> (Lane512/256/128<T>),
      the 512 -> 256 -> 128 -> row chain in Core, bit for bit on every width setting; each width's walk a method of
      its own (the inliner's budget); flat jobs level, conditions and reductions not yet (the JIT's import-time forms
      and mask registers lost through a lane struct); 64-70 % less text. The owner decides the rollout.
      **Owner, L33**: roll the kernel out for flat jobs first (package D), keep researching the walk form and the
      reduction until each shape is level, then shape by shape (package F); fix today's 128-bit (SSE) path apart
      (package E).
      **Package D handed back on branch `feat-lane-kernel`** (from 89090767 + this branch): Core's LaneKernel
      (the typed form: ILane, Lane512/256/128, ILaneStep4, Run / RunFlat / RunMasked, hidden; the width and walk
      forms moved to benchmarks/.../LaneKernelStudy.cs); SystemGenerator.LaneStepOf decides the flat shape from
      Kernel.Code (src/EasyEcs.Generator/AGENTS.md, "The lane kernel's shape"; audit-vector-loops) and EmitLanes
      writes a __Lanes struct with one Step and Execute's LaneKernel call; every other shape keeps its loops. 24 of
      the benchmark project's 260 vector jobs take it; Integrate's job 4,304 -> 2,372 bytes, the project's generated
      files 4,522,249 -> 4,502,057, the GeneratedTextTests corpus 11,532,139 -> 11,524,667. Bit for bit under the
      four width settings (LaneKernelTests and the vector-loop test classes), full suite on both runtimes, AOT,
      inlining.sh 0. A/B (two rounds, base 89090767, scratchpad/kernelD/ab.sh): FragmentsIntegrate 1.10-1.25x (a
      NoInlining walk call per width on 5-20-row tables); fixed by one walk per run with the job by value (a08825cf,
      FragmentStudy level to 0.89x); the fix's A/B scratchpad/kernelD2/ab/ab.sh: the merge waits for it
      (only level or a win at 0 B with nothing slower ships). Details: lane-kernel.md "Package D", docs-todos
      `feat-lane-kernel`.
      Package E (open): today's generator's 128-bit path from what the kernel's SSE code shows.
      **Package F, form 1 handed back on branch `feat-lane-walk`** (rebased onto feat-lane-kernel 05f91b47, the walk
      once per run): uniforms. The step form's broadcast stayed in the loop because RyuJIT's hoisting declines a cheap
      tree once the loop's count of vector locals (lane-struct temporaries included) reaches the registers it assumes;
      every lane job is now its own walk (ILaneWalk4.Walk, its loop in the job, uniforms broadcast before it, read in
      Execute), inlined per width into D's one walk per run. 29 lane jobs in the benchmark project against 24. Its first
      A/B found HolesHaul 1.21-1.31x: a NoInlining call per masked word (spills, stack arguments, prologue around ~5
      vectors); masked words now inline their walks into the runner. The owner's reduction item (2026-11-16): the
      study's Tally 1.3-1.45x at 100,000 rows was the conditional sum blended instead of a masked add (the sum reached
      SelectEqual as an argument temporary); ILane.AddWhereEqual gives the emitted loop's instructions. Gate
      scratchpad/kernelF2/gate.sh; A/B scratchpad/kernelF2/ab.sh (base a08825cf): ships only as level or a win at 0 B.
      Details: lane-kernel.md "Package F".
      Package F (open after form 1): a select under one comparison, Min / Max against constants, 8-byte lanes
      (ILaneWalk8), reductions, the masked walk's passing bits hoisted; each rolled out once level at 0 B.
      The original item: read the generated files of the GeneratedTextTests corpora, the samples and the
      benchmark worlds for redundancy (one value assigned to several locals, identity copies, unread locals and
      masks, repeated setup, `this.v0`, dead narrow temps: G17's rest in owner-review, now to do), remove each in the
      generator, add its count to GeneratedTextTests with a budget of 0; IL-changing ones proven level by Tier1 or
      BenchmarkDotNet.
      Read by hand on 2026-11-14 (the benchmark project's 836 generated files, 4,961,043 bytes, 72,648 lines):
      a. user types spelled in full: 20,708 `global::EasyEcs.Benchmark...` names = 1,006,406 bytes, a fifth of the
         text; a per-file alias per component / singleton type (`using __T1 = global::...Heat;`) as G14 did for
         the BCL names;
      b. attributes outside the alias block: 442 `[global::System.ComponentModel.EditorBrowsable(...Never)]`, the
         `UnsafeAccessor` lines, the explicit `IForEachBatch<...>.WriteMask` / `OneQuery` members;
      c. a temp then a copy: 149 `__c1_0 = __t0;` after `var __t0 = (...)`, then the store: store the expression;
      d. outer parentheses: 1,520 lines `= (expr);` and nested `((a + b) - c)` where precedence needs none;
      e. `As<int, float>(__pass)` spelled at every write of a masked loop (351 lines): one `__passF` per iteration;
      f. `ref readonly Pull __p3 = ref s3;` used once as `__p3.Value`: read `s3.Value`;
      g. `rows - i >=` / `lanes - i` in the first width's check, where `i` is the literal 0 above it;
      h. the `__loops` text's condition repeats `__Vec.IsHardwareAccelerated` in two of its three terms;
      i. not text only, measure: a masked loop selects by `__pass` at every write of a field
         (`__c1_0 = Select(m6 & pass, u7, c1); __c1_0 = Select(~m6 & pass, c1 + c2 * u8, c1);`): compute the
         writes unmasked (`Select(m6, u7, c1 + c2 * u8)`) and select by `__pass` once at the store, one select
         and no `& __pass` per extra write; KernelBenchmark's masked cases and StructuralLoop decide;
      j. the 512-bit and `Vector<T>` bodies are the same text with another prefix (the largest duplication, about
         half of every vector job): #172's "one body per width" needs a width-generic helper the JIT specializes;
         try it on one job, Tier1 equal or it stays.
      k. (owner, L28) one fallback scheme, 512 -> 256 -> 128 -> the method per row, for every vector loop: the
         width chosen on a machine by explicit widths (`Vector512`, else `Vector256`, else `Vector128`; ARM64 is the
         128-bit step) instead of a 512-bit body plus a `Vector<T>` body, and the rows a wider loop leaves run at
         the next width before the scalar tail (today up to 15 rows of a 16-lane loop run the method). Built on
         item j's one body per job (three instantiations of one text, not three texts); `Interleaved`'s loads and
         stores per width follow (feat-interleaved-256 is the 256-bit step's de-interleave). Measure: short
         tables (tails dominate: 1-63 rows), the headline worlds, Skylake-SP's case with
         `DOTNET_PreferredVectorBitWidth=256`, SSE-only with `DOTNET_EnableAVX=0`; the inliner budget (inlining.sh)
         and the generated bytes decide how many steps a job gets.
      m. read again by hand after package A (2026-11-15; benchmark project: 4,508,847 bytes, 77,226 lines): the
         alias header is now the longest part of a small file (22 `using` lines over a 100-line file; 6,740 `using`
         lines, 9 % of all lines). The BCL and EasyEcs aliases are the same in every file: write them once per
         project as `global using __V512 = ...;` in one generated file, keep per file only the aliases of its own
         component types. Check: no clash with a user's own `global using`, the IDE's cost of one more file,
         GeneratorBenchmark Edit rows level.
      n. `__c1_0 = __c1_0 + __c2_0;` then `StoreUnsafe(__c1_0, ...)` (305 lines `x = x op y;`): store the
         expression when the lane is not read again; a masked write likewise selects straight into the store.
      o. the flat and the masked `Execute` repeat the same preamble (column refs, `rows`, the element refs) and
         the per-row tail; with item l's kernel both go away, without it they could share one local function.
      p. (owner's question, 2026-11-15: "如果用了A.Position和B.Position会怎样") two types of one simple name in one
         file. Tidy.AliasOf today: the first met takes `__Position`, the second `__B_Position` (its outer name), a
         third or a closed generic over them a number (`__Span_Position2`): correct, but the reader cannot tell
         which is which. Make it symmetric: when a file names two types with one simple name, every one of them
         is qualified by as many outer names as tell them apart (`__A_Position`, `__B_Position`,
         `__Span_A_Position`), no numbers unless the full names differ only by arity. Tests: a system over
         `A.Position` and `B.Position` (both as parameters, in a filter, in a `Span<>` alias), nested types of one
         name in two outer types, a user type named like a BCL alias (`Vector`, `Unsafe`, `Span`), a type whose
         name starts with `__`; each compiles and the aliases read apart.
      q. left by package B's `Code.Reserved`: a public type named `__X` in a referenced assembly, and the fixed
         aliases a file always writes (`__ForEach`), can still be shadowed by a user's `__` type; read the
         referenced assemblies' `__` names once per compilation (cheap: the metadata names, cached per reference),
         and give `__ForEach` the same reservation; tests for both.
      k2. (owner, 2026-11-15) the platforms item k and the kernel of item l cover: x64, x86 (32-bit: the same SSE / AVX
         intrinsics, fewer registers; `nint` is 4 bytes), arm64 (AdvSimd, 128 bits; SVE not used: no hardware to
         measure) and arm32. On arm32 RyuJIT accelerates no vector type (`Vector128.IsHardwareAccelerated` is
         false), so the chain ends at the method per row there: the study confirms it on the runtime's own
         statement and the tests run the scalar path (`DOTNET_EnableHWIntrinsic=0` reproduces it on x64). CI tests
         x64 and arm64 today; whether x86 and arm32 get a CI job (Windows x86 on the standard runner, arm32 under
         emulation) is the owner's question once the kernel exists.
      k3. (owner, 2026-11-15) the 512-bit step is taken only where the runtime says it pays: `Vector512.IsHardwareAccelerated`
         is false on the CPUs that lower their clock for 512-bit work (Skylake-SP/X, Cascade Lake, Cooper Lake) and
         when `DOTNET_PreferredVectorBitWidth=256`; the kernel asks that, never `Avx512F.IsSupported`, so those
         machines start at 256 (feat-interleaved-256's two-source permutes serve them). The study states it in the
         design and tests both start widths.
      l2. (owner, 2026-11-15) the form the owner expects for item l: the width as a struct type argument
         (`where TLane : struct, ILane<TLane, T>` with static abstract operators and `Load` / `Store` / `Select` /
         `Count`), the job as a struct type argument too (`allows ref struct`), so each width is its own
         instantiation (value types are never shared-canonical), every call is direct (no interface or virtual
         dispatch) and inlinable (`AggressiveInlining`). The prototype proves exactly these three in the Tier1 listing
         of each width, plus the inliner budget of the runner that calls the kernel.
      l. (owner, L29) research: a generic, shared kernel instead of per-job loop text. Today every job carries its
         own drivers (the flat loop per width, the masked loop per width, the pass-mask loop, the tails), about 100
         lines around a body of a few lines. Design `docs/plans/designs/lane-kernel.md`:
         - Core holds the driver once: `LaneKernel.Run<TJob, ...>(ref job, columns, rows)` and its masked form choose
           the width (item k's chain), walk whole vectors, hand the rest to the next width, then call the method per
           row; a job implements one generic step, `Step<TLane>(...) where TLane : ILane<TLane>`, its body written
           once over a width-generic vector type whose operators forward to static abstract members (Core's own
           `Lane512<T>` / `Lane256<T>` / `Lane128<T>` wrappers over `Vector512/256/128<T>`), so the generated text is
           the body alone and reads like the method.
         - Questions the prototype answers: does the JIT specialize `Step<Lane512<float>>` to today's Tier1 code
           (struct type arguments get their own instantiation; static abstract calls on them inline) within the
           inliner's budget of the runner; are loads/stores of interleaved fields (`Interleaved.Load2..4`) expressible
           per width behind the same interface; do hoisted broadcasts (`__u1 = Create(0.5f)`) stay hoisted when they
           live in the job's step; does a structural mark, a reduction's accumulator and a `[Changed]` stamp fit
           the step's signature; NativeAOT (the AOT check) and generic virtual dispatch never appear.
         - Prototype on three jobs (a flat float job, a masked job with a condition, a reduction), Tier1 listings
           against today's, BenchmarkDotNet on their classes, generated bytes before and after. Rollout only where
           Tier1 is equal or better; a job shape that loses keeps its emitted loop, and the design says which.
      **Package E merged** (`feat-sse-path`): under `DOTNET_EnableAVX=0` SSE4.1's blend pins its mask to XMM0 and
         the register allocator spilled a value the loop carries around it in every iteration (a store-to-load
         chain; TallyGenerated 1.7x). A Vector<T> / Vector128 loop with one select selects through
         Interleaved.Select (three bitwise operations there, ConditionalSelect elsewhere); loops with several keep
         the blend (the xor form cost Gate, Grade, Clamp and the masked Step 8-24 %). A/B, two interleaved rounds
         (scratchpad/kernelE2): SSE4.1 25 rows faster (Tally, Cool, Brighten 40-60 %), 95 level, Move4Generated
         100k slower +8/+28 % (no select, its text is unchanged: noise at a memory-bound size, the same row at 1k
         is faster); default (AVX-512) 3 faster, 118 level; all 0 B. Details: lane-kernel.md "Package E".
- [x] **`feat-net10-only`** merged (27baf410) (owner, L25, L26): undo feat-net11's second target. Core, tests, AOT check, benchmarks,
      plugin, tools, samples and the template target `net10.0` only; `global.json` back to the .NET 10 SDK; CI and
      release install .NET 10 only (the per-runtime matrices go, the Benchmarks workflow runs one runtime);
      `bench.json` and the site back to one runtime, `sync-benchmarks --replace` and `tools/perf/sync-ci.sh` kept for
      L17; BenchmarkConfig's .NET 11 toolchain workaround and `--frameworks` go. Kept: Kernel.Tables' `table[..]` and
      the runtime's `DegreesToRadians` vector form (a .NET 11 project compiles and matches bit for bit), `net11.md` as
      the record of what was measured, a decision record in docs-todos. Docs and instructions say ".NET 10 and later".
      **Amended by L27**: the unit-test project keeps `net10.0;net11.0` (it references the net10.0 library from
      both), global.json stays on the SDK that builds both, and CI's test job is a matrix over the two runtimes;
      nothing else targets or installs .NET 11 (benchmarks, AOT, samples, tools, template, release).
      Handed back on branch `feat-net10-only`: done as described, with L27 (global.json on SDK 11.0.100; the unit
      tests on both runtimes, CI's x64 test job a matrix over them); the site's numbers still come from CI (L17).
- [x] **`feat-interleaved-256`** merged (cc240a9e) (found by feat-net11's B1): a two-source permute (`vpermt2ps`,
      `Avx512F.VL.PermuteVar8x32x2`) in `Interleaved`'s 256-bit loads and stores, behind `Avx512F.VL`: the path x64
      AVX-512 machines run by default (`Vector<T>` stays 256 bits there), 4 shuffles a pair of fields today. Both
      runtimes. Proof: KernelBenchmark's interleaved cases, the headline worlds, Tier1; DOTNET_EnableAVX512=0 unchanged.
      Handed back on branch `feat-interleaved-256`: Load2 / Store2 and the 4-byte Load4 / Store4 take the permutes;
      the path runs only where Vector512 is not accelerated (Skylake-SP / X, Cascade Lake, Cooper Lake by default, or
      DOTNET_PreferredVectorBitWidth=256), so x64 machines from Ice Lake on keep their Vector512 loops. Load3 and the
      8-byte Load4 keep their forms (llvm-mca: no better). Numbers and listings in docs-todos.
- [x] **`fix-allocation-tests-warmup`** merged (95ff51e5) handed back on its branch (causes, fix and proof in docs-todos): JIT compiles in
      the window (`Allocations.Steady`), a Scratch list made late, a test frame that leaked links, and a core fix (a
      merging parallel pair pass cut by count). The item as it was: 0 B tests that fail once in many full-suite runs on a loaded machine and
      never alone: `AllocationTests.ParallelReductionsScratchAndLookupsDoNotAllocate` (net11.0, AVX-512 off, gate of
      f2da0801) and `NeighborTests.APassOverListsAllocatesNothingOnceWarm` (net10.0, 336 B, lane 1's gate). Suspected:
      a fixed warm-up count loses the race with tier-up (OSR / Tier1 of a job compiling inside the measured window)
      when other processes hold the cores. Find the allocation's source first (an EventListener on the JIT and GC
      allocation ticks in the failing window, or `DOTNET_TieredCompilation` settings to reproduce: `TC_BackgroundWorkerTimeoutMs`,
      a busy machine), then fix the cause: warm up until N consecutive updates allocate nothing and the JIT has
      compiled nothing new, in one helper (`Allocations.WarmUp`) every 0 B test uses. Also: `HugePagesTests` goes
      Inconclusive without a free 2 MiB page (the count of tests then differs by one): expected, say so in tests/AGENTS.md.
- [x] **`chunk-floor-by-bytes`** merged (004db709) (owner, L31, L32): `MergedChunkRows` = max(floor, NextPowerOfTwo(rows) / 64), the floor
      from bytes: at least `MinChunkBytes` (a constant, measured: try 16 KB, 32 KB, 64 KB) of the columns the pass
      reads and writes, as rows = NextPowerOfTwo(MinChunkBytes / bytesPerRow), at least 64; a pair pass's bytes per
      row times its cluster's neighbour count (data, not machine). Needs the pass's bytes per row in the runner
      (the generated job knows its columns: a static `RowBytes`). Proof: the merging `[ForEach]` rows of
      ReduceBenchmark / EmitBenchmark and PairsBenchmark.GeneratedParallelResult level with the per-kind floors at
      every size, 0 B, results bit-identical across thread counts (a test at 1, 2 and 8 threads).
      **Running** on branch `chunk-floor-by-bytes` (built: core 352702af, generator 7ea76252, tests 6d2658c7;
      `MinChunkBytes` = 32 KB until the A/B picks; a hand-written job's `RowBytes` defaults to its type arguments' sizes,
      a hand-written pair job's to 0, one word). Full suite green on net10.0 and net11.0 but the new pair test's first
      build (fixed in 6d2658c7). The A/B (base 7e9e5e40 against 16 / 32 / 64 KB, two rounds, each side from its own root)
      and GeneratorBenchmark Cold / Edit* (base against head) run from the scratchpad scripts `floorbytes/run.sh`,
      `genbench.sh`, compared by `compare.py`; first round: 16 KB slower at 100,000 rows (chunks of 2,048 rows of an
      8-byte Spot: 49 chunks against 16), faster at 10,000.
      **A/B result:** 3-repeat interleaved --dev screens at 32 KB (49 chunks at 100,000 Spots) against base 7e9e5e40: level (not slower beyond both error bars in 2 of 3 repeats; EmitParallel 10,000 11.2-11.9 us against 13.7-14.9, 100,000 48.9-68.5 us against 49.0-65.2, LowestVector 1,000 to 1,000,000 rows level, 0 B everywhere), so `MinChunkBytes` stays 32 KB and `MergedChunks` 64; 32 KB with `MergedChunks` = 16 and 64 KB were also level.
- [x] **`fix-no-simd-structural`** (from 004db709; merged 2026-11-16, 5ddc511a): under `DOTNET_EnableHWIntrinsic=0` a lambda pass changing
      structure through a generated writer (`e.SetDaze(round)`) threw from the frozen gate: `LambdaPass.IsStable` saw
      only bound calls, the generator cannot bind another generator's writers, so the `Rows` job hardware without
      vectors runs carried `INoStructuralChanges`. Fix: `Symbols.UnboundEntityWriter` (the name rule of
      `GeneratedRead`) in `LambdaPass.IsStable`; test `ALambdaPassChangingStructureRowByRowRunsUnfrozen` (scalar on any
      hardware, failed before the fix). `TheScheduleNamesEachMethodsLoop` expects "; rows on this hardware" where the
      machine has neither width. Gate from the scratchpad `nosimd/gate.sh`; branch not merged.
- [x] **`perf-align-prefetch`** (owner asked 2026-11-16: "simd 有没有考虑内存对齐和 prefetch"), closed 2026-11-19 on
      branch `perf-align-prefetch` (from ffcc09a4; not merged). Results, numbers and causes: microarch.md,
      "perf-align-prefetch". What held already: unmanaged columns start row 0 on a line (`Archetype.Aligned`), runs and
      chunks on 64-row words, so whole runs load whole lines (Tier1 listings, 12 and 24-byte rows included); sequential
      reads are the hardware prefetcher's. **Shipped (c)** (41d4e3bf, tests d979e81a): a masked word's spans start on
      the cache line of its first passing row, not on the row (every 512-bit load of the word was split across two
      lines): default job, four rounds against base, Halves 100k 0.82-0.88, 1k 0.90-1.02; Move2Gaps 1k 0.88-0.93;
      LateHoles Move2 / Move3 100k 0.88-0.98; LateHoles Move2 1k 1.04 / 1.06 then 0.98 / 1.01 (not slower in every
      round); Move2Gaps 100k slower in two of six rounds (base StdDev to 8 %). `IForEachMasked.Execute` no longer says
      bit 0 is set (reference regenerated, 72b26a0f). **Dropped (a)**, prefetching the next small table's columns: in
      the L2 every form slower (1.06-1.65; loads without the prefetch as slow: the next table's metadata chase costs),
      gated past the L2 slower both rounds (TablePrefetch 16,384 Integrate 1.147 / 1.083, Move2 1.112 / 1.079);
      `TablePrefetchBenchmark` (48b06f34) kept as the evidence. **Dropped (b)**, prefetching the next page's first line
      per column: level to slower at 4M and 16M rows (0.98-1.11 but one 0.76). Gate: tests net10.0 and net11.0 4577
      passed each (2 skipped); ApiDocs: no new problem (World.ReserveBuffers' stale zh entry is from before). Docs:
      docs-todos "perf-align-prefetch".
- [x] **`feat-ground-layer`** (owner, L19; L34) merged (e87d5bdc) (branch head 6c594ca3 + this Plans commit, from
      ff4baa8d): `Layer<TCell, TTile, TLayer>` (TLayer : ILayer, a static Index) beside a `Cell<TCell, Ground<TTile>>`,
      its layer exchanged alone (`Tile.ExchangeLayer`) and read as one more stream of the row's vector loop; EECS115 for a
      layer it cannot read (another tile, past its layers, no ground cell); EECS073 for two layers of the same cells (the
      thread's one texture of them; its message now names the cells, not a Tile<> of the Layer's arguments). Code fix:
      not built (owner-review.md). TileGroundLayerBenchmark (32 x 32 chunks of 16 x 16 x 8, stone in the top layer slowing
      the warmth; us, 0 B everywhere; Hand (Tile by value, ExchangeHalo(7, 7), Load128) / Cell / Off): 1 thread 1,199 /
      772 / 1,295; 4 threads 442 / 307 / 413: the cell at 0.64x / 0.70x of the walk by hand. Gate: 0 warnings; tests
      net10.0 + net11.0 4410 (2 skipped) with the new EECS073 case after its fix (ViewTests.APassThroughAViewAllocatesNothing
      failed once in the full run, a compile in its window; it passes alone, the branch touches no view code); tiles,
      layers and their analyzer and generator tests under AVX=0 and HWIntrinsic=0 (163 each, both runtimes); ApiDocs 0
      problems (the reference left for the docs pass); agents 0 problems. Docs: docs-todos "feat-ground-layer".
- [x] **`feat-ground-cells`** merged 2026-11-14. (inner-loops leftover (a), from fc26ea4): the first run was lost the same way before
      reporting; `Cell<TCell, Ground<TTile>>` already ran through the cell pass (no core or
      generator change was needed: the shape is any ITileShape); proved by TileTests.Cells.cs (2a12b41: a cross stepping
      the ground in place, bit for bit against the whole ground, SimdMode.Off and the column walk by hand, also with
      AVX-512 and AVX off; a box blur; 0 B). EECS103 v2 extended (d77b4f2, tests de0a617): a `ref Tile` stepped by a walk of
      `Columns(lanes)` and `col.Store` as the cell step (`out TCell`), only when its value is +0 on blank cells; a
      `Ground<TTile>`'s literal width read from TTile. TileGroundBenchmark (1daf18e; Ground<Chunk> of 16 x 16 x 8 chunks
      stepped in place, us, 0 B everywhere; Cell / Hand512 (Columns(16), Load512) / Hand (Columns(4), Load128) / Off):
      Cross 1 thread 32: 421 / 442 / 1,218 / 623; 512: 198 / 214 / 402 / 239 ms; 4 threads 32: 247 / 262 / 611 / 335;
      512: 78.7 / 81.2 / 156 / 94.3 ms. Box 1 thread 32: 401 / 491 / 505 / 742; 512: 221 / 240 / 274 / 276 ms; 4 threads
      32: 271 / 301 / 302 / 381; 512: 84.2 / 96.8 / 90.9 / 96.6 ms. The cell is at or below every hand-written walk in all
      eight (Cross against Hand512 within the error, about 10 % on this shared machine). Head against base is the same code:
      the branch changes no generator or core path the pass runs (only the analyzer). VoxelServer has no
      `Ground<TTile>` (its Ground is a [Tile] of its own) and its scent step is a Gauss-Seidel scan, not a stencil the
      form covers: nothing rewritten. Not built (owner-review.md): a ground cell sampling a layer of its 3D tile (a
      public form for the owner). Docs: docs-todos "feat-ground-cells".
- [x] **`feat-lane5-renames`** merged 2026-11-08: R1-R6 (tests 4237 + 1 skipped: R4 deleted 17, R3 added 3; DocCheck 28 at
      base and head). Left: the internal `World.ChunkRows(...)` and `PairHalves.ChunkRows` follow `MaxChunkSize` as
      `ChunkSize`, after feat-lane1-core merges (it edits World.Parallel.cs).
- [x] **`feat-restructure`** merged 2026-11-08 (Finish 1b, D142): the four projects in concern folders, `tools/perf/`,
      `tools/docs/`, `docs/plans/designs/` and `owner/`; tests 4251 passed, 1 skipped, as the base.
- [x] **`feat-lane4-generator`** merged 2026-11-08 (2025276): G1-G4, G6, G9, G10, G12-G14, G16, G19, wide masked words,
      EECS133.
- [x] **`feat-inner-loops`** merged 2026-11-08 (head fda40ab: WP1, WP4, WP5b (b), WP6b, WP6c, WP7 FireDamage and the
      Sunlit / InWater split, WP8 (e), (f), (h), (d) committed, `Vector2/3/4.Clamp` as a vector loop: ClampVector
      4.1-5.6x, 0 B; gate: 0 warnings, tests in three modes, DocCheck, ApiDocs, agents, VoxelServer; ViewBenchmark Lambda
      90.6 -> 24.5 us at 100k, the other rows equal). What is left runs on **`feat-inner-loops-rest`** (from fda40ab):
      (d)'s measurement, lambda fusion, (b), (g) without Clamp, WP8's rest (Dig, Gone, Play), the demo's batch Move,
      then (a). **feat-inner-loops-rest merged 2026-11-08**: (d) the blank fill kept (TileLayers 1 thread 701 vs
      764 µs, 4 threads 292 vs 351); lambda fusion (LambdaFusion Fused / Apart 0.51 at 1M, 0.48 at 1M parallel, 0.58 at
      8M parallel, equal at 10k); (g) masked words in parallel chunks, Maybe<T> in parallel and of byte / short lanes,
      masked words of narrow lanes, narrow structural marks, Pow under FloatMode.Fast (VectorForms, head against base,
      µs: MaskedParallel 16.5 vs 85, MaybeParallel 14.6 vs 107, MaskedNarrow 18.8 vs 106, MaybeNarrow 9.4 vs 188,
      MarkNarrow 19.0 vs 204, Pow 265 vs 673); the demo's batch Move. Dropped with causes in owner-review.md: (b) masked
      AVX-512 tails (the helper's call), Atan2 / Asin (D127), wide rows' masked words (halos files); Dig / Gone
      inherent. Left: (a) a `Columns<TTile>` cell after the halos merge; the demo bundle rebuild. The original item follows. (`inner-loops.md`; Kernel*.cs, SystemGenerator.Loops/Elements/Cells/Lambdas.cs,
      LambdaPass.cs, HandSimd.cs, the analyzer's Lambdas / Views / Forms areas). 25 commits past f9faa23: WP1 lanes and
      unrolling, EECS099 for two parameters, counted sums as popcount, lambda + structural, lambda + view, WP4's
      `ParallelFor` lambda over a buffer (aef32bb, LightLambda / HeavyLambda). Left on it: WP4's rest (then the
      `audit-vector-loops` line "a ParallelFor job's Execute is compiled only in lambda form"), WP6b (EECS103 v2: tile
      `Load128` -> cell form) and WP6c (v3: `VectorN.Create(span.Slice(i, n))` -> plain loop), each with its code fix;
      WP8 (fixable-reason audit: scalar methods without a Warning in samples, benchmarks, templates, demo, fences;
      `generator.md` "WP8" lists the candidates: `--x`, a singleton field written, narrow integer division by a
      constant, lane sizes that differ); lambda passes fused (Fusion.cs, SystemGenerator.Fusion.cs); and the extra
      items:
      a. a cell method over a `Ground<TTile>` tile (was "a `Columns<TTile>` cell", inner-loops.md WP5): `feat-ground-cells`;
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
- [x] **`feat-generated-members`** (Finish 1a; `finish-scan.md` Report 1, names approved 2026-11-07; PartGenerator.cs,
      Parts.cs, Declarations.cs, Tile.cs, EasyEcsAnalyzer.Parts / Tiles.cs, the code fixes): `[Tile]` writing
      `ITile<TKey>`, the shape and `Beside`, `Beside` inferred from an `int` key or `X` / `Z` (/ `Y`) members, an Error
      for a 2D key named `(X, Y)`; `[UniqueKey(SlotsPerAxis = N)]` writing `Slots` / `SlotOf` (its doc comment says the
      slots wrap); `[MinBy]` / `[MaxBy]` in the scalar form (doc comments and EECS064's message contrast them with
      `[Min]` / `[Max]`); their rules (Info + fix for a hand-written `Beside` / shape, EECS068 extended to `[Tile]`,
      Info + fix for hand `Slots` / `SlotOf`, an Error for a non-power-of-two, EECS064 / EECS065's fix for MinBy);
      the EECS086 `GeneratedFor` fix (resets matched by type, not by name: `analyzer.md` 5); caching assertions and
      GeneratorBenchmark.
      Merged (6528999): `[Tile]` and the inferred `Beside` (EECS124-125),
      `[UniqueKey(SlotsPerAxis = N)]` (EECS126-127), `[MinBy]` / `[MaxBy]` with the scalar loop (EECS064 / EECS065 /
      EECS068 extended, EECS055's reason exact), EECS086's resets matched by GeneratedFor, BattleServer's `Battle` an
      `[Accumulate]` total and `Weakest` a `[MinBy]`; its docs are the "feat-generated-members" section of `docs-todos.md`.
      Numbers (a shared, busy machine): BattleServerBenchmark Tick 100k base 3.963 / 3.827 ms, head 3.860 / 3.952 ms
      (a wash, 0 B); ReduceBenchmark Lowest generated against hand-written swaps the lead between two runs (1k: 537 vs
      864 ns, then 845 vs 650 ns; 1M: 301 vs 314 us, then 253 vs 311 us; 100k: generated behind both times, 32.6 vs
      27.9 and 32.2 vs 24.6 us, errors 5-7 us), 0 B. Researched: the two forms are the same code. The generated
      Identity / Combine are the hand-written text, the two structs have the same fields in the same order, and the
      jobs the generator writes differ only in their names (diffed with the names normalized). `tools/perf/inlining.sh
      ReduceBenchmark`: every job inlined. The optimized listings (DOTNET_JitDisasm under tools/perf/Inlining, FullOpts) of
      the benchmark method, `ForEachBatchParallel` and `ForEachBatchRuns` for each job: 0 lines differ once names and
      addresses are normalized (227, 1191 and 710 bytes each). A run under the exclusive lock with `--launchCount 3`:
      1k 631 vs 717 ns, 10k 6.12 vs 5.45 us, 100k 35.3 +- 5.7 vs 32.6 +- 6.6 us, 1M 282 vs 346 us, every pair within
      its error, the lead swapping. What was left was the 4-thread pass's spread on a machine whose other cores ran
      other agents' work (the lock orders only the processes that take it).
      - [x] **MinBy vector form** (done in feat-lane4-generator, Lane 4 G9; the kept row's pre-check is gone, 064ebf8; `finish-scan.md` 5): per lane a compare and a select
        of every carried field, the lanes folded left to right so a tie keeps the earlier row (bit for bit on any
        thread count), then BattleServer's `FindWeakest` drops `SimdMode.Off`. Until then `Parts.KeptRowReason` gives
        EECS055 its reason before `Kernel.Vectorize` runs (SystemGenerator.ForEachModel.VectorLoop and
        EasyEcsAnalyzer.Performance.AnalyzeVectorLoop): replace that pre-check with the vector form. Also: lambda passes
        (SystemGenerator.Lambdas.cs) do not get the pre-check, so a lambda reducing into a `[MinBy]` type reports
        Kernel's own reason; and for a record's positional key Kernel.Reduced reports a property reason before the
        kept row. The proof: `ReduceBenchmark.LowestGenerated` against `LowestHandWritten` (same class).
- [x] **`feat-later-halos`** merged 2026-11-08 (head 4287251): push halos built and measured, the rings dropped (pull
      was faster: a kept ring holds the bytes the exchange copies, and finding what changed costs a scan per pass); what
      ships keeps each tile's neighbor ids per method and world (TileNeighborCache: TileExchange 0.62-0.78, TileActive
      0.61-0.85, TileStencil 0.85-0.93, TileLayers 1 thread 0.91-0.98, 4 threads within noise, 0 B). #160: 8-byte wide
      rows on AVX-512 and an AVX2 form, both for heavy math only (KernelBenchmark Dive 0.85 under AVX512=0, 0.48-0.64 on
      AVX-512; Ledger keeps its rows). Docs to-dos: docs-todos "feat-later-halos". Open: none.

## Lanes (not started)

### Lane 1: core (src/EasyEcs.Core runtime; the query enumerator templates of ArityGenerator.cs) — 20 items

Starts now; items touching `TileSteps.cs` or `Pairs.cs` wait for feat-later-halos. C12 / C13 / C14 edit
ArityGenerator.cs: not beside lane 4's generated-text steps.

- [x] **C0. Re-measure the stale bench.json rows before any A/B** (`perf.md` §0 and §2): SparseBenchmark
      PassOverTheFew (fixed by fa2770f the same day), TileStencil Blur9 / Laplacian, ParallelFor Light, Reduce
      Sum / Mass, Emit 10k, ParallelQueryComparison (then `tools/perf/inlining.sh` and jitdisasm of `Waypoints.Steer`'s
      job if still behind Arch / Friflo), OperationBenchmark EntityBatch (batch form slower than its own per-row form:
      count runs per pass, read the batch job), SingleEntityBenchmark Structural + Filtered (`--launchCount 6`).
      Proof: the numbers; a case still losing becomes its own item here.
      **Done 2026-11-16** (scratchpad c0, a clean copy: BDN refuses duplicate csproj files under `.claude/worktrees`).
      The machine read ~0.8x of bench.json across the board, so ratios inside a class count. Fixed since bench.json:
      Sparse PassOverTheFew (23x, now 1.08x QueryTheFew), TileStencil Blur9_Tile (0.89x Hand), Reduce Sum (0.16-0.38x),
      Emit 10k (0.79x serial, was 1.23x), ParallelQueryComparison (first, ahead of Friflo / Arch), EntityBatch (1.48x
      faster than per-row, level with Friflo), SingleEntity Structural (0.92-1.09, inside RatioSD 0.10-0.21). Still
      losing, each an item below: C0a-C0d.
- [x] **C0a. ParallelFor LightParallelFor at 10k is 1.36x serial** (5.6 vs 4.1 us), and its lambda form LightLambda
      is 1.64 us: find why the struct form costs 3.4x the lambda form at 10k (and 1.8x at 100k). Opus.
      **Done (perf-c0ab, 4027dbfe).** Re-measured on c9bb1b7c: 5.69 / 4.18 us (1.36x), LightLambda 2.01 us. Two causes.
      (1) Dynamic PGO devirtualized `ParallelWork.Run` at the runner's call sites and inlined the job there; inlined into
      `RunChunks`' one-chunk branch (inside its try / finally) the job's loop reloaded its two constants from memory at
      every element (9 instructions against 7, three loads against one): 1.35x its serial loop, in a process whose
      first calls were one chunk (a probe alternating chunk sizes in one process: 5.6 us against 3.95 serial; 4.0-4.3
      once a two-chunk call had come first). `RunItem` (NoInlining, no handler) is now the runner's one call of an
      item: PGO devirtualizes `Run` there and calls `Execute`, compiled as alone. A/B (default job, two interleaved
      rounds, base / head): LightParallelFor 10k 4.84 / 4.03, 5.79 / 3.82 us, 100k 31.9 / 25.3, 32.3 / 25.8; LightLambda
      10k 1.89 / 1.65, 1.97 / 1.74; ChunkSize, ParallelPass, ParallelRunner, ParallelSystems, BattleServer and TileStencil
      (two extra rounds for Blur9_Hand at 4 threads, 0.97 / 0.96) no row slower, 0 B. (2) What is left between the two
      forms is the job: LightJob's `Light` is a scalar loop (0.42 ns per element) and the lambda's job the vector loop
      (0.19 ns); at 10k both run on the caller alone (the runner finds helpers never pay: a run with them 13-17 us).
      EECS133 rewrites such a job as the lambda pass, but not this one: its Execute calls a helper holding the loop
      (a possible extension of EECS133, not done). Seen on the way: a measured cost near the 5 us floor flips the pass
      between one chunk and two (9,792 + 208 rows) run alone; with `RunItem` both cost the serial loop.
- [x] **C0b. Laplacian_Tile at Side 32 is 1.10x Hand (1 thread) and 1.16x (4 threads)**; Laplacian_Cell beats Hand
      (371 / 252 us). Find the Tile form's extra cost against Blur9_Tile, which beats Hand; or teach EECS116 to
      point the tile method to its cell form. Opus.
      **Done (perf-c0ab).** Re-measured on c9bb1b7c: 546 / 475 us (1.15x) at 1 thread, 311 / 271 (1.15x) at 4. Cause
      (Tier1 listing of WaveSystem.Step): each intercepted load computed `(uint)(dx + r)`, dx a temp of `x + k` (the
      interceptor used dx in both branches, so the inliner spilled it): a 32-bit add and a zero extension per load
      (`lea eax,[rdi+k]; add eax,2`), 26 instructions per column of the Laplacian's 13 loads, while the hand form folds
      each offset into the load's displacement. Fix (TileLoads): a loop from 0 or later takes its column back out of dx
      once (`int x = dx - k`), and `Column` adds x zero-extended plus `k + r` in 64 bits: every load of a column is
      `[base + 4*x + disp]` off one index (Blur9_Tile's 9 loads share one register too). A first form (two nested
      `Unsafe.Add`, and the local kept for k = 0) grew the interceptors past the stencil method's inline budget:
      `TileRows.MoveNext` and `MemoryMarshal.GetReference` stayed calls, Laplacian_Tile 1.18 / 1.02 (1 thread) and 1.10
      / 1.24 (4 threads) slower, dropped; the shipped form writes k = 0 without the local and checks a loop with a
      numeric bound through `Fits` over the first and last column the generator works out (less code than `Covers`),
      and every call of the method is inlined again (116 inlinees). A/B head (Core change only) / head + TileLoads, six
      interleaved default-job rounds: Laplacian_Tile 1 thread 0.894, 0.993, 0.885, 0.950, 0.960, 0.990, 4 threads
      0.918, 0.947, 0.941, 0.954, 1.080, 1.003; no row slower in every round, 0 B; the controls of unchanged code moved
      as much (Laplacian_Hand 0.86-1.10). What is left against Hand (about 1.05x at 1 thread): the exchange of the halo
      per tile and the store's row index read back from the stack each column. The cell form is the answer and needs no
      new rule: EECS103 v2 (HandStencil) already reports such a tile method with its cell rewrite (Laplacian_Cell 350-410
      us, 0.8x Hand); EECS116 stays about the second buffer.
- [ ] **C0c. MassParallelReduce at 1k / 10k is 1.12x / 1.06x MassSerialRef**: the parallel pass should fall back to
      the serial form below its break-even, as Sum does; and Mass has no serial Reduce row to compare. Sonnet for the
      row, Opus for the threshold.
- [ ] **C0d. Possible regressions to settle in a quiet round**: EmitSerial +25% at 10k and 100k (small StdDev),
      ComponentThenScan 100k +15%, the DotNetParallelFor baselines +8-27% (machine). A/B against the commit of
      bench.json's rows first; a real one becomes its own item.

- [x] **C1. `RemoveTypes` roots the removed types** (`core.md` §1): `_orderRequests`, `_tileSteps`, `PairState`'s
      clusters and lists, `_columnSpares` survive `ForgetTypes`; `Mentions` ignores `_orderRequests`, so a reused bit
      can get another type's `RowOrder`. Files: World.Order.cs, World.Types.cs, TileSteps.cs (after halos), Pairs.cs,
      Archetype.cs. Proof: `UnloadTests` with an ordered component, a pair method and a tile method (the
      `WeakReference` dies), a reused bit gets its own order. **Done: fix-core-lane1 did all but `_tileSteps`; fix-c1-tile-steps made ForgetTypes drop the `_tileSteps` entries of the doomed types and added a tile method to `UnloadTests.OrdersAndPairStatesOfAPluginLetItUnload`.**
- [x] **C2. `PairState` keeps a record per table forever, found by a linear scan** (`core.md` §5): drop entries of
      tables no longer matched, on `TrimExcess` / `RemoveTypes` (shared with C1); a slot per table instead of the scan.
      Files: Pairs.cs. Proof: a `NeighborTests` case cycling 100 `GroupBy` values; NeighborBenchmark with 64 groups.
- [x] **C3. Bulk walks bump `StorageVersion` when nothing moved** (`core.md` §2): `WalkDown` (World.Bulk.cs:171)
      bumps only when a word op flipped presence. Proof: a `SnapshotTests` case (the second `Capture(into)` after a
      `SetAll` stays incremental); a `SetAll` + `Capture(into)` per tick benchmark, 0 B.
- [x] **C4. The free-id list not sized to the initial capacity** (`core.md` §3): `_freeIds = new int[Meta.Length]`,
      `Math.Max(free, Meta.Length)` in `Restore`. Files: World.cs, World.Snapshots.cs. Proof: a 0 B test destroying
      1,000 of a reserved default world, and after a `Restore`.
- [x] **C5. Key reservations for a missing index add up without bound** (`core.md` §4): keep the largest, reset in
      `RemoveTypes` and `TrimExcess`. Files: World.Indexes.cs. Proof: `Allocations.Of` around the first index after
      100 `Reserve(prefab, 1000)`.
- [x] **C6. `Reserve(prefab, count)` takes a negative count and overflows** (`core.md` §6): throw like
      `ReserveBuffers`, compute in `long`. Files: World.cs. Proof: `Assert.Throws`; changelog Fixes line (docs pass).
- [x] **C7. `World.Entities` yields dead handles after the records grow** (`core.md` §7): read `_world.Meta` in
      `MoveNext`. Files: World.cs. Proof: a test creating past capacity in the loop, then destroying ahead.
      The first fix cost a range check per id (Introspection.Entities 18.6 -> 19.6 and 18.9 -> 20.4 us); 7177680 walks
      the records unchecked (they only grow): base 19.94 / 19.35 us, head 18.96 / 18.76 us, 0 B.
- [x] **C8. `Dispose` does not return buffer lists to their pools** (`core.md` §8): `ReleaseBuffers` per table.
      Files: World.Systems.cs, World.Archetypes.cs. Proof: 0 B first writes in the next world.
- [x] **C9. ThreadSetup's step list never shrinks** (`core.md` §9): compact in `Forget`. Files: Workers.cs. Proof:
      `WorkersTests`, 50 load / unload cycles bounded.
- [x] **C10. `EmitPool` allocates under its spin gate** (`core.md` §10): allocate after leaving it. Files: Emit.cs.
      Proof: the Emit 0 B tests.
- [ ] **C11. P1: helpers chosen on inflated work** (`perf.md` P1): a probe run alone every period while wide,
      `perItem` learned from alone runs; P1b: a merge pass under the floor runs its 16 chunks on the caller. Files:
      World.Parallel.cs. Proof: ParallelFor, Reduce, ChunkRows, Emit, ParallelPass, ParallelRunner, ParallelSystems,
      SystemDispatch, PairsBenchmark GeneratedParallel, TileActive Sparse 4 threads, BattleServer, WorldSimulation,
      MultiWorld (`--launchCount 6` for the parallel classes); a decision record extending D22 / D130.
- [x] **C12. P3: a query over tracked components stamps without a call per row** (`perf.md` P3). Files:
      ArityGenerator.cs (enumerator template), Query.cs. Proof: ChangeTrackingBenchmark QueryTick / Tick,
      MicroBenchmarks QueryIterate / QueryWrite (controls), tests of `[Changed<T>]` after a loop (full, break,
      structural change). **Done** (feat-lane1-core rerun, 7df979e + 2877f4f): stamping ahead was refused (a loop that
      breaks would report rows nobody touched); the exact form keeps a ref to each tracked component's ticks and stores
      the visited row's tick inline, a call only per word for the summaries, so the rows stamped are the rows visited,
      each before the body sees it. Against 0ac2281, default job, two rounds, net10.0, 0 B: QueryTick 100k Written 10
      base 353 / 415 us, head 220 / 206 us; Written 1,000 475 / 452 against 257 / 278 us; the Changed = true cases and
      Tick level (891 / 884 against 846 / 915 ns; 67.3 / 71.5 against 70.0 / 67.6 us); controls level: ToggleComponent
      112 / 122 against 109 / 123 us, QueryIterate 24.4 / 26.1 against 25.5 / 26.2 us, QueryIterate3 5.41 / 5.36
      against 5.19 / 5.00 us, QueryWrite 13.7 / 12.5 against 13.8 / 13.8 us (untracked: the Tier1 loop the same, the
      listings apart only in the cold path's registers, 886 / 890 bytes; a `--dev` pair read base 13.96, head 11.42).
- [x] **C13. P4: the query enumerator skips the id load and tombstone test on tables without holes** (`perf.md` P4),
      one branch with C12. Proof: MicroBenchmarks Query*, ToggleComponent, SparseBenchmark QueryTheFew; Tier1 listing.
      **Dropped** (owner-review.md, the lost run's cause: the walk's state round-trips through the stack per row).
- [x] **C14. The frame split of `World.Masked` / `Batches`** (#157's last row: the per-table preamble apart from the
      loop; ceiling about 5 ns of a 20 ns system): jitdisasm of `Masked` on Plain first. Files: ArityGenerator.cs
      templates. Proof: SystemDispatchBenchmark, the #157 classes; keep or record. **Not shipped** (feat-lane1-core
      rerun, 69b493b, owner-review.md): the masked half is 42b8e9d, already in base (the lost run measured from before it);
      the batch half (`TestedRuns` out of line) a wash at 0 B (CycleHolesScalar 1,000 base 5,533 / 5,973 ns, head
      5,660 / 5,978 ns; OptionalScalar 100,000 152 / 146 against 139 / 141 us); LateHoles' Tier1 code the same on both
      sides (`Masked` 2,126 bytes each), its head reading slower only under load 5-13.
- [x] **C15. P5: a retired entity flips bits in place** (`perf.md` P5): rows of a cleanup mask created in the table
      with the `Retired` column. Files: World.cs, World.Archetypes.cs, World.Indexes.cs; D57 updated. Proof:
      CleanupBenchmark (106 -> ~60 us), DestroyLogBenchmark, a `Without<Retired>` reader case, the headline worlds equal.
      **Dropped** (owner-review.md: 105.6 -> 133.5 us, the release pass tests every row and fills every hole).
- [ ] **C16. P6: unsized resizes uninitialized** (`perf.md` P6), after `[PageFaultDiagnoser]` and Tier1 of `Resize`
      show the clear costs. Files: ComponentInfo.cs, Archetype.cs. Proof: ColdCreateComparison PerEntity,
      MicroBenchmarks CreateAddDestroy, PageFaultBenchmark Grow, FullCopyBenchmark Grow.
- [x] **C17. P7: `Removed<T>` from a log of removals** (`perf.md` P7, low). Files: Archetype.cs, a log file, Query.cs.
      Proof: RemovalComparison, removal writers' cost. **Not built** (owner-review.md: the cost is per visited row,
      which a log still pays).
- [x] **C18. P8: integer tuple keys hashed by bytes** (`perf.md` P8, low). Files: ValueIndex.cs. Proof:
      IndexBenchmark LookupUnique, ValueQueryComparison, IndexComparison.
      **Done** (feat-lane1-core rerun, 4dda817 + 0bbab15): `ValueIndex` keys integer tuples by a wrapper,
      `BytesKey<TKey>` (the dictionary's own key, so its default comparer calls the byte hash and compare directly),
      read element by element. Three forms lost first, each with its cause: a comparer handed to the dictionary (an
      interface call per hash and per compare: LookupUnique 82.9 / 67.2 -> 204.7 / 225.4 us); the wrapper reading the key
      as bytes or through `Unsafe.BitCast` (one 8-byte load over the two 4-byte stores that wrote an `(int, int)`: a
      store-forwarding stall per probe, seen in the BenchmarkDotNet process's Tier1 listing of the workload,
      `mov dword [rbp-0x30]; mov dword [rbp-0x2C]; mov rax, qword [rbp-0x30]`; 160.6 / 154.2 against 89.2 / 69.0 us; a
      plain loop whose keys came from memory whole did not show it). Shipped form against c9518a2, default job, two
      rounds, net10.0, 0 B: LookupUnique base 102.5 / 74.3 us, head 53.0 / 49.9 us; LookupGrid 29.0 / 28.5 against
      23.7 / 25.2 us; LookupDirect (no hash on its path) level over four rounds (base 16.8 / 19.5 / 22.3 / 19.7, head
      20.7 / 20.1 / 17.8 / 22.2 us); RekeyAndLookup, RekeyAndLookupUnique, CreateUnique, TradeAndCount level.
- [ ] **C19. The core's unmanaged columns on mimalloc** (owner, 2026-11-07: option (b); `native-columns.md`), last in
      the lane and after Finish 1b's move: columns off `Array`, Dispose and a finalizer, `GC.AddMemoryPressure`, the
      NuGet `runtimes/` binaries and the pinned-heap fallback. Proof: `native-columns.md` section 4 (nothing slower,
      gen2 collections during creation counted); the M4 part is "owner, after code".

Not to build here: a generated countdown timer (`[Expires]`): the owner kept timers hand-written (2026-11-07,
owner-review "Countdown timers"); its lesson is a docs to-do.

### Lane 2: analyzer (EasyEcsAnalyzer*.cs, Symbols.cs checks, src/EasyEcs.CodeFixes, AnalyzerTests / CodeFixTests, GeneratorCorpus's filter) — 15 items

Starts now; Forms / Views / Lambdas areas wait for feat-inner-loops, Parts / Tiles for feat-generated-members.

- [x] **A1. A test runs the analyzer over the samples, templates and demo** (`analyzer.md` 1a, `code.md` 3.2):
      `SamplesAreClean` (sources + generated output, Info included, a negative control); GeneratorCorpus's filter
      narrowed from all EECS055 / EECS086 to the inherent EECS055 reasons (the EECS086 exception goes with Finish 2c).
      Files: tests/GeneratorTests.cs, benchmarks/GeneratorCorpus.cs. The gate for every severity raise.
      Done (feat-analyzer-lane2): `GeneratorTests.SamplesAreClean` over every sample but Godot, the template and
      the demo (Nino referenced), with a negative control and four tracked exceptions that fail once fixed (A2:
      Actors EECS109, VoxelServer EECS109 / EECS086 / EECS107); the template's `Spawner` is `[NotSaved]`;
      GameLoop's EECS086 was a false positive (a method that changes structure: fixed in EECS086). GeneratorCorpus
      filters only Info EECS055.
- [ ] **A2. Finish 2b: EECS086's non-reducing total, EECS109 and EECS055's bool reason become Warnings** (owner,
      2026-10-27; `analyzer.md` 1b-1d), after the exemplar pass removed their blockers (BattleServer `Battle` ->
      `[Accumulate]`, VoxelServer's `SendPackets` -> `[Drain]` and its bools split, Actors' `Storage.Save`): EECS086
      always Warning, the `[Accumulate]` fix extended to a plain numeric struct (a positional record warns without a
      fix); EECS109 Warning only when the walk could be a static `[Drain]` method (Info with its reason otherwise,
      `samples/Actors/Host/Storage.cs`), the indexed `for` and the non-ref `foreach` detected too; EECS055's bool
      reason (code in feat-inner-loops item e). Proof: `analyzer.md` 1b-1d tests, A1 green.
- [x] **A3. Rule: `Reduce<T>.Value` read as the total** (`analyzer.md` 3a; common-mistakes #reduce-value), Warning,
      the accumulation reader shared with Kernel / `Accumulations`. Proof: positives and negatives listed there.
      Done: EECS128 (provisional id). A read flowing back into the same field or the value holding it, a kept
      row's condition and Combine are not reads; `HistogramBenchmark`, BattleServer's `FindWeakest` and the Reduce
      / Fusion / Kernel tests stay clean.
- [x] **A4. Rule: `AddSingleton<T>` on a world that has T** (`analyzer.md` 3b; D58), Error in one block, Warning in
      a hook or `[ForEach]` body, fix to `SetX` / `GetOrAddSingleton`. Proof: tests and a code-fix test.
      Done: EECS129 (provisional id), fix to the generated `GetOrAddX()` (the generic call where none binds).
- [x] **A5. Rule: a raw entity index stored in a component** (`analyzer.md` 3c), Warning, EECS017's sibling.
      Done: EECS130 (provisional id), no fix. WorldSimulation's `Projectile.OwnerId` keeps the int (its ports
      store ints; pragma with the reason), NeighborTests / KernelTests store indexes on purpose (pragmas).
- [x] **A6. Rule: a `Scratch<T>` added to and never cleared** (`finish-scan.md` item 7; threading.mdx:1068 says
      "Code review" today), Warning + a fix inserting `Clear()`; the rule recorded in `audit-analyzer`.
      Done: EECS131 (provisional id) + the fix inserting `Clear()`; recorded in `audit-analyzer`.
- [x] **A7. Locality pitfalls** (docs-todos "Locality phase 0"): `Lookup.Find` from a helper ("not prepared") as a
      rule; EECS036 / CS0315's message for a `[LookupKey]` member named `Key`; a qualified `[ForEach]` call inside its
      type. Build or record each.
      Done: EECS132 (provisional id) for a key probe in another type's method; helpers of the method's own type
      are now followed by the generator (`Symbols.FindsByKey`, three calls deep), so they no longer throw; a
      `[LookupKey]` member named `Key` is no longer EECS036 (it got no index, hence CS0315); the bare call of an
      entry point gets a fix (CS7036 / CS1501 / CS1503 / CS1620).
- [x] **A8. Views follow-ups** (docs-todos "feat-views"): EECS053 through views in lambdas (verify first), EECS025 for
      a never-written view ref field, EECS049 for view locals; EECS049 for a `[WithAny<Q, T>]` whose every type is in
      `[Without<>]`. Build or record each. After feat-inner-loops.
      Done: EECS025 for a never-written view ref field, EECS049 for view locals and for an all-excluded
      `[WithAny]` were already built and tested; EECS053 through a lambda pass's view pinned in ViewTests.
- [x] **A9. EECS021 false positive on a partial static runner** (`analyzer.md` 4): skip the method itself unless its
      entry point is reachable. Files: Symbols.cs. Proof: the snippet, private and public.
      Done: a private method no longer collides with its own entry point; public / internal still EECS021.
- [x] **A10. Build cost: syntax rejection before `GetDeclaredSymbol`** (`analyzer.md` 6) in `AnalyzeVectorLoop` /
      `AnalyzeParallel`; the Tiles, Views, Records, Pairs, Forms and Fusion areas behind `Core.Create`; the two
      `ToDisplayString() ==` comparisons (Performance.cs:308, Tiles.cs:315; `generator.md` "In areas owned") by
      `Symbols.Named`. Proof: GeneratorBenchmark CompileAndAnalyze minus Compile, diagnostics diffed.
      Done: Views / Pairs / Forms / Fusion / Tiles / Records behind `Core.Create`, syntax rejection before
      `GetDeclaredSymbol`, `Symbols.Named` for both display-string compares; numbers in the hand-back.
- [x] **A11. Messages and fixes** (`analyzer.md` 7): EECS118's reasons end with what to write; the drop-the-argument
      fix for EECS035 / EECS118; EECS024's move-to-a-static-method fix. Proof: CodeFixTests per fix.
      Done: EECS118 / EECS035 messages end with "drop X or Y" and get the remove fix; EECS024 gets the move fix
      (also fixed: Captures counted `p.V` and the attribute's name as captures).
- [x] **A12. Thin tests and the suppressor** (`analyzer.md` 8): EECS004, 068, 104, 058, 108, 118, 099 / 100, 057 /
      063 / 070; `UnusedSuppressor` cases for `[Drain]`, hooks, IDE0052, and its justification text.
      Done: the thin rules at their edges (AnalyzerTests.Declarations / .Parts, PartTests, MaxRowsTests),
      `UnusedSuppressor` for `[Drain]`, hooks, IDE0052 and the justification text.
- [x] **A13. Overlap pins** (`analyzer.md` 9) in `AnalyzerTests.OneMistakeIsReportedOnce`: EECS086 / 055, EECS103 /
      055, EECS109 / 092; IDE0060 suppressed on `[ForEach]` / `[EntityMethod]` parameters (EECS026 owns them).
      Done: `AnalyzerTests.OverlappingRulesReportOnce` (EECS086 / 055 twice, EECS109 / 092); EECS103 / 055 was
      pinned in HandWrittenVectorLoopReports; IDE0060 suppressed (EECSS03) and tested in UnusedTests.
- [x] **A14. Smaller candidates** (`analyzer.md` 3, end): "removing a component to pause it", "a flag where a tag
      would do" (overlaps A2's bool split): weigh, then build or record in decisions / owner-review.
      Recorded in `docs/owner-review.md` with the causes (no signal tells a pause from an end; the bool split of
      EECS055 already gives that code its fix, and how often a flag flips is not in the code).
- [x] **A15. Stale instructions** (`analyzer.md` 10, docs-todos S1): `audit-analyzer` SKILL's file list (`.Members`,
      `.Layout`, `.Lambdas`, `.Views`, `.Records`, `.Pairs`, `.Tiles`, `.Fusion`), its range (EECS002-EECS123) and the
      overlap test's name; `src/EasyEcs.Generator/AGENTS.md` 135-136 and root `AGENTS.md`'s range; the
      `GeneratorCorpus.cs` comment; the code comment `EasyEcsAnalyzer.Performance.cs:17` ("the generated member is a
      suggestion"); an `IForSpan` Execute that is one element loop -> "write it as a lambda pass" (inner-loops.md
      section 6): build after WP4 merges or record.
      Done: the skill's file list, range and test names, both AGENTS.md ranges and file-map lines, the
      Performance.cs summary, GeneratorCorpus's comment. The IForSpan rule is recorded in `docs/owner-review.md`
      (its check needs a rewritten-call form of `LambdaPass`, which the vector-loop lane owns).

### Lane 3: tooling and CI (tools/, .github/, docs/scripts, test and benchmark harness, sample hosts) — 19 items

Starts now. T1 comes before Finish 1b (the move).

- [x] **T1. `tools/agents.sh` recursive, and maps for tests and benchmarks** (`code.md` 2.2, 2.3, 3.8):
      `glob("**/*.cs", recursive=True)` before the directory move (otherwise it passes vacuously); `tests/` and
      `benchmarks/` in the maps (a file's base name or its stem without Tests / Benchmark); the missing files listed
      (tests: CopyEntitiesTests, KernelTests, ScratchTests, UnusedTests, WorkersTests; benchmarks: ChunkRowsBenchmark,
      KernelBenchmark, ParallelRunnerBenchmark, ScratchBenchmark, Program.cs, the Comparison project's files); the two
      file maps one line per file. Proof: `tools/agents.sh --check` fails on a missing file, passes after.
      Built (fix-tooling-lane3): the maps of tests/ and benchmarks/ one line per file, every project under them
      checked by its path (`Locality/Chase.cs`), recursive over git's file list; 190 problems on the old maps, 0
      after.
- [x] **T2. GameLoop prints "SystemHandle" for every system** (`code.md` 1.1): `system.Name` in
      `samples/GameLoop/Host/Report.cs:32`; CI checks GameLoop's names and counts (`code.md` 2.5: timing lines
      strippable by `check-sample.sh`). Proof: CI step fails before the fix.
      Built: `system.Name`; CI runs GameLoop through `check-sample.sh` with `SAMPLE_MASK` (numbers masked: names,
      lines and checks compared, README and pages).
- [x] **T3. Raw allocation baselines and fixed warm-ups in tests** (`code.md` 1.2): ConcurrencyTests:178,
      LaneMathTests:488 through `Allocations.Baseline` / `Of` / `WarmUp`; the fixed warm-ups of KernelTests,
      KernelLookupTests, HelperLoopTests, AllocationTests to `Allocations.WarmUp` (KernelTests after
      feat-inner-loops), and the sentence in `tests/AGENTS.md` goes.
      Built but KernelTests (feat-inner-loops edits it; tests/AGENTS.md names it): ConcurrencyTests' baseline
      after the holder waits, LaneMath through WarmUp / Of, AllocationTests' fixed warm-ups through WarmUp;
      KernelLookupTests and HelperLoopTests already used WarmUp.
- [x] **T4. One generator list for tests, DocCheck and GeneratorCorpus** (`code.md` 3.1): DocCheck lacks View and
      Prefab generators and the interceptors feature, GeneratorCorpus lacks Prefab; one shared list, parse options and
      references. Before the docs pass (fences with views and lambda passes). Proof: a DocCheck fence with `[View]`
      and a lambda pass compiles.
      Built: tools/Shared/GeneratorHost.cs (the six generators, parse options with the interceptors feature,
      references) linked into the tests, DocCheck and GeneratorCorpus.
- [x] **T5. CI runs `bun run check`** (`code.md` 2.1): a `docs` job on `docs/**` (setup-bun 1.4.2).
      Built: `.github/workflows/docs.yml` (setup-bun, bun 1.4.2, `bun run check` on docs/**).
- [x] **T6. CI checks the public skills list** (`code.md` 2.4): `npx -y skills add . --list` lists only `easyecs`.
      Built: a `doc-snippets` step (skills@1.7.6, only `easyecs`).
- [x] **T7. VoxelServer's TickBenchmark built by CI** (`code.md` 2.6): in the slnx or the build job; its job from
      `BenchmarkConfig`; a README line.
      Built: in the slnx, on BenchmarkConfig's job (linked, `[InvocationCount(1)]`), a README line.
- [x] **T8. The committed live demo checked for staleness** (`code.md` 2.7): a source hash beside the bundle.
      Built: `docs/scripts/demo-source.sh` (blob ids of Core, the generator, the demo, Directory.Build.props),
      `docs/public/demo/source.sha256` written by build-demo.sh (set to 8082b4c's tree, which built the bundle),
      checked in `doc-snippets`: stale until the next `bun run demo:build`.
- [x] **T9. Tool scripts: one build helper** (`code.md` 3.4): errors printed, `TargetPath` read, `trap rm` in
      `loopbound.sh`; `inlining.sh --no-build` in CI (`code.md` 2.8).
      Built: `tools/build.sh` (errors, exit 2, TargetPath, --no-build) for inlining.sh / jitdisasm.sh /
      loopbound.sh, `trap rm` of the listing, CI `tools/perf/inlining.sh --no-build`.
- [x] **T10. One reflective BenchmarkDotNet runner for tools/perf/Inlining and tools/perf/LoopBound** (`code.md` 3.3): short
      or full class names, every `[Params]` / `[ParamsSource]` type.
      Built: tools/Shared/BenchmarkRunner.cs (Find: X, XBenchmark or the full name; [Params] / [ParamsSource] of
      any type; Create / Cleanup).
- [x] **T11. `tools/perf/bench.sh` resolves a class's project by listing** (`code.md` 3.6), and fails on "No benchmarks
      to choose from" instead of marking it done.
      Built: each project lists its benchmarks once; a class runs where it is declared, a name neither declares
      fails, "No benchmarks to choose from" is a failure.
- [x] **T12. `check-sample.sh`'s two copies of the block extraction** (`code.md` 3.7): one function; the README
      heading convention unified with the page's in the docs pass.
      Built: one `block` function (the heading regex through the environment); the heading convention is in docs-todos.
- [x] **T13. Stale comments and maps** (`code.md` 3.5, 5): `tools/perf/inlining.sh:4`'s class names; `docs/AGENTS.md`'s
      scripts line (`lint-terms.ts`, `chromium.ts`, `after-build.ts`, `first-commit-dates.mjs`,
      `search-expectations.json`); `lint-terms.ts`'s empty `pending` (delete or say why); `benchmarks/AGENTS.md`: a
      benchmark nests its components, only the headline worlds declare at namespace level.
      Built: inlining.sh's names, docs/AGENTS.md's scripts line, lint-terms' empty `pending` deleted,
      benchmarks/AGENTS.md's nesting rule.
- [x] **T14. The `Locality/` prototypes** (docs-todos "Locality phase 0", `locality.md` status; borrowed-ideas keeps
      them for the owner's reruns): delete the shipped prototypes and their link into the unit tests, keep the plain
      baselines the owner's runs need, or record why each stays.
      Decided: every file stays, with its reason in locality.md's status (worlds of shipped-form tests and
      benchmarks, baselines, ARM64 evidence); Timers.cs goes with the timer wheel's record (docs-todos).
- [x] **T15. Benchmark rows**: the EECS096 delegate fallback in LambdaPassBenchmark (docs-todos "feat-inline").
      Done: row `VectorLambdaDelegate` (0 B, EECS096 verified to fire without the pragma); the bench.json merge was refused (other machine than the site's numbers), so the site data is unchanged.
- [ ] **T16. The arm64 job's first run** is the owner's dispatch (Finish 4; `owner-m4.md` 6).
- [x] **T17. The owner's working rules in `AGENTS.md`** (ledger P10, P11; owner 2026-10-27 / 2026-10-30: "用中文对话";
      2026-10-28: "需要我选的给我弹窗，需要我做的给我命令"): a bullet in Workflows: talk to the owner in Chinese (code,
      comments, commits and docs stay English); a choice for the owner is asked as a popup, an action only the owner can take
      is given as the exact command. Proof: `tools/agents.sh --check`.
- [x] **T18. `docs/AGENTS.md`'s Cloudflare table** (ledger L7): the build watch paths include `docs/*` and exclude
      `docs/plans/*` and `docs/owner-review.md` (set by the owner in the dashboard, 2026-11-07).
- [x] **T19. SDK 10.0.403** (NU1903: GHSA-p3jp-9v83-76w9 in `Microsoft.DotNet.ILCompiler` 10.0.13 of SDK 10.0.402
      fails `tests/EasyEcs.Aot`'s restore): every workflow's `setup-dotnet` asks `10.0.x`, which installs the
      channel's latest SDK (10.0.403 since its release); `global.json` now asks for 10.0.403 with `latestFeature`, so an
      older SDK fails at once instead of at the AOT restore. Proof: the restore fails on 10.0.402, passes on 10.0.403.

### Lane 4: vector-loop generator (after feat-inner-loops merges; Kernel*.cs, SystemGenerator*.cs, Recorder.cs, LaneMath.cs, ArityGenerator.cs text) — 18 items

- [x] **G1. `++` / `--` as a value** (`generator.md` 1): `Expr` lowers it, masked by `_active`; Struct and Pairs paths
      too. Proof: StructuralLoop / Kernel tests (prefix, postfix, `&&` / `||`, early return, narrow lanes), a
      KernelBenchmark Expire case, BattleServer and WorldSimulation (their Burn / Thaw / Expire / Fight / Recover).
      Done (feat-lane4-generator 7101f0d, tests 9a933ed, benchmarks 8e8e51c + 5a40981): KernelBenchmark Expire 668 -> 160 ns at 1k rows, 73.5 -> 19.7 us at 100k; Cool (a step beside a square root) 4.0x its scalar method; 0 B.
- [x] **G2. A structural change of the method's own component, nothing reading it after** (`generator.md` 2):
      allowed; the `audit-vector-loops` rule narrowed. Proof: Remove / Disable / Set against the scalar twin, D122's
      order, the two headline worlds.
      Done (7101f0d, 9a933ed): UsedAfter; BattleServer `Burn` gets its loop, `Thaw` is `SimdMode.Off` (three changes). Worlds, head / base: WorldSimulation 10k 108.6 / 113.7 us, 100k 1,195 / 1,338 us; BattleServer 3.85 / 4.22 ms; 0 B.
- [x] **G3. A tag as a parameter** (`generator.md` 3): `in Tag` is a filter with no lanes, `Maybe<Tag>.HasValue` the
      presence mask. Proof: KernelTests, AccessBenchmark's two cases.
      Done (7101f0d, 9a933ed, 8e8e51c): AccessBenchmark TagParameterRow 39.0 -> 10.5 us (= WithTagAttribute 10.1), MaybeParameterRow 83.3 -> 10.3 us at 100k; 0 B.
- [x] **G4. A `ref` singleton written only as a running sum, count, Min or Max** (`generator.md` 4): the element
      loops' reduction core, folded into the singleton's field. Proof: KernelTests (non-zero start, bail), a
      KernelBenchmark sum case.
      Done (7101f0d, 9a933ed, 8e8e51c): KernelBenchmark Survey 5,512 -> 258 ns at 1k, 712 -> 11.6 us at 100k; 0 B.
- [x] **G5. `EntityRandom` draws as lanes** (`generator.md` 5): `Bits` / `Float` / `Chance` / `Range(float)` as 8-byte
      lanes, gated per width; the reason text fixed now and the `audit-vector-loops` line corrected. Proof:
      EntityRandomTests bit for bit, EntityRandomBenchmark at three widths; ARM64 "owner, after code".
      Done in feat-lane4-rest (427767e, tests 1acd152, the width gate e0646bb / 1887b05): RandomLanes, Kernel.Random.cs. EntityRandomBenchmark PassEntityRandom at 100k, head / base (42b8e9d): AVX-512 93.3 / 234.2 us, AVX2 195.0 / 244.1 us, 0 B. Under SSE alone the lanes were 393.3 / 315.2 us (two 8-byte lanes of emulated multiplies), so a loop with draws needs vectors of 256 bits (RandomLanes.IsAccelerated; ARM64's 128 bits run the rows, "owner, after code" confirms on the M4): with the gate (e0646bb) against 42b8e9d, SSE alone 275.5 / 271.8 against 275.8 / 350.0 us (the rows, as the base), AVX2 177.9 / 239.5 us, 0 B; AVX-512 after the gate (f133c34 against 42b8e9d, two rounds, fix-lane4-followups) 84.5 / 113.8 against 265.1 / 246.0 us, PassRandomShared 136.1 / 166.2 against 148.8 / 174.8 us, the reference passes (SplitMix, Philox7 / 10, RandomShared / Seeded, which no generated draw touches) within 1-6 %, 0 B. On AVX-512 the Vector<T> loop of a masked word calls RandomLanes.Float / Range once (the root's inline budget), at most one vector per masked run; the whole-word loops inline every draw.
- [x] **G6. Reason texts** (`generator.md` 6): an `Entity` receiver call, `DynamicBuffer<T>` parameters, narrow
      division by a constant (build the power-of-two form), a `Tile<,>` parameter ("write it as a cell method"), the
      tile cursor's indexer read at the loop's counter (Settle's loops, `generator.md` "In areas owned"; after
      feat-later-halos: the reason now, then the element-loop form for tile cursors built or recorded). Proof: reason
      pins, ElementLoopTests for the cursor form.
      Done (7101f0d reasons; 43132be + 87160d0 the tile loop): a loop reading a tile's cells at its index names the cell method and the whole-vector loads. The element-loop form for tile cursors is not built: docs/owner-review.md "An element-loop form for a tile cursor's indexer" (cell methods and EECS103 v2 cover it).
- [x] **G7. Math with no vector form called per lane inside the loop** (`generator.md` 7): `LaneMath.PerLane.*`,
      gated by operations against calls; D127's Considered. Coordinate with feat-inner-loops' item g (Atan2 / Asin /
      Pow lowerings). Proof: KernelBenchmark Atan2 / Sin bodies, a light body that must not gate on.
      Done in feat-lane4-rest (429a262, tests 1acd152, KernelBenchmark pairs d6983b1): LaneMath.PerLane (the scalar function per lane), Kernel.PerLane.cs with the gate PerLaneWork = 8 vector operations per call (SimdMode.Require opens it). KernelBenchmark at 100k, generated / scalar: Bearing (Atan2 in a heavy body) 4,163 / 5,305 us, Sway (Sin, no FloatMode.Fast) 1,340 / 1,639 us; Aim (Atan2 alone, below the gate) stays scalar: forced 3,701 / scalar 3,235 us, which is why the gate exists; 0 B. Under DOTNET_EnableAVX512=0 the Vector256 loop keeps the other of two NaNs of different signs in DoubleAngles at an infinite input (operand order), so that test compares NaN as NaN (1887b05).
- [x] **G8. P2: a uniform table read at a lane index** (`perf.md` P2): a permute (`vpermps`, AVX2 `PermuteVar8x32`,
      NEON TBL) for N <= 16, a gather past it; BattleServer's Steer back to `SimdMode.Auto`. Proof: KernelBenchmark
      SteerTable pair, BattleServerBenchmark, tools/perf/loopbound.sh; ARM64 "owner, after code".
      Done in feat-lane4-rest (4b066b2, tests 1acd152, benchmarks d6983b1): TableLanes (VPERMD over 16 / 8 values, Vector128.Shuffle per 4), Kernel.Tables.cs; BattleServer's Steer back to Auto, Afflict SimdMode.Off. KernelBenchmark SteerTable generated / scalar: 74.4 / 331 us at 100k, 1.30 / 3.48 us at 1k; BattleServerBenchmark Tick 3.334 / 3.784 ms (base 42b8e9d); 0 B. loopbound.sh not run on this branch. ARM64 "owner, after code".
- [x] **G9. `[MinBy]` / `[MaxBy]`'s vector form** (`finish-scan.md` item 5), after feat-generated-members: proved with
      ReduceBenchmark (FindWeakest), ties keep the earlier row.
      Done (7101f0d, ReduceTests 9a933ed, ReduceBenchmark 8e8e51c): LowestVector 507 -> 282 ns at 1k, 5.87 -> 2.87 us at 10k, 38.3 -> 8.3 us at 100k, 353 -> 59.5 us at 1M; 0 B. Parts.KeptRowReason deleted (064ebf8).
- [x] **G10. The ARM gate's rejected `Brake` shape: a benchmark pair forced open** (backlog history, ARM64 parity),
      then "owner, after code" (`owner-m4.md` 4). Files: KernelBenchmark.cs, Kernel.Wide.cs's gate.
      Done (43132be: SimdMode.Require opens every wide form; 87160d0; 8d4a9d3: BrakeScalar / BrakeGenerated / BrakeForced). On x64 BrakeForced = BrakeGenerated (AVX-512 opens it anyway); the ARM64 run is owner-m4.md 4, "owner, after code".
- [x] **G11. Incremental rules** (`generator.md` 9): Recorder's SemanticModel per member (9a, use the transform's
      model), `Symbols.ModelOf` cached (9b), `ClosedSystems` measured before any change (9c). Proof: GeneratorBenchmark
      EditMethodBody / Ide.
      9a and 9b done (7101f0d: Recorder and Symbols reuse the transform's SemanticModel); 9c (feat-lane4-rest): measured, kept as it is. GeneratorBenchmark with the stage against the stage off (its predicate returning false; the corpus names no closed generic system, so both produce the same output), BenchmarkDotNet, three runs each, OneNamespace = false / true: EditMethodBody 506-626 / 520-589 ms against 475-595 / 492-580 ms, EditMethodBodyIde 90-136 / 79-85 against 86-95 / 83-107 ms, EditUnrelatedFile 543-637 / 451-585 against 470-539 / 466-524 ms, the two variants and the runs disagreeing on the sign; allocated the same to 0.15 MB (EditMethodBody 92.90-93.04 against 92.89-92.94 MB). The stage's cost is below the benchmark's noise and allocates nothing measurable: its predicate is a type test per node of the edited tree, and its transform binds only a generic name inside a system API's type arguments.
- [x] **G12. Dead code** (`generator.md` 11): `PairForm.WritesNeighbor`.
      Done (7101f0d).
- [x] **G13. Generated text cleanup, step 1: the `GeneratedTextTests` guard and output bytes in GeneratorBenchmark**
      (`generated-text.md` 8; `generator.md` 10).
      Done (GeneratedTextTests 9a933ed, the bytes column 8e8e51c).
- [x] **G14. Step 2: the file-level alias block** (38,597 `global::`; IL identical).
      Done (7101f0d; budgets in GeneratedTextTests).
- [x] **G15. Step 3: fusion files reference the systems' jobs** (-76 % fusion text) — after feat-inner-loops' lambda
      fusion.
      Done in feat-lane4-rest (c695e1e, tests 1acd152): a non-generic system's jobs live in `__{System}_Jobs` (aliased `__ForEach`), fusion files reference them. Generated text of the GeneratedTextTests corpora 12,875,070 -> 12,310,502 bytes (with G17 and G18); FusionBenchmark / LambdaFusionBenchmark level over two rounds against 42b8e9d, 0 B.
- [x] **G16. Step 4: printer fixes** (404 `((0))`, names, Arity indentation; IL identical) **and empty hooks as
      `ISystemCode` defaults** (about 400 `{ }`; SystemCode.cs).
      Done (7101f0d: literals parenthesized only when negative, ISystemCode Create / Destroy as static virtual defaults).
- [x] **G17. Step 5: one Tier1-gated round of IL-changing cleanups** (`__d0 = Zero` marks, hoisted scalar setup,
      `this.v0`, pair broadcasts, struct-local aliases, dead narrow temps, unused masks, `ids, 0`, discards for unused
      fields, 50 identity copies) with #172's rest (one body per width where Tier1 is equal) and the WP0b follow-ups
      (GeneratorBenchmark A/B, a locals check over the samples, `.Zero` / `.One` text); the D126 `__s -> __c` copy
      stays, commented. Proof: Tier1 of the six runners, PGO off, else BenchmarkDotNet.
      Done in feat-lane4-rest (361fbeb): the structural change's mark is declared once (`var __d0 = ...` instead of Zero and one `|=`; marks zeroed then OR-ed 1,308 -> 0 in GeneratedTextTests). StructuralLoopBenchmark level or better against 42b8e9d (Vector 100k at 1 % 9.06 / 11.17 us, Lambda 100k at 50 % 33.6 / 37.5 us), 0 B. The rest of the list: docs/owner-review.md "The rest of the generated-text cleanups (G17)".
- [x] **G18. Generated members' text** (`generated-text.md`, small): ComponentGenerator's AggressiveInlining text,
      doc comments per arity (`inheritdoc`), the view's `TryGetX` double lookup.
      Done in feat-lane4-rest (019e739): ComponentGenerator's members inside the alias block (global:: 101,440 -> 85,849 over the corpora with G15), the view's TryGetX one lookup through Entity.RefOrNull<T> (hidden). ViewBenchmark level against 42b8e9d over two rounds (Refs 24.5 / 26.5 against 27.2 / 27.4 us), 0 B. Doc comments per arity as inheritdoc: docs/owner-review.md.
- [x] **G19. A bare `[StructLayout(LayoutKind.Sequential)]` keeps the vector loop** (`interop.md` 1, finding): today
      any `[StructLayout]` makes a component's method scalar ("sets its layout"); `Sequential` with no `Pack` and no
      `Size` is the default layout of a C# struct, so treat it as none. Proof: a GeneratorTests case and the
      vector-loop test for it; `Explicit`, `Pack` and `Size` keep the current reason.
      Done (7101f0d, 9a933ed).
- [x] **L4-M. A masked pass's row order** (found by feat-lane4-generator): a run of whole words reached the job
      after the masked word that followed it (MaskedRuns), so Emit<T> records and marked rows left table order. Fixed
      in 64f61ff, the run out of line in f3e8118 (LateHolesBenchmark Step level with base, KernelBenchmark Holes /
      Halves level or faster); StructuralLoopTests.AMaskedWordAfterARunKeepsTheRowOrder fails without it.
- [x] **L4-I. inlining.sh red in CI: `ParallelHeavyJob11::__MaskedLoop` "inline exceeds budget"** (found 2026-11-08,
      fixed in feat-lane4-rest 42b8e9d): a parallel masked pass inlined MaskedRuns into its chunk's Run, and the JIT
      sizes the budget from the root's IL; MaskedRuns is now NoInlining (one call per table or chunk). inlining.sh
      exits 0; KernelBenchmark.RigHolesGenerated head / base (2025276), two rounds: 932 / 968 against 978 / 963 ns at
      1k, 141.9 / 147.9 against 153.7 / 152.6 us at 100k; HalvesGenerated and VectorFormsBenchmark level; 0 B.
- [x] **L4-W. Masked words of wide rows** (owner-review, unblocked 2026-11-08 by feat-later-halos): c86d827, tests
      767317a, benchmark 6327459. KernelBenchmark.RigHoles (every third row without Mass), four rounds, head / base: 0.61 / 0.54 / 0.69 /
      0.63 at 1k rows, 0.93 / 0.92 / 0.84 / 0.90 at 100k; 0 B. Steer, Glide, Dive, Brake (no holes) within noise
      (0.94-1.09 both ways): their whole-word Execute is the same IL (the diff of the generated file only adds the
      masked loops and their calls). WorldSimulation 1.06 / 0.97 / 0.95 / 0.96 (10k, 100k, two rounds); BattleServer
      1.03 / 1.08 with its generated code and the core identical on both sides (the second side's drift).
- [x] **L4-A15. EECS133: an IForSpan job that is one element loop** (owner-review, backlog A15, unblocked
      2026-11-08): LambdaPass.FromJob (the rewrite checked in a copy of the compilation), the Warning and its fix,
      083f368; tests 767317a. Docs: docs-todos "feat-lane4-generator".

### Lane 5: renames (after feat-later-halos and feat-generated-members merge; owner, 2026-11-07) — 6 items

- [x] **R1. `tile.Restart(value)` -> `tile.ColdStart(value)`** (`naming.md` B2; `tile.Sweep` stays): Tile.cs,
      EECS115, the generator, VoxelServer, tests, benchmarks (TileRestartBenchmark -> TileColdStartBenchmark, site keys
      with it), ApiDocs zh `tile.xml`.
- [x] **R2. The shape `Columns<TTile>` -> `Ground<TTile>`, the column walk `TileColumnWalk<…>` -> `TileColumns<…>`**
      (`c.Columns(step)` stays; `naming.md` B2): Tile.cs, the generator's tile-load interceptor, tests, samples,
      ApiDocs zh.
- [x] **R3. `World.MaxChunkRows` -> `World.MaxChunkSize`** (`naming.md` B3, A1), with a code fix on CS0117 from
      the released `World.ParallelChunkRows` (the first released rename: the renamed-member mechanism stays for it).
- [x] **R4. Delete the code fixes for names that never shipped** (`naming.md` B4): `RenamedAttributes`
      (`[IndexKey]` -> `[LookupKey]`) and `RenamedArguments` (`ChunkRows` -> `ChunkSize`) in
      EasyEcsCodeFixes.Declarations.cs and their CodeFixTests; owner (2026-11-07): also the other migrations from
      forms that never shipped (the hook attributes, `[GroupBy]` on the struct, EntityRandom's tick written by hand),
      keeping only the fixes from released forms (`WithAny<T>`, the class systems, `ParallelChunkRows`); AGENTS.md's
      CodeFixes line follows; D129 amended in the docs pass (docs-todos S3).
- [x] **R5. Internal and hidden names** (`naming.md` A3-A5, approved as "clearly right"): `BadRecordPass` /
      `HandWrittenRecordPass` / `AnalyzeRecordPass` / `AnalyzeHandWrittenRecordPass` -> `BadDrain` /
      `HandWrittenDrain` / `AnalyzeDrain` / `AnalyzeHandWrittenDrain`; tracking names `RecordMethods` / `RecordBodies` /
      `RecordCode` -> `Drain*` (GeneratorTests' list and the generator's AGENTS.md); the fix `RecordMethod` ->
      `DrainMethod`; the `DrainTests` / `EmitTests` names; `Emitted<T>.Consume` -> `Drain`; `TileSteps.Sweep(Entity)` ->
      `NextSweep`; `TileRows.First` / `Last` -> `FirstLayer` / `LastLayer`; `CellRow` -> `PlaneLoads`; `TilePlane.Own` ->
      `RowBeside`; `TileCells.Pool` -> `SizePool`; `CellRows.cs` -> `TilePlane.cs`; `ChunkRowsBenchmark` ->
      `ChunkSizeBenchmark` and `GhostCopyBenchmark` -> `CopyEntitiesBenchmark` (site keys and bench.json mapping
      kept). Run the `StartsWith` / `EndsWith` grep of `naming.md` first.
- [x] **R6. `audit-naming` step 4 amended**: "a `ForEach*` name is a pass over rows, except `ForEachCollected`, the
      collected form of a `[ForEach]` method" (`naming.md` B1). Not approved and kept: `{Owner}ForEach` (not
      `{Owner}Passes`) and `CopyEntities<TTag>` (not `CopyEntitiesWith`); their reasons go into the naming record
      (docs-todos).

### Lane 6: interop, `EasyEcs.Native` (owner, 2026-11-07, ledger I1-I5; `interop.md`) — 6 items

After Lane 1's C19 (mimalloc columns: pointer lifetime and retired blocks) and Lane 4's G19 (a bare
`[StructLayout(Sequential)]` keeps the vector loop). New projects only (`src/EasyEcs.Native`, its tests, a sample), so it
runs beside any lane once those two merged.

- [ ] **N1. Fixed world ABI** (`interop.md` WP1): `easyecs_abi_version`, world create/destroy, update, update group,
      count, destroy, is-alive, `easyecs_last_error`; generated into the user's root assembly; every export catches.
      Proof: tests call the exports through `NativeLibrary`; CI publishes the AOT library and runs a C smoke test beside
      `tests/EasyEcs.Aot`.
- [ ] **N2. Per-component exports** (WP2): spawn from arrays, get/set by id, singletons, runs of rows for declared
      queries, bulk copy in and out; analyzer rules for components that cannot be exported (managed fields,
      `Explicit` layout). Proof: a generator caching test; `InteropBenchmark` (BenchmarkDotNet: per call, per run,
      bulk, against the in-process update).
- [ ] **N3. Header and bindings writer** (WP3): an MSBuild target writing `easyecs.g.h` (`_Static_assert` of sizes and
      offsets), the Rust `-sys` crate plus a safe wrapper (`Send`, not `Sync`), the Node module (koffi, `.d.ts`), the
      Go package (cgo). Proof: a smoke test per language in CI.
- [ ] **N4. Host systems** (WP4): `World.ForEachRuns` over a run-time `ComponentSet` with an access mask, placed in the
      schedule, run serially; the structural gate's throw inside a callback caught. Proof: tests; a fragmented-archetype
      benchmark of the per-run cost.
- [ ] **N5. Packaging**: the `EasyEcs.Native` NuGet package; the crates.io, npm and Go module publishing workflow (CI
      commits separate; publishing itself is the owner's release step, like NuGet).
- [ ] **N6. Docs to-dos** (WP6, for the docs pass): `guide/interop.mdx` + zh, `samples/Interop` (the cleaned-up
      prototype), README, the public skill, decision records I1-I5 and the `interop.md` records list.

## Finish (strictly in order; nothing of a later step starts before the earlier one is done)

Starts when every lane above has merged (owner, 2026-10-27: the refactor before docs and benchmarks, so docs and
numbers are written once about the final code).

- [x] **1. Directory restructure** (owner, 2026-11-08, L5, L6, L9-L12; D142; branch `feat-restructure`): the four
      projects in folders by concern (Core, Generator, the unit tests, the benchmarks; namespaces unchanged, a
      `.DotSettings` each), `tools/perf/` and `tools/docs/`, `docs/plans/designs/` and `docs/plans/owner/`, the guide
      in folders, `tools/agents.sh` reading every depth and failing on a map line naming a missing file,
      `docs/vite.config.ts` recursive. Left for the owner: `docs/scripts/build-demo.sh` (the bundle's
      `source.sha256` hashes the sources' paths, so it changed with the move and was stale before it) and the
      Cloudflare build-watch exclude of `docs/plans/*`.
- [ ] **2. The exemplar pass** (#169, `audit-code`; owner, 2026-10-27): every piece of code users read or copy in the
      most fitting form, new features included. Proof: outputs and README / page output blocks match, 0 B steady
      state, tests three ways, A/B no slower.
      a. **VoxelServer** (steps 1-3 and finish-scan 3 merged 2026-11-17 from exemplar-voxel; step 4 waits on
         research-pair-filtered-rows; steps 5 and 6 done on research-voxel-pingpong (water and scent cell forms, Q6 /
         WP5b, not tried); step 8: Terrain as a cell method not tried) (`voxel-rewrite.md` steps 1-6, 8, 9, owner answers in its section 9): bool splits
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
      c. **Benchmark worlds** (branch exemplar-worlds, 2026-11-17): BattleServer's `Battle` `[Accumulate]` (EECS086 exception
         already gone from GeneratorCorpus), `Weakest` `[MinBy]` and the kill feed were done before; now `KillFeedSystem`
         a `[Drain]` method, `Grunt` a `[Prefab]` type (Brute its variant, Hero a builder: a declaration holds no buffer),
         `CombatSystem` takes a `Fighter` `[View]`, `NearestPlayer` a runner over a `[MinBy] Nearest`, `NewRound`'s
         heal a lambda pass; regions kept, `kinds` added; `Reports.See` (`[Neighbor]`) behind `Sightings` and `LootValue` as an
         `[EntityMethod]` came back with fix-generated-dead-lines. Not done: `PlanningSystem.Plan` stays a `Mut<Goal>` with
         `SetIfChanged` (a `ref Goal` stamps every goal and replicates all of them; the page teaches Mut there, and the
         unrolled argmin needs a plain ref); WorldSimulation unchanged (no analyzer finding at any EECS id; its `Rng` and
         totals are the rules the Comparison ports share). A/B in the branch's report.
      d. Unit tests that show usage (not the ones pinning an old form on purpose).
      e. Every code example of the docs (fences en + zh, the guides' full examples, the public skill, README),
         DocCheck at 0; L4 of `locality.md` (ZombieTarget, NearestPlayer, AOI).
- [x] **2b.** merged 2026-11-16 (exemplar-samples): the nine items of 2.b, MultiWorld's ghosts through
      `CopyEntities<Ghost>`, the template's wave in a pooled array (an Entity cannot be stackalloc'd), EECS092 counting
      a `[Drain]` as the clear; the demo bundle is stale until the docs pass rebuilds it. A2 still waits on 2a / 2c.
- [ ] **3. Docs pass** (#141, `audit-docs`): everything in `docs-todos.md` ("Sweep 2026-11-07" first), with the
      analyzer page's guard test (docs-todos: every id anchored, severities equal to the descriptors') committed with
      the page; DocCheck and ApiDocs at 0.
- [ ] **4.** The `arm64` CI job's first run (owner's dispatch, `owner-m4.md` 6): a wrong runner label fails at once
      and is fixed then.
- [ ] **5.** "Owner, after code": the runs of `owner-m4.md` (1-5; the x86 prefetch run is not made, owner 2026-11-07: the gates' decision
      record says they were proven on this VM's 260 MiB LLC only and that a 32 MiB-LLC x86 run is what would move them), each result
      recorded where the file says.
- [ ] **6.** `tools/perf/bench.sh --site --sync` on the final code (perf's stale rows re-read, the classes missing from
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
