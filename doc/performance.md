# Performance guide

## Choosing an iteration API

| Need | Use | Notes |
|---|---|---|
| Per-entity logic of a system | `[ForEach]` method | Generated struct job; same speed as a hand-written `ForEach`. `[With<T>]` / `[Without<T>]` filters. |
| Ad-hoc iteration over 1–9 components | `world.ForEach<…, TJob>(ref job, filter)` | Struct job, specialized and inlined by the JIT. Keep `Execute` small and branch-light. |
| Ad-hoc loop needing the `Entity` | `foreach (var item in world.Query<…>(filter))` | Zero allocation; `item.Entity`, `item.Component1..n` are refs. (`[ForEach]` methods can take an `Entity` parameter too.) |
| React to something that happened (death, level up) | `world.Publish(new Died(..))` + `IEventHandler<Died>` | Synchronous, zero allocation; keeps modules decoupled. |
| "Is there any …?" | `world.Any<…>(filter)` | Early exit, no enumeration. |
| "How many …?" | `world.Count<…>(filter)` | Sums archetype counts. |

## Do

- Mutate through refs: `ref var hp = ref item.Component1;`, `ref var hp = ref entity.Get<Health>();`.
- Spawn with all components at once: `world.CreateEntity(a, b, c)` (placed directly in its archetype); change several
  with `entity.Set(a, b)` / `entity.Remove<A, B>()` (one transition).
- Keep components small (a few fields, well under a 64-byte cache line) and split rarely-used data into its own
  component: iteration only loads the columns it reads.
- Call `CompactArchetypes()` between updates after heavy churn: it removes tombstones and sorts ids so iteration walks
  columns forward.
- Pre-size with `WorldOptions.InitialEntityCapacity` to avoid growth reallocations at runtime.
- Use tag components (empty structs) for states (`Dead`, `Stunned`, `Dirty`) and filter on them; add/remove follow
  cached transition edges.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[ForEach]`).
- Make `[ForEach]` methods that change the entity's archetype idempotent with filters (`Without<Dead>`).
- One world per thread/shard on servers.

## Don't

- Don't modify a copy of a component and forget to write it back.
- Don't keep `ref`s across `await` or in fields; keep the `Entity` handle.
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
