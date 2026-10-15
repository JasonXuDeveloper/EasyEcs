# Analyzer rules (EECS001-EECS010)

[Docs index](../README.md) · Related: [Systems](systems.md) · [ECS principles](../ecs-principles.md)

The EasyEcs analyzer ships in the package next to the source generator. EECS001-EECS006, EECS009 and EECS010 are errors: the generator
skips invalid input and the analyzer reports it, so "not generated" always means "reported". EECS007-EECS008 are
warnings that enforce the [ECS principles](../ecs-principles.md).

| Id | Severity | Rule |
|---|---|---|
| [EECS001](#eecs001) | Error | A system with `[ForEach]` methods must be `partial` |
| [EECS002](#eecs002) | Error | Invalid `[ForEach]` parameter |
| [EECS003](#eecs003) | Error | A `[ForEach]` method takes 1 to 9 components, or none with a `[With<T>]` |
| [EECS004](#eecs004) | Error | Generic systems and methods are not supported |
| [EECS005](#eecs005) | Error | `[ForEach]` outside an `EcsSystem` |
| [EECS006](#eecs006) | Error | Components and singletons must be structs |
| [EECS007](#eecs007) | Warning | Systems should be stateless |
| [EECS008](#eecs008) | Warning | Systems must not reference systems |
| [EECS009](#eecs009) | Error | A batch `[ForEach]` method takes only spans and singletons |
| [EECS010](#eecs010) | Error | A `[ForEach(Parallel = true)]` method must be able to run on several threads |

## EECS001

**System must be partial.** A system that declares `[ForEach]` methods, and every type containing it, must be
`partial`, because the generator adds the jobs and the `GeneratedUpdate` override to it.

Fix: add `partial` to the class (and to any containing types).

## EECS002

**Invalid `[ForEach]` parameter.** Every parameter must be a `ref` / `in` / `ref readonly` struct implementing
`IComponent` or `ISingletonComponent`, a `Span<T>` / `ReadOnlySpan<T>` of an `IComponent` struct, an `Entity`, or
the `World`.

Fix: pass components and singletons by `ref` (write) or `in` / `ref readonly` (read); remove other parameters and get
what you need from data instead.

## EECS003

**Invalid component count.** A `[ForEach]` method must take between 1 and 9 components (singletons, `Entity` and
`World` do not count), or none when a `[With<T>]` names the component to query (`[ForEach, With<Dead>] static void
Remove(Entity e)`).

Fix: with no component, use `OnUpdate`; with more than 9, move the extra requirements to `[With<T>]` or split the
method.

## EECS004

**Generic systems are not supported.** `[ForEach]` in a generic type, or a generic `[ForEach]` method.

Fix: make the system and the method non-generic; share code through a non-generic base class (its `[ForEach]` methods
run first in derived systems).

## EECS005

**`[ForEach]` outside a system.** The type declaring `[ForEach]` methods does not derive from `EcsSystem`.

Fix: derive the type from `EcsSystem`, or use `world.ForEach` / `world.Query` for ad-hoc iteration.

## EECS006

**ECS data must be a struct.** A class implements `IComponent` or `ISingletonComponent`. Components and singletons
are stored by value. (Interfaces may extend the markers; classes may not implement them.)

Fix: change the class to a struct (`record struct` works well).

## EECS007

**Systems should be stateless.** A system has a mutable instance field or a settable auto-property. State in a
system is invisible to other systems, queries and persistence, and it is lost or duplicated when the system is
removed or re-added.

Fix: move per-entity state into a component and per-world state into a [singleton](singletons.md). `readonly`
fields (dependencies, reusable scratch buffers), `init`-only properties and computed properties are allowed.

## EECS008

**Systems must not reference systems.** A field or auto-property of a system has a system type. Systems that call
each other are coupled: neither can be removed, reordered or tested alone.

Fix: write the data the other system reads (a component or a singleton) and order the two with `Priority`. See
[ECS principles, rule 3](../ecs-principles.md#3-systems-never-reference-systems).

## EECS009

**Batch `[ForEach]` mixes spans and per-entity parameters.** A method that takes component spans receives whole runs
of rows, so a per-entity parameter (a `ref` / `in` component, an `Entity`) has no single value for the call, and the
`World` could change rows while the spans point into them.

Fix: take every component as a span (`Span<T>` to write, `ReadOnlySpan<T>` to read) and only singletons besides; or
write a per-entity method instead.

## EECS010

**`[ForEach(Parallel = true)]` cannot run on several threads.** A parallel method runs chunks of rows on several
threads at once, so it must make no structural change (static, no `World`, its `Entity` used only to read: `Id`,
`Has`, `Get`, ...), be per entity (not a batch method), and write no singleton (`ref` singleton parameters would race
between chunks).

Fix: take singletons as `in`, move structural changes into a sequential system that reacts to data the parallel one
writes, or drop `Parallel`.
