using System;

namespace EasyEcs;

/// <summary>
/// A set of component and singleton types, built once and kept (a <c>static readonly</c> field): the components a save
/// writes (<see cref="World.SaveEntitiesWith"/>), the
/// record of what persists without the runtime state around it; or, for a restart, the types a whole-world save leaves
/// out (<see cref="World.SaveWithout"/>) and the singletons a load keeps
/// (<see cref="World.LoadKeeping"/>).
/// <c>ComponentSet.Create().With&lt;Bank&gt;().WithSingleton&lt;Rates&gt;()</c>; a framework that learns its types at
/// registration adds them by <see cref="Type"/>. Each call returns a new set.
/// </summary>
public readonly struct ComponentSet
{
    private readonly BitMask _bits;       // components, by component bit
    private readonly BitMask _singletons; // singletons, by singleton slot

    private ComponentSet(BitMask bits, BitMask singletons)
    {
        _bits = bits;
        _singletons = singletons;
    }

    /// <summary>The empty set: the start of a chain. O(1).</summary>
    public static ComponentSet Create() => default;

    /// <summary>This set and component <typeparamref name="T"/>. O(1).</summary>
    public ComponentSet With<T>() where T : struct, IComponent => new(Adding(in _bits, ComponentType<T>.Bit), _singletons);

    /// <summary>This set and singleton <typeparamref name="T"/>. O(1).</summary>
    public ComponentSet WithSingleton<T>() where T : struct, ISingletonComponent =>
        new(_bits, Adding(in _singletons, SingletonType<T>.Index));

    /// <summary>
    /// This set and <paramref name="type"/>: a component type (a struct implementing <see cref="IComponent"/>) or a
    /// singleton type (a struct implementing <see cref="ISingletonComponent"/>). Cold: one lookup.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="type"/> is not a component or singleton type the generator or a world registered, or it is
    /// both (say which with <see cref="With{T}"/> or <see cref="WithSingleton{T}"/>).
    /// </exception>
    public ComponentSet With(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        int bit = SavedTypes.BitOf(type);
        int slot = SavedTypes.SlotOf(type);
        if (bit >= 0 && slot >= 0)
            throw new ArgumentException($"{type} is both a component and a singleton type: add it with With<T>() or WithSingleton<T>()", nameof(type));
        if (bit >= 0) return new ComponentSet(Adding(in _bits, bit), _singletons);
        if (slot >= 0) return new ComponentSet(_bits, Adding(in _singletons, slot));
        throw new ArgumentException($"{type} is not a component or singleton type of this process", nameof(type));
    }

    /// <summary>Whether the set holds component <typeparamref name="T"/>. O(1).</summary>
    public bool Contains<T>() where T : struct, IComponent => Contains(ComponentType<T>.Bit);

    /// <summary>Whether the set holds singleton <typeparamref name="T"/>. O(1).</summary>
    public bool ContainsSingleton<T>() where T : struct, ISingletonComponent => ContainsSingleton(SingletonType<T>.Index);

    internal bool Contains(int bit) => _bits.HasBit(bit);

    internal bool ContainsSingleton(int slot) => _singletons.HasBit(slot);

    /// <summary>Whether the set holds a component type.</summary>
    internal bool HasComponents => !_bits.IsEmpty;

    /// <summary>Whether the set holds a singleton type.</summary>
    internal bool HasSingletons => !_singletons.IsEmpty;

    private static BitMask Adding(in BitMask mask, int bit)
    {
        var bits = mask.Clone(); // a copy: sets in fields never change
        bits.SetBit(bit);
        return bits;
    }
}
