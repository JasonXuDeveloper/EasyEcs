using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The colors of a grid's tiles in a <see cref="GaussSeidelAttribute">[GaussSeidel]</see> step, by the keys: no tile
/// has a neighbor of its own color, so the tiles of one color step at once and read their neighbors' cells as the
/// colors before left them. Generated code declares one per method, from the parity of the key's integer fields: two
/// colors when the shape reads its faces (a five- or seven-point stencil), a bit per field when it reads its edges or
/// corners too (four for a 9-point stencil, eight for 27 points).
/// </summary>
/// <typeparam name="TKey">The grid's key.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITileColors<TKey>
{
    /// <summary>How many colors there are.</summary>
    static abstract int Count { get; }

    /// <summary>The color of the tile at <paramref name="key"/>: 0 to <see cref="Count"/> − 1.</summary>
    static abstract int Of(TKey key);
}

/// <summary>
/// The colors generated <see cref="ITileColors{TKey}"/> declare: from the parities of a key's integer fields, by the
/// shape's neighborhood. Generated code uses it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TileColoring
{
    /// <summary>
    /// The colors of a key of <paramref name="fields"/> integer fields: two when <typeparamref name="TShape"/> reads only
    /// across its faces (the parity of the fields' sum alternates across every face), else one per combination of the
    /// fields' parities (no two tiles an edge or a corner apart share all of them).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<TShape>(int fields) where TShape : struct, ITileShape =>
        TShape.Neighborhood == TileNeighborhood.Faces ? 2 : 1 << fields;

    /// <summary>
    /// The color of a key: <paramref name="sum"/>, the parity of its fields' sum, when <typeparamref name="TShape"/> reads
    /// only across its faces, else <paramref name="bits"/>, the parities of its fields, a bit each.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Of<TShape>(int sum, int bits) where TShape : struct, ITileShape =>
        TShape.Neighborhood == TileNeighborhood.Faces ? sum : bits;
}

