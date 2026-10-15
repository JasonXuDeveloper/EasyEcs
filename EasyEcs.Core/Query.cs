using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
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
    internal readonly Tag Any;

    private QueryFilter(in Tag all, in Tag none, in Tag any)
    {
        All = all;
        None = none;
        Any = any;
    }

    public static QueryFilter Create() => default;

    /// <summary>Require <typeparamref name="T"/> to be present (not passed to the query callback).</summary>
    public QueryFilter With<T>() where T : struct, IComponent
    {
        var all = All.Clone();
        all.SetBit(ComponentType<T>.Bit);
        return new QueryFilter(in all, in None, in Any);
    }

    /// <summary>Require <typeparamref name="T"/> to be absent.</summary>
    public QueryFilter Without<T>() where T : struct, IComponent
    {
        var none = None.Clone();
        none.SetBit(ComponentType<T>.Bit);
        return new QueryFilter(in All, in none, in Any);
    }

    /// <summary>
    /// Require at least one of the components named by <c>WithAny</c> calls: <c>WithAny&lt;Burning&gt;().WithAny&lt;Poisoned&gt;()</c>
    /// matches entities with Burning, Poisoned or both.
    /// </summary>
    public QueryFilter WithAny<T>() where T : struct, IComponent
    {
        var any = Any.Clone();
        any.SetBit(ComponentType<T>.Bit);
        return new QueryFilter(in All, in None, in any);
    }
}

/// <summary>Cache key of a query: required bits, excluded bits and the bits of which one is required.</summary>
internal readonly struct QueryKey : IEquatable<QueryKey>
{
    private readonly Tag _all;
    private readonly Tag _none;
    private readonly Tag _any;

    // Detached keys only (not part of equality): the required and excluded bits as arrays, for the per-archetype
    // presence tests (a table with a column can still have rows without the component).
    private readonly ushort[] _required;
    private readonly ushort[] _excluded;
    private readonly ushort[] _anyOf;

    // Lookup keys of queries on component bits >= 256 keep their overflow here instead of allocating it per query.
    [ThreadStatic] private static Vector256<long>[] _scratch;

    private QueryKey(in Tag all, in Tag none, in Tag any)
    {
        _all = all;
        _none = none;
        _any = any;
        _required = _excluded = _anyOf = [];
    }

    private QueryKey(in Tag all, in Tag none, in Tag any, ushort[] required, ushort[] excluded, ushort[] anyOf)
    {
        _all = all;
        _none = none;
        _any = any;
        _required = required;
        _excluded = excluded;
        _anyOf = anyOf;
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
                return new QueryKey(in all, in filter.None, in filter.Any);
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
        return new QueryKey(in all, in filter.None, in filter.Any);
    }

    /// <summary>A copy that owns its storage (cache entries).</summary>
    public QueryKey Detached()
    {
        var all = _all.Clone();
        var none = _none.Clone();
        var any = _any.Clone();
        return new QueryKey(in all, in none, in any, all.ToBitArray(), none.ToBitArray(), any.ToBitArray());
    }

    /// <summary>
    /// Whether archetypes with <paramref name="archetypeMask"/> can hold matching rows (detached keys only): they have
    /// every required column and, with <c>WithAny</c>, at least one of those columns. Excluded columns do not rule a
    /// table out, its rows may lack those components.
    /// </summary>
    public bool Matches(in Tag archetypeMask) =>
        archetypeMask.ContainsAll(in _all) && (_anyOf.Length == 0 || archetypeMask.Intersects(in _any));

    /// <summary>The presence tests of a matching <paramref name="archetype"/>: its required and excluded columns.</summary>
    public RowFilter FilterFor(Archetype archetype)
    {
        var require = new short[_required.Length];
        for (int i = 0; i < require.Length; i++) require[i] = (short)archetype.ColumnIndex(_required[i]);
        var exclude = new System.Collections.Generic.List<short>();
        foreach (ushort bit in _excluded)
        {
            int column = archetype.ColumnIndex(bit);
            if (column >= 0) exclude.Add((short)column);
        }

        var any = new System.Collections.Generic.List<short>();
        foreach (ushort bit in _anyOf)
        {
            int column = archetype.ColumnIndex(bit);
            if (column >= 0) any.Add((short)column);
        }

        return new RowFilter(require, exclude.ToArray(), any.ToArray());
    }

    public bool Equals(QueryKey other) =>
        _all.Equals(in other._all) && _none.Equals(in other._none) && _any.Equals(in other._any);

    public override bool Equals(object obj) => obj is QueryKey other && Equals(other);

    public override int GetHashCode() => (_all.GetHashCode() * 31 + _none.GetHashCode()) * 31 + _any.GetHashCode();
}

