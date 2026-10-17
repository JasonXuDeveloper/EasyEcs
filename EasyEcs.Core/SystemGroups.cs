using System;

namespace EasyEcs;

/// <summary>
/// Marker of a system group: systems marked <see cref="UpdateInGroupAttribute{TGroup}"/> run together when
/// <see cref="World.Update{TGroup}"/> is called, independently of the default group run by <see cref="World.Update()"/>.
/// Use groups for different cadences (a fixed-rate simulation tick vs. work that runs whenever input arrives).
/// <code>
/// public struct NetworkGroup : ISystemGroup;
///
/// [UpdateInGroup&lt;NetworkGroup&gt;]
/// public class ReceiveSystem : EcsSystem { ... }
///
/// world.Update&lt;NetworkGroup&gt;();   // whenever packets arrive
/// world.Update();                     // fixed tick: default group
/// </code>
/// A group may carry <see cref="WithAttribute{T}"/>, <see cref="WithoutAttribute{T}"/> and
/// <see cref="WithAnyAttribute{T}"/> filters: the generator adds them to every [ForEach] method of its systems
/// (<c>[Without&lt;Inactive&gt;] public struct Gameplay : ISystemGroup;</c> keeps inactive entities out of the whole
/// group). Queries a system makes in OnUpdate are its own.
/// </summary>
public interface ISystemGroup;

/// <summary>Puts the system (and derived systems) into group <typeparamref name="TGroup"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class UpdateInGroupAttribute<TGroup> : Attribute, ISystemGroupAttribute where TGroup : struct, ISystemGroup
{
    int ISystemGroupAttribute.Index => SystemGroup<TGroup>.Index;
}

internal interface ISystemGroupAttribute
{
    int Index { get; }
}

/// <summary>Index of group <typeparamref name="T"/> in every world (0 is the default group).</summary>
internal static class SystemGroup<T> where T : struct, ISystemGroup
{
    internal static readonly int Index = TypeIndex<ISystemGroup, T>.Value + 1;
}
