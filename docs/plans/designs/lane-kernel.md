# One body per job over a width type: the lane kernel (package C, ledger L28, L29)

The study of backlog items j, k, k2, k3, l and l2 (feat-generated-text-2 package C). It answers whether the vector
loops the generator writes can be one body per job, written once over a width-generic lane type the JIT specializes,
with the width chain (512 -> 256 -> 128 -> the method per row), the tails and the masked words held once in Core. It
was a prototype on three jobs, hand-written as the generator would write them. The owner chose the rollout (L33):
flat jobs first (package D, below, "Package D: flat jobs"), the walk form and the reduction researched further
(package F), today's 128-bit path fixed apart (package E).

Code:

- `src/EasyEcs.Core/VectorLoops/LaneKernel.cs` (shipped, hidden): the typed form of item l2, `ILane<TSelf, T>` with
  `Lane512<T>` / `Lane256<T>` / `Lane128<T>`, and `LaneKernel` over `ILaneStep4` (the study called it LaneKernel4).
- `benchmarks/EasyEcs.Benchmark/VectorLoops/LaneKernelStudy.cs`: the study's other forms, which Core does not ship: the
  width form (`Lane<W, T>` over W512 / W256 / W128, one type parameter for every element type; its kernel
  `WidthKernel`) and the typed walk form (`ILaneWalk4`, `LaneWalk4`).
- `benchmarks/EasyEcs.Benchmark/VectorLoops/LaneKernelBenchmark.cs`: the three jobs in each form, against today's
  generated jobs. Its setup checks every job bit for bit.

## The form

```csharp
// Core: a vector of T at one width, a struct type argument; each operation a static abstract member
public interface ILane<TSelf, T> where TSelf : struct, ILane<TSelf, T> where T : unmanaged
{
    static abstract int Count { get; }
    static abstract TSelf Load(ref T at, nint i);
    static abstract TSelf operator +(TSelf a, TSelf b);   // - * & ~, Min, Max, GreaterThan, Equal, Select, Sum, ...
    static abstract TTo As<TTo, U>(TSelf a) where TTo : struct, ILane<TTo, U> where U : unmanaged;
}
public struct Lane512<T> : ILane<Lane512<T>, T> { public Vector512<T> V; ... }   // Lane256<T>, Lane128<T> alike

// the job: its body once, over the float and int lanes of a width (one type parameter per lane size it uses)
internal ref struct IntegrateJob : IForEachMasked<Heat, Speed>, ILaneStep4
{
    public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2) { ...refs...; LaneKernel.Run(ref this, c1.Length); }
    public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2) { ...; LaneKernel.RunMasked(ref this, pass, c1.Length); }

    public void Step<LF, LI, M>(nint i, LI pass, LF passF) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
    {
        ref float e1 = ref Unsafe.As<Heat, float>(ref r1);
        var c1 = LF.Load(ref e1, i);
        var c2 = LF.Load(ref Unsafe.As<Speed, float>(ref r2), i);
        c1 = LF.Keep<M>(passF, c1 + c2 * LF.Create(0.033333335f), c1);
        LF.Store(c1, ref e1, i);
    }

    public void Row(nint i) => Kernels.Integrate(ref Unsafe.Add(ref r1, i), in Unsafe.Add(ref r2, i));
}
```

The kernel in Core (`LaneKernel.Run`) does three things:

- **Picks the widths.** It takes 512 bits where `Vector512.IsHardwareAccelerated` (never `Avx512F.IsSupported`, item
  k3), then 256, then 128. Each width takes the whole vectors the wider one left, and the method runs on the rows left
  after that.
- **Handles masked words.** A masked word (`RunMasked`) runs every width with `M = PassingRows`: each vector's passing
  lanes, a vector with none passing skipped, and the rows left tested bit by bit.
- **Writes masked or whole.** `Keep<M>` is the write: a select by the passing lanes in a masked word, the value itself
  in a whole run (`typeof(M)`, folded by the JIT).

A reduction or a job with uniforms walks a width itself (`ILaneWalk4.Walk<LF, LI, M>`). Its accumulators or broadcasts
are declared before its loop and folded after it, as today's text does. The kernel still owns the chain and the tails.

