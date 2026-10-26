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
      the core's callers; the prototypes get Vector128 / NEON paths so the owner can run `PairsBenchmark` and
      `CompressBenchmark` on ARM64. New EECS id for an invalid key; decision records; naming candidates to the owner.
- [ ] **WP0b** (`inner-loops.md`; worktree `wp0b`, branch `feat-wp0b`): every generated job within the JIT inliner's
      32 IL locals (loads into their names, constants in place, oversized Execute split per loop), D126 rewritten,
      `tools/inlining.sh`; gate: WorldSimulation / BattleServer not slower. Blocks L2, G1, the call inliner, M0.
- [ ] **Lookup gathers always on** (worktree `gather-always`, branch `feat-gather-always`): the 64-table loss
      diagnosed, per-lane column-address gathers on pinned columns, widened `LookupGatherBenchmark`; default under Auto
      only if every case wins (per ISA: ARM64 has no gather), D131 rewritten.

## Next, in this order

1. [ ] **L2: the generated pair form** (`locality.md` 3.2): the user writes what one pair does; the generator writes
       the 8x8 (x86) / 4x4 (ARM64) cluster-pair kernel over L1's blocks with cutoff and self masks, and a nearest form
       (`NearestPlayer`, zombie targeting). Right after WP0b merges. Then L4: examples rewritten in it.
       The owner's call on the spread map (nearest 1.06-1.31x, bar 1.5x): re-measure once L1 sorts on its cadence,
       with a flat grid of blocks searched in rings instead of the prototype's box tree; the nearest form stays in L2.
2. [ ] **Vector loops through the user's own calls** (owner: users should not have to avoid helpers): today a call
       into user code keeps the method scalar (EECS055 "it calls X.Y"); only Math, the generated members (SetX / IsX
       ...), Emit.Add, Lookup reads and view members (Kernel.Views.cs, inlined from source, depth 8) are translated.
       Extend the view inliner to any callee whose source is in the compilation: static helpers, extension methods,
       component instance methods and properties with bodies (no recursion, no ref out of the row, no lambdas; an
       early return becomes a mask). Calls into other assemblies, virtual / interface calls stay scalar with that
       reason. Generator cost (the owner's condition): callee bodies read only in the implementation-output pipeline,
       once per callee per compilation (cache keyed by the method symbol), syntax-first rejection before any bind, no
       compilation copy per caller (Kernel.Views copies it today: measure first, IDE and build cases).
3. [ ] **G1** (generator cost, the part phase 2 left): ForEachModel split into a declaration model and
       implementation-only body facts; SystemUpdates without Collect; SetFusions reruns; Systems / SystemPlaces share
       one FAWMN; Kernel's GetSemanticModel calls. G6 (C# 14 extension lookup cost) research only.
4. [ ] **Generated code tuned for the machine** (`microarch.md`): M0 llvm-mca over every kernel shape, then M1
       accumulators / unrolling / hoisting, M2 branchless scalar rows / mask tests / "make it a tag" rule, M3
       prefetch / streaming stores.
5. [ ] **Borrowed ideas** (`borrowed-ideas.md`): prefetching chases (AMAC) after gather-always, ghost rows between
       worlds as an example first, streaming stores, roofline in `audit-perf` (timer wheel and NUMA dropped by the
       owner: records in the docs pass). The owner's call on batch probes of ValueIndex / UniqueIndex (1.3-3.4x slower
       at 1k / 100k keys): keep, re-measure after AMAC with tables far past the last-level cache (16M keys), against
       scalar group prefetching.
6. [ ] The rest of "Later" below.

## Done this round (for the record)

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
- [ ] **Inner loops** (`inner-loops.md`), in this order: WP0b (running, branch `feat-wp0b`: every inlined method <= 32 IL
      locals, the split forms, IL-changing cleanups, masked tail retried) (generated bodies split to the inliner's budget,
      runners pinned free of `call ...Execute`), WP0 (Binding / BodyCopy), WP2 element loops, WP3 offsets,
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
- [ ] #153 benchmark job tuning (Warmup 6 -> 3, IterationTime 250 -> 150 ms) and a lighter job for GeneratorBenchmark.
- [ ] Recheck on a quiet machine: GroupBenchmark.Scan, OperationBenchmark Toggle.

## Finish

- [ ] **Exemplar pass** (#169, `audit-code`): VoxelServer / samples + templates + demo / benchmark worlds + usage
      tests rewritten in the newest recommended forms, A/B on rewritten benchmark worlds.
- [ ] **Folder restructure** of Core and Generator (`git mv` only), if still wanted (`docs-todos.md`).
- [ ] **Docs pass** (#141): everything in `docs-todos.md`; DocCheck (296 problems) and ApiDocs (46) to 0; decision
      record for the 8 declined declarations; roadmap item for inner loops; analyzer range EECS002-103.
- [ ] `tools/bench.sh --site --sync`, README numbers, `verify`, master. The owner cuts the release.
