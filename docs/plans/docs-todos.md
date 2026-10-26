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
      for Consume / ForEntity / SystemDispatch; search-expectations.json (ForEntity, Consume, entity.Run);
      high-performance "What it costs" section; grids "generator writes" tabs against real output; VoxelServer TODOs;
      example.mdx walkthrough vs BattleServer.cs regions; changelog / README What's new / migration complete.
- [ ] `tools/bench.sh --site --sync` on the final code (classes missing from bench.json: Consume, ForEntity,
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
filter-less passes, Mut column, ForEntity filter, tile neighbors, attribute reductions vectorize, column loads,
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
- P9 ChunkRows inference: only after feat-sched autotuner tries < 1024 rows, with benchmarks.
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
- Do: infer ChunkRows after feat-sched merges, if BDN shows inferred >= hand-set (task).
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
- Changelog / README What's new / migration: float Reduce<T> without ChunkRows on a world whose MaxDegreeOfParallelism != 4 now gets different (thread-independent) bits.
- guide threading/reductions: determinism without ChunkRows (16 chunks for merging passes).
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
- roadmap.mdx + zh: one Planned item for docs/plans/inner-loops.md's goal (plain loops over buffers / inline arrays / ForParallel lambdas become vector loops; Cell<TCell,TTile> shaders; EECS103), linking D77/D90. Rewrite the stale "Vector loops: the forms left for later" item (Pow and kin partly done by D127; masked tail D126).

## feat-lookupkey-chunkrows (D129, D130, merged)
- Changelog / migration / README What's new: [IndexKey] -> [LookupKey] (code fix), measured chunk sizes (ChunkRows only overrides).
- threading guide #chunk-rows + line 24 (stop telling users to set ChunkRows for heavy rows); analyzer guide EECS054 "when to set it"; api.mdx complexity; performance / why-fast chunking; public skill ChunkRows line; roadmap.
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
