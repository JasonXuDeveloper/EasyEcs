namespace EasyEcs;

/// <summary>
/// The tag of an entity whose <typeparamref name="T"/> is disabled (<see cref="Entity.Disable{T}"/>): the entity keeps
/// the value of <typeparamref name="T"/> but lacks it for queries, <c>Has</c> and <c>Get</c> until
/// <see cref="Entity.Enable{T}"/> gives it back. Query it like any tag (<c>[With&lt;Disabled&lt;Cooldown&gt;&gt;]</c>) to find
/// the entities whose <typeparamref name="T"/> is paused. Change it only through <c>Disable</c> and <c>Enable</c>.
/// </summary>
public struct Disabled<T> : IComponent, IDisabledTag where T : struct, IComponent
{
    readonly ushort IDisabledTag.Component => ComponentType<T>.Bit;
}

/// <summary>
/// A <see cref="Disabled{T}"/> tag: the bit of the component it disables. Lets <c>ComponentType</c> give the tag
/// its component's tag bit (<see cref="ComponentInfo.DisablerOf"/>) without code generic over the tag's own tag.
/// </summary>
internal interface IDisabledTag
{
    ushort Component { get; }
}

/// <summary>The row layout of every <see cref="Disabled{T}"/> column (an empty tag), described without its <c>T</c>.</summary>
internal struct DisabledTag : IComponent;

/// <summary>Bit of <see cref="Disabled{T}"/>, linked to <typeparamref name="T"/>'s info.</summary>
internal static class DisabledType<T> where T : struct, IComponent
{
    internal static readonly ushort Bit = ComponentType<Disabled<T>>.Bit;
}
