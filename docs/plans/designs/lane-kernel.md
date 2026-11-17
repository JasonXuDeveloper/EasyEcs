# One body per job over a width type: the lane kernel (package C, ledger L28, L29)

The study and its packages (feat-generated-text-2: C the study, D flat jobs, E today's 128-bit path, F the walk form
and the reduction; owner L33). Packages C and D bring the rest of this file with branch `feat-lane-kernel`; package E
stands alone on main, below.

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
