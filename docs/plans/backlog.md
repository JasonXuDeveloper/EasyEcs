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

The owner (2026-10-26): every open item of this file is done, in this order, nothing left over: Running, Next,
Later, then Finish (docs included). Each item runs through the skill that owns its work, and its agent's prompt
names the skill:

| Item | Skills |
|---|---|
| any change before it merges | `feature` (definition of done), `audit-naming` on every new name |
| L1 / L3, L2 / L4, call inliner, inner loops, follow-ups, #160 | `feature`, `audit-vector-loops`, `audit-generator`; `audit-analyzer` for a rule or code fix |
| G1, analyzer and generators by the official guidance, generated text cleanup, simplification | `audit-generator`, `audit-analyzer` |
| prefetch gate, microarch M0-M3, borrowed ideas, #164, #157, #159, #153, quiet rechecks | `audit-perf` (+ `audit-vector-loops` for generated loops) |
| flaky allocation test, exemplar pass, folder restructure | `audit-code` (+ `audit-core` for core code) |
| merging an agent's branch | `audit` (triage and merge checks) |
| docs pass | `audit-docs` |
| bench sync, master | `verify` |

## Running (3 agents, worktrees under `.claude/worktrees/`; merge with the `audit` triage checks)

- [ ] **L1 ordered rows + blocks, L3 compress** (`locality.md` section 5; worktree `agent-aef3c58e0c81ecff5`, branch
      `feat-locality-core`): a declared key orders a table's rows, re-sorted on a cadence (radix sort, one permutation
      of every column under the gate, `StorageVersion`), a box per block of 8 rows and a read path (`RowBlocks`) for
      the generated pair form; compress (AVX-512 CompressStore / AVX2 table / ARM64 TBL) replaces `Lanes.Compact` in
      the core's callers (M4: today's `Lanes.Compact` is 2-2.5x slower than the TBL table at >=50 %, `locality.md` 6.3; the helper must win every cell); the prototypes' Vector128 / NEON paths are merged (eedfeb0, pushed). L1 gives systems an order they request at
      setup (3.4), not only `[OrderBy]`; L3's compress is one core helper the generated loops call too. New EECS id for an invalid key; decision records; naming candidates to the owner.
      Status: built and handed back (merged the work branch at a69a7d6; full suite 2825 passed, the ISA subsets and the
      AOT check pass); decision records D137 / D138 drafted in docs-todos.md; the M4 rerun of CompressBenchmark is the
      owner's.
- [x] **Lookup gathers always on: not possible, stay on request** (merged after 932913f): no form beats rows when a
      vector's targets spread over many tables (walk, 64-bit pinned addresses, micro-adaptive choice all measured,
      D131 rewritten with the widened table and ARM64: rows there).

- [x] **AMAC prefetch for lookup chases, then batch probes re-measured at 16M keys** (`borrowed-ideas.md` item 1,
      `locality.md` 6.3; branch `feat-amac`): bar met at 16M only (1.29-1.36x), short at 1M (1.04-1.22x, the 1M world
      sits in this machine's 260 MiB L3), 1.9-2.3x slower at 1k: not built into the generated code. Prototype and the
      two hidden core helpers kept for a re-measure with a size gate on the owner's machines (owner's call). Batch
      probes: vertical forms dropped again (1.25-1.63x slower at 16M); scalar group prefetching 1.33x at 16M keys,
      1.3-1.6x slower in cache, no batch probe API to put it in.

- [ ] **Vector loops through the user's own calls** (worktree agent, branch `feat-call-inliner`; "Next" item 3).

