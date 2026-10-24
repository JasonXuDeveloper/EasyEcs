using System;

namespace EasyEcs;

/// <summary>
/// Marker of a system group: systems marked <see cref="UpdateInGroupAttribute{TGroup}"/> run together when
/// <see cref="World.Update{TGroup}"/> is called, independently of the default group run by <see cref="World.Update()"/>.
/// Use groups for different cadences (a fixed-rate simulation tick vs. work that runs whenever input arrives).
/// <code>
/// public struct NetworkGroup : ISystemGroup;
///
/// [EcsSystem, UpdateInGroup&lt;NetworkGroup&gt;]
/// ref struct Receive { ... }
///
/// world.Update&lt;NetworkGroup&gt;();   // whenever packets arrive
/// world.Update();                     // fixed tick: default group
/// </code>
/// A group may carry <see cref="WithAttribute{T}"/>, <see cref="WithoutAttribute{T}"/> and
/// <c>[WithAny&lt;T1, T2&gt;]</c> filters: the generator adds them to every [ForEach] method of its systems
/// (<c>[Without&lt;Inactive&gt;] public struct Gameplay : ISystemGroup;</c> keeps inactive entities out of the whole
/// group), and so may the interfaces it implements: <see cref="ISystemGroupWith{T}"/>, <see cref="ISystemGroupWithout{T}"/>.
/// Queries a system makes in its <c>OnUpdate</c> method are its own.
/// </summary>
public interface ISystemGroup;

/// <summary>
/// A system group whose [ForEach] methods visit only entities with <typeparamref name="T"/>, as
/// <see cref="WithAttribute{T}"/> on the group would. An interface can name its own type parameter where an attribute
/// cannot, so a framework's generic group interface carries the filter for every group that implements it:
/// <c>interface IActor&lt;TSelf&gt; : ISystemGroupWith&lt;In&lt;TSelf&gt;&gt;</c>, then
/// <c>struct Map : IActor&lt;Map&gt;</c> filters its systems to <c>In&lt;Map&gt;</c>. Read by the generator: no cost at
/// run time.
/// </summary>
public interface ISystemGroupWith<T> : ISystemGroup where T : struct, IComponent;

/// <summary>
/// A system group whose [ForEach] methods skip entities with <typeparamref name="T"/>, as
/// <see cref="WithoutAttribute{T}"/> on the group would (see <see cref="ISystemGroupWith{T}"/>).
/// </summary>
public interface ISystemGroupWithout<T> : ISystemGroup where T : struct, IComponent;

/// <summary>Puts the system into group <typeparamref name="TGroup"/>.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class UpdateInGroupAttribute<TGroup> : Attribute, ISystemGroupAttribute where TGroup : struct, ISystemGroup
{
    int ISystemGroupAttribute.Index => SystemGroup<TGroup>.Index;
    string ISystemGroupAttribute.Name => ScheduleInfo.NameOf(typeof(TGroup));
}

internal interface ISystemGroupAttribute
{
    int Index { get; }
    string Name { get; } // of the group type, for World.DescribeSchedule
}

/// <summary>Index of group <typeparamref name="T"/> in every world (0 is the default group).</summary>
internal static class SystemGroup<T> where T : struct, ISystemGroup
{
    internal static readonly int Index = TypeIndex<ISystemGroup, T>.Value + 1;
}
