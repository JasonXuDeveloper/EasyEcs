# Generated-text survey (tip febb35d)

Internal. The rules it led to are in `audit-generator`, "The generated text"; the work is in `backlog.md`. The scan
scripts and corpus it cites lived in a session scratchpad and are gone: `GeneratedTextTests` replaces them.

Read-only research. Nothing under src/, tests/, benchmarks/, samples/ or .claude/ was edited.

## Corpus and method

- Built `benchmarks/EasyEcs.Benchmark` (and through it `src/EasyEcs.Core`), `samples/VoxelServer`,
  `samples/MultiWorld` and `tests/EasyEcs.UnitTest` in Release with `-p:EmitCompilerGeneratedFiles=true` under the
  bench lock. **Caveat found on the way:** the `obj/.../generated` folders keep files of earlier builds; 30 of the 233
  SystemGenerator files of the benchmark project were stale (an old `partial class CombatSystem ... GeneratedUpdate`
  form). Only files written by these builds were kept (by mtime) and copied to `scratchpad/gt/corpus/{bench,core,voxel,
  multi,tests}`. Anyone counting generated text must do the same (or clean `obj` first).
- Counts come from `scratchpad/gt/scan.py` / `unused.py` (regexes over the corpus) and grep; "count" is occurrences
  in the benchmark project's output unless a row says "tests" (the unit-test project, which compiles the KernelTests /
  FusionTests / ReduceTests corpora and so has every vector form).

Fresh output sizes:

| Project | SystemGenerator | ComponentGenerator | Part / View / Prefab | `global::` names share of SystemGenerator text | Fusion files / of which copies of jobs |
|---|---|---|---|---|---|
| benchmarks | 203 files, 1,987,058 B | 283 files, 1,219,487 B | 14,246 / 2,035 / - | 51 % | 205,202 B / 156,200 B (76 %) |
| tests | 253 files, 2,951,735 B | 345 files, 1,687,794 B | 12,472 / 5,824 / 2,312 | 49 % | 177,124 B / 129,174 B (73 %) |
| VoxelServer | 38 files, 134,709 B | 40 files, 204,951 B | 3,975 / - / - | 53 % | none |
| MultiWorld | 5 files, 66,456 B | 5 files, 22,550 B | 716 / - / - | 47 % | 27,646 B / 22,900 B (83 %) |

ArityGenerator (EasyEcs.Core): World.Queries.g.cs 1,121,139 B, Entity.Structural 174,935, QueryItems 145,144, Filters
45,177, World.Lambdas 20,674. For scale: MultiWorld's three [ForEach] methods (about 30 lines in `Systems.cs`) become
66 KB of SystemGenerator text, 9.5 KB of it for the four-line `Move`.

