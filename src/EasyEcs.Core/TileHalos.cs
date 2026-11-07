using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The halos a pass of a <c>[ForEach]</c> method taking a <see cref="Tile{TCell, TTile}"/> by value kept from its last
/// exchanges, and the neighbors it found (MPI's ghost cells kept between steps instead of exchanged every step): a tile
/// whose cells and whose neighbors' cells did not change since its halo was kept takes it back
/// (<see cref="TryLoad"/>: its own cells and the kept ring, no neighbor read), and a tile reads its neighbors' ids from
/// here instead of searching the grid's index for them while no tile joined or left the grid. A tile whose cells another
/// system writes hands its neighbors that news before the pass (<see cref="TileHalos{TCell, TTile, TGrid, TKey, TJob}"/>):
/// their kept halos are dropped, and they exchange theirs again. Generated code uses it.
/// </summary>
/// <remarks>
/// Kept per entity index: the entry (its incarnation, the epochs it was kept in, whether a neighbor's cells changed),
/// the neighbors' ids in a slot of its own, and its ring in the same slot: each texture layer's cells around the tile's
/// own (the halo rows, the r cells on each side of every row, whole layers under and over the layers exchanged), in the
/// texture's order, and the layers' row masks. Slots are handed out as tiles are first visited, up to the grid's tiles.
/// Threads: <see cref="TileHalos{TCell, TTile, TGrid, TKey, TJob}.Of"/> runs on the system's thread before the pass; in
/// the pass each tile's entry and slot are written by the thread that visits the tile only (a slot taken with one
/// atomic increment).
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public class TileHaloCache<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    private protected struct Entry
    {
        public int Version;    // the incarnation of the tile kept here
        public int Epoch;      // the grid's epoch its neighbors were found in (their ids are valid while it lasts)
        public int Slot;       // its slot of ids and ring
        public int Ring;       // the ring epoch its ring was kept in (0: none)
        public short From, To; // the layers the ring holds
        public bool Masked;    // the ring was kept with the layers' row masks
        public bool Dirty;     // a cell it reads changed since its ring was kept
    }

    // Constants of the shape, written as expressions of its static members (TileSteps: the JIT folds them).
    private protected static int Hy => TTile.Height > 1 ? TTile.Halo : 0;
    private static int Interior => TTile.Width * TTile.Depth;
    private static int RingLayer => TileLayout.Layer<TTile>() - Interior; // the cells of a texture layer around the tile's own
    private protected static int RingCells => 2 * Hy * TileLayout.Layer<TTile>() + TTile.Height * RingLayer;
    private protected static int Ids => BitOperations.PopCount(TileOffsets<TTile>.SlotMask);

    private protected Entry[] _entries = [];
    private protected EntityId[] _ids = [];   // Ids per slot, in the order of the shape's slots
    private protected TCell[] _rings = [];    // RingCells per slot
    private protected TileMask[] _masks = []; // TileLayout.Layers per slot
    private protected TCell[] _borders = [];  // per slot, the border the ring was kept with
    private protected int _epoch = 1, _ringEpoch = 1, _next, _capacity;
    private protected bool _keeps;            // this pass keeps the rings it exchanges (few tiles change)

    private protected TileHaloCache() { }

    /// <summary>The tiles whose ring is kept now (tests: the kept halos are the ones read). O(entities).</summary>
    internal int Kept
    {
        get
        {
            int kept = 0;
            foreach (ref var entry in _entries.AsSpan())
                if (entry.Version != 0 && entry.Epoch == _epoch && entry.Ring == _ringEpoch && !entry.Dirty) kept++;
            return kept;
        }
    }

    /// <summary>
    /// The neighbors of <paramref name="tile"/>: kept when found since no tile joined or left the grid, else searched in
    /// <paramref name="index"/> (<see cref="TileNeighbors.Of{TShape, TGrid, TKey}"/>) and kept. Generated code calls this.
    /// O(neighbors).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Around<TGrid, TKey>(object index, Entity tile, out TileNeighbors around)
        where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        var entries = _entries;
        int i = tile.Index;
        if ((uint)i < (uint)entries.Length)
        {
            ref var entry = ref entries[i];
            if (entry.Version == tile.Version && entry.Epoch == _epoch)
            {
                around = default;
                // The bounds: a slot is below _capacity, and _ids holds Ids per slot (Grow).
                ref EntityId ids = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ids), entry.Slot * Ids);
                int k = 0;
                for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
                    around[BitOperations.TrailingZeroCount(slots)] = Unsafe.Add(ref ids, k++);
                return;
            }
        }
        Find<TGrid, TKey>(index, tile, out around);
    }

    /// <summary>The search of <see cref="Around"/>, and its result kept.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Find<TGrid, TKey>(object index, Entity tile, out TileNeighbors around)
        where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        TileNeighbors.Of<TTile, TGrid, TKey>(index, tile, out around);
        Store(tile, in around);
    }

    /// <summary>
    /// The neighbors of <paramref name="tile"/> found for another tile of its grid the method takes (the first one, which
    /// <see cref="Around"/> found them for), kept for this one unless they are. Generated code calls this. O(1) once kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Adopt(Entity tile, ref readonly TileNeighbors around)
    {
        var entries = _entries;
        int i = tile.Index;
        if ((uint)i < (uint)entries.Length && entries[i].Version == tile.Version && entries[i].Epoch == _epoch) return;
        Store(tile, in around);
    }

    /// <summary><paramref name="around"/> kept for <paramref name="tile"/> in a new slot, while the grid's tiles have slots.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Store(Entity tile, ref readonly TileNeighbors around)
    {
        int i = tile.Index;
        if ((uint)i >= (uint)_entries.Length) return; // created by the pass itself: not kept
        int slot = Interlocked.Increment(ref _next) - 1;
        if (slot >= _capacity) return; // more tiles than the grid held when the pass began: not kept
        int k = 0, at = slot * Ids;
        for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
            _ids[at + k++] = around[BitOperations.TrailingZeroCount(slots)];
        _entries[i] = new Entry { Version = tile.Version, Epoch = _epoch, Slot = slot };
    }

    /// <summary>
    /// The halo kept for <paramref name="id"/> with these layers, border and masks, put back into
    /// <paramref name="frame"/>'s texture around <paramref name="own"/> (the tile's cells): whether there was one. O(cells
    /// of the texture), two copies per row.
    /// </summary>
    internal bool TryLoad(EntityId id, TileFrame<TCell, TTile> frame, int from, int to, TCell border, bool mask, TCell[] own)
    {
        var entries = _entries;
        if ((uint)id.Index >= (uint)entries.Length) return false;
        ref var entry = ref entries[id.Index];
        if (entry.Version != id.Version || entry.Epoch != _epoch || entry.Ring != _ringEpoch || entry.Dirty
            || entry.From != from || entry.To != to || entry.Masked != mask || !TileLayout.Same(_borders[entry.Slot], border))
            return false;
        Copy(ref MemoryMarshal.GetArrayDataReference(frame.Texels), ref Ring(entry.Slot), ref MemoryMarshal.GetArrayDataReference(own), from, to, true);
        if (mask) Masks(entry.Slot).Slice(0, to - from + 1 + 2 * Hy).CopyTo(frame.Masks);
        return true;
    }

    /// <summary>
    /// What <paramref name="frame"/>'s exchange of <paramref name="id"/> left around the tile's cells kept, when this pass
    /// keeps rings and the tile has a slot. O(cells of the ring).
    /// </summary>
    internal void Keep(EntityId id, TileFrame<TCell, TTile> frame, int from, int to, TCell border, bool mask)
    {
        if (!_keeps) return;
        var entries = _entries;
        if ((uint)id.Index >= (uint)entries.Length) return;
        ref var entry = ref entries[id.Index];
        if (entry.Version != id.Version || entry.Epoch != _epoch) return; // no slot: its neighbors were not found here
        Copy(ref MemoryMarshal.GetArrayDataReference(frame.Texels), ref Ring(entry.Slot), ref Unsafe.NullRef<TCell>(), from, to, false);
        if (mask) frame.Masks.AsSpan(0, to - from + 1 + 2 * Hy).CopyTo(Masks(entry.Slot));
        _borders[entry.Slot] = border;
        (entry.From, entry.To, entry.Masked, entry.Dirty, entry.Ring) = ((short)from, (short)to, mask, false, _ringEpoch);
    }

    private ref TCell Ring(int slot) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_rings), slot * RingCells);

    private Span<TileMask> Masks(int slot) => _masks.AsSpan(slot * TileLayout.Layers<TTile>(), TileLayout.Layers<TTile>());

    /// <summary>
    /// The texture of layers <paramref name="from"/> to <paramref name="to"/> and a ring, in the texture's order: the hy
    /// layers under, then per layer the r rows of halo north and the r cells west of row 0, the tile's row, the r cells east
    /// of it and west of the next (east of the last and the r rows south), then the hy layers over. <paramref name="load"/>:
    /// the ring and <paramref name="own"/> into the texture; else the texture's ring into the ring.
    /// </summary>
    // The bounds: the texture holds (to − from + 1 + 2 hy) layers of TileLayout.Layer cells; the ring RingCells from its
    // slot, of which this walks 2 hy layers and (to − from + 1) ≤ Height rings of RingLayer; own holds the tile's cells
    // (Own checked it), of which this reads layers from to to.
    private static void Copy(ref TCell texture, ref TCell ring, ref TCell own, int from, int to, bool load)
    {
        int width = TTile.Width, depth = TTile.Depth, r = TTile.Halo, row = TileLayout.Row<TTile>(), layer = TileLayout.Layer<TTile>();
        int band = r * row + r; // the north rows and the west cells of row 0; the east cells of the last row and the south rows
        Move(ref texture, ref ring, Hy * layer, load);
        texture = ref Unsafe.Add(ref texture, Hy * layer);
        ring = ref Unsafe.Add(ref ring, Hy * layer);
        for (int y = from; y <= to; y++)
        {
            Move(ref texture, ref ring, band, load);
            texture = ref Unsafe.Add(ref texture, band);
            ring = ref Unsafe.Add(ref ring, band);
            ref TCell cells = ref Unsafe.Add(ref own, y * Interior);
            for (int z = 0; z < depth - 1; z++)
            {
                if (load) Move(ref texture, ref Unsafe.Add(ref cells, z * width), width, true);
                texture = ref Unsafe.Add(ref texture, width);
                Move(ref texture, ref ring, 2 * r, load);
                texture = ref Unsafe.Add(ref texture, 2 * r);
                ring = ref Unsafe.Add(ref ring, 2 * r);
            }
            if (load) Move(ref texture, ref Unsafe.Add(ref cells, (depth - 1) * width), width, true);
            texture = ref Unsafe.Add(ref texture, width);
            Move(ref texture, ref ring, band, load);
            texture = ref Unsafe.Add(ref texture, band);
            ring = ref Unsafe.Add(ref ring, band);
        }
        Move(ref texture, ref ring, Hy * layer, load);
    }

    /// <summary><paramref name="count"/> cells from <paramref name="source"/> into <paramref name="texture"/> (<paramref name="load"/>), or back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Move(ref TCell texture, ref TCell source, int count, bool load)
    {
        uint bytes = (uint)(count * Unsafe.SizeOf<TCell>());
        if (load) Unsafe.CopyBlockUnaligned(ref Unsafe.As<TCell, byte>(ref texture), ref Unsafe.As<TCell, byte>(ref source), bytes);
        else Unsafe.CopyBlockUnaligned(ref Unsafe.As<TCell, byte>(ref source), ref Unsafe.As<TCell, byte>(ref texture), bytes);
    }
}

