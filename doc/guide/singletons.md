# Singletons

[Docs index](../README.md) · Related: [ECS principles](../ecs-principles.md#2-systems-are-stateless) ·
[API reference](../api.md#systems-singletons-maintenance)

A singleton is a struct implementing `ISingletonComponent` with exactly one value per world: a clock, configuration,
counters, a spatial index. It is where world-level state goes, since systems are stateless
([EECS007](analyzer.md#eecs007)). Singletons are per world, not static: two worlds never share them.

```csharp
public record struct Clock(long Tick) : ISingletonComponent;
```

```csharp
world.AddSingleton<Clock>();                      // add (or reset), returns ref T
world.GetSingleton<Clock>().Tick++;               // ref T; throws if it was not added
ref var c = ref world.GetOrAddSingleton<Clock>(); // adds with default if missing
bool has = world.HasSingleton<Clock>();
if (world.TryGetSingleton<Clock>(out var copy)) { /* a copy */ }
world.RemoveSingleton<Clock>();
```

## In systems

A `[ForEach]` method takes a singleton as a `ref` / `in` parameter. It is resolved once per pass (and added if
missing), not once per entity:

```csharp
public record struct Score(int Deaths) : ISingletonComponent;

public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp, ref Score score)   // resolved once per pass
    {
        if (hp.Value > 0) return;
        unit.Set(new Dead());
        score.Deaths++;
    }
}
```

In `OnUpdate`, use the system's `World` property: `World.GetSingleton<Clock>().Tick++`.

## Stable handles

`world.SingletonRef<T>()` returns a `SingletonRef<T>` (adding the singleton if missing). A singleton's storage never
moves, so the handle stays valid and `handle.Value` is a `ref T` with no lookup. Generated `[ForEach]` jobs use it
for singleton parameters. If the singleton is removed, the handle is detached: writes through it no longer reach the
world.
