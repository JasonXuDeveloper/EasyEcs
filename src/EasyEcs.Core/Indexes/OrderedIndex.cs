using System;

namespace EasyEcs;

/// <summary>
/// Ordered walks of a value index whose key is comparable (a score, a deadline, a level): ranges and sorted iteration
/// for leaderboards, timers and matchmaking brackets. The first walk sorts the keys once; the index then keeps them
/// sorted as keys appear and disappear (O(log keys) plus a block move each).
/// </summary>
public static class OrderedIndexExtensions
{
    extension<T, TKey>(ValueIndex<T, TKey> index)
        where T : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey>, IComparable<TKey>
    {
        /// <summary>The entities whose key is between <paramref name="min"/> and <paramref name="max"/> (inclusive), by ascending key.</summary>
        public ValueIndex<T, TKey>.Ordered Range(TKey min, TKey max) => index.Walk(min, max, bounded: true, descending: false);

        /// <summary>The entities whose key is between <paramref name="min"/> and <paramref name="max"/> (inclusive), by descending key.</summary>
        public ValueIndex<T, TKey>.Ordered RangeDescending(TKey min, TKey max) => index.Walk(min, max, bounded: true, descending: true);

        /// <summary>Every indexed entity by ascending key.</summary>
        public ValueIndex<T, TKey>.Ordered Ascending() => index.Walk(default!, default!, bounded: false, descending: false);

        /// <summary>Every indexed entity by descending key (the top of a leaderboard first).</summary>
        public ValueIndex<T, TKey>.Ordered Descending() => index.Walk(default!, default!, bounded: false, descending: true);
    }
}
