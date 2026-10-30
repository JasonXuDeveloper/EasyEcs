# Docs to-dos collected while the code changes (for the docs pass, #141)

Order agreed with the owner: code first, docs checked and updated in full once at the end.

## Code (running)

- [x] #151 (merged 290b3d4) BattleServer.cs (Reduce, EntityRandom, Emit, Scratch, a SimdMode.Require method; MovementSystem.Move as a
      generated vector loop, A/B vs hand-written; WorldSimulation AoiSystem stats.CellChanges -> Reduce if not slower)
      and the samples / templates / demo / Godot (Vector2) in 4.8 forms. Agent in a worktree; it reports docs to-dos.
- [x] #142 / #137 (merged c642c0a) VoxelServer refactor per voxel-refactor.md, zero steady-state allocation. Agent in a worktree; it
      reports what the TODO(water overlap) / TODO(mapping) markers and the voxel-server page must say.

## Performance checks

- [x] ab7 (two rounds; the last nofree round cut by the 2 h limit): Group.Index -4 %, EntityBatch +1 % (the full run's
      -46 % / -43 % were noise), ColdCreate -7..-15 % time but +0.38 MB per reserved world (the free id list, by design:
      say so in docs); dc2785c reverted (36abe0b: without it Reserved 2.22 vs 2.32/2.35 ms, PerEntity 4.70 vs 5.30/5.65).
      Iterate1 -51 %, Iterate2 -75 %, Iterate3 -28 %, With -53 %, BattleServer -10 %.
- [ ] Recheck on a quiet machine (final run): GroupBenchmark.Scan (+12 %, round 2 bimodal), OperationBenchmark Toggle (+7 %).
- [ ] Real regressions: root cause, fix, BDN proof.
- [ ] #153 job tuning A/B (Warmup 6 -> 3, IterationTime 250 -> 150 ms) on WorldSimulation, BattleServer, one micro;
      GeneratorBenchmark gets a lighter job of its own (its GlobalSetup generates and analyzes everything per launch,
      each op is ~0.5 s).

## Final pass (after everything above)

- [ ] Full docs audit (audit-docs): every page en/zh against the final code; the agents' docs to-do lists; BenchTables
      for Consume / EntityMethod / SystemDispatch; search-expectations.json (EntityMethod, Consume, entity.Run);
      high-performance "What it costs" section; grids "generator writes" tabs against real output; VoxelServer TODOs;
      example.mdx walkthrough vs BattleServer.cs regions; changelog / README What's new / migration complete.
- [ ] `tools/bench.sh --site --sync` on the final code (classes missing from bench.json: Consume, EntityMethod,
      SystemDispatch, Access, Kernel, ChangeTrackingWrite, DestroyLog, Slices, Trim); check the moved list.
- [ ] README numbers: headline (1.72 ms -> about 1.5 ms), the 4-row table, the comparison table; tell the owner to
      update the GitHub description.
- [ ] verify skill; `git fetch origin master && git merge-base --is-ancestor origin/master HEAD`; push master.
- [ ] Owner cuts 4.8.0 (Actions -> Release on master). Never triggered by us.

## Also in 4.8 (owner, 2026-10-23)

- [ ] #138 research (SpanAction-style span runs, stencil SIMD gaps, tick time): agent; report in
      scratchpad/research138.md, builds only proven wins.
- [ ] Vector loops over mixed lane widths (int rows into a long total, widen/narrow): agent, with EECS061 below.
- [ ] EECS061: an explicit `Merges => false` is reported (constant value read).

## Done this round (for the record)

Systems as [EcsSystem] ref structs (sets, switch dispatch, SystemHandle, EECS074-077, code fixes, suppressor,
generic systems across assemblies for AOT), OneQuery (dispatch -34%), owner-thread gate, runner padding, Lock/CAS,
filter-less passes, Mut column, EntityMethod filter, tile neighbors, attribute reductions vectorize, column loads,
EECS089 / EECS060-061, BlockTicks atomic OR only when shared (QueryTick -18%), RS1030, IlcInstructionSet native,
UnsafeAccessor + PGO checked (inlined with guarded devirtualization; the C4 wrapper reverted).
Generator A/B: EditMethodBody 421 -> 227 ms, 39 -> 28 MB; EECS086-089 no measurable cost.
ab5 tip vs master: ParallelSystems -61..-75 %, ChangeTracking.Tick -45..-69 %, Sparse.PassOverTheRest -66 %,
WorldSimulation -14 %, MultiWorld -6 %, BattleServer level, all 0 B.