/// <summary>
/// The state of a stepping tile pass (a <c>[ForEach]</c> method taking <c>ref Tile&lt;TCell, TTile&gt;</c>), one per
/// world and method, kept across passes: per tile of the grid (by entity index, opt-in like a value index's nodes), the
/// second buffer of a Jacobi step, the rows of each buffer that may hold a cell that is not zero, whether the tile may
/// change (it sleeps otherwise) and the edges its last step changed. Generated code uses it.
/// <para>
/// A pass: <see cref="Of"/> on the system's thread (the wakes from writes of others, the tiles that left the grid,
/// the buffers sized from the shape), then the tiles of each color in parallel (<see cref="Enter"/>, the method,
/// <see cref="Sweep"/> between the sweeps of a Gauss-Seidel step, <see cref="Leave"/>: the rows the step skipped zeroed,
/// the rows written compared with the cells, a change and its edges recorded per tile), then <see cref="Next"/> on the
/// system's thread after each color (the swaps of a Jacobi step, the stamps, the wakes of the tiles facing the edges
/// that changed). Each tile's state is written only by the thread stepping it, and the bits of the tiles that changed
/// are set atomically.
/// </para>
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's shape.</typeparam>
/// <typeparam name="TGrid">The grid's tile component (<typeparamref name="TTile"/> itself, unless the shape is <see cref="Columns{TTile}"/> of it).</typeparam>
/// <typeparam name="TKey">The grid's key.</typeparam>
/// <typeparam name="TColors">
/// The method's colors, generated per method (its type keys the state in each world): one for a Jacobi step.
/// </typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class TileSteps<TCell, TTile, TGrid, TKey, TColors>
    where TCell : unmanaged where TTile : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey>
    where TKey : IEquatable<TKey> where TColors : struct, ITileColors<TKey>
{
    private static readonly int Cells = TileLayout.Cells<TTile>();
    private static readonly ulong AllRows = TTile.Depth >= 64 ? ulong.MaxValue : (1UL << TTile.Depth) - 1; // a bit per row of a layer
    private static bool Jacobi => TColors.Count == 1;

    private struct Entry
    {
        public BufferStore<TCell> Next;  // a Jacobi step's second buffer, its Count the tile's cells; null until the first
        public ulong[] FrontRows;        // per layer, the rows of the cells that may hold a cell that is not zero
        public ulong[] NextRows;         // ...and of Next
        public TKey Key;                 // where the tile was when it first stepped (its neighbors, when it leaves)
        public int Version;
        public bool Live;                // the entity at this index with Version stepped here
        public bool Dirty;               // it may change: it steps in the next pass of its color
        public TileFaces Edges;          // the faces whose edge the last step changed
    }

    private readonly World _world;
    private Entry[] _entries = [];
    private ulong[] _changed = [];       // the tiles whose step changed them in the color running, a bit per entity index
    private ValueIndex<TGrid, TKey> _index;
    private int _membership = -1;        // the index's membership count when the tiles were last checked
    private ulong _lastTick;             // the tick the pass stamped its writes with: later writes are others'
    private bool _sleeps;
    private int _maxSweeps, _color;

    private TileSteps(World world) => _world = world;

    /// <summary>
    /// The state of the method's pass (the one of <typeparamref name="TColors"/>) in <paramref name="world"/>, its pass begun: the tiles that left
    /// the grid (or moved) wake the tiles that were around them; the tiles whose cells another system wrote since this
    /// pass last ran (a tile created, loaded or dug, <c>Changed&lt;DynamicBuffer&lt;TCell&gt;&gt;</c>) wake with the tiles
    /// around them, an empty buffer sized for the shape; the pool keeps lists of the tile's cells
    /// (<see cref="DynamicBuffer{T}.MaxPooledLength"/> at least the cells). With <paramref name="sleeps"/> (the method
    /// reads nothing the pass cannot see change), only the tiles that may change step; else every tile does.
    /// <paramref name="maxSweeps"/>: the sweeps a Gauss-Seidel step makes at most before it waits for the next pass
    /// (0: the tile's cells). O(tiles changed by others) plus, when tiles joined or left the grid, O(tiles).
    /// </summary>
    public static TileSteps<TCell, TTile, TGrid, TKey, TColors> Of(World world, bool sleeps, int maxSweeps)
    {
        ref object slot = ref world.TileStepsOf(TileJobs<TColors>.Id);
        var steps = slot as TileSteps<TCell, TTile, TGrid, TKey, TColors> ?? (TileSteps<TCell, TTile, TGrid, TKey, TColors>)(slot = new TileSteps<TCell, TTile, TGrid, TKey, TColors>(world));
        steps.Begin(sleeps, maxSweeps);
        return steps;
    }

    private void Begin(bool sleeps, int maxSweeps)
    {
        TileShapeCheck<TTile>.Check();
        _sleeps = sleeps;
        _maxSweeps = maxSweeps > 0 ? maxSweeps : Cells;
        _color = 0;
        var index = LookupExtensions.Prepared<TGrid, TKey>(_world) as ValueIndex<TGrid, TKey>;
        if (!ReferenceEquals(index, _index))
        {
            _index = index;
            _membership = -1;
        }
        if (BufferStore<TCell>.MaxPooledLength < Cells) BufferStore<TCell>.MaxPooledLength = Cells; // the lists of a tile's cells come back to the pool
        int capacity = _world.Meta.Length;
        if (_entries.Length < capacity) Grow(capacity);
        if (_index.Membership != _membership) Check();
        WakeChanged();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity)
    {
        Array.Resize(ref _entries, Math.Max(capacity, _entries.Length * 2));
        Array.Resize(ref _changed, (_entries.Length + 63) >> 6);
    }

    /// <summary>
    /// Tiles joined, left or moved in the grid: the tiles around each one that left (destroyed, its tile component or
    /// buffer removed, its key changed) wake, and its state goes. A tile that joined wakes with its first step.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Check()
    {
        _membership = _index.Membership;
        var entries = _entries;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            if (!entry.Live) continue;
            var tile = new Entity(_world, i, entry.Version);
            if (_world.IsAlive(tile.Id) && !Unsafe.IsNullRef(ref tile.Find<DynamicBuffer<TCell>>(false)))
            {
                ref var grid = ref tile.Find<TGrid>(false);
                if (!Unsafe.IsNullRef(ref grid) && grid.Key.Equals(entry.Key)) continue;
            }
            WakeAround(entry.Key, uint.MaxValue);
            Forget(ref entry);
        }
    }

    /// <summary>The tile at entity index of <paramref name="entry"/> left: its second buffer goes back to the pool.</summary>
    private static void Forget(ref Entry entry)
    {
        entry.Next?.Recycle();
        entry.Next = null;
        entry.Live = false;
        entry.Dirty = false;
        entry.Key = default!;
    }

    /// <summary>The tiles whose cells another system wrote since the pass last stamped (or that were created since), and the tiles around them.</summary>
    private void WakeChanged()
    {
        foreach (var item in _world.Read<DynamicBuffer<TCell>, TGrid>(ChangedSince<DynamicBuffer<TCell>>.Filter.Since(_lastTick + 1)))
        {
            var tile = item.Entity;
            if (item.Component1.Count == 0) tile.Find<DynamicBuffer<TCell>>(true).SetCount(Cells); // a tile's cells, sized from its shape
            ref var entry = ref Live(tile.Id);
            if (!Unsafe.IsNullRef(ref entry))
            {
                entry.Dirty = true;
                entry.FrontRows.AsSpan().Fill(AllRows); // written by another: any row may hold cells
            }
            WakeAround(item.Component2.Key, uint.MaxValue);
        }
    }

    /// <summary>
    /// Generated code calls this before the pass for each other tile the method reads of the grid (a
    /// <c>Tile&lt;TOther, …&gt;</c> by value): the tiles whose <typeparamref name="TOther"/> cells another system wrote
    /// since the pass last stamped, and the tiles around them, wake.
    /// </summary>
    public void WakeOnCells<TOther>() where TOther : unmanaged
    {
        foreach (var item in _world.Read<DynamicBuffer<TOther>, TGrid>(ChangedSince<DynamicBuffer<TOther>>.Filter.Since(_lastTick + 1)))
        {
            Wake(item.Entity.Id);
            WakeAround(item.Component2.Key, uint.MaxValue);
        }
    }

    /// <summary>
    /// Generated code calls this before the pass for each tracked component the method reads of its tile entity
    /// (<c>in T</c>): the tiles whose <typeparamref name="T"/> another system wrote since the pass last stamped wake.
    /// </summary>
    public void WakeOn<T>() where T : struct, IComponent, ITracked
    {
        foreach (var item in _world.Read<T, TGrid>(ChangedSince<T>.Filter.Since(_lastTick + 1))) Wake(item.Entity.Id);
    }

    /// <summary>The state of <paramref name="id"/> when it is the tile that stepped at its index, else a null ref.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Entry Live(EntityId id)
    {
        var entries = _entries;
        if ((uint)id.Index < (uint)entries.Length)
        {
            ref var entry = ref entries[id.Index];
            if (entry.Live && entry.Version == id.Version) return ref entry;
        }
        return ref Unsafe.NullRef<Entry>();
    }

    private void Wake(EntityId id)
    {
        ref var entry = ref Live(id);
        if (!Unsafe.IsNullRef(ref entry)) entry.Dirty = true;
    }

    /// <summary>The tiles around the one at <paramref name="key"/> in the slots of <paramref name="slots"/> wake.</summary>
    private void WakeAround(TKey key, uint slots)
    {
        TileNeighbors.Find<TTile, TGrid, TKey>(_index, key, out var around);
        for (slots &= TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            var id = around[BitOperations.TrailingZeroCount(slots)];
            if (id != default) Wake(id);
        }
    }

    /// <summary>
    /// Whether <paramref name="tile"/> steps now (its color's turn, and it may change, or the pass does not sleep), and
    /// if so its step begun on this thread: the frame's second buffer (Jacobi) or its sweeps (Gauss-Seidel). A tile that
    /// steps for the first time wakes. Called per tile, on the thread that steps it.
    /// </summary>
    public bool Enter(Entity tile)
    {
        int i = tile.Index;
        if ((uint)i >= (uint)_entries.Length) Grow(i + 1); // created by the method itself: a serial pass
        ref var entry = ref _entries[i];
        if (!entry.Live || entry.Version != tile.Version) Join(ref entry, tile);
        if (!Jacobi && TColors.Of(entry.Key) != _color) return false;
        if (_sleeps && !entry.Dirty) return false;
        entry.Dirty = false;
        var frame = TileFrame<TCell, TTile>.Current;
        frame.Stepping = tile.Id;
        frame.Sweep = 0;
        frame.Written.AsSpan().Clear();
        if (Jacobi)
        {
            var next = entry.Next;
            if (next == null)
            {
                entry.Next = next = BufferStore<TCell>.Rent();
                entry.NextRows.AsSpan().Fill(AllRows); // a list from the pool holds whatever it held
            }
            if (next.Items.Length < Cells) next.Items = new TCell[Cells];
            next.Count = Cells;
            frame.Next = next.Items;
        }
        else
        {
            frame.Next = null;
            frame.Touched.AsSpan().Clear();
        }
        return true;
    }

    /// <summary>A tile steps for the first time at its index: its state, awake, any row of its cells possibly set.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Join(ref Entry entry, Entity tile)
    {
        if (entry.Live) Forget(ref entry); // an earlier tile at this index, destroyed since
        var key = tile.Find<TGrid>(true).Key;
        if (!Jacobi) CheckColors(key);
        entry.Key = key;
        entry.Version = tile.Version;
        entry.Live = true;
        entry.Dirty = true;
        entry.FrontRows ??= new ulong[TTile.Height];
        entry.NextRows ??= new ulong[TTile.Height];
        entry.FrontRows.AsSpan().Fill(AllRows);
        entry.NextRows.AsSpan().Fill(AllRows);
    }

    /// <summary>No tile around the one at <paramref name="key"/> (that the grid holds) has its color: tiles of one color step at once.</summary>
    private void CheckColors(TKey key)
    {
        int color = TColors.Of(key);
        for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            var at = TileOffsets<TTile>.KeyAt<TGrid, TKey>(key, BitOperations.TrailingZeroCount(slots));
            if (TColors.Of(at) == color && _index.TryFindId(at, out _)) ThrowColors(key, at);
        }
    }

    /// <summary>
    /// After a sweep of a Gauss-Seidel step of <paramref name="tile"/>: whether it sweeps again (a row it wrote changed,
    /// and it made fewer sweeps than the most). The rows written get their masks back, so the next sweep visits the rows
    /// the stencil now reaches.
    /// </summary>
    public bool Sweep(Entity tile)
    {
        var frame = TileFrame<TCell, TTile>.Current;
        int width = TTile.Width, depth = TTile.Depth, hy = TTile.Height > 1 ? TTile.Halo : 0;
        bool changed = false;
        for (int y = 0; y < TTile.Height; y++)
        {
            ulong written = frame.Written[y];
            if (written == 0) continue;
            frame.Touched[y] |= written;
            frame.Written[y] = 0;
            ulong rows = 0, cleared = 0;
            for (ulong w = written; w != 0; w &= w - 1)
            {
                int z = BitOperations.TrailingZeroCount(w);
                var now = new ReadOnlySpan<TCell>(frame.Texels, frame.Texel(0, y, z), width);
                var before = new ReadOnlySpan<TCell>(frame.Before, (y * depth + z) * width, width);
                if (!changed && !TileLayout.Equal(now, before)) changed = true;
                if (TileLayout.AnyNotZero(now)) rows |= 1UL << z;
                else cleared |= 1UL << z;
            }
            if (frame.Masked)
            {
                ref var mask = ref frame.Masks[y - frame.From + hy];
                mask = mask with { Rows = (mask.Rows | rows) & ~cleared };
            }
        }
        if (!changed) return false;
        if (++frame.Sweep < _maxSweeps) return true;
        ref var entry = ref _entries[tile.Index];
        entry.Dirty = true; // not settled: it sweeps on in the next pass of its color
        return false;
    }

    /// <summary>
    /// The end of <paramref name="tile"/>'s step on this thread. Jacobi: the rows of the second buffer the step did not
    /// write are zeroed (those it held from before, by vector stores), and the rows written or possibly set before are
    /// compared with the cells: a change and the edges it touched. Gauss-Seidel: the rows the sweeps wrote are compared
    /// with the cells and copied into them where they differ. A tile that changed is recorded for <see cref="Next"/>.
    /// </summary>
    public void Leave(Entity tile)
    {
        var frame = TileFrame<TCell, TTile>.Current;
        frame.Stepping = default;
        frame.Sweep = 0;
        ref var entry = ref _entries[tile.Index];
        ref var buffer = ref tile.Find<DynamicBuffer<TCell>>(true);
        bool changed = Jacobi ? LeaveJacobi(ref entry, frame, ref buffer) : LeaveInPlace(ref entry, frame, ref buffer);
        frame.Next = null;
        if (!changed) return;
        int i = tile.Index;
        Interlocked.Or(ref _changed[i >> 6], 1UL << i);
    }

    private bool LeaveJacobi(ref Entry entry, TileFrame<TCell, TTile> frame, ref DynamicBuffer<TCell> buffer)
    {
        int width = TTile.Width, depth = TTile.Depth;
        var next = frame.Next;
        // The cells as they are: an empty buffer (a tile no exchange read) is zeros.
        var cells = buffer.Store is { Count: > 0 } store ? store.Items : TileMemory<TCell, TTile>.Blank(default, true);
        if (cells.Length < Cells) Tile<TCell, TTile>.ThrowCells(default, buffer.Count, Cells);
        var edges = TileFaces.None;
        bool changed = !_sleeps; // a pass that does not sleep swaps every tile it steps, unseen
        for (int y = 0; y < TTile.Height; y++)
        {
            ulong written = frame.Written[y];
            for (ulong stale = entry.NextRows[y] & ~written; stale != 0; stale &= stale - 1) // rows the step skipped: zero
                new Span<TCell>(next, (y * depth + BitOperations.TrailingZeroCount(stale)) * width, width).Clear();
            entry.NextRows[y] = written;
            if (!_sleeps) continue;
            for (ulong rows = written | entry.FrontRows[y]; rows != 0; rows &= rows - 1)
            {
                int z = BitOperations.TrailingZeroCount(rows), at = (y * depth + z) * width;
                if (TileLayout.RowEdges<TCell, TTile>(new ReadOnlySpan<TCell>(cells, at, width), new ReadOnlySpan<TCell>(next, at, width), y, z, ref edges))
                    changed = true;
            }
        }
        if (changed) entry.Edges = edges;
        else entry.NextRows.AsSpan().CopyTo(entry.FrontRows); // the same cells: what the step wrote is all they hold
        return changed;
    }

    private bool LeaveInPlace(ref Entry entry, TileFrame<TCell, TTile> frame, ref DynamicBuffer<TCell> buffer)
    {
        int width = TTile.Width, depth = TTile.Depth;
        var edges = TileFaces.None;
        bool changed = false;
        TCell[] cells = null;
        for (int y = 0; y < TTile.Height; y++)
        {
            ulong touched = frame.Touched[y] | frame.Written[y]; // a method that wrote rows and stopped before its sweep ended
            frame.Written[y] = 0;
            for (; touched != 0; touched &= touched - 1)
            {
                int z = BitOperations.TrailingZeroCount(touched), at = (y * depth + z) * width;
                var now = new ReadOnlySpan<TCell>(frame.Texels, frame.Texel(0, y, z), width);
                cells ??= Writable(ref buffer);
                if (!TileLayout.RowEdges<TCell, TTile>(new ReadOnlySpan<TCell>(cells, at, width), now, y, z, ref edges)) continue;
                changed = true;
                now.CopyTo(new Span<TCell>(cells, at, width));
            }
        }
        if (changed) entry.Edges = edges;
        return changed;
    }

    /// <summary>The tile's cells to write in place: its own list (a copy of a shared one), sized for the shape.</summary>
    private static TCell[] Writable(ref DynamicBuffer<TCell> buffer)
    {
        if (buffer.Count == 0) buffer.SetCount(Cells);
        var store = DynamicBufferExtensions.Own(ref buffer);
        if (store.Count < Cells) Tile<TCell, TTile>.ThrowCells(default, store.Count, Cells);
        return store.Items;
    }

    /// <summary>
    /// After the tiles of the color running stepped, on the system's thread: each tile that changed becomes its step
    /// (Jacobi: its second buffer swapped with its cells, O(1); the old cells its second buffer, unless shared), its
    /// cells are stamped as written, and the tiles across the edges that changed wake (with the tile itself, after a
    /// Jacobi step). True while another color is to step; after the last, the next writes are newer than this pass's.
    /// </summary>
    public bool Next()
    {
        var world = _world;
        var changed = _changed;
        for (int word = 0; word < changed.Length; word++)
        {
            for (ulong bits = changed[word]; bits != 0; bits &= bits - 1)
            {
                int i = word << 6 | BitOperations.TrailingZeroCount(bits);
                ref var entry = ref _entries[i];
                var tile = new Entity(world, i, entry.Version);
                if (Jacobi) Swap(ref entry, ref tile.Find<DynamicBuffer<TCell>>(true));
                Entity.Stamp<DynamicBuffer<TCell>>(tile);
                if (!_sleeps) continue;
                if (Jacobi) entry.Dirty = true; // one step of Jacobi is no fixed point: it steps again
                if (entry.Edges != TileFaces.None) WakeAround(entry.Key, TileOffsets<TTile>.Facing(entry.Edges));
            }
            changed[word] = 0;
        }

        if (++_color < TColors.Count) return true;
        _lastTick = world.ChangeTick;
        // Outside an update (a pass run by hand), a write made next would carry the stamp's tick: the tick moves on, as
        // between updates. Inside one, the next stage's tick is newer; a parallel stage's systems share the tick.
        if (!world.Gate.InParallel) world.NextChangeTick();
        return false;
    }

    private static void Swap(ref Entry entry, ref DynamicBuffer<TCell> buffer)
    {
        var cells = buffer.Store;
        buffer.Store = entry.Next;
        (entry.FrontRows, entry.NextRows) = (entry.NextRows, entry.FrontRows);
        if (cells is { Shares: 0 })
        {
            entry.Next = cells;
            return;
        }
        // A list others share (a prefab's, a clone's, a snapshot's) is theirs to keep: the next step takes a new one.
        cells?.Recycle();
        entry.Next = null;
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining), StackTraceHidden]
    private static void ThrowColors(TKey key, TKey at) => throw new InvalidOperationException(
        $"The {typeof(TGrid).Name} tiles at {key} and {at} are neighbors of one color: a [GaussSeidel] step colors the tiles by the parity of their keys, which a grid that wraps around an odd number of tiles breaks");
}

