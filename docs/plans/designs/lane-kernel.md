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

**What the generator writes** (KernelBenchmark's Integrate; package F, below, replaced the step by a walk of the same
body, with the same machine code):

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

### Masked words: the walks inlined into the runner (HolesHaul)

**What the first A/B showed.** `$S/kernelF/ab.sh` (base a5e0e905): HolesHaulGenerated 1.21x at 1,000 rows and
1.31x at 100,000 (both rounds; HolesHaulScalar faster in the same runs), 1.16x at 1,000 under `DOTNET_EnableAVX=0`.
HolesHaul is the first flat job measured over masked words (package D's benchmarks had none: LaneKernelBenchmark's
Holes is Step, a condition), so the cause is the kernel's masked mode itself, uniforms or not.

**The cause** (FullOpts, `$S/kernelF2/asm/holes-head.asm` against `holes-base.asm`). `_holes` lacks Mass on every
third row, so every 64-row word is masked and goes to the job's `Execute(pass, ...)` from `World.MaskedRuns`, with
the rows from its first passing row to its last (about 63). The emitted loops run inline there: three 512-bit
vectors, one 256-bit, the passing rows after. The kernel made a call per word: `RunMasked` computed the three ends
with branches, spilled five values of the word loop (`[rbp-0x48]`, `-0x4C`, `-0x70`, `-0xC0`, `-0xC8`), stored the
job's three refs and uniform as stack arguments, called `LaneKernel:Walk[HaulJob3+__Lanes, PassingRows]` (prologue,
the uniform broadcast at each width, `vzeroupper`) and reloaded them, about thirty instructions around five vectors of
about ten each. The walk's own loops are the emitted ones (`vpternlogd` for the kept lanes, the same masks).

**The fix.** `RunMasked` inlines the three walks (`Widths<TJob, PassingRows>` over a copy of the job): the runner's
walk over words is a `NoInlining` root of its own that holds nothing else of the job (whole runs go to `MaskedRun`,
where the job's whole-run `Execute` calls the walk once per run), the same root the emitted text's two widths of
masked loops are inlined into. The whole-run walk keeps its call: a run is long and D's Fragments numbers chose it.
The inlining budget: `$S/kernelF2/stressM` (package D's stress world, a flat step of six statements over seven
components, its rows with masked words) checked by `lanecalls.py` for lane operations left as calls.

### Reductions: the conditional sum's masked add (Tally)

**What the A/B showed.** LaneKernelBenchmark's TallyTyped (the lane types) and TallyKernel (the width form) at
100,000 rows: 8,700-10,100 ns against TallyGenerated's 6,600-7,100 (1.3-1.45x); level at 37 and 1,000.

**The cause** (Tier1, `$S/kernelE/r-def.asm`: `TypedJobs+TallyJob:Walk[Lane512...]` against `World:ForEachBatchRuns
[..., TallyJob3]`). No column ref is reloaded and the loop is not rotated in today's listings (the study's first
listing predates the fused members); the two loops differ in the sum under a condition. The emitted text's
`ConditionalSelect(__m0, __a1_1 + __c1_0, __a1_1)` is one tree, and the JIT contracts it into a masked add,
`vptestnmd k1; vpaddd zmm1 {k1}, zmm5, zmm1`. The lane form's `SelectEqual(c1 & 1, 0, a1s + c1, a1s)` reaches the
select with the sum as an argument's temporary (the inliner spills every argument that is not a local or a constant),
so the JIT blends: `vpaddd zmm7, zmm5, zmm1; vpblendmd zmm1 {k1}, zmm1, zmm7`, one more uop per vector on AVX-512's two
vector ports and a loop-carried chain of two operations instead of one. llvm-mca: 2.57 cycles per vector against 3.08
(icelake-server), the 1.2x the listing explains of the 1.3-1.45x measured. The repro (`$S/kernelF2/rx`, FullOpts,
`rx1.asm` / `rx.asm`): the mask as a local then `Select` keeps the mask as a vector (`vpmovm2d; vpternlogd`), the add
inside the select's member with the mask from `Equal` likewise, the select as `a + (c & m)` an extra `vpmovm2d; vpandd`;
only the comparison, the add and the select in one member give the masked add.

