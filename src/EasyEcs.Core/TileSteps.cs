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

/// <summary>When a sleeping pass walks the woken tiles of a color, not every tile of the grid (<see cref="TileSteps{TCell, TTile, TGrid, TKey, TColors}.StepWoken{TJob}"/>).</summary>
internal static class TileWalk
{
    /// <summary>
    /// A color whose woken tiles are at least one in <see cref="Share"/> of the grid's walks every tile instead
    /// (TileWokenBenchmark, 2 to 75 % of the plates awake: the walks tie up to a quarter; past it the walk of every tile
    /// wins at 4 threads, its chunks whole runs of rows, by up to a fifth, and costs up to a tenth at 1 thread).
    /// Internal and writable for the benchmarks and tests: 0 always walks the woken, <see cref="int.MaxValue"/> every
    /// tile once one woke.
    /// </summary>
    internal static int Share = 4;

    /// <summary>
    /// The tiles (entity indexes) from which the swaps of a Jacobi pass that does not sleep run on every thread: a few
    /// microseconds of dispatch against tens of nanoseconds a swap (TileStencilBenchmark measures 1,024 and 262,144
    /// tiles, both past it). Internal and writable for the tests: 0 always on every thread.
    /// </summary>
    internal static int Swaps = 256;
}

/// <summary>
/// The state of a stepping tile pass (a <c>[ForEach]</c> method taking <c>ref Tile&lt;TCell, TTile&gt;</c>), one per
/// world and method, kept across passes: per tile of the grid (by entity index, opt-in like a value index's nodes), the
/// second buffer of a Jacobi step, the rows of each buffer that may hold a cell that is not zero, whether the tile may
/// change (it sleeps otherwise) and the edges its last step changed. Generated code uses it.
/// <para>
/// A pass: <see cref="Of"/> on the system's thread (the wakes from writes of others, the tiles that left the grid,
/// the buffers sized from the shape), then the tiles of each color in parallel: in a pass that sleeps, the tiles of
/// the color's queue (those that woke, <see cref="StepWoken{TJob}"/>), else every tile of the grid (the query's walk;
/// <see cref="Enter"/>, the method, <see cref="Sweep"/> between the sweeps of a Gauss-Seidel step, <see cref="Leave"/>:
/// the rows the step skipped zeroed, the rows written compared with the cells, a change and its edges recorded per
/// tile), then <see cref="Next"/> on the system's thread after each color (the swaps of a Jacobi step, the stamps, the
/// wakes of the tiles facing the edges that changed, queued by color). Each tile's state is written only by the thread
/// stepping it; a walk of every tile marks the blocks of 64 tiles holding news with a read before the one atomic set.
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
    // Constants of the shape, written as expressions of its static members (not static readonly fields of this generic
    // class, which the JIT reads through a helper call per use where it does not fold them).
    private static int Cells => TileLayout.Cells<TTile>();
    private static ulong AllRows => TTile.Depth >= 64 ? ulong.MaxValue : (1UL << TTile.Depth) - 1; // a bit per row of a layer
    private static bool Jacobi => TColors.Count == 1;
    private static TileFaces AllEdges => TileFaces.North | TileFaces.South | TileFaces.West | TileFaces.East
        | (TTile.Height > 1 ? TileFaces.Above | TileFaces.Below : TileFaces.None); // what RowEdges can find

    private struct Entry
    {
        public BufferStore<TCell> Next;  // a Jacobi step's second buffer, its Count the tile's cells; null until the first
        public BufferStore<TCell> Front; // the cells' list when the tile's step changed it (Leave), for the swap (Next)
        public ulong[] FrontRows;        // per layer, the rows of the cells that may hold a cell that is not zero
        public ulong[] NextRows;         // ...and of Next
        public TKey Key;                 // where the tile was when it first stepped (its neighbors, when it leaves)
        public int Version;
        public bool Live;                // the entity at this index with Version stepped here
        public bool Dirty;               // it may change: it steps in the next pass of its color
        public bool Queued;              // in the queue of its color (Dirty since it was last walked)
        public bool Changed;             // its step changed it in the color running (Leave), for Next
        public bool Rewake;             // to wake in Next: it ran out of sweeps, or joined in the walk of another color
        public TileFaces Edges;          // the faces whose edge the last step changed
        public EntityId[] Around;        // the tiles across those faces, as the step's exchange found them (by slot)
    }

    private readonly World _world;
    private Entry[] _entries = [];
    private ulong[] _blocks = [];        // the blocks of 64 entity indexes holding a tile whose step changed it (or did not settle) in the color running
    private int[][] _queues = [];        // per color, the entity indexes of the tiles that woke since its last walk (a sleeping pass's walk)
    private int[] _queued = [];          // ...how many each holds
    private EntityId[] _walk = [];       // the tiles of the color running a walk of the woken visits
    private int _walked = -1;            // how many (-1: the color running walks every tile of the grid)
    private bool _listed;                // every tile that may change is in its color's queue (the last pass slept)
    private bool _wokenOnly;             // this pass walks the queues (sleeping, the queues complete, no tile joined)
    private ValueIndex<TGrid, TKey> _index;
    private int _membership = -1;        // the index's membership count when the tiles were last checked
    private ulong _lastTick;             // the tick the pass stamped its writes with: later writes are others'
    private bool _sleeps;                // this pass keeps tiles asleep (the method may sleep, and the field is not moving everywhere)
    private int _awake;                  // Jacobi: passes left that step every tile, unseen, before one looks again

    /// <summary>
    /// Passes a Jacobi step runs with every tile awake once a pass that looked saw nearly every tile change: comparing
    /// each tile's rows and waking its neighbors costs more than the sleep it allows when the whole field moves (a wave,
    /// heat spreading everywhere). The step of an unchanged tile writes the same cells, so the result is the same.
    /// </summary>
    private const int AwakePasses = 15;
    private int _maxSweeps, _color;

    private TileSteps(World world) => _world = world;

    /// <summary>
    /// The state of the method's pass (the one of <typeparamref name="TColors"/>) in <paramref name="world"/>, its pass begun: the tiles that left
    /// the grid (or moved) wake the tiles that were around them; the tiles whose cells another system wrote since this
    /// pass last ran (a tile created, loaded or dug, <c>Changed&lt;DynamicBuffer&lt;TCell&gt;&gt;</c>) wake with the tiles
    /// around them, an empty buffer sized for the shape (the pool keeps lists of the tile's cells:
    /// <see cref="TileCells.Pool{TCell, TTile}"/>). With <paramref name="sleeps"/> (the method
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
        _sleeps = sleeps && _awake == 0;
        if (sleeps && _awake > 0) _awake--;
        _maxSweeps = maxSweeps > 0 ? maxSweeps : Cells;
        _color = 0;
        var index = LookupExtensions.Prepared<TGrid, TKey>(_world) as ValueIndex<TGrid, TKey>;
        if (!ReferenceEquals(index, _index))
        {
            _index = index;
            _membership = -1;
        }
        int capacity = _world.Meta.Length;
        if (_entries.Length < capacity) Grow(capacity);
        if (_queues.Length == 0)
        {
            _queues = new int[TColors.Count][];
            for (int c = 0; c < _queues.Length; c++) _queues[c] = [];
            _queued = new int[TColors.Count];
        }
        _wokenOnly = _sleeps && _listed;
        if (_index.Membership != _membership) Check();
        WakeChanged();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity)
    {
        Array.Resize(ref _entries, Math.Max(capacity, _entries.Length * 2));
        Array.Resize(ref _blocks, (_entries.Length + 4095) >> 12);
    }

    /// <summary>
    /// Tiles joined, left or moved in the grid: the tiles around each one that left (destroyed, its tile component or
    /// buffer removed, its key changed) wake, and its state goes. A tile that joined wakes with its first step.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Check()
    {
        _membership = _index.Membership;
        _wokenOnly = false; // a tile that joined steps first in a walk of every tile
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
        entry.Queued = false; // its index may stay in a queue: a walk skips it (not live), a later tile at it queues anew
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
                Wake(ref entry, tile.Index);
                entry.FrontRows.AsSpan().Fill(AllRows); // written by another: any row may hold cells
            }
            else _wokenOnly = false; // a tile that has not stepped here yet: it joins in a walk of every tile
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
        if (!Unsafe.IsNullRef(ref entry)) Wake(ref entry, id.Index);
    }

    /// <summary>
    /// The tile at entity index <paramref name="index"/> may change: it steps in the next walk of its color, which a
    /// sleeping pass finds in the color's queue. On the system's thread only.
    /// </summary>
    private void Wake(ref Entry entry, int index)
    {
        entry.Dirty = true;
        if (!_sleeps || entry.Queued) return; // a pass that steps every tile keeps no queue (the next one walks them all)
        entry.Queued = true;
        int color = ColorOf(in entry);
        var queue = _queues[color];
        int count = _queued[color];
        if (count == queue.Length) Array.Resize(ref _queues[color], Math.Max(16, count * 2)); // cold: the covariance check of a ref into int[][] once per growth
        _queues[color][count] = index;
        _queued[color] = count + 1;
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
        Clear(frame.Written);
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
            Clear(frame.Touched);
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorOf(in Entry entry) => Jacobi ? 0 : TColors.Of(entry.Key);

    /// <summary>A row set per layer cleared: one store for a 2D tile (no call into the span helpers).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Clear(ulong[] layers)
    {
        if (TTile.Height == 1) layers[0] = 0;
        else Array.Clear(layers);
    }

    /// <summary>
    /// A walk of the tiles of the color running that woke (<see cref="ITileStep.Step"/> on each, on this thread), when
    /// the pass sleeps and its queues are complete: true. False when the pass must walk every tile of the grid (it does
    /// not sleep, a tile joined or left, or the last pass stepped every tile); the generated pass then walks the query.
    /// O(tiles woken).
    /// </summary>
    public bool StepWoken<TJob>(ref TJob job) where TJob : struct, ITileStep, allows ref struct
    {
        if (!TakeQueue()) return false;
        var world = _world;
        var walk = _walk;
        for (int n = 0; n < _walked; n++) job.Step(new Entity(world, walk[n]));
        return true;
    }

    /// <summary>
    /// <see cref="StepWoken{TJob}"/> for a parallel method: the tiles woken in chunks of at most
    /// <paramref name="chunkSize"/> tiles (the method's <c>ChunkSize</c>, not rounded to 64 rows: a tile is heavy and a
    /// walk of the woken short), at least about four per thread, on the world's chunk runner.
    /// </summary>
    public bool StepWokenParallel<TJob>(ref TJob job, int chunkSize) where TJob : struct, ITileStep
    {
        if (!TakeQueue()) return false;
        if (_walked == 0) return true;
        var run = new WokenRun<TJob> { Job = job, Walk = _walk, World = _world };
        // About four chunks per thread at least: a few woken tiles, each heavy, are spread over every thread.
        int size = Math.Max(1, Math.Min(chunkSize > 0 ? chunkSize : int.MaxValue, _walked / (4 * _world.ChunkThreads)));
        _world.ParallelForChunks(_walked, ref run, size);
        return true;
    }

    /// <summary>The tiles a chunk of a parallel walk of the woken steps.</summary>
    private struct WokenRun<TJob> : IForRange where TJob : struct, ITileStep
    {
        public TJob Job;
        public EntityId[] Walk;
        public World World;

        public void Execute(int start, int end)
        {
            for (int n = start; n < end; n++) Job.Step(new Entity(World, Walk[n]));
        }
    }

    /// <summary>
    /// The queue of the color running taken: the tiles in it that may still change become the walk (each once: a
    /// tile is queued while it is not), or, when this pass walks every tile, the queue is dropped (the walk finds them).
    /// </summary>
    private bool TakeQueue()
    {
        var queue = _queues[_color];
        int count = _queued[_color];
        _queued[_color] = 0;
        var entries = _entries;
        // An index left by a tile that went (Forget) may sit in the queue of another color than the tile at it now,
        // which is queued in its own: only an entry of this color leaves the queue here.
        if (!_wokenOnly)
        {
            for (int n = 0; n < count; n++)
            {
                ref var entry = ref entries[queue[n]];
                if (ColorOf(in entry) == _color) entry.Queued = false;
            }
            return false;
        }
        if (_walk.Length < count) _walk = new EntityId[Math.Max(count, _walk.Length * 2)];
        int walked = 0;
        for (int n = 0; n < count; n++)
        {
            int i = queue[n];
            ref var entry = ref entries[i];
            if (ColorOf(in entry) != _color) continue;
            entry.Queued = false;
            if (entry.Live && entry.Dirty) _walk[walked++] = new EntityId(i, entry.Version);
        }
        // Many woken: the walk of every tile in table order (its rows read in place, a skipped tile one test, chunks of
        // whole runs of rows) against a lookup per woken tile (TileWalk.Share); Enter steps only the woken (Dirty), and
        // Settle queues the next wakes either way.
        if ((long)walked * TileWalk.Share >= _world.Count<DynamicBuffer<TCell>, TGrid>()) return false;
        _walked = walked;
        return true;
    }

    /// <summary>A tile steps for the first time at its index: its state, awake, any row of its cells possibly set.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Join(ref Entry entry, Entity tile)
    {
        if (entry.Live) Forget(ref entry); // an earlier tile at this index, destroyed since
        var key = tile.Find<TGrid>(true).Key;
        if (!Jacobi) CheckColors(key);
        if (!Jacobi && TColors.Of(key) != _color)
        {
            entry.Rewake = true; // it steps in its own color's walk: queued in Next (this thread may not queue it)
            Mark(tile.Index);
        }
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
        entry.Rewake = true; // not settled: it sweeps on in the next pass of its color (woken in Next)
        Mark(tile.Index);
        return false;
    }

    /// <summary>
    /// The end of <paramref name="tile"/>'s step on this thread. Jacobi: the rows of the second buffer the step did not
    /// write are zeroed (those it held from before, by vector stores), and the rows written or possibly set before are
    /// compared with the cells: a change and the edges it touched. Gauss-Seidel: the rows the sweeps wrote are compared
    /// with the cells and copied into them where they differ. A tile that changed is recorded for <see cref="Next"/>.
    /// </summary>
    public void Leave(Entity tile, in TileNeighbors around)
    {
        var frame = TileFrame<TCell, TTile>.Current;
        frame.Stepping = default;
        frame.Sweep = 0;
        ref var entry = ref _entries[tile.Index];
        ref var buffer = ref tile.Find<DynamicBuffer<TCell>>(true);
        bool changed = Jacobi ? LeaveJacobi(ref entry, frame, ref buffer) : LeaveInPlace(ref entry, frame, ref buffer);
        frame.Next = null;
        if (!changed) return;
        // Stamped, (Jacobi) swapped and woken in Next, on the system's thread: a stamp here would write the summary of its
        // 64-row word, a cache line the chunks beside share. The flag is the tile's own (no other thread writes its entry).
        entry.Changed = true;
        Mark(tile.Index);
        if (Jacobi) entry.Front = buffer.Store;
        if (!_sleeps) return;
        // The tiles across the edges that changed wake after this color (Next): the ids the step's exchange found, kept
        // in the tile's own state (no lookup in Next, and no memory another thread writes).
        var ids = entry.Around ??= new EntityId[32 - BitOperations.LeadingZeroCount(TileOffsets<TTile>.SlotMask)];
        for (uint slots = TileOffsets<TTile>.Facing(entry.Edges) & TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            ids[slot] = around[slot];
        }
    }

    /// <summary>
    /// The tile at entity index <paramref name="index"/> has news for <see cref="Next"/> (a walk of every tile finds it by
    /// its block of 64 indexes): the block's bit is read first and set once, so the chunks of a pass that changes
    /// everything do not take turns on one cache line with a locked instruction per tile.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Mark(int index)
    {
        if (_walked >= 0) return; // a walk of the woken: Next reads its list
        ref ulong word = ref _blocks[index >> 12];
        ulong bit = 1UL << ((index >> 6) & 63);
        if ((Volatile.Read(ref word) & bit) == 0) Interlocked.Or(ref word, bit);
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
        bool compare = _sleeps;
        // The first and last layer first, and in each the last row first: once every face's edge changed, the other
        // rows can tell nothing more (a field that moves everywhere compares a few rows per tile, not all of them).
        for (int k = 0, height = TTile.Height; k < height; k++)
        {
            int y = k == 0 ? 0 : k == 1 ? height - 1 : k - 1;
            ulong written = frame.Written[y];
            for (ulong stale = entry.NextRows[y] & ~written; stale != 0; stale &= stale - 1) // rows the step skipped: zero
                new Span<TCell>(next, (y * depth + BitOperations.TrailingZeroCount(stale)) * width, width).Clear();
            entry.NextRows[y] = written;
            if (!compare) continue;
            ulong rows = written | entry.FrontRows[y];
            if (rows == 0) continue;
            int z = 63 - BitOperations.LeadingZeroCount(rows);
            for (rows &= ~(1UL << z); ; rows &= rows - 1)
            {
                int at = (y * depth + z) * width;
                if (TileLayout.RowEdges<TCell, TTile>(new ReadOnlySpan<TCell>(cells, at, width), new ReadOnlySpan<TCell>(next, at, width), y, z, ref edges))
                {
                    changed = true;
                    if (edges == AllEdges)
                    {
                        compare = false;
                        break;
                    }
                }
                if (rows == 0) break;
                z = BitOperations.TrailingZeroCount(rows);
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
    /// cells are stamped as written, and the tiles across the edges that changed wake (Leave marked them). True while another color is to
    /// step; after the last, the next writes are newer than this pass's.
    /// </summary>
    public bool Next()
    {
        var world = _world;
        int count = 0;
        if (_walked >= 0)
        {
            var walk = _walk;
            for (int n = 0; n < _walked; n++) Settle(walk[n].Index, ref count, false);
        }
        else
        {
            // A Jacobi pass of every tile that does not sleep changed every tile: their swaps (each its own tile's
            // state and lists, the pool per thread) run on every thread, the stamps (rows of shared words) after.
            bool swapped = Jacobi && !_sleeps && world.ChunkThreads > 1 && _entries.Length >= TileWalk.Swaps;
            if (swapped)
            {
                var run = new SwapRun { Steps = this };
                world.ParallelForChunks((_entries.Length + 63) >> 6, ref run, 4);
            }
            var blocks = _blocks;
            for (int b = 0; b < blocks.Length; b++)
            {
                for (ulong bits = blocks[b]; bits != 0; bits &= bits - 1)
                {
                    int first = (b << 12) | (BitOperations.TrailingZeroCount(bits) << 6), end = Math.Min(first + 64, _entries.Length);
                    for (int i = first; i < end; i++) Settle(i, ref count, swapped);
                }
                blocks[b] = 0;
            }
        }
        _walked = -1;

        if (++_color < TColors.Count) return true;
        _listed = _sleeps; // a pass that slept queued every tile it woke
        // Nearly every tile changed: the next passes step them all without looking (AwakePasses).
        if (Jacobi && _sleeps && count * 8 >= world.Count<DynamicBuffer<TCell>, TGrid>() * 7) _awake = AwakePasses;
        _lastTick = world.ChangeTick;
        // Outside an update (a pass run by hand), a write made next would carry the stamp's tick: the tick moves on, as
        // between updates. Inside one, the next stage's tick is newer; a parallel stage's systems share the tick.
        if (!world.Gate.InParallel) world.NextChangeTick();
        return false;
    }

    /// <summary>
    /// The tile at entity index <paramref name="i"/> after the color running, when its step changed it (Jacobi: its second
    /// buffer swapped in; stamped; the tiles across the edges that changed woken) or ran out of sweeps (it wakes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Settle(int i, ref int count, bool swapped)
    {
        ref var entry = ref _entries[i];
        if (entry.Rewake)
        {
            entry.Rewake = false;
            Wake(ref entry, i);
        }
        if (!entry.Changed) return;
        entry.Changed = false;
        count++;
        var tile = new Entity(_world, i, entry.Version);
        if (Jacobi && !swapped)
        {
            Swap(ref entry, tile);
            Wake(ref entry, i); // one step of Jacobi is no fixed point: it steps again
        }
        // Stamped where it lives, read from its meta (no lookup through the handle): a tile that stepped is alive with its
        // cells unless a serial method changed structure since, which the version and the table's mask catch (no stamp
        // then, as Entity.Stamp: a destroyed tile or cells removed).
        ref var meta = ref _world.MetaRef(i);
        if (meta.Version == entry.Version && meta.Archetype is { } table && table.ComponentMask.HasBit(ComponentType<DynamicBuffer<TCell>>.Bit))
            table.Stamp<DynamicBuffer<TCell>>(meta.Slot, _world.ChangeTick);
        if (!_sleeps) return;
        for (uint slots = TileOffsets<TTile>.Facing(entry.Edges) & TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            var id = entry.Around[BitOperations.TrailingZeroCount(slots)];
            if (id != default) Wake(id); // a tile that has not stepped yet wakes as it joins
        }
    }

    /// <summary>
    /// The tiles of the 64-index groups [start, end) that changed take their step (<see cref="Swap"/>, woken: a pass that
    /// does not sleep keeps no queue), for a Jacobi pass that does not sleep on every thread; Settle stamps them after.
    /// </summary>
    private struct SwapRun : IForRange
    {
        public TileSteps<TCell, TTile, TGrid, TKey, TColors> Steps;

        public void Execute(int start, int end)
        {
            var entries = Steps._entries;
            var blocks = Steps._blocks;
            var world = Steps._world;
            for (int g = start; g < end; g++)
            {
                if ((blocks[g >> 6] & (1UL << g)) == 0) continue; // shift is mod 64
                for (int i = g << 6, last = Math.Min(i + 64, entries.Length); i < last; i++)
                {
                    ref var entry = ref entries[i];
                    if (!entry.Changed) continue;
                    Swap(ref entry, new Entity(world, i, entry.Version));
                    entry.Dirty = true; // Wake in a pass that keeps no queue
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="tile"/> takes its step: the lists of its cells and of its second buffer trade their
    /// arrays (the buffer keeps its list object, so no lookup of it), or, when its list is shared (a prefab's, a clone's, a
    /// snapshot's) or sized otherwise, the buffer takes the second list and the next step a new one.
    /// </summary>
    private static void Swap(ref Entry entry, Entity tile)
    {
        (entry.FrontRows, entry.NextRows) = (entry.NextRows, entry.FrontRows);
        var cells = entry.Front;
        var next = entry.Next;
        entry.Front = null;
        if (cells is { Shares: 0 } && cells.Count == next.Count)
        {
            (cells.Items, next.Items) = (next.Items, cells.Items);
            return;
        }
        ref var buffer = ref tile.Find<DynamicBuffer<TCell>>(true);
        buffer.Store = next;
        // A list others share is theirs to keep: the next step takes a new one.
        cells?.Recycle();
        entry.Next = null;
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining), StackTraceHidden]
    private static void ThrowColors(TKey key, TKey at) => throw new InvalidOperationException(
        $"The {typeof(TGrid).Name} tiles at {key} and {at} are neighbors of one color: a [GaussSeidel] step colors the tiles by the parity of their keys, which a grid that wraps around an odd number of tiles breaks");
}

/// <summary>
/// The job of a stepping tile's method (generated): <see cref="Step"/> runs it on one tile, as the query's walk would
/// (the tile's components and the method's filter checked), for <see cref="TileSteps{TCell, TTile, TGrid, TKey, TColors}.StepWoken{TJob}"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITileStep
{
    /// <summary>The method on <paramref name="tile"/>, when it has the components and passes the filter.</summary>
    void Step(Entity tile);
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
