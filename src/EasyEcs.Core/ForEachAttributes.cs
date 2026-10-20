using System;

namespace EasyEcs;

/// <summary>
/// Runs a method of an <see cref="EcsSystem"/> (or a static method of any other type, through a generated entry point) for every entity matching its component parameters (and
/// the <see cref="WithAttribute{T}"/> / <see cref="WithoutAttribute{T}"/> / <see cref="WithAnyAttribute{T}"/> filters), each update, after
/// <see cref="EcsSystem.OnUpdate"/>, in declaration order.
/// <para>Parameters: 1..9 components (<c>ref</c> to write, <c>in</c> to read, <see cref="Mut{T}"/> to write some rows; or <c>Span&lt;T&gt;</c> /
/// <c>ReadOnlySpan&lt;T&gt;</c> for a batch method that receives runs of rows), singletons (<c>ref</c> / <c>in</c>), and
/// optionally <see cref="Entity"/> and <see cref="World"/>. Static and instance methods are supported.</para>
/// <code>
/// public class MovementSystem : EcsSystem
/// {
///     [ForEach, Without&lt;Frozen&gt;]
///     static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
/// </code>
/// EasyEcs.Generator compiles it to a struct job (<see cref="IForEach{T1}"/>, <see cref="IForEachEntity{T1}"/> or
/// <see cref="IForEachBatch{T1}"/>): no delegate, no allocation, inlined.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ForEachAttribute : Attribute
{
    /// <summary>
    /// Run chunks of the matching rows on several threads (<c>World.ForEachParallel</c>). The method must make no
    /// structural change, run per entity and write no singleton (EECS010); it pays off for heavy per-row work over tens
    /// of thousands of rows. Default false.
    /// </summary>
    public bool Parallel { get; init; }

    /// <summary>
    /// Spread the method's rows over N runs of its system: each run visits one slice, about 1 / N of the rows of every
    /// matching table (slice <see cref="EcsSystem.RunCount"/> mod N, <see cref="QueryFilter.Slice"/>), so every row is
    /// visited once per N runs and a system over many entities costs about 1 / N per update instead of a spike on one.
    /// With <see cref="EcsSystem.UpdateInterval"/> M, a row is visited once per N * M updates. Rows that move between
    /// runs (swap-removes, structural changes, tables that grow or shrink) may be skipped or visited twice in one cycle.
    /// Works per entity, in batches and with <see cref="Parallel"/> (the slice is cut into chunks). Not with
    /// <c>[Changed&lt;T&gt;]</c> / <c>[Removed&lt;T&gt;]</c> (EECS035): the rows of the other slices would lose the
    /// changes made since the system last ran. Default 1: every row on every run.
    /// </summary>
    public int Slices { get; init; } = 1;
}

/// <summary>
/// The <see cref="ForEachAttribute"/> method also requires component <typeparamref name="T"/> (not passed in). On a
/// system group (an <see cref="ISystemGroup"/> struct), every [ForEach] method of the group's systems does.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method skips entities having component <typeparamref name="T"/>. On a system
/// group (an <see cref="ISystemGroup"/> struct), every [ForEach] method of the group's systems does:
/// <code>[Without&lt;Inactive&gt;] public struct Gameplay : ISystemGroup;</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method requires at least one of the components named by its
/// <c>[WithAny&lt;T&gt;]</c> attributes (not passed in): <c>[ForEach, WithAny&lt;Burning&gt;, WithAny&lt;Poisoned&gt;]</c>.
/// On a system group, every [ForEach] method of the group's systems requires one of the group's.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithAnyAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method visits only entities whose <typeparamref name="T"/> was written since its
/// system last ran (<see cref="EcsSystem.LastRunTick"/>; on the first run, every entity having it). Requires
/// <typeparamref name="T"/> like <see cref="WithAttribute{T}"/>; <typeparamref name="T"/> tracks changes
/// (<see cref="ITrackChanges"/>). Several attributes require all of the components to have changed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ChangedAttribute<T> : Attribute where T : struct, IComponent, ITrackChanges;

/// <summary>
/// The <see cref="ForEachAttribute"/> method visits only entities that lost <typeparamref name="T"/> since its system
/// last ran (<see cref="EcsSystem.LastRunTick"/>) and have not got it back: replication sends the removal, a cleanup
/// releases what the component stood for. Removing a component keeps the entity's row, so the method can read the
/// entity's other components; destroyed entities are not visited. <typeparamref name="T"/> tracks changes
/// (<see cref="ITrackChanges"/>) and is not a parameter (the entity no longer has it, EECS016).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RemovedAttribute<T> : Attribute where T : struct, IComponent, ITrackChanges;