/// <summary>
/// A <c>Changed&lt;T&gt;</c> filter built once and moved to each pass's tick (<see cref="QueryFilter.Since"/>): a filter
/// built per pass allocates when <typeparamref name="T"/>'s bit is past 255 (<see cref="FilterOverflow"/> interns it).
/// </summary>
internal static class ChangedSince<T> where T : struct, IComponent, ITracked
{
    public static readonly QueryFilter Filter = QueryFilter.Create().Changed<T>(0);
}

/// <summary>A dense id per stepping method (its colors type): its slot in each world's tile steps.</summary>
internal static class TileJobs<TColors>
{
    public static readonly int Id = Interlocked.Increment(ref TileJobs.Count) - 1;
}

/// <summary>The count of <see cref="TileJobs{TJob}"/>.</summary>
internal static class TileJobs
{
    public static int Count;
}

public sealed partial class World
{
    private object[] _tileSteps = [];

    /// <summary>The slot of the tile steps of job <paramref name="id"/> (<see cref="TileJobs{TJob}"/>).</summary>
    internal ref object TileStepsOf(int id)
    {
        if (id >= _tileSteps.Length) Array.Resize(ref _tileSteps, Math.Max(id + 1, _tileSteps.Length * 2));
        return ref _tileSteps[id];
    }

    /// <summary>The change tick moves on (a stepping tile pass run outside an update, <see cref="TileSteps{TCell, TTile, TGrid, TKey, TColors}.Next"/>).</summary>
    internal void NextChangeTick() => _changeTick++;
}
