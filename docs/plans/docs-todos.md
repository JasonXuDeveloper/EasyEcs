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
- `api.mdx`: nothing (`Lookup<T>.Prefetches` / `PrefetchAhead`, `LookupLanes.Prefetches` are hidden, for generated code).

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
  buffer), `SortRows<T>`, what a sort costs (RowOrderBenchmark: BenchTable), `world.Blocks<T>()` / `entities.Blocks` /
  `RowBlocks.Pairs` for hand-written searches until the pair form ships, refs and handles ending at a sort (like any
  structural change), saves keeping the row order and redrawing the boxes at the next update.
- refs-and-await.mdx + zh: a sort is a structural change at the start of an update (re-acquire refs after it).
- saves.mdx + zh, snapshots.mdx + zh: the row order is kept, boxes redrawn (D137).
- analyzer.mdx + zh: EECS104 (section, table row; EECS range) and EECS041's list (SortRows, OrderRows).
- high-performance.mdx + zh: Lookup targets ordered by their target read near rows (LookupOrderBenchmark: Chase 2x,
  64-table gathers 4.6x at 1M); `Lanes.RowNumbers` for a row list from a mask; Compact on every ISA (NEON, SSSE3).
- architecture.mdx + zh: RowOrder in the table diagram (Archetype.Order, Permute), the block side array.
- glossary + zh: ordered table (有序表), block (块, not the parallel chunk), box (包围盒), margin (余量), cadence (节奏);
  tools/ApiDocs/zh/README.md glossary gets the same rows.
- changelog / README What's new: ordered tables (the read path), Lanes.RowNumbers, Compact on ARM64.
- Benchmarks into bench.json: RowOrder, LookupOrder, Pairs (Core*), Compress (CoreCompact) after the reference run.
- skills/easyecs/SKILL.md (public skill): ordered tables (the order is inferred; `[OrderBy]` only for orders no pair
  method implies; refs end at a sort), `Blocks` / `Pairs` for hand-written neighbor searches, `Lanes.RowNumbers`.
