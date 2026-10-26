# Backlog: what is left before 4.8 (internal, not published)

The single list of open work, kept in the repository so a new session finds it (AGENTS.md, "Plans that span sessions
are committed"). Update it when an item starts, merges or is dropped. Designs live next to it (`inner-loops.md`);
docs to-dos collected while code still changes live in `docs-todos.md`.

Order: code -> exemplar pass (`audit-code`) -> docs pass (`audit-docs`) -> bench sync -> `verify` -> master.
At most 3 code agents at once; benchmarks serialize on `/tmp/bench.lock`.

## Running (branches of their own; merge with the `audit` triage checks)

- [ ] **Lookup gathers** (#162 part 3, D131): vectorized reads of other entities, sorted by target table; EECS055
      LooksEligible also lets Entity / Emit parameters through. Branch `feat-lookup-gather`.
- [ ] **EECS103** (WP6a, D134): hand-written SIMD the generator would write from a plain method, with a code fix.
      Worktree `.claude/worktrees/feat-eecs103`.
- [ ] **Analyzer tests split**: GeneratorTests.cs -> AnalyzerTests.* files (mechanical).
- [x] **zh reference fix** (merged af585ed): Chinese restored, ApiDocs keeps a page it would write in English and
      exits 1; leftovers in `docs-todos.md`.
- [x] **Generated text survey** (`generated-text.md`); rules in `audit-generator` "The generated text" and `feature`.
- [x] **Generator / analyzer cost, phase 2** (merged after 5f412b6): A1-A5, G2, G5, A7 (one analyzer per area),
      PartGenerator without Collect, EquatableArray loops; CompileAndAnalyze -8 % / -2-3 %, edits within noise (the
      benchmark corpus reaches few of the slow paths phase 1 found in the unit tests). Left: **G1** (ForEachModel
      split into a declaration model and implementation-only body facts; SystemUpdates without Collect; SetFusions
      reruns; Systems / SystemPlaces share one FAWMN; Kernel's GetSemanticModel calls) after WP0b merges; G6 research.

- [ ] **Loads write each field into its name** (branch `research-copy-asm`: 1e21668 Kernel.cs, 0ff81cc 32-local test,
      71f03f1 Move3Gaps benchmark). Root cause of D126's "copy changed the asm": the JIT inlines no method with more
      than 32 IL locals; the copy took Move3's masked Execute to 36. Move3Gaps 100k -5.5 %, Move4 1k -8 %, others level;
      WorldSimulation 100k and BattleServer means +2-4 % inside base's spread on a loaded machine. Gate: rerun those two
      on a quiet machine (after the generator A/B), then merge with D126 rewritten (text in the branch report:
      the root cause, the vmovaps cost, the numbers) and `generated-text.md` section 3 corrected. Nine jobs still over
      32 locals (StepJob0 masked 34, CycleJob9 masked 33, GateJob26 / SeekJob14 40-42): WP0b.
      Now carried by WP0b (branch `feat-wp0b` starts on it; WP0b's A/B covers the copy fix against the tip).
      The remote branch `research-copy-asm` could not be deleted from the session (proxy); the owner deletes it.

- [ ] **Layout instead of gathers** (phase 0 merged 0e206d8: pairs 3-16x, compress 5-42x, nearest packed only; batch probes, timer wheel dropped to owner-review; L1 ordered rows + L3 compress running on `feat-locality-core`; L2 pair form after WP0b; owner: redesign the layers under the vector loops after GROMACS's cluster pairs
      and Polychroniou SIGMOD 2015; `locality.md`): phase 0 hand-written benchmarks first (pairs, nearest, ordered
      lookups, compress / batch probes), each with its bar; starts after gather-always reports and WP0b merges.
- [ ] **Borrowed ideas** (`borrowed-ideas.md`): prefetching chases (AMAC) after gather-always, a timer wheel for
      waiting entities, ghost rows between worlds as an example first, streaming stores, roofline in `audit-perf`;
      NUMA / hybrid cores wait in `owner-review.md` for a machine that has them.
- [ ] **Generated code tuned for the machine** (owner: users never think about pipeline, caches, branches;
      `microarch.md`): M0 llvm-mca over every kernel shape after WP0b merges, then M1 accumulators / unrolling /
      hoisting, M2 branchless scalar rows / mask tests / "make it a tag" rule, M3 prefetch / streaming stores.
- **Order set by the owner (2026-10-26):** cluster pairs and compress first (L1 + L3 running, then L2 right after
  WP0b), then the rest in the order of this list; every SIMD path covers ARM64 (`audit-vector-loops`); docs last.
- [ ] **Vector loops through the user's own calls** (owner: users should not have to avoid helpers): today a call into
      user code keeps the method scalar (EECS055 "it calls X.Y"); only Math, the generated members (SetX / IsX ...),
      Emit.Add, Lookup reads and view members (Kernel.Views.cs, inlined from source, depth 8) are translated. Extend the
      view inliner to any callee whose source is in the compilation: static helpers, extension methods, component
      instance methods and properties with bodies (same limits: no recursion, no ref out of the row, no lambdas; an
      early return becomes a mask). Calls into other assemblies, virtual / interface calls stay scalar with that reason.
      After WP0b (Kernel). Docs: list what is inlined and what is not. Generator cost (the owner's condition): callee
      bodies are read only in the implementation-output pipeline, once per callee per compilation (a cache keyed by
      the method symbol, shared by every caller), syntax-first rejection before any bind, no copy of the compilation
      per caller (Kernel.Views copies it today: measure that first with GeneratorBenchmark's IDE and build cases).
- [ ] **ARM runs of the locality prototypes** (owner wants to run them on an ARM device): the prototypes are
      Vector256 / AVX2 / AVX-512 only, so on ARM64 they measure the software fallback of Vector256 (not accelerated
      there); add Vector128 / AdvSimd paths (TBL permutation for compress, a 4-wide or Vector<T> pair kernel) and an
      ISA column in the results.
- [ ] **Lookup gathers always on** (owner: they should win everywhere, then no option; branch `feat-gather-always`):
      diagnose the 64-table loss; lead: unmanaged columns are pinned (POH), so a per-table column-address table could
      let one vector gather across tables GC-safely; widen LookupGatherBenchmark (1/4/64/1,000 tables, dead lanes,
      presence bits, light/heavy body); default under Auto only if every case wins, D131 rewritten.

## Next

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
