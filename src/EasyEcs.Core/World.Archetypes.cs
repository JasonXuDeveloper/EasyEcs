using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
namespace EasyEcs;

// Archetype registry, entity membership and the query cache.
//
// Thread-safety contract:
// - Structural mutation (archetype creation, membership changes) happens under the owner's gate (Structural()).
// - The cold paths the threads of a parallel stage reach on their own (a query cache miss, a missing singleton, closing
//   tombstones) take the shared gate (Shared()) instead. The two flags do not exclude each other: this is safe because
//   other threads only run while the structure is frozen (a stage or parallel chunks), when the owner's gate throws.
// - Lock-free readers (queries) only see immutable Archetype[] snapshots: _allArchetypes and every query cache entry
//   are replaced copy-on-write and published with Volatile.Write, never mutated in place.
public sealed partial class World
{
    // Guards structural modifications (see StructuralGate: the owner's flag, the shared compare-and-swap).
    internal StructuralGate Gate;

    /// <summary>Hold the owner's structural gate until the returned scope is disposed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StructuralScope Structural() => new(ref Gate);

    /// <summary>Hold the shared gate (cold paths the threads of a parallel stage reach) until the scope is disposed.</summary>
    internal SharedScope Shared() => new(ref Gate);

    // Bumped by every structural change (and every storage reallocation or compaction), so an in-flight ForEach pass
    // knows when to refresh its cached array references and check whether its job moved the visited entity.
    internal int StorageVersion;

    // BitMask -> archetype in group 0 of every grouped column (only touched under the gate).
    private readonly Dictionary<BitMask, Archetype> _archetypes = new();

    // The tables of other groups (World.GroupBy), by mask and groups. Empty until a group other than 0 is used.
    private readonly Dictionary<GroupedTable, Archetype> _groupedTables = new();

    private readonly record struct GroupedTable(BitMask Mask, string Groups); // groups as chars: value equality, cold

    // Copy-on-write snapshot of all archetypes (archetype creation is rare, reads are frequent).
    private Archetype[] _allArchetypes = Array.Empty<Archetype>();

    // Query -> matching archetypes. Entries are created once and their array is replaced copy-on-write.
    private readonly ConcurrentDictionary<QueryKey, QueryCacheEntry> _queryCache = new(QueryKeyComparer.Instance);
    private readonly ConcurrentDictionary<QueryKey, QueryCacheEntry>.AlternateLookup<QueryLookup> _queryLookup;

    internal sealed class QueryCacheEntry(QueryMatch match)
    {
        public QueryMatch Match = match;
    }

    // Per call signature (TypeIndex<ComponentSignature, (T1, ..., Tn)>): the first MemoWays filters queried with it and
    // their cache entries. A hit compares the filter's masks instead of building a key, hashing it and probing the
    // dictionary. Queries run on any thread, so the arrays are immutable snapshots replaced copy-on-write; once a
    // signature's ways are full, other filters take the dictionary (no allocation in steady state).
    //
    // Lock-free: a writer (MatchAndMemo, KeepQuery) reads the array, then looks up its cache entry, then publishes its
    // copy with Interlocked.CompareExchange against the array it read, retrying from the start when another writer
    // or a reset got there first. A reset (RemoveTypes, ClearArchetypes) first drops the query cache's stale entries,
    // then swaps in an empty array with Interlocked.Exchange: a writer that read the array before the reset fails its
    // CAS and retries on the empty array with an entry looked up again, so a stale entry is never republished; one that
    // published before the reset is wiped by it. Readers only Volatile.Read the array.
    private QueryMemo[][] _queryMemo = [];

    // Per job type that always runs one query (TypeIndex<KeptQuery, TJob>; a job's OneQuery, which generated jobs set):
    // the query's cache entry, so a pass of the job reads its match without comparing filters. Immutable snapshots
    // published by CAS and reset like the memo (the same invariant).
    private QueryCacheEntry[] _kept = [];
    private const int MemoWays = 4;

    // A struct: a hit reads the ways' filters in one array, without a hop to an object per way.
    private readonly struct QueryMemo(in QueryFilter filter, QueryCacheEntry entry)
    {
        public readonly QueryFilter Filter = filter;
        public readonly QueryCacheEntry Entry = entry;
    }

    /// <summary>
    /// Put a new entity (no archetype yet) into <paramref name="target"/> and return its row. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Place(int id, Archetype target)
    {
        ref var meta = ref MetaRef(id);
        int slot = target.Add(id, meta.Version);
        meta.Slot = slot;
        meta.Archetype = target;
        StorageVersion++;
        return slot;
    }