**The fix.** `ILane.AddWhereEqual(acc, value, x, y)`: `ConditionalSelect(Equals(x, y), acc + value, acc)` in one
expression, at every width. Its 512-bit loop is the emitted one instruction for instruction (`vpaddd zmm1 {k1}`), its
256- and 128-bit loops too (`vpcmpeqd; vpaddd; vpblendvb`, as the emitted `Vector<T>` loop). The study's Tally
takes it, and its walks run as Core's whole runs do (`StudyWalk4`: one `NoInlining` call per run holding the three
widths, the job's copy written back). The generator maps a conditional sum to it when reductions join the kernel.

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

**Next shapes** (package F, below, each only once level at 0 B): uniforms (the walk form, broadcasts before its
loop: form 1), a select under one comparison and Min / Max against constants (the fused members), 8-byte lanes
(`ILaneWalk8`), reductions.

## Package F: the walk form (branch feat-lane-walk, on feat-lane-kernel 05f91b47)

One form per package, each shipped only once level or a win at 0 B (GeneratorBenchmark Cold / Edit included) and bit
for bit under the four width settings. Listings: `$S/kernelF/` (`exp/` the hand-written reproduction,
`haul-base.asm` / `haul-head.asm` KernelBenchmark's Haul / HolesHaul / Integrate in FullOpts, `fall*.asm` the NaN
investigation below).

### Form 1: uniforms (a singleton's value, a parameter's)

**The cause of the in-loop broadcast.** The step form's uniform was a float field of the job's copy; the step
broadcast it (`LF.Create(u)`) every vector. Reproduced by hand (`$S/kernelF/exp/Program.cs`, FullOpts, `a.asm` /
`b.asm`): with one multiply-add in the step (14 inlinees) the JIT hoists the broadcast out of the loop; with the
study's flat arithmetic plus a uniform (33 inlinees) it leaves `vbroadcastss zmm5, zmm0` inside the loop, the scalar
load hoisted but the broadcast not. That is RyuJIT's loop hoisting declining a cheap tree under its register-pressure
estimate (`optIsProfitableToHoistTree`: once the loop's count of live floating-point / SIMD locals reaches the
registers it assumes available, a tree cheaper than `2 * IND_COST_EX` is not hoisted), and every inlined lane-struct
operation adds argument and return temporaries to that count. Today's emitted text never depends on hoisting: it
declares each broadcast in its setup, before the loop. The release JIT has no JitDump to print the decision; the
evidence is the pair of listings and the walk form's below.

**The fix: the job's walk.** The job's step became a walk (`ILaneWalk4.Walk<LF, LI, M>(nint i, nint end, ulong
word)`): the generator writes the loop in the job, its uniforms broadcast before it (`var __u0 = LF.Create(this.__u0);`),
and Core's `LaneKernel` inlines it once per width: a whole run's three walks in package D's one `NoInlining`
`Walk<TJob>(TJob copy, e512, e256, e128)` per run (the branch was rebased onto it from the per-width walks it was
first written on), a masked word's into the runner (below).
Execute reads each uniform's scalar into the walk's struct (`__u0 = s3.Value`, through the setup's `ref readonly`
alias of a singleton). One form for every lane job: a flat job without uniforms compiles to the step form's machine
code (`haul-*.asm`, Integrate's walks: the same instructions, inlinee counts aside), so the kernel keeps one entry
chain and no second interface.

```csharp
private ref struct __Lanes : __ILaneWalk4
{
    public ref float e1;
    public ref float e2;
    public ref float e3;
    public float __u0;

    [__MethodImpl(__MethodImplOptions.AggressiveInlining)]
    void __ILaneWalk4.Walk<LF, LI, M>(nint i, nint end, ulong word)
    {
        var __u0 = LF.Create(this.__u0);
        for (; i < end; i += LF.Count)
        {
            if (!__LaneKernel.Lanes<LI, M>(word, i, out var pass)) continue;
            var passF = LI.As<LF, float>(pass);
            var __c1_0 = LF.Load(ref e1, i);
            var __c2_0 = LF.Load(ref e2, i);
            var __c3_0 = LF.Load(ref e3, i);
            __c1_0 = LF.Keep<M>(passF, __c1_0 + (__c2_0 - __c3_0 * __u0) * LF.Create(0.033333335f), __c1_0);
            LF.Store(__c1_0, ref e1, i);
        }
    }
}
```

FullOpts of Haul's 512-bit walk (`haul-head.asm`): `vmovss xmm0, [rdi+0x18]; vbroadcastss zmm0, zmm0` before the
loop, the loop `vmovups x3; vmulps zmm4, zmm4, zmm0; vsubps; vmulps {1to16}; vaddps; vmovups`, no call, the same
instructions as today's loop in the runner (`haul-base.asm`). Its masked walk matches today's masked loop too, both
with the passing lanes as a vector (`vpcmpeqd k1; vpmovm2d; vptestmd`); one difference is left there: the passing
bits' constant (`Lanes32In512`'s 1, 2, 4, ...) is loaded inside the walk's loop (`vmovups zmm1, [RWD00]`), where
today's loop holds it in a register and copies it (`vmovaps`) per vector: the same hoisting limit, for a later form.

