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
  drops two uops: 4.39 -> 4.06 c/it (Ice Lake), 4.04 -> 3.22 (Zen 4) on Integrate's masked loop.
- **Finding: a Tier1 runner compiled before a table had holes calls the class-initialization helper per vector.**
  The masked loop of Move2 (and of every component of 2-4 fields) calls `CORINFO_HELP_GET_GCSTATIC_BASE` six times
  per vector and spills its vectors around each call (91 instructions, 6 calls). The runner `World.Masked<P2, V2,
  Move2Job>` reached Tier1 while only the table without holes had run (the flat loop, no permutes), so the class of
  the permutation tables (`Interleaved.Tables32`) was not initialized when the JIT compiled it, and Tier1 code is
  never compiled again: every later masked word pays the check. Any program whose table first has no holes and
  later gets some (a component removed in place, a `[Changed<T>]` filter) runs this code. It is the JIT side of the
  Native AOT item of `backlog.md` (a class-initialization check per iteration).