- The Native AOT listing of `Lanes.Compact`'s loops on the owner's AOT check (the tables are RVA data now; no class-init
  check expected) and the M4 rerun of `CompressBenchmark` (the 128-bit TBL form against the prototypes' ShuffleTable).

### Decision record drafts (D137, D138)

The records for decisions.mdx / .zh.mdx, written in the docs pass (index rows after the last record; renumber if
D137 / D138 are taken by then, and fix the numbers in `Declarations.cs`, `RowOrder.cs`, `tools/ApiDocs/zh/orderbyattribute.xml`,
roadmap). The zh drafts carry the decision and what was considered; their **数据** sections are still to translate
from the English. Raw reports: BenchmarkDotNet's markdown, quoted below; the snapshots were frozen copies of the
branch (A = 0e206d8 + the NEON prototypes, B/F = this branch).

<details><summary>D137 (en)</summary>

## D137. Tables keep their rows in the order of a key, with a box per block of rows [#d137-ordered-rows]

**Decision.** A table can keep its rows sorted by a key and draw an axis-aligned box over each block of rows: the
layout a pair kernel reads (a block of rows against a block, the pairs of blocks found from the boxes), and a side
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
- **Blocks.** 8 rows where `Vector256` is hardware, 4 otherwise (ARM64's NEON is 128 bits wide), or the power of two
  up to 64 a request asks for, so a kernel's block is one register of floats per axis. The sort draws each block's box
  over `axes` axes of `Point`, widened by the margin; a row that moves into a block between sorts widens that block's
  box; rows created after the sort wait past `SortedRows` until the next one. The boxes live in a side array of the
  order (bounded by the table's blocks, released by `TrimExcess`), never in a hot struct. `world.Blocks<T>()` and
  `EntitySpan.Blocks` hand out `RowBlocks`: the boxes, a block's rows, read-only columns, ids, presence, and
  `Pairs(reach, into)` / `Pairs(other, reach, into)`, the block pairs whose boxes come within reach (a sweep over the
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
  The block-pair search was the largest part of a rebuild: gathering the other sides into the sorted order once (instead of copying a table's sides per candidate) took the 100k rebuild from 11.0 / 11.3 to 9.9 / 10.3 ms in a crowd and from 5.77 / 5.75 to 5.08 / 5.14 ms sparse.
- **Blocks of 16** (`CoreClusters16`, two 8-row blocks per 512-bit kernel step): within 5 % of 8 at 1k and 10k in a
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
- **Hilbert keys**: better locality per block than Morton, more instructions per key; the boxes absorb Morton's jumps
  and the sort's cost is the permutation, not the keys.
- **Sorting on every write, or a tree of rows**: GROMACS re-sorts on a cadence; the margin keeps the boxes valid
  between sorts, and a sort of rows already in order costs the keys and the boxes only.
- **Blocks of 16 rows**: see the numbers.
- **Moving only the rows out of order**: a random 1 % churn already moves most rows, so the sort moves every row once.
- **A box tree for the block pairs** (the prototype's): one walk per block; the sweep over sorted low sides is
  simpler, needs no tree to keep, and is parallel-safe (it reads the boxes only).
- **Saving the order and the boxes**: see Saves.

</details>

<details><summary>D138 (en)</summary>

## D138. Compaction picks its instructions at run time on every machine; `Lanes.RowNumbers` is the one row-list helper [#d138-compaction-everywhere]

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
per lane widened by the load (`VPMOVZXBD`), 2 KiB. `Lanes.RowNumbers(mask, first, destination)` writes the row numbers a
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
  `Lanes.Compact` and `Lanes.RowNumbers` stay the only entry points.
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

## D137. 表按键的顺序保存行，每块行一个包围盒 [#d137-ordered-rows]

**决定。** 一张表可以让它的行按某个键排好，并在每块行上画一个轴对齐包围盒：这是成对内核读取的布局（一块行对一块行，
块对由包围盒找出），也让每个读取其他实体的遍历顺带受益：按目标排好的读取方读到的记录和行彼此相近。

- **谁来要求。** 顺序通常是推断出来的：生成的方法若在一个截断距离内比较位置，它的初始化就为读取位置的组件调用
  `World.OrderRows<T, TOrder>(axes, cell, interval, margin)`。`TOrder` 是实现 `IRowOrder<T>` 的结构体，带静态的
  `Key` 和 `Point`（没有委托，每种键类型编译出一份排序）；键是按截断距离量化后位置的 `RowKeys.Morton`（每轴 63、32 或
  21 位，用整数钳位）。这个要求覆盖现在和以后的表；一个组件以第一次要求为准。成员上的 `[OrderBy]`（生成它的
  `IOrderKey<TKey>`）对所有世界覆盖推断：非空间的键，或另一个格子大小。没人要求顺序的表，只在行移动或行数收缩处多一次判空。
- **排序。** 对 (键, 行) 对做 LSD 基数排序，缓冲区来自池，键放得下时用 32 位，所有键共有的字节跳过，行已经有序时什么也不搬；
  然后在结构门下对每一列、存在位、变更 tick（字和块的摘要由 tick 重建）和槽做一次置换（`Archetype.Permute`），并增加
  `StorageVersion`。没有这个组件的行排在最后。它每隔 `interval` 次更新在默认组更新开始时运行（GROMACS 的 `nstlist`），
  或用 `SortRows<T>()` 立即运行；两者在遍历中都会抛出（EECS041 列出它们）。ref、句柄缓存的行和标记的行在排序时失效，和任何行移动一样。
- **块。** `Vector256` 有硬件支持时 8 行，否则 4 行（ARM64 的 NEON 宽 128 位），或要求指定的不超过 64 的 2 的幂，
  这样内核的一块正好是每个轴一个浮点寄存器。排序在 `Point` 的 `axes` 个轴上画出每块的包围盒，再加上余量；两次排序之间
  移进某块的行会扩大那块的包围盒；排序之后新建的行排在 `SortedRows` 之后，等下一次排序。包围盒放在顺序的旁路数组里
  （以表的块数为界，`TrimExcess` 释放），从不进热结构。`world.Blocks<T>()` 和 `EntitySpan.Blocks` 给出 `RowBlocks`：
  包围盒、一块的行、只读列、id、存在位，以及 `Pairs(reach, into)` / `Pairs(other, reach, into)`：包围盒相距在 reach
  之内的块对（按第一轴低边排序后扫描，其他边按这个顺序收集）。
- **存档。** 恢复或读档保留它读到的物理行序（存档按表内顺序写行），丢掉包围盒；下一次更新会排序（已经有序的行只花键和包围盒的开销）
  并重画它们。把包围盒写进存档会把格式和节奏绑在一起，而第一次更新本来就会重建它们。
- **EECS104** 报告生成的部分无法维护的 `[OrderBy]`：键不是普通值（结构体、字符串）、包围盒成员不是 float 或一个
  `Vector2` / `Vector3`、interval 或 margin 不合法、两个键、单例，或者同时手写了 `OrderKey` / `BoxPoint`。

**数据。** NUMBERS

**考虑过的方案。**
- **以面向用户的特性为主入口**：用户要为成对方法读取的每个组件写出键和格子；而生成器已经看到距离判断和截断距离。
  `[OrderBy]` 留给没有方法暗示的顺序。
- **Hilbert 键**：每块的局部性比 Morton 好，但每个键的指令更多；包围盒吸收了 Morton 的跳跃，排序的开销在置换，不在键。
- **每次写入都排序，或行的树**：GROMACS 按节奏重排；余量让包围盒在两次排序之间保持有效，已经有序的行排序只花键和包围盒的开销。
- **16 行的块**：见数据。
- **只搬不在位置上的行**：随机 1 % 的变动已经搬动大多数行，所以排序把每行搬一次。
- **块对用包围盒树**（原型的做法）：每块走一遍树；按低边排序后扫描更简单，没有树要维护，也可以并行（只读包围盒）。
- **把顺序和包围盒存进存档**：见存档。

</details>

<details><summary>D138 (zh)</summary>

## D138. 压缩在每台机器上运行时选择指令；`Lanes.RowNumbers` 是唯一的行列表辅助 [#d138-compaction-everywhere]

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
只写选中的个数（向量步骤留出一整步的空间，或在边上退回标量遍历）。`Lanes.RowNumbers(mask, first, destination)` 写出掩码选中的行号
（对 0..63 做 `Compact`，再按向量加上 `first`）：生成的循环或核心要行列表时只调这一个，所以生成的代码从不写出指令集的名字。

**不用压缩的地方。** 生成循环里的 TZCNT 遍历（`ArityGenerator` 的掩码遍历、`SystemGenerator.Fusion`）每行或每段行调一次方法；
先做行列表只多一遍，不少一次调用。`MarkedRows` 保留位字（应用它们是每行一次结构变更，那才是大头），`World.Bulk` 的遍历也一样。
`Kernel.Structural` 生成的 `Emit.AddIds` / `AddLanes` 已经走 `Compact`。

**数据。** NUMBERS

**考虑过的方案。**
- **每个指令集一个公开的压缩 API**：用户要自己写核心已经做好的分派；`Lanes.Compact` 和 `Lanes.RowNumbers` 仍是仅有的入口。
- **8 字节通道的 128 位形式**（每步两个通道，每 2 位一项的 4 项表）：每个字 32 步，输给标量遍历，没有 AVX2 时
  `CompactBenchmark` 在 1M 个 id、选中 50 % 时 1.19 ms 对 1.00 ms；放弃。
- **跳过掩码空的部分的步骤**：每步一个分支，在混合掩码上预测失败；带这些分支的 128 位路径在选中 50 % 时比标量遍历慢 3 倍。
- **每 16 位一张表**（没有 `VPCOMPRESS` 时 AVX-512 的宽度）：2 MB，而 8 KB 和 256 B 的表和读它的循环一起留在 L1。
- **没有 AVX-512 时用标量遍历**：核心以前的做法；NUMBERS_SCALAR。

</details>