**Where it applies.** The benchmark project: 29 lane jobs against 24 (+ KernelBenchmark's Haul and MoveVector,
ViewBenchmark's Motion ByRefs / ByView, AccessBenchmark's SingletonParameter); the generated files of SystemGenerator
2,970,422 -> 2,968,845 bytes (52,338 -> 52,377 lines: each lane job's loop is now in its text, the uniform jobs lose
their per-width loops); Haul's job 89 lines / 5,098 bytes -> 56 / 3,028.

**NaN.** Bit for bit except which NaN two NaN operands give: LaneKernelTests with a NaN uniform failed at first
(`fall*.asm`): the SimdMode.Off method at Tier0 (`LaneKernelRows:Fall`) computes `r.Value += x` as
`vaddss xmm0(x), [r]`, the JIT swapping the operands of +, so with r and x both NaN the scalar method keeps x's NaN
and the vector walk r's. The scalar method's own compiles differ there (Tier0 against Tier1), so the tests compare
every NaN as one, as KernelTests.Same does; the old loops matched only by the luck of their operand order.

**Proof.** LaneKernelTests (Fall, Pace, Sink: float and int uniforms, a flat one, a singleton changed each update to
0.75, NaN, infinity; masked words) and the vector-loop classes (LaneKernel, Kernel, InlineArray, LambdaPass, View,
ElementLoop, Fusion, BattleServer, SwitchLoop, HelperLoop tests) under the default, `DOTNET_PreferredVectorBitWidth=256`
and `DOTNET_EnableAVX=0`, and under `DOTNET_EnableHWIntrinsic=0` all but TheScheduleNamesEachMethodsLoop (base fails
it there too); the full suite on net10.0 and net11.0 (AReductionFoldsEachFieldOnItsOwn and
ASwitchIsMasksOfItsArms fail on base a5e0e905 already: package E's Interleaved.Select changed the text they read;
ViewTests.AViewMethodsPlainArgumentKeepsFlatLanes read the flat loop's text and now reads the walk's too, rechecked
with GeneratorTests by `$S/kernelF/recheck.sh` into `$S/kernelF/RECHECKDONE`);
`tools/perf/inlining.sh` over KernelBenchmark, the headline worlds, FusionBenchmark, LookupGatherBenchmark,
LaneKernelBenchmark, VectorFormsBenchmark, ViewBenchmark and AccessBenchmark: every job inlined. After the rebase,
the masked words' fix and AddWhereEqual: `$S/kernelF2/gate.sh` (the same gate and LaneKernelTests'
AddWhereEqualIsTheSelectOfTheSum, into `gate.log`) and the A/B `$S/kernelF2/ab.sh` (base a08825cf with head's three
benchmark files and AddWhereEqual, which the study needs; two rounds of KernelBenchmark's Haul / HolesHaul /
MoveVector / Integrate rows, LaneKernelBenchmark, BattleServer and WorldSimulation, then two rounds of the
KernelBenchmark rows and LaneKernelBenchmark under `DOTNET_EnableAVX=0`). The first A/B:
`$S/kernelF/ab.sh` (base a5e0e905 with head's three benchmark files against 7d2c676c, two rounds under the default
of KernelBenchmark's Haul / HolesHaul / MoveVector / Integrate rows, LaneKernelBenchmark, ViewBenchmark, AccessBenchmark's
singleton rows, WorldSimulation, BattleServer, GeneratorBenchmark Cold / Edit; one round of the KernelBenchmark rows
under `DOTNET_EnableAVX=0`). Ships only when level or a win at 0 B with nothing slower.

