# Backlog: what is left before 4.8 (internal, not published)

The single list of open work, kept in the repository so a new session finds it (AGENTS.md, "Plans that span sessions
are committed"). Update it when an item starts, merges or is dropped. Designs live next to it (`inner-loops.md`);
docs to-dos collected while code still changes live in `docs-todos.md`.

Order: every feature (code) -> generated-members scan and paths -> exemplar pass, the refactor (`audit-code`) -> docs
pass (`audit-docs`) -> bench sync -> `verify` -> master.
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

- [ ] **Arrive runs** (item 8i, branch `feat-arrive-runs`): MoveEntities' gain from feat-copy-entities' ArriveRows
      changes, without CopyEntities.
- [ ] **Tile reads checked once** (owner, 2026-10-29: "先合并，再修读取"): the in-cache gap the ping-pong step
      keeps against step + commit by hand (Laplacian 1.33x at 1 thread) is the Tile API's per-column range check and
      sign extension in `TileCursor` loads, paid by the by-value form too; a generator rewrite of `c.Load128(x + k)`
      into one checked base when the x loop's range is known, or an unchecked row cursor. Proof: Tier1 listings and
      TileStencil 32 / 512 at 1 and 4 threads against hand, sparse and dense.

Merged since the last update: symmetric pair methods (`feat-symmetric-pairs`, 685bbc2: half lists inferred from
`ref` + `[Neighbor] ref`, one pair at a time; `Parallel = true` runs 256-row chunks colored at distance two, about 2x
at 4 threads, same bits on any thread count, floats within rounding of the serial pass; per-chunk buffers and the
vector loop measured and dropped; PairsBenchmark GeneratedParallel rerun on the owner's M4: no case slower past
the noise, docs-todos "feat-symmetric-pairs"), ghost rows in MultiWorld (`feat-ghost-rows`: 4 zones side by side, border units
copied as `Ghost` rows each update, crossings moved with `MoveEntities`, checked against one world; a
`CopyEntities<TTag>` proposal in `owner-review.md`, not built; CI now checks the sample's output), Emitted<T>'s segments (`feat-emit-segments`, 4925b09: the chunks' lists kept as
segments, joined only for a span read or a write; EmitParallel 1.4-2x, a `[ForEachRecord]` reader about 1.2x, 0 B;
generator unchanged), fewer copies (`feat-fewer-copies`, 837c956: incremental Capture / Restore, Rollback
with 16 writes 3.2 ms -> 36 us and BufferSnapshot Restore 78 -> 1.2 us; sorts gather into a spare column and swap it in,
1M wide rows 77-88 -> 71-81 ms; `FillPool` exact on helper threads, so VoxelServer fills its field buffers once, not
twice), the generator crash and reason texts (`fix-kernel-foreign`: metadata backing
fields read as their properties, a translation exception becomes the method's reason), the L2 leftovers (`feat-pairs-2`: parallel pairs, `[Neighbor] ref`, nested-walk
rewrites, neighbor lists by cost, `Reduce<T>` / `Mut<T>` in pair methods, the cluster renames; the owner's M4 run of
PairListsBenchmark is open), the zombies' distance field (`feat-zombie-field`, 9c73602), CI fixes (`fix-ci`, ae9d525: Restore gives rows written since the capture their own
copy on the owner thread, VoxelServer 0 B again; DocCheck 0), L1 ordered rows + L3 compress (`feat-locality-core`), the pair form
(`feat-locality-pairs`), vector loops through the user's own calls (`feat-call-inliner`), the prefetch gate per form
(`feat-prefetch-gate`, 216e29a: row chases, fused walks, buffer chases and `DestroyEntities` past the L2; probes and
vector loops past the last-level cache); generated code tuned for the machine (`feat-microarch`, 9161797: paired
accumulators for float Min / Max / Fast sums, Energy 1.43-1.47x and Spread 1.89x; the tables initialized at module
load, LateHoles 2.7-4.5x; masked skip, unrolling, hoisting and branchless rows measured and dropped; streaming stores
a prototype, `owner-review.md`).

The owner's order (2026-10-26): every code change before any docs (generator cost and ARM64 parity included).
CI runs the unit tests on ARM64 (b1dbdb0).

## Next, in this order

- [x] **Flaky: `PrefetchingProbesBuffersWalksAndLambdasAllocateNothing`** (fix-flaky-alloc): on-stack replacement
  compiles on the measuring thread. In the full suite the tiering backlog keeps the four probe jobs' loops at Tier0
  past the 50 warm-up frames; their OSR compiles land in the window, 168 B each (one compile per job, 672 B in every
  failing run; 6 failures in 10 instrumented AVX2=0 runs, 1 in 2 normal runs), and none happens with
  `DOTNET_TC_OnStackReplacement=0` (3 of 3 clean). Fix: `Allocations.WarmUp` runs until the JIT has settled (no
  compile in the process for 40 runs and 0.5 s). Decision draft in `docs-todos.md`, "fix-flaky-alloc".

**Rename `[ForEachRecord]` -> `[Drain]`** (owner, 2026-10-28, naming audit: "[ForEach] 和 [ForEachRecord] 太像但做的事不一样";
unreleased, so no migration): right after the tile branch merges (it edits SystemGenerator.cs too). With it
`IForEachRecord<T>` -> `IDrain<T>`, the analyzer and code-fix strings, `Symbols`, tests (RecordTests names), the
benchmarks' method names (EmitBenchmark, RecordBenchmark: site keys unaffected, not synced yet), zh ApiDocs files
(`foreachrecordattribute.xml` -> `drainattribute.xml`, `iforeachrecord.xml`), the reference regenerated, plans, file
maps and skills. Glossary: drain = 排空 (each record handled once, then cleared).
In the same pass (owner, 2026-10-28, all unreleased): **`[ForEntity]` -> `[EntityMethod]`** (a callable on one entity,
not a pass; "method" as the docs say `[ForEach]` method and pair method; zh 实体方法; D82 rewritten, EECS080's fix
title, `Symbols`, the code fixes) and **`world.ForParallel` -> `world.ParallelFor`** (.NET `Parallel.For`, OpenMP
`parallel for`, TBB / Kokkos `parallel_for`; `IForRange` / `IForSpan` unchanged). `[GaussSeidel]` stays (renamed
from `[RedBlack]` on 2026-10-27). Grep every old name afterwards (code, tests, benchmarks, samples, plans, skills,
zh ApiDocs; history pages only may keep them).

Agent slots as they free (at most 3): 12c + 12d, 11 + 12a + 12b (one tile branch: padded storage, ping-pong, zero
rows, edges found while stepping), 8e and 12e merged; 8f + 8g (one generator branch) start once the tile branch merged (they edit Kernel.cs,
SystemGenerator.cs and Symbols.cs too), then 9, 10, 4, 7, Later, then Finish.
The VoxelServer plan was refreshed read-only (`voxel-rewrite.md`, Finish step 2a).

1. [x] **L2: the generated pair form** (merged e9c3db8, `feat-pairs-2`; the owner's M4 run of PairListsBenchmark done 2026-10-28, below) (`locality.md` 3.2, 3.4: the owner's rule that users' plain code adapts by itself: order inferred from the pair method, nested walks rewritten, hand-written grids get a code fix): the user writes what one pair does; the generator writes
       the 8x8 (x86) / 4x4 (ARM64) cluster-pair kernel over L1's blocks with cutoff and self masks, and a nearest form
       (`NearestPlayer`, zombie targeting). Right after WP0b merges. Then L4: examples rewritten in it.
       The spread map (nearest 1.06-1.31x, bar 1.5x; owner 2026-10-27: a bar is not a ship gate, a gain beyond the noise
       with nothing slower ships): re-measure once L1 sorts on its cadence,
       with a flat grid of blocks searched in rings instead of the prototype's box tree; the nearest form stays in L2.
       The owner's call on Lookup gathers under `SimdMode.Auto` (2026-10-26): once L1 and WP0b have merged, re-measure
       the micro-adaptive choice (walk vs rows, D131 Considered) with L1's ordered rows and the lookup job under 32
       IL locals; on by default only if no case is slower. No per-entity table id.
       ARM64 (owner's M4 run, `locality.md` 6.1a): crowded maps 2-8x, sparse maps only 1.1-2.1x while neighbor lists
       reach 2.5-3.4x: L2 ships clusters there too (faster is kept, owner) and optimizes: 4-row blocks and the
       neighbor-list form on ARM64, picked by density; never a slower case (no re-sort every tick on sparse maps).
       **Built on `feat-locality-pairs`** (`locality.md` 6.6): `[Neighbor]`, the pass, the generated order / job / vector
       loop, EECS105-107, cutoff per row, two kinds of entity, ties and the keep-target hint (merged; `[Neighbor]` approved
       by the owner, 2026-10-27). **`feat-pairs-2`:** writes to j (`[Neighbor] ref`), parallel pair passes, the EECS107
       fix and nested-walk rewrites, lists of neighbors picked by cost (Auto never slower; the owner's M4 run, 2026-10-28, one round, 0 B:
       Auto within the error of the best choice in all 24 cases. Generated (clusters): Auto = Off everywhere, and lists
       On are 1.7-3.2x slower (10k crowd 1,070 / 1,066 / 3,440 us, 10k sparse 482 / 499 / 894, 100k crowd 17.4 / 17.5 /
       41.0 ms, 100k sparse 7.97 / 8.11 / 13.6 ms for Auto / Off / On), so Auto keeps clusters on ARM64. GeneratedRows:
       the three within 4 % (10k crowd 3,579 / 3,551 / 3,452 us, inside Auto's 96 us error); lists never win clearly on
       the M4), `Reduce<T>` / `Mut<T>` in pair methods (GA1); measured and dropped: Lookup gathers under Auto (slower with
       many target tables), the spread-map grid of clusters (faster spread, slower packed), the cluster prefetch below
       (slower at every size). L4 (examples) moved to the finish phase (docs-todos).
       Follow-up after L2 merges (owner, 2026-10-27: "cluster pairs + prefetch, faster?"): prefetch the j block of the
       pair `k` pairs ahead (its rows' lines of every column the pair method reads) behind a `CacheSizes`
       gate as item 6 (which one, L2 or last level, measured per form as item 6's gate per form was); i blocks are
       sequential and need none. Expected to pay only when the ordered table is past the
       last-level cache (this machine's L3 is 260 MiB: PairsBenchmark at 4M-16M rows, plus a forced-on column just
       under the cache); ships only if faster there and never slower in the cache (`audit-perf`, two rounds, 0 B).
       Measured on `feat-pairs-2` (a scratch PairPrefetchBenchmark, 100k-16M rows): slower everywhere, 16M included; dropped.
2. [x] **ARM64 parity of the generated forms** (merged 186c84d): LD2-4 / ST2-4 for multi-field components, NEON TBL /
       TBX for wide components (a gate per form), BitMask in registers, the `arm64` CI job asserting loop lines.
       Owner's M4 run (default job, one round): SteerGenerated 2.07x faster than SteerScalar at 1k, 1.55x at 100k;
       GlideGenerated 1.87x / 1.63x; 0 B: the ARM gate admits only forms that pay. Not yet known whether it is too
       strict: a rejected shape (the test's `Brake`, 8 words, 3 fields) gets a benchmark pair with the gate forced
       open, and the gate loosens if that form wins on the M4. Gathers stay rows (D131); compress comes with L3. The 16-word locals bug (a 3-field gather's
       `__Loop512` of 34 IL locals) is fixed on `feat-prefetch-gate` (the loop cut to 32; past it the 512 form is dropped).
3. [x] **Vector loops through the user's own calls** (merged, `feat-call-inliner`) (owner: users should not have to avoid helpers): today a call
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
5. [x] **Generated code tuned for the machine** (merged 9161797, branch `feat-microarch`) (`microarch.md`): M0 llvm-mca over every kernel shape, then M1
       accumulators / unrolling / hoisting, M2 branchless scalar rows / mask tests / "make it a tag" rule, M3
       prefetch / streaming stores.
6. [x] **Prefetched lookup chases with a size gate** (`feat-prefetch-gate`: row and vector forms; `feat-prefetch-all`,
       merged at bbd5c8a; its SVE path is first proven by the arm64 CI job, qemu could not: index probes, relation targets, parallel row methods, fused walks and pairs, lambda passes,
       `World.DestroyEntities`' id list, SVE's PRFB on ARM64; `borrowed-ideas.md` 5, item 1; decision drafts and docs
       to-dos in `docs-todos.md`, "feat-prefetch-all"). The owner's rule "a faster case ships, a slower one never":
       16M 1.28-1.77x across the forms (a shared key's Count 1.04-1.19x), in the cache today's code. A pass prefetches
       `k` rows ahead (record, then row; a probe's slot or bucket, then entry; k = 8 rows / 16 for vectors) only when
       the memory it reads is past the last-level cache (`CacheSizes.LastLevel`; some forms past the L2 since, below;
       read once per process as the GC reads it: Windows `GetLogicalProcessorInformationEx(RelationCache)`, Linux the largest sysfs `index*/size` then
       `sysconf(_SC_LEVEL3_CACHE_SIZE)`, macOS `hw.l3cachesize` else `hw.perflevel0.l2cachesize`; unknown means off).
       It reads and prefetches only where a prefetch exists (`Prefetching.IsSupported`: x86 SSE, ARM64 SVE).
       Left, the owner's: `LookupChaseGateBenchmark` and the prefetch classes on a 32 MiB-LLC x86 box; approve the hidden names `LookupExtensions.Probes`
       and `LookupExtensions.PrefetchKey`; a gate per form on the L2 (`owner-review.md`).
   The owner's answers (2026-10-27): the hidden names `LookupExtensions.Probes` / `PrefetchKey` stay; SVE's PRFB stays
   and the owner runs the CI workflow by hand on the work branch (its arm64 job is the proof; if it fails,
   `Prefetching.IsSupported` keeps x86 only).
   - [x] **A gate per form** (branch `feat-prefetch-gate` at 850a6b1, worktree `/workspace/wt-gate`, ready to merge;
     `borrowed-ideas.md` item 1, "A gate per form"; decision draft and docs to-dos in `docs-todos.md`,
     "feat-prefetch-gate: a gate per form"): `CacheSizes` (was `LastLevelCache`) reads one core's L2 like the LLC
     (sysfs level-2 index then sysconf, `GetLogicalProcessorInformationEx` level 2, `hw.perflevel0.l2cachesize` /
     `hw.l2cachesize`; unknown: the LLC gate). Past the L2: the row form's chases (one thread, parallel, fused walks,
     buffer elements, lambdas; `Lookup<T>.PrefetchesPastL2`) and `DestroyEntities`' id list: 1.08-2.0x at 262,144 and
     1.2-1.57x at 1M, Auto equal to Off inside the L2. On the last level: probes (parallel ones too: the owner's list
     had them on the L2, measured a wash or 1.16x slower at 262k-524k, reverted) and vector loops' chases. The owner's
     machines: `EASYECS_BENCH_ENTITIES=<sizes between L2 and LLC>` over the prefetch classes.
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
7. [ ] **Borrowed ideas** (`borrowed-ideas.md`): ghost rows between (MultiWorld done, feat-ghost-rows)
       worlds as an example first (owner, 2026-10-27: "都做呗，voxelserver和multiworld都做": in MultiWorld and in
       VoxelServer, two region worlds with `MoveEntities` at the border; VoxelServer's in Finish step 2a), streaming stores, roofline in `audit-perf` (timer wheel and NUMA dropped by the
       owner: records in the docs pass). Vertical batch probes dropped after the 16M re-measure (record in the docs
       pass); scalar group prefetching of probes is built in item 6 (index probes).
8. [x] **VoxelServer: zombies follow a distance field** (merged: step + commit form, the in-place form dropped for an
       intermittent allocation; numbers in docs-todos "feat-zombie-field") (approved by the owner, 2026-10-27: "可以做，排进去"). Today a zombie walks the straight line to its target and sticks at walls and the bots' pits. One
       field per chunk (`DynamicBuffer<PathCost>`, 16 x 16 over the surface height map, a step up of one block
       walkable) seeded by the players' cells, relaxed as a tile shader: each step sweeps its own tile to settle
       inside it, red-black tiles so the front crosses two chunks a tick, `ChangedEdges` sleep where no player
       moved. All zombies read the gradient of one shared field (nearest player for free; ZombieTargetSystem keeps
       the target for the attack). Proof: TickBenchmark before/after at 50 and 2,000 players, zombies stuck
       before and not after, grids page and VoxelServer page sections.
8b. [x] **Bug: a vector loop over components from another assembly crashes the generator** (found by feat-zombie-field,
       2026-10-27): `Kernel` throws `IndexOutOfRangeException` in `StructLocalLane` (`Kernel.Struct.cs`) or `Resolve`
       (`Kernel.Lookups.cs`), which drops every generated file of the project (hundreds of unrelated CS7036 / CS0122;
       CS8785 in the log). Repro: VoxelServer's old straight chase in a test project that references the sample
       (scratchpad `TmpRepro.cs.txt`, copy it into the test). Fix, regression test, and no generator exception may ever
       drop the other files (catch per method, report it). First in line.
8c. [ ] **Gaps the distance field met** (feat-zombie-field report; each through `audit-generator`'s rule: build it
       when it saves users lines or thought): a 2D tile beside a 3D one on one key (today a second tile component and
       index); colors without generic systems (`[With<TColor>]` is CS8968; `[GaussSeidel]` removes it); EECS059 as
       an Error where a checkerboard makes the write safe (goes with `[GaussSeidel]`); `PaddedTile` read-only (settling
       in a tile copies it into a stackalloc by hand; no halo-row or lane-shift helper; 12a's padded storage may
       answer it); `FillPool` ignores the up to 64 lists each thread parks (a likely cause of the in-place form's
       allocation; done in 12d: `Fill` counts 64 lists per thread with lists of its own).
8e. [ ] **Symmetric pair methods** (`voxel-rewrite.md` GA4; owner, 2026-10-27: "加，做 GA4"): each unordered pair
       visited once and both sides written (Newton's third law in MD codes: half neighbor lists), for item merging in
       VoxelServer and collisions; today a pair writing the neighbor's component it also reads of its row is refused.
       With or after item 1 (the pair agent), `feature` + `audit-vector-loops`: bit for bit against the per-pair walk,
       deterministic order, parallel only where the halves cannot race, 0 B, analyzer reasons and naming (`audit-naming`).
       Status: built on branch `feat-symmetric-pairs` (from 5b8f926), waiting for merge: inferred from `ref` +
       `[Neighbor] ref` of one component (`IPairJob.Symmetric`), half lists, one pair at a time; in parallel
       (`Parallel = true`, owner 2026-10-28) chunks of 256 rows colored at distance two, color by color (both designs
       built and measured, per-chunk buffers dropped); its vector loop measured and dropped (docs-todos
       "feat-symmetric-pairs").
8d. [x] **Bug: EECS055 reason text** (`voxel-rewrite.md` GA7): VoxelServer's Chase reports `local 'cell' is a ??`
       (the type name missing); Float reports `LookupExtensions.TryFind, defined in another assembly` where the reason is
       that a key probe has no vector form. Fix the text, a test per case.
8f. [ ] **The other body stages catch their exceptions** (fix-kernel-foreign's report): `Chases.Of`, `PairModel.Create`
       and `FusionSide.Of` still drop the project's output when they throw; each needs a safe fallback model (the
       method's plain job) and a reason, like Kernel. Also a clearer EECS055 reason for a switch expression over a
       struct call (VoxelServer's Chase reads "'new Nearby(...).Trail(cell.X, cell.Z) s...' is not supported").
8g. [ ] **Constructors and helpers of another assembly, read from what its generator recorded** (found at the
       fix-kernel-foreign merge; owner, 2026-10-28: "有没有别的办法解决这种问题呢"): a component in a contracts
       assembly (HotReload's `Position(float X, float Y)`) makes `new Position(x, y)` an EECS055 warning, because
       metadata has no constructor body; users with shared contracts would have to avoid an everyday idiom.
       Ways checked: reading the IL fails where it matters (a ProjectReference compiles against the project's
       reference assembly, whose bodies are `throw null`; NuGet packages may ship `ref/` only; a generator does no
       file IO); guessing from metadata (a record's `Deconstruct` and properties named like the parameters) is wrong
       when a positional property has its own initializer, and a wrong guess is a wrong vector loop.
       Chosen: the EasyEcs generator runs in the declaring assembly anyway (it references EasyEcs for IComponent),
       sees the source and records the facts as hidden assembly-level attributes, which reference assemblies keep
       (as the compiler records nullability or tuple names): per constructor of a component, the field each
       parameter is assigned to when the body is only that; per small static helper a vector loop may call (the
       call inliner's candidates: `Terrain.Cell`-like), its body, so the call inliner inlines helpers of other
       assemblies too. The referencing Kernel reads them (`compilation.GetAssemblyOrModuleSymbol(reference)
       .GetAttributes()`, cached per reference); an assembly built without them keeps today's fixable reason.
       Members the recorded body names that the referencing assembly cannot see (a private backing field, an
       internal helper, an internal type) are reached through generated `[UnsafeAccessor]` externs, as the systems'
       private methods already are (owner, 2026-10-28: "还可以搭配 unsafe accessor 之类的吧"): `Field` kind returns a
       `ref` the JIT turns into a plain load or store, `Method` / `StaticMethod` for calls, .NET 10's
       `[UnsafeAccessorType]` names a type the caller cannot reference; the vector loop reads such fields as lanes at
       their offsets like any other field. Checked on JIT x64, ARM64, Native AOT and the WebAssembly demo (Mono AOT),
       with the Tier1 code showing direct field access and no call.
       Proof: the HotReload sample's old `new Position(...)` vectorizes with no warning; a hand-written constructor
       and an initializer-changed positional property stay scalar; a helper of a referenced assembly inlines bit for
       bit as from source; GeneratorBenchmark shows the attribute read costs nothing per keystroke.
8h. [x] **`CopyEntities<TTag>` prototype + benchmark: measured, not shipped** (owner, 2026-10-28: "做原型+基准";
       `owner-review.md`, "Ghost rows between worlds"). The prototype is on branch `feat-copy-entities` (unmerged,
       `GhostCopyBenchmark`, `CopyEntitiesTests`): 0.63-0.92x of the `CreateEntity` loop with 3 components, 0.82-1.00x
       with 1 component on sorted rows, but 0.92-1.09x with 1 component on scattered rows (MultiWorld's case), and
       `Border.cs` grows by 5 lines instead of shrinking. Fails "never slower" and "less code"; what would change the
       verdict is in `owner-review.md`.
8i. [x] **MoveEntities 0.82-0.84x: `ArriveRows` kept out of its callers** (branch `feat-arrive-runs`, unmerged).
       The gain of feat-copy-entities was the JIT's inlining, not its copy code: base inlined `ArriveRows` with
       `CopyRun` into both call sites of `MoveEntities` (Tier1 11.9 KB, 692 stack references); the copy branch's
       wider signature changed that. `[MethodImpl(NoInlining)]` on `ArriveRows` alone: 3.7 KB, 136, Move
       0.79-0.84x in 8 of 8 rounds; the rewrite of its row loop alone 0.98-1.00x (a wash, dropped); `Gather`, the
       column-by-bit map and `Remaps` serve only copies (not ported). 8h's WorldSimulation 10k +1-6 % and SaveRecord
       +3 % were noise: here 0.93-1.09x and 0.96-1.02x in every build, including a control whose path is base's
       (base + the copy branch's World fields). Decision record and changelog line in the agent's report, for the
       docs pass.
9. [ ] **G10: deferred consume for chunk load / save** (owner, 2026-10-27; `voxel-rewrite.md` G10): VoxelServer's
       ChunkRead / ChunkSave load and save outside the pass by hand; the generator turns the plain form (a
       `[ForEach]` that requests a load or a save) into the deferred one, so users write neither the queue nor the
       drain. Analyzer rule and code fix for the hand-written form; proof: VoxelServer lines before / after, tests,
       0 B steady state. The form carries a per-tick budget (`voxel-rewrite.md` GA9: VoxelServer caps requests per
       tick, `Rules.MaxRequests`; the owner wants the cap kept).
10. [x] **Streaming stores in generated loops, gated** (merged 1b1aa12: Predict 0.86-0.94x, Mix 0.90-0.92x past the
       cache with the reader counted in, gate off inside; ARM64 STNP unmeasured) (owner, 2026-10-27; `microarch.md` M3, prototype
       `Locality/Streams.cs`): non-temporal stores for a column the method writes without reading, only when the
       column's bytes are past `CacheSizes.LastLevel`. Measured in a real world with the next pass reading that column
       counted in; ships when faster in both rounds and never slower (StreamingStoreBenchmark: 1.47x past the cache,
       a tie inside).
11. [ ] **Ping-pong steps instead of a commit copy** (owner, 2026-10-27: MPI's u / v buffers swapped after a sweep;
       part of #157's "ping-pong buffer"). Today a tile shader writes `DynamicBuffer<NextX>` and a commit system copies
       it into `DynamicBuffer<X>` (VoxelServer `WaterCommitSystem`, the grids page's `CommitHeatSystem`): one full copy
       of every stepped tile per tick. Swap the two buffers' stores instead (O(1) per tile, under the gate; X marked
       changed, so replication and `[Changed<X>]` see the write). Catch: the stencil skips zero rows (the row mask) and
       relies on the copy making both buffers equal; after a swap the skipped rows hold the step before last. Chosen
       (owner, 2026-10-27): the generated step writes every row it skips as zero, with vector stores (a zero row is a
       few stores, cheaper than the copy it replaces). Per the
       owner's rule (`audit-generator`): users write the plain step and no commit system; the generator owns the step
       buffer and the swap (a `Tile` parameter that steps in place in user code's eyes), with an analyzer rule and
       code fix for a hand-written commit that is a plain copy. Proof: TileStencilBenchmark / TileActiveBenchmark and
       VoxelServer's TickBenchmark before / after, two rounds, 0 B; the grids page and the VoxelServer page in the
       docs pass.
    **Two update forms, one user code** (owner, 2026-10-27): the plain tile step is Jacobi (default, no attribute):
       each pass is one time step, every cell reads the step before; the generator owns the two buffers, swaps them
       and writes skipped rows as zero. `[ForEach, GaussSeidel]` (approved name, renamed from `[RedBlack]` on 2026-10-27: red-black is two colors
       only, a 9- or 27-point stencil needs multicolor ordering; a marker beside `[ForEach]` like `[Consume]`) relaxes toward a settled field in place: one buffer, no commit, the colors inferred from the
       stencil's offsets (5-point 2 colors, 9-point 4, 27-point 8), sweeps inside a tile until it settles, tiles in
       color order, settled tiles sleep. Both deterministic and independent of the thread count; they differ in what
       one pass means, which only the user knows, so the choice is declared. An Info rule suggests `[GaussSeidel]` for a
       relaxation shape (`d = Min(neighbor + cost)`) whose per-pass value nothing else reads. The docs explain "Jacobi"
       "Gauss-Seidel", "red-black" and "multicolor" in one plain sentence each where first used (WRITING.md).
    **With it, the tile gaps the VoxelServer plan found** (`voxel-rewrite.md` section 4): GA10 a tile's cell
       buffers sized at instantiation from its shape (the hand `SetCount`s of ChunkGenerate, `EnsureCapacity` of
       Water Wake); GA3 `MaxPooledLength` inferred for those buffers; GA12 tile sleep and wake owned by the tile pass
       (from the step's edges and `Changed<cell buffer>`) instead of `foreach (id in tile.Facing(e))
       world.GetEntity(id).SetX()`, written four times in the sample, for Jacobi as `[GaussSeidel]` already does.
12. [ ] **Fewer memory copies** (owner, 2026-10-27: "看看还有什么类似的能做的减少内存拷贝的做法"; a-e approved: "这些都可以做"). Each one measured
       (two rounds, 0 B, nothing slower) and done by the generator or the core where users would otherwise copy:
       a. **Tiles stored padded, halos exchanged** (MPI's ghost cells; the grids page's TODO(water overlap)): today
          `Pad()` copies the whole tile and its neighbors' edges into a thread texture every pass. Keep each tile's
          cells padded in its own buffer (the halo stored beside them), so a step reads its buffer in place and a
          pass copies only the neighbors' edges into the halo, only the edges that changed (`ChangedEdges` already
          knows which), and the interior can step while the ring waits (interior / ring overlap). With item 11 the
          step and its padded buffer ping-pong. Cost to weigh: the halo's memory (27 % of a 16 x 16 tile at r = 1).
          Renames with it (owner, 2026-10-27): `Pad()` becomes `ExchangeHalo()` (MPI's halo exchange), and
          `PaddedTile` folds into the tile itself.
       b. **Changed edges found while stepping**: `ChangedEdges` reads the cells and the step again after the step
          (O(cells), both buffers). The generated step compares each row it writes with the row it read (one vector
          compare per row it already holds in registers) and returns the faces, so no second pass.
       c. [x] **Re-sorts swap columns instead of copying back**: `Archetype.Permute` writes each column into a rented
          scratch and copies it back (and the slots, the presence bits). Write into a spare pinned column and swap it
          in; the old column becomes the spare (one spare per column size per world, kept for the next sort).
       d. [x] **Incremental Capture / Restore**: `Capture(into)` copies every table every time. Copy only the columns
          (or 4,096-row blocks, `BlockTicks`) written since `into` was last captured from this world with no structural
          change between (`StorageVersion`, `LastWrite`); Restore copies back only what changed since the capture.
          Rollback netcode captures every tick with few writes per tick.
          Also win back the cost of fix-ci's restore (owner, 2026-10-27): rows written since the capture now take
          copies of the snapshot's elements on the owner thread (BufferSnapshotBenchmark Restore 66.6 -> 84.4 us,
          Rollback 141 -> 177 us, 0 B), so the first parallel write after a restore does not copy on a helper thread.
          Bar: back to the old times or better with the 0 B steady state kept.
       e. [x] **Emitted<T> without the join copy**: `Emit<T>.Join` copies every chunk's list into the singleton's list.
          Keep the chunks' lists in order as segments: `[ForEachRecord]` and `ForParallel` walk the segments (users
          see no change); a contiguous span is joined only when user code asks for one.
13. [ ] The rest of "Later" below.

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
  merged: G0 enum lanes and single-bool components in vector loops, G5 `[Accumulate]` totals (EECS086's
  total case and fix), G6 `[ForEachRecord]` (EECS108, EECS109 and fix), G4 `World.ReserveBuffers(prefab, count)`; G7
  not built (owner-review.md). Names approved by the owner (2026-10-27): `[Accumulate]`, `[ForEachRecord]` +
  `IForEachRecord<T>`, `World.ReserveBuffers`. Docs pass: docs-todos.md "feat-generator-gaps". G1 / G2 come with inner-loops WP5 /
  WP2; G8 / G9 with L2.
- [ ] **A `using` alias to a system does not bind its generated entry point**: `using Alias = Hunters;` then
  `Alias.Hunt(world)` fails with CS7036 (found by feat-locality-core). Find whether the generator or the interceptor
  matches the call by the written name; fix it (an alias is ordinary C#), with a generator test, or have the
  analyzer say so if it cannot be fixed.
- [x] **Flaky: `ParallelReductionsScratchAndLookupsDoNotAllocate`** (fix-flaky-alloc): the same path as the
  prefetching test. Its window saw OSR compiles on the measuring thread (updates 53, 62 and 81, in 2 of 4 instrumented
  full runs; none with OSR off); the other paths were ruled out (the measuring thread made its `Scratch` list during
  the warm-up in every run; `Allocations.Of` settles background collections; a filter built per pass past bit 255
  would allocate on every update, and the instrumented runs measured 0 B on the others). Its own failure did not
  reproduce in 15 full runs before the fix (13 with AVX2 off), so the cause is the shared one by evidence, not by a
  caught failure. Fix: `Allocations.WarmUp`.
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
- [ ] **`[Accumulate]` users that do not conflict** (`voxel-rewrite.md` GA2): systems that only add into one
      `[Accumulate]` singleton share a stage, combined in registration order at the stage end. Built only if the
      VoxelServer rewrite's `--schedule` shows stages lost to its `Stats` (step 2 of that plan); otherwise a record.
- [ ] **EECS031 on nested creation loops** (feat-tiles-pingpong): suggests `Reserve(inner bound)` where
      `outer × inner` rows are created, and only once the bound is not a constant (`audit-analyzer`, "Open findings").
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

## Finish

Starts only when every feature is built: every code item of Next and Later is done (owner, 2026-10-26: "先把代码全改完").
Then, in this order (owner, 2026-10-27: "重构应该排在写文档和 benchmark 前吧，等所有功能先落实好"): 1 the last code
gaps and the paths, 2 the refactor of every example, 3 the docs, 4-5 the benchmarks and master. Nothing of a later
step starts before the earlier one is done, so docs and numbers are written once, about the final code.

- [ ] **1a. Generated-members scan** (roadmap "Less code through the generator", D26,
      `audit-generator`): the API, the battle server and the guides scanned for lines users write twice;
      what it finds is a code item, built before step 2.
- [ ] **1b. Directory restructure of the whole repository** (owner, 2026-10-26: "调整整个项目的目录结构"): `git mv` only, before the
  refactor so it lands in the final paths,
  after every agent branch merges; the layout plan (old → new per file, every path reference)
  goes to the owner first.
- [ ] **2. The exemplar pass: the refactor** (#169, `audit-code`; owner, 2026-10-27: "9 的第一步不是重构全部 sample demo
      benchmark unit test 和文档里的代码例子吗，全用上最正确的最合适的功能（新功能也包含），尤其是 voxelserver 需要大规模重构").
      Every piece of code users read or copy is rewritten in the most correct, most fitting form, new features
      included (vector loops through plain `[ForEach]`, `[Accumulate]`, `[ForEachRecord]`, `Emit`, `Scratch`,
      `EntityRandom`, views, `[OrderBy]` / pairs, tiles with `[GaussSeidel]` and the ping-pong step, `IDirectMapped`,
      `ChunkSize` only where it pays, the least code the generator allows):
      a. **VoxelServer, a large rewrite** (owner answers in `voxel-rewrite.md` section 9: every zombie looks every
         tick, output changes accepted, Target kept, item merging through symmetric pairs (8e), Terrain as a Cell
         method after WP5, a slower generated scent is a generator gap, ghost rows of two region worlds) (owner, 2026-10-26: every high-performance feature, the least user code;
         2026-10-27: "需要大规模重构"): the plan is `voxel-rewrite.md`, made on 2515638 before L1 / L2, the call
         inliner, the generator gaps, the prefetch gates, the distance field and the tile items. It found ~7% to cut
         as line edits; the owner expects a restructure, so it is refreshed now on the tip against everything merged
         since (systems merged or split, components reshaped for vector loops, pairs instead of rings, tiles instead
         of hand loops). Its per-system plan is made now, read-only, not at Finish (per system: the features it does not use yet,
         the lines saved, the generator or feature gaps it exposes); the gaps (lines users would write twice) go into
         Later as code items, so the rewrite itself only uses what already exists. Bools split (Body.OnGround,
         Exposure.Sunlit). The plan covers every feature, not only tiles (owner, 2026-10-27: "全部现在的功能都看看",
         prefetch, cluster pairs, ...): a matrix of every feature, used today or not, where it belongs, and for the
         automatic ones (prefetch gates, ordered rows, fusion) whether the sample's data and scale trigger them.
         The rewrite waits for item 11 (+ 12a / 12b): the tile systems are written once, in the ping-pong form
         (owner: "你得等你的 uv 交换先修好").
      b. The other samples, `templates/`, the live demo (`demo/`, its page includes the files).
      c. Benchmark worlds (`BattleServer.cs`: the Battle singleton moves to `[Accumulate]`, then GeneratorBenchmark's
         EECS086 filter exception is deleted: EECS086's total case reports Shoot, SendFire and SendFireOut, and a
         benchmark must not keep suppressing a rule our own example breaks; `WorldSimulation.cs`), with an A/B on each
         rewritten world.
      d. Unit tests that show usage (fixtures, scenario and feature tests: the forms users would copy), not the tests
         that pin an old form on purpose (code fixes, analyzer negatives, migrations).
      e. Every code example of the docs (`docs/content` fences en + zh, the guide pages' full examples, the public
         skill `skills/easyecs/SKILL.md`, README), DocCheck at 0.
      Proof: outputs and README / page output blocks match, 0 B steady state, tests three ways, A/B no slower.
- [ ] **2b. EECS086, EECS109 and EECS055's "a bool among N fields" become Warnings** (owner, 2026-10-27) once the
      samples use `[Accumulate]` / `[ForEachRecord]` and VoxelServer splits its bools (Body.OnGround, Exposure.Sunlit):
      one line each in the analyzer (pass `true`, fixable, in Kernel.cs for EECS055), with step 2.
- [ ] **3. Docs pass** (#141, after the refactor: pages describe and include the final code): everything in `docs-todos.md`; DocCheck and ApiDocs kept at 0 (both 0 since the fix-ci merge); decision
      record for the 8 declined declarations; roadmap item for inner loops; analyzer range EECS002-103.
- [ ] **4.** The `arm64` CI job (`ci.yml`, `runs-on: ubuntu-24.04-arm`, added b1dbdb0) has never run: it runs on the next
      push to master or a manual dispatch; if the label is wrong the job fails at once and the label is fixed then.
- [ ] **5.** `tools/bench.sh --site --sync` (after the refactor: the rewritten benchmark worlds are what the site measures), README numbers, `verify`, master. The owner cuts the release.
