# The deinterleave copy (`var __c1_0 = __s1_0;`): IL and asm analysis

Internal. The analysis behind the 32-local rule (`audit-vector-loops`) and the pending merge of `research-copy-asm`
(`backlog.md`). Paths under the session scratchpad it cites are gone.

Worktree `/workspace/EasyEcs/.claude/worktrees/copy-asm`, branch `research-copy-asm` (base 46314ee). Machine: 4 cores,
AVX-512 (F/BW/CD/DQ/VL/IFMA/VBMI), .NET SDK 10.0.402. Listings: `DOTNET_TieredPGO=0`, `DOTNET_TC_CallCountingDelayMs=0`,
`DOTNET_JitDisasm` + `DOTNET_JitDisasmSummary` + `DOTNET_JitStdOutFile`, run from a driver that calls the benchmark
methods 4 x 2,000 times (worlds: 200 ticks) with pauses so Tier1 lands. Inlining decisions: the runtime's own
`MethodJitInliningSucceeded/Failed` events (JitTracing keyword 0x1000) read by an in-process `EventListener`
(`DOTNET_JitPrintInlinedMethods` is checked-runtime only). IL: `ilspycmd --ilcode`.
Everything is under `/tmp/claude-0/-workspace/1ad27225-4a0d-50e5-acbc-5950e4df5375/scratchpad/copyasm/`
(`driver/`, `tools/`, `asm/{base,nocopy,head,pgo-*}`, `bdn/`).

## 1. Root cause: the JIT never inlines a method of more than 32 IL locals

D126 saw Move3's runner (`World.Masked`) go from 934 to 1,095 instructions when the copy was removed and kept the copy
"with no root cause". The cause is not IL size, not address exposure, not register pressure:

* RyuJIT refuses to inline any callee whose IL declares more than **32 locals** (`MAX_INL_LCLS`, observation
  `CALLEE_TOO_MANY_LOCALS`, reported as **"too many locals"**). The observation is fatal and applies to
  `AggressiveInlining` methods too; the callee is then marked noinline for every later call site
  ("noinline per IL/cached result").
* The copy doubles the locals of every deinterleaved field: `out Vector512<float> __s1_0` is one IL local and
  `var __c1_0 = __s1_0` another. Roslyn keeps both because `__c1_0` is reassigned later
  (`__c1_0 = ConditionalSelect(...)`); it only folds the copy when the copy is never written (DeathSystem's DieJob0:
  its IL is byte-identical either way, 632 / 666 bytes).
* Move3's masked `Execute` (P3 + V3, 6 deinterleaved fields, two widths): **36 IL locals with the copy, 24 without**.
  IL size 638 -> 590 bytes. The runtime's events, Move3 run:
  ```
  base:   Failed:    World::Masked <- Move3Job12::Execute (ES,unsigned int64,Span<P3>,Span<V3>) too many locals
          Failed:    World::Masked <- Move3Job12::Execute (ES,unsigned int64,...) noinline per IL/cached result
  nocopy: Succeeded: World::Masked <- Move3Job12::Execute (ES,unsigned int64,Span<P3>,Span<V3>)
  ```
  The threshold is exact on every job measured: 32 locals inline (`UpdateCellJob0` masked: 32, inlined), 33 do not
  (`CycleJob9` masked: 33, "too many locals" in both variants), 36 / 41 / 43 do not.
* So D126's "+161 instructions, 36 -> 54 permutes" was the masked body **inlined into the runner** instead of a
  `call [Move3Job12:Execute(...,ulong,...)]` per 64-row word. It was an improvement in kind, not a regression; Move3's
  benchmark never runs a masked word (no holes), which is why BDN saw it level.

Hypotheses ruled out with the listings:
* **Address-exposed `out` locals.** After the `Load3` inline, local morph turns the `ldloca` + store into plain local
  stores; neither variant has a vector stack slot in the hot loop (`[rbp-..]` zmm traffic 0 in Move3Gaps, Showcase
  Move, Scenario Move; the 20 stack slots in the first no-copy Move3 listing were the masked path never having run:
  `Interleaved.Tables32` was not yet initialized at Tier1, so the JIT emitted 4 `CORINFO_HELP_GET_GCSTATIC_BASE` calls
  and kept the tables in stack slots; with the masked path hot (Move3Gaps) they are constants and there is no spill).