### Constants: Haul under SSE4.1 (after the merge of D and E)

**What the A/B showed.** Three interleaved rounds against D (`$S/rerunDF/F_sse.txt`): KernelBenchmark's HaulGenerated
under `DOTNET_EnableAVX=0` at 1,000 rows 1.059-1.121x the emitted loop in every round (base spread 1.8 %).

**The cause** (Tier1, `$S/haulF/head-walk-sse.asm` against `base-runs.asm`). The two loops are the same eleven
instructions but one: the emitted loop multiplies by the constant `Vector<float>.Create(Dt)` held in a register
(`movddup xmm1` hoisted, `mulps xmm3, xmm1`), the walk by the constant in memory (`mulps xmm3, xmmword ptr [RWD16]`),
while the `movddup xmm1` the loop hoisting wrote before its loop is left unused. Both texts write the constant in the
loop; what keeps it in a register is the JIT's CSE taking the hoisted copy, and in the walk it did not, so lowering
contained the constant as the multiply's memory operand. A fourth load per vector where three bound the loop:
llvm-mca 2.1 -> 2.4 cycles a vector on icelake-server, 2.1 -> 2.6 on znver4 (`tools/perf/loopbound.py --reg xmm`).
At AVX-512 the constant is an embedded broadcast (`{1to16}`) in both forms, a load too, so only SSE showed it.

**The fix** (`SystemGenerator.StepText` / `WalkText`): each constant vector of a step (`LF.Create(0.033333335f)`,
`LI.Create(1)`) is a local `__kN` of the walk, declared once before its loop like the uniforms, never written in the
loop. Haul's SSE walk is then the emitted loop's instructions (`mulps xmm3, xmm1`, the constant in a register); at
AVX-512 the JIT propagates the local into its use and keeps the embedded broadcast, the same loop as before. Every
flat lane walk of KernelBenchmark and LaneKernelBenchmark is as short or shorter by llvm-mca (Integrate's 512-bit walk
2.6 -> 2.3 cycles a vector on icelake-server, its SSE walk 2.6 -> 2.1). Constants of Min / Max, when those join the
walk, must stay in place (above).

**Proof.** GeneratorTests.ALaneWalksConstantsAreBroadcastBeforeItsLoop; the full suite on net10.0 and net11.0 (4,486
passed each, `$S/haulF/gate.log`); the vector-loop classes under `DOTNET_PreferredVectorBitWidth=256`,
`DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0`; `tools/perf/inlining.sh` over the nine benchmark classes:
every job inlined. The A/B (`$S/haulF/ab.sh`, base = claude/relaxed-ramanujan-j4j9k4 775d7ef4 with head's three benchmark
files and `AddWhereEqual`, head = this branch; three interleaved rounds of KernelBenchmark's Haul / HolesHaul rows and
LaneKernelBenchmark, under `DOTNET_EnableAVX=0` and the default; `$S/haulF/ab.txt`): every row level at 0 B. Under
SSE4.1: HaulGenerated 1,000 131.5 / 132.6 / 130.7 ns -> 138.3 / 135.1 / 136.8 (+5.2 / +1.9 / +4.7 %, was +10.5 / +5.9 /
+12.1); HolesHaulGenerated 1,000 +5.4 / -1.4 / +0.1 %; IntegrateGenerated faster at every size (37: -10 / -8 / -9 %,
1,000: -6 / -10 / -7 %, 100,000: -27 / -23 / -24 %); the 100,000-row rows move with their scalar twins (HaulScalar
+5.0 / -5.5 / +12.3 %, spread 8-24 %). Default: every row level, HaulGenerated 1,000 -0.4 / +3.5 / +4.5 % (spread 4.1),
HolesHaulGenerated 100,000 +16.6 / +1.5 / -4.9 % (spread 13.3).