    /// <summary>Resolve an add transition once (mask hashing) and cache it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Transition CreateEdge(Archetype from, ushort bit)
    {
        var mask = from.ComponentMask.Clone();
        mask.SetBit(bit);
        var transition = new Transition(from, GetOrCreateArchetype(in mask, from));
        from.SetAddEdge(bit, transition);
        return transition;
    }

    /// <summary>
    /// Resolve a multi-component add transition once (mask hashing) and cache it on <paramref name="from"/> under
    /// <paramref name="signature"/>. The target is <paramref name="from"/> itself when it has every column already.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Transition CreateSetEdge(Archetype from, ReadOnlySpan<ushort> bits, int signature)
    {
        var mask = from.ComponentMask.Clone();
        for (int i = 0; i < bits.Length; i++) mask.SetBit(bits[i]);
        var transition = new Transition(from, GetOrCreateArchetype(in mask, from));
        from.SetAddSetEdge(signature, transition);
        return transition;
    }

    /// <summary>
    /// Get or create the archetype for <paramref name="mask"/>, its grouped columns in the groups of
    /// <paramref name="like"/> (0 where it has none), group <paramref name="group"/> for <paramref name="regroup"/>'s
    /// column when it is one. A new archetype is appended (copy-on-write) to every cached query it matches. Must be
    /// called inside the structural gate.
    /// </summary>
    private Archetype GetOrCreateArchetype(in BitMask mask, Archetype like = null, int regroup = -1, int group = 0)
    {
        var componentMask = ComponentInfo.AnyTracked ? WithCompanions(in mask) : mask;
        int[] groups = like?.Groups != null || regroup >= 0 ? GroupsOf(in componentMask, like, regroup, group) : null;
        return Table(in componentMask, groups);
    }

    /// <summary>
    /// Get or create the table of <paramref name="mask"/> (companions included) in <paramref name="groups"/>
    /// (by column; null when none is set). Inside the structural gate.
    /// </summary>
    private Archetype Table(in BitMask mask, int[] groups)
    {
        Archetype archetype;
        if (groups == null && _archetypes.TryGetValue(mask, out archetype)) return archetype;
        string groupsKey = groups != null ? GroupsKey(groups) : null;
        if (groups != null && _groupedTables.TryGetValue(new GroupedTable(mask, groupsKey), out archetype)) return archetype;

        // A new table owns its mask: a caller's reused mask (Load) keeps changing its bits past 256.
        var componentMask = mask.Clone();
        archetype = new Archetype(in componentMask, groups);
        if (archetype.Unique) IndexUnique(archetype); // before the table is registered: throws when no index can be made
        if (TypeDefaults.Any || _orderRequests != null) ApplyDefaults(archetype);
        if (groups == null) _archetypes[componentMask] = archetype;
        else _groupedTables[new GroupedTable(componentMask, groupsKey)] = archetype;

        Volatile.Write(ref _allArchetypes, Append(_allArchetypes, archetype));
        _hierarchy |= componentMask.HasBit(ChildOfBit); // destroys look for children from now on

        foreach (var pair in _queryCache)
        {
            if (pair.Key.Matches(archetype))
                Volatile.Write(ref pair.Value.Match, pair.Value.Match.Append(archetype, pair.Key.FilterFor(archetype)));
        }

        return archetype;
    }

    /// <summary>
    /// The groups of a table with <paramref name="mask"/> (by column): <paramref name="like"/>'s for the grouped
    /// columns it has, <paramref name="group"/> for bit <paramref name="regroup"/>, 0 elsewhere. Null when all are 0
    /// (the table of the plain registry). Cold path: new transitions only.
    /// </summary>
    private static int[] GroupsOf(in BitMask mask, Archetype like, int regroup, int group)
    {
        var bits = mask.ToBitArray();
        int[] groups = null;
        for (int c = 0; c < bits.Length; c++)
        {
            if (!ComponentInfo.Of(bits[c]).Grouped) continue;
            int value = bits[c] == regroup ? group : like?.GroupOf(bits[c]) ?? 0;
            if (value == 0) continue;
            groups ??= new int[bits.Length];
            groups[c] = value;
        }

        return groups;
    }

    private static string GroupsKey(int[] groups)
    {
        var chars = new char[groups.Length * 2];
        for (int i = 0; i < groups.Length; i++)
        {
            chars[2 * i] = (char)(groups[i] >> 16);
            chars[2 * i + 1] = (char)groups[i];
        }

        return new string(chars);
    }

