# .NET 11: what it ships and what EasyEcs tries (feat-net11, ledger L14)

Research for packages B onward of `feat-net11`, from the GA documents (the runtime and libraries pages were "last
updated for the general availability (GA) release", 2026-11-10; the performance post is of 2026-09-15, RC1). Each item
says where it could help an allocation-free archetype ECS and its generated vector loops, and how it is tried. The rule
for every item (audit-perf, ".NET 11"): code under `#if NET11_0_OR_GREATER` with the net10.0 path unchanged, generated
code compiling on both, kept only as a win at 0 B on net11.0 against the same code without it, nothing slower; a drop
goes to `docs/owner-review.md` with its cause; a public API change is the owner's question.

Sources:
[runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime),
[libraries](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries),
[C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15),
[Performance Improvements in .NET 11](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/)
(cited as "perf" with its dotnet/runtime PR numbers).

## Found while making both targets compile (package A)

- **`MemoryMarshal.Cast` overloads.** With .NET 11's reference assemblies a `ref readonly` inline array passed whole
  binds to the `Span<T>` overload and fails (CS9164); Kernel.Tables passes the table as a range (`table[..]`, a
  ReadOnlySpan). Found by the build of the benchmark worlds on net11.0.
- **`float.DegreesToRadians` / `RadiansToDegrees` round differently on .NET 11**
  ([Single.cs on release/11.0](https://github.com/dotnet/runtime/blob/release/11.0/src/libraries/System.Private.CoreLib/src/System/Single.cs):
  `(float)(degrees * double.DegreesToRadiansHead)`; the double forms are a correctly rounded three-limb product). The
  generator wrote `(x * Pi) / 180` per lane (.NET 10's scalar); it now calls the runtime's vector form, which matched
  the scalar bit for bit on both runtimes over 2M floats and 2M doubles at 128, 256, 512 bits and `Vector<T>`
  (KernelTests.DegreesAndUniformCallsMatchTheMethodBitForBit failed on net11.0 before). Cost on net11.0: the float
  form widens to double; measured in package B with the math kernels.

## 1. What the runtime alone gives (no code change)

Measured first: the headline worlds and the classes below on net11.0 against net10.0 in one process
(`--frameworks net10.0,net11.0`), to know what moving costs or gives before any item below is credited.

| Item | Where it may show in EasyEcs | Source |
|---|---|---|
| Bounds checks: `i + cns < len`, index-from-end, `!span.IsEmpty && span[0]`, `(uint)i < span.Length` lower bound, checks coalesced per block, repeated `Slice` checks in vectorized loops | hand-written core loops (ArchetypeTable walks, bit words), generated tails and masked words | runtime "JIT improvements"; perf #121273, #125056, #127439, #127488, #122040, #127117 |
| Loop cloning for `!=` loops, induction variables, non-unit strides | generated row loops, ParallelFor chunk loops | perf #129268, #129303, #119537, #128303 |
| Redundant branch / test elimination after inlining | AggressiveInlining jobs inside World runners | runtime "Redundant branch and test elimination" |
| `Vector256.Sum` / `Vector512.Sum` at full width | RunningTotals folds, Reduce<T> | perf #127329 |
| Vector constant propagation for 256 / 512 bits; blend simplification; SIMD cost model for hoisting and CSE | every vector loop's broadcasts and selects | perf #127402, #123146, #127048 |
| User structs bitcast to SIMD keep their vector form; SIMD and mask stores kept intact | Kernel.Struct (Vector2/3/4 lanes), Interleaved | perf #129563, #128013 |
| Adjacent stores combined into wider ones; struct-copy write barriers with vector stores | component Set of small structs; managed component copies | perf #126562, #130107, #130535, #128238, #128542 |
| Generic virtual method devirtualization (also AOT), generic virtual call CSE and hoisting | the system set's dispatch, `IForEach*` jobs through generic runners | runtime "Devirtualization improvements"; perf #120866, #122023, #128702, #122017, #130202 |
| PopCount / LZCNT / TZCNT results with known ranges | presence words, masked words, bit-index walks | perf #128620 |
| Array-store helper expanded (covariance check removed when the type is exact) | managed component columns `T[]` of reference types | perf #126547 |
| Thread pool: less overhead for small work items, fewer fences, less over-waking | only where Workers fall back to the pool (check) | perf #122726 |
| Native AOT: interface dispatch through a patched helper, GVM dispatch cells | the AOT check's battle server | runtime "NativeAOT: faster interface dispatch" |
| x86-64-v2 baseline for JIT and AOT; R2R target x86-64-v3 | AOT builds without `IlcInstructionSet` now get SSE4.2 / POPCNT | runtime "Updated minimum hardware requirements" |

## 2. APIs and language features that need code (tried one per package)

| # | Item | Candidate use in EasyEcs | How it is tried | Source |
|---|---|---|---|---|
| B1 | `Vector64/128/256/512<T>` and `Vector<T>`: `Unzip` / `UnzipEven` / `UnzipOdd`, `Zip` / `ZipLower` / `ZipUpper`, `Concat*`, `Reverse` | Kernel.Struct and Kernel.Wide de-interleave struct fields (Vector2/3/4, wide rows) with permute trees and `Interleaved.Pick`; `UnzipEven/Odd` of two vectors is a field pair of Vector2 rows; on ARM64 it maps to UZP1/UZP2, on x64 to the permutes the JIT picks | generated text under `#if NET11_0_OR_GREATER` in `Interleaved` (Core) first, so no generator change: the de-interleave of two-field structs; KernelBenchmark Move2 / StructBenchmark, Tier1 listing | libraries "Cross-lane vector operations"; perf #129627 |
| B2 | `CreateGeometricSequence` / `CreateAlternatingSequence` | lane masks and per-lane bit selectors (`Vector.Indices`-based masks in masked words, `1 << lane`) | where Core builds a constant of powers of two per lane (masked-word compress); a constant either way after folding, so expected equal: listing first | runtime "SIMD lane construction" |
| B3 | Float -> long / ulong conversions inline on AVX-512 / AVX10.2 | the generator's `ConvertToInt64` lanes, EntityRandom scaling | runtime-only; checked in the VectorForms benchmarks of the 1 run | perf #125180 |
| B4 | Saturating float -> small integer conversions (semantics) | scalar `(byte)f` now saturates; the generator vectorizes no float -> narrow conversion today (Kernel.Convert: same sizes only), so nothing differs; a new form must follow the runtime it runs on | a test: no vector form for `(byte)float` until one is bit-exact on both | runtime "Saturating floating-point conversions" |
| B5 | `AvxVnni.V512`, AVX-512 BMM | integer dot products of byte lanes; EasyEcs has no such pass | not tried: no pass in the core or the samples needs a u8 x i8 dot product | runtime JIT; perf #128365, #124804 |
| B6 | Hardware FP16 (`Half` arithmetic on AVX10.1, conversions on F16C) | none: no `Half` lanes in the kernels | not tried; a `Half` lane type is a feature request of its own | runtime "Hardware intrinsics" |
| B7 | Runtime async (`<Features>runtime-async=on</Features>`) | EasyEcs.Core has no async method (checked: no `async`/`await` in src/EasyEcs.Core) | not applicable | runtime "Runtime Async" |
| B8 | C# 15 labeled `break` / `continue` | the generator's `goto` exits of nested loops (flat loops, masked words); clarity only, the IL is the same branch | not a performance item; left to audit-generator | C# 15 |
| B9 | C# 15 collection expression arguments (`[with(capacity: n), ..]`) | cold paths that size a list first | not a performance item on hot paths (they allocate nothing) | C# 15 |
| B10 | C# 15 extension indexers | `world[entity]` or `entity[T]` style access | public API: the owner's question, not built | C# 15 |
| B11 | C# 15 union types, closed hierarchies | none in an allocation-free ECS (unions of reference cases box) | not tried | C# 15 |
| B12 | `BitArray.PopCount`, span constructors | none: EasyEcs keeps its own bit words | not tried | libraries "Collections" |
| B13 | Span `Sort` with value-type comparers without boxing | `[OrderBy]` uses its own radix sort; any `Span.Sort` with a struct comparer in Core | grep Core for `Sort(`; measure where one runs per tick | perf (Sorting section) #116109 |
| B14 | `Dictionary.Remove` for value-type keys, `HashSet` chain walk | Core's dictionaries on cold paths (type registry) only | not a hot path; not tried | perf #125884, #125893 |
| B15 | SVE: `CreateWhile` variants, `Vector<T>` by reference for SVE, SVE2 narrowing | ARM64 tails as predicated loops | needs SVE hardware (CI's ARM64 runner reports none); recorded, not measured | runtime "Hardware intrinsics" |

## Order

Package B: the runtime-alone comparison (section 1: WorldSimulationBenchmark, BattleServerBenchmark,
MultiWorldBenchmark, KernelBenchmark, the math kernels with the new DegreesToRadians) and B1. Then B2, B3/B4 checks,
B13. Each kept item's numbers and each dropped item's cause go into this file, then into D21's .NET 11 part (docs pass).
