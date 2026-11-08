# The owner's hardware runs, after all code changes (internal, not published)

> The owner (2026-11-07): every run that needs hardware this VM does not have is his, on his M4, once every code
> change is done (backlog: "owner, after code"). This file is the script: each command, what it compares, and what
> result changes what. Run from a clone of the work branch's final tip; `bench` below is
> `dotnet run -c Release --project benchmarks/EasyEcs.Benchmark --` (after Finish 1b the paths of `tools/` scripts
> move to `tools/perf/`). Add `--launchCount 6` to any 100k case that differs past its error (page placement,
> `audit-perf`). Send the BenchmarkDotNet reports (`BenchmarkDotNet.Artifacts/results`); the agent that reads them
> records the numbers in the decision records named.
>
> Not in this file: the helpers' queue line and thread pinning (`owner-review.md`: an 8+ core quiet machine).

## 1. Native AOT: the loops' tables, a no-change check (backlog history "Native AOT: no class-initialization check")

What it compares: base `85e0394` against `d5e0227` (feat-aot-tables' tip): the x64 lane masks became literals behind
`!AdvSimd.IsSupported`; ARM64 runs the same `Masks.Narrow`, statics and `fixed` LD/ST before and after, so it should
show no change.

```bash
git worktree add ../ecs-base 85e0394
git worktree add ../ecs-head d5e0227
F="*KernelBenchmark.Move2Generated *KernelBenchmark.Move3Generated *KernelBenchmark.Move4Generated \
*KernelBenchmark.StepGenerated *KernelBenchmark.HolesGenerated *KernelBenchmark.IntegrateGenerated \
*KernelBenchmark.WaveGenerated *KernelBenchmark.ShareGenerated *KernelBenchmark.SeekGenerated \
*KernelBenchmark.GlideGenerated *KernelBenchmark.OptionalGenerated *KernelBenchmark.BrightenGenerated \
*KernelBenchmark.GradeGenerated"
echo '<Project><PropertyGroup><IlcInstructionSet>native</IlcInstructionSet></PropertyGroup></Project>' > ../ilc-native.targets
# in each worktree, alternately, twice; JIT then Native AOT:
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter $F
CustomAfterMicrosoftCommonTargets=$PWD/../ilc-native.targets \
  dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --runtimes nativeaot10.0 --filter $F
```

What changes what: equal within the error -> the item closes. A case slower on head past the error in both rounds ->
the literal arm is not as inert on ARM64 as built; the agent reads the ILC listing of that runner and moves the
literals behind a narrower condition.

## 2. 8g: helpers of another assembly with a hidden static field (UnsafeAccessor), ARM64 and Native AOT

What it compares: nothing against a base; it proves the recorded bodies run on ARM64 (JIT and AOT): a hidden
`static readonly` field read once per run through an `[UnsafeAccessor(StaticField)]` extern.

```bash
dotnet test tests/EasyEcs.UnitTest -c Release --filter "FullyQualifiedName~ForeignBodyTests"
dotnet publish tests/EasyEcs.Aot -c Release -r osx-arm64 && ./tests/EasyEcs.Aot/bin/Release/net10.0/osx-arm64/publish/EasyEcs.Aot
cd docs && bun run demo:build && bun run dev   # open /en/docs/demo: the demo runs (Mono AOT, WebAssembly)
```

What changes what: all pass -> done. A failure under AOT or in the browser -> the generator falls back to the method
for a hidden static on that target (the reason text names it), and the agent records why.

## 3. 8j: switches and patterns as lane masks on ARM64

```bash
bench --filter '*KernelBenchmark.GradeScalar' '*KernelBenchmark.GradeGenerated'
```

What it compares: the generated loop of a switch expression / statement against the method row by row (x64: 0.022-0.133
of scalar). Generated faster at 100k -> done. Slower -> the select chain is gated off on ARM64 (the agent reads the
loop with `tools/perf/jitdisasm.sh`).

## 4. The ARM gate's rejected shape `Brake` (8 words, 3 fields), forced open

Needs the benchmark pair the vector-loop generator lane builds first (backlog). Then:

```bash
bench --filter '*KernelBenchmark.Brake*'
```

What it compares: Brake's wide form with the ARM gate forced open against its scalar form and the gate's own choice.
Forced open faster in both rounds -> the gate loosens for that shape class (the agent changes `Kernel.Wide.cs`' ARM
gate and reruns `ParityTests`); slower or equal -> the gate stays and the record says so.

## 5. Items built after this file was written that name the M4

Run each once its lane has merged (the backlog marks them "owner, after code"):

- `EntityRandom` draws as vector lanes (vector-loop lane, generator sweep 5): NEON has no 64-bit multiply, so the
  lowering is emulated. `bench --filter '*EntityRandomBenchmark*'`: generated slower than scalar on the M4 -> the
  ARM64 gate stays closed for it.
- A uniform table read at a lane index through `TBL` (vector-loop lane, perf P2):
  `bench --filter '*KernelBenchmark.SteerTable*' '*BattleServerBenchmark*'`: TBL slower than scalar -> rows on ARM64.
- mimalloc columns (core lane, `native-columns.md`): `bench --filter '*NativeColumnsBenchmark*' '*SnapshotBenchmark*'
  '*FullCopyBenchmark*' '*BattleServerBenchmark*' '*WorldSimulationBenchmark*'` on base and head (PageFaultDiagnoser
  reads `/proc`: skipped on macOS); anything slower -> the osx-arm64 RID keeps the pinned heap. Plus a Godot export of
  `samples/Godot` to iOS and Android: it starts and its columns come from mimalloc (the log line the fallback writes is
  absent).
- Optional (microarch M3 candidate 9): if a wide fused walk ever drops on the M4 in the bench sync, run
  `bench --filter '*FusionBenchmark*'` with 8M rows to see the prefetcher's streams.

## 6. Not on the M4

- **SVE's PRFB**: the M4 has SVE in streaming mode only. The proof is the CI's `arm64` job (`ubuntu-24.04-arm`), run by
  hand: Actions -> CI -> Run workflow on the work branch. Its step "The prefetch this runner issues" passing (and
  every test) -> done; failing -> `Prefetching.IsSupported` keeps x86 only. The job has never run (backlog Finish 4):
  a wrong runner label fails at once and is fixed then.
- **The prefetch gates on an x86 machine with a 32 MiB last-level cache: not run** (owner, 2026-11-07). The docs pass
  records in the gates' decision record that they were proven only on this VM (260 MiB LLC), and that this run
  (`EASYECS_BENCH_ENTITIES=262144,1048576,4194304,16777216`, the four `*Prefetch*` / `*ChaseGate*` classes, Auto /
  Off / On per form) is what would move a gate.
