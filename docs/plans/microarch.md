# Plan: generated code tuned for the pipeline, the caches and the branch predictor (internal, not published)

The owner's rule: users write plain methods and never think about instruction-level parallelism, cache use or branch
prediction; the code the generators write takes care of them. This plan lists what the generated code already does,
what it does not, how to find where a kernel is bound, and the work packages. The rule itself lives in
`audit-generator` ("Tuned for the machine").

## 1. How to see where a loop is bound on this machine

- **No hardware counters here**: the VM exposes no `cpu` PMU (`/sys/bus/event_source/devices` has none), so no
  `perf stat` for IPC, branch misses or cache misses, and BenchmarkDotNet's hardware counters do not work on Linux.
- **Static: `llvm-mca` (installed, LLVM 18)** on the Tier1 listing of a job's loop (`tools/jitdisasm.sh`, the loop
  body between its label and its back edge, AT&T or Intel syntax with `-x86-asm-syntax=intel`), `-mcpu=icelake-server`
  (and `znver4` for the other vendor): it reports the bound of a loop (throughput per iteration, the critical
  dependency chain, port pressure). A loop whose bound is the chain (latency) gains from independent accumulators or
  unrolling; one bound by ports gains only from fewer instructions.
- **Dynamic: BenchmarkDotNet with controlled data**: branch cost shows as sorted against shuffled input of the same
  values; cache cost as 1k (L1) / 100k (L2/L3) / 1M+ (memory) rows of the same method; a chain as the time of one
  accumulator against two in a hand-written copy.

## 2. What the generated code already does

| Concern | Already there |
|---|---|
| Branches | conditions become masks in vector loops (no branch per row); a cold branch bails out of line (`NoInlining`); a masked vector tail; presence bits walked by words; data instead of flags keeps branches out of the rows (tags split tables) |
| Pipeline | vector loops; the 32-IL-locals budget so the JIT inlines (WP0b); permutation tables hoisted; no `ref` field across rows (a store per row, `SystemGenerator.cs` comment) |
| Caches | SoA columns aligned to 64 bytes; pass fusion (each row loaded once for several methods); chunks of 64-row multiples cut by measured cost (D130); per-thread scratch |
| Front end | cold paths out of line; WP0b moves each loop of an oversized Execute into its own method |

## 3. Candidates (each: the llvm-mca or data evidence first, then BenchmarkDotNet, two rounds, 0 B)

**Pipeline**
1. **Independent accumulators for reductions.** A `Reduce<T>` / sum in a vector loop adds into one vector per field:
   a chain of one `vaddps` (4 cycles) per vector while two can issue per cycle. Two to four accumulators per field,
   folded at the end, cut the chain. Exactness: lanes already reassociate a float sum (D68); more accumulators are
   the same kind of change, fixed by the code's shape (never by the thread count, D125), and the Precise / Fast rule
   of `audit-vector-loops` decides whether floats get it or integers only. Bar: a sum-bound method 1.5x at 100k.
