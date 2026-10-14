# Performance guide

## Choosing an iteration API

| Need | Use | Notes |
|---|---|---|
| Per-entity logic of a system | `[Each]` method | Generated struct job; same speed as a hand-written `ForEach`. `[With<T>]` / `[Without<T>]` filters. |
| Ad-hoc iteration over 1–9 components | `world.ForEach<…, TJob>(ref job, filter)` | Struct job, specialized and inlined by the JIT. Keep `Execute` small and branch-light. |
| Need the `Entity` (structural changes, destroy) in a loop | `foreach (var item in world.Query<…>(filter))` | Zero allocation; `item.Entity`, `item.Component1..n` are refs. |
| "Is there any …?" | `world.Any<…>(filter)` | Early exit, no enumeration. |
| "How many …?" | `world.Count<…>(filter)` | Sums archetype counts. |

## Do

- Mutate through refs: `ref var hp = ref item.Component1;`, `ref var c = ref entity.GetComponent<T>().Value;`.
- Add several components at once with `AddComponents<T1, T2, …>()` (one archetype transition).
- Pre-size with `WorldOptions.InitialEntityCapacity` to avoid growth reallocations at runtime.
- Use tag components (empty structs) for states (`Dead`, `Stunned`, `Dirty`) and filter on them; add/remove follow
  cached transition edges.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[Each]`).
- Monitor `GetFragmentationStats()` and call `CompactArchetypes()` between frames when fragmentation is high.
- One world per thread/shard on servers.

## Don't

- Don't modify a copy of a component and forget to write it back.
- Don't keep `ref`s across `await` or in fields; keep `EntityRef` / `ComponentRef<T>` handles.
- Don't store raw entity ids without their version: ids are recycled.
- Don't write lambdas that capture locals in methods that run every update: the closure is allocated at method entry
  even when the lambda's branch is not taken (this exact bug was caught in `World.Update` by `AllocationTests`).
- Don't use LINQ, `params object[]` or string formatting in per-update code.

## Measuring

Performance numbers come from BenchmarkDotNet only:

```bash
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*World*'         # headline: realistic server tick
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*' --job short   # everything, quick
```

`WorldSimulationBenchmark` / `ShardedWorldBenchmark` model a busy MMO-style world and must stay at 0 B allocated per
tick. `MicroBenchmarks.RawArrayWrite` is the no-ECS floor. `AllocationTests` (unit tests) guard the zero-allocation
contract on every test run.
