using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
namespace EasyEcs;

/// <summary>
/// Extra query constraints: components that must be present without being read (<see cref="With{T}"/>) or must be
/// absent (<see cref="Without{T}"/>). Build once and keep it (e.g. in a static readonly field):
/// <code>static readonly QueryFilter Alive = QueryFilter.Create().Without&lt;Dead&gt;();</code>
/// </summary>
public readonly struct QueryFilter
{
    internal readonly Tag All;
    internal readonly Tag None;

    private QueryFilter(in Tag all, in Tag none)
    {
        All = all;
        None = none;
    }

    public static QueryFilter Create() => default;

    /// <summary>Require <typeparamref name="T"/> to be present (not passed to the query callback).</summary>
    public QueryFilter With<T>() where T : struct, IComponent
    {
        var all = All.Clone();
        all.SetBit(ComponentType<T>.Bit);
        return new QueryFilter(in all, in None);
    }

    /// <summary>Require <typeparamref name="T"/> to be absent.</summary>
    public QueryFilter Without<T>() where T : struct, IComponent
    {
        var none = None.Clone();
        none.SetBit(ComponentType<T>.Bit);
        return new QueryFilter(in All, in none);
    }
}

/// <summary>Cache key of a query: required bits and excluded bits.</summary>
internal readonly struct QueryKey : IEquatable<QueryKey>
{
    private readonly Tag _all;
    private readonly Tag _none;

    // Lookup keys of queries on component bits >= 256 keep their overflow here instead of allocating it per query.
    [ThreadStatic] private static Vector256<long>[] _scratch;

    private QueryKey(in Tag all, in Tag none)
    {
        _all = all;
        _none = none;
    }

    /// <summary>
    /// Key to look a query up with: allocation-free, but it shares this thread's scratch buffer, so it is valid only
    /// until the next lookup on the thread. Store <see cref="Detached"/> instead.
    /// </summary>
    public static QueryKey ForLookup(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        // Common case: every bit fits the inline 256 bits, so a plain copy allocates nothing.
        if (!filter.All.HasOverflow)
        {
            bool inline = true;
            for (int i = 0; i < bits.Length; i++) inline &= bits[i] < 256;
            if (inline)
            {
                var all = filter.All;
                for (int i = 0; i < bits.Length; i++) all.SetBit(bits[i]);
                return new QueryKey(in all, in filter.None);
            }
        }

        return ForLookupWithOverflow(bits, in filter);
    }

    // Cold path kept out of line: inlining the thread-static access into ForEach measurably slowed its entity loop.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QueryKey ForLookupWithOverflow(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        int maxBit = 0;
        for (int i = 0; i < bits.Length; i++) maxBit = Math.Max(maxBit, bits[i]);
        var all = filter.All.CopyWithScratch(maxBit, ref _scratch);
        for (int i = 0; i < bits.Length; i++) all.SetBit(bits[i]);
        return new QueryKey(in all, in filter.None);
    }

    /// <summary>A copy that owns its storage (cache entries).</summary>
    public QueryKey Detached() => new(_all.Clone(), _none);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(in Tag archetypeMask) => archetypeMask.ContainsAll(in _all) && !archetypeMask.Intersects(in _none);

    public bool Equals(QueryKey other) => _all.Equals(in other._all) && _none.Equals(in other._none);

    public override bool Equals(object obj) => obj is QueryKey other && Equals(other);

    public override int GetHashCode() => _all.GetHashCode() * 31 + _none.GetHashCode();
}

/// <summary>
/// Walks the alive entity ids of an archetype snapshot. Re-reads archetype storage on every step, so it stays valid
/// while entities are created, destroyed or change components during iteration.
/// </summary>
internal struct ArchetypeCursor
{
    private readonly Archetype[] _archetypes;
    private int _archetype;
    private int _slot;

    public ArchetypeCursor(Archetype[] archetypes)
    {
        _archetypes = archetypes;
        _archetype = 0;
        _slot = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext(out int entityId)
    {
        var archetypes = _archetypes;
        while (_archetype < archetypes.Length)
        {
            var archetype = archetypes[_archetype];
            while (_slot < archetype.Count)
            {
                int id = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds), _slot++);
                if (id >= 0)
                {
                    entityId = id;
                    return true;
                }
            }

            _archetype++;
            _slot = 0;
        }

        entityId = -1;
        return false;
    }

    public readonly int AliveCount => CountAlive(_archetypes);

    public readonly bool AnyAlive() => AnyAlive(_archetypes);

    public static int CountAlive(Archetype[] archetypes)
    {
        int count = 0;
        for (int i = 0; i < archetypes.Length; i++)
            count += archetypes[i].AliveCount;
        return count;
    }

    public static bool AnyAlive(Archetype[] archetypes)
    {
        for (int i = 0; i < archetypes.Length; i++)
            if (archetypes[i].AliveCount > 0)
                return true;
        return false;
    }
}