2. **Unrolling short vector loops by two.** The JIT does not unroll; a body of a few instructions pays the loop
   overhead and one chain per vector. Two vectors per iteration where llvm-mca says the loop is latency-bound and the
   locals stay within 32 (WP0b's budget). Bar: 1.2x on the short kernels of `KernelBenchmark`, nothing slower.
3. **Hoisting what the JIT reloads.** Constants and table addresses the inlined runner reloads each iteration
   (no CSE across the big body, seen in the copy-asm work): written once into locals before the loop, within the
   locals budget. Proof: the Tier1 listing, then BDN.

**Branches**
4. **Scalar rows without branches.** A method that stays scalar with `if (c) x = a;` shapes: written as a select the
   JIT turns into `cmov` (its if-conversion takes only simple shapes); proven on shuffled against sorted data.
5. **Uniform masks.** Where a mask is all-true or all-false for long runs (a tag-like condition on a field), the
   "any lane" test before a masked block is a predictable branch; where it flips every vector it costs a miss per
   vector: measure both, and drop the test where the block is cheaper than a miss.
6. **The analyzer for conditions that should be data.** A method whose body is one `if` on a component's bool or
   enum field says to make it a tag (the ECS principle "data instead of flags") so the rows that do not match are not
   walked: check whether a rule exists (`audit-analyzer`), add it if not.

**Caches**
7. **Prefetching chases** (`borrowed-ideas.md` 1) for `Lookup` reads and relation walks.
8. **Streaming stores** (`borrowed-ideas.md` 5) for write-only columns of passes much larger than the cache.
9. **Wide components** already get the split suggestion (EECS055/091); check the walk of a fused group does not
   exceed the hardware prefetcher's streams (columns read at once), and split the walk if it does.

## 4. Work packages and order

- **M0** (one agent, after WP0b merges; Kernel files): llvm-mca over every kernel shape of `KernelBenchmark`,
  `BattleServer` and `WorldSimulation` jobs; a table "job, bound (chain / ports / memory), cycles per vector" in
  section 5; the method added to `audit-vector-loops`.
- **M1** candidates 1-3 (Kernel), **M2** 4-6 (generator + analyzer), **M3** 7-9 (core + Kernel, with
  `borrowed-ideas.md`). Each with its decision record; a failed bar goes to `docs/owner-review.md`.

## 5. Results

### M0: where each kernel shape is bound (llvm-mca 18, 2026-10-27)

Method: `tools/loopbound.sh <class> [rows]` runs a benchmark class's methods past Tier1 (`DOTNET_TC_CallCountingDelayMs=0`, 300
calls each) with `DOTNET_JitDisasm` on the runners, takes every innermost loop of each Tier1 listing that uses zmm
registers (a back edge to an earlier label, no back edge inside), drops duplicate copies (the runner inlines a job's
`Execute` at three sites: whole runs, runs between masked words, masked words), and runs `llvm-mca -iterations=200
-bottleneck-analysis` on each body with `-mcpu=icelake-server` and `-mcpu=znver4`. Three numbers bound a loop: the
ports (llvm-mca's block reciprocal throughput), the front end (uops per iteration / the model's dispatch width, 6 for
both) and the loop-carried chain: the longest path, with llvm-mca's latencies, from a register the loop reads before
it writes it back to that register (computed by the tool; llvm-mca's own "data dependencies" figure also counts the
long paths inside one iteration, which the out-of-order window overlaps across iterations). The bound is the largest
of the three; the simulated cycles per iteration are llvm-mca's, which adds what the model's window and divider cost
on top (Seek: a sqrt and a division per vector, 22 cycles of the unpipelined divider).

Cycles per vector iteration (16 lanes of 4 bytes, 8 of 8, 64 of 1), AVX-512, `KernelBenchmark` at 1,000 entities,
`WorldSimulation` / `BattleServer` at 10,000. "whole": the loop over whole vectors of a run; "masked": the loop over
a 64-row word of which some rows pass. Columns: simulated c/it | ports / front end / chain | bound.

