# Owner decisions: the ledger (internal, not published)

Every decision the owner has made, in one place, so a later session finds it. A decision is a popup answer or a
message that sets a rule or chooses a design; status questions and "continue" are not. Rebuilt on 2026-11-07 from the
owner's messages and popup answers of 2026-10-27 to 2026-11-07 and from the plan files, commits and skills that
recorded the earlier ones (2026-10-23 to 2026-10-26).

**Keeping it current** (AGENTS.md, Workflows): every answer the owner gives is appended here in the same session,
with where it is applied. The docs pass (`audit-docs`) writes the records still due into `docs/content/decisions.mdx`
+ `.zh.mdx` and then replaces "due" with the D number. An answer that replaces an earlier one says so, and the
earlier row says "superseded by".

**Columns.** *Applied*: a commit on `claude/relaxed-ramanujan-j4j9k4`, a backlog item (`backlog.md`: C19, R4,
Finish 2, ...) or a plan file. *Record*, one of:
- **Dn**: the decision record in `decisions.mdx` that covers it.
- **due**: a record the docs pass writes (a draft or to-do in `docs-todos.md` is named where one exists; a provisional
  number such as "D139 draft" is not a published record yet).
- **rule: file**: a working rule, not a product decision; its text lives in the named instruction file or skill and
  gets no decision record.

## Waiting for the owner

| Date | Question | State |
|---|---|---|
| (none) | | |

## Open: not yet applied

| Id | Date | Decision | Lane |
|---|---|---|---|
| P10 | 2026-10-27, 2026-10-30 | Talk to the owner in Chinese | Lane 3 (instruction files), backlog T17 |
| P11 | 2026-10-28 | A choice for the owner goes to the owner as a popup; an action only the owner can take, as the exact command | Lane 3 (instruction files), backlog T17 |
| L7 | 2026-11-07 | The Cloudflare build watch excludes `docs/plans/*` and `docs/owner-review.md` (done by the owner) | Lane 3, backlog T18: `docs/AGENTS.md`'s Cloudflare table still says only "include `docs/*`" |

P10 and P11 are kept by the session that heard them, but no instruction file says them, so a new session would not
know. L7 is applied in the dashboard, but the file that documents the dashboard's settings does not show it.

