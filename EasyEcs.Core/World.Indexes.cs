using System;
namespace EasyEcs;

// Value indexes: one per indexed component type (IIndexed<TKey>), created on first use.
public sealed partial class World
{
    // Indexed by TypeIndex<ValueIndexFamily, T>.Value; each slot is a ValueIndex<T, TKey>.
    private IResettable[] _indexes = [];

    private readonly struct ValueIndexFamily;

    internal interface IResettable
    {
        void Reset();
    }

    /// <summary>
    /// The value index of <typeparamref name="T"/>: find entities by <typeparamref name="T"/>'s key, or by the entity a
    /// relation component points at. Created on the first call (cold), O(1) after; keep it in a local or field.
    /// </summary>
    public ValueIndex<T, TKey> Index<T, TKey>() where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>
    {
        int slot = TypeIndex<ValueIndexFamily, T>.Value;
        if (slot < _indexes.Length && _indexes[slot] is ValueIndex<T, TKey> index) return index;
        if (slot >= _indexes.Length) Array.Resize(ref _indexes, Math.Max(slot + 1, _indexes.Length * 2));
        index = new ValueIndex<T, TKey>(this);
        _indexes[slot] = index;
        return index;
    }

    /// <summary>Make every index re-index from scratch on its next lookup (rows were replaced wholesale).</summary>
    private void ResetIndexes()
    {
        foreach (var index in _indexes) index?.Reset();
    }
}
