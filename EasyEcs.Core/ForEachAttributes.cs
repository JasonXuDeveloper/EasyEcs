using System;

namespace EasyEcs;

/// <summary>
/// Runs a method of a <c>partial</c> <see cref="EcsSystem"/> for every entity matching its component parameters (and
/// the <see cref="WithAttribute{T}"/> / <see cref="WithoutAttribute{T}"/> / <see cref="WithAnyAttribute{T}"/> filters), each update, after
/// <see cref="EcsSystem.OnUpdate"/>, in declaration order.
/// <para>Parameters: 1..9 components (<c>ref</c> to write, <c>in</c> to read), and optionally <see cref="Entity"/> and
/// <see cref="World"/>. Static and instance methods are supported.</para>
/// <code>
/// public partial class MovementSystem : EcsSystem
/// {
///     [ForEach, Without&lt;Frozen&gt;]
///     static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
/// </code>
/// EasyEcs.Generator compiles it to a struct <see cref="IForEach{T1}"/> job: no delegate, no allocation, inlined.
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
}

/// <summary>The <see cref="ForEachAttribute"/> method also requires component <typeparamref name="T"/> (not passed in).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>The <see cref="ForEachAttribute"/> method skips entities having component <typeparamref name="T"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method requires at least one of the components named by its
/// <c>[WithAny&lt;T&gt;]</c> attributes (not passed in): <c>[ForEach, WithAny&lt;Burning&gt;, WithAny&lt;Poisoned&gt;]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithAnyAttribute<T> : Attribute where T : struct, IComponent;

/// <summary>
/// The <see cref="ForEachAttribute"/> method visits only entities whose <typeparamref name="T"/> was written since its
/// system last ran (<see cref="EcsSystem.LastRunTick"/>; on the first run, every entity having it). Requires
/// <typeparamref name="T"/> like <see cref="WithAttribute{T}"/>; <typeparamref name="T"/> tracks changes
/// (<see cref="ITrackChanges"/>). Several attributes require all of the components to have changed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ChangedAttribute<T> : Attribute where T : struct, IComponent, ITrackChanges;