    /// <summary>
    /// <paramref name="mask"/> with the change-tick column of every tracked component in it, and without those whose
    /// component it lacks (a transition adds or removes the component alone). Cold path: new transitions only.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BitMask WithCompanions(in BitMask mask)
    {
        var result = mask;
        bool cloned = false;
        for (int bit = 0; bit < mask.Capacity; bit++) // no bit array: a lookup of a known table allocates nothing
        {
            if (!mask.HasBit(bit)) continue;
            var info = ComponentInfo.Of(bit);
            int add = info.Companion >= 0 && !mask.HasBit(info.Companion) ? info.Companion : -1;
            bool drop = info.Owner >= 0 && !mask.HasBit(info.Owner);
            if (add < 0 && !drop) continue;
            if (!cloned)
            {
                result = mask.Clone();
                cloned = true;
            }

            if (add >= 0) result.SetBit(add);
            if (drop) result.ClearBit(bit);
        }

        return result;
    }

    private static Archetype[] Append(Archetype[] source, Archetype item)
    {
        var result = new Archetype[source.Length + 1];
        Array.Copy(source, result, source.Length);
        result[source.Length] = item;
        return result;
    }

    /// <summary>
    /// Archetypes containing all <paramref name="bits"/> and matching <paramref name="filter"/>, with their per-row
    /// presence tests. Lock-free on cache hits; the result is an immutable snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // once per pass: keeps dictionary and gate code out of the iteration loops
    internal QueryMatch GetMatchingArchetypes(int signature, ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var memo = Volatile.Read(ref _queryMemo);
        if ((uint)signature < (uint)memo.Length && memo[signature] is { } ways)
        {
            for (int i = 0; i < ways.Length; i++)
                if (ways[i].Filter.SameQuery(in filter))
                    return Volatile.Read(ref ways[i].Entry.Match);
            if (ways.Length == MemoWays) return Volatile.Read(ref GetQueryEntry(bits, in filter).Match);
        }

        return MatchAndMemo(signature, bits, in filter);
    }