## API and names

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| A1 | 2026-10-24 | `[IndexKey]` and `[UniqueKey]` read alike: rename? | `[IndexKey]` -> `[LookupKey]`; `[UniqueKey]` stays | 3edca9a (owner-review decided); feat-lookupkey-chunkrows | D129 (its "a code fix renames it" amended: due, docs-todos S3) |
| A2 | 2026-10-24 | The declaration surface (declarations.md P1-P6) | `WithAny` takes 2-9 types; hooks found by name (EECS090); `ReadOnlyMaybe<T>` is the read; `[GroupBy]` on the grouped member; `[Prefab]` (not `[Archetype]`); the world advances `EntityRandom.Tick` on every Update | b6c7c0b, 3157a68, aef682d, 0052f49, 45d6170; feat-views | D111 (`[Prefab]`); the rest due (D115-D117, docs-todos "D115-D117 have no draft") |
| A3 | 2026-10-27 | Names of the generator-gap forms | `[Neighbor]`, `[Accumulate]`, `[ForEachRecord]` (renamed by A8), `World.ReserveBuffers` | 695bb76 | due (D139 draft for `[Neighbor]`; docs-todos "feat-generator-gaps") |
| A4 | 2026-10-27 | Terminology popup | `[ForEach(ChunkRows)]` -> `ChunkSize`; `IDirectIndexed<TKey>` -> `IDirectMapped<TKey>`; `FloatMode.Exact` stays ("Burst's Strict + Deterministic"); `Lanes.RowNumbers` -> `Lanes.Selection` | f7df2e4 (code applied) | due (the reference-design renames record, docs-todos) |
| A5 | 2026-10-27 | Vocabulary renames of the naming popups | `TileReads` -> `TileStencil`; `TileBoundary.Fill` -> `Border`; `Lanes.Bits` -> `Ballot`; hidden `Prefetches` -> `PrefetchesPastLastLevel`; block -> cluster, `Pairs(reach)` -> `Pairs(cutoff)`; `[RedBlack]` -> `[GaussSeidel]`; `Pad()` -> `ExchangeHalo()` | feat-pairs-2, 9c73602, 0010f67 | due (same record) |
| A6 | 2026-10-27 | The hidden prefetch helpers' names | `LookupExtensions.Probes` / `PrefetchKey` stay | 038cfec | due (prefetch gates record, docs-todos S7) |
| A7 | 2026-10-27 | Chinese name of the user's pair method | 邻居对方法 (簇 / 簇对 stay the runtime's units) | docs-todos S5 (glossary) | due (glossary) |
| A8 | 2026-10-28 | `[ForEachRecord]` (walks `Emitted<T>`, clears it) renamed to? | `[Drain]` | 23664ee, dee6594 | D141 |
| A9 | 2026-10-28 | `[ForEntity]` and `world.ForParallel` renamed to? | `[EntityMethod]` (replaces the "keep `[ForEntity]`" answer of the same morning); `world.ParallelFor` | 3fc17a3, dee6594, 7f00105 | D141 |
| A10 | 2026-10-30 | Names of the row budget and its public runner | `[ForEach(MaxRows = N)]`; `World.ForEachCollected` | 6a901f4, bd6dd77 | due (docs-todos "MaxRows / World.ForEachCollected") |
| A11 | 2026-10-30 | Names of the `[GaussSeidel]` cold start and the sweep counter | `tile.Restart(value)` (superseded by A14); `tile.Sweep` | 2d2329d, 99b1b65 | due |
| A12 | 2026-10-31 | `TileColumns` / `TileColumn` too close to the shape `Columns<TTile>` | `TileColumnWalk` (superseded by A15) | 23a85f6 | due |
| A13 | 2026-11-07 | Name of the lambda over a buffer's elements | a `world.ParallelFor` overload | aef32bb on feat-inner-loops (not merged) | due (D80 extended) |
| A14 | 2026-11-07 | "Restart" already means a server restart in the docs | `tile.ColdStart(value)`; `tile.Sweep` stays | R1 | due |
| A15 | 2026-11-07 | "Columns" has three meanings | shape `Ground<TTile>`; the walk `TileColumns<…>`; `c.Columns(step)` stays | R2 | due |
| A16 | 2026-11-07 | `World.MaxChunkRows` -> `MaxChunkSize`? | yes; migration entry and a CS0117 code fix from the released `ParallelChunkRows` | R3, docs-todos S2 | due |
| A17 | 2026-11-07 | The naming sweep's other proposals: (a) delete the code fixes of never-shipped names; (b) `{Owner}ForEach` -> `{Owner}Passes`; (c) `CopyEntities<TTag>` -> `CopyEntitiesWith` | (a) only; (b) and (c) keep their names | R4, R6 | D129 amend due; naming record due (docs-todos "MaxRows / ForEachCollected") |
| A18 | 2026-11-07 | Delete the migration fixes of the other never-shipped forms (hook attributes, `[GroupBy]` on the struct, EntityRandom's tick by hand)? | delete; keep only the fixes from released forms (`WithAny<T>`, class systems, `ParallelChunkRows`) | R4, docs-todos S3 | due |

## Generated forms

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| G1 | 2026-10-24 | Declarations proposed in owner-review | declined: infer `ITracked`, infer `Mut<T>`, infer `[Consume]`, `[With<A,B>]` / `[Without<A,B>]` arity, auto Phase, `ISystemGroupWithAny`, `Scan<T>`, `Element<T>` for buffers | 3edca9a | due ("Declarations not added", docs-todos "Owner review decided (2026-10-24)") |
| G2 | 2026-10-24 | Infer `ChunkSize`? | yes, after feat-sched, if inferred is never slower than hand-set | feat-lookupkey-chunkrows | D130 |
| G3 | 2026-10-25 | Who tunes for the pipeline, caches and branch predictor? | the generated code; users write plain methods and never think about it | 0f2b89d (audit-generator "Tuned for the machine") | rule: audit-generator |
| G4 | 2026-10-26 | Do users learn cluster pairs and compress? | no: the order is inferred and the core picks the kernel from the plain code | 6297274 (locality.md 3.4) | due (D137 draft) |
| G5 | 2026-10-27 | When is a generator gap built? | whenever it saves users lines, boilerplate or thought, without them noticing; declined only when it saves neither | 6bf18d0 (audit-generator) | rule: audit-generator |
| G6 | 2026-10-27 | VoxelServer's gaps G3, G7, G11, G10; streaming stores | G3 / G7 / G11 declined; G10 (deferred consume, became `MaxRows`) built; streaming stores built behind a gate | 6bf18d0, 6a901f4, 38cc0a4 | due (docs-todos "Decided by the owner (2026-10-27)") |
| G7 | 2026-10-27 | EECS086's non-reducing total, EECS109 and EECS055's bool reason: Info or Warning? | Warnings, once the exemplar pass removes their blockers | A2 (lane 2), Finish 2b | due |
| G8 | 2026-10-27 | Slices on the pair target (zombies look at a tenth per tick) | every zombie every tick; GA8 not built | e3dbc12, voxel-rewrite.md 9 Q1 | due (VoxelServer page) |
| G9 | 2026-10-27 | Accept output changes from pairs, ordered rows and the scent | accepted; outputs rewritten once at the end | voxel-rewrite.md 9 Q2, Finish 2a | due (VoxelServer page) |
| G10 | 2026-10-27 | Item merging as the symmetric pair (needs GA4) | add it, build GA4 | e3dbc12, 685bbc2, Finish 2a | due (symmetric pairs draft, docs-todos) |
| G11 | 2026-10-27 | Keep the `Target` component? | keep (the nearest pair with the keep hint is the lesson) | voxel-rewrite.md 9 Q4, Finish 2a | due (VoxelServer page) |
| G12 | 2026-10-27 | A bundle of lookups as one parameter (GA5); box cutoffs for pairs (GA6) | both declined | a32c6c2, voxel-rewrite.md 9 Q8 | due (docs-todos "VoxelServer plan: decisions") |
| G13 | 2026-10-27 | Prefetch never fires at VoxelServer's scale: a larger parameter? | no; the sample keeps its scale | voxel-rewrite.md 9 Q9 | due (VoxelServer page) |
| G14 | 2026-10-27 | Ghost rows' example | both: VoxelServer (two region worlds, `MoveEntities` at the border) and MultiWorld | 6c3c08e (MultiWorld), Finish 2a | due |
| G15 | 2026-10-28 | Symmetric pairs (each pair once, both sides written) run serial: how to parallelize? | build every known HPC solution (no float atomics) and let the benchmark choose | 685bbc2, 5f49ac9 (feature skill) | due (symmetric pairs draft); rule: feature |
| G16 | 2026-10-28 | Reach members of other assemblies (8g) | as proposed, through `UnsafeAccessor` | b05cabc, 020813b | due (docs-todos "fix-kernel-foreign") |
| G17 | 2026-10-28 | Ghost rows: `world.CopyEntities<TTag>(target, ids, set)`? | build a prototype and benchmark it (2026-10-30: again yes) | c327e38, 581d784, 2ef54cb; kept 0e57f2b | due (draft, docs-todos "feat-owner-calls") |
| G18 | 2026-11-03 | A row-mask parameter for batch `[ForEach]` methods (`ulong rows` or `RowMask`) | not built: the per-row method plus EECS103 already gives the loop | 49c8d6f | due (draft, docs-todos "feat-later-small") |
| G19 | 2026-11-04 | Tile declarations written by hand 10 times: an attribute? | build `[Tile(Width = .., Depth = ..)]` writing `ITile<TKey>`, the shape and `Beside` | feat-generated-members (running) | due |
| G20 | 2026-11-04 | Hand-written direct-mapped windows | generate `Slots` / `SlotOf` from `[UniqueKey(SlotsPerAxis = N)]` | feat-generated-members | due |
| G21 | 2026-11-04 | The lowest-key reduction (MPI MINLOC) | build `[MinBy]` / `[MaxBy]`, ties keep the earlier row; vector form proved with ReduceBenchmark | feat-generated-members, G9 (lane 4) | due |
| G22 | 2026-11-04 | Countdown timers as a generated form | not built; the absolute deadline (`Until`) is the lesson | owner-review.md entry, docs-todos "countdown timers' lesson" | due (owner-review entry stays) |
| G23 | 2026-11-07 | `Beside` inferred for a 2D key named `(X, Y)`? | infer only `X` / `Z` (`Y` as height); an Error explains it otherwise | feat-generated-members | due |
| G24 | 2026-11-07 | The live demo's `MovementBatchSystem` (hand SIMD under a pragma) | rewrite it (and `DragBatchSystem`) in the plain form; the page compares the generated loop with `SimdMode.Off` | feat-inner-loops item c, docs-todos "The live demo's comparison" | due (D134's demo note) |

## Interop (Node, Rust, Go)

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| I1 | 2026-11-07 | Can EasyEcs be used from Node, Rust or Go ("看看能不能导出给node用，或者rust和go，你研究一下可行性") | Research first; the report (`interop.md`) shows it works through a Native AOT C-ABI library | `interop.md` | due |
| I2 | 2026-11-07 | Scope: a C# world driven from other languages, or users who never write C# | A C# world driven from other languages (components and fast systems in C#; host systems as per-run callbacks); run-time components (the flecs model) not built | backlog Lane 6, `interop.md` | due |
| I3 | 2026-11-07 | Which host languages first | Rust, Node and Go (bindings published as crates.io `easyecs-sys` + safe crate, npm, Go module) | backlog Lane 6 | due |
| I4 | 2026-11-07 | A WebAssembly npm package | Not built (the native library is faster and multi-threaded for Node servers) | `interop.md` | due |
| I5 | 2026-11-07 | Package form and ABI prefix | A new package `EasyEcs.Native`; prefix `easyecs_`, `easyecs_abi_version()`, status codes plus `easyecs_last_error()`; no schema file (the C# struct is the schema) | backlog Lane 6 | due |

## Core and memory

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| C1 | 2026-10-23 | What else goes into 4.8 | #138 research, mixed lane widths in vector loops, EECS061 | docs-todos "Also in 4.8" (done) | D90 (lane widths); the rest due |
| C2 | 2026-10-24 | Retry vectorized reads of other entities (sorted by table, AVX-512) | yes, measured | feat-lookup-gather | D131 |
| C3 | 2026-10-26 | Lookup gathers under `SimdMode.Auto` | re-measure after L1 and WP0b; result: they stay on request | 648050c, feat-pairs-2 | D131 |
| C4 | 2026-10-26 | A timer wheel | dropped | 932913f | due (docs-todos "Owner decisions on owner-review (2026-10-26)") |
| C5 | 2026-10-26 | NUMA placement and hybrid cores | dropped | 932913f | due (same) |
| C6 | 2026-10-26 | Batch probes and the spread-map nearest | re-measure; result: vertical probes dropped at 16M keys, the spread map keeps the pair method | 932913f, locality.md 6.2 / 6.3 | due (docs-todos "feat-amac") |
| C7 | 2026-10-26 | Cluster pairs on ARM64 while sparse maps are only 1.1-2.1x faster | ship them there too and optimize; a bar decides what to build, never ships a slower case | 533793f | due (D139 draft) |
| C8 | 2026-10-27 | Look at every feature, prefetch included ("都研究一下，比如 prefetch") | prefetch gates built, then a gate per form | 7c186ca, 038cfec | due (docs-todos S7) |
| C9 | 2026-10-27 | SVE's PRFB | stays; its proof is the arm64 CI job the owner dispatches | 038cfec, T16, Finish 4 | due (S7) |
| C10 | 2026-10-27 | fix-ci made Restore 66.6 -> 84.4 us, Rollback 141 -> 177 us | win the cost back later | f7df2e4; won back by incremental snapshots (5c5e99a) | due (docs-todos "Incremental snapshots") |
| C11 | 2026-10-27 | More ways to copy less memory (12a-e) | all approved ("这些都可以做") | 2728b73, 5c5e99a | due (docs-todos fewer-copies drafts) |
| C12 | 2026-10-31 | Huge pages to cut page faults | research and build | bf79c63, 685c1f5, 85e0394 | due (docs-todos "feat-later-memory") |
| C13 | 2026-11-07 | The core's unmanaged columns on native memory (owner-review) | option (b): mimalloc as a native dependency | native-columns.md, C19 | due |
| C14 | 2026-11-07 | mimalloc's fallback switch and binaries | a hidden `AppContext` switch `EasyEcs.NativeColumns`; binaries per RID; automatic fallback to the pinned heap | native-columns.md 2-3, C19 | due |
| C15 | 2026-11-07 | Page-fault attempts not kept (prefaulting copies, a pool of disposed worlds' columns, prefault against huge-page refaults) | settled: not built | dcb8019 | due (draft 4, docs-todos "owner-review settled") |
| C16 | 2026-11-07 | `[Accumulate]` users sharing a stage | settled: not built | dcb8019 | due (draft 5) |
| C17 | 2026-11-07 | The helpers' queue counter on its own line; thread pinning | stay in owner-review: need a quiet 8+ core machine; not in the M4 list | dcb8019, owner-m4.md header | owner-review.md (open entry) |

## Tiles and cells

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| T1 | 2026-10-27 | Two ways to update a grid | the plain step is Jacobi (ping-pong, generator owns the buffers); `[GaussSeidel]` relaxes in place | 4ae42d2, ed9f5e9, 307fb2d | due (D77 rewrite) |
| T2 | 2026-10-27 | When does the VoxelServer rewrite run? | after the u/v swap is fixed ("你得等你的 uv 交换先修好"), so every tile system is written once in ping-pong form | c3fd180, Finish 2a | rule: voxel-rewrite.md |
| T3 | 2026-10-27 | The scent: if the plain `[GaussSeidel]` cell form is slower than the hand scan | a generator gap: fix the generator, keep no hand scan, accept no slower form | voxel-rewrite.md 9 Q6, 8c (2d2329d), Finish 2a | due |
| T4 | 2026-10-27 | Terrain generation | a cell method with `out Block` after WP5, only if not slower | voxel-rewrite.md 9 Q5, Finish 2a | due |
| T5 | 2026-10-28 | The ping-pong step is no faster than step + commit by hand: drop it (net-gain rule) or fix? | fix, then merge | f6ff45d, 49b7afe | due (draft 6, docs-todos "owner-review settled") |
| T6 | 2026-10-28 | Generated code loses where the idea should win | the generated code is at fault: read its asm and choose the path from use | fb46e8f (audit-generator "An owned form picks its path by use") | rule: audit-generator |
| T7 | 2026-10-28 | After the fix: in-cache grids still lose on neighbor reads | merge first, then fix the reads | 307fb2d; reads: T9-T12 | due |
| T8 | 2026-10-29 | Zombie chase: cluster pair + the scent tile instead of the complex form | the rewrite uses the nearest pair and the `[GaussSeidel]` scent | Finish 2a (voxel-rewrite.md) | due (VoxelServer page) |
| T9 | 2026-10-30 | `c.Columns(...)` to close the 1.14x Laplacian gap | add it | 3ecde01 | due (draft, docs-todos "feat-owner-calls") |
| T10 | 2026-10-30 | An Info rule when a `TileCursor` loop is not proven | add it now (EECS120) | a8b8b4a | due |
| T11 | 2026-10-30 | EECS116: a code fix limited to the step method? | build it | a8b8b4a | due |
| T12 | 2026-10-31 | The old commit still copies an unwritten buffer after EECS116's fix | both: the fix deletes the commit and EECS121 reports the rest | 140db6b, 60d6388 | due (draft) |
| T13 | 2026-10-31 | `col.Store(vector, into)` for the last 6 instructions | add it | 23a85f6 | due (draft) |
| T14 | 2026-11-04 | VoxelServer's water as cells: 3.61 against 1.40 ms; an API for the layers holding data? | build it this round | cc1cf2c; the water port did not ship (c5667bd); backlog feat-inner-loops item d | due (docs-todos "ITileLayers") |
| T15 | 2026-11-04 | Name of that API | `ITileLayers` (`From` / `To`) | cc1cf2c | due |
| T16 | 2026-11-07 | `Enter` handing the stepped tile out | settled: not built | dcb8019 | due (draft 1) |
| T17 | 2026-11-07 | A lane-shift helper for tiles | settled: not built | dcb8019 | due (draft 2) |
| T18 | 2026-11-07 | `Rows()` turning odd `[GaussSeidel]` sweeps by itself | settled: not built | dcb8019 | due (draft 3) |
| T19 | 2026-11-07 | The generated ping-pong step against step + commit by hand | settled: the generated step is the recommended form | dcb8019 | due (draft 6) |

## Process and rules

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| P1 | 2026-10-24 | Where do proposals not taken go? | `docs/owner-review.md`, decided by the owner | 9719a9b | rule: AGENTS.md ("Decide, then record") |
| P2 | 2026-10-24, 10-25 | How many code agents at once | at most 3 (2 on 2026-10-24, 3 from 2026-10-25); benchmarks one at a time under the lock | audit skill, backlog "Order" | rule: audit |
| P3 | 2026-10-26 | Order of the work | every code change before any docs ("先把代码全改完"); docs checked once at the end | 2515638, backlog "Order", docs-todos header | rule: backlog.md |
| P4 | 2026-10-26 | Vector forms on ARM64 | every vector form runs on ARM64 too, or records why not | 799aea6 | rule: audit-vector-loops |
| P5 | 2026-10-27 | Refactoring against docs and benchmarks | the refactor first, so docs and numbers are written once about the final code | backlog Finish | rule: backlog.md |
| P6 | 2026-10-27 | A plan's bar (1.2x, 1.5x, 20 %) | not a ship gate ("门槛不用严格卡，有收益就行"): faster in both rounds, 0 B, nothing slower ships (Energy's 1.43x shipped) | 6d074d4 | rule: audit-perf |
| P7 | 2026-10-27 | Names of borrowed features | the reference design's vocabulary (OpenMP, CUDA, MPI, Burst, MD) | edd495c | rule: audit-naming |
| P8 | 2026-10-27 | Where is the exemplar pass? | first step of Finish: every sample, the demo, benchmarks, tests that show usage and every docs example rewritten in the best form (restated 2026-10-30) | 912e412, ba6da74, Finish 2 | rule: backlog.md, audit-code |
| P9 | 2026-10-28 | `[ForEach]` and `[ForEachRecord]` look alike | two names that look alike must do the same kind of thing | audit-naming step 2 | rule: audit-naming |
| P10 | 2026-10-27, 10-30 | Language of the conversation | Chinese ("用中文对话") | not in any instruction file: Open, backlog T17 | rule due: AGENTS.md |
| P11 | 2026-10-28 | How the owner is asked | a choice as a popup, an action as the command to run ("需要我选的给我弹窗，需要我做的给我命令") | not in any instruction file: Open, backlog T17 | rule due: AGENTS.md |
| P12 | 2026-10-30 | Deferring, skipping problems | never defer: try it now; research the cause of a problem (read the asm) before deciding | d1135fd | rule: AGENTS.md ("Try it now") |
| P13 | 2026-11-01, clarified 2026-11-07 | "强推去掉"; "如果需要改commit message之类的才强推，不然不需要" | Force-push only when history must be rewritten (a commit message or author to fix); otherwise plain pushes | AGENTS.md, Rules (Commits) | rule: AGENTS.md |
| P14 | 2026-11-07 | Open items | none left behind ("不要有backlog"): every open item is done | backlog "Order" | rule: backlog.md |
| P15 | 2026-11-07 | Check the plans against the code | run every skill over the repository | the sweep of f9faa23, 78ffdd4, 3b0a5cc | none (one round) |
| P16 | 2026-11-07 | Finished branches | deleted at the very end of Finish, after master (`research-copy-asm` with them) | backlog "Owner actions", Finish 6 | rule: backlog.md |
| P17 | 2026-11-07 | Where are decisions kept? ("这些要做的决策得记下来，不然以后找不到了") | this ledger, appended in the same session | plans-ledger branch | rule: AGENTS.md (Workflows) |
| P18 | 2026-11-08 | Token cost: three Opus agents at once used half the weekly usage overnight; keep, drop to one, pause, or switch to Sonnet? | keep as is: three agents, Opus | this session | none |
| P19 | 2026-11-08 | Which model does each agent use? ("根据任务决定用什么，机械活用 sonnet，高难度的再用 opus") | Sonnet for mechanical work the prompt spells out, Opus for work needing judgment; every Agent call names its model | AGENTS.md (Workflows) | rule: AGENTS.md |

## Hardware runs

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| H1 | 2026-10-26 | Wide components on NEON (owner's M4) | ship: Steer 2.07x / 1.55x, Glide 1.87x / 1.63x faster than rows | b1ee249 | due (docs-todos "feat-arm-parity") |
| H2 | 2026-10-27 | Loads issued ahead on Apple silicon (owner's M4) | dropped: Auto equals Off | 567fa37 | due |
| H3 | 2026-10-28 | Neighbor lists against ranges on ARM64 (owner's M4, PairListsBenchmark) | Auto within the error of the best in all 24 cases; lists never win clearly | a43af2a | due (D139 draft) |
| H4 | 2026-10-29 | Streaming stores on ARM64 (owner's M4) | Auto stays on ARM64 | 54870ee | due (streaming stores record) |
| H5 | 2026-10-29 | NEON tables as literals (owner's M4 bisect) | stay statics until the M4 measures a change | 518eeca, owner-m4.md 1 | due |
| H6 | 2026-11-07 | When are the hardware runs? | on the owner's M4, after every code change | owner-m4.md, Finish 5 | rule: owner-m4.md |
| H7 | 2026-11-07 | Prefetch gates on an x86 box with a 32 MiB LLC (the M4 has no .NET prefetch) | not run; the record states the status (proven on this VM's 260 MiB LLC only, and the run that would move a gate) | owner-m4.md 6, Finish 5, docs-todos S7 | due |

## Docs and site layout

| Id | Date | Question | Answer | Applied | Record |
|---|---|---|---|---|---|
| L1 | 2026-10-26 | Restructure the whole repository's directories ("调整整个项目的目录结构") | yes, after every code branch merges; tests and benchmarks too | c2e9e90, layout.md, Finish 1 | rule: Finish 1 (maps in AGENTS.md) |
| L2 | 2026-10-27 | Tiles' names: shader or HPC terms | HPC terms in names and headings (stencil, tile, halo); the shader analogy in prose only | grids page, docs-todos "Owner answers 2026-10-27" | due (grids page) |
| L3 | 2026-10-27 | Chinese for Slice | 分帧 (not 分片, which reads as sharding) | tools/docs/ApiDocs/zh/README.md glossary | due (glossary) |
| L4 | 2026-10-29 | Unify the tile exception texts with the current terms | yes ("Outside the tile and its halo") | ee5ef76 | none (applied) |
| L5 | 2026-11-07 | The folder layout (338 flat files) | all as recommended: Generator keeps `Kernel/`, pair methods in `OrderedTables/`, prefetch files in `Lookups/`, the either-way files as in finish-scan.md 2.2 | f9faa23, Finish 1 | D142 |
| L6 | 2026-11-07 | `tools/` subfolders | add `tools/perf/` for the performance scripts and projects | f9faa23, Finish 1 | D142 |
| L7 | 2026-11-07 | Where `docs/plans/` and `owner-review.md` live (Cloudflare rebuilds on `docs/*`) | stay; Cloudflare's build watch excludes `docs/plans/*` and `docs/owner-review.md` (done by the owner) | backlog "Owner actions" [x]; docs/AGENTS.md not yet (Open, T18) | rule due: docs/AGENTS.md |
| L8 | 2026-11-07 | Countdown timers on the docs pages | teach the absolute deadline (`clock.Tick < cooldown.Until`) and when a countdown still fits | docs-todos "The countdown timers' lesson" | due |
| L9 | 2026-11-08 | When the folder restructure runs ("目录里面的东西太扁平了") | as soon as the three running branches (halos, analyzer lane 2, lane 4) merge; lanes 1, 5 and 6 start after it, on the new folders | backlog Finish 1 moved ahead | rule: Finish 1 |
| L10 | 2026-11-08 | What else gets folders besides the four projects and `tools/perf/` | also `docs/content/guide` pages (grouped, with `_redirects` and meta.json sections), `tools/` beyond perf, and `docs/plans/` | backlog Finish 1 | D142 (the folders); the guide's record: due |
| L11 | 2026-11-08 | How the guide pages are grouped | real folders with 301 redirects: basics/ (project-structure, entities-and-components, prefabs, queries, systems, singletons), changes/ (structural-changes, bulk, cleanup-and-disable, refs-and-await, change-tracking), data/ (dynamic-buffers, indexes-and-relations, hierarchy, groups), saves/ (snapshots, saves, migration-and-unload), performance/ (threading, high-performance, grids), production/ (hosting, testing, diagnostics), help/ (analyzer, common-mistakes, troubleshooting); links, zh pages, search expectations updated | backlog Finish 1 | due |
| L12 | 2026-11-08 | tools/ and docs/plans/ folders | tools/perf/ (bench.sh, jitdisasm.sh, loopbound.sh/.py, inlining.sh, Inlining/, LoopBound/), tools/docs/ (ApiDocs/, DocCheck/), tools/Shared/, agents.sh and build.sh at the root; docs/plans/ keeps backlog.md, docs-todos.md, owner-decisions.md at its root, design plans in designs/, owner-m4.md in owner/, layout.md deleted after the move | backlog Finish 1 | D142 |
| L13 | 2026-11-08 | `llms-full.txt` over lint:seo's 3,000,000 cap | the generated API reference moves into its own `llms-reference.txt` (+ `.zh`), capped apart (2,000,000); `llms-full.txt` keeps the written pages under 3,000,000; llms.txt and the AI page link both | generate-static.ts, lint-seo.ts, ai.mdx + zh | applied |
| L14 | 2026-11-14 | .NET 11 is released ("net11成正式版了，可以平台添加net11，然后兼容一下，把net11的新的性能优化用的特性也弄进来") | add `net11.0` beside `net10.0` (both stay supported), use .NET 11's performance features where they measure as wins (under `#if NET11_0_OR_GREATER`), update AGENTS.md, CI and the docs | backlog `feat-net11`; package A (delegated calls, recorded for the docs pass's record): libraries, tests, AOT check and benchmarks target `net10.0;net11.0`; samples, DocCheck and ApiDocs `net11.0` (the SDK's runtime); Godot and the demo `net10.0`; the template's `--framework` defaults to `net10.0` (the LTS, which either SDK builds), `-f net11.0` picks .NET 11; global.json 11.0.100 | due (docs-todos `feat-net11`) |
| L15 | 2026-11-14 | Generated code after G12-G18 ("生成的代码顺便看看能否进一步精简，例如重复赋值的相同变量之类的") | another round over the generated text: the same value assigned to several locals, identity copies, unread locals, repeated setup; the text gets shorter even where the JIT already removes the copy (this overrides owner-review's "the rest of G17 changes the text only") | backlog `feat-generated-text-2` | due |
| L16 | 2026-11-14 | Which runtime the site's benchmark numbers come from, with net11.0 beside net10.0 | both ("两个都放，然后ci用matrix并行跑，这样耗时一样，页面和同步脚本改一下"): every table shows .NET 10 and .NET 11; CI runs the two runtimes as a matrix in parallel (tests and checks, and a dispatched benchmark workflow, one job per runtime, whose reports the sync script merges); `sync-benchmarks.ts`, `bench.json` and the pages' tables carry a runtime dimension | backlog `feat-net11` package S | due |
| L17 | 2026-11-14 | Where the site's published numbers are measured (a fixed machine, CI, or a self-hosted runner) | CI ("用 CI 跑出来的数字"): the Benchmarks workflow's matrix run is the source of `bench.json` for both runtimes; the site names each runtime's machine; the reference-VM run is no longer the release step | Finish 6/7 (`verify`: dispatch Benchmarks, download, sync `--runtime` per set), docs/AGENTS.md | due |
| L18 | 2026-11-14 | How the owner's decisions are asked ("需要我决定的问题也全部弹窗问我") | every question that is the owner's goes out as a popup as soon as it is known (also the ones a lane parks in owner-review as "the owner's call"), not as a note in a report | AGENTS.md "Talking to the owner" | rule |
| L19 | 2026-11-14 | A ground cell that also reads a layer of its 3D tile (feat-ground-cells' open form) | build it; the form's shape is asked as a popup before it is implemented | backlog `feat-ground-layer` | due |
| L20 | 2026-11-14 | Which runtimes the package carries mimalloc for (C19) | all mainstream ones: linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64, built by a matrix in the release workflow (Windows and macOS runners added) | backlog C19 (lane 1 package F) | due |
| L21 | 2026-11-14 | Which runtime the home page's headline numbers, the social cards and llms.txt quote | .NET 11 (one runtime there; the tables and charts keep both) | backlog `feat-net11` | due |
| L22 | 2026-11-14 | C# 15 extension indexers (`entity[...]`, `world[entity]`) | not added: the generated members are already the short form, a second syntax for the same access | owner-review, net11.md B10 | settled |
