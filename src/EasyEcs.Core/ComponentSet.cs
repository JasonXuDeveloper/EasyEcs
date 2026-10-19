using System;

namespace EasyEcs;

/// <summary>
/// A set of component types, built once and kept (a <c>static readonly</c> field): the components a save writes
/// (<see cref="World.SaveEntities(System.Buffers.IBufferWriter{byte}, ReadOnlySpan{EntityId}, in ComponentSet)"/>), the
/// record of what persists without the runtime state around it. <c>ComponentSet.Create().With&lt;Bank&gt;().With&lt;Deposits&gt;()</c>;
/// a framework that learns its types at registration adds them by <see cref="Type"/>. Each call returns a new set.
/// </summary>
public readonly struct ComponentSet
{
    private readonly BitMask _bits;

    private ComponentSet(BitMask bits) => _bits = bits;

    /// <summary>The empty set: the start of a chain. O(1).</summary>
    public static ComponentSet Create() => default;

    /// <summary>This set and <typeparamref name="T"/>. O(1).</summary>
    public ComponentSet With<T>() where T : struct, IComponent => Adding(ComponentType<T>.Bit);

    /// <summary>This set and component type <paramref name="type"/> (a struct implementing <see cref="IComponent"/>). Cold: one lookup.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a component type the generator or a world registered.</exception>
    public ComponentSet With(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        int bit = SavedTypes.BitOf(type);
        return bit >= 0 ? Adding(bit) : throw new ArgumentException($"{type} is not a component type of this process", nameof(type));
    }

    /// <summary>Whether the set holds <typeparamref name="T"/>. O(1).</summary>
    public bool Contains<T>() where T : struct, IComponent => Contains(ComponentType<T>.Bit);

    internal bool Contains(int bit) => _bits.HasBit(bit);

    private ComponentSet Adding(int bit)
    {
        var bits = _bits.Clone(); // a copy: sets in fields never change
        bits.SetBit(bit);
        return new ComponentSet(bits);
    }
}