/// <summary>
/// The archetypes a query matches and, parallel to them, the presence tests of each. Immutable: the query cache
/// publishes a new instance when an archetype is added.
/// </summary>
internal sealed class QueryMatch(Archetype[] archetypes, RowFilter[] filters)
{
    public readonly Archetype[] Archetypes = archetypes;
    public readonly RowFilter[] Filters = filters;

    public QueryMatch Append(Archetype archetype, RowFilter filter)
    {
        var archetypes = new Archetype[Archetypes.Length + 1];
        var filters = new RowFilter[Filters.Length + 1];
        Array.Copy(Archetypes, archetypes, Archetypes.Length);
        Array.Copy(Filters, filters, Filters.Length);
        archetypes[^1] = archetype;
        filters[^1] = filter;
        return new QueryMatch(archetypes, filters);
    }

    /// <summary>Alive matching entities: O(matching archetypes), plus O(rows / 64) for tables with rows to test.</summary>
    public int CountAlive()
    {
        int count = 0;
        for (int i = 0; i < Archetypes.Length; i++) count += Filters[i].CountAlive(Archetypes[i]);
        return count;
    }

    public bool AnyAlive()
    {
        for (int i = 0; i < Archetypes.Length; i++)
            if (Filters[i].CountAlive(Archetypes[i]) > 0)
                return true;
        return false;
    }
}

/// <summary>
/// Presence test of a query on one archetype: the presence bits of <see cref="Require"/> must be set, those of
/// <see cref="Exclude"/> clear, and at least one of <see cref="AnyOf"/> set when it is not empty (column indices of the
/// archetype; a column without presence bits is full).
/// </summary>
internal sealed class RowFilter(short[] require, short[] exclude, short[] anyOf)
{
    public readonly short[] Require = require;
    public readonly short[] Exclude = exclude;
    public readonly short[] AnyOf = anyOf;

    /// <summary>No row of the table passes: every alive row has an excluded component.</summary>
    public const int None = -1;

    /// <summary>Every alive row passes: every row has the required components and lacks the excluded ones.</summary>
    public const int All = 0;

    /// <summary>Rows must be tested (<see cref="Word"/>).</summary>
    public const int Test = 1;

    /// <summary>
    /// How the rows of <paramref name="archetype"/> pass right now: <see cref="None"/>, <see cref="All"/> or
    /// <see cref="Test"/>, from the per-column counts of rows lacking a component (O(columns)). Every change of those
    /// bumps the world's storage version, so a pass re-checks this after a structural change.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Mode(Archetype archetype)
    {
        var absent = archetype.Absent;
        bool test = false;
        var exclude = Exclude;
        for (int i = 0; i < exclude.Length; i++)
        {
            int lacking = absent[exclude[i]];
            if (lacking == 0) return None;
            test |= lacking != archetype.AliveCount;
        }

        var require = Require;
        for (int i = 0; i < require.Length; i++) test |= absent[require[i]] != 0;

        var anyOf = AnyOf;
        if (anyOf.Length != 0)
        {
            // Some column every row has: no test. No column any row has: no row passes. Else rows differ.
            bool full = false, empty = true;
            for (int i = 0; i < anyOf.Length; i++)
            {
                int lacking = absent[anyOf[i]];
                full |= lacking == 0;
                empty &= lacking == archetype.AliveCount;
            }

            if (empty) return None;
            test |= !full;
        }

        return test ? Test : All;
    }

