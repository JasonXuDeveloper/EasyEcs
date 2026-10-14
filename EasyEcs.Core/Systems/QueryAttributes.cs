using System;
using EasyEcs.Core.Components;

namespace EasyEcs.Core.Systems;

/// <summary>
/// Turns a method of a <c>partial</c> system class into a zero-allocation query (EasyEcs.Generator).
/// Parameters: <c>ref</c>/<c>in</c> components (1..9, at least one), optionally <c>int entityId</c>,
/// <c>EntityRef entity</c> and <c>Context context</c>. The generator emits <c>Run{Method}(Context)</c> and, unless the
/// class defines <c>OnExecute</c> itself, an <c>IExecuteSystem.OnExecute</c> running every query in declaration order.
/// <code>
/// public partial class MoveSystem : SystemBase
/// {
///     [Query, Without&lt;Frozen&gt;]
///     static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class QueryAttribute : Attribute
{
}

/// <summary>Query also requires component <typeparamref name="T"/> (not passed to the method).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : struct, IComponent
{
}

/// <summary>Query skips entities having component <typeparamref name="T"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : struct, IComponent
{
}
