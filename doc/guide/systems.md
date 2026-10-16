# Systems, `[ForEach]` and groups

[Docs index](../README.md) · Related: [ECS principles](../ecs-principles.md) · [Analyzer rules](analyzer.md) ·
[Threading](threading.md) · [API reference](../api.md#systems-singletons-maintenance)

A system is behavior with one responsibility. It derives from `EcsSystem`, keeps no state (state is in components
and singletons) and never references another system.

## At a glance

| | |
|---|---|
| **Use it for** | All per-tick behavior: one responsibility per system, one `[ForEach]` method per loop, groups for different cadences. |
| **Avoid** | State in systems (EECS007), systems calling systems (EECS008), an `Entity` or singleton parameter the method does not need. |
| **Cost** | A `[ForEach]` method compiles to a struct job the JIT inlines into the row loop; per system and update three virtual calls plus one query-cache lookup per method. |

## Lifecycle

| Hook / member | When |
|---|---|
| `OnCreate()` | once, when the system is added (`world.AddSystem<T>()` / `AddSystem(system)`) |
| `OnUpdate()` | every update of its group, before its `[ForEach]` methods |
| `[ForEach]` methods | every update, after `OnUpdate`, in source order (base class first) |
| `OnDestroy()` | once, on `world.RemoveSystem<T>()` or `world.Dispose()` (reverse order) |
| `World` | the owning world, set before `OnCreate` |
| `Priority` (override) | execution order: lower runs first; default 0 |
| `UpdateInterval` (override) | run every N-th update of its group (when the group's update count is a multiple of N, so first on the N-th update; a system added later follows the group's count); default 1 |
| `Enabled` | disabled systems are skipped by `Update()` and `Update<TGroup>()` |

`AddSystem<T>()` returns the system; `GetSystem<T>()` finds one (for tooling and tests, not for other systems:
see [EECS008](analyzer.md#eecs008)). `world.Systems` lists every system (a new list: for tooling), and
`world.UpdateCount` counts the default group's updates so far.

```csharp
world.AddSystem(new BurnSystem());                    // an instance you built yourself
var found = world.GetSystem<BurnSystem>();
foreach (var system in world.Systems) Console.WriteLine($"{system.GetType().Name}: {system.Stats.Runs}");
world.RemoveSystem<BurnSystem>();                     // OnDestroy runs
long updates = world.UpdateCount;
```

## One update

```mermaid
sequenceDiagram
    participant U as world.Update()
    participant S as each system (ascending Priority)
    U->>S: skip if disabled or UpdateInterval not due
    S->>S: OnUpdate()
    S->>S: [ForEach] methods, in source order (base class first)
    S-->>S: structural changes apply immediately: later systems see them
```

Systems are sorted by `Priority`, registration order breaking ties. By default everything runs on the thread that
calls `Update()`. With `WorldOptions.Parallel`, systems of one priority whose generated access does not conflict
(`ref` = write, `in` = read) run at the same time; the result is the same as running them in order (see
[Threading](threading.md#parallel-systems)).

## `[ForEach]` methods

A `[ForEach]` method is declared in a `partial` class that derives from `EcsSystem`. It can be static or an instance
method, and the generator compiles it to a struct job with a static filter.

| Parameter | Meaning |
|---|---|
| `ref T` / `in T` / `ref readonly T` (`T : IComponent`) | component data, 1-9 of them (`ref` to write; `in` or `ref readonly` to read) |
| `ref T` / `in T` / `ref readonly T` (`T : ISingletonComponent`) | the world's singleton, resolved once per pass (added if missing) |
| `Entity entity` | handle of the current entity (structural changes, destroy) |
| `World world` | the world (useful in static methods) |
| `Span<T>` / `ReadOnlySpan<T>` (`T : IComponent`) | a batch method: whole runs of matching rows at once (see below) |

| Attribute | Meaning |
|---|---|
| `[ForEach]` | run the method for every matching entity |
| `[With<T>]` | the entity must have `T`, which is not passed in; repeatable. With no component parameter, the first `[With<T>]` (else the first `[Changed<T>]`) is the queried component (`[ForEach, With<Dead>] static void Remove(Entity e)`) |
| `[Without<T>]` | the entity must not have `T`; repeatable |
| `[WithAny<T>]` | the entity must have at least one of the components named by the method's `[WithAny<T>]` attributes |
| `[Changed<T>]` | the entity's `T` (`ITrackChanges`) was written since the system last ran ([change tracking](change-tracking.md)); requires `T` like `[With<T>]` |

```csharp
public record struct Burning(int Ticks, int DamagePerTick) : IComponent;
public struct Player : IComponent;      // tag
public record struct BurnTotals(int Damage) : ISingletonComponent;

public partial class BurnSystem : EcsSystem
{
    [ForEach, With<Player>, Without<Dead>]   // players only (Player is not passed in), living only
    static void Burn(ref Health hp, in Burning burning, ref BurnTotals totals)   // two components + a singleton
    {
        hp.Value -= burning.DamagePerTick;
        totals.Damage += burning.DamagePerTick;
    }
}
```

A static method that takes no `World` and uses its `Entity` (if any) only to read (`Id`, `Version`, `IsAlive`, `Has`,
`Get`, `TryGet`) cannot change structure, so its job skips the bookkeeping that structural changes need and walks
each table with no per-entity check.

### Batch methods

A method whose component parameters are spans receives runs of consecutive matching rows: element `i` of every span
belongs to the same entity, and a table where every row matches is a single call. Spans let the method use SIMD.
Its other parameters can only be singletons ([EECS009](analyzer.md#eecs009)), and it must not make structural changes.

```csharp
public partial class DriftSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Drift(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i].X += velocities[i].X;
            positions[i].Y += velocities[i].Y;
        }
    }
}
```

`[ForEach]` methods run in source order, and derived systems run the base class's `[ForEach]` methods first. Keep a
system's `[ForEach]` methods in one file: across the files of a `partial` class, the order is by position within each
file, which is rarely what you mean. Mistakes (missing `partial`, an invalid parameter, zero components without a
`[With<T>]` or `[Changed<T>]` or more than 9, generic systems, `[ForEach]` outside a system) are compile errors: see
[Analyzer rules](analyzer.md).

Methods may change structure (create, destroy, `Set`, `Remove`): changes apply at once and each entity is visited at
most once per pass. See [Structural changes during iteration](structural-changes.md).

### Parallel methods

`[ForEach(Parallel = true)]` cuts the matching rows into chunks of `World.ChunkRows` (16,384) rows and runs them on the
calling thread plus up to `WorldOptions.MaxDegreeOfParallelism - 1` thread-pool threads. It is for heavy per-entity
work (path finding, steering, AI scoring) over tens of thousands of entities; for light work the hand-off costs more
than it saves.

```csharp
public partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true), Without<Dead>]
    static void Steer(ref Velocity velocity, in Position position, in Clock clock)
    {
        velocity = new Velocity(MathF.Cos(clock.Tick + position.X), MathF.Sin(clock.Tick + position.Y));
    }
}
```

The method must be able to run on several threads at once (the analyzer checks it, [EECS010](analyzer.md#eecs010)):
no structural change, per entity (not a batch method), and singletons only as `in`. It also must not write what
another row's run reads, such as another entity through `Get`. Fewer than two chunks of rows run on the calling
thread. It allocates nothing after its first update. Ad-hoc code calls `world.ForEachParallel` /
`world.ForEachEntityParallel` with an `INoStructuralChanges` job.

## System groups

`world.Update()` runs the default group. A system marked `[UpdateInGroup<TGroup>]` (where `TGroup` is a
`struct : ISystemGroup`) runs only when `world.Update<TGroup>()` is called. Use groups for different cadences; a
server can run input handling on every wake-up and the simulation at a fixed tick:

```csharp
public struct InputGroup : ISystemGroup;

[UpdateInGroup<InputGroup>]
public partial class ApplyInputSystem : EcsSystem { /* [ForEach] methods */ }
```

```csharp
world.Update<InputGroup>();   // whenever input arrives
world.Update();               // fixed tick
```

`Priority`, `UpdateInterval` and `Enabled` apply within each group, and the attribute is inherited. Updating a group
with no systems is a no-op.

## Measuring systems

With `WorldOptions.MeasureSystems`, every run of a system (its `OnUpdate` plus its `[ForEach]` methods) is timed, and
`system.Stats` holds `Runs`, `Last`, `Total` and `Average`. It costs two timestamp reads per system run and nothing
when off. Read it between updates, for example from a diagnostics endpoint:

```csharp
var world = new World(new WorldOptions { MeasureSystems = true });
world.AddSystem<BurnSystem>();
world.Update();
var burn = world.GetSystem<BurnSystem>().Stats;
Console.WriteLine($"burn: {burn.Last.TotalMicroseconds} μs (average {burn.Average.TotalMicroseconds} μs)");
```

## Errors

Subscribe to `world.SystemError` (`Action<EcsSystem, Exception>`) to handle exceptions thrown by systems (including
`OnDestroy` during `Dispose`); the update continues with the next system. Without a subscriber the exception
propagates out of `Update()`. A system that failed still counts as run: a `[Changed<T>]` method's `LastRunTick` moves on, so
the rows written before the failed run are not delivered again.

```csharp
world.SystemError += static (system, error) =>
{
    Console.Error.WriteLine($"{system.GetType().Name} failed: {error.Message}");
    system.Enabled = false;                           // switch it off; the rest of the tick goes on
};
```

`AddSystem` throws `InvalidOperationException` for a system that already belongs to a world, and `Update()` after
`Dispose()` throws `ObjectDisposedException`.