* **The IL size budget.** Masked Execute IL 638 vs 590 bytes; both are far below what an AggressiveInlining callee
  may have, and the failure reason is "too many locals", not "inline exceeds budget".
* **Struct promotion of Vector512 locals.** Vector locals are SIMD-typed primitives, never promoted structs.

## 2. What the copy costs and what removing it costs (Tier1, PGO off)

Instructions per runner (whole method) and per vector of the AVX-512 masked loop:

| Case | Method | base (copy) | no copy | Note |
|---|---|---|---|---|
| Move3 (KernelBenchmark, no holes) | `World.Masked<P3,V3,Move3Job12>` | 833 ins, 0 shuffles, `call Execute` per masked word | 1,044 ins, 27 shuffles, no call | masked Execute inlined (cold here) |
| Move3Gaps (masked words, hot) | runner + masked loop | 833 + call to a 177-ins Execute; **54 ins/vector** (18 perm, 12 table loads, 4 reg moves) | 964 ins, no call; **64 ins/vector** (18 perm, 18 table loads, 8 reg moves) | inlined; the big runner reloads the 6 Load3 tables for the second Load3 (no CSE) |
| Move4Gaps (masked words, hot) | idem | 808 + call to a 200-ins Execute; **66 ins/vector** | 963, no call; **73 ins/vector** | inlined, same table-CSE loss |
| Cycle / CycleHoles | `World.Masked<Heat,Mass,CycleJob9>` | 1,024 | 1,024 (identical) | CycleJob9 masked: 33 locals both ways, still called |
| BattleServer Showcase Move (masked) | `World.Masked<Position,Velocity,MoveJob0>` | 853, **27 ins/vector** | 854, **28 ins/vector** | +1 `vmovaps zmm, zmm` |
| BattleServer, every other runner and job (DieJob0 included) | | identical | identical | |
| WorldSimulation Move (masked) | `World.Masked<Position,Velocity,MoveJob0>` | 936 | 938 | +2 `vmovaps` |
| WorldSimulation Respawn (masked) | `World.Masked<Health,RespawnJob1>` | 1,047 | 1,048 | +1 `vmovaps` |
| WorldSimulation UpdateCell | `World.Masked<Position,AoiCell,...>` | 1,039 | 1,039 (identical) | |

The `+1 vmovaps` (Showcase Move, both listings):
```
base                                        no copy
vaddps     zmm3, zmm4, zmm6                 vaddps     zmm3, zmm4, zmm6
vaddps     zmm2, zmm1, zmm2                 vaddps     zmm2, zmm1, zmm2
vpternlogd zmm3, zmm0, zmm4, -30            vpternlogd zmm4, zmm0, zmm3, -72
vpternlogd zmm0, zmm1, zmm2, -84            vpternlogd zmm1, zmm0, zmm2, -72
vmovups    zmm1, [reloc @RWD192]            vmovups    zmm0, [reloc @RWD192]
vmovaps    zmm2, zmm3                       vmovaps    zmm2, zmm4
vpermt2ps  zmm2, zmm1, zmm0                 vpermt2ps  zmm2, zmm0, zmm1
vmovups    [r9+4*r14], zmm2                 vmovups    [r9+4*r14], zmm2
vmovups    zmm1, [reloc @RWD256]            vmovups    zmm0, [reloc @RWD256]
                                            vmovaps    zmm2, zmm4
vpermt2ps  zmm3, zmm1, zmm0                 vpermt2ps  zmm2, zmm0, zmm1
vmovups    [r9+4*r14+0x40], zmm3            vmovups    [r9+4*r14+0x40], zmm2
```
Why: LSRA allocates per IL local (one interval per lclVar), not per SSA definition. With the copy, the loaded value
(`__s1_0`) and the selected value (`__c1_0`) are two locals, two intervals; the select is free to overwrite the dead
mask register and the last destructive `vpermt2ps` consumes `__c1_0` in place. Without the copy, the load and the
select are two definitions of one local, so the select is tied to the load's register (`zmm4`) and the local stays in
it at the loop back edge: the last `vpermt2ps` (which overwrites its first source) needs a copy. It is a register move
(eliminated at rename on current x64 cores), one per written two-field component per vector.

