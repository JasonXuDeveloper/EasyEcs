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
  (`Set<T>()`, `Set(new T())`, `Remove<T>()`, `Destroy()`) through the shared `Symbols` helpers. The condition runs as a
  vector loop into a lane mask, the passing rows are compacted (`Lanes.Compact`), and the change is applied in bulk
  after the pass (whole words, the `World.Bulk` row ops): no per-row structural call, no tombstones. The generic form
  keeps its EECS033 Warning (a better generated member exists), never an Error.
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

## Reasons users can fix

`Kernel.Vectorize` returns the loop or one reason; the analyzer reports that reason (EECS055 / EECS056), so a method
left scalar is exactly a method reported. Each reason is classified once, in Kernel, as:

- **fixable** (EECS055 Warning, the fix in the message): the reason names the exact statement or sub-expression that
  blocks the loop and what to write instead. Typical: a condition with a call or a lookup ("split it into its own
  `[ForEach]` method: a vector pass sets a tag, a second pass over the tagged rows does the scalar part"), too many
  branches for one loop (split by tag or by group), a per-row lookup (`Lookup<T>` hoisted or a component copied in), a
  component of too many fields (split the component), a mixed type the loop cannot widen;
- **inherent** (not reported, or Info): the algorithm needs scalar work (a loop over a buffer per row, a call into
  user code whose result decides the row).

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