/// <summary>
/// The kept halos and neighbors of one method's <see cref="Tile{TCell, TTile}"/> parameter in a world
/// (<typeparamref name="TJob"/>, the method's generated job, keys it): <see cref="Of"/> begins its pass. Generated code
/// uses it.
/// </summary>
/// <typeparam name="TCell">The tile's cells.</typeparam>
/// <typeparam name="TTile">The tile's shape.</typeparam>
/// <typeparam name="TGrid">The grid's component.</typeparam>
/// <typeparam name="TKey">The grid's key.</typeparam>
/// <typeparam name="TJob">The generated job of the method.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class TileHalos<TCell, TTile, TGrid, TKey, TJob> : TileHaloCache<TCell, TTile>
    where TCell : unmanaged where TTile : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>
    /// A pass keeps the rings it exchanges only while at most one tile in this many changed since the last one: when
    /// the whole field moves (a wave, heat spreading everywhere) every ring would be dropped before it is read again, and
    /// keeping it costs a copy per tile.
    /// </summary>
    private const int Sparse = 4;

    private readonly World _world;
    private ValueIndex<TGrid, TKey> _index;
    private int _membership = -1;
    private ulong _mark; // the change tick the last pass began at: later writes (and writes at it) are news

    private TileHalos(World world) => _world = world;

    /// <summary>
    /// The kept halos of the method's tile in <paramref name="world"/>, its pass begun: when a tile joined or left the
    /// grid, nothing kept stays; the tiles whose cells another system wrote since the last pass, or that lost them, drop
    /// their neighbors' rings and their own; the pass keeps rings when few did. O(tiles changed) plus, when tiles joined
    /// or left, nothing more (the epochs move on).
    /// </summary>
    public static TileHalos<TCell, TTile, TGrid, TKey, TJob> Of(World world)
    {
        ref object slot = ref world.TileStepsOf(TileJobs<TileHalos<TCell, TTile, TGrid, TKey, TJob>>.Id);
        var halos = slot as TileHalos<TCell, TTile, TGrid, TKey, TJob> ?? (TileHalos<TCell, TTile, TGrid, TKey, TJob>)(slot = new TileHalos<TCell, TTile, TGrid, TKey, TJob>(world));
        halos.Begin();
        return halos;
    }

    private void Begin()
    {
        TileShapeCheck<TTile>.Check();
        var index = (ValueIndex<TGrid, TKey>)LookupExtensions.Prepared<TGrid, TKey>(_world);
        if (!ReferenceEquals(index, _index) || index.Membership != _membership)
        {
            // Tiles joined, left or moved: every id and ring kept may be wrong.
            _index = index;
            _membership = index.Membership;
            _epoch++;
            _ringEpoch++;
            _next = 0;
        }
        int tiles = _world.Count<DynamicBuffer<TCell>, TGrid>();
        if (_entries.Length < _world.Meta.Length || _capacity < tiles) Grow(tiles);
        ulong since = _mark;
        _mark = _world.ChangeTick;

        // Tiles that lost their cells: the tiles around them read the border now.
        foreach (var item in _world.Read<TGrid>(RemovedSince<DynamicBuffer<TCell>>.Filter.Since(since))) DirtyAround(item.Entity, item.Component1.Key);

        var changed = ChangedSince<DynamicBuffer<TCell>>.Filter.Since(since);
        int count = _world.Count<DynamicBuffer<TCell>, TGrid>(in changed);
        _keeps = count * Sparse <= tiles;
        if (!_keeps)
        {
            _ringEpoch++; // the field moves everywhere: no ring kept is read, none is kept
            return;
        }
        if (_rings.Length < _capacity * RingCells) GrowRings();
        if (count == 0) return;
        foreach (var item in _world.Read<DynamicBuffer<TCell>, TGrid>(in changed))
        {
            var tile = item.Entity;
            ref var entry = ref _entries[tile.Index];
            if (entry.Version == tile.Version) entry.Dirty = true; // its own cells: its masks, a clamped or mirrored halo
            DirtyAround(tile, item.Component2.Key);
        }
    }

    /// <summary>The tiles around <paramref name="tile"/> (at <paramref name="key"/>) drop their rings: from its kept ids, else a search.</summary>
    private void DirtyAround(Entity tile, TKey key)
    {
        TileNeighbors around;
        ref var entry = ref _entries[tile.Index];
        if (entry.Version == tile.Version && entry.Epoch == _epoch)
        {
            around = default;
            int k = 0, at = entry.Slot * Ids;
            for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
                around[BitOperations.TrailingZeroCount(slots)] = _ids[at + k++];
        }
        else TileNeighbors.Find<TTile, TGrid, TKey>(_index, key, out around);
        // A grid's Beside is symmetric (the tile across a face sees this one across the opposite face), as the sleeping
        // tiles of TileSteps assume: the tiles this one finds around it are those whose halo reads it.
        for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            var id = around[BitOperations.TrailingZeroCount(slots)];
            if (id == default || (uint)id.Index >= (uint)_entries.Length) continue;
            ref var other = ref _entries[id.Index];
            if (other.Version == id.Version) other.Dirty = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int tiles)
    {
        if (_entries.Length < _world.Meta.Length) Array.Resize(ref _entries, Math.Max(_world.Meta.Length, _entries.Length * 2));
        if (_capacity >= tiles) return;
        _capacity = Math.Max(tiles, _capacity * 2);
        Array.Resize(ref _ids, _capacity * Ids);
        Array.Resize(ref _borders, _capacity);
        if (_rings.Length > 0) GrowRings();
    }

    /// <summary>The rings and masks for every slot: allocated on the first pass that keeps rings.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowRings()
    {
        Array.Resize(ref _rings, _capacity * RingCells);
        Array.Resize(ref _masks, _capacity * TileLayout.Layers<TTile>());
    }
}

public sealed partial class World
{
    /// <summary>The first state of a tile pass kept here that is a <typeparamref name="T"/>, or null (tests). O(passes).</summary>
    internal T TileState<T>() where T : class
    {
        foreach (var state in _tileSteps)
            if (state is T found) return found;
        return null;
    }
}

/// <summary>A <c>Removed&lt;T&gt;</c> filter built once and moved to each pass's tick (<see cref="ChangedSince{T}"/>'s counterpart).</summary>
internal static class RemovedSince<T> where T : struct, IComponent, ITracked
{
    public static readonly QueryFilter Filter = QueryFilter.Create().Removed<T>(0);
}
