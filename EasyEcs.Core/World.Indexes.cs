using System;
using System.Runtime.CompilerServices;
namespace EasyEcs;

// Value indexes: one per indexed component type (IIndexed<TKey>), created on first use.
public sealed partial class World
{
    // Indexed by TypeIndex<ValueIndexFamily, T>.Value; each slot is a ValueIndex<T, TKey>.
    private IValueIndex[] _indexes = [];

    // Indexed by component bit: the index of that component, told when an entity loses it. Null until the first index.
    private IValueIndex[] _indexByBit;

    private readonly struct ValueIndexFamily;

    internal interface IValueIndex
    {
        void Reset();

        /// <summary>Entity <paramref name="id"/> is destroyed or loses the indexed component. Inside the structural gate.</summary>
        void Removed(int id);
    }

    /// <summary>
    /// The value index of <typeparamref name="T"/>: find entities by <typeparamref name="T"/>'s key, or by the entity a
    /// relation component points at. Created on the first call (cold), O(1) after; keep it in a local or field. Throws
    /// <see cref="InvalidOperationException"/> when <typeparamref name="T"/> is already indexed by another key type.
    /// </summary>
    public ValueIndex<T, TKey> Index<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        int slot = TypeIndex<ValueIndexFamily, T>.Value;
        if (slot < _indexes.Length && _indexes[slot] is { } existing)
            return existing as ValueIndex<T, TKey> ?? throw OtherKey(typeof(T), existing);
        if (slot >= _indexes.Length) Array.Resize(ref _indexes, Math.Max(slot + 1, _indexes.Length * 2));
        var index = new ValueIndex<T, TKey>(this);
        _indexes[slot] = index;
        int bit = ComponentType<T>.Bit;
        _indexByBit ??= [];
        if (bit >= _indexByBit.Length) Array.Resize(ref _indexByBit, bit + 1);
        _indexByBit[bit] = index;
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException OtherKey(Type component, IValueIndex existing) =>
        new($"{component.Name} is already indexed by another key type ({existing.GetType().GenericTypeArguments[1].Name}): one index per component.");

    /// <summary>Tell the index of <paramref name="bit"/>, if any, that entity <paramref name="id"/> lost it (cold).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unindex(ushort bit, int id)
    {
        if (bit < _indexByBit.Length) _indexByBit[bit]?.Removed(id);
    }

    /// <summary>Tell the indexes of <paramref name="archetype"/>'s components that entity <paramref name="id"/> is destroyed (cold).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unindex(Archetype archetype, int id)
    {
        var components = archetype.Components;
        for (int i = 0; i < components.Length; i++) Unindex(components[i], id);
    }

    /// <summary>Make every index re-index from scratch on its next lookup (rows replaced by Restore, or released by Dispose).</summary>
    private void ResetIndexes()
    {
        foreach (var index in _indexes) index?.Reset();
    }
}