    /// <summary>
    /// The match of the one query of job type <paramref name="job"/> (<c>TypeIndex&lt;KeptQuery, TJob&gt;</c>): two loads
    /// and a null test when the world has run the job before, else <see cref="KeepQuery"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryMatch KeptMatch(int job, ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var kept = Volatile.Read(ref _kept);
        return (uint)job < (uint)kept.Length && kept[job] is { } entry ? Volatile.Read(ref entry.Match) : KeepQuery(job, bits, in filter);
    }

    /// <summary>The first pass of a job type in this world: its query's entry is kept for the next ones (cold path).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private QueryMatch KeepQuery(int job, ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        while (true)
        {
            var old = Volatile.Read(ref _kept);
            var entry = GetQueryEntry(bits, in filter); // after the read: see _queryMemo
            if ((uint)job < (uint)old.Length && old[job] != null) return Volatile.Read(ref old[job].Match);
            var kept = job < old.Length ? (QueryCacheEntry[])old.Clone() : Grown(old, job);
            kept[job] = entry;
            if (Interlocked.CompareExchange(ref _kept, kept, old) == old) return Volatile.Read(ref entry.Match);
        }
    }

    /// <summary>A copy of <paramref name="array"/> long enough for index <paramref name="index"/> (doubled at least).</summary>
    private static T[] Grown<T>(T[] array, int index)
    {
        var grown = new T[Math.Max(index + 1, array.Length * 2)];
        Array.Copy(array, grown, array.Length);
        return grown;
    }

    /// <summary>
    /// Whether row <paramref name="slot"/> of <paramref name="archetype"/> passes a query (<c>entity.Run</c> with a
    /// filter): the table must be one of the query's and the row must pass its row test. O(matching tables).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool RowPasses(QueryMatch match, Archetype archetype, int slot, ulong since)
    {
        var archetypes = match.Archetypes;
        for (int i = 0; i < archetypes.Length; i++)
            if (ReferenceEquals(archetypes[i], archetype))
                return new RowFilter(match.Filters[i], since).Passes(archetype, slot);
        return false;
    }

    /// <summary>A filter the signature's ways do not hold yet: look it up and give it a way (cold path).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private QueryMatch MatchAndMemo(int signature, ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        while (true)
        {
            var old = Volatile.Read(ref _queryMemo);
            var entry = GetQueryEntry(bits, in filter); // after the read: see _queryMemo
            var ways = (uint)signature < (uint)old.Length ? old[signature] ?? [] : [];
            for (int i = 0; i < ways.Length; i++)
                if (ways[i].Filter.SameQuery(in filter)) return Volatile.Read(ref ways[i].Entry.Match); // another writer's
            if (ways.Length == MemoWays) return Volatile.Read(ref entry.Match); // full: the dictionary serves it
            var memo = signature < old.Length ? (QueryMemo[][])old.Clone() : Grown(old, signature);
            Array.Resize(ref ways, ways.Length + 1);
            ways[^1] = new QueryMemo(in filter, entry);
            memo[signature] = ways;
            if (Interlocked.CompareExchange(ref _queryMemo, memo, old) == old) return Volatile.Read(ref entry.Match);
        }
    }

    /// <summary>
    /// The cache entry of a query: its <see cref="QueryCacheEntry.Match"/> is replaced (volatile) whenever a new
    /// archetype matches, so holders of the entry always see every matching table without another lookup.
    /// </summary>
    internal QueryCacheEntry GetQueryEntry(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        return _queryLookup.TryGetValue(new QueryLookup(bits, in filter), out var entry) ? entry : CreateQueryEntry(bits, in filter);
    }

    /// <summary>
    /// The miss path of <see cref="GetQueryEntry"/>: a method of its own, so a hit pays neither its frame (the lists,
    /// the gate) nor the copies of the key it makes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private QueryCacheEntry CreateQueryEntry(ReadOnlySpan<ushort> bits, in QueryFilter filter)
    {
        var lookup = QueryKey.ForLookup(bits, in filter);
        using (Shared())
        {
            if (_queryCache.TryGetValue(lookup, out var entry))
                return entry;

            var key = lookup.Detached();

            var all = _allArchetypes;
            var archetypes = new List<Archetype>();
            var filters = new List<RowPlan>();
            for (int i = 0; i < all.Length; i++)
            {
                if (!key.Matches(all[i])) continue;
                archetypes.Add(all[i]);
                filters.Add(key.FilterFor(all[i]));
            }

            entry = new QueryCacheEntry(new QueryMatch(archetypes.ToArray(), filters.ToArray()));
            _queryCache[key] = entry;
            return entry;
        }
    }

    /// <summary>
    /// Close the tombstone rows of every archetype (moving the last rows into them: O(tombstones x columns)).
    /// Structural changes keep tables dense on their own: outside passes a removed row is filled at once, tombstones
    /// left by changes during passes are closed when the last pass ends, and passes end even when a job throws. Only a
    /// query enumerator abandoned without <c>Dispose</c> leaves a pass open; <see cref="Update()"/> then calls this, as
    /// no pass runs across an update.
    /// </summary>
    internal void CompactArchetypes()
    {
        using (Structural())
        {
            PassDepth = 0;
            var all = _allArchetypes;
            for (int i = 0; i < all.Length; i++)
                all[i].FillHoles(Meta);
            Array.Clear(_dirty, 0, _dirtyCount);
            _dirtyCount = 0;
            StorageVersion++;
        }
    }

    /// <summary>
    /// Rows in use, living entities and the share of rows that are holes (tombstones of a pass in progress), for tests:
    /// tables are dense between passes.
    /// </summary>
    internal (int Rows, int Alive, float HoleRatio) RowStats()
    {
        var all = Volatile.Read(ref _allArchetypes);
        int totalSlots = 0;
        int aliveEntities = 0;
        for (int i = 0; i < all.Length; i++)
        {
            totalSlots += all[i].Count;
            aliveEntities += all[i].AliveCount;
        }

        float fragmentation = totalSlots > 0 ? 1.0f - (float)aliveEntities / totalSlots : 0f;
        return (totalSlots, aliveEntities, fragmentation);
    }

    private void ClearArchetypes()
    {
        var all = _allArchetypes;
        for (int i = 0; i < all.Length; i++)
            Array.Clear(all[i].Slots);

        _archetypes.Clear();
        _groupedTables.Clear();
        _prefabTables?.Clear();
        _allArchetypes = Array.Empty<Archetype>();
        _queryCache.Clear();
        ResetQueryMemo();
    }

    /// <summary>
    /// Empties the per-signature memo and the kept queries, after the caller dropped the query cache's stale entries
    /// (see <see cref="_queryMemo"/>): a writer that read the arrays before fails its CAS and retries on the empty ones.
    /// </summary>
    internal void ResetQueryMemo()
    {
        Interlocked.Exchange(ref _queryMemo, []);
        Interlocked.Exchange(ref _kept, []);
    }
}