## #138 merge gate (2026-10-24)
- ab8 + ab9 (t138tip e221fee vs c642c0a; loaded machine, #156 agent outside the lock): BattleServer -3 %, MultiWorld -1 %,
  ParallelSystems(Parallel) -12 %, dispatch Plain -11..-22 %, WorldSim level. ParallelForEach Parallel=true +17..+26 %
  on best runs (705 vs 821 us): the runner's wide path is unchanged, so suspect noise. Rerun ParallelForEach +
  ParallelSystems alone once #156 finishes, before merging e221fee.

## Merge notes (agents started before e7a47e6)
- The rules file's source is now skills/easyecs/SKILL.md. An agent branch that edits docs/public/easyecs-rules.md
  (the analyzer agent: AI migration guide) -> port its edit into SKILL.md, then `bun run sync:rules`.
- Agents' new rules/features -> add to SKILL.md in the docs pass.

## Folder restructure (after every agent branch is merged, before the docs pass)
Core (71 flat files) -> Entities/ Storage/ Queries/ Systems/ Parallel/ Indexes/ Relations/ Persistence/ (namespace stays EasyEcs).
Generator (28) -> Generators/ Kernel/ Analyzer/ Shared/. git mv only, one commit, no code change; update the
nested CLAUDE.md maps into sections per folder, tools/filemaps.sh to recurse, csproj globs unchanged.
Agents' CLAUDE.md layout edits: port into src/*/CLAUDE.md maps (the root no longer lists files).
- Agents' branches edit CLAUDE.md (now the one line @AGENTS.md): on merge take ours for CLAUDE.md files and port their
  edits into AGENTS.md / the directory AGENTS.md file maps; run tools/filemaps.sh.

## Declaration surface (declarations.md) — mine at merge, no approval needed
- P7: renumber EECS090+ across feat-analyzer / feat-inline / feat-views in merge order.
- P10: [EditorBrowsable(Never)] on non-generic IIndexed / IUniqueIndexed.
- P11: cheat sheet `class S<T> : EcsSystem` (stale since D102); D12 Considered text vs D101.
- Placeholders LAMBDA_NUMBERS / NUMBERS_D109 must be real numbers before merge.
- P8 (inline lambda passes refuse [Changed<T>]) + one cheat-sheet rule for the four ways to write a pass outside a system.
- P9 ChunkSize inference: only after feat-sched autotuner tries < 1024 rows, with benchmarks.
- Owner decisions pending: P1 WithAny<A,B>, P2 hooks by name, P3 ReadOnlyMaybe param, P4 [GroupBy] on member, P6 [Archetype] name/Prefab, EntityRandom.Tick advanced by Update.

## Renumbering at merge (naming audit, sent to agents)
Merge order: declarations, analyzer, hugepages, cow-buffers, sched, structural-simd, fusion, inline, views.
EECS: declarations 090; analyzer 091-092; fusion 093-094; inline 095-097; views 098-102 (range EECS002-102).
D: views 109-111; inline 112-114; declarations 115-117; analyzer 118-120; fusion 121; structural-simd 122; hugepages 123; cow-buffers 124; sched 125.
ScalarReason(Text, IsFixable, SplitComponent) owned by feat-analyzer; structural-simd adopts it at merge.
feat-analyzer rules text -> skills/easyecs/SKILL.md (docs to-do).
Kept names (decided): AsX/TryGetX (D109), FusionMode (mirrors SimdMode).

## Owner request 2026-10-24 (after restart)
- Fewer concurrent subagents (3 at a time). Resumed: declarations, analyzer, hugepages. Next wave: cow-buffers, sched, structural-simd; then fusion, inline, views.
- Wide rows, mixed types, one field written (int,int,long,long,long; write a long): Kernel.Wide does it today for 4-byte lanes on AVX-512 only (D90).
  Gaps: 8-byte (and 2/1-byte) lanes in wide rows; AVX2 path (vpermpd/vpermps single-source + blend, not two-source); rows > 64 bytes.
  Prototype: scratchpad/widelanes (Scalar / RowPerVector (the literal idea: 1 useful lane) / Avx2Transpose / Avx2Gather / Avx512Permute / Split). Build after structural-simd merges (Kernel owner), gated by Gate's cost model.
- Cell kernels (shader form for Tile<,>): user writes a per-cell method (reads neighbors by offset, writes its cell); the generator writes Pad, the Rows walk and the vector loop over the row's cells (neighbor reads = unaligned loads at offsets). Replaces hand-written Load128 in grids.mdx examples. Design + build after Kernel branches merge; docs: grids page You write / The generator writes tab.
- Docs to-dos: grids.mdx (+zh) "Before you start": tile, halo, ghost cells, shader, lanes, masks with mermaid; every hand-written SIMD example rewritten to the plain form where the generator vectorizes it; README <details> "What the generator writes" under each headline example (rule exists in audit-docs; README lacks it today).
- Scheduling (owner, again): at most 2 agents writing code at once; benchmarks serialize on the lock. Now: declarations, analyzer (+ hugepages finishing its benchmark run). Queue in merge order: cow-buffers, sched, structural-simd, fusion, inline, views; then wide rows (#160) and cell kernels, then docs pass (#141).
- Removed 9 merged worktrees (+ local branches) and scratch abmaster/genbase; kept fullrun (213 BDN results).

## Owner review decided (2026-10-24), removed from docs/owner-review.md
- Declined -> one decision record in the docs pass (decisions.mdx + zh, "Declarations not added" with each reason):
  infer ITracked; infer Mut<T>; infer [Consume]; [With<A,B>]/[Without<A,B>] arity; auto Phase; ISystemGroupWithAny;
  Scan<T>; Element<T> for buffers. (Texts were in owner-review.md at d56a82d.)
- Do: infer ChunkSize after feat-sched merges, if BDN shows inferred >= hand-set (task).
- Do: rename [IndexKey] -> [LookupKey] (one key, many entities); [UniqueKey] stays. After feat-declarations merges. Public rename: migration, changelog, code fix/analyzer from old name, api.mdx, ApiDocs zh, samples, docs, D record.
- Do: retry vectorized reads of other entities: sort lookups by target table first, and AVX-512 (measure; D90 numbers 0.8-1.18 AVX2).
- widelanes round 2 confirms round 1 (ratio to scalar, 1024 light/heavy, 100k light/heavy): Split 0.17/0.19/0.15/0.21;
  Avx512Permute 0.62/0.34/0.99/0.57; Avx2Transpose 1.13/0.60/1.00/0.76; RowPerVector 1.20/1.28/1.16/1.18;
  Avx2Gather 1.51/2.19/0.99/2.17. Numbers for D118 (split first) and #160's AVX2 gate (heavy math only).

## Merged feat-analyzer (16989bb, 2026-10-24): EECS055 Warning/Info split, EECS091 cold fields + split code fix, EECS092 uncleared Emitted records, ScalarReason; D118-120. No Core change, so no runtime A/B; tests 2015/2015.
Docs to-dos: skills/easyecs/SKILL.md text in docs-todo-analyzer-rules.diff (renumbered; incl. "Migrating object-oriented code" section D119 cites); analyzer.mdx en/zh (EECS091/092, EECS055 severity, split fix; range to EECS002-102 at the end); high-performance "split first, the fix writes it"; common-mistakes "Leaving Emitted<T> full" now EECS092; troubleshooting, changelog, roadmap; HotColdBenchmark into bench.json; rerun ReduceBenchmark Bounds*; zh glossary 热字段/冷字段, 遍历; 36 EECS033 hits in docs fences (base).
Watch: ParallelEmitsDoNotAllocateAfterWarmUp flaked once (56 B) under load.
GeneratorBenchmark analyzer cost: one round only (within error) -> rerun in #153.

## feat-hugepages (WP1) DROPPED (2026-10-24): gain 0-12% at 1M (bar 20%), noise +-10%. Code kept on feat-hugepages-dropped (7e8906c, 184a39b). feat-hugepages = 630baac: LookupChaseBenchmark only -> merge after declarations (WP4 prefetch's acceptance benchmark).
Docs to-dos: D123 dropped record (text in agent report: idea, prototype 0.73/0.56, measured 0.92/0.92/1.00 TryGetEntity, 0.93/1.00/0.89 serial, 1.02/0.96/0.98 parallel, outward rounding 0.88-1.02; why not; would change: >=16M entities or WP3 2 MiB native blocks); roadmap Dropped; high-performance note (random lookups in millions are latency-bound, THP always is the user's system choice); hosting page THP madvise/always + defrag; LookupChaseBenchmark + LookupBenchmark into bench.json when shown.
WP3 (NativeMemory columns): its huge-page motive is weak now; only build if it pays on its own (bounds/stale-ref safety gated) -> research first, low priority.
Note: orphan MSBuild node (nodeReuse) held /tmp/bench.lock; agents must keep MSBUILDDISABLENODEREUSE=1.
- TODO when the machine is quiet: build benchmarks/EasyEcs.Benchmark on the work tip (LookupChaseBenchmark merged unbuilt at 9ad7be1; reviewed: no changed APIs).

## #161 add-on (from the constant-fold question)
- Kernel: lower Math/MathF.Pow(x, c) with a constant small integer c (2, 3, 4; 0.5 -> Sqrt; -1 -> 1/x) to multiplies
  instead of keeping the method scalar (EECS055 "Pow has no vector form"). Same family as the uniform hoisting in
  Scalar(); tests: each exponent, non-constant exponent stays scalar with its reason; BDN against the scalar loop.

## feat-sched merged (b3ce794) — docs to-dos
- D22 "No spinning, no dedicated threads" is stale (Workers spin 100 µs): rewrite.
- Changelog / README What's new / migration: float Reduce<T> without ChunkSize on a world whose MaxDegreeOfParallelism != 4 now gets different (thread-independent) bits.
- guide threading/reductions: determinism without ChunkSize (16 chunks for merging passes).
- ApiDocs: 38 problems from the declarations merge (oncreate/ondestroy/onupdate attrs, QueryFilter.WithAny<T> entries to delete); rerun tools/ApiDocs and regenerate docs/content/reference.
- Flaky allocation tests (ScratchTests.AParallelPassWithScratchDoesNotAllocateAfterWarmUp, ParallelEmits*): more frequent with the 400k-row Reduce test in the same run; investigate the measurement race (task #166).

## feat-cow-buffers merged — docs to-dos (D124)
- D124 copy-on-write DynamicBuffer lists (vLLM shared blocks): text in the agent's first report (later.md has it via this note):
  share count on the list (plain on owner thread, atomic in parallel passes; RunChunks counts passes), prefab lists
  uncounted (shared across worlds/threads, never pooled), counted lists copied when moved across worlds, singleton
  (Emitted<T>) lists own copies. Numbers 10k x 16: Instantiate unwritten 0.47-0.54x, Clone unwritten 0.56-0.66x;
  written Instantiate 0.97-1.02x after the Unshare fix (one thread-local lookup per pooled store, span copy), written
  Clone 1.00-1.08x; Capture reused 0.17x, Restore 0.41-0.44x, rollback 0.50x; churn within noise, 0 B.
  Rejected: paged blocks (AsSpan contiguous), atomic count everywhere (2.23x written clones), copy at instantiate
  across threads, shared component values (D14).
- guide/dynamic-buffers (shared until written, first write one copy, Clear drops without copy), guide/prefabs
  (SetBuffer list shared by every instance, later prefab changes don't affect instances), guide/snapshots, api.mdx
  complexity (Instantiate/Clone O(components); Capture/Restore buffers O(1)/row; first write O(count)), changelog line.
- bench.json: BufferSharingBenchmark, BufferSnapshotBenchmark new classes to sync.

## feat-structural-simd merged (cb9e156) — docs to-dos (D122 already written by the agent)
- Guide page (vector loops / structural changes): "You write / The generator writes" tabs for BattleServer's Die.
- README example + What's new + migration note: entity-taking methods now vectorize; EECS055/056 reason texts changed.
- Analyzer follow-up: EECS055 LooksEligible still excludes methods with Entity or Emit params (Info not shown) -> fix in #163.
- BattleServer.cs Die comment "a plain scan" -> mention the vector loop.
- Numbers: Vector 0.02-0.09x of Scalar (10k/100k, 1-50% pass), BattleServer Tick 100k 4.08/4.21 -> 3.88/3.77 ms.
- changelog (coming version): Nino 4.0.0-rc.7 (save/load level, crafted data rejected with Nino's exceptions).

## feat-inline merged — docs to-dos (D112-D114 written by the agent)
- Guide page "lambda passes" (world.ForEach(values..., static (ref P p, ...) => ...), ForEachParallel, change filters with a tick first); You write / The generator writes tabs.
- analyzer.mdx + zh: EECS095 (Error, cannot compile), EECS096 (Warning, runs through its delegate), EECS097 (Info, scalar); fix the stated EECS range.
- cheat-sheet, features, glossary (lambda pass / lambda 遍历), changelog, README What's new, roadmap, public skill.
- Regenerate docs/content/reference (40 stale zh entries from other merges).
- Follow-ups: lambda bodies with structural changes stay scalar (stable-only gate) -> enable Kernel.Structural for lambdas; benchmark row for the EECS096 delegate fallback.
- Numbers: lambda = method within error (vector 1k ~70 ns, 100k ~11.5 us; hand-written scalar job 6-11x slower); GeneratorBenchmark EditMethodBody within error, CompileAndAnalyze +2% alloc.

## feat-views merged — docs to-dos (D109-D111 written)
- Guide page views + prefab declarations ([View] partial ref struct; entity.AsX()/TryGetX(); [Prefab] partial struct -> X.Prefab; world.Instantiate(X.Prefab)); You write / The generator writes.
- analyzer.mdx: EECS098-102, range EECS002-102 (with fusion 093-094, lambdas 095-097).
- roadmap, changelog, migration ([Archetype] -> [Prefab] if it was ever public — check), README What's new, public skill, cheat sheet, glossary (view / prefab declaration), example.mdx/BattleServer.
- ApiDocs: one base fix commit for ~40 stale zh entries (OnUpdate/WithAny/hooks pages, DynamicBuffer + Prefab.SetBuffer copy-on-write text) before the gate passes.
- Follow-ups: lambda + view stays scalar; EECS053 through views in lambdas; EECS025 for never-written view ref fields; EECS049 for view locals.

## feat-fusion merged — docs to-dos (D121 text in the agent's final report: FusedRows 131,072 threshold, exceptions per-row/parallel/same-written-component)
- Guide "Pass fusion" (what fuses, FusedRows + 3 exceptions, FusionMode Auto/Require/Off, DescribeSchedule "one walk", SystemInfo.FusedWith, views, error semantics of a fused pair).
- D121 into decisions.mdx/.zh.mdx (agent did NOT write it).
- analyzer.mdx: EECS093 (Info), EECS094 (Error); range EECS002-102.
- glossary "pass fusion"; api.mdx FusionMode, SystemCode.FusedRows, SystemInfo.FusedWith, RunFused bool, EntitySpan.Slice; ApiDocs zh entries; changelog; migration (ISystemSet.RunFused returns bool, generated only); README; public skill; bench.json FusionBenchmark.
- Follow-up: lambda passes don't fuse.

## Scheduling (owner, 2026-10-25): up to 3 code agents at once (was 2).

## fix-parallel-alloc (D128, merged)
- guide/diagnostics (+zh): SystemStats.LastAllocated can include the unused rest of a thread's allocation context voided by a background GC; settle (blocking full GC) before measuring 0 B yourself; pointer to D128.
- Testing guidance (if a page shows asserting 0 B): take the starting count after a blocking full collection.
- Changelog Fixes: parallel passes no longer allocate the first time a helper waits on the queue.
- audit-core candidate: Interleaved.LoadTail lazily creates Tail<T>.Rows outside ThreadSetup.
- AGENTS.md layout: docs/content guide 'analyzer lists EECS002-089' -> current range after the docs pass.

## feat-loop-shape (D126, D127, merged)
- guide/systems vector-loop section: flat lanes, integer division and math lowerings, FloatMode.Fast forms, new reason texts.
- guide/analyzer: EECS055/056 reasons (Fast-mode suggestions, % of floats, long % by non-power-of-two, no vector form in .NET 10).
- api.mdx: decide on LaneMath rows (EditorBrowsable(Never)) — likely not listed, say so in D127.
- Note: scalar integer-division lambda body is now (int)((long)r.Value / split).
- Changelog + README What's new: flat lanes, masked-word speedup, integer division and math in vector loops.
- owner-review.md: masked tail only for bodies the runner still inlines (needs inliner budget in the generator).

## audit-analyzer-tests step 1 (merged)
- analyzer.mdx + zh: new fix outputs (EECS027 drops the Entity/World parameter that only fed lookups; EECS025 reads e.T; EECS038 keeps the ref via e.X / e.WriteX(); EECS080 writes [With<T>] for unnamed components).
- Changelog Fixes: code fixes leave no follow-up diagnostic; applying a fix twice no longer crashes.
- Follow-up (analyzer): EECS049 could also report a [WithAny<Q,T>] whose every type is in [Without<>] (matches nothing). New detection; do with the analyzer follow-ups.
- analyzer.mdx + zh: EECS025/053 cover lambda passes; EECS025 unwritten view ref fields (compilation end, 'ref readonly' fix, public library views excluded); EECS049 new cases (WithAny all excluded, lambdas, AsX/TryGetX on visited entity), new message; EECS049 on a lambda suppresses EECS095-097. Changelog.
- Decision record (small): EECS025 for view fields is compilation-wide, reported on the field (a per-method report would break methods that write it).
- roadmap.mdx + zh: one Planned item for docs/plans/inner-loops.md's goal (plain loops over buffers / inline arrays / ParallelFor lambdas become vector loops; Cell<TCell,TTile> shaders; EECS103), linking D77/D90. Rewrite the stale "Vector loops: the forms left for later" item (Pow and kin partly done by D127; masked tail D126).

## feat-lookupkey-chunkrows (D129, D130, merged)
- Changelog / migration / README What's new: [IndexKey] -> [LookupKey] (code fix), measured chunk sizes (ChunkSize only overrides).
- threading guide #chunk-rows + line 24 (stop telling users to set ChunkSize for heavy rows); analyzer guide EECS054 "when to set it"; api.mdx complexity; performance / why-fast chunking; public skill ChunkSize line; roadmap.
- bun run lint:terms on D130 zh.
- DocCheck had 296 pre-existing problems: 108 fences use removed [OnUpdate]/[OnCreate]/[OnDestroy]; new EECS025/EECS067 hits; EECS068/GroupBy errors in analyzer.mdx. ApiDocs 46 pre-existing problems. All for the docs pass (verify gate).

## fix-zh-reference (merged af585ed)
- `migrate-from-arch.zh.mdx:58`: English "`entity.IsDead` for a tag" -> Chinese.
- `lint:terms` fails on decisions.zh.mdx: lines ~2305, 2337, 2578 跟踪 -> 追踪 (D118); ~2570 结构门 -> 结构闸门 (D128).
- `api.zh.mdx` complexity rows keep English words (e.g. "O(count × c)"): Chinese names per tools/ApiDocs/zh/README.md.
- src XML docs: World `ForEachParallel(Delegate)` example shows `world.ForEach(since, ...)` (should it be ForEachParallel?);
  ForEachAttributes.cs / SystemGroups.cs write `<c>[WithAny<T1, T2>]</c>` where a `<see cref="WithAnyAttribute{T1,T2}"/>`
  would link again (then refresh the zh entries).

## audit-analyzer-tests step 2 (merged)
- analyzer guide (en/zh) EECS025: the unwritten view-field case is reported at compilation end (after a full analysis
  or build, not while typing); the other EECS025 cases stay live.

## feat-eecs103 (D134, merged)
- analyzer.mdx + zh: EECS103 section, table row, code-fix line ("Write the method for one row"); EECS range in the
  guide (EECS002-089 today) and root AGENTS.md to EECS002-103.
- changelog, roadmap, public skill (do not hand-write the vector loop of a batch method; write the method for one row).
- demo.mdx includes MovementBatchSystem.cs with a #pragma now; the exemplar pass (WP7) rewrites demo Move and
  VoxelServer FireDamageSystem.Hurt as one-row methods and drops both pragmas; rebuild docs/public/demo after.

## feat-gather-always (D131 measured again, gathers stay on request)
- Lookup<T> guide page and SimdMode docs (en/zh): when `SimdMode.Require` pays, from D131's widened table (one
  table or runs into a few: 0.41-0.99; spread over 64 / 1,000 tables, dead targets, presence bits: up to 1.21), and
  the layout advice: a lookup is cheapest when the entities it reads share components and group value (`[Group]`,
  `GroupBy`), so a vector's targets share a table.
- guide/high-performance.mdx (+zh): the same advice next to the gather line.
- benchmark pages that show LookupGather numbers: resync bench.json after a `tools/bench.sh LookupGatherBenchmark --sync`
  run (the benchmark gained 1,000 tables, dead and removed targets and three bodies).

## feat-lookup-gather (D131, merged)
- guide/high-performance.mdx:59 (+zh): a lookup "has no vector form" is stale: it gathers on `SimdMode.Require`.
- Lookup<T> guide page and SimdMode docs (en/zh): Require opts into gathers, the backoff (3/15/63 vectors after a
  miss), when it pays (targets sharing a table; spread over many tables it does not).
- analyzer guide EECS055: Info reason for Lookup methods under Auto; new reasons for methods taking Entity / Emit<T>.
- changelog, README What's new, migration: methods taking Entity or Emit<T> can now get EECS055 Warnings (split the
  method or mark it SimdMode.Off); Lookup gathers on Require.
- api.mdx: Lookup.Resolve only if listed (EditorBrowsable Never).
- rebuild docs/public/demo (BounceSystem / FadeSystem got SimdMode.Off).

## Locality phase 0 merged (0e206d8): pitfalls users hit (docs or analyzer)

- `Lookup.Find` on a `[LookupKey]` index is prepared only when called in the `[ForEach]` body itself; called from a
  helper it throws "index not prepared" at run time. Document it on the indexes page, and check whether the analyzer
  can report the call from a helper (`audit-analyzer`).
- Calling a `[ForEach]` method from inside its declaring type needs the qualified name (`Crowd.Separate(world)`): the
  bare name binds to the user's method (CS7036). Say so where the generated entry points are documented.
- A `[LookupKey]` member named `Key` gives EECS036 / CS0315 (the generated explicit `Key` collides): the message or
  the docs should name the fix.
- When L1 / L2 ship, the prototypes under `benchmarks/EasyEcs.Benchmark/Locality/` and their link into the unit tests
  go (nothing stale); their benchmarks keep only the plain baselines against the shipped forms.

## Calls in vector loops (owner question 2026-10-26)

- systems.mdx "Vector loop" conditions: say which calls the loop takes (Math / MathF with vector forms, the generated
  members, Emit.Add, Lookup reads on Require, view members inlined from source) and that any other call (a helper of
  your own, an extension method, a property with a body, a call into another assembly) keeps the method scalar today,
  with EECS055's "it calls X.Y"; update when the inliner item of the backlog ships. The list there is also stale: it
  still says no `Entity`, `Lookup<T>`, `Emit<T>` parameter (structural changes of the row's entity, Emit records and
  Lookup gathers are vector forms now).

## Owner decisions on owner-review (2026-10-26): decision records to write in the docs pass

(Written then, not now: three running branches also edit decisions.mdx.)
- **Timer wheel: dropped** ("Not added" record, en + zh). TimerWheelBenchmark, 100k waiting, 1 % waking, two rounds:
  the wheel 1.18-1.26x slower than the generated vector scan (21 us per tick, 0.2 ns a row against about 25 ns per
  woken entity), with a tag 3.6-3.9x slower; it beats only the scan row by row (3x). Would change it: waits the
  vector loop cannot take, or far fewer wakes per waiting entity (1M waiting, 0.1 %), against the vector scan.
- **NUMA placement and hybrid cores: dropped** ("Not added" record). First-touch columns, a world per node, helpers
  kept on their node (OpenMP proc_bind, HyPer morsels), P / E and SMT checks: node placement needs dedicated threads,
  which D22 declined for the thread pool, and D130's measured chunks already give slower threads fewer chunks. Would
  change it: a two-socket or hybrid-core machine showing a loss in ParallelForEach / BattleServer that placement
  removes.
- Batch probes and nearest on a spread map are not decided yet: they moved to the backlog (re-measure later).

## feat-amac (borrowed-ideas.md item 1, locality.md 6.3; nothing user-visible)

Decision records for the docs pass (en + zh, "Not added" unless the owner's re-measure with a size gate builds it):
- **Prefetching lookup chases (group prefetching, Chen et al. 2004; AMAC, Kocberber et al. VLDB 2015): not built.**
  The generated job would prefetch the record of row i + 2k and the T row of row i + k (two stages: a row's address is
  known only once its record is in cache), k = 8 rows (the vector loop the 8 rows 2k and k past its vector).
  LookupChaseBenchmark, random targets, two rounds, 0 B: against the generated jobs 1.29-1.35x (rows) and 1.31-1.36x
  (gathers) at 16M entities, 1.04-1.22x at 1M (inside this machine's 260 MiB last-level cache), and 1.9x (rows) /
  2.3x (gathers) slower at 1k, where there is no miss to hide. Bar 1.3x at 1M and 16M, nothing slower at 1k: not met.
  Considered: records only (stage 1: about half the gain), distances 2-64 rows (8-32 best), the vector loop's gathers
  prefetched like rows. ARM64: .NET 10 has no prefetch but SVE's (not used), so the form does nothing there. Would
  change it: a size gate (records past the last-level cache) and numbers from a machine with a common-size LLC.
- **Batch probes of an index (Polychroniou's vertical probes, with and without refill): not added**, re-measured at
  16M keys: 1.25-1.63x slower than one key at a time (a gather waits for its slowest lane). Scalar group prefetching
  is the form that pays there (1.33x at 16M keys, 1.3-1.6x slower when the table is in cache), and an index has no
  batch probe to put it in (keys known ahead exist only in generated code). Numbers: locality.md 6.3.
- `api.mdx`: nothing (the helpers are hidden, for generated code only).


## feat-arm-parity: wide components on ARM64, the arm64 job's loop lines

- guide page of vector loops (systems.mdx "Vector loop" / the wide-component section): a wide component (up to 16
  words a row) now has two forms, AVX-512 permutes and ARM64's 128-bit NEON table lookups (one TBL per field over the
  up to 4 registers holding its 4 rows' words, one TBX per written vector); each is emitted only where its own cost
  gate pays, and EECS055 reports the reason only when neither does. Say what this means for the user: a wide
  component can be vector on one ISA and rows on the other; DescribeSchedule's line says which
  ("vector (Vector512 or Vector<float> of 128 bits)", "(Vector512 only)", "(Vector<float> of 128 bits only)") and
  adds "; rows on this hardware" when the running machine has none of the emitted widths.
- introspection page (DescribeSchedule / `__loops`): the new parts "wide component (permutes, table lookups)",
  "gathers (Lookup<T>)" and the runtime suffix "; rows on this hardware" (en + zh).
- performance page: the ARM64 numbers of `KernelBenchmark.Steer*` / `Glide*` (owner's M4, 2026-10-26: Steer 2.07x /
  1.55x, Glide 1.87x / 1.63x faster than rows at 1k / 100k, 0 B) once the site can show them
  (bench.json through `tools/bench.sh KernelBenchmark --sync` on that machine is a separate machine: keep it out of the
  reference numbers unless the site gains an ARM64 column).
- performance / why-fast pages: compress on ARM64 (owner's M4 CompressBenchmark, `locality.md` 6.3: the NEON TBL
  table 1.7-12x over branchy) once L3's core helper is measured there; the same separate-machine caveat.
- decisions.mdx + .zh.mdx: the decision record of the TBL form (draft in the branch's hand-off: D136 or the next free
  number at merge), and update D131's ARM64 note if it lists wide components among the rows-only forms.
- the public skill (skills/easyecs/SKILL.md): "a wide component vectorizes on AVX-512 and ARM64 where the gate pays;
  check DescribeSchedule on the target machine" in the SIMD section.
- tests/AGENTS.md's Kernel line could name the wide forms' bit-for-bit test and the ARM64 loop-line test (file map,
  not docs/content: do it with the next tests change).

## feat-prefetch-gate: the 16-word locals fix, lookup chases prefetched past the last-level cache

Decision record drafts (en + zh, next free numbers at merge):
- **Prefetching lookup chases, behind a last-level cache gate** (rewrites the feat-amac "not built" record above).
  A `[ForEach]` method reading another entity at an id of a component it takes by `in` (`lookup[x.F]`, `Read`, `Has`)
  prefetches the targets of later rows in two stages (group prefetching, Chen et al. 2004; AMAC, Kocberber et al. VLDB
  2015): the record of row i + 2k, then the row of T of row i + k, whose address is known once its record is in cache.
  Row form: k = 8 rows, in a twin job (`Job{i}Chases`) the pass's call site picks; vector loop: the 8 rows 32 and 16
  past each vector, behind a flag its job reads once per pass. Only when the live entities' records and rows of T are
  past the last-level cache, read once per process as the GC reads it (Windows `GetLogicalProcessorInformationEx`,
  Linux sysfs then `sysconf`, macOS `sysctl hw.l3cachesize` / `hw.perflevel0.l2cachesize`), x86 only (.NET 10 has no
  prefetch on ARM64 but SVE's); unknown means off. Why: 1.36-1.43x (rows) and 1.27-1.34x (gathers) at 16M entities,
  while in the cache a prefetch costs 1.4-1.9x with nothing to hide (1k); the gate keeps today's code there (measured
  equal). Considered: prefetching always (slower in cache), a fixed record count (machine-dependent), the GC's
  `GCGen0MaxBudget` (configurable, not a contract), a run job for every pass (3x slower parallel heavy rows at 1k, a
  few percent on serial rows: hence the twin per pass), parallel row methods (keep the per-row form: not built),
  distances 2-64 (8 rows / 16 for vectors best). Numbers: borrowed-ideas.md section 5, item 1.
- **D135 addendum: wide components count their loop's locals** (`Kernel.FitLocals`): a 16-word row gathering 3 fields
  wrote a `__Loop512` of 34 IL locals; the extract reuses the field's name, merges of merges reuse their input's local,
  the packed stores share one temp (32 now), and a loop past 32 drops its AVX-512 form (the 128-bit form stays) or, with
  none, reports "gathering the N fields it uses declares M locals per 16 rows, past the 32 a job's loop may declare to
  be inlined" (EECS055, fixable when a split helps).

Docs to-dos (docs pass):
- guide page of `Lookup<T>` (and lookups in vector loops, D131): "reads of other entities past the last-level cache are
  prefetched rows ahead; nothing to write; x86 only; in the cache nothing changes"; which forms qualify (an `in`
  component's id; not keyed lookups, parallel row methods, lambda passes or fused walks) and the cost model in one
  sentence; en + zh.
- introspection page: DescribeSchedule's new part "lookups prefetched past the last-level cache" (scalar and vector
  lines); en + zh.
- performance / why-fast pages: the 16M chase numbers through bench.json (`tools/bench.sh LookupChaseGateBenchmark
  --sync`, a BenchTable: Off / Auto / On), and the owner's runs on other machines if the site shows them.
- vector loops guide (EECS055 reasons list): the new wide-component reason "declares M locals per 16 rows, past the 32
  a job's loop may declare to be inlined".
- architecture page (generated code): the twin job beside a method's own job (as `Job{i}Rows` for vector tails).
- public skill (skills/easyecs/SKILL.md): "a chase through `Lookup<T>` at an `in` component's id is prefetched when the
  world outgrows the cache: keep ids in `in` components".
- `api.mdx`: nothing (`Lookup<T>.PrefetchesPastLastLevel` / `PrefetchAhead`, `LookupLanes.Prefetches` are hidden, for generated code).

## feat-call-inliner: vector loops inline the user's own calls

- systems.mdx and the guide's "what vectorizes" list: helpers are inlined. Say "write helpers freely", then list the
  callees that are inlined (static, extension and C# 14 extension members, struct and component instance members
  including ones writing `this`, local functions, up to 8 deep, ref/out arguments, early returns, a structural change
  through a generated member) and the limits (virtual, interface or delegate targets, another assembly, recursion,
  deeper than 8, a body with a loop / lambda / try / switch / fixed / stackalloc, a writing helper inside a larger
  expression, a body naming a private or file-local member: make it internal). Lambda passes do not inline.
- guide/analyzer.mdx: EECS055's new reasons and their Warning (fixable: "call it in a statement of its own", "make it
  internal") / Info split; EECS055 applies only to [ForEach] methods.
- Views page: views are inlined through the same path; drop any mention of a compilation copy.
- performance / why-fast: KernelBenchmark.StepHelpers (equal to StepGenerated) and the Slices' Think (4.5-4.8x
  faster) through bench.json (`tools/bench.sh KernelBenchmark Slices --sync`); the generator's IDE keystroke about
  half (GeneratorBenchmark EditMethodBodyIde 417 → 214 ms, 57 → 28 MB; build-mode keystroke +18%).
- changelog + README "What's new": helpers inlined into vector loops; IDE keystroke cost halved.
- the public skill: helpers are fine in [ForEach] methods; keep the members they name internal, not private; no
  virtual, interface or delegate calls in pass code.
- decisions.mdx + .zh.mdx, next free D: "Vector loops inline the user's own calls". Decision: Kernel inlines calls
  whose source is in the compilation before translation; each callee read once per compilation, refused on syntax
  first, bound in a speculative model of the method body; system loops computed only for implementation output;
  views desugared as a static local function in the same speculative body. Why: users should not avoid their own
  helpers (StepHelpers = StepGenerated; Slices 4.5-4.8x). Considered: a compilation copy per method (+80 ms, +19 MB
  per keystroke for 40 view methods); inlining in the declaration transform (reruns per keystroke); a code fix for
  "call it in a statement of its own" (not semantics-preserving: `x = a.V + Take(ref a)`). Limits: depth 8; no
  recursion, virtual, interface, delegate or cross-assembly callees; private members stay calls.

## feat-locality-core (L1 + L3 of locality.md: D137, D138; D135 went to WP0b, D136 may go to feat-arm-parity)
- New guide page (or a section of high-performance.mdx + zh): ordered tables. That the order is inferred for pair
  methods (no attribute), `[OrderBy]` as the override (non-spatial keys), `Interval` / `Margin` (GROMACS's nstlist and
  buffer), `SortRows<T>`, what a sort costs (RowOrderBenchmark: BenchTable), `world.Clusters<T>()` / `entities.Clusters` /
  `RowClusters.Pairs` for hand-written searches until the pair form ships, refs and handles ending at a sort (like any
  structural change), saves keeping the row order and redrawing the boxes at the next update.
- refs-and-await.mdx + zh: a sort is a structural change at the start of an update (re-acquire refs after it).
- saves.mdx + zh, snapshots.mdx + zh: the row order is kept, boxes redrawn (D137).
- analyzer.mdx + zh: EECS104 (section, table row; EECS range) and EECS041's list (SortRows, OrderRows).
- high-performance.mdx + zh: Lookup targets ordered by their target read near rows (LookupOrderBenchmark: Chase 2x,
  64-table gathers 4.6x at 1M); `Lanes.Selection` for a row list from a mask; Compact on every ISA (NEON, SSSE3).
- architecture.mdx + zh: RowOrder in the table diagram (Archetype.Order, Permute), the cluster side array.
- glossary + zh: ordered table (有序表), cluster (簇, the unit of an ordered table; 块 is the parallel chunk), box (包围盒), margin (余量), cadence (节奏);
  tools/ApiDocs/zh/README.md glossary gets the same rows.
- changelog / README What's new: ordered tables (the read path), Lanes.Selection, Compact on ARM64.
- Benchmarks into bench.json: RowOrder, LookupOrder, Pairs (Core*), Compress (CoreCompact) after the reference run.
- skills/easyecs/SKILL.md (public skill): ordered tables (the order is inferred; `[OrderBy]` only for orders no pair
  method implies; refs end at a sort), `Clusters` / `Pairs` for hand-written neighbor searches, `Lanes.Selection`.
- The Native AOT listing of `Lanes.Compact`'s loops on the owner's AOT check (the tables are RVA data now; no class-init
  check expected) and the M4 rerun of `CompressBenchmark` (the 128-bit TBL form against the prototypes' ShuffleTable).

### Decision record drafts (D137, D138)

The records for decisions.mdx / .zh.mdx, written in the docs pass (index rows after the last record; renumber if
D137 / D138 are taken by then, and fix the numbers in `Declarations.cs`, `RowOrder.cs`, `tools/ApiDocs/zh/orderbyattribute.xml`,
roadmap). The zh drafts carry the decision and what was considered; their **数据** sections are still to translate
from the English. Raw reports: BenchmarkDotNet's markdown, quoted below; the snapshots were frozen copies of the
branch (A = 0e206d8 + the NEON prototypes, B/F = this branch).

<details><summary>D137 (en)</summary>

## D137. Tables keep their rows in the order of a key, with a box per cluster of rows [#d137-ordered-rows]

**Decision.** A table can keep its rows sorted by a key and draw an axis-aligned box over each cluster of rows: the
layout a pair kernel reads (a cluster of rows against a cluster, the pairs of clusters found from the boxes), and a side
effect for every pass that reads other entities: readers sorted by their targets read near records and rows.

- **Who asks.** The order is normally inferred: the setup of a generated method that compares positions within a
  cutoff calls `World.OrderRows<T, TOrder>(axes, cell, interval, margin)` for the component it reads positions from.
  `TOrder` is a struct implementing `IRowOrder<T>` with static `Key` and `Point` (no delegate, one sort per key type
  compiled for it); the key is `RowKeys.Morton` of the position quantized by the cutoff (63, 32 or 21 bits per axis,
  clamped in integers). The request covers the tables there now and those to come; the first request for a component
  stands. `[OrderBy]` on a member (its generated `IOrderKey<TKey>`) overrides it for every world: a non-spatial key, or
  another cell. A table nobody asked an order for pays one null check where a row moves or the rows shrink.
- **The sort.** An LSD radix sort of (key, row) pairs over pooled buffers, 32-bit keys when they fit, the bytes every
  key shares skipped, nothing moved when the rows are already in order; then one permutation (`Archetype.Permute`)
  of every column, presence bit, change tick (the word and block summaries rebuilt from the ticks) and slot, under
  the structural gate, bumping `StorageVersion`. Rows lacking the component sort last. It runs at the start of the
  default group's update every `interval` updates (GROMACS's `nstlist`), or at once with `SortRows<T>()`; both throw
  during a pass (EECS041 lists them). Refs, handles' cached rows and marked rows end at a sort as at any row move.
- **Clusters.** 8 rows where `Vector256` is hardware, 4 otherwise (ARM64's NEON is 128 bits wide), or the power of two
  up to 64 a request asks for, so a kernel's cluster is one register of floats per axis. The sort draws each cluster's box
  over `axes` axes of `Point`, widened by the margin; a row that moves into a cluster between sorts widens that cluster's
  box; rows created after the sort wait past `SortedRows` until the next one. The boxes live in a side array of the
  order (bounded by the table's clusters, released by `TrimExcess`), never in a hot struct. `world.Clusters<T>()` and
  `EntitySpan.Clusters` hand out `RowClusters`: the boxes, a cluster's rows, read-only columns, ids, presence, and
  `Pairs(cutoff, into)` / `Pairs(other, cutoff, into)`, the cluster pairs whose boxes come within the cutoff (a sweep over the
  boxes sorted by their low side on the first axis, the other sides gathered into that order).
- **Saves.** A restore or a load keeps the physical row order it finds (a save writes rows in table order) and drops
  the boxes; the next update sorts (rows already in order cost the keys and the boxes) and draws them. Writing the
  boxes into a save would tie the format to the cadence for nothing the first update does not rebuild.
- **EECS104** reports an `[OrderBy]` the generated part cannot keep: a key that is not a plain value (a struct, a
  string), box members that are not floats or one `Vector2` / `Vector3`, a bad interval or margin, two keys, a
  singleton, or `OrderKey` / `BoxPoint` written by hand as well.

**Numbers.** Xeon 2.10 GHz VM (4 cores, AVX-512), .NET 10, BenchmarkDotNet's default job, two rounds over frozen
snapshots; other agents shared the machine (load average 1.1 to 11 at the start of a class), so differences under
about 10 % between A and B are noise. Every row allocates 0 B.

- **The sort** (`RowOrderBenchmark`, one call, round 1 / round 2). Narrow table (the ordered component alone), all
  rows reversed: 1k 13.8 / 15.9 µs, 10k 178 / 195 µs, 100k 2.33 / 2.38 ms, 1M 32.7 / 38.1 ms. Wide table (six more components, one tracked, one
  managed): 23.7 / 25.0 µs, 351 / 312 µs, 3.51 / 3.78 ms, 71.8 / 81.1 ms. A churn of 1 % of the keys costs as much as a reversal (every row between
  a moved key's old and new place shifts). Rows already in order: 0.66 / 0.67 ms at 100k, 6.5 / 7.0 ms at 1M (the keys and the
  boxes, about 6.5 ns per row).
- **The pair kernel on the core's layout** (`PairsBenchmark`, per tick, round 1 / round 2): the cluster kernel reading
  the table's own columns in their sorted order against the phase-0 prototype reading sorted side arrays
  (`Clusters`): crowd 1k 60.4 / 60.5 against 52.6 / 53.9 µs, 10k 747 / 697 against 597 / 647 µs, 100k 7.59 / 7.41
  against 6.70 / 6.91 ms; sparse 1k 30.0 / 29.5 against 28.2 / 28.0 µs, 10k 452 / 324 against 328 / 323 µs, 100k
  3.50 / 3.61 against 3.40 / 3.50 ms: within 0-15 % of the copies, without the per-tick copies a live world would need
  (the prototype's `ClustersWithCopies10`). With the core's sort and the pair lists rebuilt every 10 ticks
  (`CoreClustersSort10`), against phase 0's grid read through lookups (its baseline, section 6.1 of the plan): crowd 10k 0.82 / 0.81 ms against 9.95 ms, 100k 9.9 / 10.3 ms against 153 ms (12-15 times); sparse 10k 0.43 / 0.42 ms against 2.15 ms, 100k 5.1 / 5.1 ms against 34.7 ms (5-7 times).
  The cluster-pair search was the largest part of a rebuild: gathering the other sides into the sorted order once (instead of copying a table's sides per candidate) took the 100k rebuild from 11.0 / 11.3 to 9.9 / 10.3 ms in a crowd and from 5.77 / 5.75 to 5.08 / 5.14 ms sparse.
- **Clusters of 16** (`CoreClusters16`, two 8-row clusters per 512-bit kernel step): within 5 % of 8 at 1k and 10k in a
  crowd, 22-25 % slower at 1k sparse and at 100k. 8 (4 on ARM64) stays the size; a request can ask for another.
- **Readers ordered by their targets** (`LookupOrderBenchmark`, round 1 / round 2, measured before the merge of the reworked LookupChase / LookupGather (the same passes, `Steer` then), one table of readers sorted once by
  the target's id): `Lookup` chases across 1M entities 15.7 / 17.8 ms → 7.60 / 7.34 ms; across 100k 890 / 819 →
  421 / 384 µs. D131's gathers with targets in 64 tables at 1M 11.5 / 13.3 ms → 2.48 / 2.58 ms, and the method row by
  row 11.6 / 11.1 → 3.09 / 3.05 ms: once ordered, the gathers beat the method row by row in the case they lost.
- **A table without an order** (A/B against the snapshot before this change): `WorldSimulation` at 100k 1.18 → 1.16 ms in
  round 1, 1.27 → 1.33 ms in round 2 (10k 98 → 97 µs, 101 → 109 µs); `BattleServer` 3.45 → 3.82 ms in round 1 (B started at load 7.4), 3.88 → 3.87 ms in round 2; `OperationBenchmark` (EasyEcs rows) within -10 % / +1 %; `StructuralLoopBenchmark`
  within ±5 %; `BulkBenchmark` within -5 % / +9 % (StunAndLift 22.9 → 25.0 µs in round 1, 24.5 → 25.3 µs in round 2; DisableAndEnable 20.8 → 23.0, 23.4 → 22.2 µs).

**Considered.**
- **A user-facing attribute as the main entry point**: users would name the key and the cell for every component a
  pair method reads; the generator sees the distance test and the cutoff already. `[OrderBy]` stays for orders no
  method implies.
- **Hilbert keys**: better locality per cluster than Morton, more instructions per key; the boxes absorb Morton's jumps
  and the sort's cost is the permutation, not the keys.
- **Sorting on every write, or a tree of rows**: GROMACS re-sorts on a cadence; the margin keeps the boxes valid
  between sorts, and a sort of rows already in order costs the keys and the boxes only.
- **Clusters of 16 rows**: see the numbers.
- **Moving only the rows out of order**: a random 1 % churn already moves most rows, so the sort moves every row once.
- **A box tree for the cluster pairs** (the prototype's): one walk per cluster; the sweep over sorted low sides is
  simpler, needs no tree to keep, and is parallel-safe (it reads the boxes only).
- **Saving the order and the boxes**: see Saves.

</details>

<details><summary>D138 (en)</summary>

## D138. Compaction picks its instructions at run time on every machine; `Lanes.Selection` is the one row-list helper [#d138-compaction-everywhere]

**Decision.** `Lanes.Compact` (the stream compaction behind `Emit.AddIds` / `AddLanes`, `CollectIds` and batch
methods) walks a word of 64 selected bits in the first form that fits:

1. **4 selected or fewer**: one write per bit (TZCNT), on every machine.
2. **AVX-512**: `CompressStore`, 16 lanes of 4 bytes or 8 of 8 bytes per instruction.
3. **12 selected or fewer**: one write per bit again (below that, the narrower vector steps cost more than the bits).
4. **AVX2**: a permutation table (`PermuteVar8x32`, one entry per byte of the mask for 8 lanes of 4 bytes, one per
   nibble for 4 lanes of 8 bytes; Polychroniou, Raghavan and Ross's selective store).
5. **128-bit, 4-byte lanes**: a byte shuffle per nibble (16 entries of 16 bytes) through
   `AdvSimd.Arm64.VectorTableLookup` (NEON `TBL`) on ARM64 and `Ssse3.Shuffle` (`PSHUFB`) on x86.
6. **Otherwise** (8-byte lanes without AVX2, other sizes): the scalar walk.

The vector steps (4 and 5) are branch-free: each stores a whole vector and advances by its count while the
destination has room for a whole vector; the last selected elements (fewer than a vector) are then written one per
set bit. So every form reads nothing past the end of the source and writes only the selected count, with no partial
store. Every intrinsic sits behind its `IsSupported`, so one build runs everywhere and the JIT drops the branches a
machine cannot take. The tables are literal data in the assembly (`ReadOnlySpan<byte>` over constants): nothing to
initialize, so under Native AOT a loop reading one checks no class initialization; the AVX2 permutations are a byte
per lane widened by the load (`VPMOVZXBD`), 2 KiB. `Lanes.Selection(mask, first, destination)` writes the row numbers a
mask selects (`Compact` over 0..63, then `first` added a vector at a time): the one call a generated loop or the core
makes for a row list, so generated text never names an instruction set.

**Where compaction is not used.** The TZCNT walks of generated loops (`ArityGenerator`'s masked walks,
`SystemGenerator.Fusion`) call the method once per row or per run of rows; a row list first would add a pass without
removing a call. `MarkedRows` keeps bit words (applying them is a structural change per row, which dominates), and
`World.Bulk`'s walks likewise. `Kernel.Structural`'s emitted `Emit.AddIds` / `AddLanes` already go through
`Compact`.

**Numbers.** Xeon 2.10 GHz VM (AVX-512), .NET 10, BenchmarkDotNet's default job, two rounds, base (A) against this
change, the instruction sets turned off with `DOTNET_EnableAVX512=0` / `DOTNET_EnableAVX2=0` (the ISA column of each
report confirms the knob; `DOTNET_EnableAVX512F` is ignored by .NET 10). `CompressBenchmark.CoreCompact` (4-byte row
numbers from a float condition, 10 / 50 / 90 % selected), round 1 / round 2:

| ISA | Rows | 10 % | 50 % | 90 % |
|---|---|---|---|---|
| AVX-512 | 4,096 | 1.07 / 0.98 → 0.87 / 0.93 µs | 1.08 / 1.15 → 1.03 / 1.02 µs | 1.15 / 1.11 → 1.06 / 1.04 µs |
| AVX-512 | 262,144 | 88 / 80 → 87 / 88 µs | 82 / 83 → 89 / 85 µs | 89 / 91 → 92 / 90 µs |
| AVX2 | 4,096 | 0.93 / 0.90 → 0.89 / 0.83 µs | 4.17 / 3.76 → 1.52 / 1.54 µs | 3.82 / 3.81 → 1.60 / 1.51 µs |
| AVX2 | 262,144 | 125 / 108 → 130 / 111 µs | 268 / 269 → 168 / 156 µs | 233 / 237 → 145 / 149 µs |
| SSE4.1 (128-bit) | 4,096 | 1.54 / 1.63 → 1.44 / 1.45 µs | 4.65 / 4.71 → 3.02 / 3.06 µs | 4.33 / 4.32 → 3.25 / 3.22 µs |
| SSE4.1 (128-bit) | 262,144 | 136 / 144 → 153 / 144 µs | 321 / 312 → 251 / 228 µs | 302 / 285 → 212 / 213 µs |

AVX-512 is unchanged (its form was already there); 10 % is within the rounds' spread everywhere (the worst pair,
SSE4.1 at 262,144, is 136 → 153 in one round and 144 → 144 in the other). `CompactBenchmark` (1M rows, 8-byte ids)
without AVX-512: `Compact` 1.08 ms → 0.78 ms and `CollectIds` 0.87 → 0.64 ms at 50 %; without AVX2 equal (8-byte
lanes keep the scalar walk). All 0 B. ARM64 (NEON `TBL`) runs the 128-bit form; the owner's M4 measures it.

**Considered.**
- **A public compaction API per instruction set**: users would write the dispatch the core already does;
  `Lanes.Compact` and `Lanes.Selection` stay the only entry points.
- **A partial store per step at the destination's end** (a masked store with AVX2, a copy of a variable length on
  128-bit vectors): every step after the first few selected ones paid for it at low density; a whole-vector loop and
  the last elements one per bit cut the 128-bit form at 50 % from 1.67 to 1.36 µs (4,096 rows, screening).
- **The base's branch-free scalar walk for 5 to 12 selected** instead of a write per bit: 2.7 µs against 0.85 at
  4,096 rows and 10 % without AVX-512 (screening); dropped.
- **No sparse rule** (the vector steps from 5 selected): 10 % lost (0.90 against 0.81 µs, 122 against 104 µs).
- **A 128-bit form for 8-byte lanes** (two lanes per step, a 4-entry table per 2 bits): 32 steps a word lost to the
  scalar walk, `CompactBenchmark` without AVX2 at 1M ids and 50 % selected 1.19 ms against 1.00 ms; dropped.
- **Steps that skip an empty part of the mask**: a branch per step mispredicts on mixed masks; the 128-bit path with
  those branches ran 3 times slower than the scalar walk at 50 % selected.
- **Static readonly tables built by code**: Native AOT checks the class's initialization on every read in a loop
  (`audit-vector-loops`, "Read the loop the JIT wrote"); literal data costs nothing to initialize.
- **A table per 16 bits** (AVX-512's width without `VPCOMPRESS`): 2 MB against the 2 KiB and 256 B tables that stay
  in L1 next to the loop that reads them.

</details>

<details><summary>D137 (zh)</summary>

## D137. 表按键的顺序保存行，每簇一个包围盒 [#d137-ordered-rows]

**决定。** 一张表可以让它的行按某个键排好，并在每簇上画一个轴对齐包围盒：这是成对内核读取的布局（一簇行对一簇行，
簇对由包围盒找出），也让每个读取其他实体的遍历顺带受益：按目标排好的读取方读到的记录和行彼此相近。

- **谁来要求。** 顺序通常是推断出来的：生成的方法若在一个截断距离内比较位置，它的初始化就为读取位置的组件调用
  `World.OrderRows<T, TOrder>(axes, cell, interval, margin)`。`TOrder` 是实现 `IRowOrder<T>` 的结构体，带静态的
  `Key` 和 `Point`（没有委托，每种键类型编译出一份排序）；键是按截断距离量化后位置的 `RowKeys.Morton`（每轴 63、32 或
  21 位，用整数钳位）。这个要求覆盖现在和以后的表；一个组件以第一次要求为准。成员上的 `[OrderBy]`（生成它的
  `IOrderKey<TKey>`）对所有世界覆盖推断：非空间的键，或另一个格子大小。没人要求顺序的表，只在行移动或行数收缩处多一次判空。
- **排序。** 对 (键, 行) 对做 LSD 基数排序，缓冲区来自池，键放得下时用 32 位，所有键共有的字节跳过，行已经有序时什么也不搬；
  然后在结构门下对每一列、存在位、变更 tick（字和簇的摘要由 tick 重建）和槽做一次置换（`Archetype.Permute`），并增加
  `StorageVersion`。没有这个组件的行排在最后。它每隔 `interval` 次更新在默认组更新开始时运行（GROMACS 的 `nstlist`），
  或用 `SortRows<T>()` 立即运行；两者在遍历中都会抛出（EECS041 列出它们）。ref、句柄缓存的行和标记的行在排序时失效，和任何行移动一样。
- **簇。** `Vector256` 有硬件支持时 8 行，否则 4 行（ARM64 的 NEON 宽 128 位），或要求指定的不超过 64 的 2 的幂，
  这样内核的一簇正好是每个轴一个浮点寄存器。排序在 `Point` 的 `axes` 个轴上画出每簇的包围盒，再加上余量；两次排序之间
  移进某簇的行会扩大那簇的包围盒；排序之后新建的行排在 `SortedRows` 之后，等下一次排序。包围盒放在顺序的旁路数组里
  （以表的簇数为界，`TrimExcess` 释放），从不进热结构。`world.Clusters<T>()` 和 `EntitySpan.Clusters` 给出 `RowClusters`：
  包围盒、一簇的行、只读列、id、存在位，以及 `Pairs(reach, into)` / `Pairs(other, reach, into)`：包围盒相距在 reach
  之内的簇对（按第一轴低边排序后扫描，其他边按这个顺序收集）。
- **存档。** 恢复或读档保留它读到的物理行序（存档按表内顺序写行），丢掉包围盒；下一次更新会排序（已经有序的行只花键和包围盒的开销）
  并重画它们。把包围盒写进存档会把格式和节奏绑在一起，而第一次更新本来就会重建它们。
- **EECS104** 报告生成的部分无法维护的 `[OrderBy]`：键不是普通值（结构体、字符串）、包围盒成员不是 float 或一个
  `Vector2` / `Vector3`、interval 或 margin 不合法、两个键、单例，或者同时手写了 `OrderKey` / `BoxPoint`。

**数据。** NUMBERS

**考虑过的方案。**
- **以面向用户的特性为主入口**：用户要为邻居对方法读取的每个组件写出键和格子；而生成器已经看到距离判断和截断距离。
  `[OrderBy]` 留给没有方法暗示的顺序。
- **Hilbert 键**：每簇的局部性比 Morton 好，但每个键的指令更多；包围盒吸收了 Morton 的跳跃，排序的开销在置换，不在键。
- **每次写入都排序，或行的树**：GROMACS 按节奏重排；余量让包围盒在两次排序之间保持有效，已经有序的行排序只花键和包围盒的开销。
- **16 行的簇**：见数据。
- **只搬不在位置上的行**：随机 1 % 的变动已经搬动大多数行，所以排序把每行搬一次。
- **簇对用包围盒树**（原型的做法）：每簇走一遍树；按低边排序后扫描更简单，没有树要维护，也可以并行（只读包围盒）。
- **把顺序和包围盒存进存档**：见存档。

</details>

<details><summary>D138 (zh)</summary>

## D138. 压缩在每台机器上运行时选择指令；`Lanes.Selection` 是唯一的行列表辅助 [#d138-compaction-everywhere]

**决定。** `Lanes.Compact`（`Emit.AddIds` / `AddLanes`、`CollectIds` 和批处理方法背后的流压缩）对 64 位选择字使用
第一个适用的形式：

1. **选中不超过 4 个**：每位写一次（TZCNT），所有机器都一样。
2. **AVX-512**：`CompressStore`，每条指令 16 个 4 字节通道或 8 个 8 字节通道。
3. **选中不超过 12 个**：再次每位写一次（低于这个数，更窄的向量步骤比逐位更慢）。
4. **AVX2**：置换表（`PermuteVar8x32`，8 个 4 字节通道时掩码每个字节一项，4 个 8 字节通道时每 4 位一项；Polychroniou、
   Raghavan 和 Ross 的选择性存储），每一步整体写出、游标按这一步的个数前进，所以没有一步按它的位做分支。
5. **128 位、4 字节通道**：每 4 位一次字节重排（16 项，每项 16 字节），ARM64 上走 `AdvSimd.Arm64.VectorTableLookup`
   （NEON `TBL`），x86 上走 `Ssse3.Shuffle`（`PSHUFB`），同样没有分支。
6. **其他情况**（没有 AVX2 时的 8 字节通道、其他大小）：标量遍历。

每个 intrinsic 都在它的 `IsSupported` 之后，所以一份构建到处能跑，JIT 会去掉这台机器走不到的分支。每种形式都不读源末尾之后的内容，
只写选中的个数（向量步骤留出一整步的空间，或在边上退回标量遍历）。`Lanes.Selection(mask, first, destination)` 写出掩码选中的行号
（对 0..63 做 `Compact`，再按向量加上 `first`）：生成的循环或核心要行列表时只调这一个，所以生成的代码从不写出指令集的名字。

**不用压缩的地方。** 生成循环里的 TZCNT 遍历（`ArityGenerator` 的掩码遍历、`SystemGenerator.Fusion`）每行或每段行调一次方法；
先做行列表只多一遍，不少一次调用。`MarkedRows` 保留位字（应用它们是每行一次结构变更，那才是大头），`World.Bulk` 的遍历也一样。
`Kernel.Structural` 生成的 `Emit.AddIds` / `AddLanes` 已经走 `Compact`。

**数据。** NUMBERS

**考虑过的方案。**
- **每个指令集一个公开的压缩 API**：用户要自己写核心已经做好的分派；`Lanes.Compact` 和 `Lanes.Selection` 仍是仅有的入口。
- **8 字节通道的 128 位形式**（每步两个通道，每 2 位一项的 4 项表）：每个字 32 步，输给标量遍历，没有 AVX2 时
  `CompactBenchmark` 在 1M 个 id、选中 50 % 时 1.19 ms 对 1.00 ms；放弃。
- **跳过掩码空的部分的步骤**：每步一个分支，在混合掩码上预测失败；带这些分支的 128 位路径在选中 50 % 时比标量遍历慢 3 倍。
- **每 16 位一张表**（没有 `VPCOMPRESS` 时 AVX-512 的宽度）：2 MB，而 8 KB 和 256 B 的表和读它的循环一起留在 L1。
- **没有 AVX-512 时用标量遍历**：核心以前的做法；NUMBERS_SCALAR。

</details>

## feat-locality-pairs (L2: the generated pair form)

What shipped (code): `[Neighbor]` parameters (`src/EasyEcs.Core/Pairs.cs`), the pair pass (`World.ForPairs`, hidden;
`IPairJob` / `PairRun`, hidden), the generated order, job and vector loop (`SystemGenerator.Pairs.cs`, `PairForm.cs`,
`Kernel.Neighbors.cs`), EECS105-107 (`EasyEcsAnalyzer.Pairs.cs`). Numbers: the final report of the branch (PairsBenchmark
`Generated` / `GeneratedRows` against `GridLookups` and `CoreClusters`).

Docs pass:
- **New guide page `guide/pairs.mdx` + `.zh.mdx`** (or a section of the locality page if one exists by then): when to
  use a pair method (sums over neighbors within a cutoff, the nearest within a range, counts), the form (row
  components, `[Neighbor] in` components, the test first: `if (d2 >= r2) return;` or the rest under `if (d2 < r2)`),
  the distance forms the generator reads (sums of `dx * dx`, `Vector2/3.DistanceSquared`, `(a - b).LengthSquared()`),
  cutoffs (constants, statics, singletons, values, the row's `in` components: a cutoff per row), two kinds of entity
  (`[Neighbor] in Quarry _` as the neighbors' filter, `[With<Hunter>]` on the rows), self included (exclude with
  `d2 == 0` or the entity), ties broken by the entity (lower index wins), the vector loop and when it stays one pair at
  a time (the neighbor's `Entity`, lookups, reductions, structural changes: Kernel's reasons), costs (O(n) boxes per
  pass, a sweep of the clusters when one left its box, the pairs within reach), pitfalls (no Parallel / Slices / change
  filters / [Consume]; the row's write must not be a component the neighbor reads), the "keep the current target"
  pattern (a [ForEach] with a Lookup before the pair method leaves a hint in the target component).
- **Analyzer list**: EECS105 (Error), EECS106 (Warning), EECS107 (Info, no code fix and why) on the analyzer page,
  en + zh.
- **api.mdx complexity rows**: `[Neighbor]` (a pass: O(rows + neighbors) to draw boxes, O(w log w) sweep when a
  cluster left its box, O(pairs within reach + cluster pairs) per pass). `IPairJob`, `PairRun`, `World.ForPairs` are
  EditorBrowsable(Never), so ApiDocs skips them (generated code calls them).
- **example.mdx / BattleServer** (L4, finish phase): the battle server gains a pair method where it searches
  neighbors (the AOI or separation); keep its regions.
- **Migration / README "What's new"**: a new attribute, no breaking change; README "What's new" entry.
- **The public skill** (`skills/easyecs/SKILL.md`): "a neighbor search is a pair method, not a grid": the form and the
  test-first rule.

L4 (moved to the finish phase by the owner, 2026-10-26): what it needs from L2.
- **VoxelServer `ZombieTargetSystem`** (`samples/VoxelServer`, Zombies.cs:51-80, Terrain.cs:371-385 with `ChunkRing`):
  a `Keep` [ForEach] with `Lookup<Position>` / `Lookup<Player>` (the current target kept while within Vision: the
  hint `Distance = -1`), then a pair method over zombies x players, the cutoff `z.Vision * z.Vision` (per row), ties to
  the lower `EntityId.Index` (NeighborTests.TheCurrentTargetIsKeptWhileInSightThroughAHint is that system). The chunk
  ring goes, so does `ChunkRing`'s upkeep where nothing else reads it.
- **VoxelServer `Pickup`** (Items.cs:43-69): a pair with a write to j (the item picked up). Not built: needs
  `[Neighbor] ref` and a pass that runs the pairs of one neighbor in one place (the owner of j). The API fits:
  `PairRun.Others<T>()` returns a ref; EECS105 refuses `[Neighbor] ref` today with its reason. Until then Pickup stays
  as it is, or the pickup is an Emit<T> record (row, item) resolved by a second pass.
- **NearestPlayer** (the samples' nearest): the pair method with a cutoff and a `Nearest` component reset before the
  pass; a query without a range (nearest of everything) is not a pair method (no cutoff: EECS106).
- **Templates / demo**: a pair method in the demo's boids (the separation) if the demo gains one.

Decision record drafts (numbered at merge, after the feat-prefetch-all drafts):
- **D139 The pair form.** Decision: a `[ForEach]` method with `[Neighbor] in` parameters runs per pair within the
  cutoff its first test names; the generator reads the test (distance forms above, cutoff values) and writes the
  order (Morton keys over the axes the test names, cell = the largest reach), the pair lists (clusters of
  `Vector<float>.Count` rows, boxes drawn every pass against the kept boxes widened by an adaptive margin, a sweep of
  the A boxes widened by their clusters' reach against raw B boxes when one left) and the vector loop (a cluster's rows
  in lanes, each neighbor broadcast, the body masked: GROMACS's cluster pair with i in lanes). Why: users write what
  one pair does, as the owner's rule (locality.md 3.4) asks; every pair order per lane is the scalar order (B tables
  in query order, rows in order), so the vector loop and the method per pair are bit for bit equal and the pruning is
  invisible. Considered: lists per entity (Verlet lists; the 4 B per neighbor per entity, and no vector form), the
  8 x 8 kernel over L1's clusters (CoreClusters: needs the clusters sorted every pass and lists cached by the caller; the
  clusters here are the table's own rows in order and need no sort to stay right), a grid of cells (the baseline), a
  code fix from hand-written grids (EECS107 is Info without a fix: the grid's upkeep and its readers go with it, which
  a fix cannot see). Numbers: PairsBenchmark Generated / GeneratedRows vs GridLookups / CoreClusters (final report).
- **D140 Ties and the nearest form.** The pair order is the tables' rows (not entities); a nearest search breaks ties
  by `EntityId.Index` so its result does not depend on the order; "keep the current target" is a hint in the
  component written by a pass before the pair method, never state in the system.

## feat-pairs-2 (L2 leftovers: backlog item 1)

What shipped (code), each with its tests and the analyzer's messages:
- **Writes of the neighbor** (`[Neighbor] ref T`, 1cdc80f): the pairs run one at a time in the order of the tables'
  rows (a pickup), the written neighbor rows stamped (`PairRun.StampOther`); EECS105 reports a neighbor component the
  row reads or writes and one taken twice; EECS025 a `[Neighbor] ref` only read (fix: `in`).
- **Parallel pair passes** (`[ForEach(Parallel = true)]` on a pair method that writes no neighbor, d105c5e):
  `World.ForPairsParallel`, chunks of whole words of clusters, bit for bit the serial pass; `IPairJob.Stamp` stamps
  the rows after the chunks. PairsBenchmark `GeneratedParallel` 1.7-2.6x the serial pass, 0 B (92e71e9).
- **EECS107's fix** (356aa40, ef5f588): a mechanical neighbor search (candidate ids through `Lookup<T>`, or a walk of
  `world.Read` / `Query` of other entities) rewritten as the pair method; Warning with the fix when mechanical, Info
  otherwise.
- **Lists of each row's neighbors** (Verlet lists, 0cad100, b355f64): a run may carry each row's list instead of its
  cluster's ranges, picked by the estimated cost (`PairState.Lists`, internal, Auto by default); bit for bit the
  ranges.
- **`Reduce<T>` and `Mut<T>` in pair methods** (VoxelServer GA1, 93264a3): `ref Reduce<T>` (so `[Accumulate]`
  totals) serial and parallel, each chunk's copy of the job combined in chunk order (`IPairJob.Merges` / `Merge`);
  a component of the row as `Mut<T>` (`Mut<DynamicBuffer<T>>` included) next to `[Neighbor] ref` writes, stamping
  only the rows it writes (`PairRun.MutRows`); a parallel pair method with a `Mut<T>` is EECS105 (take it by `ref`, or
  drop Parallel). Both keep the pairs one at a time (EECS055 names the parameter).
- **Renames** (owner-approved): the ordered table's block is a cluster (`RowClusters`, `ClusterPair`, `world.Clusters<T>()`,
  `EntitySpan.Clusters`, `RowOrder.DefaultClusterSize`); the pair run's window is a cluster and
  its reach the cutoff (`PairRun.ClusterSize`, `CutoffPerRow`, `CutoffSquared`, `CutoffsSquared`, `Cutoff`).

Measured and not shipped (numbers in `locality.md` 6.6):
- Lookup gathers under `SimdMode.Auto` (backlog item 1, the owner's 2026-10-26 call): still slower in several cases
  with L1's order; they stay on request (D131 stands).
- The hoist of the run's end into a local in the generated pair loops: a wash in two rounds; the 5-8 % serial
  regression on sparse maps seen once is within the job's noise in four later runs.
- The prefetch of the neighbors' clusters `PairRun.PrefetchDistance` (4) ahead behind a `CacheSizes` gate: slower
  in every case of two rounds, the table past the 260 MiB last-level cache (16M rows, 512 MB) included; built in a
  scratch worktree only, never on the branch.
- The nearest search through a flat grid of clusters in rings (the prototype of 6.2): 1.4-1.5x the box tree on the
  spread map, 1.1-1.2x slower packed; the tree stays, and the nearest form is the pair method anyway.

Migration (the docs pass; pre-1.0 renames, no `[Obsolete]` aliases): `RowBlocks` -> `RowClusters`,
`BlockPair` -> `ClusterPair`, `world.Blocks<T>()` -> `world.Clusters<T>()`, `EntitySpan.Blocks` ->
`EntitySpan.Clusters`, `RowBlocks.Pairs(reach, ...)` -> `RowClusters.Pairs(cutoff, ...)`, `RowOrder.DefaultBlockRows`
-> `DefaultClusterSize` (and the internal `OrderRows(..., blockRows)` -> `clusterSize`); the hidden `PairRun` members the generator calls (window -> cluster,
reach -> cutoff) need no user change. The migration guide's section of the coming version, en + zh, and a changelog
"needs code changes" entry linking it. `[GaussSeidel]` (formerly proposed as `[RedBlack]`) is the name to use in
these notes and in any later page; nothing ships under it yet.

Docs pass (guide/pairs.mdx + zh, the section listed under feat-locality-pairs, extended):
- Writes of the neighbor: `[Neighbor] ref` for a pickup, the order (tables' rows), what EECS105 refuses and why, the
  stamps of the written neighbors; no Parallel with it.
- Parallel pair methods: when they pay (the parallel pass 1.7-2.6x), bit for bit the serial pass, no `[Neighbor]
  ref`, no `Mut<T>`.
- Results of a pair pass: `ref Reduce<T>` and `[Accumulate]` totals in a pair method; parallel results are combined in
  chunk order, so an associative reduction gives the serial result; a float sum in parallel depends on the chunking
  (as in other passes: FloatMode / ChunkRows, D125).
- `Mut<T>` of the row in a pair method: only the rows written are stamped (against `ref`, which stamps every row the
  pass runs for); not in parallel.
- The vector loop stays one pair at a time with a `[Neighbor] ref`, the neighbor's `Entity`, `Reduce<T>` or `Mut<T>`
  (EECS055's reasons).
- Lists of neighbors: none to write; what the pass picks and why (ranges for the vector loop, lists where one pair at
  a time runs on sparse maps), no setting.
- analyzer.mdx + zh: EECS105's new cases (neighbor read and written, taken twice, Parallel with `[Neighbor] ref` or
  `Mut<T>`), EECS107 Warning with its fix (the two searches it rewrites) or Info, EECS025 on a `[Neighbor] ref` only
  read.
- api.mdx: no new public user-facing name (the new members are on EditorBrowsable(Never) types the generator calls;
  ApiDocs skips them).
- The public skill: a neighbor search is a pair method; writes of the neighbor, results and `Mut<T>` in it.
- VoxelServer `Pickup` (L4, finish phase): now buildable as a pair method writing `[Neighbor] ref` the item, the
  inventory as `Mut<DynamicBuffer<ItemStack>>` (NeighborTests.AMutOfTheRowStampsOnlyTheRowsItWrites is that shape).

Decision record drafts (numbered at merge, after D139 / D140):
- **Pair methods write their neighbor one pair at a time.** Decision: `[Neighbor] ref T` runs the pairs in the order
  of the tables' rows, never in lanes or chunks, and EECS105 refuses what would make a row's view depend on that
  order. Why: a pickup has to decide who takes an item; the rows' order is the only order both forms share.
  Considered: an Emit<T> record resolved by a second pass (more code for users, a frame of latency), lanes with
  conflict detection (CUDA atomics: no ordering, nothing bit for bit).
- **Parallel pair passes split the rows, not the pairs.** Decision: chunks of whole 64-row words of clusters, every
  chunk reading all neighbors, each row written by its own chunk only; stamps after the chunks. Why: bit for bit the
  serial pass with no atomics, no write conflicts. Considered: Newton's third law (half the pairs, a write to j:
  conflicts and a different summation order), a chunk per pair of tables (too few chunks). Numbers: 92e71e9.
- **Lists of neighbors are picked by their cost per pass.** Decision: ranges by default where the vector loop runs;
  each row's list (Verlet) where one pair at a time runs and the list's entries cost less than the ranges'
  candidates, estimated on 64 sampled clusters every 16th draw. Why: the 8-lane loop tests a cluster's neighbors in
  one step, so lists (which hold rows, not clusters) never pay there; one pair at a time tests every candidate, so
  lists pay on sparse maps. Numbers: `locality.md` 6.6. Considered: lists always (3-5x slower than the 8-lane loop),
  every cluster counted at every draw (27 % of the pass at 100k), a setting for users (the pass knows its costs).
- **Results of a parallel pair pass are combined in chunk order.** Decision: each chunk runs a copy of the job made
  when the pass began and the copies are combined in chunk order after each pair of tables. Why: the same rule as
  other passes' reductions (D125): the result depends on the code and the layout, never on the thread count.
- **The pair pass does not prefetch the neighbors' clusters.** Decision: no prefetch of the clusters ahead, on any
  gate. Why: the neighbors' clusters of a row cluster are the clusters next to it in the sorted table, which the
  hardware prefetchers already stream; software prefetches only add instructions. Numbers (PairPrefetchBenchmark, ms,
  rounds 1 / 2, Off against On): 1M crowd 105 / 96.5 against 125 / 122, 4M crowd 406 / 393 against 480 / 490, 16M
  crowd (past the last-level cache) 1,620 against 1,947 (round 2), sparse alike. Considered: the L2 gate, the last-level
  gate, another distance (each case slower at the distance tried; the gap does not shrink with size).
- **Lookup gathers stay on request under `SimdMode.Auto`** (D131 measured again, with L1's ordered rows). Numbers
  (LookupGatherBenchmark, one round, the vector loop's time over the rows', 1k / 100k / 1M rows, three bodies): one
  table 0.48-0.85, four tables 0.48-0.88, 64 tables 0.93-1.53 (1.19-1.53 at 1M), 1,000 tables 0.90-1.43 (1.31-1.43 at
  1M), dead targets 0.61-1.24 (slower at 1k), presence bits 0.89-1.32 (1.26-1.32 at 1M); LookupOrder's unordered 1M
  gather 1.49x slower than per row, the ordered one 1.9x faster. A case slower, so the owner's condition (no case slower) is not met.

Names for the owner (new public members, all on EditorBrowsable(Never) types the generator calls): `PairRun.First`,
`End`, `StampOther`, `Listed`, `Near`, `MutRows`; `IPairJob.Stamp`, `Width`, `Merges`, `Merge<TSelf>`;
`World.ForPairsParallel`; EECS107's fix title "Make it a pair method".

## feat-prefetch-all: every chase the generated code can prefetch, SVE's prefetch on ARM64, the core's id lists

Numbers: `borrowed-ideas.md` 5, item 1, "feat-prefetch-all" (BenchmarkDotNet default job, base 63771f4 against the
branch as frozen snapshots, two rounds; this machine's last-level cache is 260 MiB, so only the 16M worlds are past
it; all 0 B).

Decision record drafts (en + zh, the next free D numbers at merge: D139 onward if D137 / D138 keep theirs):
- **Every chase is prefetched past the last-level cache** (extends the feat-prefetch-gate record). The twin job of a
  method's row form now also covers: index probes at a key of an `in` component (`lookup.TryFind(x.F, ...)`,
  `Find`, `Count`; the index's slot, or the hash's bucket 16 rows ahead and its entry 8 ahead, a relation key's slot
  then its list), reads at the ids of an `in` buffer's elements (`foreach (var link in links) lookup[link.Target]`,
  the indexer: each element's record 16 ahead, its row 8 ahead), parallel row methods (the twin is a batch job per
  chunk through `ForEachBatchParallel`, which cuts the same chunks as `ForEachParallel`; methods with `Maybe` /
  `Mut` keep the per-row form, unprefetched), walks two methods share (fusion within a system and the pair of two
  systems: a twin walk of the members' twins, picked before the size check) and lambda passes (the interceptor picks
  the twin as a method's pass does). The gate of a probe is the index's own memory (slots, buckets, entries, lists)
  past the cache (`LookupExtensions.Probes`), read once per pass with the dictionary's arrays. Numbers at 16M (base
  / head): probes FindLogin 979 / 700 and 1020 / 688 ms (1.40x, 1.48x), the lambda 960 / 705 and 988 / 719 ms (1.36x, 1.37x), the parallel probe 287 / 206 and 287 / 215 ms (1.39x, 1.33x), a shared key's
  Count 592 / 570 and 665 / 561 ms (1.04x, 1.19x); buffer chases 2090 / 1460 ms and 2095 / 1443 ms (1.43x, 1.45x); a fused walk 1071 / 606 and
  1041 / 594 ms (1.77x, 1.75x); a parallel chase 285 / 174 and 260 / 174 ms (1.64x, 1.49x). In the cache (1k, 1M:
  gate off) equal within the run-to-run spread (head Auto against head Off at 1M: within 1-7 %, the same code).
  Considered: a third probe stage for a key many entities share (its list of ids: finding its head is a second
  lookup per row, 2.4x slower at 16M, dropped); the parallel per-row form prefetching (ForEachParallel hands one row
  per call: no rows ahead to read); a twin per fused method run inside today's walk (a walk interleaves methods per
  row or per block of 256: the twin walk is one job, so prefetches stay a fixed distance ahead).
- **ARM64: SVE's prefetch** (`Prefetching.Line`): `Sve.Prefetch8Bit` with a one-element predicate
  (`CreateTrueMaskByte(VectorCount1)`) where `Sve.IsSupported` (Graviton 3/4, Azure Cobalt 100, Neoverse V1/N2/V2;
  GitHub's ubuntu-24.04-arm runners). The API is `[Experimental("SYSLIB5003")]` in .NET 10: one call, behind the
  JIT-time `IsSupported`, the diagnostic suppressed there only; x86 keeps `PREFETCHT0`, other ARM64 (Apple silicon:
  SVE in streaming mode only) issues nothing and never reads the cache size. Linux ARM64 reads sysfs (the largest
  `index*/size`; glibc's sysconf has no L3 there). Tests: the CI arm64 job (PrefetchTests on SVE
  hardware, its `ThePrefetchIsTheMachines` line). NOT YET PROVEN: under qemu here the SVE run (neoverse-n2) crashed
  with SIGSEGV in the first PrefetchTests test, and the non-SVE runs could not finish (a stack overflow with SVE off on
  n2, no libssl in the aarch64 sysroot on n1, which passed the first three tests), so the arm64 job is the first real
  test of the PRFB path; record its result before this ships. Considered: a native PRFM stub (a binary per RID, a call
  per prefetch), waiting for a PRFM intrinsic (none in .NET 10).
- **ARM64 without SVE: loads issued ahead, dropped** (LookupChaseLoadAheadBenchmark, Locality/Chase.cs
  `LoadAhead`): the record of row i + k loaded (Has) into a ring, the row of row i + k / 2 touched through it, both
  consumed by row i. On this x86 machine 1.70-1.75x slower than the plain rows at 16M (1600-1643 ms against 930-984;
  prefetching 667-777) and 4-5x at 1k: a load that misses holds retirement, and the public API makes each stage a
  whole lookup. The owner's M4 (2026-10-27) settles it: slower at every size, 1.7-1.9x at 16M (783-966 ms against
  451-504), 2.3-3.0x at 1M (31-36 against 12.6-16.5 ms), 4.4-7.2x at 1k (8.9-13.6 against 1.9-2.2 us). Decision record
  in the docs pass.
- **macOS**: Apple silicon has no prefetch .NET issues, so its gates are off without a read; were one gated there,
  `hw.perflevel0.l2cachesize` (the performance cluster's L2, 16 MB on an M4) understates the cache the cores share
  (the system level cache is not exposed): a gate would open early, never late. Intel Macs read `hw.l3cachesize`.
- **The core's id lists are prefetched past the cache**: `World.DestroyEntities(ReadOnlySpan<Entity>)` prefetches the
  record 16 ids ahead when the world's records and a row line each are past the cache (the L2 since a gate per form)
  (`LookupLanes.PrefetchesIds`). DestroyAndCreate (a sixteenth of the world) at 16M 211 / 157 and 208 / 162 ms (1.34x,
  1.28x), at 1M (gate off) 3.80 / 3.86 and 3.95 / 4.04 ms; forced on at 1M 3.02 against 3.96: past the L2 since
  ("feat-prefetch-gate: a gate per form").
- **Fix: a lambda pass finding a non-unique indexed component by key threw** ("This Lookup<T> holds no index"): the
  generator looked for the key calls in a method's body only, so a lambda's `Lookup<T>` got no index built before
  the pass. `Symbols.LookupKey` now reads the lambda's body too (LambdaPassTests.ALambdaFindsByAKeyOfANonUniqueIndex).

Docs to-dos (docs pass):
- guide page of `Lookup<T>` and the index page: "past the last-level cache, a pass prefetches the targets of the
  rows ahead: ids in an `in` component or an `in` buffer's elements, keys of an `in` component (TryFind / Find /
  Count), parallel or not, fused or not, lambda passes too; nothing to write; x86 and ARM64 with SVE"; replace the
  feat-prefetch-gate to-do's list of forms that do not qualify (keyed lookups, parallel row methods, lambda passes,
  fused walks all qualify now; methods with `Maybe` / `Mut` in parallel and vector loops' probes do not); en + zh.
- relations page: links' targets read in a pass are prefetched past the cache (the buffer form); en + zh.
- parallel page and fusion page: one sentence each (the parallel twin keeps the chunks; a fused walk's twin);
  en + zh.
- introspection page: DescribeSchedule's "lookups prefetched past the last-level cache" now appears on probing,
  buffer-chasing, parallel and fused methods; en + zh.
- performance / why-fast pages: the 16M numbers through bench.json (`tools/bench.sh ProbePrefetchBenchmark
  ChaseFormsPrefetchBenchmark LookupChaseGateBenchmark --sync`), a BenchTable of Auto / Off / On per form.
- ARM64 notes (performance, faq): SVE machines prefetch, Apple silicon does not (loads ahead pending the M4).
- public skill: "keys and ids a pass reads other entities by belong in `in` components or `in` buffers: they are
  prefetched when the world outgrows the cache".
- changelog + README "What's new": Performance (prefetched probes, buffer chases, parallel, fused, lambda; SVE);
  Fixes (the lambda key lookup).
- `api.mdx`: nothing; the new members are hidden for generated code (`LookupExtensions.Probes`,
  `LookupExtensions.PrefetchKey`: names for the owner to approve).

## feat-prefetch-gate: a gate per form

Decision record draft (decisions.mdx + .zh.mdx, next to the prefetch gate's record): **A prefetch gate per form.**
Decision: each prefetching form reads the cache it pays past. The row form's chases of other entities (one thread,
parallel, fused walks, a buffer's elements, lambda passes) and `World.DestroyEntities`' id list prefetch once their
memory leaves one core's L2; index probes (on one thread and in parallel) and the vector loops' chases only past the
last-level cache. The L2 is read like the last-level cache (Linux sysfs level-2 index then sysconf, Windows
`GetLogicalProcessorInformationEx` level 2, macOS `hw.perflevel0.l2cachesize` / `hw.l2cachesize`), only where a prefetch
exists; an unknown L2 uses the last-level gate. Why: between the L2 and the LLC (262,144 and 1M entities on a 2 MiB L2
/ 260 MiB L3 x86) the row chases run 1.20-1.36x faster, the fused walk 1.44-2.0x, DestroyEntities 1.22-1.35x, the
buffer chase 1.04-1.26x; inside the L2 the gate keeps today's code (forced on costs 1.12-1.6x). Considered: one gate on
the L2 for every form (probes 1.13-1.6x slower forced on at 262,144, the vector loop 1.04-2.1x); parallel probes on the
L2 (the owner's first list: a wash at 262,144, 1.16x slower at 524,288, faster only at 1M: kept on the last level); a
multiple of the L2 (no form needed one: each that pays, pays from the first size past it). Numbers:
`borrowed-ideas.md` item 1, "A gate per form".

Docs to-dos (docs pass, en + zh):
- guide page of `Lookup<T>` (the prefetch section written for feat-prefetch-all): "past the L2 for chases of other
  entities, past the last-level cache for keyed lookups and vector loops"; the "What it costs" line of each.
- performance / why-fast: the per-form BenchTable gains the 262,144 size (`tools/bench.sh ProbePrefetchBenchmark
  ChaseFormsPrefetchBenchmark IdListPrefetchBenchmark LookupChaseGateBenchmark --sync`; the classes' default sizes are
  now 1k / 262,144 / 1M / 16M).
- introspection page: DescribeSchedule's scalar line reads "lookups prefetched past the cache" (the form decides which).
- faq (ARM64 / other machines): the gate reads the L2 too; unknown L2 = the last-level gate.
- `api.mdx`: nothing (the new `Lookup<T>.PrefetchesPastL2` is hidden for generated code, a name for the owner to see).

## feat-generator-gaps (G0, G4, G5, G6; G7 not built)

Pages to write in the docs pass (en + zh each; zh terms: 块 = a parallel chunk only, never 区块):

- **guide/reductions (Reduce<T>)**: a section "A total across passes: `[Accumulate]`". When: a singleton every pass adds
  to (kills this round, bytes sent). Form: `[Accumulate, Sum] public partial struct Stats : ISingletonComponent { long
  Kills; long Hits; [Max] int Longest; }` and `ref Reduce<Stats>`; the struct-level `[Sum]` for the fields without an
  attribute. Order: the world's value first, then the pass (a non-commutative `Combine` keeps run order). Costs:
  AccumulateBenchmark (100k rows: hand-written `ref Singleton` scalar 232.6 us, `[Accumulate]` 8.3 us, parallel 6.6 us,
  0 B). Pitfall: without `[Accumulate]` the pass replaces the value. EECS086's total case (Info until the singleton
  reduces, then a Warning whose fix takes it as `Reduce<T>` and declares `[Accumulate]`).
- **guide/emit (Emit<T>)**: a section "Handling the records: `[Drain]`". Form: `[Drain] static void
  Send(in EntityUpdate u, ref Stats s, Lookup<Viewers> v, World w)`; runs in declaration order inside the system,
  an entry point `Owner.Method(world, values...)` outside; records in emitted order, then cleared; a pass that throws
  clears nothing; an `Emit<T>` of the same record type is refused (EECS108). Costs: DrainBenchmark (both tally into
  a singleton: 100k records 284.1 us by hand, 278.9 us generated; 1k 2.44 / 2.52 us; 0 B). EECS109 (Info) and its fix
  (a system's OnUpdate that is only the walk becomes the method).
- **guide/systems#vector-loops**: enums are lanes (their integer's: compare, convert, set to a constant), and a bool
  component of one field (one byte per row, read as a mask, written as 0 or 1); a bool among several fields keeps the
  method scalar with the reason "is a bool among N fields: a vector loop takes a bool in a component of its own".
  Costs: FlagLanesBenchmark, 100k rows, SimdMode.Off twin against the loop: enum 120.6 -> 3.07 us, bool written
  110.2 -> 3.08 us, bool read 80.6 -> 2.93 us.
- **guide/dynamic-buffers**: `world.ReserveBuffers(prefab, count)` next to `FillPool`: a list per instance and member
  holding the buffer; lists of `MaxPooledLength` elements (set it first), process-wide, not cumulative across prefabs
  sharing a type; when not to (a buffer that stays short: count x MaxPooledLength of memory).
- **guide/analyzer**: EECS108 (Error) and EECS109 (Info, fix) entries; EECS086's total case; EECS055's new bool reason.
- **api.mdx**: done on the branch (rows for `[Accumulate]`, `[Drain]`, `ReserveBuffers`, the record pass).
- **migration / changelog / README "What's new"**: New: `[Accumulate]`, struct-level `[Sum]`..`[Any]`,
  `[Drain]`, `World.ReserveBuffers`, enum and bool lanes. No breaking change (EECS055's message for a component
  with a bool among several fields changed).

- **BattleServer.cs (example.mdx)**: EECS086's total case reports Shoot, SendFire and SendFireOut (they add to the
  `Battle` singleton row by row). Move `Battle`'s counters to `[Accumulate, Sum]` taken as `ref Reduce<Battle>` (Tick
  stays assigned by its own system), update the page's walkthrough, then drop the EECS086 exception in
  GeneratorBenchmark.Analyzed. Not done on this branch (BattleServer.cs was out of its scope).

What each form replaces in the samples (VoxelServer rewrite, `voxel-rewrite.md` section 3; samples were not edited here):

- G5: the four systems that reset nothing and add to a singleton row by row (`ref Totals t` then `t.X += ...`): each
  becomes `ref Reduce<Totals>` plus `[Accumulate]` on the singleton (gets a vector loop, may run parallel).
- G6: the three get / loop / clear blocks over `Emitted<T>` (VoxelServer SendPackets and two more): each becomes one
  `[Drain]` method holding the loop's body (about 4 lines each).
- G0: methods scalar only for an enum or a single-bool component now vectorize; Play (Body.OnGround), Expose and Out
  (Exposure.Sunlit) stay scalar until those bools get components of their own (the new reason says so).
- G4: Program.cs `DynamicBuffer<Block / Fluid / Surface / ItemStack>.FillPool(...)` become
  `world.ReserveBuffers(prefabs.Chunk, capacity.Chunks)` and `world.ReserveBuffers(prefabs.Player, capacity.Players)`;
  NextFluid keeps its own FillPool (Flowing, fewer than chunks), and the MaxPooledLength lines stay (process-wide).

Decision-record drafts (for decisions.mdx + .zh.mdx):

- **[Accumulate]: a total is a reduction combined into the world's value.** Why: the hand-written total is a
  `ref Singleton` every row writes, so the pass is scalar and on one thread; as a reduction it gets a vector loop and
  chunks, and one Combine more per pass. Considered: a new parameter type (`Total<T>`), a second interface; rejected:
  the reduction already is the right form, only its end differs. Numbers: AccumulateBenchmark above.
- **[Drain]: the records' walk is generated, one thread, the job copied to a local.** Why: get / loop / clear is
  written once per record type; the job's refs reloaded per record cost 2x (first measurement: 286 vs 124 us when the
  job was walked through its ref), a local copy keeps them in registers (equal). Considered: a parallel record pass
  (order matters to most consumers: dropped), records as entities (structural cost per record: dropped).
- **Enum and bool lanes.** An enum is its integer (enumeration conversions are the integer's); a bool component of one
  field is a byte column read as a mask (`GreaterThan(bytes, 0)`) and written as `-mask` (0 or 1), which is what C#
  stores. A bool among several fields would need a shuffle of 1-byte lanes, which the narrow loops do not have.
- **ReserveBuffers is a call of its own, not part of Reserve(prefab).** Filling lists of MaxPooledLength elements for
  every instance can cost count x 4 KB per buffer type by default; Reserve stays cheap and the caller opts in.
- **G7 not built** (owner-review): a keyed read needs both lookups anyway.

## feat-microarch (M0-M3, `docs/plans/microarch.md` section 5)

What shipped (code): two accumulator sets for floating-point reductions in the loop over whole vectors
(`SystemGenerator.Paired`, `Kernel.Code.Chains`); the loops'
tables initialized by a module initializer (`Interleaved.InitializeTables`, internal) so a runner compiled at Tier1
before its table had holes reads them as constants. Tools: `tools/loopbound.sh` (llvm-mca over the Tier1 loops). No new public name.

Decision drafts (numbers in microarch.md, "Numbers"):
- **Floating-point reductions run two vectors per iteration into two sets of accumulators.** Why: a float `+`, `Min`
  or `Max` is a 3-4 cycle chain per vector, the only chain M0 found in the generated loops; two sets halve it
  (llvm-mca, BDN). Considered: four sets (no better than two by the model: the loop becomes port bound), integer
  reductions (a 1-cycle chain under a port bound of 1.5+: nothing to win), unrolling every loop (under 10 % by the
  model, the body twice against the 32 locals). Exactness: Min / Max exact in any order; a float sum only under
  FloatMode.Fast, whose order is the code's shape, never the thread count (D68, D125).
- **The tables of the vector loops are initialized when EasyEcs loads.** Why: Tier1 code is compiled once; a runner
  whose first calls ran no masked word was compiled before its tables' class was initialized and called the
  class-initialization helper per vector forever after (LateHoles numbers). Considered: vectors of literals (the AOT
  attempt reverted for the M4's numbers, backlog), reading the table into a local before the loop (more locals,
  still one helper call per run). Native AOT unchanged (its own item).
- **Dropped:** the masked skip on the word's bits (fewer uops by llvm-mca, BattleServer 1.09-1.10x slower),
  unrolling loops without a chain, hoisting the broadcast reloads, branchless scalar rows (the JIT keeps
  a branch where PGO sees it biased; the generator does not rewrite users' statements), streaming stores and the
  prefetcher-stream split (see microarch.md for the evidence of each).

Docs pass:
- performance / why-fast pages: "a floating-point reduction keeps two chains in flight" next to the reductions'
  lanes; the LateHoles and Spread / Energy numbers through bench.json (`tools/bench.sh KernelBenchmark
  LateHolesBenchmark --sync`).
- reductions guide page (`Reduce<T>`): FloatMode.Fast's sum order now also depends on whether a run has two whole
  vectors (still fixed by the code and the table's layout, never the thread count); Min / Max unchanged and exact;
  en + zh.
- `audit-vector-loops` skill: `tools/loopbound.sh` as the way to read a loop's bound
  (the coordinator edits skills).

## Decided by the owner (2026-10-27): decision records for the docs pass

- Declined generator gaps of the VoxelServer plan (owner agreed): G3 (`[Prefab]` with buffer and absent fields: adds
  lines in VoxelServer), G7 (a keyed read of the found entity: the index and the value live on two components, two
  lookups either way, no line saved), G11 (a helper for the steady-state allocation check: one sample's test
  plumbing). The rule they were judged by: a gap is built when it saves users lines or thought (`audit-generator`).
- G10 and gated streaming stores are code items (backlog "Next" 9, 10); EECS086 / EECS109 / EECS055's bool reason
  become Warnings with the VoxelServer rewrite.

## Vocabulary renames (owner-approved naming popups, 2026-10-27; code applied)

Each needs a migration entry (old -> new), a changelog line, a README "What's new" line and a decision record that
names the field each term comes from (audit-naming, "The reference design's vocabulary"):
- `QueryOptions.ParallelChunkRows` -> `MaxChunkRows` (released in 4.7: migration entry required), and the
  `chunkRows` parameter of `world.ParallelFor` -> `chunkSize` (OpenMP's `schedule(static, chunk_size)`).
- `TileReads` -> `TileStencil` (the cells a tile method reads: a stencil, as in stencil codes).
- `TileBoundary.Fill` -> `TileBoundary.Border`, and `Pad(from, to, fill, mask)` -> `Pad(from, to, border, mask)`
  (a Dirichlet boundary; Clamp and Mirror unchanged).
- `Lanes.Bits` -> `Lanes.Ballot` (CUDA's `__ballot_sync`: the lane mask as bits).
- Hidden, no migration: `Lookup<T>.Prefetches` -> `PrefetchesPastLastLevel` (beside `PrefetchesPastL2`).
- zh: Slice is 分帧 everywhere (was 分片, which reads as sharding); glossary row in `tools/ApiDocs/zh/README.md`.
- Still to come: block -> cluster and `Pairs(reach)` -> `Pairs(cutoff)` (feat-pairs-2), `[RedBlack]` ->
  `[GaussSeidel]` (feat-zombie-field), `Pad()` -> `ExchangeHalo()` with backlog item 12a.

## Terminology audit (2026-10-27, read-only agent): docs-pass items
- Glossary: add each term's source field (vector loop: Burst/ISPC; struct job: Unity IJob*; batch method: IJobChunk;
  `Mut<T>`/`SetIfChanged`: Bevy `Mut`/`set_if_neq`; change tick: Bevy; tracked: Unity change versions; optional:
  Bevy `Option<&T>`/Flecs `?`; tombstone: databases/LSM, and say compaction once; stage: Bevy sync points; access set:
  Bevy `Access`; prefab: Unity/Flecs; dynamic buffer/cleanup/disabled: Unity; relation/ChildOf: Flecs; group: Flecs
  `group_by`; slice: time slicing). New entries: stencil, lanes/ballot, fusion, pair search (cutoff, neighbor list),
  ordered tables and clusters (Morton, bounding box, margin), `[Accumulate]`, `IDirectMapped`.
- zh pages: 截断距离 -> 截断半径 (7 uses) after feat-pairs-2 merges (the glossary row says 截断半径).
- EECS093 title/message: "...that are not fused into one walk" (the setting is `FusionMode`).
- `OrderBy.Margin` doc comment: "half of LAMMPS's skin" (both boxes widen by it); keep the name.

## Owner answers 2026-10-27 (terminology popup)
- `[ForEach(ChunkRows)]` / `chunkRows:` -> `ChunkSize` / `chunkSize` (applied, with a CS0246 / CS1739 migration
  code fix): migration, changelog, README, decision record (OpenMP's chunk size), alongside `MaxChunkRows`.
- `IDirectIndexed<TKey>` -> `IDirectMapped<TKey>` (a direct-mapped cache: collisions fall back to the hash).
- `FloatMode.Exact` stays; its doc comment says "Burst's Strict + Deterministic".
- `Lanes.RowNumbers` -> `Lanes.Selection`, documented as the selection vector of DuckDB / Vectorwise.
- grids page: the shader/texture analogy stays in prose, names (headings, seoTitle, `Tile.Texels()`) take the HPC
  terms (stencil, tile, halo).
- fix-ci merged with Restore 66.6 -> 84.4 us, Rollback 141 -> 177 us (correctness: 0 B steady state); the owner
  wants that cost won back later: backlog item.

## fix-ci merged (ae9d525): docs-pass leftovers from its report
- decisions.mdx still shows the hook attributes; changelog still says `[IndexKey]`; some prose keeps the old WithAny
  and GroupBy forms; EECS090 has no docs entry; copy-on-write snapshots have no decision record; snapshots.mdx still
  says Capture copies buffer elements.
- EECS055 gives circular "fixable" advice for a 2-field Emit record and a conditional Remove of an `in` component
  (Info rather than Warning? owner); EECS033 suggests members a fence compiled alone does not get.
- grids: headings and seoTitle now say stencil / ghost cells (owner: the shader analogy stays in prose). The internal
  `TileMemory.Texels()` and its `texels` locals become the halo-padded buffer's name with backlog item 12a, which
  rewrites that code (`Pad()` -> `ExchangeHalo()`); renaming them now would only conflict with 12a and the zombie field.
- systems.mdx/.zh link EECS103 to D134 until the analyzer page has its EECS103 entry (then link #eecs103 again).

## feat-zombie-field (backlog item 8: VoxelServer zombies follow a distance field)

What shipped: `samples/VoxelServer` Components/Scent.cs (Ground, Footing, Scent, Trail, Hunt, ScentStep, Spreading,
Red / Black, Footprint) and Systems/Scent/ (FootingSystem, ScentWakeSystem + FootprintSystem, HuntSystem,
ScentStepSystem<TColor>, ScentSettleSystem<TColor>); ZombieChaseSystem reads one Trail byte;
tests/EasyEcs.UnitTest/VoxelServerTests.cs. No src/ change.

- **CI: docs/content/samples/voxel-server.mdx and .zh.mdx `## Output [#output]` blocks** must take the README's new
  expected output (check-sample.sh compares them; it fails on this branch until the docs pass, since docs pages were
  out of this branch's scope).
- voxel-server.mdx + zh, "Zombies and death" (or a new section "The scent [#scent]" after Water): the distance field
  as a tile shader beside the water's, what it teaches that the water does not:
  - a 2D tile on the entity of a 3D one: `Ground` (16 x 16 x 1, the same key as `Chunk`, `IDirectIndexed`) because a
    tile's buffer holds Width * Depth * Height cells, so a height map cannot be a tile of the 3D chunk;
  - a field that is only read settled: the step settles its own tile in place (Gauss-Seidel rows, the row scanned east
    and west in doublings of 1, 2, 4, 8 columns gated by the walkable runs) until a sweep changes nothing, then sleeps
    through ChangedEdges like the water;
  - red / black chunks (checkerboard by parity, a presence bit each, all chunks in one table): a generic system per
    color (`ScentStepSystem<Red>`, `<Black>`), the color as an `in TColor` parameter because an attribute cannot name a
    type parameter (CS8968); the black half steps from the red half's new edges, the front crosses two chunks an update;
  - step + commit (NextScent, copied by the settle) like the water. The in-place form (no NextScent, the step writes
    Scent behind an EECS059 suppression: the checkerboard keeps every read off the rows the pass writes) gave the
    same output and the same time, but allocated in 2 of 4 rounds at 2,000 players (312 B, 672 B per 100 ticks), so
    it was dropped until `[ForEach, GaussSeidel]` ships it (numbers below);
  - cold start: the chunk's own columns from nothing each step, not its last field (a stale peak counts to infinity:
    7.5 sweeps a step against 2.7, with a long tail to the cap);
  - seeds from an index (`Footprint`, `[LookupKey] ChunkPos`): the players of a chunk without walking InChunk;
  - Trail, a flow field: the step writes the side each column's zombie walks toward, so the chase reads one byte, not five
    columns and their footing (still a chunk lookup per zombie: the chase costs about twice the straight one);
  - sleeping by day (`Hunt`), waking on a footing change, a player crossing a column, or a neighbor's changed edge;
  - Minecraft framing: a mob path steps up one block, drops at most 3 (MaxDrop), never enters water; the field reaches
    as far as the zombies' Vision (35 steps, in walking steps: a diamond, not a circle);
  - the pools: the field's buffers sized on the owner thread at generation, FillPool twice the chunks (helpers park lists
    of their own; a snapshot restore shares every buffer until written, so the steady check's replay writes them all).
- grids.mdx + zh: three patterns the page lacks: (1) a field read only when settled, settled inside the tile per step
  (a loop over the padded texture, copied to a stackalloc buffer first since `PaddedTile` is read-only); (2) red-black
  tiles by hand today (generic system per color, tag parameter), pointing to `[ForEach, GaussSeidel]` once it ships;
  (3) a 2D tile beside a 3D one on the same entity (its own key component). Also: `Footing`-style precomputed masks
  (the rule once per step, not per sweep), and `ChangedEdges` before writing the buffer to skip unchanged writes.
- performance pages: none (the sample's TickBenchmark is not on the site).
- decisions.mdx + zh (sample-level, a short record or a line in the VoxelServer record): in-place red-black over
  Jacobi step + commit for a settled field (equal time, one buffer and one copy fewer); cold start over warm start;
  Trail over the gradient; ScentEvery 4 kept (8 measured no faster).

Numbers (TickBenchmark, 100 ticks from tick 900, the second night; Xeon VM shared with other agents' benchmarks,
load average 2.8 to 22 at a class's start, StdDev 10-40 %: only differences past about 15 % are real). Mean of the
rounds, ms per 100 ticks, every row 0 B unless said:

| Form | rounds | 50 players | 2,000 players |
|---|---|---|---|
| before (straight chase) | 5 | 44.4 (34.4-57.1) | 110.3 (105.4-116.0) |
| step + commit (shipped) | 4 | 58.7 (56.3-63.2) | 132.2 (119.9-142.8) |
| in place, red-black tiles | 3 | 55.6 (47.1-62.1) | 125.9 (116.6-136.3), 672 B in one round |
| in place, ScentEvery 8 | 2 | 58.7 (54.0-63.4) | 126.0 (120.4-131.5) |

The field costs about 0.13 ms a tick at 50 players and 0.2 ms at 2,000 (of the 50 ms budget). Part of it may be the game
changing rather than the field's systems: zombies hunt within 35 walking steps (a diamond) instead of 35 blocks (a
circle) and hit less (334 hits against 459 at 50 players), so the worlds compared differ. Systems measured in the sample
(MeasureSystems, 2,000 players): the two steps 0.6 ms on average; the chase 0.9 ms against 0.47 ms straight (a
chunk lookup per zombie).

## VoxelServer plan: decisions for the docs pass (owner, 2026-10-27)

- decisions.mdx + .zh.mdx: "Not added: a bundle of lookups as one parameter" (GA5: saves about 5 lines in
  VoxelServer's `Nearby`, a fifth parameter form to learn) and "Not added: box (Chebyshev) cutoffs for pair methods"
  (GA6: the only user, an AOI of chunk squares, counts viewers per chunk in O(1) through an index; a pair method would
  visit every pair within the view). Both declined by the owner.
- The VoxelServer page says why its AOI counts instead of searching, and why an inventory is a buffer, not relations.

## fix-kernel-foreign (8b, 8d)

- guide/analyzer.mdx + zh: EECS055's new reasons: a constructor of another assembly (set the fields by name: `new
  Velocity { Value = ... }` or `with`), a key probe has no vector form, "the generator failed to translate it"
  (report as a bug).
- decisions.mdx + zh, next free D: "Components of another assembly are read as in source": Roslyn does not link a
  metadata field to its auto-property, so `Symbols.Associated` maps `<Name>k__BackingField` to its property and every
  field-to-property check goes through it. A constructor of another assembly has no body and no positional mark, so a
  method calling one stays scalar with a fixable EECS055; dropped: parameters named after properties taken as
  positional (a guess: a body may compute the field), reading IL (Roslyn exposes none). Any exception translating one
  method becomes its reason, since a throwing generator loses all its output. An inlined target-typed value carries
  the callee's return type (`new T(...)`, `default(T)`); a cast binds as a conversion Kernel does not take.

## feat-fewer-copies (12c, 12d, 8c FillPool; merged 837c956)

- Behaviour changes (migration, changelog, README "What's new"):
  - an in-sync Restore marks as written or removed only the rows it copies back, not every row (`Changed<T>`,
    `Removed<T>` see fewer rows);
  - Capture and Restore advance `ChangeTick`;
  - after a sort, a `ref` taken before it reads the old column (it already was not the moved row; say it plainly);
  - a world that calls `FillPool` holds about 64 more lists per thread for that type.
- zh ApiDocs entries to refresh: Capture, Restore, `DynamicBuffer<T>.FillPool` (their English XML docs changed).
- Snapshots page and the rollback example: Capture(into) every tick is cheap when little is written; RollbackBenchmark
  and BufferSnapshotBenchmark.RollbackSpawning on the site (new classes, bench sync).
- VoxelServer page: the capacity region no longer fills the field buffers twice; check the walkthrough's prose.
- Decision records (agent's text, en + zh, in the scratchpad report `tasks/a0d6c60ebd81b8ba4.output`, also copied here):
  - **Incremental snapshots.** Capture(into) and Restore copy only what was written since the copy was last in sync:
    columns by LastWrite, 4,096-row blocks and 64-row words by their ticks, single rows when a word has at most 8
    written rows. In sync = same StorageVersion, no load in between, same rows; otherwise the table is copied whole,
    except buffer rows that still hold the same entity and were not written. Buffers get their own lists on both
    sides, so writes after a restore stay in place. Why: rollback netcode restores often and writes little. Numbers:
    Rollback with 16 writes per tick 3.2 ms -> 36 us; BufferSnapshot Restore 78 -> 1.2 us. Rejected: per-row dirty
    lists (memory per entity), lists kept shared (the fix-ci cost).
    zh: 增量快照。Capture(into) 与 Restore 只复制自上次同步以来写过的部分：按 LastWrite 选列，按 tick 选 4,096 行的区段和 64 行的字，一个字内写过的行不超过 8 行时逐行复制。同步的条件是 StorageVersion 相同、其间没有 Load、行相同；否则整表复制，但仍持有同一实体且未写过的缓冲行除外。缓冲在两侧各有自己的列表，恢复之后的写入原地进行。原因：回滚网络同步恢复频繁、写入很少。考虑后放弃：按行记录脏列表（每实体占内存）、保持列表共享（fix-ci 的代价）。
  - **Sort spares.** A re-sort gathers each column into a spare of the same type and length and swaps it in; the old
    column becomes the spare; one spare per shape per world, managed spares cleared, TrimExcess releases them. Why:
    rows move once, not twice. Numbers: Reverse 1M wide rows 77-88 -> 71-81 ms; small tables unchanged; 0 B.
    zh: 排序备用列。重排时把每列聚集到同类型、同长度的备用列再换入，旧列成为备用列；每个世界每种形状一个，托管备用列会清空，TrimExcess 释放。原因：行只移动一次而不是两次。
  - **Exact buffer pool.** FillPool(n) fills to n plus 64 lists per thread with lists of its own, and a thread set up
    later adds its 64; a thread allocates only when more than n lists are held. Taking 1 or 8 lists per lock instead
    cost the parallel churn 5x or 1.5x its time.
    zh: 精确的缓冲池。FillPool(n) 填到 n，再为每个持有自有列表的线程加 64 个，之后建立的线程补上自己的 64 个；只有持有的列表超过 n 时线程才会分配。改为每次加锁取 1 个或 8 个列表，会让并行 churn 慢 5 倍或 1.5 倍。

## feat-emit-segments (12e, merged 4925b09)

- Threading page (Emit), "What it costs" / "How to use it fast": a parallel pass no longer copies records; Count,
  Clear, `[Drain]` and ParallelFor take them where they are; a span read (`foreach`, `AsReadOnlySpan`, the
  indexer), a write or `SingletonRef<Emitted<T>>` joins once (O(records)); prefer `[Drain]` / ParallelFor.
- `IForSpan` docs: one `Execute` per segment inside a chunk over segmented records.
- `api.mdx` rows: Emit join O(1) per chunk; `Emitted.Items.Count` O(1); `Clear` O(segments); first span read or write
  after a parallel pass O(records); `SingletonRef<Emitted<T>>` O(records) once.
- Behaviour notes (changelog): `Items.Capacity` can be below `Count` until joined; a `[Drain]` job removing
  records of the same `Emitted<T>` stops at the new count; EmitPool keeps chunks' lists until joined or cleared (at most
  64 segments per `Emitted<T>`); `BufferStore<T>` unsealed (internal).
- Glossary: segment = 分段 (added to tools/ApiDocs/zh/README.md).
- Decision record (agent's text): **Emitted<T> keeps the chunks' lists as segments.** The singleton's list
  (`EmitStore<T>`) takes each chunk's list, in chunk order, as a segment at O(1); Count, Clear, `[Drain]` and
  `World.ParallelFor` use them in place; a span read, a write, `World.SingletonRef`, capture, save and load join first,
  once. Records stay in chunk order, bit-identical at any thread count; users and the generator unchanged. Considered:
  a `Count`/`Clear` beside `Items` (users change code), a joining ref property `Items` (joins for Count/Clear too), a
  segments field on every `BufferStore<T>` (8 bytes per entity buffer). Chosen: a subclass flagged by a negative
  `Shares`; writes already branch on it, span reads gain one compare; at most 64 segments before an append joins.
  Numbers (two rounds, 0 B): EmitParallel 10k 16.3 -> 11.1 / 12.3 us, 100k 98.1 -> 50.4, 81.4 -> 52.9 us; then a
  `[Drain]` pass 10k 29.1 -> 23.0 / 24.5, 100k 241.5 -> 192.4, 232.5 -> 203.8 us; WorldSimulation, BattleServer,
  ParallelForEach, ParallelFor, Record, DynamicBuffer, StructuralLoop within noise.
  zh: **Emitted<T> 把各块的列表留作分段。** 单例的列表（`EmitStore<T>`）按块的顺序直接接收每个块的列表作为一个分段，每块 O(1)；`Count`、`Clear`、`[Drain]` 遍历和 `World.ParallelFor` 就地使用；按一个 span 读、写入、`World.SingletonRef`、快照、存档和加载先合并一次。记录仍按块的顺序，任何线程数下逐位相同；用户代码和生成器不变。考虑过：在 `Items` 旁加 `Count`/`Clear`（用户要改代码）、访问时合并的 ref 属性 `Items`（`Count`/`Clear` 也会合并）、给每个 `BufferStore<T>` 加分段字段（每个实体缓冲多 8 字节）。选定：子类加负的 `Shares` 标记；写入本来就按它分支，span 读多一次比较；最多 64 个分段，再追加就先合并。

## feat-ghost-rows (MultiWorld, merged)

- Samples index (`samples/README.md`, `docs/content/samples/index.mdx` + `.zh.mdx`, `docs/content/README.md`): the
  MultiWorld row mentions ghost rows and `MoveEntities` at the borders.
- Glossary (`glossary.mdx` + `.zh.mdx`): "ghost row" (幽灵行) and "halo exchange" (光环交换) beside "Tile and halo";
  `tools/ApiDocs/zh/README.md` halo row gains ghost rows = 幽灵行.
- Threading guide: a world may pass between threads through a `Barrier` post-phase action (the sample's exchange);
  `MoveEntities`' "both worlds owned by the calling thread" says the same.
- Plans: `borrowed-ideas.md` item 4 notes the MultiWorld part is done and the verdict is in owner-review.
- Run `bun run lint:seo` on the sample's pages (en seoTitle 52 chars, description 144).
- Decision depends on the owner: the `CopyEntities<TTag>(target, ids, set)` proposal (owner-review.md).

## feat-symmetric-pairs (8e, voxel-rewrite.md GA4)

What shipped: a pair method that takes a component by `ref` of the row and by `[Neighbor] ref` (PairForm.IsSymmetric)
writes both sides of each unordered pair once (Newton's third law, MD codes' half neighbor lists). No new attribute:
the form is inferred from the parameters. The neighbors are the rows (the method's filter); a table against itself
runs its pairs (a, b > a), two tables each pair once (the draw keeps the neighbor's cluster at or after the row's, a
built list keeps j > i, the Rows loop starts after the row). Each side's writes are stamped by its own table (no
StampOther). One pair at a time; with `Parallel = true` the pass runs in chunks of 256 rows (PairHalves.ChunkRows,
never chosen by the thread count) colored greedily at distance two over the clusters each chunk writes, the colors one
after another with a barrier, a color's chunks in parallel (World.ForPairHalves; the colors cached per draw of the
list, PairSchedule); a table of fewer than 4 chunks runs the serial pass. The result is the same on any number of
threads; float sums differ from the serial pass's in the last bits (another order of the pairs), integer sums are equal.

- guide page of pair methods (the one documenting `[Neighbor]`; today only api.mdx and reference/ name it) + zh:
  a section "Writing both sides": when to choose it (forces, contacts, merges: half the pairs of the full pass), what
  it runs (each unordered pair once, one pair at a time; in parallel, colored chunks), what it refuses (EECS105 below), a full example
  (repulsion: `ref Force f, [Neighbor] ref Force g`, `f.X += dx; g.X -= dx;`) and item merging (the lower EntityId
  survives: `SymmetricPairTests.Merge` / the VoxelServer form). Costs from the A/B below: against the full vector pass
  (Generated) it wins on sparse worlds and small crowds, loses about 1.2-1.3x on dense crowds of 10k-100k; against
  the per-pair full pass (GeneratedRows) it always wins (2-2.8x).
  Parallel (PairHalvesBenchmark, AVX-512, 20 ticks per op, two rounds; serial = the same method on one thread):
  4 threads: 10k crowd 542 / 596 us against 1,039 / 1,037 serial (0.52-0.58), 100k crowd 5,115 / 5,244 against
  10,784 / 10,892 (0.48), 10k sparse 124 / 153 against 217 / 183, 100k sparse 1,604 / 1,494 against 2,724 / 2,674
  (0.56-0.59). On 1 thread the colored pass costs 0-15 % over the serial one (10k sparse 1.15, crowds 1.02-1.03): the
  price of one result on any thread count, so the guide says to keep Parallel off where the world runs one thread.
  Against the parallel full pass writing its row only (Full, vector loop over every ordered pair): Full is faster on
  dense crowds (10k 296 / 339 us, 100k 3,263 / 3,503 at 4 threads), the symmetric pass on sparse worlds (Full 143-177
  and 1,696-1,769): pick the symmetric form for contacts/merges and sparse worlds, the full vector pass for dense
  force sums. 0 B per pass after warm-up (every Parallel row "-").
- guide/analyzer.mdx + zh:
  - EECS105's symmetric reasons: the neighbors are not the rows (filters on the neighbor side), a component by `ref`
    of one side only ("to write both sides of each pair once, take it by 'ref' of the row too"), a Mut<T>. Parallel is
    no longer refused for this form (the EECS105 description says so: "on several threads (Parallel) when it writes
    no neighbor or writes both sides of each pair (its chunks colored apart, the same result on any number of
    threads)").
  - EECS106: the compared component written on both sides (the test would read moved positions).
  - EECS107's fix: a query walk with the half skip (`if (o.Entity.Id.Index <= self.Id.Index) continue;`) writing
    both sides becomes the symmetric pair method (skip and the unused `Entity self` dropped).
  - EECS056 under SimdMode.Require: "it writes both sides of its pairs ('g', [Neighbor] ref): each unordered pair
    runs once, one at a time". EECS053 is not reported for these refs (both are written).
- api.mdx + zh: `IPairJob.Symmetric` (static virtual, generated; complexity row: half the pairs of ForPairs), the
  PairRun.Same doc (a symmetric run of a table against itself starts after the row). Chinese entry for the new XML doc
  in tools/ApiDocs/zh.
- decisions.mdx + zh, next free D: "A pair method writing both sides runs each unordered pair once".
  - Inferred, no attribute: `ref` of the row plus `[Neighbor] ref` of the same component already says it; a method
    that writes the neighbor's component it reads of its row was refused before, so no method changes meaning.
  - Half lists over a sorted cluster grid (pairs a < b; a cluster's neighbor clusters at or after it), as GROMACS /
    LAMMPS `newton on`.
  - Parallel by spatial coloring (LAMMPS / GROMACS): chunks of a fixed number of rows (256; never by the thread count,
    so one result on any number of threads), colored greedily at distance two over the clusters each chunk writes
    (its own and those its ranges reach), the colors run one after another with a barrier, a color's chunks in
    parallel; chunks past 64 colors run alone. The colors are worked out once per draw of the list (PairSchedule;
    without the cache 100-190 us per pass). A table of fewer than 4 chunks runs the serial pass.
  - Not bit for bit with the serial half pass: no parallel order of float additions into a row equals the serial
    order (a "levels" order that would keep it serializes, since neighboring Morton chunks always conflict). What
    holds: the same bits at 1 and 4 threads (fixed chunks, fixed color order, a color's chunks write disjoint rows),
    integer sums equal to the half walk's, float sums within rounding of it (tests: SymmetricPairParallelTests).
  - Dropped: the boundary split with per-chunk buffers (GROMACS nbnxm / LAMMPS USER-OMP reverse communication), built
    and measured against the colors in the same benchmark (decision text below).
  - Atomics excluded by the owner (float order not deterministic).
  - Dropped: the vector loop of the symmetric form, measured in two shapes (PairsBenchmark --dev screens, AVX-512,
    10k/100k crowd and sparse): the cluster's lanes summed into the neighbor in lane order (bit for bit) ran 1.4-2.3x
    slower than the pairs one at a time (10k crowd 1,499 us against 1,059; 100k sparse 5,662 against 2,500), and the
    FloatMode.Fast tree sum into the neighbor 1.1-2.3x slower (100k sparse 4,582 against 2,442; 10k crowd 1,173
    against 977). Why: a horizontal reduction per neighbor costs more than the halved j count saves, and the half
    list's diagonal cluster needs a masked prefix per j.
- Decision text for the dropped per-chunk buffers (decisions.mdx + zh, its own D or a section of the decision above):
  "A parallel pair method writing both sides colors its chunks; per-chunk buffers were dropped". Built: every chunk
  at once, rows of its own chunk written in place, rows of other chunks (only the clusters it reaches) written into a
  buffer owned by the chunk (not the thread), zeroed at its start, added into the rows after every chunk in chunk
  order (a generated Gather), the far clusters cached per draw. It needs a method whose writes of the neighbor only add
  (`g.F += / -=`, `++ / --` as a statement, on float, double, int or long fields), so merges and other bodies would
  still need the colors. Measured (PairHalvesBenchmark, two rounds, AVX-512, 0 B both): 1 thread 1.18-1.25x the serial
  pass on crowds (10k 1,195 / 1,252 us against 982 / 1,054; 100k 13,071 / 13,713 against 10,499 / 11,621), 1.00-1.16x
  sparse, where the colors cost 1.02-1.03x on crowds; 4 threads 0-9 % ahead of the colors on crowds (10k 500 / 526
  against 542 / 596; 100k 4,673 / 5,262 against 5,115 / 5,244), behind on sparse worlds (10k 142 / 163 against
  124 / 153; 100k 1,572 / 1,574 against 1,604 / 1,494). Why not: a second form for a subset of bodies, a 20 % loss on
  one thread, a generated reduction and per-chunk memory, for a gain within the noise at 4 threads. What would change
  it: many cores (16+) where the colors' barriers idle threads (more colors than chunks per thread), measured on
  PairHalvesBenchmark with Threads 16.
- roadmap.mdx + zh: GA4 done (symmetric pair methods, serial and parallel); dropped: their vector loop and the
  per-chunk buffers (the decisions).
- changelog + README "What's new": pair methods writing both sides of each unordered pair once.
- Existing forms against base (5b8f926), BDN two rounds each, AVX-512, after the parallel form (head 9cc3362):
  PairListsBenchmark 24 cases within -9..+8 % per round with no case slower in both rounds by more than 6 % (10k crowd
  GeneratedRows Auto +5.8 / +4.3 %, Generated On +4.4 / +5.1 %; most others change sign between rounds);
  PairsBenchmark Generated / GeneratedRows / GeneratedParallel within -16..+9 %, except GeneratedParallel at 1k crowd
  (a 30 us pass): +16 / +14 % in the two rounds and +3 / +11 % in a re-check (q1, q2), against a standard deviation of
  4-5 us (13-15 %); older rounds of the same case went -7 / +8 %. The only change on that path is one more field of
  PairRun and a predictable `half` branch in the sweep, so it reads as noise: rerun PairsBenchmark --filter
  '*GeneratedParallel*' on a quiet machine before merging (verify).
  **Rerun on the owner's M4 (2026-10-28, base 685bbc2^1 vs head c327e38, two interleaved rounds, 0 B):** means of the
  two rounds base -> head: 1k crowd 25.45 -> 26.38 us (+3.6 %; the base alone went 23.26 / 27.64 between rounds),
  1k sparse 17.26 -> 16.71 (-3 %), 10k crowd 222.0 -> 225.7 (+1.7 %), 10k sparse 100.4 -> 102.1 (+1.7 %), 100k crowd
  2108 -> 2181 (+3.5 %; head round 2 2092), 100k sparse 881.5 -> 869.6 (-1.4 %). No case slower past the noise:
  the 1k crowd row was noise.
- No migration entry: a method EECS105 refused now compiles (both sides, and both sides with Parallel); nothing that
  compiled changes.
- guide/parallel (or the pair page): `Parallel = true` on a method writing both sides is deterministic across thread
  counts but not bit for bit with the same method on one thread's serial pass; small tables (< 4 chunks of 256 rows)
  run the serial pass.

## feat-streaming-stores (merged 1b1aa12)

- Decision record (en + zh): text in the agent's final report, kept in the scratchpad
  (`feat-streaming-stores/decision.md`); gate counts every column the pass moves, not only the written one.
- `docs/plans/microarch.md`: candidate 8 from "Dropped" to "Shipped" with the numbers; `owner-review.md`: remove the
  streaming entry; roadmap item; changelog + README "What's new" (Performance).
- Guide page on vector loops (en + zh): what streams and when (write-only column, no blocks, past the LLC, >= 1,024
  rows), the schedule line "write-only columns streamed past the last-level cache", pitfall: `+=` or a conditional
  write keeps plain stores.
- Public skill: writing a column whole (`x = new X(...)`) instead of `+=` lets a pass past the cache stream it.
- No api.mdx row (`StreamingStores` is EditorBrowsable Never). Not measured on ARM64: ask the owner for an M4 run of
  StreamingStoreGateBenchmark.

## fix-flaky-alloc (decision draft, a record after D135)
- **Decision draft.** The tests' 0 B checks of code run many times warm up with `Allocations.WarmUp(step, least)`:
  `least` runs, then until the JIT has settled (no method compiled in the process during the last 40 runs and the
  last 0.5 s, `JitInfo.GetCompiledMethodCount`), before `Allocations.Of` measures. No change to the core.
- **Why.** `PrefetchingProbesBuffersWalksAndLambdasAllocateNothing` failed in the full suite (6 of 10 instrumented runs
  with AVX2 off, 1 of 2 normal), never alone. Measured per probe job inside the window: each of the four jobs of
  `ProbeAll` allocated 168 B once, in the frame where `JitInfo.GetCompiledMethodCount(currentThread: true)` grew by
  one, frames 4 to 9 after the 50 warm-up frames, while 10 to 27 methods compiled elsewhere (the tiering backlog of
  the suite's earlier tests). A compile on the thread running an old method is an on-stack replacement: with
  `DOTNET_TC_OnStackReplacement=0` no compile happened on the measuring thread and the test passed 3 of 3. In a run
  where the four compiles came at warm-up frames 48 and 49, the test passed: the fixed warm-up raced the tiering.
  `ParallelReductionsScratchAndLookupsDoNotAllocate` had OSR compiles on its measuring thread inside its window too
  (updates 53, 62, 81 of 100, in 2 of 4 instrumented runs; 0 B those times; none with OSR off); its own failure did not
  reproduce in 15 runs before the fix. Ruled out for both: a background collection (settled, D128), helper threads
  (not counted), the measuring thread's `Scratch` list (made during the warm-up in every run), a filter past bit 255
  (it would allocate on every pass).
- **Numbers.** After the fix, full Release suite, 0 failures in every run: AVX2 off 5 of 5 in a row, normal 1, AVX
  off 1, AVX-512 off 1 (3,178-3,179 tests each; the suite took 4 m 23 s to 4 m 48 s, 4 m 36 s to 5 m 5 s before).
- **Considered.** A longer fixed warm-up (the feature skill's "200 passes past OSR"): how many runs the tiering needs
  depends on the backlog the earlier tests leave, so any fixed count races it. `TieredCompilation` or
  `TC_OnStackReplacement` off for the test project: the tests would run other code than users do (no Tier0, no PGO).
  Retrying a measurement: it loosens the check.
- Docs pass: tests/AGENTS.md says it (done); the feature skill's "warm up past OSR (200 passes)" becomes
  `Allocations.WarmUp` (skill owner).

## feat-tiles-pingpong (items 11, 12a, 12b, GA10, GA3, GA12, 8c) — docs to-dos

Terms (zh): tile 瓦片, halo 光环, ghost cells 幽灵格子, stencil 模板, ping-pong 乒乓缓冲, slice 分帧; Jacobi 雅可比,
Gauss-Seidel 高斯-赛德尔, red-black 红黑, multicolor 多色 (add the last four to tools/ApiDocs/zh/README.md's glossary
and lint-terms if they are not there). Each explained in one plain sentence where first used (WRITING.md).

- guide/grids.mdx + zh (the tile page):
  - "Stepping a tile": take the tile by ref (`ref Tile<Heat, Plate> heat`), call `ExchangeHalo()`, read rows with
    `At(row)` and write them with `Write(row)`. Jacobi (default: each pass is one time step, every cell reads the step
    before): the pass keeps a second buffer per tile, swaps it with the cells after every tile stepped (O(1) per
    tile), writes the rows the step skipped as zero, stamps only the tiles that changed. No `NextHeat` buffer, no
    commit system: replace the page's `CommitHeatSystem` example with the ref form (EECS116 reports the old one).
  - `[ForEach, GaussSeidel]`: in place, one buffer; tiles step color by color (colors from the parity of the key's
    integer fields: 2 for a Faces neighborhood, else one per field: 4 in 2D, 8 in 3D), each tile sweeps until it
    settles (`MaxSweeps`, default the tile's cell count), deterministic for any thread count. When to pick which:
    Jacobi when another system reads the field after each pass as a step in time (heat, a wave, Life); Gauss-Seidel
    for a field read only when settled (distance, flood fill, scent). EECS117 suggests it for a Min / Max relaxation.
    A torus with an odd number of tiles per axis throws (two neighbors of one color).
  - Sleeping tiles (GA12): a stepped tile that wrote nothing sleeps; the pass wakes it when a neighbor's facing edge
    changed, another system wrote its cells (`Changed<DynamicBuffer<TCell>>`), a by-value tile of the same grid or a
    tracked component it reads changed, or a tile around it left the grid. Sleeping applies only when every other
    parameter is an Entity, a Scratch, such a tile or a tracked component (else every tile steps each pass). A Jacobi
    field that moves nearly everywhere (7 of 8 tiles changed in a pass that looked) steps every tile for the next 15
    passes without comparing or waking (same result: an unchanged tile's step writes the same cells; such passes stamp
    every tile they step, so a [Changed<DynamicBuffer<TCell>>] reader sees them all). Remove the
    page's hand-written `ChangedEdges` / `Facing` wake loops from the recommended form (keep them as the manual form).
  - GA10 / GA3: an empty cell buffer is sized from the tile's shape on the pass; `MaxPooledLength` is raised to a
    tile's cells, so no `SetCount` and no `MaxPooledLength` line in user code.
  - 8c: a 2D tile over a 3D grid's key: `Tile<Heat, Columns<Chunk>>` (no second tile component and index);
    `[GaussSeidel]` replaces the generic per-color systems (`[With<TColor>]`, CS8968); EECS059 is not reported for a
    lookup of the stepped tile's own grid under `[GaussSeidel]`.
  - Remove the TODO(water overlap) and the `PaddedTile` text; `Pad()` reads `ExchangeHalo()` everywhere.
  - Costs: Jacobi O(cells) per stepped tile plus O(1) swap; zeroed rows a few vector stores; Gauss-Seidel
    O(cells x sweeps) per tile per color pass, one walk per color even when a color is empty.
- api.mdx + zh: complexity rows for `Tile<TCell, TTile>.ExchangeHalo`, `Write(row)`, `GaussSeidelAttribute`,
  `Columns<TTile>`; remove `PaddedTile`, `Pad`. Then tools/ApiDocs: zh entries for the new XML docs (GaussSeidelAttribute,
  MaxSweeps, ITileColors<TKey>, TileColoring, TileSteps<...> members, Tile.ExchangeHalo / Write / Rows / At / indexer,
  Columns<TTile>, TileNeighbors.Of / KeyAt / Facing) and regenerate docs/content/reference.
- guide/analyzer.mdx + zh: EECS115 (Error: a step the generator cannot write: [GaussSeidel] without a ref tile, two
  ref tiles, a ref tile with [EntityMethod] / Slices > 1 / [Consume], MaxSweeps < 0, a key without integer fields; Write
  on a tile taken by value), EECS116 (Warning, end of compilation: a [ForEach] method that only copies `in
  DynamicBuffer<TNext>` into the cells a step writes into TNext; no code fix, see docs/owner-review.md), EECS117 (Info:
  a Jacobi ref tile relaxing with Min / Max). EECS003's message now also accepts a Tile parameter alone. EECS072 says
  ExchangeHalo. EECS073 compares cells and grid.
- samples/voxel-server page: unchanged output; the rewrite (not this branch) ports Water's step + `WaterCommitSystem`
  to a ref tile and Scent to `[GaussSeidel]` (EECS116 leaves Water/Commit.cs alone: it also
  writes FluidCount and a Reduce<Filled>, so it is no plain copy).
- migration.mdx + zh (next version): `tile.Pad()` -> `tile.ExchangeHalo()` (returns nothing; read through the tile:
  `tile.At(row)`, `tile[x, y, z]`, `tile.Rows()`); `PaddedTile<TCell, TTile>` removed (use the Tile itself);
  `TileNeighbors.Of<TTile, TKey>(index, entity, out around)` -> `TileNeighbors.Of<TShape, TGrid, TKey>(...)` (generated
  code only); a hand-written NextX buffer + commit -> `ref Tile` + `Write(row)`. README "What's new": stepping tiles
  (Jacobi ping-pong, [GaussSeidel]), sleeping tiles, ExchangeHalo.
- changelog: New (stepping tiles, GaussSeidel, sleeping, Columns, EECS115-117), Changes that need code changes (Pad,
  PaddedTile), Performance (the swap instead of the commit copy: numbers below).
- decisions.mdx + zh (drafts):
  - "Tiles step by ref: the generator owns the second buffer": why (the commit copied every stepped tile per tick;
    MPI's u / v swap), what was considered (a swap of two user buffers under the gate: two buffer types left to
    users; copy-on-write), the zero rows (the step's skipped rows would hold the step before last after a swap),
    numbers below.
  - "Two update forms, declared": Jacobi vs [GaussSeidel]; colors from key parity, not from the stencil's offsets read
    by the generator (a key parity is known per tile without reading the body; 2 colors suffice for a Faces stencil,
    one bit per integer field covers All / Edges); empty colors still cost a walk.
  - "Sleeping tiles, owned by the pass": the rule for what a step may read and stay asleep (Entity, Scratch, same-grid
    tiles, tracked components), the edges found while stepping (12b: LeaveJacobi compares each written row with the
    cells it replaces, RowEdges, no second pass over the tile; the last row and layer first, stopping once every face
    changed), the wakes from the neighbor ids the step's exchange found (kept per tile, no index lookup: the lookups
    in the serial Next made the ping-pong blur 1.3x slower than step + commit at 1 thread, 1.6x at 4), stamps in Next
    (a stamp from the chunk's thread writes its 64-row word's summary, a line the chunks beside share: 4 threads lost
    ~25 %), the swap as a trade of the two lists' arrays (no buffer lookup; a shared list goes whole), and the awake
    passes (sleep bookkeeping costs more than it saves when the field moves everywhere: measured in the toggles, the
    bookkeeping off was 737 us against 1,003 us at 1 thread).
  - 12a padded storage: the verdict and numbers below.
  - Changed filters built once per type (ChangedSince<T>): a filter built per pass interns its bits past 255 and
    allocates (FilterOverflow.With), found by the 0 B test after ComponentTests registered 300 types.
- Numbers (BenchmarkDotNet on the shared VM, sd 10-40 %, so differences under ~15 % are not resolved; base 5ac9b37,
  head e016a59; rerun on the reference machine with `tools/bench.sh TileStencilBenchmark TileActiveBenchmark
  TileHaloBenchmark --sync` before the pages quote any):
  - TileStencil (us, 1 / 4 threads): Blur9 by value 751 -> 781 / 397 -> 373, Laplacian by value 742 -> 762 / 397 ->
    400; the ping-pong step (new) Blur9 788 / 431, Laplacian 760 / 489 against step + commit by hand ~ the by-value
    numbers (a later dev run: 779 / 421 and 707 / 409). So the ping-pong form ties the hand-written commit at 1 thread
    and is not yet faster at 4: the pages say it saves the commit system and its copy, not that it is faster.
  - TileActive (us, 1 / 4 threads): AlwaysOn 435 -> 418 / 239 -> 231, Sparse (sleeping by hand) 12.7 -> 13.1 /
    13.8 -> 15.0, PingPong (sleeping owned by the pass, new) 22.3 / 15.7: a sparse field costs the generated form
    1.7x the hand-written one at 1 thread (see docs/owner-review.md).
  - VoxelServer TickBenchmark (ms, four rounds base -> head, 0 B everywhere): 50 players 56.6 -> 64.0, 49.2 -> 59.3,
    65.3 -> 69.8, 59.3 -> 51.8; 500 85.4 -> 83.0, 85.3 -> 84.6, 86.8 -> 83.2, 86.4 -> 84.8; 2000 127.0 -> 104.5,
    122.6 -> 124.4, 140.1 -> 146.1, 139.8 -> 132.0 (no difference past the noise; the sample uses by-value tiles only).
  - 12a TileHalo (us, 1 / 4 threads): Exchange 261 / 118, Padded 253 / 161: padded storage loses 1.36x at 4 threads
    (each tile's buffer 27 % larger for a 16 x 16 tile at r = 1, the ring copy scattered over the neighbors' lines),
    within noise at 1. Dropped; decision record: "Tiles keep their cells unpadded; the halo is exchanged per step".
    12b shipped inside the Jacobi step (LeaveJacobi), so ChangedEdges stays for hand-written steps only.

## feat-tiles-pingpong merged (2026-10-29): what the final round added

- Tier1 fixes and their numbers, the woken walk per color with `TileWalk.Share = 4`, parallel dense swaps over
  `TileWalk.Swaps = 256` tiles with serial stamps, LoadPlane, the plane Find, nint cursor offsets: the agent's final
  report (owner-review "The generated ping-pong step against step + commit by hand" has the verdict numbers).
- Decision drafts: LoadPlane against the table of barriers; the woken walk against every tile (`TileWalk.Share`);
  parallel swaps with serial stamps (64-row word summaries are shared); `Swaps` reasoned, not measured apart.
- zh ApiDocs entries for ITileStep / StepWoken / StepWokenParallel if they are public.
- The 4-round TileStencil / TileActive / TileSettle and the 2-round TileWoken final numbers were cut off by a worker
  restart; the bench sync runs them again.

## feat-stage-catch (8f)

- guide/analyzer.mdx + zh, EECS055: a switch expression now names itself and its fix. Over a call on a struct
  (VoxelServer's Chase: `new Nearby(...).Trail(...) switch`): "it switches over a call on struct {T} ('...'), which
  runs row by row, and a vector loop has no switch: call {T}.{M} in a [ForEach] method of its own that stores the
  result in a component, and switch over that field here with conditional expressions ('v == A ? a : v == B ? b :
  c')"; otherwise "it has a switch expression ('...'), which a vector loop has no form of: write it as conditional
  expressions". Both fixable (Warning under SimdMode.Require).
- guide/analyzer.mdx + zh: the generator's own failures, each reported as a bug with the stage named ("the generator
  failed to {what} (Exception: message Please report this as an EasyEcs bug)"), and what the method keeps:
  - EECS055 (and EECS097 for a lambda pass): "failed to read its reads of other entities": the plain job, no
    prefetching twin;
  - EECS106: "failed to read its cutoff test": every pair runs; EECS106 now also reports a cutoff the pass cannot
    compute once ("its cutoff is not a value the pass can read once: ..."), which the analyzer missed before;
  - EECS093: "failed to read what M reads, writes and filters": the two passes keep their own walks.
- guide/neighbors (pair methods) + zh: a partial pair method works (the attributes on the defining part, the body on
  the implementing one); it used to drop the project's generated output.
- guide/prefetch (or the Lookup page) + zh: reads through a Lookup<T> or an index of a local function's own
  parameters are not the method's chases (they used to crash the generator); only the method's own in components and
  lookups are prefetched.
- decisions.mdx + zh, next free D: "Every body stage catches its exceptions": Kernel, Chases, PairForm.ReadCutoff and
  FusionSide each turn an exception into a reason (Kernel.FailedTo) and the plain form, since one throwing stage drops
  every file of the project (CS8785, then hundreds of errors in user code); the analyzer calls the same function, so
  what the generator skips it reports.

## feat-switch-masks (8j)

- guide/vector-loops (the page on what a [ForEach] method's vector loop takes) + zh: switches and patterns are lane
  masks now. A switch expression whose arms are patterns over lanes (numbers, enums, bools, structs of them, tuples of
  them) is first match wins: each arm's mask (a `when` guard and-ed in) selects its value over the arms after it; `_`
  or any arm that matches every lane ends it; a row no arm matches goes to the method, which throws
  SwitchExpressionException as written (no silent default). A switch statement runs each section under the lanes its
  first matching label takes (a `default` section wherever it stands takes the lanes no other label took); a section
  may return. Property (`p is { X: > 0, Y: < 5 }`), positional (`v is (> 0, > 0)`: a record's Deconstruct, a
  Deconstruct that only sets out parameters from fields, the System.Numerics vectors', a tuple's elements), type and
  `var` patterns over value types; a constant NaN pattern matches NaN (`float.NaN => ...`), as C# tests it. Helpers
  with a switch expression or a switch statement (returns in its sections) are inlined. Show one example with its
  generated loop (the "generator writes" tab).
- guide/analyzer.mdx + zh, EECS055: 8f's two switch texts go ("write it as conditional expressions"). The reasons now:
  - "it switches over '{x}', a {T}, which no vector lane holds: a vector loop matches numbers, enums and bools of
    {sizes} bytes, structs of them and tuples of them" (and "it tests '{x}', ..." for `is`); a number of another
    size: "it switches over '{x}', a byte, not a lane type of 4 or 8 bytes";
  - "it matches a list pattern ('{p}'), which tests the elements of a list: a vector loop tests lanes, not lists";
  - "pattern '{p}' declares '{v}' as a {T}, which no vector lane holds: name the value it tests, or a field of it,
    instead";
  - "it jumps from one switch section to another ('goto case ...'): a vector loop runs each section on its own lanes"
    and "it leaves a switch section before its end ('break;')" (cold branches in a method that does not change
    structure; the reason in one that does);
  - "its switch expression ('...') gives a value twice as wide as the lanes: assign it to a local of its own first"
    (lanes of two sizes);
  - over a call on a struct the loop cannot inline (VoxelServer's Chase): "it switches over a call on struct {T}
    ('...'), which runs row by row: call {T}.{M} in a [ForEach] method of its own that stores the result in a
    component, and switch over that field here" (fixable: Warning under SimdMode.Require); where the call gives a
    number of another lane size, "... stores the result in a component field of 4 or 8 bytes (not a byte) ..."
    (VoxelServer's Chase says this now; its other blockers are listed in the 8j report and backlog item 8j).
  - EECS055 inliner reasons: "which has a switch" is gone; "which jumps from one switch section to another (goto
    case)" is new.
- performance page / KernelBenchmark table + zh: the Grade rows (GradeScalar, GradeGenerated) once synced.
- decisions.mdx + zh, next free D: the record drafted in the 8j report ("Switches and patterns are lane masks").
- changelog (coming version, ### Performance or ### New) + zh: the line from the 8j report.
- skills/easyecs/SKILL.md (public skill): has no switch advice today; if the docs pass lists what vectorizes, add
  "switches and patterns over lanes vectorize; list patterns and switches over strings or classes do not".

## feat-foreign-bodies (8g)

- guide/vectorization (the vector loop page) + zh, "Calls": a helper of another assembly is inlined like one of the
  project's when that assembly was built with EasyEcs's generator (a contracts project referencing EasyEcs is): its
  public static methods, extension methods, struct methods and property accessors with a body, small and without a
  loop, throw, switch or lambda (the same syntax limits as a helper of the project). Fields and static helpers the
  assembly hides are reached anyway (private fields of a component are lanes like any other; a static readonly field
  of an internal type is read once per run through an `[UnsafeAccessor]` extern of the job). An assembly built
  without the generator (or with an older EasyEcs) keeps "which is defined in another assembly".
- guide/vectorization + zh, "Struct values": `new Position(x, y)` of a component of another assembly vectorizes when
  that assembly recorded the constructor: a positional record struct, or a constructor whose body only assigns its
  parameters to fields (`=> _x = x;` too, now also in the project itself). Reasons that remain, now named after the
  record: "its constructor does more than set fields from its parameters ('...')", "the parameter 'X' of T's
  constructor sets no positional property" (a positional property redeclared), "T's constructor also gives 'F' its
  initial value ('...')" (a field or property initializer: new in the project too, which used to ignore it and gave
  the initializer's field the default value in the vector loop).
- guide/analyzer.mdx + zh, EECS055: the reasons a recorded helper carries ("it calls Physics.Steps, which has a
  loop"; "it calls T.M, which other assemblies cannot reach" for an internal type it names; "whose recorded body does
  not bind in this assembly"), and that the fixable "declared in another assembly ... set them by name instead" is
  now only for assemblies built without the generator.
- samples/HotReload README + page: Move writes `position = new Position(...)` again (restored after 5fa54c1), with a
  line saying the contracts assembly's generator records the constructor.
- architecture.mdx + zh (generator section): Recorder (declaring side, source output, one file per source file, read
  once per syntax tree: a syntax provider's transform reruns for every file on every keystroke otherwise) and
  Recorded (reading side, cached per referenced assembly symbol); only members taking and returning values are
  recorded; the attributes are `EditorBrowsable(Never)` API of EasyEcs.Core (`ConstructorFieldsAttribute`,
  `InlineBodyAttribute`), written only by the generator. EasyEcs.Core records nothing of its own API.
- guide/vectorization + zh, "Calls": a helper of another assembly taking or returning a class (string, World, an
  array) is not recorded; in an IDE, a record follows the edits of its own file (another file's change to what it
  names shows once that file is edited again; a build always reads every file).
- decisions.mdx + zh: the decision record of this branch (draft in the 8g report).
- changelog (coming version, New): "A [ForEach] method calling a constructor or a small helper of another assembly
  gets its vector loop when that assembly was built with EasyEcs: the generator records constructors' fields and
  helpers' bodies as hidden assembly attributes, and reaches the fields they hide through [UnsafeAccessor]."

## feat-tile-reads: tile loads checked once per loop (nothing user-visible in the API)

- grids guide (en + zh): a column loop `for (int x = 0; x < TTile.Width; x += 4) ... c.Load128<T>(x + k)` gets one
  check per loop; say which loops qualify (an int from a constant, a constant or the shape's extent as the bound, a
  positive step, x written nowhere else, no lambda or local function around the load) and that other loads stay
  checked; the decision record (draft in the agent's report) next to D-tiles.

## feat-renames

- README line about `ChunkRows` says the old name (now `ChunkSize`).
- decisions index table: no D136 row.
- The record pass's docs (guide/emit, threading; listed under feat-generator-gaps) are still open, now as `[Drain]`.

## feat-tiles-rest (items 11, 12a, 12b leftovers: tile pools at load, EECS117's fix)

- guide/dynamic-buffers.mdx + zh, "#pool": the cells of a tile need no `MaxPooledLength` line: for each
  `Tile<TCell, TTile>` a `[ForEach]` method takes (by value or by ref, closed types), the generator raises
  `DynamicBuffer<TCell>.MaxPooledLength` to the tile's cells when the assembly loads (never lowers it; a larger
  shape of the same cell wins), so `FillPool` at start-up fills lists of a whole tile. The example at line ~317
  (`DynamicBuffer<Block>.MaxPooledLength = ChunkBlocks`) and the tables at ~459 / ~503 say "unless the buffer is a
  tile's cells". A buffer only read as another buffer (`NextFluid`, a side buffer like `Trail`) still needs it.
- guide/grids.mdx + zh: "Costs" / the pool line: the pool of a tile's cells is sized from its shape at load (GA3);
  remove any `MaxPooledLength` line from tile examples. Line ~504: remove the TODO(water overlap) comment and say
  in "What it costs" why there is no interior / ring overlap (below).
- guide/analyzer.mdx + zh, EECS117: a code fix adds `[GaussSeidel]` after `[ForEach]`, offered when the grid's key
  has integer fields to color the tiles by (a key with a string gets no fix, it would be EECS115).
- decisions.mdx + zh: the two drafts in the feat-tiles-rest report (tile pools at load; no interior / ring overlap).
- decisions.mdx + zh (prose, not fences): lines ~1965 (`returns a PaddedTile`) and ~2470 (`tile.Load()` →
  `tile.Pad()`) describe the API before the rename: say `ExchangeHalo()` fills the thread's texture and the tile
  reads it (`Rows`, `At`, the indexer); add the later rename `Pad()` → `ExchangeHalo()`, `PaddedTile` folded into
  `Tile` (both unreleased: no migration entry, v4.7.0 has no tile API).
- migration.mdx + zh, "#v4-8-new-types": drop `PaddedTile<TCell, TTile>` from the list of new types (it no longer
  exists); add nothing for `TileCells` (EditorBrowsable(Never), generated code only).
- api.mdx + zh: `TileCells.Pool` is generated-code API (hidden): no complexity row, like `TileNeighbors.Of`.
- samples/voxel-server page + README: the options region lost the Block and Fluid `MaxPooledLength` lines (the page
  includes the file; check the walkthrough's sentence about the pools).

## feat-deferred-consume (backlog item 9, G10 / GA9): `[ForEach(MaxRows = N)]`

- guide/systems (en + zh): a section "A budget per run: MaxRows" next to `[Consume]` (anatomy: what it is; when to
  use it: loads, saves, requests a server serves per tick, anything a pass refuses (EECS041) or that costs too much
  for every row at once; how: `You write` / `The generator writes` from real output (the job, `world.ForEachCollected`,
  the Visit count `Math.Min(Count, N)`); semantics: the first N rows in Query order collected when the pass starts,
  the method run on each outside the walk on copies of its components, `ref` ones written back (to the row the entity
  moved to, unless it lost them) and stamped, rows an earlier row's method destroyed or changed so they no longer
  match skipped, the rest wait; with `[Consume]` each row destroyed after its method (never a DestroyAll); singletons
  resolved once per pass as in any job; a throwing row keeps the rows after it; costs: CollectIds + one Entity lookup
  and a component copy per row, up to 64 ids on the stack, more from ArrayPool (MaxRowsBenchmark numbers through
  BenchTable); pitfalls: EECS118 (parallel, Slices, change filters, spans, Maybe, Mut, view, tile, Scratch,
  [Neighbor], SimdMode.Require, FusionMode.Require), no vector loop and no fusion (the schedule line says "at most N
  rows per run, collected first, then run one by one outside the walk"), the system runs alone when it takes the World
  or consumes.
- guide/systems `[Consume]` section: a consuming method with MaxRows destroys per row; the "Skip it for" Callout can
  point to MaxRows for a backlog.
- guide/structural-changes and guide/saves (en + zh): LoadEntities / SaveEntities / MoveEntities from a [ForEach]
  method with a budget (no OnUpdate + CollectIds by hand any more); EECS041's message now says so.
- guide/analyzer (en + zh): EECS118 (Error, Invalid [ForEach(MaxRows = N)]) and EECS119 (Warning, rows collected
  and looped over by hand in an OnUpdate; fix: the [ForEach(MaxRows = N)] method, its filter field deleted when
  unused, guards dropped, declarations moved in, continue -> return, id -> e.Id; no fix for a for loop, a break or
  return out of the loop, a use of the count, or statements it cannot move, such as the copy-then-DestroyEntities
  shape). Ids provisional (taken at merge: 110-114 unused here, 115-117 taken by tiles). Rule list and range in
  src/EasyEcs.Generator/AGENTS.md and the public skill.
- api.mdx + zh: `World.ForEachCollected<T1..T9, TJob>(ref job, maxRows, in filter)` complexity row (O(matching
  archetypes + the rows read until maxRows are collected) + one lookup per row); `ForEachAttribute.MaxRows`.
- samples/voxel-server.mdx + zh: the load / unload walkthrough (regions `load`, `unload` of Systems/Chunks.cs) now
  shows the plain methods: say the budget is `MaxRows = Rules.MaxRequests`, that the requests are consumed per row,
  and that Save returns to keep a chunk; the "(a pass may not load or create entities, EECS041)" wording in the page
  if it repeats the old comment. The README's file map line for Systems/Chunks.cs if it names OnUpdate.
- skills/easyecs/SKILL.md: "work a pass refuses, or a budget per tick: [ForEach(MaxRows = N)], not OnUpdate +
  CollectIds".
- decisions.mdx + zh: the decision record (draft in the item-9 report); roadmap: item 9 done; changelog (New) and
  README "What's new".
- glossary: "budget" (a cap of rows per run, game engines' time-sliced work) if the docs pass keeps the word.

## feat-generator-cost (G1): declaration models apart from body facts

- architecture.mdx + zh (generator section, the paragraph on the incremental rules): type models (runners, record
  passes, [EntityMethod] types, systems) are built from declarations only, one per type by the node holding its
  first attribute (no Collect regroup); body facts (stable, parallel, lookups, vector loop, pair, chases, fusion
  side) are a model per method merged only into implementation outputs, so a keystroke inside a body reruns no
  source output; one SemanticModel per syntax tree per compilation for the vector loops.
- architecture.mdx + zh (SystemGenerator row): a runner's and an [EntityMethod]'s entry points are a source output
  (`<Type>.ForEach.EntryPoints.g.cs`, `.EntityMethod.EntryPoints.g.cs`) that call a `static partial void` pass; the
  implementation output writes the pass and the job, so the IDE keeps the members while typing.
- guide/queries.mdx + zh, "The generator writes" tab of `[EntityMethod]` (RequestsEntityMethod): now
  `public static partial class RequestsEntityMethod` whose `TakeHit` sets `bool ran = false;`, calls
  `RequestsEntityMethod.__TakeHit_0(unit, damage, ref ran)` and returns `ran`; the pass (`static partial void`,
  AggressiveInlining) does `if (unit.World is not { } world) return;` then builds the job and
  `ran = unit.Run<...>(ref job, in filter)`.
- decisions.mdx + zh: the decision record of this branch (draft in the G1 report).
