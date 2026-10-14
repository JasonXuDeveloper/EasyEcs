using System;
using EasyEcs.Core.Components;

namespace EasyEcs.Core.Systems;

/// <summary>
/// Marks a method of a <c>partial</c> system as a per-entity update: every update of the system, after
/// <see cref="SystemBase.OnUpdate"/>, the method runs for each entity matching its component parameters
/// (and the <see cref="WithAttribute{T}"/> / <see cref="WithoutAttribute{T}"/> filters), in declaration order.
/// <para>Parameters: <c>ref</c>/<c>in</c> components (1..9, at least one), optionally <c>int entityId</c>,
/// <c>EntityRef entity</c> and <c>World world</c>.</para>
/// <code>
/// public partial class MovementSystem : SystemBase
/// {
///     [Each, Without&lt;Frozen&gt;]
///     static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
/// </code>
/// EasyEcs.Generator compiles it to an inlined struct job over <see cref="World.ForEach{T1,TJob}"/>: zero allocation.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EachAttribute : Attribute
{
}

/// <summary>The <see cref="EachAttribute"/> method also requires component <typeparamref name="T"/> (not passed in).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : struct, IComponent
{
}

/// <summary>The <see cref="EachAttribute"/> method skips entities having component <typeparamref name="T"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : struct, IComponent
{
}