The Move3/Move4 per-vector growth once inlined comes from the bigger method, not from the text: in the 177-instruction
`Execute` the JIT CSEs the six Load3 tables across `e1`'s and `e2`'s loads (12 table loads per vector, `vpermi2ps`
form), in the 964-instruction runner it reloads them (18) — CSE's profitability thresholds depend on how many locals
the method tracks. Table loads are L1 hits off the critical path.

## 3. Candidate forms

IL measured on a micro (`forms/forms.il`): one Load2 and a use, Roslyn Release.

| Form | Text per load | IL locals per n-field load | IL bytes (2 fields) | API | Listings |
|---|---|---|---|---|---|
| A. `out __s` + `var __c = __s;` (base) | n + 1 lines | 2n (Roslyn folds the copy only when `__c` is never written) | 21 | none | base |
| **B. `out Vector512<float> __c1_0` directly** | 1 line | **n** | **19** | none | measured: inlines Move3/Move4 masked, +1 `vmovaps` per written 2-field component in masked loops elsewhere |
| C. `var (__c1_0, __c1_1) = Interleaved.Load2Of512(ref e1, i)` (ValueTuple) | 1 line | n (Roslyn uses `dup`, no temp) | 28 | 6 new methods (Load2/3/4 x two widths: a return type cannot overload) | same locals as B, more IL, a 128-256-byte tuple returned through a hidden buffer; no gain over B on the limit that matters, not prototyped further |
| D. a `Pair`/`Triple` struct, fields copied out | 1 + n lines | n | 28 | new structs + methods | same as C |
| E. the struct kept, fields read in place (`__l1.F0` at every use) | 1 line | **1** | 26 | new structs + methods | fewest locals, but every write site becomes a field store (`__l1.F0 = ...`), the whole Kernel text changes (not localized) and a struct of 3-4 Vector512 depends on physical promotion; a lever only if a job still needs it |
| F. `Unsafe.ReadUnaligned` per field | - | - | - | - | not applicable: fields are interleaved, a deinterleave needs the permutes |

B has the least text, the fewest IL bytes, and the fewest locals any form without a struct can have.

## 4. BenchmarkDotNet (BenchmarkConfig job: 2 launches, 6 warmups, 8 iterations; PGO on; base vs head interleaved)

Head = B for component and `Maybe<T>` loads. `Move3Gaps` is new (Move3 over the Gaps table, every third row without
V3: masked words, the form whose inlining the copy decided). Machine shared with other agents (load average 4-7), so
whole-world ticks drift between rounds.

| Benchmark | base r1, r2 | head r1, r2 |
|---|---|---|
| KernelBenchmark.Move3GapsGenerated 1,000 | 695.2, 720.5 ns | 680.6, 713.4 ns |
| KernelBenchmark.Move3GapsGenerated 100,000 | 85,139, 85,967 ns | **80,499, 81,191 ns** (-5.5 %) |
| KernelBenchmark.Move4Generated 1,000 | 186.9, 185.4 ns | **170.2, 172.8 ns** (-8 %) |
| KernelBenchmark.Move4Generated 100,000 | 113,604, 125,924 ns | 118,243, 117,085 ns |
| KernelBenchmark.Move3Generated 1,000 | 128.9, 143.8 ns | 133.1, 131.9 ns |
| KernelBenchmark.Move3Generated 100,000 | 62,915, 68,368 ns | 67,380, 66,900 ns |
| KernelBenchmark.Move2GapsGenerated 1,000 | 363.4, 382.7 ns | 414.9, 350.8 ns |
| KernelBenchmark.Move2GapsGenerated 100,000 | 44,945, 44,165 ns | 43,838, 42,497 ns |
| KernelBenchmark.PushGenerated 1,000 | 107.0, 103.2 ns | 105.2, 97.6 ns |
| KernelBenchmark.PushGenerated 100,000 | 26,344, 29,156 ns | 25,539, 26,564 ns |
| WorldSimulationBenchmark.Tick 10,000 | 109.4, 122.3 µs | 108.3, 115.4 µs |
| WorldSimulationBenchmark.Tick 100,000 | 1,227.2, 1,266.1 µs | 1,355.2, 1,367.4 µs |
| BattleServerBenchmark.Tick 100,000 | 3.652, 3.921 ms | 4.191, 4.149 ms |

The world ticks were rerun (rounds 3-4: WorldSimulation and BattleServer, head first in round 3; rounds 5-6:
BattleServer only, head first in round 5):

