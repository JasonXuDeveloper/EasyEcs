using System;
using JetBrains.Annotations;

namespace EasyEcs;

/// <summary>
/// Declares a system: a <c>ref struct</c> whose static methods are the logic that runs on a world. A system is never an
/// object (a ref struct cannot be boxed, stored or captured); the world keeps its bookkeeping as data
/// (<see cref="SystemHandle"/>), and the generator writes the code that runs it, called directly by the world.
/// <para>Lifecycle: the <see cref="OnCreateAttribute">[OnCreate]</see> method when it is added
/// (<see cref="World.AddSystem{T}"/>), then every update its <see cref="OnUpdateAttribute">[OnUpdate]</see> method followed
/// by its <see cref="ForEachAttribute">[ForEach]</see> methods in declaration order, and its
/// <see cref="OnDestroyAttribute">[OnDestroy]</see> method when it is removed or the world is disposed. Everything runs
/// synchronously within <see cref="World.Update()"/> (or <see cref="World.Update{TGroup}"/> for systems in a
/// <see cref="UpdateInGroupAttribute{TGroup}">group</see>): on the calling thread, or with
/// <see cref="WorldOptions.ParallelSystems"/> on one of the process's helper threads next to systems whose
/// <see cref="SystemAccess">access</see> does not conflict.</para>
/// <code>
/// [EcsSystem, UpdateAfter&lt;Input&gt;]
/// ref struct Movement
/// {
///     [ForEach] static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
/// }
///
/// world.AddSystem&lt;Movement&gt;();
/// </code>
/// A system declares static members only (state belongs in components and singletons); what several systems share is
/// a generic system (<c>[EcsSystem] ref struct Regen&lt;T&gt;</c>) or a static helper, and what a constructor argument
/// configured is a singleton.
/// </summary>
[AttributeUsage(AttributeTargets.Struct), MeansImplicitUse(ImplicitUseTargetFlags.WithMembers)]
public sealed class EcsSystemAttribute : Attribute;

/// <summary>
/// The static method of an <see cref="EcsSystemAttribute">[EcsSystem]</see> called once when the system is added to a
/// world. It takes the <see cref="World"/>, the <see cref="SystemHandle"/>, both or neither, and returns void; one per
/// system (EECS076).
/// </summary>
[AttributeUsage(AttributeTargets.Method), MeansImplicitUse]
public sealed class OnCreateAttribute : Attribute;

/// <summary>
/// The static method of an <see cref="EcsSystemAttribute">[EcsSystem]</see> called on every run of the system, before
/// its [ForEach] methods. It takes the <see cref="World"/>, the <see cref="SystemHandle"/>, both or neither, and returns
/// void; one per system (EECS076). It may touch anything, so the system never shares a parallel stage
/// (<see cref="SystemAccess.RunAlone"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method), MeansImplicitUse]
public sealed class OnUpdateAttribute : Attribute;

/// <summary>
/// The static method of an <see cref="EcsSystemAttribute">[EcsSystem]</see> called once when the system is removed or
/// the world is disposed. It takes the <see cref="World"/>, the <see cref="SystemHandle"/>, both or neither, and
/// returns void; one per system (EECS076).
/// </summary>
[AttributeUsage(AttributeTargets.Method), MeansImplicitUse]
public sealed class OnDestroyAttribute : Attribute;

/// <summary>
/// Run the system on every <paramref name="interval"/>-th update of its group only: the updates whose count
/// (<see cref="World.DefaultUpdateCount"/>, <see cref="World.UpdateCount{TGroup}"/>) modulo the interval is
/// <see cref="Phase"/>. Systems with the same interval and different phases spread their work over the updates instead
/// of all running on the same one. The generator writes the values into the system's code; the interval is 1 or more
/// and the phase 0 to interval - 1 (EECS083).
/// <code>
/// [EcsSystem, UpdateInterval(4, Phase = 1)]   // updates 1, 5, 9, ...
/// ref struct Regrowth { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class UpdateIntervalAttribute(int interval) : Attribute
{
    /// <summary>Run every N-th update of the system's group (1 = every update).</summary>
    public int Interval { get; } = interval;

    /// <summary>Which of the <see cref="Interval"/> updates the system runs on, 0 to interval - 1. Default 0.</summary>
    public int Phase { get; init; }
}
