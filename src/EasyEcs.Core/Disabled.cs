namespace EasyEcs;

/// <summary>
/// The tag of an entity whose <typeparamref name="T"/> is disabled (<see cref="Entity.Disable{T}"/>): the entity keeps
/// the value of <typeparamref name="T"/> but lacks it for queries, <c>Has</c> and <c>Get</c> until
/// <see cref="Entity.Enable{T}"/> gives it back. Query it like any tag (<c>[With&lt;Disabled&lt;Cooldown&gt;&gt;]</c>) to find
/// the entities whose <typeparamref name="T"/> is paused. Change it only through <c>Disable</c> and <c>Enable</c>.
/// </summary>
public struct Disabled<T> : IComponent where T : struct, IComponent;

/// <summary>Bit of <see cref="Disabled{T}"/>, linked to <typeparamref name="T"/>'s info the first time it is used.</summary>
internal static class DisabledType<T> where T : struct, IComponent
{
    internal static readonly ushort Bit = Link();

    private static ushort Link()
    {
        ushort bit = ComponentType<Disabled<T>>.Bit;
        ComponentInfo.LinkDisabled(ComponentType<T>.Bit, bit);
        return bit;
    }
}