Classes used below: **(a)** text only, the JIT certainly removes it (IL may still differ); **(b)** may change Tier1
(a job's IL locals: the JIT inlines no method of more than 32, D126/D135: removing the `__s -> __c` copies brought
Move3's masked `Execute` from 36 to 24 locals and into its runner, 934 to 1,095 instructions); **(c)** runtime cost.

Proof tiers:
- **P1, IL identical**: a change that touches only names, parentheses, aliases, whitespace or comments leaves the IL
  byte-identical (Release IL has no local names, an alias binds the same TypeRef/MemberRef). Proof: compare the
  method bodies of the two builds' assemblies (System.Reflection.Metadata or `ildasm` text minus the header). No
  Tier1 listing or BenchmarkDotNet needed; GeneratorBenchmark for the build-time win.
- **P2, IL changes**: Tier1 listings identical with `DOTNET_TieredPGO=0` (`tools/jitdisasm.sh` on the runners
  `EasyEcs.World:Masked` / ForEachBatch* of MultiWorld, BattleServer, KernelBenchmark Move3 / Cycle / CycleHoles,
  checking `call ...Execute` stays absent), then BenchmarkDotNet only if a listing differs.

---

## 1. The same loop body once per width (and per masked / unmasked form, and the tail)

| | |
|---|---|
| Count | 328 `if (Vector512.IsHardwareAccelerated...` blocks, 327 `if (Vector.IsHardwareAccelerated...` blocks, 6 VectorTail copies. Bytes inside the Vector512 blocks 418,709, inside the Vector<T> blocks 362,708: **38 % of SystemGenerator output is width blocks, 18 % is the second copy**. A VectorTail job (DeathSystem) carries the body 6 times: {512, Vector<T>, tail} x {unmasked, masked}. |
| Example | `Showcase.DeathSystem...System.g.cs`, `DieJob0.Execute`: lines 37-54 (512) and 55-72 (Vector<T>) differ only in `global::System.Runtime.Intrinsics.Vector512` vs `global::System.Numerics.Vector`; lines 73-95 (tail) repeat the body a third time; the masked `Execute` repeats all three. |
| Template | `SystemGenerator.cs:742-788` `EmitVectorLoop` (one text, `Kernel.V` token replaced twice at 784-786); `EmitFlatLoop` 820-838 (same trick at 835-836); `EmitTail` 796-813 (the body a third time with `Kernel.V` -> Vector); the pass loop over masked/unmasked at `SystemGenerator.cs:619-664`. The template is already single-source: the duplication exists only in the output. |
| Class | (a) for the width the machine lacks (constant `IsHardwareAccelerated`, removed at import). But note: on AVX-512 **both** blocks run (Vector512 for whole 16-lane vectors, then Vector<T> (256-bit) for 8..15 leftover rows), so the second block is live code there, not dead text. (b) for the IL: the locals of both blocks count towards the 32 a runner inlines before the JIT folds the dead one (D135). |
| Fix | Not removable by text tricks. Per-width methods (D135) are written when an `Execute` would pass 32 locals: each width's loop is its own method, so the dead width's locals no longer count; a generic width wrapper is the only form with one body. |
| Proof | P2 + BDN (WP0b's set). |

## 2. Fully qualified names everywhere (no file-level aliases)

| | |
|---|---|
| Count | 23,913 `global::` names in SystemGenerator output, **1,041,347 characters = 52 % of its bytes**. Vector names alone: 3,739 `global::System.Runtime.Intrinsics.Vector512` + 3,674 `global::System.Numerics.Vector` = 270,997 chars. `global::System.Runtime.CompilerServices.Unsafe` x1,005, `MethodImpl(...AggressiveInlining)` x481 in SystemGenerator and x2,169 in ComponentGenerator (~110 chars each), `UnsafeAccessor`/`UnsafeAccessorKind` x320. |
| Example | `global::EasyEcs.Interleaved.Load2(ref e1, (nuint)i, out global::System.Runtime.Intrinsics.Vector512<int> __s1_0, out global::System.Runtime.Intrinsics.Vector512<int> __s1_1);` |
| Template | constants `SystemGenerator.cs:843` (`Vector`, `Vector512`), `Kernel.cs:117` (`Interleaved`), `Kernel.Wide.cs:182-183` (`Permute2/1`), the attribute string written in three places: `SystemGenerator.cs:623`, `SystemGenerator.Fusion.cs:143`, `ComponentGenerator.cs:250` (itself a template-level duplication). |
| Class | (a), IL identical. |
| Fix | A file-level alias block before the namespace with reserved names, e.g. `using __V512 = global::System.Runtime.Intrinsics.Vector512;` (static class), one closed alias per lane type used (`using __V512i = global::System.Runtime.Intrinsics.Vector512<int>;`, C# 12 allows any closed type), `using __Unsafe = global::System.Runtime.CompilerServices.Unsafe;`, `using __Inline = global::System.Runtime.CompilerServices.MethodImplAttribute;` + `[__Inline((global::System.Runtime.CompilerServices.MethodImplOptions)256)]` or an options alias. Names must be reserved (`__` prefix): a plain `using System.Numerics;` is unsafe because a user type `Game.Vector` (or one in the global namespace) is found before compilation-unit usings and would silently rebind `Vector.Create`. The width token then maps to `__V512` / `__Vec` and `Vec(lane)` to the closed alias. User component types stay `global::`-qualified (an alias named after the type could be shadowed by a same-named type in an enclosing namespace). Simulated on the corpus (vector names, Unsafe, MemoryMarshal, UnsafeAccessor, Interleaved, the attribute): **SystemGenerator -20.0 %, ComponentGenerator -21.8 %** of bytes. D126 rejected the alias as "shortens the text, not the code"; the owner now asks for the text. |
| Proof | P1 (IL identical) + GeneratorBenchmark (output size, EditMethodBody). |

## 3. Identity copies

The copies that cost IL locals (each one counts towards the 32 a runner inlines) are gone (D126, D135); the rows
below that are not marked done are text, still to clean.

| Kind | Count | Example | Template | Class | Fix |
|---|---|---|---|---|---|
| deinterleave out-temp copied into the field's name `var __c1_0 = __s1_0;` | **done** (D126): each field is loaded into its name; the copy was two IL locals per field and put Move3's and Move4's masked jobs past the 32 locals a runner inlines | - | `Kernel.cs` loads | - | - |
| whole-value struct write held in `__t`: `var __t1 = __c1_0; __c2_0 = ConditionalSelect(__m0, __t1, __c2_0);` | **done** (D135): a lane name no write of the statement overwrites is used as is, and a lane written with its own value is not written; a value that is an expression, or a lane the statement overwrites (a swap), is still held | `ChangeTrackingBenchmark+ReplicateAll...System.g.cs:29` | `Kernel.Struct.cs` `WriteStruct` | - | - |
| `var __t0 = (__c1_0 + __c2_0); __c1_0 = __t0;` | (inside the 580 parenthesized RHS; same template) | `FusionBenchmark+Integrate...Fusion.g.cs:27-28` | same `WriteStruct` hold | (a)/(b) | as above |
| `var __live = __pass;` (masked forms), `var __live = AllBitsSet;` | **done** (D135): the guard's lanes are written in place (`Kernel.Live`): `AllBitsSet` or the active mask over whole vectors, `__pass` (and it) in a masked word | - | `Kernel.cs` `Live` | - | - |
| `var __c1_0 = __g8;` after a wide gather tree | in every wide job (KernelBenchmark Craft) | `KernelBenchmark+Kernels...:2526` region | `Kernel.Wide.cs:229-256` | (a)/(b) | gather straight into `__c1_0`. P2. |
| `var __w4 = AddSaturate(...); __c1_0 = __w4;` | narrow saturating idioms | `KernelBenchmark+Kernels...` Brighten | `Kernel.Narrow.cs:82, 380` | (a) | assign the idiom to the target. P2. |
| struct local copied lane by lane, then held again, then assigned: `var v = velocity.Value;` gives `var __l2 = __c2_0; var __l3 = __c2_1; var __l4 = __c2_2;` (`__l2`, `__l4` never read), then `var __t7 = __l3; ... __c2_1 = __t7;` - three names for one value | VoxelServer `PlayerMoveSystem` (`Walk`, samples/VoxelServer/Systems/Movement.cs:15-19), every struct local | `VoxelServer.PlayerMoveSystem...System.g.cs:57-66` | `Kernel.Struct.cs:271-282` (`DeclareStruct`: one `var __lN = lane` per lane, used or not) + the `WriteStruct` hold above | (a) | a struct local's lane is an alias of the lane value until either is written (map the local to the existing name, as `Alias` does for `Hold`), copy only on a write that would break the alias; never declare a lane nothing reads. P2. |

## 4. Per-width constants and setup re-created in each width block (and the tail)

| Kind | Count | Example | Template | Class | Fix |
|---|---|---|---|---|---|
| broadcast of a literal re-created per width and per tail | **done for loops over whole vectors** (D135, `SystemGenerator.Constants`): the literal is written at its use; the JIT hoists it the same and sees the value (`Max(x, -50f)` became one `vmaxps`). Kept as a setup local in a masked word's loop, a gathering loop and the tail: their bodies run under a `continue`, the JIT hoists nothing from a block not every iteration runs, and the inlined literal was broadcast again per vector (Gate's masked loop 49 to 52 instructions) | `var __u1 = ...Vector512.Create<int>((0));` | `SystemGenerator.Loops.cs` `Constants` | - | `.Zero` / `.One` for 0 and 1 is text only (P2) |
| scalar setup repeated per width (`ref readonly Rink __p2 = ref s2;`, `var __id1 = ScoreTotal.Identity;`) | 71 `ref readonly ... __pN = ref sN;` lines, 36 `__id` lines | FusionBenchmark Bounce, ReduceBenchmark Totals | `Kernel.cs:570-572` (uniform parameter setup), `Kernel.cs:1199` (reduction identity) | (a) | Split `Setup` into a scalar part (emitted once before the width blocks: refs, identities, scalar values) and a vector part (broadcasts). Scalars are width-independent. P2 (local order changes). |
| `this.v0 = default(Dead);` in each width block and tail | 6 (DeathSystem: 3 per Execute x 2) plus 6 non-default `this.v0 = value` | DeathSystem lines 40, 58, 80 | `Kernel.Structural.cs:216` (`_setup.Append($"this.v0 = {_change.Value};")`) | (a) for `default` (the field is already default in `new DieJob0 {...}`), (a)/(b) otherwise | A `default(T)` value needs no line at all (and the `SetAll(job0.v0)` call can pass `default`); a non-default value is computed once in the system's Update where the job is built (it is uniform: only job fields) or once at the top of Execute, not per width. P2. |
| duplicate pair broadcasts `var __a1_0_0 = Create<long>(__id1.Value); var __a1_0_1 = Create<long>(__id1.Value);` | 2 per pair reduction per width | ReduceBenchmark SumSerialJob1 | `Kernel.cs:1199` | (a) (CSE) | one broadcast, both halves start from it. P2. |
| duplicate wide permute tables (same values, two `static readonly` fields) | 10 of 31 tables in KernelBenchmark (`__x18 == __x25`, ... `__x23 == __x30`) | `KernelBenchmark+Kernels...` Craft job statics | `Kernel.Wide.cs:186-191` (`Table` never looks up an existing table) | (a)/(b): two RIP constants; the JIT may or may not merge them | memoize `Table` by its values (keep `_permutes++` for the cost model). P2 (listing should be identical or shrink). |

## 5. `var __d0 = Zero; ... __d0 |= (x);` where `__d0 = x` would do

| | |
|---|---|
| Count | 18; tests 72 (every vector loop with one structural change; all of them have exactly one `|=`). |
| Example | DeathSystem: `var __d0 = ...Vector512<int>.Zero; // the rows the change marks` ... `__d0 |= (__live & __alive2);` |
| Template | `Kernel.Structural.cs:102` (`Mark` appends `__d0 |= lanes`), `:214` (`_body.Insert(0, "var __d0 = Zero")`). |
| Class | (a): `Zero | x` folds. |
| Fix | Count the marks in `FinishChanges`: with one, emit `var __d0 = {lanes};` at the mark and no Zero line (Mark can record its lanes text instead of appending). Several marks keep the fold. P2. |

Related in the same jobs: `var __live = Vector512<int>.AllBitsSet;` then `(__live & __alive2)` (12; `Kernel.cs:689`): with
no early return `__live` is all lanes and `(__live & x)` is `x` (a, the JIT folds `& AllBitsSet` since .NET 9 - verify in
the listing). And `(__live & __alive2)` is written twice (for `__d0` and for `AddIds`): hold it once (a, CSE).

## 6. Redundant parentheses

| Kind | Count | Example | Template | Class | Fix |
|---|---|---|---|---|---|
| int/long literals wrapped `((0))`, `((10))`, `(-1)` | 266 `((N))`; tests 465 | `Create<int>((0))`, `Create<int>((10))` | `Kernel.cs:2073, 2075` (`Literal` wraps every int and long in parentheses; only negatives need it, before a unary or binary operator) | (a), IL identical | parenthesize negatives only. P1. |
| `unchecked((byte)(2))` | narrow broadcasts | KernelBenchmark Brighten | `Kernel.Narrow.cs:88, 102` | (a) | `(byte)2` for an in-range constant. P1. |
| whole right-hand side in parentheses `x = (a + (b * c));`, `var __m0 = (~...);` | 580 statements; tests 950 | `__c1_0 = (__c1_0 + (__c2_0 * __u0));` (Move3) | `Kernel.cs:1105, 1646, 1652, ...` (each operator printer wraps itself); `Kernel.Narrow.cs:123, 327, 363` | (a), IL identical | strip one balanced outer pair where an expression becomes a statement's right-hand side (`Write`, `Hold`, the `var x = ` emitters: one helper), leaving inner parentheses (a precedence-aware printer is the full fix, more work). P1. |

## 7. Opaque names

| Name | Means | Where | Proposed |
|---|---|---|---|
| `__c1_0`, `__c1_1` | lane vector of field 0/1 of job column 1 | `Kernel.cs:594-599` | `__hp_Value` (parameter + field path, `_0`/`_1` suffix only for pair halves); fall back to the number when two names collide (`a_b`.`c` vs `a`.`b_c`) |
| `__s1_0` | deinterleave temp | `Kernel.cs:597` | `__hp_Value_load` or keep with a comment (see 3) |
| `e1`, `r1`, `c1` | lane ref, row ref, span of column 1 | `SystemGenerator.cs:654, 744` | `hp_lanes`, `hp_rows`, the span keeps `c1` (it is the interface parameter) |
| `__u1`, `__p2`, `__a1_0`, `__q1_0`, `__has1`, `__m0`, `__t3`, `__w2`, `__x9`, `__n0`, `__g8` | broadcast, singleton ref, accumulator, optional lane, presence mask, condition, temp, narrow temp, permute table, permute node, gather | `Kernel.cs:887` (`Temp(prefix)`), `Kernel.Wide.cs:188, 229, 256` | broadcasts after their value (`__zero`, `__dt`, `__rink_Size`), conditions after the source text where short (`__alive`, as `_alive` already does at `Kernel.cs:968`), accumulators after the reduction field (`__total_Value`) |
| `d0`, `v0` (job fields) | marked rows, value to set | `SystemGenerator.cs:590-592` | `marked`, `value` (job fields cannot collide with users) |
| `j0`, `j1`, `Tick` in fused jobs; `Run_Run_Fused0` | the two jobs | `SystemGenerator.Fusion.cs` | `integrate`, `bounce` (system names) |

Class (a), IL identical (local names live in the PDB only; job field renames change metadata names, not code). Proof P1
(+ the doc pages that show generated text must be refreshed). A unit test pins uniqueness.

## 8. Dead and unused lines

| Kind | Count | Example | Template | Class | Fix | Proof |
|---|---|---|---|---|---|---|
| a deinterleaved field the body never reads (`var __c1_1 = __s1_1;` unused) | 10; tests 72 | DeathSystem (Health.Value only) | `Kernel.cs:597-599`; the `columns` filter at 691-694 drops unused columns, not unused fields | (a) (the `out` must stay: Load2 writes both) | skip the copy for fields no body/store names; or discard (`out _`) | P2 |
| unused broadcasts (`__u0`, `__u3` in Brighten) and dead narrow temps (`var __w2 = (__c1_0 + __w1);` superseded by the saturating idiom) | 4 + 2; tests 44 + 24 | `KernelBenchmark+Kernels...:2630-2638` | `Kernel.Narrow.cs:82, 380`; `Kernel.cs:1588` | (a) | emit setup/temps lazily (record and only print what the final body names, as `columns` already does) | P2 |
| a pair condition narrowed into a mask nothing reads: `var __m1 = Vector512.Narrow(__t3, __t4);` (the selects use the halves `__t3`, `__t4`) | tests 20 | `PairKernels...ForEach.g.cs` | `Kernel.Pairs.cs:228-229` (the narrowed mask is always written, its halves recorded in `_maskHalves`) | (a) | emit the narrowed mask lazily, only when a whole-width use asks for it | P2 |
| a masked word holds every accumulator when one field is a pair: `var __h1_0 = __a1_0;` unused (only the pair's `__h1_1_0/_1` are restored) | tests 10 | `ReduceTests+SerialScores...System.g.cs` | `Kernel.cs:688-692` (`maskedHold` takes all of `held`) | (a) | hold only the pair fields in a masked word without bails | P2 |
| `Position = default; Tag = default;` in a prefab's generated constructor | per [Prefab] field without initializer (tests: 2 files) | `Bestiary+Goblin...Prefab.g.cs` | `PrefabGenerator.cs:57` | (a): C# 11 auto-defaults struct fields; the generator already requires C# 14 | drop the lines | P1/P2 |
| empty `Create` / `Destroy` hooks | 171 + 171 (one pair per system); tests 203 + 204; VoxelServer 37 + 37 | `__CombatSystem_SystemCode.Create(...) { }` | `SystemGenerator.cs:430-435` | (a) text; (c) negligible: `__EasyEcsSystems.Create/Destroy` switch over all 171 systems calling empty methods | `ISystemCode` gets `static virtual void Create/Destroy(...) { }` defaults (core change: `SystemCode.cs:20,23`), the generator emits a hook only when the system has one, and the set's `Create`/`Destroy` switches list only systems with that hook (`SystemGenerator.cs:172-178`; the set sees the hooks of closed generics too) | P2 (the set's dispatch changes; one listing of `World.Create`/system creation), trivially safe |
| `__EasyEcsSystems` switches with 171 cases each for Update/Create/Destroy | 513 case lines | `case 0: global::EasyEcs.Benchmark.__AccessBenchmark__EntityHas_SystemCode.Create(world, handle); return;` | `SystemGenerator.cs:172-178` | (a) | as above for Create/Destroy | P1/P2 |
| `ids = ref Unsafe.Add(ref ids, 0)` (a step of 0 left in the loop) | 18 in World.Queries.g.cs | `for (...; ids = ref Unsafe.Add(ref ids, 0), r1 = ref ...)` | `ArityGenerator.cs:1002-1008` (`Lean` rewrites the step to 0 instead of deleting it) | (a) | delete the step (regex the `ids = ref Unsafe.Add(ref ids, 1), ` away); these are NoInlining roots, no inliner budget involved | P2 (one listing) |
| `unused object __owner` parameter of lambda copies, called with `null` | 1 per lambda pass | `internal static void Lambda_0(object __owner, ref Health h, ...)` | `SystemGenerator.Lambdas.cs:172, 179` | (a) | drop it (it mirrors the UnsafeAccessor signature) | P2 |
| `rows - i >= ...` with `i` just set to 0 | 440 checks, half of them in the first block where `i` is 0 | `if (Vector512.IsHardwareAccelerated && rows - i >= Vector512<int>.Count)` | `SystemGenerator.cs:754, 828` | (a) | write `rows >= Count` for the first block only (the second needs `- i`) | P2 (IL changes) |
| `Vector512.As<int, float>(__pass)` re-written per masked write | 106 occurrences, 52 extra in-block repeats | Move3 masked: three `ConditionalSelect(Vector512.As<int, float>(__pass), ...)` | `Kernel.cs:638, 1119-1125` (`Write`) (`MaskAs`/`PassAs` per write) | (a) (reinterpret, free) | hold `var __passF = As<int,float>(__pass)` once per vector | P2 |

## 9. Repeated per-system / per-job / per-component boilerplate a shared helper could carry

| Kind | Size | Example | Template | Class | Fix |
|---|---|---|---|---|---|
| **Fusion files re-emit both systems' full jobs** (file-local `__ForEach`), identical to the jobs in each system's own file | benchmarks: 11 Fusion files, 205,202 bytes, of which **156,200 (76 %) are copies** of jobs already emitted (tests 73 %, MultiWorld 83 %: its one Fusion file, 27.6 KB, is the largest generated file of the sample); `FusionBenchmark+Integrate...Fusion.g.cs` lines 11-104 diff-identical to the job in `FusionBenchmark+Integrate...System.g.cs` | `RunJob0` in both files | `SystemGenerator.Fusion.cs:462-466` (`EmitAccessor` + `EmitJob` per method of both systems inside a new `file static class __ForEach`) | (a) text/build time; the JIT compiles each copy as its own type (two instantiations of the runner: some JIT time, code size) | Make the per-system job class reachable (`internal static class __Integrate_ForEach`, or nested in `__Integrate_SystemCode`) and have the fused job hold `__Integrate_ForEach.RunJob0` / `__Bounce_ForEach.RunJob0`. Watch: job indices are per system (the fused file renumbers them), generics need the closed owner. P2 (the fused runner's listing) + GeneratorBenchmark. |
| reduction fold loop after each width loop (and in the tail) | 36 fold loops | `for (int __j = 0; __j < Vector512<int>.Count; __j++) { var __v = default(ScoreTotal); __v.Value = ...; l1.Combine(__v); }` | `Kernel.cs:663-686` | (a) | one generic helper in Core per lane shape (`Lanes.Fold<T, TLane>(Vector512<TLane> acc, ref Reduce<T> into, field setter)` is not expressible without a delegate; a helper per one-field reduction `Reduce.FoldLanes(ref l1, __a1_0)` is, when T has one field and an `IReduction` that takes a lane) | P2 + BDN ReduceBenchmark |
| `MethodImpl(AggressiveInlining)` on every one-line extension member | 2,169 attributes in ComponentGenerator (~240 KB of 1.2 MB) | `[global::System.Runtime.CompilerServices.MethodImpl(...AggressiveInlining)] get => @this.Has<Account>();` | `ComponentGenerator.cs:250` | (a) if kept; (b) if removed (bodies are under the always-inline IL size, but measure) | keep, shorten through the alias (kind 2) |
| doc comments repeated per arity / per component | World.Queries 220,842 doc bytes; World.Lambdas 19,165 of 20,674 bytes are docs; ComponentGenerator 415,435 doc bytes of 1.2 MB | World.Lambdas: the same 12-line summary on ForEach<T1>..<T9> | `ArityGenerator.cs` templates; `ComponentGenerator.cs` | (a) | `<inheritdoc cref="...{TValue1}"/>` for arities >= 2 (needs tools/ApiDocs + the zh entries to follow inheritdoc) - low priority, these are what IntelliSense shows |
| `__loops` string array and `Access` per system, `WriteMask`/`OneQuery` per job | per job | | | (a), necessary | none: the values differ per job and the interface defaults differ (WriteMask default -1, OneQuery false) |
| View `TryGetX`: `Has<A>() && Has<B>()` then `Get<A>()`, `Read<B>()` | per [View] | `ViewBenchmark__MoverEcsExtensions.TryGetMover` | `ViewGenerator.cs` | (c) small: two lookups per component | one `TryGet`-by-ref per component if Core has it; else note only |

## 10. Layout noise in ArityGenerator output

- Mis-indented lines from interpolating multi-line helpers at a fixed indent: 270 lines like
  `                ref T1 r1 = ref Unsafe.Add(ref archetype.Base<T1>(b1), start);` at 16 spaces in an 8-space body,
  stamp lines one level deeper than their neighbors, blank lines where `IndexedRows` is empty (`ArityGenerator.cs:1043`
  `startAt`, `StampRows`/`StampWord` at 995-1000, the `{{(n >= 3 ? IndexedRows(...) : "")}}` slot at ~1313). (a), P1.
- The same explanatory `//` comment per arity (World.Queries: 104,040 bytes of `//` comments): keep in the template,
  print once (on arity 1) - (a), P1.

## Source-level options (removing the duplication at the source)

1. **One emitter parameterized by width** - already done (`Kernel.V` token, `EmitVectorLoop` replaces it). It removes
   template duplication, not output duplication. Nothing left to win here.
2. **File-level `using` alias block** - the single biggest text win (-20 % of SystemGenerator and ComponentGenerator
   bytes), IL identical, so provable with P1 and no benchmarks. Needs reserved `__` names and one closed alias per
   (width, lane type) a file uses (the generator knows them: `simd.Lane`, `simd.Mask`, the struct/pair lanes); the
   width token then maps to `__V512` / `__Vec`, which also makes the two width copies visibly the same body.
   **Promising, do first.**
3. **Hoisting width-independent setup above the width blocks** - scalars (singleton refs, `Identity`, `this.v0`)
   once per Execute; vector broadcasts cannot be hoisted (their type is per width) but literal 0/1 become `.Zero`/`.One`
   and other literals are inlined in the loops over whole vectors (D135; not in masked loops, see section 4).
   Scalars once per Execute: **promising, small.**
4. **Per-width methods** - shipped (D135) for an `Execute` past 32 locals: `Execute` calls `__Loop512(...)` /
   `__Loop(...)`; the text is still per width. Neutral for text.
5. **A generic static-abstract width wrapper** (`interface IWidth<TSelf> { static abstract ... }`, `struct W512 :
   IWidth`, body written once as `Loop<W>(...)`): one body in the output. Costs: every vector op the kernel emits
   (arithmetic, compares, ConditionalSelect, Load/Store, Interleaved.LoadN, Widen, Sqrt, permutes...) needs a
   wrapper method that must inline at Tier1 (dozens of methods; `ISimdVector<TSelf,T>` is internal in .NET 10);
   operators on the vector type itself are not available through the abstraction, so the body becomes method calls
   (`W.Add(a, W.Mul(b, c))`), less readable than today's `a + b * c`. Wide/permutes are AVX-512-only anyway. **Not
   promising now**; revisit when .NET exposes `ISimdVector`.
6. **Reusing per-system jobs in fusion files** (kind 9): removes 76 % of fusion output with no change to any job's
   code. **Promising.**
7. **Default interface implementations for empty hooks** (kind 8): removes 342 empty methods and two thirds of the
   set's switch text. **Promising, trivial.**

## Guards against regressions

Existing harness: `GeneratorTests.Compile(...)` + `GeneratorTests.Driver()` (`tests/EasyEcs.UnitTest/GeneratorTests.cs:23-31`)
run all generators over sources; `IsCode(tree)` picks `.System.g.cs` / `.ForEach.g.cs`; `KernelTests` and
`FusionTests`/`LambdaPassTests` already hold a corpus of every vector form. A guard test can collect the generated
trees of those corpora (one helper: run `Driver().RunGeneratorsAndUpdateCompilation` over the sources each test class
already uses) and assert over the text and the semantic model:

| Guard | How | Reliable | Cost |
|---|---|---|---|
| Unused locals | Roslyn data flow, not warnings: for each method body of a generated tree, `SemanticModel.AnalyzeDataFlow(body)`; a local in `VariablesDeclared` that is not in `ReadInside` fails (allow-list the `out` discards). CS0168/CS0219 do not catch it (CS0219 fires only for constant initializers; IDE0059 is an IDE analyzer not in the test references). | yes, no false positives | low (one data-flow pass per method of the corpus) |
| Identity copies | syntax: `var A = B;` where B is a local and neither A nor B is assigned afterwards in the scope (data flow `WrittenInside` of the rest of the block). The loads no longer copy (D126), so nothing needs an allow-list. | yes | low |
| `((literal))` and whole-RHS parentheses | syntax: `ParenthesizedExpressionSyntax` whose parent is a `ParenthesizedExpressionSyntax`, an `EqualsValueClause`, an `ArgumentSyntax` or an assignment's right side (Roslyn knows exactly when parentheses are needed: `SyntaxFacts`/simplifier rules; a simple version flags parenthesized literals and identifiers and parens directly under `=`) | yes for these shapes | trivial |
| Constant created inside a width block | syntax: `Create<T>(literal)` / `Create<T>((0))` anywhere, and any `var __x = ...Create(...)` whose argument is a compile-time constant (`SemanticModel.GetConstantValue`) | yes | trivial |
| `= ...Zero;` then a single `|=` | data flow: a local initialized to `.Zero` and written exactly once more, by a compound `|=` | yes | low |
| Duplicate static tables / broadcasts | text: identical initializers among `static readonly` fields of one type, or among `var` locals of one block | yes | trivial |
| Fusion copies | a job type name present in two generated files of one compilation (after the fix the fused file must name the system's job) | yes | trivial |
| Empty methods | syntax: method with an empty block body in generated code, except interface requirements without default | yes | trivial |
| Unqualified BCL names / missing aliases | text: no `global::System.Runtime.Intrinsics.Vector512` outside the alias block | yes, once aliases exist | trivial |
| Size budgets | per-form byte budget for a fixed corpus (e.g. the KernelTests corpus total, DeathSystem's file) checked within +-5 %; fails when a change adds text, the author updates the budget consciously | medium: brittle but explicit; better as a reported number in GeneratorBenchmark than a hard fail | trivial |
| IL equality for text-only refactors | not a unit test: a script in tools/ that builds base and head and diffs method-body IL of the benchmark assembly | yes | one build each; only when a change claims P1 |

The cheapest reliable set: unused locals + identity copies (data flow), the parenthesis/constant syntax checks, and
the no-empty-method check, all in one `GeneratedTextTests` over the existing corpora. Size budgets as a printed number
(GeneratorBenchmark already measures time; adding output bytes there is one line) rather than a failing test.
The `audit-generator` skill's "The generated text" checklist should point to the guard test and to the obj staleness
caveat.

## Ranked recommendation

1. **File-level alias block** (kind 2) with reserved names: -20 % text, IL identical (P1). Also collapses the three
   copies of the AggressiveInlining string in the templates into one constant.
2. **Fusion files reference the systems' jobs** (kind 9): -156 KB of 205 KB fusion output in the benchmarks, -129 KB
   in the tests, -23 KB of MultiWorld's 66 KB; fewer job types for the JIT. P2 on the fused runner.
3. **Pure printer fixes, IL identical (P1)**: literal parentheses (266), outer RHS parentheses (580), names after
   parameters and fields (kind 7), Arity indentation. Ship together with the alias block; prove by IL diff.
4. **Empty hooks** (342 methods, 2/3 of the set's switches): `static virtual` defaults in `ISystemCode`.
5. **Small IL-changing cleanups, one P2 round together**: `__d0` single mark, `.Zero`/`.One`, scalar
   setup and `this.v0` hoisted once, deduplicated
   wide tables and pair broadcasts, struct locals as aliases (VoxelServer `Walk`), dead narrow temps, unused
   broadcasts, unused pair masks and holds, the Arity `ids, 0` step. Gate: Tier1
   listings of Move3, Cycle, CycleHoles, WorldSimulation, BattleServer, MultiWorld runners identical (PGO off), else
   BDN; D126 shows a "free" text change can move the inliner.
6. The `__s -> __c` copy, the `__t` holds of plain names, `__live` and the literal broadcasts of loops over whole
   vectors are gone (D126, D135).
7. **A generic width wrapper** is not worth it in .NET 10.
8. Guards: one `GeneratedTextTests` class (data-flow unused locals and identity copies, syntax checks), output bytes in
   GeneratorBenchmark.

## Coverage

Read in full or sampled: SystemGenerator (System, ForEach, Fusion, Lambdas files, `__EasyEcsSystems`),
ComponentGenerator (components, buffers, QueryItem, TypeRegistry), PartGenerator, ViewGenerator, PrefabGenerator (tests:
`Bestiary+Goblin...Prefab.g.cs`), ArityGenerator (all five files), over the benchmark, VoxelServer, MultiWorld and
unit-test builds. Not done: Tier1 listings (no change was made to compare, and benchmarks were off limits); the
"class" column is therefore a judgment from the D126 / audit-vector-loops lessons, not a measurement, except where it
cites D126.