| Job | Loop | Instr. | Ice Lake-SP | | Zen 4 | |
|---|---|---|---|---|---|---|
| Step | whole | 28 | 8.2 \| 7.5 / 6.7 / 1 | ports | 9.6 \| 6.0 / 5.3 / 1 | ports |
| StepHelpers | whole | 28 | 8.2 \| 7.5 / 6.7 / 1 | ports | 9.6 \| 6.0 / 5.3 / 1 | ports |
| Ratio (int / 7 through doubles) | whole | 43 | 32.3 \| 32.0 / 9.7 / 0 | ports (divider) | 22.2 \| 22.0 / 8.8 / 0 | ports (divider) |
| Integrate | whole | 7 | 2.1 \| 1.7 / 1.7 / 1 | ports | 1.6 \| 1.5 / 1.2 / 1 | ports |
| Integrate | masked | 20 | 4.5 \| 4.2 / 4.2 / 1 | ports | 4.1 \| 3.3 / 3.3 / 1 | front end |
| Count | whole | 5 | 1.3 \| 1.2 / 1.2 / 1 | ports | 1.1 \| 1.0 / 0.8 / 1 | ports |
| Count | masked | 17 | 4.1 \| 3.5 / 3.5 / 1 | ports | 4.1 \| 3.2 / 3.2 / 1 | ports |
| Move2 / Push / Move4 / MoveVector (flat) | whole | 7 | 2.1 \| 1.7 / 1.7 / 1 | ports | 1.6 \| 1.5 / 1.2 / 1 | ports |
| Move3 (flat) | whole | 8 | 2.1 \| 2.0 / 2.0 / 1 | ports | 2.1 \| 2.0 / 1.3 / 1 | ports |
| Move3 | masked | 66 | 23.1 \| 21.0 / 14.7 / 1 | ports | 20.8 \| 18.0 / 11.2 / 1 | ports |
| Move4 | masked | 110 | 33.1 \| 27.0 / 24.3 / 1 | ports | 27.9 \| 24.0 / 20.3 / 1 | ports |
| MoveVector | masked | 43 | 11.6 \| 9.3 / 9.3 / 1 | front end | 9.6 \| 7.2 / 7.2 / 1 | ports |
| Seek (3 fields, sqrt, division) | whole | 97 | 51.1 \| 22.3 / 22.3 / 1 | ports (divider) | 44.6 \| 21.0 / 19.8 / 1 | ports (divider) |
| Seek | masked | 133 | 64.2 \| 30.2 / 30.2 / 1 | ports | 53.6 \| 25.7 / 25.7 / 1 | ports |
| Travel (3 doubles) | whole | 52 | 15.8 \| 13.0 / 12.7 / 1 | ports | 14.1 \| 12.0 / 10.0 / 1 | ports |
| Travel | masked | 68 | 22.6 \| 16.0 / 15.7 / 1 | ports | 19.1 \| 13.0 / 12.3 / 1 | ports |
| Clamp | whole | 21 | 5.2 \| 4.8 / 4.8 / 1 | ports | 6.1 \| 6.0 / 3.5 / 1 | ports |
| Quantize | whole | 21 | 6.7 \| 5.5 / 4.3 / 1 | ports | 7.3 \| 7.0 / 4.0 / 1 | ports |
| Quantize | masked | 38 | 11.1 \| 8.3 / 8.3 / 1 | front end | 11.7 \| 8.5 / 8.0 / 1 | ports |
| Gate | whole | 33 | 11.2 \| 9.0 / 8.0 / 1 | ports | 12.1 \| 10.0 / 5.8 / 1 | ports |
| Gate | masked | 44 | 15.4 \| 10.0 / 10.0 / 4 | ports | 14.1 \| 10.0 / 7.5 / 2 | ports |
| Tally (int sum, count, Min, Max) | whole | 10 | 2.6 \| 2.0 / 1.8 / 1 | ports | 3.0 \| 3.0 / 1.7 / 1 | ports |
| Tally | masked | 28 | 7.1 \| 5.3 / 5.3 / 2 | front end | 7.6 \| 7.0 / 4.8 / 2 | ports |
| Cycle (writes + an int count) | whole | 16 | 4.3 \| 3.2 / 3.2 / 2 | ports | 4.6 \| 3.0 / 2.7 / 2 | ports |
| Cycle | masked | 32 | 8.7 \| 6.8 / 6.8 / 2 | front end | 8.1 \| 5.8 / 5.8 / 2 | front end |
| **Energy (float sum, Fast)** | whole | 8 | **4.1 \| 1.7 / 1.7 / 4** | **chain** | **3.1 \| 2.0 / 1.3 / 3** | **chain** |
| Energy | masked | 22 | 5.1 \| 4.3 / 4.3 / 5 | chain | 4.5 \| 3.8 / 3.8 / 4 | chain |
| Wealth (int rows into a long sum) | whole | 10 | 2.1 \| 1.5 / 1.5 / 0 | ports | 1.4 \| 1.3 / 1.3 / 0 | front end |
| Accrue (long column += int * 3) | whole | 16 | 6.5 \| 6.0 / 3.8 / 0 | ports | 2.8 \| 2.5 / 2.3 / 0 | ports |
| Optional (Maybe) | whole | 45 | 10.2 \| 10.0 / 9.3 / 1 | ports | 11.0 \| 9.0 / 8.3 / 1 | ports |
| Hurt (a cold branch) | whole | 13 | 2.8 \| 2.7 / 2.7 / 1 | ports | 2.3 \| 2.2 / 2.2 / 1 | ports |
| Steer (wide, 3 of 12 fields) | whole | 101 | 54.1 \| 32.0 / 24.8 / 1 | ports (permutes) | 50.7 \| 31.0 / 17.0 / 1 | ports |
| Glide (wide, mixed) | whole | 28 | 8.7 \| 6.8 / 6.8 / 1 | front end | 7.6 \| 6.5 / 4.8 / 1 | ports |
| Brighten (byte lanes) | whole | 11 | 2.7 \| 2.3 / 2.3 / 1 | front end | 2.6 \| 2.0 / 1.8 / 1 | ports |
| WorldSimulation Movement.Move | whole | 13 | 3.1 \| 2.8 / 2.8 / 1 | front end | 2.4 \| 2.2 / 2.2 / 1 | ports |
| WorldSimulation Movement.Move | masked | 55 | 18.6 \| 15.0 / 11.5 / 1 | ports | 15.8 \| 9.5 / 9.5 / 1 | ports |
| WorldSimulation Aoi.UpdateCell | whole | 34 | 11.0 \| 8.0 / 8.0 / 0 | ports | 11.1 \| 7.2 / 7.2 / 0 | ports |
| WorldSimulation Aoi.UpdateCell | masked | 47 | 14.1 \| 10.3 / 10.3 / 0 | front end | 13.5 \| 9.2 / 9.2 / 0 | ports |
| BattleServer Move | whole | 6 | 1.7 \| 1.5 / 1.5 / 1 | ports | 1.6 \| 1.5 / 1.0 / 1 | ports |
| BattleServer Move | masked | 39 | 11.1 \| 9.0 / 8.7 / 1 | ports | 8.5 \| 6.5 / 6.5 / 1 | ports |
| BattleServer Regenerate | whole | 16 | 4.1 \| 4.0 / 3.3 / 1 | ports | 4.1 \| 4.0 / 2.7 / 1 | ports |
| BattleServer Regenerate | masked | 29 | 8.1 \| 7.0 / 6.5 / 1 | ports | 6.4 \| 5.0 / 4.8 / 1 | ports |
| BattleServer Die | whole | 26 | 4.8 \| 4.7 / 4.7 / 0 | ports | 6.0 \| 6.0 / 4.3 / 0 | ports |

