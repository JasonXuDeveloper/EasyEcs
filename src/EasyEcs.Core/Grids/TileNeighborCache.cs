using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The neighbors a pass of a <c>[ForEach]</c> method taking a <see cref="Tile{TCell, TTile}"/> by value found for each
/// tile, kept while no tile joins, leaves or moves in the grid (<typeparamref name="TJob"/>, the method's generated job,
/// keys it per world): a tile reads its neighbors' ids here (<see cref="Around"/>) instead of searching the grid's
/// index for each of them every pass. Generated code uses it.
/// </summary>
/// <remarks>
/// The halo itself is pulled every pass: a halo kept between passes and loaded back while nothing around it changed
/// (MPI's ghost cells, D73's push) copies the same bytes the exchange copies from the neighbors' buffers, from memory
/// of its own, and finding what changed costs a scan of the changed tiles before every pass; the search was the cost
/// a pass of few changes paid, and this removes it.
/// Kept per entity index: an entry (the incarnation of the tile, the epoch its neighbors were found in, its slot) and
/// the neighbors' ids in that slot, in the order of the shape's slots. Slots are handed out as tiles are first visited,
/// up to the grid's tiles when the epoch began.
/// Threads: <see cref="Of"/> runs on the system's thread before the pass; in the pass each tile's entry and slot are
/// written by the thread that visits the tile only (a slot taken with one atomic increment).
/// </remarks>
/// <typeparam name="TTile">The tile's shape.</typeparam>
/// <typeparam name="TGrid">The grid's component.</typeparam>
/// <typeparam name="TKey">The grid's key.</typeparam>
/// <typeparam name="TJob">The generated job of the method.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class TileNeighborCache<TTile, TGrid, TKey, TJob> : IKeptNeighbors
    where TTile : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
{
    private struct Entry
    {
        public int Version; // the incarnation of the tile kept here
        public int Epoch;   // the epoch its neighbors were found in (their ids are valid while it lasts)
        public int Slot;    // its slot of ids
    }

    private static int Ids => BitOperations.PopCount(TileOffsets<TTile>.SlotMask);

    private readonly World _world;
    private ValueIndex<TGrid, TKey> _index;
    private int _membership = -1;
    private Entry[] _entries = [];
    private EntityId[] _ids = []; // Ids per slot
    private int _epoch, _next, _capacity;

    private TileNeighborCache(World world) => _world = world;

    /// <summary>
    /// The kept neighbors of the method's tile in <paramref name="world"/>, its pass begun: when a tile joined, left or
    /// moved in the grid since the last pass, none kept stays. O(1); O(tables) when tiles joined or left.
    /// </summary>
    public static TileNeighborCache<TTile, TGrid, TKey, TJob> Of(World world)
    {
        ref object slot = ref world.TileStepsOf(TileJobs<TileNeighborCache<TTile, TGrid, TKey, TJob>>.Id);
        var cache = slot as TileNeighborCache<TTile, TGrid, TKey, TJob> ?? (TileNeighborCache<TTile, TGrid, TKey, TJob>)(slot = new TileNeighborCache<TTile, TGrid, TKey, TJob>(world));
        var index = (ValueIndex<TGrid, TKey>)LookupExtensions.Prepared<TGrid, TKey>(world);
        if (!ReferenceEquals(index, cache._index) || index.Membership != cache._membership) cache.Begin(index);
        return cache;
    }

    /// <summary>A new epoch: every id kept may be wrong; room for the grid's tiles.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Begin(ValueIndex<TGrid, TKey> index)
    {
        TileShapeCheck<TTile>.Check();
        _index = index;
        _membership = index.Membership;
        _epoch++;
        _next = 0;
        int tiles = _world.Count<TGrid>();
        if (_entries.Length < _world.Meta.Length) Array.Resize(ref _entries, Math.Max(_world.Meta.Length, _entries.Length * 2));
        if (_capacity >= tiles) return;
        _capacity = Math.Max(tiles, _capacity * 2);
        Array.Resize(ref _ids, _capacity * Ids);
    }

    /// <summary>
    /// The neighbors of <paramref name="tile"/>: kept when found in this epoch, else searched in
    /// <paramref name="index"/> (<see cref="TileNeighbors.Of{TShape, TGrid, TKey}"/>) and kept. Generated code calls this.
    /// O(neighbors).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Around(object index, Entity tile, out TileNeighbors around)
    {
        var entries = _entries;
        int i = tile.Index;
        if ((uint)i < (uint)entries.Length)
        {
            ref var entry = ref entries[i];
            if (entry.Version == tile.Version && entry.Epoch == _epoch)
            {
                around = default;
                // The bounds: a slot is below _capacity, and _ids holds Ids per slot (Begin).
                ref EntityId ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ids), entry.Slot * Ids);
                int k = 0;
                for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
                    around[BitOperations.TrailingZeroCount(slots)] = Unsafe.Add(ref ids, k++);
                return;
            }
        }
        Find(index, tile, out around);
    }

    /// <summary>The search of <see cref="Around"/>, and its result kept in a new slot while the grid's tiles have slots.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Find(object index, Entity tile, out TileNeighbors around)
    {
        TileNeighbors.Of<TTile, TGrid, TKey>(index, tile, out around);
        int i = tile.Index;
        if ((uint)i >= (uint)_entries.Length) return; // created after the epoch began: not kept
        int slot = Interlocked.Increment(ref _next) - 1;
        if (slot >= _capacity) return; // more tiles than the grid held when the epoch began: not kept
        int k = 0, at = slot * Ids;
        for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
            _ids[at + k++] = around[BitOperations.TrailingZeroCount(slots)];
        _entries[i] = new Entry { Version = tile.Version, Epoch = _epoch, Slot = slot };
    }

    Type IKeptNeighbors.Shape => typeof(TTile);

    /// <summary>The tiles whose neighbors are kept now (tests). O(entities).</summary>
    int IKeptNeighbors.Kept
    {
        get
        {
            int kept = 0;
            foreach (ref var entry in _entries.AsSpan())
                if (entry.Version != 0 && entry.Epoch == _epoch) kept++;
            return kept;
        }
    }
}

/// <summary>What the tests read of a <see cref="TileNeighborCache{TTile, TGrid, TKey, TJob}"/> without its job's type.</summary>
internal interface IKeptNeighbors
{
    Type Shape { get; }
    int Kept { get; }
}

public sealed partial class World
{
    /// <summary>The tiles of shape <typeparamref name="TTile"/> whose neighbors a pass keeps now (tests). O(passes + entities).</summary>
    internal int KeptNeighbors<TTile>() where TTile : struct, ITileShape
    {
        int kept = 0;
        foreach (var state in _tileSteps)
            if (state is IKeptNeighbors cache && cache.Shape == typeof(TTile)) kept += cache.Kept;
        return kept;
    }
}