- [x] **Prefetch with a cache-size gate + the 16-word locals bug** (branch `feat-prefetch-gate`, ready to merge; "Next"
      item 6): the 16-word row's `__Loop512` within 32 locals (or the reason, `Kernel.FitLocals`); lookup chases
      prefetched past the last-level cache (`LastLevelCache`, the row form's twin job `Job{i}Chases`, the vector loop's
      `PrefetchAhead` behind `f{i}`): 16M rows 1.36-1.43x, gathers 1.27-1.34x, nothing slower in the cache
      (`borrowed-ideas.md` 5, item 1). Docs to-dos and the decision draft in `docs-todos.md`.

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
       **Built on `feat-locality-pairs`** (`locality.md` 6.6): `[Neighbor]`, the pass, the generated order / job / vector
       loop, EECS105-107, cutoff per row, two kinds of entity, ties and the keep-target hint. Open: the spread-map nearest
       re-measure, the ARM64 neighbor-list form by density, writes to j (Pickup), parallel pair passes, the EECS107 fix,
       nested-walk rewrites, Lookup gathers under Auto. L4 (examples) moved to the finish phase (docs-todos).
       Follow-up after L2 merges (owner, 2026-10-27: "cluster pairs + prefetch, faster?"): prefetch the j block of the
       pair `k` pairs ahead (its rows' lines of every column the pair method reads) behind the same `LastLevelCache`
       gate as item 6; i blocks are sequential and need none. Expected to pay only when the ordered table is past the
       last-level cache (this machine's L3 is 260 MiB: PairsBenchmark at 4M-16M rows, plus a forced-on column just
       under the cache); ships only if faster there and never slower in the cache (`audit-perf`, two rounds, 0 B).
2. [x] **ARM64 parity of the generated forms** (merged 186c84d): LD2-4 / ST2-4 for multi-field components, NEON TBL /
       TBX for wide components (a gate per form), BitMask in registers, the `arm64` CI job asserting loop lines.
       Owner's M4 run (default job, one round): SteerGenerated 2.07x faster than SteerScalar at 1k, 1.55x at 100k;
       GlideGenerated 1.87x / 1.63x; 0 B: the ARM gate admits only forms that pay. Not yet known whether it is too
       strict: a rejected shape (the test's `Brake`, 8 words, 3 fields) gets a benchmark pair with the gate forced
       open, and the gate loosens if that form wins on the M4. Gathers stay rows (D131); compress comes with L3. The 16-word locals bug (a 3-field gather's
       `__Loop512` of 34 IL locals) is fixed on `feat-prefetch-gate` (the loop cut to 32; past it the 512 form is dropped).
3. [ ] **Vector loops through the user's own calls** (running, see above) (owner: users should not have to avoid helpers): today a call
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
5. [ ] **Generated code tuned for the machine** (running: worktree agent, branch `feat-microarch`) (`microarch.md`): M0 llvm-mca over every kernel shape, then M1
       accumulators / unrolling / hoisting, M2 branchless scalar rows / mask tests / "make it a tag" rule, M3
       prefetch / streaming stores.
6. [x] **Prefetched lookup chases with a size gate** (`feat-prefetch-gate`: row and vector forms; `feat-prefetch-all`,
       merged at bbd5c8a; its SVE path is first proven by the arm64 CI job, qemu could not: index probes, relation targets, parallel row methods, fused walks and pairs, lambda passes,
       `World.DestroyEntities`' id list, SVE's PRFB on ARM64; `borrowed-ideas.md` 5, item 1; decision drafts and docs
       to-dos in `docs-todos.md`, "feat-prefetch-all"). The owner's rule "a faster case ships, a slower one never":
       16M 1.28-1.77x across the forms (a shared key's Count 1.04-1.19x), in the cache today's code. A pass prefetches
       `k` rows ahead (record, then row; a probe's slot or bucket, then entry; k = 8 rows / 16 for vectors) only when
       the memory it reads is past the last-level cache (`LastLevelCache`, read once per process as the GC reads it:
       Windows `GetLogicalProcessorInformationEx(RelationCache)`, Linux the largest sysfs `index*/size` then
       `sysconf(_SC_LEVEL3_CACHE_SIZE)`, macOS `hw.l3cachesize` else `hw.perflevel0.l2cachesize`; unknown means off).
       It reads and prefetches only where a prefetch exists (`Prefetching.IsSupported`: x86 SSE, ARM64 SVE).
       Left, the owner's: `LookupChaseGateBenchmark` and the prefetch classes on a 32 MiB-LLC x86 box; approve the hidden names `LookupExtensions.Probes`
       and `LookupExtensions.PrefetchKey`; a gate per form on the L2 (`owner-review.md`).
   The owner's answers (2026-10-27): the hidden names `LookupExtensions.Probes` / `PrefetchKey` stay; SVE's PRFB stays
   and the owner runs the CI workflow by hand on the work branch (its arm64 job is the proof; if it fails,
   `Prefetching.IsSupported` keeps x86 only); **a gate per form now** (next code item, `audit-perf`): fused walks,
   `DestroyEntities`' id list, parallel probes and buffer chases open past the L2 (forced on at 1M here: 18.1 -> 11.5,
   3.96 -> 3.02, 5.54 -> 4.92, 41.6 -> 39.3 ms), plain probes keep the last-level gate (20.1 -> 23.0 ms forced on);
   the L2 size read like the LLC (sysfs index2, `GetLogicalProcessorInformationEx`, `hw.perflevel0.l2cachesize`);
   proven per form at the size between the L2 and the LLC, nothing slower in the L2.
   ARM64 without SVE (the M4; owner, 2026-10-26: "is there another instruction or the TLB instead?"): .NET 10 exposes
   no PRFM (only SVE's PRFB/PRFD, and the M4 has SVE in streaming mode only), so the candidate is a plain load issued
   ahead (`Locality/Chase.cs` `LoadAhead`: the record of row i + k loaded into a ring, the row touched from it k / 2
   rows later). Dropped: 1.7x slower than the plain rows on x86 at 16M, and the owner's M4 run (2026-10-27) is slower at
   every size (1.7-1.9x at 16M, 2.3-3.0x at 1M, 4.4-7.2x at 1k; numbers in `borrowed-ideas.md`); its decision record
   goes in with the docs pass. On the M4 the gate's Auto equals Off, as it should with no prefetch to issue.
   Not taken: a native PRFM stub (a native binary per RID in a managed package; a call per prefetch), Apple's
   data-memory-dependent prefetcher (keys on pointer-like 64-bit values, which an `EntityId` is not). TLB: macOS
   arm64 keeps 16 KiB pages with no user superpages; Linux arm64 and x86 get huge pages through #164 (NativeMemory
   2 MiB + madvise). The layout answer works everywhere: L1's readers ordered by target halved the 1M chase without
   any prefetch.
7. [ ] **Borrowed ideas** (`borrowed-ideas.md`): ghost rows between
       worlds as an example first, streaming stores, roofline in `audit-perf` (timer wheel and NUMA dropped by the
       owner: records in the docs pass). Vertical batch probes dropped after the 16M re-measure (record in the docs
       pass); scalar group prefetching of probes is built in item 6 (index probes).
8. [ ] The rest of "Later" below.

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
- [x] **Generator gaps found by the VoxelServer plan** (`voxel-rewrite.md` section 3): built on feat-generator-gaps,
  waiting to merge: G0 enum lanes and single-bool components in vector loops, G5 `[Accumulate]` totals (EECS086's
  total case and fix), G6 `[ForEachRecord]` (EECS108, EECS109 and fix), G4 `World.ReserveBuffers(prefab, count)`; G7
  not built (owner-review.md). Docs pass: docs-todos.md "feat-generator-gaps". G1 / G2 come with inner-loops WP5 /
  WP2; G8 / G9 with L2.
- [ ] **A `using` alias to a system does not bind its generated entry point**: `using Alias = Hunters;` then
  `Alias.Hunt(world)` fails with CS7036 (found by feat-locality-core). Find whether the generator or the interceptor
  matches the call by the written name; fix it (an alias is ordinary C#), with a generator test, or have the
  analyzer say so if it cannot be fixed.
- [ ] **Flaky: `ParallelReductionsScratchAndLookupsDoNotAllocate`** fails about once per full Release run (seen on
  45feeeb), passes 5 of 5 alone on the tip and on the base before the call inliner. The parallel allocation
  checks' measurement race (#166's fix) has another path when the whole suite runs: find it and fix the cause or
  the measurement, never loosen the 0 B check.
- [ ] **Native AOT: no class-initialization check per iteration for a loop's tables** (owner's find, 2026-10-26):
      under Native AOT a loop reading a `static readonly` vector built by code (`Interleaved.Tables*`, `Masks`, the
      generated `__permN`) tests the class's initialization every iteration and spills around the helper call. The
      first fix (vectors of literals, 2206090) was reverted with the NEON `fixed` removal (cf43ac4) because the M4
      measured every multi-field loop 25-58% slower (`audit-vector-loops`, "Read the loop the JIT wrote"). Next:
      bisect the two on the M4 (owner runs `KernelBenchmark` `*Generated` at cf43ac4 vs its parent), then try the
      fix per path (literals only where AOT pays; or the table read into a local before the loop) and land it only
      with JIT x64, JIT ARM64 (M4) and Native AOT numbers.
- [ ] **The inliner's limit, checked instead of assumed** (owner, 2026-10-26: "is a fixed JIT budget right when SIMD
      width differs?"). Answer: `MAX_INL_LCLS = 32` is RyuJIT's implementation constant with no target condition
      (`inline.h`, release/10.0 and main), the same for x64, ARM64, ReadyToRun and Native AOT, and it counts IL
      locals, which no vector width changes; per-width register pressure is M1's (`microarch.md`). Left to do:
      `tools/inlining.sh` in CI on the x64 and arm64 jobs (fails when a runner stops inlining a job, so a runtime
      that changes the limit is seen at once). Mono (the WebAssembly demo; `mini/method-to-ir.c`,
      `mono_method_check_inlining`, release/10.0) read: no limit on locals at all ("also consider num_locals?"), an IL
      size limit of 20 bytes (30 LLVM AOT, 100 LLVM JIT) that `AggressiveInlining` lifts, an inline depth of 10, no
      inlining in gsharedvt code or of methods with exception clauses: the 32-locals split costs Mono nothing, and
      every helper a loop inlines must keep `AggressiveInlining` (the demo's runners inline only through it).
      Decision record with D135 in the docs pass.
- [ ] Follow-ups: lambda passes do not fuse; lambda + structural change and lambda + view stay scalar.
- [ ] **From the published roadmap** (`docs/content/roadmap.mdx` "Planned", checked against this file 2026-10-26):
      push halos for grid neighbors (a chunk hands its edges to its neighbors' halos when they change, D73; with
      WP5 / WP5b, `feature` + `audit-vector-loops`); the vector-loop forms left for later (masked words in parallel
      methods, `Maybe<T>` in parallel methods and with narrow lanes, masked words of narrow lanes and wide rows,
      `Atan2` / `Asin` / `Pow` lowerings; each ships only where it measures faster, `audit-vector-loops`). Not
      actionable here: Unity support waits for a Unity release on .NET 10; the toolchain item is a standing rule.
- [ ] #160 wide rows on AVX2 (heavy math only, per the widelanes numbers); #164 NativeMemory 2 MiB columns (research
      first); #157 the #138 recommendations; #159 remaining research.
- [ ] #153: the `--dev` job is in (screening); still open: check the default job could drop to Warmup 3 / 150 ms with
      the same results (two rounds of a few classes at both settings), and a lighter job for GeneratorBenchmark.
- [ ] Recheck on a quiet machine: GroupBenchmark.Scan, OperationBenchmark Toggle.
- [ ] Flaky under full-suite load with `DOTNET_EnableAVX2=0`: `AllocationTests.ParallelReductionsScratchAndLookupsDoNotAllocate`
      (1 failure in 2 runs, passes alone): find the measurement race (#166's kind).

## Finish

Starts only when every code item of Next and Later is done (owner, 2026-10-26: "先把代码全改完").

- [ ] **Exemplar pass** (#169, `audit-code`): VoxelServer / samples + templates + demo / benchmark worlds + usage
      tests rewritten in the newest recommended forms, A/B on rewritten benchmark worlds. BattleServer's Battle
      singleton moves to [Accumulate], then GeneratorBenchmark's EECS086 filter exception is deleted (EECS086's total
      case reports Shoot, SendFire and SendFireOut; a benchmark must not keep suppressing a rule our own example breaks).
- [ ] **Directory restructure of the whole repository** (owner, 2026-10-26: "调整整个项目的目录结构"): `git mv` only,
  after feat-prefetch-all and feat-locality-pairs merge; the layout plan (old → new per file, every path reference)
  goes to the owner first.
- [ ] **VoxelServer rewritten with every high-performance feature, the least user code** (owner, 2026-10-26): after
  every code item; its per-system plan is made now (read-only), and the generator or feature gaps it exposes (lines
  users would write twice) go into Later as code items, so the rewrite itself only uses what already exists.
- [ ] **Generated-members scan before the release** (roadmap "Less code through the generator", D26,
      `audit-generator`): the API, the battle server and the guides scanned for lines users write twice.
- [ ] **Docs pass** (#141): everything in `docs-todos.md`; DocCheck (296 problems) and ApiDocs (46) to 0; decision
      record for the 8 declined declarations; roadmap item for inner loops; analyzer range EECS002-103.
- [ ] The `arm64` CI job (`ci.yml`, `runs-on: ubuntu-24.04-arm`, added b1dbdb0) has never run: it runs on the next
      push to master or a manual dispatch; if the label is wrong the job fails at once and the label is fixed then.
- [ ] `tools/bench.sh --site --sync`, README numbers, `verify`, master. The owner cuts the release.