Not in the table: Share (integer division through doubles, like Ratio), Wave (Sin / Exp: calls into .NET's vector
math), the parallel jobs (the same bodies as Step and Seek, in the chunk runner), and the masked loops of Step,
StepHelpers, Ratio, Move2 and Push, which hold calls (the cold rows, or the finding below).

What M0 says:

- **One chain in every shape: a float reduction.** The only loops bound by a loop-carried chain are the float sum of
  `Energy` (one `vaddps` per vector, 4 cycles on Ice Lake, 3 on Zen 4, against 1.7 / 2.0 for the ports): 2.4x / 1.5x
  above their port bound. The same holds for float and double `Min` / `Max` (`vminps` / `vmaxps`, 4 cycles), which
  reduce exactly in any order and so vectorize under Precise. Integer sums, counts, `Min` / `Max` and the long pairs
  are 1-cycle chains under a port bound of 1.5 or more: more accumulators cannot help them. M1 candidate 1 is for
  floating-point reductions only.
- **No other loop carries a chain** (the index, 1 cycle): unrolling (candidate 2) can only save the loop's own
  `add` / `cmp` / `jcc`, 1-2 uops of 7-10 in the shortest loops, under 10 % by the model; the rest are port bound.
- **Reloads** (candidate 3): the copy of a whole-vector loop that the runner inlines inside its per-word loop
  broadcasts its constant every iteration (the JIT hoists less there, register pressure): 2.09 against 2.10 cycles,
  nothing to win. But the masked loops of components of several fields show a reload that costs: see the finding.
