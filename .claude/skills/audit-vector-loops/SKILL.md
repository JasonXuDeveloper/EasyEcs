---
name: audit-vector-loops
description: "Audit and extend the vector loops the EasyEcs generator writes from plain [ForEach] methods (Kernel*.cs, Burst's idea) - which reads, value writes, tag and structural writes, conditions and parameters become SIMD, side effects that keep a method scalar, the exact fixable reason EECS055 / EECS056 report (and the split it suggests), and the proof each form needs. Use when a method stays scalar, when adding a vectorized form, or when changing Kernel. Triggers: Kernel*.cs, EECS055, EECS056, Vector<T>, SIMD, \"not vectorized\", \"why scalar\", if conditions, side effects, 向量化, SIMD, 没有 SIMD, 拆分条件."
metadata:
  internal: true
---

# Vector loops audit

Users write a plain `[ForEach]` method; the generator emits the vector loop (`Kernel*.cs`, D68 / D90). The goal:
every method that only reads and writes components of the rows it visits runs as SIMD, whether it writes a number,
a state (a tag) or the table itself (adding, removing, destroying). Files: `Kernel.cs` (`Kernel.Vectorize`: the loop
or the reason), `Kernel.Narrow.cs`, `Kernel.Struct.cs`, `Kernel.Wide.cs`, the job emission in `SystemGenerator.cs`,
`Interleaved.cs` / `Lanes.cs` / `World.Bulk.cs` in the core. `audit-generator` owns the rest of the generators;
`audit-analyzer` owns the EECS055 / EECS056 descriptors.

## What becomes a vector loop

- **Reads**: `in` / `ref` component parameters (one column load per lane group, interleaved fields deinterleaved),
  `Maybe<T>` (presence as a lane mask), singletons (broadcast once per pass), constants and job fields. Reads of other
  entities (`Lookup<T>`, an index) are gathers: vectorize only where measured faster.
- **Value writes**: assignments, compound assignments, several fields, `Mut<T>.Write`; under a condition a masked
  blend (`Vector.ConditionalSelect`) then a store; a tracked component stamped only where the mask is set.
- **State and structural writes**: a tag or component added or removed, a destroy (`[Consume]`), recognized by
  meaning, not by name: the generated members (`SetDead()`, `RemoveX()`, `SetX(v)`) and the generic forms
  (`Set<T>()`, `Set(new T())`, `Remove<T>()`, `Destroy()`) through `Symbols.StructuralChange` (`Kernel.Structural.cs`).
  The condition runs as a vector loop into a lane mask, the passing rows are marked (`MarkedRows`), and the change is
  applied after the pass by the `World.Bulk` row ops: whole words where a table changes in place, else row by row in
  the order the pass marked them, inside a pass of their own, so every table keeps the method's row order (later
  passes emit in the scalar method's order, ids are freed in its order: D122). `Emit<T>` records of a lane or the
  entity's id are appended compacted (`Lanes.Compact`). Every row takes the vector form (the last rows through
  `Interleaved.LoadTail`), and hardware without the loop's vectors runs the method's own job. One change of one
  uniform value per method; the method must not take the component it changes; a method that changes structure has
  no cold branch (its rows cannot run the method during the pass). The generic form keeps its EECS033 Warning (a
  better generated member exists), never an Error.
- **Wide and mixed rows** (`Kernel.Wide`): a component of more fields than the loop splits, or of mixed types, where
  the method uses a few fields of one lane size. Load several rows as whole vectors, pick each used field into one
  vector with permutes (two-source `vpermt2*` on AVX-512; single-source `vpermpd` / `vpermps` plus blends on AVX2),
  write back only the vectors holding written fields, the other bytes as loaded. One row per vector (one useful lane)
  never pays. It pays only when the method's operations outweigh the permutes (`Gate`); otherwise EECS055 suggests
  splitting the used fields into a component of their own, which is always the fastest form.
