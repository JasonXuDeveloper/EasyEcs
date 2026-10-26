# Backlog: what is left before 4.8 (internal, not published)

The single list of open work, kept in the repository so a new session finds it (AGENTS.md, "Plans that span sessions
are committed"). Update it when an item starts, merges or is dropped. Designs live next to it (`inner-loops.md`);
docs to-dos collected while code still changes live in `docs-todos.md`.

Order: code -> exemplar pass (`audit-code`) -> docs pass (`audit-docs`) -> bench sync -> `verify` -> master.
At most 3 code agents at once; benchmarks serialize on `/tmp/bench.lock`.

## Order (set by the owner, 2026-10-26)

Cluster pairs and compress first, then the rest in the order of "Next"; every SIMD path covers ARM64
(`audit-vector-loops`, "Every vector form runs on ARM64 too"); generators stay fast while they read more source; docs
last (`docs-todos.md` collects them meanwhile).

## Running (3 agents, worktrees under `.claude/worktrees/`; merge with the `audit` triage checks)

- [ ] **L1 ordered rows + blocks, L3 compress** (`locality.md` section 5; worktree `agent-aef3c58e0c81ecff5`, branch
      `feat-locality-core`): a declared key orders a table's rows, re-sorted on a cadence (radix sort, one permutation
      of every column under the gate, `StorageVersion`), a box per block of 8 rows and a read path (`RowBlocks`) for
      the generated pair form; compress (AVX-512 CompressStore / AVX2 table / ARM64 TBL) replaces `Lanes.Compact` in
      the core's callers; the prototypes' Vector128 / NEON paths are merged (eedfeb0, pushed). L1 gives systems an order they request at
      setup (3.4), not only `[OrderBy]`; L3's compress is one core helper the generated loops call too. New EECS id for an invalid key; decision records; naming candidates to the owner.
- [x] **Lookup gathers always on: not possible, stay on request** (merged after 932913f): no form beats rows when a
      vector's targets spread over many tables (walk, 64-bit pinned addresses, micro-adaptive choice all measured,
      D131 rewritten with the widened table and ARM64: rows there).

- [ ] **AMAC prefetch for lookup chases, then batch probes re-measured at 16M keys** (`borrowed-ideas.md` item 1;
      worktree agent, branch `feat-amac`): core only, generator changes listed for after WP0b.

- [ ] **ARM64 parity** (worktree agent, branch `feat-arm-parity`): wide components through NEON TBL, a repo-wide audit
      for x86-only paths, the ARM64 CI job asserting which forms run as vectors. (Interleaved LD2-4 / ST2-4: a296ecb.)

The owner's order (2026-10-26): every code change before any docs (generator cost and ARM64 parity included).
CI runs the unit tests on ARM64 (b1dbdb0).

## Next, in this order