- **Masked words** (candidate 5): each vector of a masked word builds its lane mask from the word (`shrx`,
  `vpbroadcastd`, `vpandd`, `vpcmpeqd`, `vpmovm2d`) and then tests it as a vector (`vptestmd`, `kortestw`) before the
  body: testing the word's bits for the vector as integers (`shrx`, `test`) moves the skip ahead of the mask and
  drops two uops: 4.39 -> 4.06 c/it (Ice Lake), 4.04 -> 3.22 (Zen 4) on Integrate's masked loop. (Measured slower
  on the worlds, reverted: M2 below.)
- **Finding: a Tier1 runner compiled before a table had holes calls the class-initialization helper per vector.**
  The masked loop of Move2 (and of every component of 2-4 fields) calls `CORINFO_HELP_GET_GCSTATIC_BASE` six times
  per vector and spills its vectors around each call (91 instructions, 6 calls). The runner `World.Masked<P2, V2,
  Move2Job>` reached Tier1 while only the table without holes had run (the flat loop, no permutes), so the class of
  the permutation tables (`Interleaved.Tables32`) was not initialized when the JIT compiled it, and Tier1 code is
  never compiled again: every later masked word pays the check. Any program whose table first has no holes and
  later gets some (a component removed in place, a `[Changed<T>]` filter) runs this code. It is the JIT side of the
  Native AOT item of `backlog.md` (a class-initialization check per iteration).

### M1-M3: what shipped, what was dropped (2026-10-27)

A/B: base = bbd5c8a with this branch's benchmark commit, head = the branch, frozen copies outside the repository;
`--dev` screens first, then the default job, two rounds; every row 0 B. Tables: section "Numbers" below.

**Shipped**

- **M1 candidate 1, two accumulator sets for floating-point reductions** (`SystemGenerator.Paired`,
  `Kernel.Code.Chains`). A float or double `+` (FloatMode.Fast), `Min` or `Max` reduction is the only chain M0 found.
  Before the loop over whole vectors, a loop runs two vectors per iteration into a second set of those accumulators,
  folded into the first after it (`a = a + b`, `Vector.Min(a, b)`); the loop over whole vectors takes the vector left.
  Not in masked words (at most four vectors), nor where a vector may run the method (a bail, a lookup) or a wide
  component, and only while the method stays within the 32 inlined locals (counted with the body twice). Integer
  reductions keep one set (a 1-cycle chain under a port bound of 1.5 or more). Exactness: Min / Max are exact in
  any order (Precise); a float sum is reordered only under FloatMode.Fast, which already reassociates it (D68), and
  the order is fixed by the code's shape, never the thread count (D125). llvm-mca: Energy 4.1 -> 2.1 c/vector (Ice
  Lake-SP), 3.1 -> 2.1 (Zen 4); four sets measured 6.8 c per 4 vectors, no better than two.
- **The Tier1 class-initialization finding, fixed for the JIT** (`Interleaved.InitializeTables`, a module
  initializer running the class constructors of `Interleaved`, `Masks`, `Tables32` and `Tables64`): a runner compiled
  at Tier1 before its table had holes no longer calls `CORINFO_HELP_GET_GCSTATIC_BASE` per vector of a masked word
  (LateHoles: 0 such calls in the three runners' Tier1 code, from 6 per vector in Move2's). Native AOT is not
  changed by it (its preinitialization decides there): the backlog item stays open for AOT.
- **M2 candidate 6, "make it a tag"**: no rule existed. This branch first appended the tag advice to EECS055's reason
  for a bool field; feat-generator-gaps (merged meanwhile) made a bool component of one field a 1-byte lane read as
  a mask (no branch per row) and gave a bool among several fields its own reason ("a vector loop takes a bool in a
  component of its own"), so the advice was dropped at the merge: the method now gets a vector loop or the split, and
  a tag remains the ECS principles' advice for a state few rows hold (the analyzer page's EECS056 example). No new
  rule: no frequency of change can be read from the code, and an `if` on an enum or bool lane is a mask per vector.

**Dropped (with the reason)**