The width form (`Lane<W, T>`, one type parameter `W` for every element type) gave the same machine code as the typed
form in every listing compared (Step at 512 bits). The typed form is the one the owner named; it reads better
(`LF.Max(...)` against `Lane<W, float>.Max(...)`). The width form's single type parameter is its one advantage, for
jobs of many lane sizes.

## What the Tier1 listings show (FullOpts, `DOTNET_JitDisasm`, tools/perf LoopBound)

The listings are in `$S/t1c` (width form), `$S/t1e` and `$S/t1f` (typed form).

**Own instantiation per width.** Each width compiles on its own, for example
`LaneKernel:Walks[WalkJobs+StepJob, Lane512<float>, Lane512<int>, EveryRow]` and its Lane256 twin. No call is shared
across widths, and no generic virtual dispatch appears. The job is a struct type argument with `allows ref struct`,
and every `LF.Op` is a constrained call on a struct, which the JIT resolves.

**Direct and inlined, with one condition.** Inlined into the runner, one root held the runner, three widths and two
modes. The JIT's inlining budget is per root method, and it ran out: the operations of the third width were left as
calls (`Lane<W128,float>.op_Multiply`, `Create`, `Load`; 42 calls in the first listing). That first form measured
1.5-4x slower (r1).

Each width's walk is therefore a method of its own (`NoInlining`), called once per run. Each walk is then its own root,
and every step inlines to the vector code with no call left. The runner inlines `Execute` and the chain, and
`tools/perf/inlining.sh LaneKernelBenchmark KernelBenchmark` exits 0. Package D's A/B measured what a call per width
costs on small tables and moved the three widths into one walk per run, the job passed by value ("Package D", "The
A/B and the fix").

**Hoisting.** Through the caller's `ref` to the job, every store to a column might write the job, so the column refs
and a uniform were reloaded every vector. A copy of the job in the walk fixes the refs: they load into registers once.
A uniform broadcast (`Create(s3.Value)`) still sat inside the loop in the step form (`vbroadcastss zmm5, zmm0` per
vector, never hoisted). Only a walk that broadcasts it before its loop, as today's text does, hoists it.

**What a lane struct costs the JIT.** Today's text calls `Vector512.Min(x, Vector512.Create(10f))` and keeps a mask in
a plain SIMD local. Through a lane struct, the JIT imports the inlined `Min` with a field, not a constant, and keeps a
mask in a promoted struct field.

| Code | Today's text | Through a lane struct |
|---|---|---|
| Min / Max against a constant (cannot be NaN) | `vminps` / `vmaxps` | `vrangeps` + 2 `vfixupimmps` each (the IEEE sequence) |
| a select under a comparison | `vcmpgtps k1` + `vblendmps zmm {k1}` | `vpmovm2d` + `vpternlogd` (the mask as a vector) |

Members that put the constant or the comparison in the call itself recover both in the prototype:
`Min(a, T constant)`, `Max(a, T constant)`, `SelectGreaterThan(x, y, a, b)`, `SelectEqual(...)`. The general case
stays a JIT limitation: a mask kept in a local and combined with others (`__m0 & __pass`, several branches). One gap is
left even with those members: three constant broadcasts per vector stay inside the walk's loop, where today's text has
them hoisted into registers.

## BenchmarkDotNet

LaneKernelBenchmark, one round (r2; r1 was the first width form), each ratio against IntegrateGenerated. Means in ns:

| rows | Integrate gen / typed | Step gen / typed / walk | Holes (masked) gen / typed / walk | Tally gen / typed |
|---|---|---|---|---|
| 37 | 19.9 / 23.5 | 28.8 / 28.3 / 24.7 | 16.3 / 22.4 / 20.0 | 113 / 109 |
| 1,000 | 68.0 / 67.0 | 142 / 267 / 190 | 112 / 193 / 134 | 176 / 169 |
| 100,000 | 10,195 / 10,238 | 20,276 / 30,574 / 16,227 | 14,430 / 17,884 / 15,218 | 6,984 / 9,235 |

What the rows say:

- **Integrate (flat float): level** at every size. The typed form's text is 36 lines against 79.
- **Step (a condition, a uniform):**
  - The step form is slower everywhere past 37 rows (1.5-1.9x), for the reasons above.
  - The walk form with the fused members is faster at 100,000 rows (0.80x) and slower at 1,000 (1.33x).
  - Holes, its masked words, run 1.2x at 1,000 and 1.05x at 100,000.
- **Tally (a reduction):** level to faster at 37 and 1,000 rows, 1.32x slower at 100,000 rows.
  - Its loop is the generated one's with one extra column-ref reload and a rotated loop (two branches per vector).
- **Allocation:** every row is 0 B.

This is one round on a shared machine (load 10-30). The coordinator's two-round run (below) is the verdict.

## Correctness and platforms (item k2, k3)

`$S/lkcheck` runs the benchmark's setup at 1, 3, 4, 5, 15, 16, 17, 37, 63, 64, 65, 1,000 and 100,000 rows. Each kernel
job matches today's generated job bit for bit, columns and totals, under every setting the machine reproduces:

| Setting | Widths the runtime accelerates | Chain |
|---|---|---|
| default (this Xeon: AVX-512) | 512, 256, 128 | 512 -> 256 -> 128 -> row |
| `DOTNET_PreferredVectorBitWidth=256` (Skylake-SP's default, item k3) | 256, 128 | starts at 256 |
| `DOTNET_EnableAVX=0` (SSE only) | 128 | 128 -> row |
| `DOTNET_EnableHWIntrinsic=0` (arm32's case: RyuJIT accelerates no width there) | none | the method per row |

Per platform:

- **x86 (32-bit).** Not tested: .NET ships no Linux x86 runtime, so it needs Windows. The kernel has nothing width-
  or pointer-size-specific beyond `nint`, which is 4 bytes there. A Windows x86 CI job would test it, and whether to add
  one is the owner's question, as item k2 says.
- **arm64.** No hardware here. The 128-bit step is AdvSimd, and Lane256 / Lane512 are never taken because the runtime
  says so.
- **arm32.** The `DOTNET_EnableHWIntrinsic=0` row is that path.

## Generated bytes (the three jobs, hand-written in the generator's style)

| Job | Today's text | Typed form | Change |
|---|---|---|---|
| Integrate | 79 lines, 4,233 bytes | 36 lines, 1,520 bytes | -64 % |
| Step | 132 lines, 8,090 bytes | 50 lines, 2,401 bytes (walk) | -70 % |
| Tally | 127 lines, 7,510 bytes | 61 lines, 2,727 bytes | -64 % |

The generator is unchanged, so GeneratorBenchmark Cold / Edit are unchanged by construction. A rollout would change
the Kernel's emission (one body instead of two to four), which writes less text and likely costs less; it is measured
when it is built.

## Side by side: Integrate

Today (79 lines; the masked Execute repeats the flat one with `__pass`):

```csharp
public void Execute(__EntitySpan entities, __Span_Heat c1, __Span_Speed c2)
{
    ... refs ...
    nint i = 0;
    if (__V512.IsHardwareAccelerated && rows >= __V512_float.Count)
    {
        for (nint last = rows - __V512_float.Count; i <= last; i += __V512_float.Count)
        {
            var __c1_0 = __V512.LoadUnsafe(ref e1, (nuint)i);
            var __c2_0 = __V512.LoadUnsafe(ref e2, (nuint)i);
            __c1_0 = __c1_0 + __c2_0 * __V512.Create<float>(0.033333335f);
            __V512.StoreUnsafe(__c1_0, ref e1, (nuint)i);
        }
    }
    if (__Vec.IsHardwareAccelerated && rows - i >= __Vec_float.Count)
    {
        ... the same loop over __Vec ...
    }
    for (; i < rows; i++) __ForEach.Integrate_2(null, ref __Unsafe.Add(ref r1, i), in __Unsafe.Add(ref r2, i));
}
public void Execute(__EntitySpan entities, ulong pass, __Span_Heat c1, __Span_Speed c2)
{
    ... the same two loops with __pass = Lanes32In512(pass, i), skip, ConditionalSelect by __pass ...
}
```

The typed form (36 lines; the chain, the masked words and the tail are Core's):

```csharp
public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2) { r1 = ...; r2 = ...; LaneKernel.Run(ref this, c1.Length); }
public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2) { r1 = ...; r2 = ...; LaneKernel.RunMasked(ref this, pass, c1.Length); }
public void Step<LF, LI, M>(nint i, LI pass, LF passF) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
{
    ref float e1 = ref Unsafe.As<Heat, float>(ref r1);
    var c1 = LF.Load(ref e1, i);
    var c2 = LF.Load(ref Unsafe.As<Speed, float>(ref r2), i);
    c1 = LF.Keep<M>(passF, c1 + c2 * LF.Create(0.033333335f), c1);
    LF.Store(c1, ref e1, i);
}
public void Row(nint i) => Kernels.Integrate(ref Unsafe.Add(ref r1, i), in Unsafe.Add(ref r2, i));
```

## Which job shapes keep their emitted loops, and why

These shapes keep today's emitted loops:

- **Masks held across statements.** Masks kept in locals, combined, and used by several writes: an if / else chain,
  switches, `& __pass` mixes. Through a lane struct a mask is a vector, not a mask register (the JIT limitation above).
  Only a select under a single comparison is recovered.
- **Wide components (Kernel.Wide).** AVX-512 permute trees against NEON TBL lines: each width's text differs by
  design, so there is no single body.
- **Element loops, cells, pairs, streaming stores and lookups' gathers.** They have drivers of their own (a row's
  buffer, a tile's rows, clusters, a non-temporal path, gathers behind a per-table check). A per-width body helps only
  inside them, a later step.
- **Jobs past one lane size** (byte lanes with int masks, long sums of ints). These need one lane type parameter per
  size: `ILaneStep4` carries float and int; a general `Step<L1, L2, L4, L8, LF, LD>` is mechanical but unbuilt.

The candidates are flat jobs (Integrate-like) and jobs whose conditions are a single comparison per write, in the walk
form with the fused members. Reductions are level only at small sizes in this round.

## Recommendation

The form works where the owner asked: one body per job, an instantiation per width, every call direct and inlined
once each width is its own method, the 512 -> 256 -> 128 -> row chain and its tails in Core, and the same results bit
for bit on every width setting. It also removes 64-70 % of the text.

It is not yet "nothing slower": Step at 1,000 rows, its masked words and Tally at 100,000 rows read slower. The causes
are named above:

- the JIT's import-time forms and mask registers are lost through a lane struct;
- constants are not hoisted in the walk.

Recommended: do not roll out across the generator now. If the owner wants a step:

1. Ship the kernel for the flat shape only (Integrate-like jobs, level), with the walk form for the rest kept as the
   study's prototype.
2. Re-measure the walk form on a quiet machine. The coordinator's two rounds and both start widths decide whether
   Step / Tally are noise or real.
3. Raise the mask-register gap with the runtime: a promoted SIMD field defined by a comparison could stay `TYP_MASK`.
   That would close the remaining gap for every shape.

## Package D: flat jobs (owner L33, branch feat-lane-kernel)

**The shape** (`SystemGenerator.LaneStepOf`, read from `Kernel.Code` alone; src/EasyEcs.Generator/AGENTS.md, "The lane
kernel's shape"): 4-byte float or int lanes; the row loop of one-field components, or a flat loop over several fields
alike (`Code.Flat`, walked as `rows * fields` lanes by `RunFlat`); straight-line lane arithmetic (loads and stores at
`i`, `+ - * / & | ^ ~`, unary `-`, locals, literals); constants as the only uniforms (written in place); no bail,
reduction, structural change, record, vector tail, gather, draw, prefetch, streamed column, wide component, statics,
pair mode or cold branch; at most 32 IL locals in the step. Masked words take the same step when their text is the
whole run's with each write as `Keep<M>(pass, value, old)`; a flat loop of several fields keeps its deinterleaving
masked loops.

**What the generator writes** (KernelBenchmark's Integrate):

```csharp
public void Execute(__EntitySpan entities, __Span_Heat c1, __Span_Speed c2)
{
    ref __Heat r1 = ref __MemoryMarshal.GetReference(c1);
    ref __Speed r2 = ref __MemoryMarshal.GetReference(c2);
    nint rows = c1.Length;
    var __lanes = new __Lanes { e1 = ref __Unsafe.As<__Heat, float>(ref r1), e2 = ref __Unsafe.As<__Speed, float>(ref r2) };
    nint i = __LaneKernel.Run(ref __lanes, rows);
    for (; i < rows; i++) // the rows after the last whole vector: the method itself
        __ForEach.Integrate_2(null, ref __Unsafe.Add(ref r1, i), in __Unsafe.Add(ref r2, i));
}
// the masked Execute: the same with __LaneKernel.RunMasked(ref __lanes, pass, rows) and the bit test in the tail

private ref struct __Lanes : __ILaneStep4
{
    public ref float e1;
    public ref float e2;

    [__MethodImpl(__MethodImplOptions.AggressiveInlining)]
    void __ILaneStep4.Step<LF, LI, M>(nint i, LI pass, LF passF)
    {
        var __c1_0 = LF.Load(ref e1, i);
        var __c2_0 = LF.Load(ref e2, i);
        __c1_0 = LF.Keep<M>(passF, __c1_0 + __c2_0 * LF.Create(0.033333335f), __c1_0);
        LF.Store(__c1_0, ref e1, i);
    }
}
```

The step lives in a struct of the column refs, not in the job: a parallel job is a struct (no ref fields), and the
tail keeps Execute's call of the method with every argument it already writes. The explicit implementation needs no
constraint clauses. Tier1 of `LaneKernel:Walk[IntegrateJob2+__Lanes, EveryRow]` (one walk per run, the three widths in it): the column
refs loaded once, each width's loop `vmovups; vmovups; vmulps {1to16} (the constant as an embedded broadcast); vaddps;
vmovups`, no call; `tools/perf/inlining.sh` over KernelBenchmark, the headline worlds, FusionBenchmark,
LookupGatherBenchmark, LaneKernelBenchmark and VectorFormsBenchmark: every generated job inlined.

**Where it applies.** The benchmark project: 24 of 260 vector jobs (30 Executes: 6 Run, 18 RunFlat, 6 RunMasked):
KernelBenchmark's Integrate, Move2, Push, Move3, Move4; BattleServer's Move; FusionBenchmark's Movement and
Integrate jobs; Trim's Drift, CrowdPairs' steps, Regeneration, a lambda pass of ViewBenchmark. The rest have a
condition, a uniform (a singleton's dt), a reduction, interleaved fields or 8-byte lanes. WorldSimulation's Move
has conditions: it keeps its loops.

**Generated text.** Integrate's job 80 lines / 4,304 bytes -> 47 / 2,372 (-45 %; the study's 36 lines left out the
two Execute preambles and tails the job keeps); BattleServer's Move job (its masked words deinterleave, so they keep
their loops) 83 / 4,883 -> 76 / 4,295. The benchmark project's generated files 4,522,249 -> 4,502,057 bytes
(77,486 -> 77,209 lines); the GeneratedTextTests corpus 11,532,139 -> 11,524,667: few jobs of either are flat.

**Proof.** LaneKernelTests (five flat jobs against SimdMode.Off, every length to 70, 255, 1,000, 1,021, with and
without masked words) and KernelTests, InlineArrayTests, LambdaPassTests, ViewTests, ElementLoopTests,
FusionTests, BattleServerTests bit for bit in the default mode and under `DOTNET_PreferredVectorBitWidth=256`,
`DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0` (one failure there, TheScheduleNamesEachMethodsLoop, is the
schedule's "; rows on this hardware" under no SIMD at all, on base too). The full suite on net10.0 and net11.0; the
AOT check (BattleServer's Move is a kernel job). A/B: `scratchpad/kernelD/ab.sh` (base 89090767 against af168ac8,
two rounds; KernelBenchmark's flat cases, LaneKernelBenchmark, VectorFormsBenchmark, the headline worlds,
GeneratorBenchmark Cold and Edit); the numbers go here and into docs-todos `feat-lane-kernel`.

**The A/B and the fix.** The first A/B (`scratchpad/kernelD/ab.sh`, base 89090767 against af168ac8, two rounds,
the AVX-512 machine, two other A/Bs interleaved on it) said hold: controls level (median 0.995 / 0.997), five rows
slower in both rounds. A focused rerun of those rows (`scratchpad/kernelD2/focus.sh`, three launches, two rounds) and
the generated text and Tier1 listings of base and head (`scratchpad/kernelD2/asm`) sorted them:

| Row | head / base (A/B r1, r2; focused r1, r2) | Verdict |
|---|---|---|
| FragmentsIntegrateGenerated (both Entities: the same 1,000 tables of 5-20 rows) | 1.25 1.17, 1.10 1.02; 1.14 1.15, 1.00 1.11 | real, about 1.12x |
| FragmentsMove2Generated / Move3Generated | 0.98-1.16 / 0.75-1.13; 0.92-1.03 / 0.94-1.01 | level |
| Move2Generated 1,000 (Move3, Move4 alike) | 1.06 1.07; 1.12 1.04 | small, the same cause |
| VectorForms Pow | 1.06 1.11; 1.00 0.97 | noise: PowJob's text is base's but for the usings |
| GeneratorBenchmark EditUnrelatedFile NS=False | 1.11 1.21; 1.04 1.06 | not the change's code (below) |

- **The cause (Fragments, small runs).** Head's runner (`World:Masked[..., IntegrateJob2]`, Tier1) calls a
  NoInlining walk per width: a table of 12-15 rows takes the 256 and the 128 walk, two calls where base's loops are
  inline. With the job passed as a `ref` to a local of the runner, the runner also keeps it in memory around each call.
  The walks themselves are the tight loops of the study (no call, refs in registers).
- **What fixed it.** `FragmentStudy` (`scratchpad/kernelD2/varS`, one process, Integrate on Fragments / one table of
  1,000 / of 100,000 rows, two rounds, ratios against the emitted loops): a walk per width 1.18 / 0.99 on Fragments;
  one walk per run through a ref 1.13 / 1.05; **one walk per run with the job by value 1.02 / 0.93** (1,000 rows
  0.96 / 0.89, 100,000 rows 1.04 / 0.96); the walks inlined 0.94 / 1.01; the emitted loops plus a 128-bit loop 0.83 /
  0.82 (the 4-7 rows past the 256-bit vectors as one vector instead of the method per row: why the kernel's 128 width
  pays on small tables). Core's `LaneKernel` now computes the three ends in the inlined caller and calls one
  `Walk<TJob, M>(TJob copy, e512, e256, e128, pass)` per run, none for a run shorter than a 128-bit vector.
- **Why not inline the walks.** With `AggressiveInlining` walks the runner holds six step copies (three widths, two
  modes), and a flat step of six statements over seven components (`scratchpad/kernelD2/varD2/stress`, in the shape)
  left `Lane512.op_Addition`, `op_Multiply`, `Store`, `Keep` as calls in `ForEachBatchRuns`: the per-root inlining
  budget again. The walk per run held the same step with no lane call left (`varA2/stress.asm`, `lanecalls.py`).
- **EditUnrelatedFile.** `LaneStepOf` and `EmitLanes` run only when a job's text is emitted, and every output is
  cached on that edit: the step runs base's code, allocates the same to 10 KB (84.14 MB both), and NS=True is level
  (0.95-1.03). Its StdDev is 35-110 ms on 440 ms.

The A/B of the fix (a08825cf against 89090767, two rounds): `scratchpad/kernelD2/ab/ab.sh`, its numbers to be added
here before the merge.

**Next shapes** (package F, each only once level at 0 B): uniforms (the walk form, broadcasts before its loop), a
select under one comparison and Min / Max against constants (the fused members), 8-byte lanes (`ILaneStep8`),
reductions.

## Runs left for the coordinator (Sonnet)

- **Script:** `$S/lkbdn.sh <tag>` (`$S` = `/tmp/claude-0/-workspace/1ad27225-4a0d-50e5-acbc-5950e4df5375/scratchpad`).
  It runs `*LaneKernelBenchmark*` from the worktree into `$S/lk/<tag>`.
- **Runs:**
  - two rounds as is (tags `q1`, `q2`);
  - one round with `DOTNET_PreferredVectorBitWidth=256` exported (tag `w256`): the kernel starts at 256 bits, and
    today's text takes Vector512 only where the runtime says, then `Vector<T>`;
  - one round with `DOTNET_EnableAVX=0` (tag `sse`).
- **Sides:** each run holds both sides in one class: the `*Generated` rows are today's text, and the `*Typed` /
  `*Walk` / `*Kernel` rows are the kernel forms.