| Benchmark | base, all rounds | head, all rounds |
|---|---|---|
| WorldSimulationBenchmark.Tick 10,000 | 109.4, 122.3, 110.8, 111.5 µs | 108.3, 115.4, 104.5, 109.7 µs |
| WorldSimulationBenchmark.Tick 100,000 | 1,227.2, 1,266.1, 1,275.1, 1,339.1 µs (mean 1,277) | 1,355.2, 1,367.4, 1,289.6, 1,317.3 µs (mean 1,332) |
| BattleServerBenchmark.Tick 100,000 | 3.652, 3.921, 4.388, 3.711, 3.949, 4.273 ms (mean 3.98) | 4.191, 4.149, 4.197, 4.111, 3.794, 3.998 ms (mean 4.07) |

Both world ticks are inside base's own spread (BattleServer 3.65-4.39 ms, WorldSimulation 100,000 1,227-1,339 µs);
their PGO-off Tier1 code differs from base by 1 `vmovaps` per 16 rows in the masked words of `Move` (BattleServer) and
2 + 1 in `Move` / `Respawn` (WorldSimulation), a few thousand register moves per tick against a 4 ms / 1.3 ms tick,
and with PGO on (BDN's setting) the Tier1 code of jobs this change does not touch (AfflictJob0, ForEachEntityLive)
differs between two runs of the same build by hundreds of instructions. The +2 % / +4 % means are not attributable;
the machine was shared (load average 4-7) throughout.

All 0 B.

## 5. Recommendation

Ship B (done on `research-copy-asm`, not pushed):

* `1e21668` Vector loops: deinterleaved loads write each field into its name (Kernel.cs, component loads and
  `Maybe<T>` loads; a field of another type of the lane size keeps `__s` + `As`; the template comment names the
  32-local reason).
* `0ff81cc` Test: `GeneratorTests.DeinterleavedJobsStayWithinTheInlinersLocals` emits Move3 / Move4 / a mixed
  int+float component in Release and reads each `Execute`'s local signature (System.Reflection.Metadata): none over
  32. Fails on base (`Move3Job0` 41, `Move4Job1` 43), passes on head. Full suite on head: 2,718 passed.
* `71f03f1` KernelBenchmark: `Move3GapsScalar` / `Move3GapsGenerated` (the Gaps table also holds P3/V3 rows, every third
  without V3).

Why it wins: it is the least text (one line per load instead of n + 1), the least IL (-4 bytes and -1 local per
field), needs no API, and it is what decides whether a masked job is inlined at all: 16 `Execute`s of the benchmark
assembly fall under the JIT's 32-local limit (25 over -> 9 over), among them Move3 / Move4 masked and Fusion's
Bounce jobs (37 / 39 -> 29 / 31). Measured: `Move3Gaps` 100,000 rows -5.5 % (two rounds each), `Move4` 1,000 rows
-8 %, the other kernels level, 0 B. Cost: one register move per written two-field component per vector in masked
loops that were already inlined (LSRA's one interval per local); not visible in any benchmark.

Before landing on master: rerun WorldSimulation 100,000 and BattleServer on a quiet machine (`audit-perf`'s method),
since their means sit 2-4 % above base's on a busy one. Remove the allow-list entry `GeneratedTextTests` was to have
for this copy (`audit-generator` "The generated text" and `docs/plans/generated-text.md` section 3 both say the copy
stays; they should now say it is gone and why), and correct `audit-vector-loops`' two lessons (below).

## 6. Text for D126 (replaces the bullet "Not changed: the text of the loads" and the paragraph "Naming the loads
directly changed the machine code")

Decision list, replacing "Not changed: the text of the loads":

> - **The loads name each field once.** A deinterleaving load writes each field straight into its name
>   (`out Vector512<float> __c1_0`); only a field of another type of the lane size keeps a temporary and its
>   reinterpretation (`As<int, float>`).

Why, replacing "Naming the loads directly changed the machine code":

> - **A copy per field kept masked jobs out of their runner.** The JIT inlines no method that declares more than 32
>   IL locals (`MAX_INL_LCLS`, reported as "too many locals"), `AggressiveInlining` or not, and then marks it noinline
>   for every caller. Loading into a temporary and copying it into the field's name (`out __s1_0; var __c1_0 =
>   __s1_0;`) declares two locals per field, which Roslyn keeps when the field is written later: `Move3`'s masked
>   `Execute` had 36, `Move4`'s 44, so `World.Masked` called them once per 64-row word. Without the copy they have 24
>   and 28 and are inlined; that is the 934 to 1,095 instructions and 36 to 54 permutes seen in `Move3`'s runner,
>   whose benchmark never runs a masked word (its speed was level). In the benchmark assembly 16 jobs fall under
>   the limit (25 over, 9 over after). With PGO off the masked loops already inlined gain one register move
>   (`vmovaps`) per written two-field component per vector: the register allocator gives a local one register for
>   both its definitions, the load and the select, so the last destructive `vpermt2ps` needs a copy; every other
>   listing of `Cycle`, `CycleHoles`, `BattleServer` and `WorldSimulation` is identical. Neither the `out` (no
>   stack slot in any loop) nor the IL size (590 / 638 bytes, the reason given is the locals) was the cause.
>   BDN, two rounds (base / head): `Move3GapsGenerated` (Move3 over a table whose every third row lacks the velocity)
>   100,000 rows 85.1, 86.0 / 80.5, 81.2 µs, 1,000 rows 695, 721 / 681, 713 ns; `Move4Generated` 1,000 rows 186.9,
>   185.4 / 170.2, 172.8 ns; `Move3`, `Move2Gaps`, `Push` level; `WorldSimulationBenchmark` and
>   `BattleServerBenchmark` within run-to-run spread (BattleServer six rounds each, 3.65 to 4.39 ms / 3.79 to
>   4.20 ms); 0 B. A test emits the generated jobs and fails on any `Execute` over 32 locals.

Considered, appended:

> - **The loads returning a value tuple or a struct of vectors** (`var (__c1_0, __c1_1) = Interleaved.Load2Of512(...)`):
>   as many locals as naming the outs (Roslyn deconstructs with `dup`), more IL, six new methods (a return type cannot
>   overload), and a 128- to 256-byte return through a hidden buffer. Reading a struct's fields in place would declare
>   one local per load, but every write becomes a field store and the whole loop text changes.

And in the masked-tail paragraph ("The Tier1 code says why: the job's `Execute` grew past the inliner's budget"):
on the current code the JIT's reason for not inlining `Cycle`'s masked `Execute` is its 33 IL locals ("too many
locals"), not its IL size; the sentence should name the locals (the tail experiment itself was not rerun).

The same correction in `.claude/skills/audit-vector-loops/SKILL.md`: "Copies in the text are free ... Removing them is
not neutral either: it changed which forms the JIT inlined" -> copies cost an IL local each, and the 32-local limit
is what decides inlining; "The job must stay inlined ... text that grows the body past the JIT's inlining budget" ->
"past 32 IL locals (MAX_INL_LCLS)", with the check being the local count of each `Execute` (the new test's method),
not only `call ...Execute` in the listing.

## 7. Follow-ups found on the way (not done here)

* **The 32-local limit is the inlining rule, not the IL budget.** `audit-vector-loops` ("The job must stay inlined")
  and D126's tail paragraph attribute the runner calling `Execute` to "the inliner's budget"; on the current code the
  JIT's reason is "too many locals" (CycleJob9 masked: 33 IL locals). Jobs still over 32 locals in the benchmark
  assembly with B: `CycleJob9` masked 33, `StepJob0` masked 34 (`Holes` / `Halves`: a call per masked word),
  `ShareJob8` 34 / 36, `GateJob26` 40 / 42, `SeekJob14` 40 / 42, `SeekParallelJob22` 40 (with the copy, 25 jobs were
  over: Fusion's Bounce jobs 37 / 39 and Move3/Move4 masked fall under it with B). The locals to cut next:
  both widths' blocks declare their own locals (`Vector512<T>` and `Vector<T>` are different types, so Roslyn cannot
  share slots), `__live = __pass`, the `__t` holds of whole-value writes, the deinterleaved fields a body never reads.
  WP0b (one method per width) halves the count; a `GeneratedTextTests` check can count locals per `Execute`.
* **Table CSE in big runners.** Once a masked body is inlined into `World.Masked`, the JIT reloads permutation tables a
  second Load of the same arity could reuse (Move3: 18 vs 12 table loads per vector).
* **LSRA ties a reassigned local to one register.** Writing a select into a new local instead of reassigning `__c`
  would remove the `vmovaps` but costs a local per written field; not worth it against the 32-local limit.