- **M2 candidate 5, the masked skip on the word's bits**: testing `pass >> i << (64 - Count) == 0` before the lane mask
  instead of the vector compare after it drops two uops by llvm-mca (Integrate's masked loop 4.39 -> 4.06 c/it on Ice
  Lake-SP, 4.04 -> 3.22 on Zen 4), but measured slower where the passing rows are many: against the same head with the
  vector test, BattleServer 1.10 / 1.09x (two rounds), WorldSimulation at 100k 1.06 / 0.92, CycleHoles at 100k 1.04 /
  1.11, Holes 0.84 / 0.95, Move2Gaps at 1k 0.85 / 0.85. In real worlds the skip is almost never taken (a tag few
  rows hold leaves most vectors with a passing row), so the integer test is work in front of every vector; the
  model's loop takes the skip's cost as free. Reverted; what would change it: a world of sparse words, where whole
  vectors fail (then a test per word, not per vector, would be the form to try).
- **M1 candidate 2, unrolling loops without a chain**: M0 found no loop-carried chain but the float reductions; the
  rest are port bound, and unrolling saves only the loop's own `add` / `cmp` / `jcc`, 1-2 uops of 7-10 in the shortest
  loops: under 10 % by the model (bar 1.2x), at the cost of every body written twice against the 32 locals.
- **M1 candidate 3, hoisting reloads**: the per-word copy of a whole-vector loop rebroadcasts its constant each
  iteration, 2.09 against 2.10 c/it with it hoisted: nothing to win. The reload that cost was the class-init helper
  (shipped above).
- **M2 candidate 4, branchless scalar rows**: the generator calls the user's method for a scalar row and does not
  rewrite its statements. The JIT's if-conversion keeps a branch whenever profile data says it is biased: on Tier1,
  `if (hp.Value > hp.Max) hp.Value = hp.Max;`, `if (f.On) hp.Value += f.Bonus;` and even the ternary form all compile
  to a `jg` / `jne` with the store in a cold block (listing of a probe, 2026-10-27), which is the right code for
  predictable data. These shapes vectorize anyway (a select per vector) when their fields are lanes; a method that
  stays scalar does so for another reason (a call, a bool among several fields), which no branch rewrite changes.
- **M3 candidate 8, streaming stores** (prototype `Locality/Streams.cs`, `StreamingStoreBenchmark`): a write-only
  column `dst = src * k + c` with non-temporal stores against plain ones. Past the last-level cache (64M floats, two
  256 MB columns against 260 MB) they win 1.47x (49.3 -> 33.4 ms, default job); in the cache (1M floats) they tie
  (271 / 260 us). Not built into the generated loops: it pays only for a column the method writes without reading
  (`p = new P { ... }` from other components), which most passes do not (`p.X += ...` loads the line anyway, and a
  streaming store after that load only evicts it), and only for columns of 64M rows; a pass that writes such a
  column also leaves it out of the cache for the next pass that reads it. What would change it: a world whose
  write-only columns exceed the last-level cache (then a gate like the prefetch gate: `LastLevelCache`, the
  column's bytes, the method's write-only set from Kernel). Recorded in `owner-review.md`.
- **M3 candidate 9, the hardware prefetcher's streams**: a walk reads at most nine columns (the runners' arity, fused
  walks included: a fused walk is one runner over the union of its methods' components) plus the presence and
  change-tick words, under the 32 streams Intel's optimization manual gives its L2 streamer (one per 4 KB page):
  no walk can exceed them, nothing to split. Not measured on Zen 4 or ARM64 (no machine): an item for the M4 if a
  wide fused walk ever shows a drop there.
- **M3 candidate 7, prefetching chases**: merged before this work (item 6 of the backlog), not redone.

#### Numbers (BenchmarkDotNet default job, base / head, two rounds, every row 0 B)