- **Inner loops** (Burst's loop vectorizer): a scalar `for` / `foreach` over a span inside a method the generator
  already compiles (a `[ForEach]` method, a `ForParallel` job's `Execute`) is a vector loop over its elements: a
  `DynamicBuffer<T>`'s elements, an `[InlineArray]` or fixed buffer field, an `IForSpan<T>` / `IForRange` body, a
  neighbor list. One core for every element loop; the per-row loop and the element loop share the same lowering of
  the body. Inferred, no declaration (`audit-generator`, declaration surface: inference first).
- **Cells of a tile** (the shader form, built on the inner loop): a method that computes one cell from its neighbors
  by offset; the generator writes the pad, the row walk and the element loop, the neighbor reads unaligned loads at
  offsets in the padded texture, so users write no `Load128`.
- **Not kernels** (measured or overlapping, D90): reads of other entities (`Lookup<T>`, parent transforms) stay
  scalar, gathers ran at 0.8-1.18 of scalar; a per-element parameter for buffers would say what an inner loop says.
- **Hand-written SIMD where the plain form vectorizes** is reported (`audit-analyzer`, written by hand where the
  generator writes it): `Vector128` / `Vector<T>` code in a batch or tile method whose plain form becomes the same loop.
- **Per-chunk parameters**: `Reduce<T>` (lane partials combined in chunk order), `Emit<T>` (records of the passing
  rows compacted, in row order), `EntityRandom` (a pure function per lane).
- **Conditions**: `if` / `else` chains, nested ifs, early `return` (a mask of rows still running), ternaries, `&&`,
  `||`, `!`, comparisons across fields of several components and singletons, `Math.Min` / `Max` / `Abs` / `Clamp`,
  the saturating and narrow-lane idioms.

## Side effects keep a method scalar (or need care)

A vector loop evaluates every lane of both sides of a branch, so it is correct only when evaluating a masked-off lane
changes nothing and cannot fail:

- **Short-circuit guards**: `b != 0 && a / b > k` must not divide by zero in a masked-off lane: a safe divisor under
  the mask, or scalar. Integer division, checked arithmetic, array indexing and casts that throw are the usual cases.
- **Calls**: a call into user code or the runtime that may write state, allocate or throw keeps the method scalar; a
  known pure call (math) is translated.
- **Shared state**: writes to statics, captured fields or another entity; the order of anything observable (records,
  logs) beyond what `Emit<T>` keeps.
- **A method reading its own write**: a structural write deferred to the end of the pass is correct only when the
  method cannot observe it (it does not read the tag it sets, the filter does not depend on it in this pass).
- **Floating point**: no reassociation or contraction unless fast math is on (`SimdMode`); results bit-identical to
  the scalar method otherwise, and at 1 and 4 threads.
- **Math rewrites (strength reduction) are proven bit for bit, not by algebra**: a rewrite of a call or operator into
  cheaper vector operations is exact only if it matches the scalar result for every input, checked exhaustively over
  all 2^32 floats (and sampled doubles) against the runtime the user runs. The C runtime's `pow` is not correctly
  rounded, so `MathF.Pow(x, 2)` differs from `x * x` by 1 ulp on about 1.5M floats (likewise `Pow(x, 0.5)` against
  `Sqrt`, `Pow(x, -1)` against `1 / x`; `Pow(x, 0)` is NaN for signaling NaNs): Pow rewrites belong to fast math only.
  `Pow(x, 1)` / `Pow(x, 0)` send NaN lanes to the method (the C runtime's `pow` of NaN differs between platforms).
  Exact by default: `Pow(x, 1)`; a float division by a power-of-two constant as a multiply by its reciprocal; integer
  division and `%` (by a power of two: shifts, any lane size; by another constant or any divisor: through `double` on
  32-bit lanes, `LaneMath.Quotient`, exact since both operands fit 53 bits; 64-bit lanes stay scalar; the lanes that
  would throw, 0 or MinValue / -1, are sent to the method as `Clamp` does); integer `Abs` / `CopySign` / `Math.Sign`
  with the throwing lanes guarded the same way. Fast math only: `Pow` by a small integer or 0.5, division by any other
  constant, `Log10`, `Tan`, `Sinh` / `Cosh` / `Tanh` through .NET's vector `Exp` / `Log` / `Sin` / `Cos` in double
  precision (`LaneMath`; `Log2(x) * c` was 3.4 ulps off, `Pow(x, -2)` is `(1 / x)^2`: `1 / (x * x)` broke the bound
  where `x * x` is subnormal). The exhaustive sweeps (`LaneMathTests`, category `Exhaustive`) take about 8 minutes in
  Release on 4 cores and far longer in Debug: run them with `-c Release` after any change to a lowering (D127). Never:
  functions .NET has no vector form for, written as our own polynomials (`Atan2`, `Cbrt`, general `Pow`). Uniform
  arguments already need none of this: the scalar call runs once per pass.

## Reasons users can fix

`Kernel.Vectorize` returns the loop or one reason; the analyzer reports that reason (EECS055 / EECS056), so a method
left scalar is exactly a method reported. Each reason is classified once, in Kernel, as:

- **fixable** (EECS055 Warning, the fix in the message): the reason names the exact statement or sub-expression that
  blocks the loop and what to write instead. Typical: a condition with a call or a lookup ("split it into its own
  `[ForEach]` method: a vector pass sets a tag, a second pass over the tagged rows does the scalar part"), too many
  branches for one loop (split by tag or by group), a per-row lookup (`Lookup<T>` hoisted or a component copied in), a
  component of too many fields (split the component), a mixed type the loop cannot widen;
- **inherent** (not reported, or Info): the algorithm needs scalar work (a call into user code whose result decides
  the row; a loop over a buffer per row until element loops land, `docs/plans/inner-loops.md`).

The owner's rule: plain code that a small rewrite would turn into a vector loop must warn. When in doubt, a reason
is fixable; a reason stays inherent only when no rewrite of the method keeps its meaning and vectorizes. A fixable
reason whose rewrite is mechanical gets a code fix (`audit-analyzer`). Hand-written SIMD the plain form would
generate warns too (EECS103).

Reason strings are stable and specific; tests pin them. Rank the reasons by how often they appear over `samples/`,
`BattleServer.cs`, `WorldSimulation.cs`, templates and the docs examples: the most frequent fixable reason is the next
form to support.

## Proof for each new form

- A test that the vector loop equals the scalar method on random data, edges included (no row passes, every row
  passes, an empty table, a structural change by another system in the same tick); bit-identical at 1 and 4 threads.
- The 0 B allocation test; a `KernelBenchmark` case against the scalar method and the hand-written fast form (bulk
  call, hand-written SIMD) at several pass rates (1 %, 10 %, 50 %) and sizes (10k, 100k).
- Tier1 asm (`tools/jitdisasm.sh`): no call in the loop body, no unexpected range check.
- A change to `Kernel` leaves the code generated for `WorldSimulation.cs` and `BattleServer.cs` identical, or ships
  with their BenchmarkDotNet numbers.
- The docs show `You write` / `The generator writes` from real output wherever the form appears (`audit-docs`).

## Read the loop the JIT wrote, not only the text the generator wrote

Every round reads the Tier1 code of the samples' loops (`tools/jitdisasm.sh samples/MultiWorld 'EasyEcs.World:Masked'`)
next to the generated text, and asks per loop: how many instructions per vector, which of them move data instead of
computing. Lessons so far (MultiWorld, .NET 10, AVX-512):

- **Copies in the text are free, shuffles are not**: `var __c = __s;` and `__t` temporaries for a whole-value
  assignment vanish (the JIT copy-propagates them). Removing them is not neutral either: it changed which forms the
  JIT inlined into `Move3`'s runner (D126), so a text cleanup ships only with identical Tier1 listings. Compare
  listings with `DOTNET_TieredPGO=0`: with PGO on, Tier1 follows the profile the run collected. What costs is
  deinterleaving: `Move` (`position += velocity`, two floats each) spends 6 `vpermt2ps` and 4 permutation-table loads
  per 16 rows on 2 adds. A pass whose every write field k depends only on field k of inputs with the same layout
  (element-wise: `+=`, `*= scalar`, `Lerp`) runs over the flattened floats with no deinterleave; deinterleave only
  when fields mix (`x * x + y * y`).
- **Permutation tables are loaded per permute, and that is right** (D126): every `vpermt2ps` / `vpermi2ps` overwrites
  one operand, so a table kept in a register is copied before each permute anyway; the `[rip+const]` load costs the
  same. Hoisting tables into arguments changes nothing per vector.
- **A mask the rows already have is the start of the active mask** (D126, done): the masked form starts its condition
  chain from `pass`, one `ConditionalSelect` per write, no trailing selects per column and accumulator.
- **Element-wise passes take flat lanes** (D126, done), whole vectors only: splitting a masked word's flat lanes
  into per-field masks lost to the deinterleaving loop, so masked words leave the flat loop.
- **The job must stay inlined into the runner**: `World.Masked` / the run loop inline the job's `Execute`; text that
  grows the body past the JIT's inlining budget turns it into a call per run (per 64-row word in a masked table) and
  costs far more than it saves. A masked AVX-512 tail for the last rows (D126) won 0.5-0.66x on a world of 1,000 tiny
  tables for a one-float body and lost up to 3.7x on `CycleHoles` for this reason: check the Tier1 listing of the
  runner (`tools/jitdisasm.sh`, `EasyEcs.World:Masked`) for `call ...Execute` after any change that adds text to the
  body. The answer is in the generator, not in less code (the owner's rule): it shapes what it writes so the hot
  loop stays inlined (cold parts in `NoInlining` helpers called once per run, each width in its own method, or a
  generated entry that runs the word loop itself), and a test pins `call ...Execute`-free runners. The plan and its
  measurements: `docs/plans/inner-loops.md`, WP0b.
- **The four copies of the body are text, not code**: two widths × masked/unmasked; the width the hardware lacks is
  removed at JIT time (`IsHardwareAccelerated` is a constant). They cost generator output and build time, and IL the
  inliner may count before it removes the dead width (the lesson above): measure any copy added.
  One body for both widths needs a generic vector abstraction: `ISimdVector<TSelf, T>` is internal in .NET 10, so it
  would be our own static-abstract wrapper, adopted only if BenchmarkDotNet and the Tier1 code show no loss.

Each lesson becomes a form with its proof (above) or an entry in `docs/owner-review.md` with the measurement.
