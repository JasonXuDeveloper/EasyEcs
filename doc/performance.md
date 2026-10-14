# Performance guide

## Choosing an iteration API

| Need | Use | Notes |
|---|---|---|
| Per-entity work over 1–6 components | `ctx.ForEach<..., TJob>(ref job)` | Fastest. The struct job is specialized and inlined. Keep `Execute` small and branch-light. |
| Need the `Entity` (add/remove components, destroy) or 7–9 components | `foreach (var r in ctx.GroupOf<...>())` | Zero allocation. `r.Entity` and `r.ComponentN` return refs. |
| "Is there any …?" | `ctx.AnyOf<...>()` | Early exit, no enumerator. |
| "How many …?" | `ctx.CountOf<...>()` | Sums archetype alive counts; does not iterate entities. |

Inside `ForEach`, use `entityId` with `ctx.GetEntityById(entityId)` only when you really need the entity. Prefer
putting the data you need in the job's components.

## Do

- Mutate components through refs: `ref var hp = ref r.Component1;`, `ref var c = ref entity.GetComponent<T>().Value;`.
- Add several components at once with `AddComponents<T1, T2, ...>()`, which makes one archetype transition instead
  of several.
- Pre-size: `new Context.Options(initialEntityCapacity: expectedPeak)` avoids growth reallocations at
  runtime.
- Use tag components (empty structs) for states such as `Dead`, `InCombat` or `Dirty`, and query on them. Add and
  remove are cheap thanks to cached transition edges.
- Monitor `GetFragmentationStats()` and call `CompactArchetypes()` between frames when fragmentation is high.
- Keep one context per thread or shard on servers.

## Don't

- Don't copy a component to a local, modify it and forget to write it back. Use `ref`.
- Don't keep `Entity` values across frames. Keep `EntityRef`, which is validated on access.
- Don't store raw entity ids outside the ECS without a version. Ids are recycled.
- Don't use LINQ, closures or `params object[]` in systems that run every frame.
- Don't call `CompactArchetypes()` from inside a query.

## Measuring

Every performance claim needs a number from `EasyEcs.Benchmark`:

```bash
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*' --job short   # quick
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*Churn*'         # full precision, one case
```

`RawArrayWrite` is the theoretical floor: two plain arrays and no ECS. Compare `ForEachWrite` and `QueryWrite`
against it.