| Benchmark | Rows | Round 1 | | Round 2 | |
|---|---|---|---|---|---|
| Kernel EnergyGenerated (Fast float sum) | 1k | 153.8 / 124.1 ns | 0.81 | 146.3 / 126.0 ns | 0.86 |
| Kernel EnergyGenerated | 100k | 10.21 / 6.92 us | 0.68 | 9.87 / 6.86 us | 0.70 |
| Kernel SpreadGenerated (float Min + Max) | 1k | 348.9 / 284.9 ns | 0.82 | 374.8 / 281.6 ns | 0.75 |
| Kernel SpreadGenerated | 100k | 20.61 / 10.82 us | 0.53 | 20.48 / 10.82 us | 0.53 |
| LateHoles Move2 (masked words, Tier1 before holes) | 1k | 1,429 / 515 ns | 0.36 | 1,509 / 427 ns | 0.28 |
| LateHoles Move2 | 100k | 211.9 / 49.9 us | 0.24 | 133.3 / 55.2 us | 0.41 |
| LateHoles Move3 | 1k | 2,898 / 715 ns | 0.25 | 2,867 / 705 ns | 0.25 |
| LateHoles Move3 | 100k | 316.6 / 91.0 us | 0.29 | 308.6 / 96.6 us | 0.31 |
| LateHoles Step | 1k | 643 / 580 ns | 0.90 | 702 / 686 ns | 0.98 |
| LateHoles Step | 100k | 71.1 / 67.0 us | 0.94 | 75.5 / 64.9 us | 0.86 |
| StreamingStore Plain / NonTemporal (prototype, head) | 1M floats | 288.5 / 263.7 us | | 273.8 / 257.9 us | |
| StreamingStore Plain / NonTemporal (prototype, head) | 64M floats | 49.35 / 34.18 ms | | 48.26 / 34.10 ms | |

The other kernels, both rounds (head / base): Cycle 1.03 / 0.97 (1k), 1.01 / 1.03 (100k); CycleHoles 1.10 / 0.89,
1.05 / 0.97; Clamp 1.18 / 0.96, 1.04 / 0.86; Halves 1.04 / 0.99, 0.93 / 1.02; Holes 1.01 / 0.90, 1.01 / 1.04;
Integrate 1.06 / 1.07, 1.02 / 0.95; Move2Gaps 0.97 / 0.92, 1.03 / 0.96; Move3 1.06 / 1.08, 1.06 / 0.99; Push 1.05 /
1.09, 0.94 / 1.09; Wealth 1.00 / 1.01, 0.97 / 1.04. No kernel slower in both rounds beyond the noise of the job
(+-10 % at 1k rows); Move3 and Push at 1k (1.06-1.09) run the same 7-instruction loop on both sides (llvm-mca over
both Tier1 listings: 2.1 c/it Ice Lake-SP, 1.6 Zen 4): the difference is outside the loop, in the runner's layout.
Energy's 1.43-1.47x misses the plan's bar of 1.5x by a hair and ships (owner, 2026-10-27: a measured gain with nothing
slower ships under its bar); Spread's 1.89x clears it.
The kernel rows above were measured with the masked skip still in (reverted after); the masked loops of Energy and
Spread are not on their hot path (no holes).

The worlds, base / final head (the masked skip reverted), two rounds: WorldSimulation 10k 113.4 / 111.5 us (0.98),
117.1 / 111.6 (0.95); 100k 1,260 / 1,336 us (1.06), 1,241 / 1,312 (1.06); BattleServer 3.88 / 4.08 ms (1.05), 4.08 /
4.26 (1.04); LateHoles as above (Move2 0.33-0.37, Move3 0.22-0.30, Step 0.78-0.93). The WorldSimulation 100k
difference is not the change: its generated code is identical on both sides (generated sources diffed) and a build
of the final head without the module initializer measures the same (1,337 against 1,340 us), while that build
against base, whose only difference for this world is other jobs' generated text in the same assembly, measures 1.05
again (1,262 / 1,329 us): the assembly's layout, not a code path. BattleServer: 1.10 / 0.88 / 1.05 / 1.04 over four
pairs, noise of the job.

Ablations (same head, one change off): the masked skip on the word's bits against the vector test, two rounds:
BattleServer 1.10 / 1.09, WorldSimulation 100k 1.06 / 0.92, 10k 0.99 / 0.95, CycleHoles 100k 1.04 / 1.11, Holes 100k
0.84 / 0.95, Move2Gaps 1k 0.85 / 0.85: reverted. The module initializer against none, on the worlds: 1.03 / 1.00 /
0.94 (WorldSimulation 10k / 100k, BattleServer): neutral where the tables are initialized before Tier1 anyway.
