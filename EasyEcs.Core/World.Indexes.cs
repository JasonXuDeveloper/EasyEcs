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
        /// <summary>Forget every entry; <paramref name="rebuild"/>: index the rows again on the next lookup (after a restore).</summary>
        void Reset(bool rebuild);

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

    /// <summary>The index of a component type, told of every value written with Set or CreateEntity.</summary>
    internal interface IIndexOf<T> where T : struct, IComponent
    {
        void Put(int id, int version, in T value);
    }

    /// <summary>
    /// Entity <paramref name="id"/> (<paramref name="version"/>) now holds <paramref name="value"/>: re-index it if
    /// <typeparamref name="T"/> is indexed and this world has its index. Folded away for other components.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Indexed<T>(int id, int version, in T value) where T : struct, IComponent
    {
        if (!ComponentType<T>.Indexed) return;
        var byBit = _indexByBit;
        int bit = ComponentType<T>.Bit;
        if (byBit != null && bit < byBit.Length && byBit[bit] is { } index)
            Unsafe.As<IIndexOf<T>>(index).Put(id, version, in value);
    }

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

    /// <summary>Empty every index: <see cref="Restore"/> re-indexes on the next lookup, <see cref="Dispose"/> leaves them empty.</summary>
    private void ResetIndexes(bool rebuild)
    {
        foreach (var index in _indexes) index?.Reset(rebuild);
    }
}
