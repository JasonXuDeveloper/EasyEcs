# EasyEcs

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/.github/workflows/dotnet.yml?branch=master)
![license](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)

An Entity-Component-System library

## Why?

Sometimes we may want to remove bidirectional dependencies between our code, and ECS is a good way to do this.

This design pattern offers a clean way to separate the data from the logic, and it's also a good way to improve the
performance.

## What is ECS?

E - Entity

C - Component

S - System

But what really is it?

Well, as a human, we (entities) live in the world (context), and we have some properties (components). Moreover, we have
things to do based on our properties (systems).

## Concepts in EasyEcs

- A `Context` holds several `Entity` instances, some `System` instances and some `SingletonComponent` instances.
- Each `Entity` has some `Component` instances.
- Each `Component` (or `SingletonComponent`) has only data properties.
- Each `System` can filter lots of `Entity` instances in the same `Context` by their components and operate logics on
  them.

## Why it removes bidirectional dependencies?

- Only `System` contains logics and none of them should reference on each other. But we allow one system depends on
  another by specifying `Priority`. (They should only depend on the filtered entities/components)
- `Component` only contains data properties and no logics. (Again, no way to have dependency)
- `Entity` only contains components. (It is really just a container)

## Why is it fast?

- As a `System` can operate on a batch of entities with components, we can well utilize the cache of the MMU.
- We can also easily parallelize the systems to improve the performance, in a multi-core CPU environment.

## Anything special in EasyEcs?

- We have **priority** for `System`, so you can control the order of systems.
- We have **frequency** for `System`, so you can control the frequency of systems being executed.
- We only allow **asynchronous** interfaces for `System` and `Context`, so our ECS should not block the thread (unless
  you screw up).
- We introduce built-in **parallelism** and/or **concurrency** for `System`, so you can easily parallelize your
  systems (for those who are in the same priority) and well utilize the multi-core CPU.
- We have a cool guy who is maintaining this library. (Just kidding)

## What's New in v4.0.0

v4 targets long-running servers: bounded memory under entity churn, cheaper structural changes and
context-scoped state. Targets `net8.0` and `net10.0` (uses `System.Threading.Lock` on .NET 9+).

### Changes

- **Entity id recycling**: destroyed ids are reused (versions are bumped, so stale `EntityRef`/`Entity`
  copies are rejected). Per-entity and per-component arrays now stay bounded by the peak live entity count
  instead of growing forever.
- **Destroy releases component data**: component slots of a destroyed entity are cleared, so managed references
  inside components no longer leak until the id is reused.
- **O(1) archetype membership without hashing**: the context tracks entity -> (archetype, slot); archetypes
  no longer keep a `Dictionary<int,int>` reverse index. Single-component add/remove follows cached archetype
  transition edges instead of hashing a 256-bit tag. New archetypes start small (64 slots instead of 1024 +
  1024-entry dictionary).
- **Per-context singletons**: `AddSingletonComponent<T>` is now scoped to the context (previously a process-wide
  static shared by every context). Added `HasSingletonComponent<T>()` / `RemoveSingletonComponent<T>()`.
- **`ForEach<T1..T6, TJob>(ref TJob job)`**: struct-job iteration (`IForEach<...>`), zero allocation and no
  delegates; the JIT specializes and inlines the job. Faster than `GroupOf` enumeration.
- Query enumerators / `GroupResult` accessors skip redundant bounds checks.

### Benchmarks (`EasyEcs.Benchmark`, N = 10,000, .NET 10, ShortRun)

| Benchmark | v3.1.1 | v4.0.0 | Change |
|---|---:|---:|---:|
| CreateAddDestroy | 5,358 μs | 3,501 μs | -35% |
| Churn (10 rounds create/add/destroy in one context) | 5,247 μs / 3,190 KB | 1,794 μs / 156 KB | -66% time, -95% alloc |
| ToggleComponent (remove + add) | 1,185 μs | 428 μs | -64% |
| QueryIterate (`GroupOf<P,V>`) | 40.6 μs | 43.5 μs | noise |
| ForEachIterate (new, same work as QueryIterate) | - | 32.2 μs | -21% vs GroupOf |
| QueryIterate3 | 16.2 μs | 12.1 μs | -25% |
| SingletonAccess (x1000) | 1.42 μs | 0.89 μs | -37% |

Run them yourself: `dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'`.

See [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md#migration-guide-easyecs-v3x--v40) for upgrade notes.

## What's New in v3.1.0

### New Features

- **`Count` and `IsEmpty`** on `GroupResultEnumerator` — check query result count without iterating:
  ```csharp
  var group = ctx.GroupOf<Health, Position>();
  if (!group.IsEmpty) { /* ... */ }
  int n = group.Count;
  ```
- **`CountOf<T>()` and `AnyOf<T>()`** on `Context` — lightweight alternatives that skip enumerator creation:
  ```csharp
  if (ctx.AnyOf<Health>()) { /* ... */ }
  int count = ctx.CountOf<Health, Position>();
  ```
- **`Tag.SetBits` / `Tag.ClearBits`** — bulk bit operations with `params ReadOnlySpan<int>`:
  ```csharp
  tag.SetBits(bitIdx1, bitIdx2, bitIdx3);
  ```
- **`Tag.ContainsAll`** — SIMD-optimized archetype matching via `Vector256.AndNot`, replacing the previous `(mask & query) == query` pattern (eliminates temporary `Tag` allocation and reduces from 2 SIMD ops to 1).

### Performance Optimizations

- **`GetFragmentationStats()`** is now lock-free (previously locked + iterated all archetypes every call).
- **`Update()`** reuses a cached task buffer instead of per-bucket `ArrayPool.Rent`/`Return`.
- **`ExecuteFrequency`** is cached as a local variable per `Update` call to avoid repeated interface property access.
- **`AggressiveInlining`** added to `GroupOf`, `CountOf`, and `AnyOf` methods.
- All archetype dictionary iterations replaced with index-based `List` traversal for better cache locality.

## Example

Just check out the `EasyEcs.UnitTest` project. I have comments there.

## Documentation

Believe me, one day I will make a website for this and document everything.