**What is left at 1,000 rows: the call per run.** Haul's walk is still the one `NoInlining` `LaneKernel.Walk` per
run (package D's form), its job of three column refs and a float (28 bytes) passed in memory (SysV passes a struct
of more than 16 bytes on the stack: four stores in the runner, four loads in the walk, the call, its frame): about
20 cycles a run, 2-5 % of a 130 ns run. Integrate's job (two refs, 16 bytes) goes in registers. Measured
(`$S/haulF/ab2.sh`, `ab2.txt`, two rounds): `RunFlat` inlining the widths into the runner instead of calling `Walk`
takes HaulGenerated 1,000 to 130.8 / 131.0 ns under SSE4.1 (the emitted loop's 131) and 70.0 / 68.9 at AVX-512
(head 71.9 / 72.5), the other rows level. It does not ship as is: inlined into every runner, the stress world's walk
(`$S/kernelF2/stressM`, six statements over seven components) leaves 11 lane operations as calls at AVX-512 and 14
under SSE4.1 (the runner's inlining budget, package D's reason for the call). A walk small enough to inline
(Haul's, Integrate's) needs a size rule the generator can read (lane operations times the runner's three call sites
and three widths) and its own A/B at the rule's edge: below.

### The call per run: small walks inlined (branch feat-lane-walk-inline)

**The form.** Core's `LaneKernel.RunInlined` / `RunFlatInlined` inline a whole run's three widths into the caller
(the job's copy as a local, so its refs stay in registers; no broadcast for a run under one vector); `Run` / `RunFlat`
keep the `NoInlining` `Walk`. The generator picks the inlined entry when `InlinesWalk` holds: at most 16 lane
operations (`LaneOperations`: each `LF.` / `LI.` member of the step text, a masked step's `Keep` included, each
operator, each constant and uniform broadcast before the loop) over at most 6 components (Execute's spans).

**Why the edge is there.** `ForEachBatchRuns` calls Execute at three sites (filtered runs, a dense table, runs past
tombstones), so an inlined walk lands nine times at AVX-512 (three widths a site), six at 256 bits, three under
SSE4.1, and each lane operation brings its argument and return temporaries. The runtime's inlining events
(`MethodJitInliningFailed`, the sweep's listener) name the limits: "too many locals" (the root reaching the 512 locals
the JIT inlines into one method) and "inline exceeds budget". Past them the runner leaves lane operations, the walk of a
width or the scalar tail (`Run_0`, the method for the rows after the vectors) as calls. Each component adds its column
ref, span and stamp at each site, so the edge falls with the components.

**The sweep** (`$S/walkI`: `gen/sweep.py` writes one system per method, `ref A a` plus 0 to 8 `in` components, 1 to
7 terms `x.Value * k` in statements of four, fusion off, parallel twins of 3 and 9 components; `gen/matrix.sh` builds
it twice, base = every walk called, exp = every walk inlined, and runs each at Tier1 (`DOTNET_TC_CallCountingDelayMs=0`,
3,200 updates with pauses for the background compiles) and FullOpts (`DOTNET_TieredCompilation=0`) under the default
(AVX-512), `DOTNET_PreferredVectorBitWidth=256` and `DOTNET_EnableAVX=0`, `DOTNET_JitDisasm` of every runner;
`gen/diff.py` / `gen/sum.py` list each call the exp runner holds that base's does not; `out/g1-*.sum`). The first
lane operation or walk left as a call, in lane operations:

| components | Tier1 AVX-512 | Tier1 256 bits | FullOpts AVX-512 |
|---|---|---|---|
| 1 | 24 | none to 27 | 27 (the runner's own span helpers from 21) |
| 2 | 25 | none to 28 | 25 |
| 3 | 23 | none to 29 | 23 |
| 4 | 24 | none to 30 | 24 |
| 5 | 21 | 31 | 21 |
| 6 | 21 | 29 | 21 |
| 7 | 17 (the scalar tail from 9) | 26 (the tail from 9) | 17 (the tail from 9) |
| 8 | 17 (the tail at 9) | 26 | 17 (the tail from 9) |
| 9 | 17 (the tail from 9) | 21 (the tail at 13) | 13 (the tail from 9) |

Under SSE4.1 (one width a site) no lane operation stays a call up to 34 operations; seven components push the tail out
from 17 at Tier1, seven to nine from 17 at FullOpts. At FullOpts most walks also leave one site's tail a call at any
size (below).

The rule takes the edge of 5 and 6 components (21) less a margin of five operations, and leaves 7 to 9 components to
the call, where even a walk of 9 operations pushed the scalar tail out of the runner. Rerun with the rule
(`out/g2-*.sum`): every Tier1 runner at every width holds exactly base's calls less the walk's (parallel chunks
included; a few listing differences between runs of identical code are Tier1's timing).

**FullOpts keeps one call.** Without a profile (`DOTNET_TieredCompilation=0`, the inlining tool, an AOT-like compile)
most walks within the rule make one site's scalar tail a call (the user's method, "unprofitable inline", at every
width; `out/g2-full-*.sum`): the inliner's profitability falls with the root's locals, and
`DOTNET_JitMaxLocalsToTrack=0x800` removes the call from every case but one (`out/lt.txt`). It costs a call per row
for the 0-3 rows after the vectors of a run, where the inlined walk saves a call per run; at Tier1 the profile keeps
the tail inlined. No lane operation stays a call at FullOpts within the rule (`tools/perf/inlining.sh`, which now
reports lane operations and walks left as calls, over KernelBenchmark, the headline worlds, FusionBenchmark,
LookupGatherBenchmark, LaneKernelBenchmark, VectorFormsBenchmark, ViewBenchmark and AccessBenchmark: every job
inlined).

**Where it applies.** Every lane job of the benchmark project is within the rule (4 to 11 operations over 1 to 3
components: Haul, Integrate, Move, the flat MoveVector / Move2-4 / Push, the views' and lambdas'), 11 in the generator
test corpus. The stress world's Mix (`$S/kernelF2/stressM`, seven components) keeps the call. BattleServer's Move
(Tier1, `gen/bsd.sh`, `out/bs-*.asm`): its `ForEachBatchRuns`, `Masked` and `MaskedRun` hold base's calls less the
three, two and one `LaneKernel:Walk` calls; nothing else of the tick's code changes.

**A/B** (`$S/walkI/ab/ab.sh`, base 62218aef, head = base + the Core and generator change; three interleaved rounds
of KernelBenchmark's Haul / HolesHaul / Fragments rows and LaneKernelBenchmark under `DOTNET_EnableAVX=0`, the same
plus BattleServer and WorldSimulation under the default; `ab/ab.txt`). SSE4.1: all 58 rows level at 0 B;
HaulGenerated 1,000 135.5 / 135.7 / 134.8 -> 131.9 / 130.1 / 132.7 ns (-2.7 / -4.1 / -1.6 %, the emitted loop's 131:
package F's last gap closed), IntegrateGenerated 37 -5.6 / -6.5 / -2.9 %. Default: 60 rows level at 0 B, HaulGenerated
1,000 -1.2 / +0.2 / -0.3 %; one row past the bar, BattleServer Tick +4.3 / +4.1 / +4.8 % (base spread 2.4). Its only
changed code is Move's runner losing its calls, a pass of about 1 % of the tick, so it was rerun alone
(`ab/bs.sh`, six interleaved rounds of base, head and a control with head's Core and base's text): head -4.1 / +8.7 /
-4.3 / -1.9 / +3.1 / +27.8 % (base 2.36-2.90 ms, spread 23 %), the control -2.6 / -1.9 / -1.2 / -16.7 / +16.1 %
(`ab/bs.txt`): level, the round-to-round swing audit-perf records for BattleServer. Fragments' rows swing 20-100 %
between rounds on both sides (their scalar twins too). Ships: level or faster everywhere at 0 B, Haul's gap closed.

**Proof.** GeneratorTests.ARunnerInlinesOnlyASmallWalk (16 operations inlined, 17 called; 6 components inlined, 7
called); the full suite on net10.0 and net11.0 (4,492 passed each, `$S/walkI/gate.log`); the vector-loop classes under
`DOTNET_PreferredVectorBitWidth=256`, `DOTNET_EnableAVX=0` and `DOTNET_EnableHWIntrinsic=0`; `tools/perf/inlining.sh`
over the ten benchmark classes: every job inlined, no lane operation left as a call.

### The forms left, and what form 1 showed about them

- **A select under one comparison, Min / Max against constants.** The fused members (`SelectGreaterThan`, `Min(a, T
  constant)`) recover the mask register and the cheap Min / Max in the study. In the walk they need StepText to map a
  mask local used once (`var __m6 = GreaterThan(...)`, then `ConditionalSelect(__m6, ...)`) into the fused call, and
  `Min(x, Create(10f))` into `Min(x, 10f)`. A constant kept as a broadcast local before the loop would reach `Min` as
  a lane struct's field (the IEEE `vrangeps` sequence again), so the constants of Min / Max stay in place; their
  `vbroadcastss` from memory inside the loop (the study's walk listing) is the hoisting limit above, cheap loads.
- **8-byte lanes (`ILaneWalk8`: double, long).** Core needs the walk interface over `LD` / `LL` and the chain at 8, 4,
  2 lanes per width; StepText needs `Size: 8` and the double / long names. Uniforms come for free with form 1.
- **Reductions.** The walk owns its accumulators (declared before its loop, as form 1's broadcasts) and folds them
  after it; Core's walk must then write the copy back (`job = copy`, the study's `StudyWalk4`). A sum under a condition
  goes through `AddWhereEqual` (above: through `SelectEqual` the add is blended, not masked); StepText maps
  `ConditionalSelect(m, a + b, a)` of a mask used once to it, and a reduction under `GreaterThan` needs its own member.
- **The passing bits' constant of a masked walk** (above): hoisted by declaring it before the walk's loop
  (`LI.PassingBits` and a `Passing(bits, word, i)`), measured against today's copy per vector.

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

## Package E: today's 128-bit path (branch feat-sse-path)

`$S` = `/tmp/claude-0/-workspace/1ad27225-4a0d-50e5-acbc-5950e4df5375/scratchpad`.

**What the kernel's SSE code showed.** Under `DOTNET_EnableAVX=0` LaneKernelBenchmark's TallyGenerated ran 2.5 times
the kernel's walk of the same job (529 against 209 ns at 1,000 rows), Step and Holes generated 1.3 times. At the
default widths the same jobs are level.

**The cause.** SSE4.1's `blendvps` / `pblendvb` take their mask in XMM0. In the generated loop, inlined into its runner
(`World:Masked[..., TallyJob3]`), the register allocator kept a value the loop carries in XMM0 (Tally: the constant 1,
then the count's accumulator; Step: the hoisted 0.5f; Cool: the constant 3; Brighten: a byte mask) and stored and
reloaded it around the blend inside the loop: `movaps [rbp-0xC0], xmm0` ... `pblendvb` ... `movaps xmm0, [rbp-0xC0]`
every vector, a store-to-load chain carried by the loop (about five cycles against a short loop's two to four). The
kernel's walk kept its constants as memory operands, so XMM0 stayed free. With AVX the blend is VEX-encoded and takes
any register; ARM64's BSL has no fixed register: the issue is x64 without AVX alone.

**First form (19d064fc, on feat-lane-kernel): every select of a Vector<T> / Vector128 text through
`Interleaved.Select`**, `whenFalse ^ ((whenTrue ^ whenFalse) & mask)` when `Sse41.IsSupported && !Avx.IsSupported`,
else ConditionalSelect. A/B (`$S/kernelE/cmp.out`, two rounds, base 035b3b6f) under `DOTNET_EnableAVX=0`: Brighten,
Cool, Tally -34..-57 %, Step -26..-33 %, LaneKernelBenchmark's generated Step / Holes / Tally -17..-44 %, all at 0 B;
but 20 rows slower. The causes, from Tier1 listings of base 89090767 and the rebased head under `DOTNET_EnableAVX=0`
(`$S/kernelE2/ls/{base,head}-*.asm`, `tools/perf/loopbound.py` for the llvm-mca columns, Ice Lake server model):

| row (SSE) | moved | selects in the loop | cause |
|---|---|---|---|
| KernelBenchmark Gate (100k) | +24 % | 4 | Base hoisted its nine constants into xmm1-9 and spilled xmm0 (2.5f) around the blends. The xor form's temporaries pushed the loop past the JIT's hoisting budget: six `movddup` and three memory-operand constants in every iteration (4 loads per vector -> 12), no spill. Reproduced in a standalone loop (`$S/kernelE2/sel/Program.cs`): the blend hoists nine constants; the xor form reloads them whether the helper has two returns, one expression or a conditional expression. |
| Grade (1k, 100k) | +18-22 % | 6, nested 3 deep per field | Base had no carried chain (two masks spilled and reloaded inside one iteration). Head: 61 instructions against 63, but each nested select is three dependent operations (xor, and, xor: 3 cycles) where the blend took one, along two chains of three. |
| Clamp (100k) | +14.5 % | 2 (Math.Clamp) | The loop holds the method's call for a NaN, so both sides reload the window's bounds from the stack every vector; base's blends had no carried chain. Head: 29 instructions against 27, the two selects 6 cycles of latency in series instead of 2 (llvm-mca 6.3 against 6.0 cycles per vector). |
| Halves, Holes (Step on masked words) | +8-12 % | 3 (the 100f select and two Keeps) | StepJob0's `__MaskedLoop` is its own method: no XMM0 spill in base (65 against 64 instructions; llvm-mca 23.4 against 22.7). The xor form lengthens the chain from the select through the Keep into the store. |
| Survey (1k, 100k) | +7-14 % | 0 in the loop | The vector loop is identical (15 instructions; `Hot++` under the condition is a `psubd`). The runner differs only in the scalar fold of Peak after the loop (`jl` against `jge`, once per run): block layout, not the select. Re-measured. |
| Count (1k), Energy (100k), Move4 (1k) | +4-7 % | 0 run | Every runner of these jobs (Masked, ForEachBatchRuns, ForEachMasked, Match) is the same Tier1 code on both sides, normalized: not the change. Re-measured. |
| VectorFormsBenchmark MaybeParallel | +10.5 % | 1 | The chunk's own Execute: base computed the mask into XMM0 and kept its constants as memory operands, no spill; head +2 instructions (36 against 34). |
| WorldSimulation Tick (10k) | +6.5 % | Move: 1 | Move's loop lost its XMM0 spill (20 -> 19 instructions); the other differences in the listings are tiering and inlining choices in code without a select (Fight_0 397 -> 209 instructions, CheckDeath 96 -> 356). Re-measured. |
| LaneKernelBenchmark HolesKernel, StepTyped, TallyTyped | +3.5-75 % | Lane128 | Package D's Lane128 hunk, which E carried; dropped with the rebase onto main (D is not merged). |

**The fix (2d3012e8): the select form per loop.** A spill around the blend costs an iteration once, for all its selects;
the xor form costs two uops and two cycles of latency per select, and enough of them stop the JIT's hoisting. So
`Kernel.Selects`, on the text of one loop, writes `Interleaved.Select` only when the loop has one select; a loop with
several keeps ConditionalSelect. Every winning row has one select in its loop (Tally's conditional sum, Cool, Brighten,
Step and StepParallel dense, WorldSimulation's Move); every slower row with a changed loop has two or more (Gate 4,
Grade 6, Clamp 2, the masked Step 3), so its text is base's again. Cycle (two selects, -12.7 % at 100k) gives its win
back and is level. MaybeParallel keeps the xor form (one select): the A/B decides it.
Tier1 under `DOTNET_EnableAVX=0` after the fix (`$S/kernelE2/ls/new-*.asm`): the hot loops of Grade, Clamp and the
masked Step are base's instruction for instruction, Gate's but for two base registers' names; Count, Energy and Move4
stay the same code as base.

**Proof.** KernelTests.SelectIsConditionalSelectsBits; GeneratorTests read Interleaved.Select in one-select loops and
ConditionalSelect in loops with several; the vector-loop classes under the four width settings (under
`DOTNET_EnableHWIntrinsic=0` TheScheduleNamesEachMethodsLoop and ALambdaPassChangingStructureEndsWhereTheMethodDoes
fail on base 89090767 alike); the full suite on net10.0 and net11.0; agents.sh --check; inlining.sh (every job
inlined). A/B: `$S/kernelE2/ab.sh` (base 89090767 against the fix, KernelBenchmark's
*Generated / *Forced rows, VectorFormsBenchmark, WorldSimulation and BattleServer, two interleaved rounds under
`DOTNET_EnableAVX=0` and two at the default widths; reports in `$S/kernelE2/bdn/`). Ships only as a win under SSE with
every row level or faster, at 0 B.