    /// <summary>Alive rows of <paramref name="archetype"/> that pass.</summary>
    public int CountAlive(Archetype archetype) => Mode(archetype) switch
    {
        None => 0,
        All => archetype.AliveCount,
        _ => Count(archetype),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Passes(Archetype archetype, int slot) => (Word(archetype, slot >> 6) & (1UL << slot)) != 0;

    /// <summary>Rows <c>64 * word</c> .. <c>64 * word + 63</c> that pass, one bit each (tombstones may pass: check ids).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Word(Archetype archetype, int word)
    {
        ulong bits = ulong.MaxValue;
        var require = Require;
        for (int i = 0; i < require.Length; i++)
            if (archetype.Bits(require[i]) is { } present) bits &= present[word];
        var exclude = Exclude;
        for (int i = 0; i < exclude.Length; i++)
            bits &= archetype.Bits(exclude[i]) is { } present ? ~present[word] : 0;
        var anyOf = AnyOf;
        if (anyOf.Length != 0)
        {
            ulong any = 0;
            for (int i = 0; i < anyOf.Length; i++)
                any |= archetype.Bits(anyOf[i]) is { } present ? present[word] : ulong.MaxValue;
            bits &= any;
        }

        return bits;
    }

    /// <summary>Alive rows of <paramref name="archetype"/> that pass (O(rows / 64) plus one id check per passing row).</summary>
    public int Count(Archetype archetype)
    {
        int count = 0;
        int rows = archetype.Count;
        var ids = archetype.EntityIds;
        for (int w = 0; w << 6 < rows; w++)
        {
            ulong bits = Word(archetype, w) & RowMask(w, rows);
            while (bits != 0)
            {
                if (ids[(w << 6) + BitOperations.TrailingZeroCount(bits)] >= 0) count++;
                bits &= bits - 1;
            }
        }

        return count;
    }

    /// <summary>
    /// Next run of consecutive alive rows that pass, at or after <paramref name="start"/> among the first
    /// <paramref name="count"/> rows (batch iteration): false when there is none.
    /// </summary>
    public bool NextRun(Archetype archetype, ref int start, int count, out int length)
    {
        var ids = archetype.EntityIds;
        // First passing alive row: skip 64 rows per word that has none.
        while (true)
        {
            if (start >= count)
            {
                length = 0;
                return false;
            }

            int w = start >> 6;
            ulong bits = Word(archetype, w) & RowMask(w, count) & (ulong.MaxValue << start); // shift is mod 64
            if (bits == 0)
            {
                start = (w + 1) << 6;
                continue;
            }

            start = (w << 6) + BitOperations.TrailingZeroCount(bits);
            if (ids[start] >= 0) break;
            start++;
        }

        // Its end: the first row that fails, then cut at the first tombstone (vectorized).
        int end = start + 1;
        while (end < count)
        {
            int w = end >> 6;
            ulong fails = ~Word(archetype, w) & (ulong.MaxValue << end);
            if (fails != 0)
            {
                end = (w << 6) + BitOperations.TrailingZeroCount(fails);
                break;
            }

            end = (w + 1) << 6;
        }

        end = Math.Min(end, count);
        int tombstone = new ReadOnlySpan<int>(ids, start, end - start).IndexOf(-1);
        length = tombstone < 0 ? end - start : tombstone;
        return true;
    }

    /// <summary>The bits of word <paramref name="word"/> that are below <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RowMask(int word, int rows)
    {
        int left = rows - (word << 6);
        return left >= 64 ? ulong.MaxValue : (1UL << left) - 1;
    }
}

/// <summary>
/// Walks the alive matching rows of an archetype snapshot (the shared core of query enumerators). Each archetype is
/// walked up to the row count it had when the walk started, so entities that enter (or move between) matching
/// archetypes during the walk are not visited; rows that lose their entity or a queried component are skipped. While
/// walking, the world does not reuse tombstone rows (see <see cref="World.Passes"/>); <see cref="Dispose"/> (called by
/// <c>foreach</c>) or reaching the end ends the walk.
/// </summary>
internal ref struct ArchetypeCursor
{
    /// <summary><see cref="MoveNext"/>: no more rows.</summary>
    public const int End = 0;

    /// <summary><see cref="MoveNext"/>: a row of the same table, whose storage did not change.</summary>
    public const int Same = 1;

    /// <summary><see cref="MoveNext"/>: a row of another table, or storage changed: refresh column refs.</summary>
    public const int Moved = 2;

    private readonly World _world;
    public readonly QueryMatch Match;
    private int[] _counts; // row counts at the start of the walk (rented), null before the first MoveNext
    private int _archetype;
    private RowFilter _filter;
    private ref int _ids;  // the current table's id column
    private int _count;    // rows of the current table to walk
    private int _mode;     // RowFilter mode of the current table
    private int _storage;  // the world's storage version _ids and _mode were read at
    private bool _done;

    /// <summary>Table of the current row.</summary>
    public Archetype Current;

    /// <summary>Row and entity id of the current row.</summary>
    public int Slot, Id;

    public ArchetypeCursor(World world, QueryMatch match)
    {
        _world = world;
        Match = match;
        _archetype = -1;
    }

    /// <summary>Next alive matching row: <see cref="End"/>, <see cref="Same"/> or <see cref="Moved"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MoveNext()
    {
        int result = Same;
        if (_storage != _world.StorageVersion)
        {
            if (_counts == null) return Advance();
            Refresh();
            result = Moved;
        }

        while (++Slot < _count)
        {
            int id = Unsafe.Add(ref _ids, Slot);
            if (id >= 0 && (_mode == RowFilter.All || _filter.Passes(Current, Slot)))
            {
                Id = id;
                return result;
            }
        }

        return Advance();
    }

    /// <summary>The caller made a structural change: re-read the id column and the presence counts.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refresh()
    {
        _storage = _world.StorageVersion;
        if (Current == null) return;
        _ids = ref MemoryMarshal.GetArrayDataReference(Current.EntityIds);
        _mode = _filter.Mode(Current);
        if (_mode == RowFilter.None) _count = 0;
    }

    /// <summary>The first matching row of the next tables (starting the walk on the first call).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int Advance()
    {
        if (_counts == null && !Start()) return End;
        var archetypes = Match.Archetypes;
        while (++_archetype < archetypes.Length)
        {
            var archetype = archetypes[_archetype];
            _filter = Match.Filters[_archetype];
            _storage = _world.StorageVersion;
            _mode = _filter.Mode(archetype);
            if (_mode == RowFilter.None || archetype.AliveCount == 0) continue;
            Current = archetype;
            _ids = ref MemoryMarshal.GetArrayDataReference(archetype.EntityIds);
            _count = _counts[_archetype];
            for (Slot = 0; Slot < _count; Slot++)
            {
                int id = Unsafe.Add(ref _ids, Slot);
                if (id >= 0 && (_mode == RowFilter.All || _filter.Passes(archetype, Slot)))
                {
                    Id = id;
                    return Moved;
                }
            }
        }

        Current = null;
        _count = 0;
        Dispose();
        return End;
    }

    private bool Start()
    {
        if (_done) return false;
        var archetypes = Match.Archetypes;
        var counts = ArrayPool<int>.Shared.Rent(Math.Max(1, archetypes.Length));
        for (int i = 0; i < archetypes.Length; i++) counts[i] = archetypes[i].Count;
        _counts = counts;
        _world.BeginPass();
        return true;
    }

    /// <summary>End the walk (idempotent).</summary>
    public void Dispose()
    {
        if (_counts == null || _done) return;
        _done = true;
        ArrayPool<int>.Shared.Return(_counts);
        _counts = null;
        _world.EndPass();
    }
}