1. [ ] **L2: the generated pair form** (`locality.md` 3.2, 3.4: the owner's rule that users' plain code adapts by itself: order inferred from the pair method, nested walks rewritten, hand-written grids get a code fix): the user writes what one pair does; the generator writes
       the 8x8 (x86) / 4x4 (ARM64) cluster-pair kernel over L1's blocks with cutoff and self masks, and a nearest form
       (`NearestPlayer`, zombie targeting). Right after WP0b merges. Then L4: examples rewritten in it.
       The owner's call on the spread map (nearest 1.06-1.31x, bar 1.5x): re-measure once L1 sorts on its cadence,
       with a flat grid of blocks searched in rings instead of the prototype's box tree; the nearest form stays in L2.
       The owner's call on Lookup gathers under `SimdMode.Auto` (2026-10-26): once L1 and WP0b have merged, re-measure
       the micro-adaptive choice (walk vs rows, D131 Considered) with L1's ordered rows and the lookup job under 32
       IL locals; on by default only if no case is slower. No per-entity table id.
       ARM64 (owner's M4 run, `locality.md` 6.1a): crowded maps 2-8x, sparse maps only 1.1-2.1x while neighbor lists
       reach 2.5-3.4x: L2 ships clusters there too (faster is kept, owner) and optimizes: 4-row blocks and the
       neighbor-list form on ARM64, picked by density; never a slower case (no re-sort every tick on sparse maps).
2. [ ] **ARM64 parity of the generated forms** (running, see above) (owner, 2026-10-26: every generated SIMD path runs on ARM64; table in
       `audit-vector-loops`, "Every vector form runs on ARM64 too"): an ARM64 CI job first (unit tests on GitHub's
       ARM64 Linux runner, `__loops` lines printed), then multi-field / `Vector2/3/4` components through LD2-4 / ST2-4,
       then wide components through TBL. Touches SystemGenerator / Kernel: after WP0b, beside L2. Proof: tests with
       `DOTNET_EnableAVX=0` here, the ARM64 job, the owner's ARM64 BenchmarkDotNet numbers.
3. [ ] **Vector loops through the user's own calls** (owner: users should not have to avoid helpers): today a call
       into user code keeps the method scalar (EECS055 "it calls X.Y"); only Math, the generated members (SetX / IsX
       ...), Emit.Add, Lookup reads and view members (Kernel.Views.cs, inlined from source, depth 8) are translated.
       Extend the view inliner to any callee whose source is in the compilation: static helpers, extension methods,
       component instance methods and properties with bodies (no recursion, no ref out of the row, no lambdas; an
       early return becomes a mask). Calls into other assemblies, virtual / interface calls stay scalar with that
       reason. Generator cost (the owner's condition): callee bodies read only in the implementation-output pipeline,
       once per callee per compilation (cache keyed by the method symbol), syntax-first rejection before any bind, no
       compilation copy per caller (Kernel.Views copies it today: measure first, IDE and build cases).
4. [ ] **G1** (generator cost, the part phase 2 left): ForEachModel split into a declaration model and
       implementation-only body facts; SystemUpdates without Collect; SetFusions reruns; Systems / SystemPlaces share
       one FAWMN; Kernel's GetSemanticModel calls. G6 (C# 14 extension lookup cost) research only.
5. [ ] **Generated code tuned for the machine** (`microarch.md`): M0 llvm-mca over every kernel shape, then M1
       accumulators / unrolling / hoisting, M2 branchless scalar rows / mask tests / "make it a tag" rule, M3
       prefetch / streaming stores.
6. [ ] **Borrowed ideas** (`borrowed-ideas.md`): prefetching chases (AMAC) after gather-always, ghost rows between
       worlds as an example first, streaming stores, roofline in `audit-perf` (timer wheel and NUMA dropped by the
       owner: records in the docs pass). The owner's call on batch probes of ValueIndex / UniqueIndex (1.3-3.4x slower
       at 1k / 100k keys): keep, re-measure after AMAC with tables far past the last-level cache (16M keys), against
       scalar group prefetching.
7. [ ] The rest of "Later" below.

## Done this round (for the record)

- [x] **WP0b** (merged, D126 rewritten, D135): every generated job within the inliner's 32 IL locals (split per width,
      constants and kept lanes in place, the loads' copy gone), `tools/inlining.sh`; WorldSimulation / BattleServer
      level, Step / Share / Gate / Seek 6-22 % faster; masked AVX-512 tail retried and dropped (slower). Open
      follow-ups: GeneratorBenchmark A/B, a locals check over the samples, `.Zero` / `.One` text, one `__d0` mark,
      discards for unused fields, aliases (with "Generated text cleanup").

- [x] Lookup gathers on request (D131, merged f1767ef), EECS103 (D134), analyzer tests split, zh reference fix
      (af585ed), generated text survey (`generated-text.md`), generator / analyzer cost phase 2 (merged before
      c10af12: one analyzer per area, body facts once, syntax-only shapes), locality phase 0 (0e206d8: pairs 3-16x,
      compress 5-42x, nearest packed only; batch probes and timer wheel in owner-review), the copy fix (carried by
      WP0b). The remote branch `research-copy-asm` could not be deleted from the session (proxy): the owner deletes it.

## Later

- [ ] **Analyzer and incremental generators by the official .NET guidance** (owner, 2026-10-25; phase 2 above is
      its first part): refactor what does not follow it (incremental pipeline: ForAttributeWithMetadataName, equatable symbol-free
      models, no CompilationProvider in a pipeline that runs per edit, cached steps proven by tracking names;
      analyzer: RegisterSymbolStartAction / operation actions over syntax walks where cheaper, no per-node semantic
      model work, compilation-end rules only where needed, `EnableConcurrentExecution`, generated code skipped).
      Measured before and after (GeneratorBenchmark, ReportAnalyzer: UnitTest analyzer 40 s, generators 4 s).
- [ ] **Inner loops** (`inner-loops.md`), in this order (WP0b done): WP0 (Binding / BodyCopy), WP2 element loops, WP3 offsets,
      WP5 `Cell<TCell,TTile>` (bar: hand-written speed), WP5b (per-tile results, several inputs/outputs, VoxelServer
      `Flow.cs`), WP1 InlineArray, WP4 ForParallel lambda, WP6b/c EECS103 v2/v3, WP8 fixable-reason audit (plain code
      a small rewrite would vectorize warns).
- [ ] **Generated text cleanup** (`generated-text.md`, ranked; one agent, SystemGenerator owner, after lookup gathers):
      1 `GeneratedTextTests` guard first (fails on today's output, allow-list shrinks as each step lands);
      2 alias block (-20 % text, IL identical); 3 fusion files run the systems' jobs (-76 % fusion output);
      4 printer fixes and names (IL identical); 5 empty hooks as `ISystemCode` defaults; 6 one Tier1-gated round of
      the IL-changing cleanups (`__d0`, constants, hoisted setup, `__t`, dead temps, duplicate tables);
      7 GeneratorBenchmark prints output bytes. Leave the D126 `__s -> __c` copy (comment it).
- [ ] **Generated code simplification**: continues inside WP0 / WP0b. Done: flat lanes, the pass mask as the start of
      the active mask, research lessons recorded. Reverted: loads into their own names (Tier1 asm grew, Move3 runner
      934 -> 1095 instructions). Not done: file-level `using` aliases per closed generic. Gate: identical or better
      Tier1 asm (`tools/jitdisasm.sh`) and BenchmarkDotNet. Targets seen in a generated job (Showcase DeathSystem):
      the same body written once per width (Vector512 block, then Vector<T> block): one body per width method or one
      emitter call per width with the width as a parameter; `global::` everywhere: file-level `using` aliases;
      identity copies (`var __c1_0 = __s1_0`); per-width constants re-created (`__u1`, `this.v0 = default`);
      `__d0 = Zero; __d0 |= x` where `__d0 = x` would do. Users read this code (docs "The generator writes" tabs), so
      readability counts once the asm is equal.
- [ ] Follow-ups: lambda passes do not fuse; lambda + structural change and lambda + view stay scalar.
- [ ] #160 wide rows on AVX2 (heavy math only, per the widelanes numbers); #164 NativeMemory 2 MiB columns (research
      first); #157 the #138 recommendations; #159 remaining research.
- [ ] #153: the `--dev` job is in (screening); still open: check the default job could drop to Warmup 3 / 150 ms with
      the same results (two rounds of a few classes at both settings), and a lighter job for GeneratorBenchmark.
- [ ] Recheck on a quiet machine: GroupBenchmark.Scan, OperationBenchmark Toggle.
- [ ] Flaky under full-suite load with `DOTNET_EnableAVX2=0`: `AllocationTests.ParallelReductionsScratchAndLookupsDoNotAllocate`
      (1 failure in 2 runs, passes alone): find the measurement race (#166's kind).

## Finish

- [ ] **Exemplar pass** (#169, `audit-code`): VoxelServer / samples + templates + demo / benchmark worlds + usage
      tests rewritten in the newest recommended forms, A/B on rewritten benchmark worlds.
- [ ] **Folder restructure** of Core and Generator (`git mv` only), if still wanted (`docs-todos.md`).
- [ ] **Docs pass** (#141): everything in `docs-todos.md`; DocCheck (296 problems) and ApiDocs (46) to 0; decision
      record for the 8 declined declarations; roadmap item for inner loops; analyzer range EECS002-103.
- [ ] `tools/bench.sh --site --sync`, README numbers, `verify`, master. The owner cuts the release.
