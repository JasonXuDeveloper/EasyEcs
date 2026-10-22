using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

/// <summary>
/// The faces of a tile (<see cref="ITileShape"/>): north is −z, south +z, west −x, east +x, below −y, above +y. One
/// face names a neighbour (<see cref="ITile{TKey}.Beside"/>, <see cref="Tile{TCell, TTile}.Beside"/>); a set of them is
/// <see cref="ITileShape.Faces"/>, the faces that have neighbours.
/// </summary>
[Flags]
public enum TileFaces : byte
{
    /// <summary>No face.</summary>
    None = 0,
    /// <summary>−z: the row before row 0.</summary>
    North = 1,
    /// <summary>+z: the row after the last row.</summary>
    South = 2,
    /// <summary>−x: the cell before cell 0 of each row.</summary>
    West = 4,
    /// <summary>+x: the cell after the last cell of each row.</summary>
    East = 8,
    /// <summary>−y: the layer under layer 0.</summary>
    Below = 16,
    /// <summary>+y: the layer over the last layer.</summary>
    Above = 32,
    /// <summary>North, south, west and east: the neighbours of a 2D grid, or of the columns of a voxel world.</summary>
    Sides = North | South | West | East,
    /// <summary>The six faces: the neighbours of a 3D grid of cubes.</summary>
    All = Sides | Below | Above,
}

/// <summary>
/// The shape of a tile, on the component that makes an entity a tile of a grid (<see cref="ITile{TKey}"/>): the
/// entity's cells, <see cref="Width"/> × <see cref="Depth"/> × <see cref="Height"/> of them, are in its
/// <c>DynamicBuffer&lt;TCell&gt;</c> in rows of <see cref="Width"/> (x), <see cref="Depth"/> rows (z) per layer and
/// <see cref="Height"/> layers (y): cell (x, y, z) at <c>(y * Depth + z) * Width + x</c>. Static members, read once
/// by the JIT: a tile's code is compiled for its shape.
/// </summary>
public interface ITileShape
{
    /// <summary>The cells of a row (x), 1 or more.</summary>
    static abstract int Width { get; }

    /// <summary>The rows of a layer (z), 1 to 64 (the active rows of a layer are one 64-bit word).</summary>
    static abstract int Depth { get; }

    /// <summary>The layers (y), 1 or more: 1 (the default) is a 2D tile, which has no layers below or above.</summary>
    static virtual int Height => 1;

    /// <summary>
    /// The faces that have neighbours (default <see cref="TileFaces.Sides"/>); the halo of the others is the fill value.
    /// <see cref="TileFaces.Below"/> and <see cref="TileFaces.Above"/> count only for a tile of more than one layer.
    /// </summary>
    static virtual TileFaces Faces => TileFaces.Sides;
}

/// <summary>
/// A component that makes its entity a tile of a grid, found by its key (an indexed component,
/// <see cref="IIndexed{TKey}"/>): <c>record struct Chunk(ChunkPos Pos) : IComponent, ITile&lt;ChunkPos&gt;</c> with its
/// shape (<see cref="ITileShape"/>) and the key of each neighbour (<see cref="Beside"/>). A <c>[ForEach]</c> method
/// taking a <see cref="Tile{TCell, TTile}"/> of it gets the entity's cells with those of its neighbours, found through
/// the index.
/// </summary>
/// <typeparam name="TKey">The key: the tile's position in the grid.</typeparam>
public interface ITile<TKey> : ITileShape, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>The key of the tile beside the one at <paramref name="key"/> across <paramref name="face"/> (one face of <see cref="ITileShape.Faces"/>).</summary>
    static abstract TKey Beside(TKey key, TileFaces face);
}

/// <summary>
/// What a stencil reads around a cell, which decides the rows <see cref="PaddedTile{TCell, TTile}.Rows"/> visits: a row
/// is visited when one of the rows it reads holds a cell that is not zero. A stencil whose result is zero wherever
/// everything it reads is zero (water that does not flow out of nothing, heat that does not rise from cold) then skips
/// the quiet rows and gets the same result.
/// </summary>
[Flags]
public enum TileReads : byte
{
    /// <summary>The row itself, with the cell before and after it (west and east).</summary>
    Self = 1,
    /// <summary>The rows north and south of it in its layer.</summary>
    Sides = 2,
    /// <summary>The row over it, in the layer above.</summary>
    Above = 4,
    /// <summary>The row under it, in the layer below.</summary>
    Below = 8,
    /// <summary>The row and its four sides: a 2D five-point stencil.</summary>
    Layer = Self | Sides,
    /// <summary>The row, its sides and the rows over and under it: a 3D seven-point stencil.</summary>
    All = Self | Sides | Above | Below,
}

/// <summary>
/// The neighbours of a tile, found once per entity by generated code and shared by the method's
/// <see cref="Tile{TCell, TTile}"/> parameters of one grid. Generated code uses it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct TileNeighbours
{
    internal EntityId North, South, West, East, Below, Above;

    /// <summary>
    /// The neighbours of <paramref name="entity"/> across the faces of <typeparamref name="TTile"/>, found in
    /// <paramref name="index"/> (its built value index, <see cref="LookupExtensions.Prepared{T, TKey}"/>). Generated code calls this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TileNeighbours Of<TTile, TKey>(object index, Entity entity)
        where TTile : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        var grid = index as ValueIndex<TTile, TKey> ?? Unprepared<TTile, TKey>();
        TKey key = entity.Find<TTile>(true).Key;
        var faces = TTile.Faces;
        if (TTile.Height == 1) faces &= TileFaces.Sides;
        var around = default(TileNeighbours);
        if ((faces & TileFaces.North) != 0) around.North = Find(grid, TTile.Beside(key, TileFaces.North));
        if ((faces & TileFaces.South) != 0) around.South = Find(grid, TTile.Beside(key, TileFaces.South));
        if ((faces & TileFaces.West) != 0) around.West = Find(grid, TTile.Beside(key, TileFaces.West));
        if ((faces & TileFaces.East) != 0) around.East = Find(grid, TTile.Beside(key, TileFaces.East));
        if ((faces & TileFaces.Below) != 0) around.Below = Find(grid, TTile.Beside(key, TileFaces.Below));
        if ((faces & TileFaces.Above) != 0) around.Above = Find(grid, TTile.Beside(key, TileFaces.Above));
        return around;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EntityId Find<TTile, TKey>(ValueIndex<TTile, TKey> grid, TKey key)
        where TTile : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        grid.TryFind(key, out var found) ? found.Id : default;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueIndex<TTile, TKey> Unprepared<TTile, TKey>() where TTile : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        throw new ArgumentException($"The index passed is not the built index of {typeof(TTile).Name} by {typeof(TKey).Name}");

    /// <summary>The neighbour across <paramref name="face"/> (one face), default when there is none.</summary>
    internal readonly EntityId Across(TileFaces face) => face switch
    {
        TileFaces.North => North,
        TileFaces.South => South,
        TileFaces.West => West,
        TileFaces.East => East,
        TileFaces.Below => Below,
        TileFaces.Above => Above,
        _ => throw new ArgumentOutOfRangeException(nameof(face), face, "One face: North, South, West, East, Below or Above"),
    };
}

/// <summary>
/// The cells of a tile (an entity whose <typeparamref name="TTile"/> makes it one, <see cref="ITile{TKey}"/>) with those
/// of its neighbours: a <c>[ForEach]</c> parameter, as a CUDA block's tile and halo. <c>static void Diffuse(Tile&lt;Heat,
/// Plate&gt; heat, Mut&lt;DynamicBuffer&lt;NextHeat&gt;&gt; next)</c>: the generated job finds the neighbours by key
/// (one search per entity for every tile of the grid), and <see cref="Load(int, int, TCell, bool)"/> copies the layers
/// the stencil shades into a padded tile (<see cref="PaddedTile{TCell, TTile}"/>): the entity's cells in the middle,
/// a halo of one cell around them from the neighbours' facing rows, columns or layers, and the fill value where there
/// is no neighbour, so the stencil has no edge cases.
/// <para>
/// The contract: the system is scheduled as a reader of <c>DynamicBuffer&lt;TCell&gt;</c> and of <typeparamref name="TTile"/>
/// (and builds its index before the pass), so every neighbour is read as it was before the pass, and the method may run in
/// parallel; a method that writes either of them may not take the tile (EECS059). Write the step into another component
/// and commit it in another system (double buffering).
/// </para>
/// Valid for the call it is passed to (a ref struct).
/// </summary>
/// <typeparam name="TCell">The cell: the element of the entity's <c>DynamicBuffer&lt;TCell&gt;</c>.</typeparam>
/// <typeparam name="TTile">The component that places the entity in the grid, with the tile's shape.</typeparam>
public readonly ref struct Tile<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    private readonly Entity _entity;
    private readonly ref readonly TileNeighbours _around;

    /// <summary>The tile of <paramref name="entity"/>, whose neighbours are <paramref name="around"/>. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Tile(Entity entity, ref readonly TileNeighbours around)
    {
        _entity = entity;
        _around = ref around;
    }

    /// <summary>The tile's own cells, in the layout of <see cref="ITileShape"/>. O(1).</summary>
    public ReadOnlySpan<TCell> Cells
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entity.Find<DynamicBuffer<TCell>>(true).AsReadOnlySpan();
    }

    /// <summary>
    /// The neighbour across <paramref name="face"/> (one face), or default when the grid has no tile there or
    /// <typeparamref name="TTile"/> declares no neighbour on that face: read its other components through a
    /// <see cref="Lookup{T}"/>. O(1).
    /// </summary>
    public EntityId Beside(TileFaces face) => _around.Across(face);

    /// <summary>All the layers padded, the halo of a missing neighbour zero: <see cref="Load(int, int, TCell, bool)"/>.</summary>
    public PaddedTile<TCell, TTile> Load() => Load(0, TTile.Height - 1);

    /// <summary>
    /// Layers <paramref name="from"/> to <paramref name="to"/> padded into this thread's texture: each layer's cells in
    /// the middle, a ring of one cell around each layer (the facing column of the west and east neighbours, the facing
    /// row of the north and south ones, <paramref name="fill"/> at the four corners), and, for a tile of more than one
    /// layer, a layer under <paramref name="from"/> and over <paramref name="to"/> (the tile's own, or the facing layer
    /// of the neighbour below or above, its ring <paramref name="fill"/>). A face with no neighbour, or whose
    /// neighbour's buffer is empty, is <paramref name="fill"/>. With <paramref name="mask"/>, it records which rows hold
    /// a cell that is not zero (every bit), for <see cref="PaddedTile{TCell, TTile}.Rows"/>.
    /// <para>
    /// O(layers × (Width + 2) × (Depth + 2)), no allocation once the thread has its texture (allocated on its first load,
    /// for all the layers). The texture belongs to the thread and to this tile type: the next load of
    /// <typeparamref name="TCell"/> and <typeparamref name="TTile"/> on the thread replaces it, so a method takes one tile
    /// per cell type and grid. Throws when the entity's buffer, or a neighbour's that is not empty, holds fewer cells
    /// than the shape, or the layers are not 0 ≤ from ≤ to &lt; Height.
    /// </para>
    /// </summary>
    public PaddedTile<TCell, TTile> Load(int from, int to, TCell fill = default, bool mask = true)
    {
        Shape<TTile>.Check();
        if (from < 0 || from > to || to >= TTile.Height) ThrowLayers(from, to);
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, halo = height > 1 ? 1 : 0;
        int row = width + 2, layer = row * (depth + 2), cells = width * depth * height;
        int layers = to - from + 1 + 2 * halo;
        ref TCell texels = ref TileMemory<TCell, TTile>.Texels();
        ref TileMask masks = ref TileMemory<TCell, TTile>.Masks();

        var self = Own(cells);
        var north = Of(_around.North, cells);
        var south = Of(_around.South, cells);
        var west = Of(_around.West, cells);
        var east = Of(_around.East, cells);
        ref TCell s = ref MemoryMarshal.GetReference(self);
        ref TCell n = ref MemoryMarshal.GetReference(north);
        ref TCell so = ref MemoryMarshal.GetReference(south);
        ref TCell w = ref MemoryMarshal.GetReference(west);
        ref TCell e = ref MemoryMarshal.GetReference(east);
        bool hasNorth = !north.IsEmpty, hasSouth = !south.IsEmpty, hasWest = !west.IsEmpty, hasEast = !east.IsEmpty;
        bool zero = Cell.IsZero(fill);
        ulong southBit = 1UL << (depth - 1);

        // The bounds: every read below is a row of the entity's buffer or of a neighbour's that is not empty, each of
        // which holds at least `cells` (Own / Of checked it), at a layer 0 <= y < Height and a row 0 <= z < Depth; every
        // write is inside layer t < layers of the texture, which holds (Height + 2 * halo) layers.
        for (int t = 0; t < layers; t++)
        {
            int y = from - halo + t;
            ref TCell at = ref Unsafe.Add(ref texels, t * layer);
            if ((uint)y >= (uint)height)
            {
                // Under layer 0 or over the last: the neighbour's facing layer, its ring the fill.
                var across = Of(y < 0 ? _around.Below : _around.Above, cells);
                var edge = Edge(ref at, across, y < 0 ? height - 1 : 0, fill, zero, mask);
                if (mask) Unsafe.Add(ref masks, t) = edge;
                continue;
            }

            ulong rows = 0;
            for (int z = 0; z < depth; z++)
            {
                int from0 = (y * depth + z) * width;
                ref TCell into = ref Unsafe.Add(ref at, (z + 1) * row + 1);
                bool any = Cell.Copy(ref into, ref Unsafe.Add(ref s, from0), width);
                TCell before = hasWest ? Unsafe.Add(ref w, from0 + width - 1) : fill;
                TCell after = hasEast ? Unsafe.Add(ref e, from0) : fill;
                Unsafe.Add(ref into, -1) = before;
                Unsafe.Add(ref into, width) = after;
                if (mask && (any || !Cell.IsZero(before) || !Cell.IsZero(after))) rows |= 1UL << z;
            }

            // The north neighbour's last row and the south neighbour's first, and the four corners.
            ref TCell top = ref Unsafe.Add(ref at, 1);
            ref TCell bottom = ref Unsafe.Add(ref at, (depth + 1) * row + 1);
            bool northWet = hasNorth ? Cell.Copy(ref top, ref Unsafe.Add(ref n, (y * depth + depth - 1) * width), width) : Cell.Fill(ref top, width, fill, zero);
            bool southWet = hasSouth ? Cell.Copy(ref bottom, ref Unsafe.Add(ref so, y * depth * width), width) : Cell.Fill(ref bottom, width, fill, zero);
            at = fill;
            Unsafe.Add(ref top, width) = fill;
            Unsafe.Add(ref bottom, -1) = fill;
            Unsafe.Add(ref bottom, width) = fill;
            if (mask) Unsafe.Add(ref masks, t) = new TileMask(rows, (northWet ? 1UL : 0) | (southWet ? southBit : 0));
        }

        return new PaddedTile<TCell, TTile>(ref texels, ref (mask ? ref masks : ref Unsafe.NullRef<TileMask>()), from, to);
    }

    /// <summary>
    /// A layer under or over the tile: <paramref name="source"/>'s layer <paramref name="y"/> in the middle (the fill
    /// when it is empty), the ring the fill.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TileMask Edge(ref TCell at, ReadOnlySpan<TCell> source, int y, TCell fill, bool zero, bool mask)
    {
        int width = TTile.Width, depth = TTile.Depth, row = width + 2;
        bool wet = Cell.Fill(ref at, row * (depth + 2), fill, zero);
        ulong all = depth == 64 ? ulong.MaxValue : (1UL << depth) - 1;
        if (source.IsEmpty) return mask && wet ? new TileMask(all, 1UL | 1UL << (depth - 1)) : default;
        ulong rows = 0;
        ref TCell s = ref MemoryMarshal.GetReference(source);
        for (int z = 0; z < depth; z++)
            if (Cell.Copy(ref Unsafe.Add(ref at, (z + 1) * row + 1), ref Unsafe.Add(ref s, (y * depth + z) * width), width) || wet)
                rows |= 1UL << z;
        return mask ? new TileMask(rows, wet ? 1UL | 1UL << (depth - 1) : 0) : default;
    }

    private ReadOnlySpan<TCell> Own(int cells)
    {
        var own = _entity.Find<DynamicBuffer<TCell>>(true).AsReadOnlySpan();
        if (own.Length < cells) ThrowCells(_entity.Id, own.Length, cells);
        return own;
    }

    /// <summary>The cells of <paramref name="id"/>: empty when there is no such tile, or it has no cells.</summary>
    private ReadOnlySpan<TCell> Of(EntityId id, int cells)
    {
        if (id == default) return default;
        ref var buffer = ref new Entity(_entity.World, id).Find<DynamicBuffer<TCell>>(false);
        if (Unsafe.IsNullRef(ref buffer)) return default;
        var span = buffer.AsReadOnlySpan();
        if (span.Length != 0 && span.Length < cells) ThrowCells(id, span.Length, cells);
        return span;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLayers(int from, int to) =>
        throw new ArgumentOutOfRangeException(nameof(from), $"Layers {from} to {to} of a {typeof(TTile).Name} tile of {TTile.Height} layers");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCells(EntityId id, int length, int cells) =>
        throw new InvalidOperationException($"The DynamicBuffer<{typeof(TCell).Name}> of {id} holds {length} cells; a {typeof(TTile).Name} tile has {cells}");
}

/// <summary>A layer's active rows: bit z for row z (with the cell before and after it), and the halo rows north (bit 0) and south (bit Depth − 1).</summary>
internal readonly record struct TileMask(ulong Rows, ulong Ends);

/// <summary>
/// Layers of a tile padded with their halo (<see cref="Tile{TCell, TTile}.Load(int, int, TCell, bool)"/>): the texture a
/// stencil samples. Walk the rows to shade with <see cref="Rows"/> (the active ones, by counting trailing zeros) and
/// sample around a row with <see cref="At"/>: every sample one cell, row or layer away stays inside the texture,
/// which is checked when the offsets are not constants and folded away when they are. Valid until the thread loads
/// this tile type again.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly ref struct PaddedTile<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    private readonly ref TCell _texels;
    private readonly ref TileMask _masks;
    private readonly int _from, _to;

    internal PaddedTile(ref TCell texels, ref TileMask masks, int from, int to)
    {
        _texels = ref texels;
        _masks = ref masks;
        _from = from;
        _to = to;
    }

    /// <summary>The first layer loaded (the halo adds one under it).</summary>
    public int From => _from;

    /// <summary>The last layer loaded (the halo adds one over it).</summary>
    public int To => _to;

    /// <summary>
    /// Cell (x, y, z) of the texture: x from −1 to Width, z from −1 to Depth, y from From − 1 to To + 1 (From to To for a
    /// 2D tile). Throws outside. O(1).
    /// </summary>
    public ref readonly TCell this[int x, int y, int z]
    {
        get
        {
            int halo = TTile.Height > 1 ? 1 : 0;
            if ((uint)(x + 1) > (uint)(TTile.Width + 1) || (uint)(z + 1) > (uint)(TTile.Depth + 1)
                || (uint)(y - _from + halo) > (uint)(_to - _from + 2 * halo))
                Cell.ThrowOffset();
            return ref Unsafe.Add(ref _texels, (y - _from + halo) * Cell.Layer<TTile>() + (z + 1) * (TTile.Width + 2) + x + 1);
        }
    }

    /// <summary>
    /// The rows of layers From to To that a stencil reading <paramref name="reads"/> must shade, layer by layer, in
    /// order (<c>foreach (var row in padded.Rows(TileReads.Layer))</c>): those with a cell that is not zero among the
    /// rows it reads. O(1) per row visited and per layer. Throws when the tile was loaded without its mask.
    /// </summary>
    public TileRows<TTile> Rows(TileReads reads = TileReads.All)
    {
        if (Unsafe.IsNullRef(ref _masks)) Cell.ThrowNoMask();
        return new TileRows<TTile>(ref _masks, _from, _to, reads);
    }

    /// <summary>
    /// The texture at the first cell of <paramref name="row"/> (a row of layers From to To, from <see cref="Rows"/> of
    /// this tile or of another loaded with the same layers): its samples. Throws for a row outside. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TileCursor<TCell, TTile> At(TileRow<TTile> row)
    {
        if ((uint)(row.Y - _from) > (uint)(_to - _from) || (uint)row.Z >= (uint)TTile.Depth) Cell.ThrowOffset();
        int halo = TTile.Height > 1 ? 1 : 0;
        return new TileCursor<TCell, TTile>(ref Unsafe.Add(ref _texels,
            (row.Y - _from + halo) * Cell.Layer<TTile>() + (row.Z + 1) * (TTile.Width + 2) + 1));
    }
}

/// <summary>A row of a tile: layer <see cref="Y"/>, row <see cref="Z"/>, from <see cref="PaddedTile{TCell, TTile}.Rows"/>.</summary>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly struct TileRow<TTile> where TTile : struct, IComponent, ITileShape
{
    /// <summary>The row <paramref name="z"/> of layer <paramref name="y"/>.</summary>
    public TileRow(int y, int z)
    {
        Y = y;
        Z = z;
    }

    /// <summary>The layer.</summary>
    public int Y { get; }

    /// <summary>The row in the layer.</summary>
    public int Z { get; }

    /// <summary>Where the row's first cell is in the tile's buffer (and in any buffer of the same layout): <c>(Y * Depth + Z) * Width</c>.</summary>
    public int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Y * TTile.Depth + Z) * TTile.Width;
    }
}

/// <summary>
/// The active rows of a padded tile (<see cref="PaddedTile{TCell, TTile}.Rows"/>), walked with <c>foreach</c>: per layer
/// a word of the rows the stencil may change, then a trailing-zero count per row.
/// </summary>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public ref struct TileRows<TTile> where TTile : struct, IComponent, ITileShape
{
    private readonly ref TileMask _masks;
    private readonly int _base, _last;
    private readonly TileReads _reads;
    private int _layer;
    private ulong _rows;
    private int _z;

    internal TileRows(ref TileMask masks, int from, int to, TileReads reads)
    {
        int halo = TTile.Height > 1 ? 1 : 0;
        _masks = ref masks;
        _base = from - halo;            // the layer of texture layer 0
        _layer = halo - 1;              // the texture layer before From's
        _last = to - from + halo;       // To's
        _reads = reads;
        _rows = 0;
        _z = 0;
    }

    /// <summary>Itself: <c>foreach</c> walks it.</summary>
    public readonly TileRows<TTile> GetEnumerator() => this;

    /// <summary>The row the walk is at.</summary>
    public readonly TileRow<TTile> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_base + _layer, _z);
    }

    /// <summary>Moves to the next active row; false after the last.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (_rows == 0)
        {
            if (_layer == _last) return false;
            _rows = Visit(++_layer);
        }

        _z = BitOperations.TrailingZeroCount(_rows);
        _rows &= _rows - 1;
        return true;
    }

    /// <summary>The rows of texture layer <paramref name="layer"/> the stencil reads something in (its neighbours are in the texture: halo layers).</summary>
    private readonly ulong Visit(int layer)
    {
        int depth = TTile.Depth;
        ref var here = ref Unsafe.Add(ref _masks, layer);
        ulong rows = here.Rows, visit = 0;
        if ((_reads & TileReads.Self) != 0) visit |= rows;
        if ((_reads & TileReads.Sides) != 0) visit |= rows << 1 | rows >> 1 | here.Ends;
        if (TTile.Height > 1)
        {
            if ((_reads & TileReads.Above) != 0) visit |= Unsafe.Add(ref here, 1).Rows;
            if ((_reads & TileReads.Below) != 0) visit |= Unsafe.Add(ref here, -1).Rows;
        }

        return depth == 64 ? visit : visit & ((1UL << depth) - 1);
    }
}

/// <summary>
/// A padded tile at the first cell of a row (<see cref="PaddedTile{TCell, TTile}.At"/>): the cells around it, one or
/// a vector at a time. An offset is in cells: dx from −1 (the cell before the row) to Width (the cell after it), dy
/// and dz from −1 to 1 (dy 0 for a 2D tile); a vector of cells must end by the cell after the row. Constant offsets are
/// checked by the JIT, others by a compare.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly ref struct TileCursor<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    private readonly ref TCell _at;

    internal TileCursor(ref TCell at) => _at = ref at;

    /// <summary>The cell at (dx, dy, dz) from the row's first cell. O(1).</summary>
    public ref readonly TCell this[int dx, int dy, int dz]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.Add(ref _at, Offset(dx, dy, dz, Unsafe.SizeOf<TCell>()));
    }

    /// <summary>The 128 bits of cells from (dx, dy, dz) as lanes of <typeparamref name="TLane"/> (a primitive: <c>byte</c> lanes of byte cells). O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<TLane> Load128<TLane>(int dx = 0, int dy = 0, int dz = 0) =>
        Vector128.LoadUnsafe(ref Unsafe.As<TCell, TLane>(ref Unsafe.Add(ref _at, Offset(dx, dy, dz, Vector128<byte>.Count))));

    /// <summary>The 256 bits of cells from (dx, dy, dz) as lanes of <typeparamref name="TLane"/>. O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector256<TLane> Load256<TLane>(int dx = 0, int dy = 0, int dz = 0) =>
        Vector256.LoadUnsafe(ref Unsafe.As<TCell, TLane>(ref Unsafe.Add(ref _at, Offset(dx, dy, dz, Vector256<byte>.Count))));

    /// <summary>A <c>Vector&lt;TLane&gt;</c> of cells from (dx, dy, dz) (the machine's vector width). O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector<TLane> LoadVector<TLane>(int dx = 0, int dy = 0, int dz = 0) =>
        Vector.LoadUnsafe(ref Unsafe.As<TCell, TLane>(ref Unsafe.Add(ref _at, Offset(dx, dy, dz, Vector<byte>.Count))));

    /// <summary>
    /// The offset in cells of (dx, dy, dz), whose <paramref name="bytes"/> stay inside the padded row the offset is in
    /// (cells −1 to Width), in the layers the texture holds: the invariant that lets the loads skip their bounds check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Offset(int dx, int dy, int dz, int bytes)
    {
        int size = Unsafe.SizeOf<TCell>();
        if (dx < -1 || (dx + 1) * size + bytes > (TTile.Width + 2) * size || (uint)(dz + 1) > 2u
            || (TTile.Height > 1 ? (uint)(dy + 1) > 2u : dy != 0))
            Cell.ThrowOffset();
        return dx + dy * Cell.Layer<TTile>() + dz * (TTile.Width + 2);
    }
}

/// <summary>A thread's texture and masks for a tile type: allocated on its first load for all the layers, then reused.</summary>
internal static class TileMemory<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    [ThreadStatic] private static TCell[] t_texels;
    [ThreadStatic] private static TileMask[] t_masks;

    /// <summary>This thread's texture (created on its first load).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TCell Texels() => ref MemoryMarshal.GetArrayDataReference(t_texels ?? Create());

    /// <summary>This thread's masks, a layer each: after <see cref="Texels"/>, which creates them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TileMask Masks() => ref MemoryMarshal.GetArrayDataReference(t_masks);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TCell[] Create()
    {
        int layers = TTile.Height + (TTile.Height > 1 ? 2 : 0);
        t_masks = new TileMask[layers];
        return t_texels = GC.AllocateUninitializedArray<TCell>(layers * Cell.Layer<TTile>());
    }
}

/// <summary>The checks of a tile shape, once per shape.</summary>
internal static class Shape<TTile> where TTile : struct, IComponent, ITileShape
{
    private static readonly bool Valid = TTile.Width >= 1 && TTile.Depth is >= 1 and <= 64 && TTile.Height >= 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check()
    {
        if (!Valid) Throw();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw() => throw new InvalidOperationException(
        $"{typeof(TTile).Name} is a {TTile.Width} x {TTile.Depth} x {TTile.Height} tile: Width and Height must be 1 or more, Depth 1 to 64");
}

/// <summary>Row copies and tests of cells, as bytes: whole vectors where the row has them.</summary>
internal static class Cell
{
    /// <summary>The cells of a padded layer of <typeparamref name="TTile"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Layer<TTile>() where TTile : ITileShape => (TTile.Width + 2) * (TTile.Depth + 2);

    /// <summary>
    /// Copies <paramref name="count"/> cells from <paramref name="source"/> to <paramref name="target"/>; whether one of
    /// them is not zero. Unchecked: the caller holds both ranges in bounds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Copy<TCell>(ref TCell target, ref TCell source, int count) where TCell : unmanaged
    {
        ref byte to = ref Unsafe.As<TCell, byte>(ref target);
        ref byte from = ref Unsafe.As<TCell, byte>(ref source);
        nuint bytes = (nuint)(count * Unsafe.SizeOf<TCell>());
        // A row's size is a constant of the shape, so one of these is the whole copy, unrolled: one or two vectors
        // (the second overlapping the first when the row is not a whole number of them), or a loop for long rows.
        if (Vector128.IsHardwareAccelerated && bytes == 16)
        {
            var v = Vector128.LoadUnsafe(ref from);
            v.StoreUnsafe(ref to);
            return v != Vector128<byte>.Zero;
        }
        if (Vector128.IsHardwareAccelerated && bytes is > 16 and <= 32 && !(Vector256.IsHardwareAccelerated && bytes == 32))
        {
            var a = Vector128.LoadUnsafe(ref from);
            var b = Vector128.LoadUnsafe(ref from, bytes - 16);
            a.StoreUnsafe(ref to);
            b.StoreUnsafe(ref to, bytes - 16);
            return (a | b) != Vector128<byte>.Zero;
        }
        if (Vector256.IsHardwareAccelerated && bytes == 32)
        {
            var v = Vector256.LoadUnsafe(ref from);
            v.StoreUnsafe(ref to);
            return v != Vector256<byte>.Zero;
        }
        if (Vector256.IsHardwareAccelerated && bytes is > 32 and <= 64)
        {
            var a = Vector256.LoadUnsafe(ref from);
            var b = Vector256.LoadUnsafe(ref from, bytes - 32);
            a.StoreUnsafe(ref to);
            b.StoreUnsafe(ref to, bytes - 32);
            return (a | b) != Vector256<byte>.Zero;
        }
        if (Vector256.IsHardwareAccelerated && bytes > 64)
        {
            var any = Vector256<byte>.Zero;
            nuint i = 0;
            for (; i + 32 <= bytes; i += 32)
            {
                var v = Vector256.LoadUnsafe(ref from, i);
                v.StoreUnsafe(ref to, i);
                any |= v;
            }
            if (i < bytes) // the last 32 bytes again, overlapping
            {
                var v = Vector256.LoadUnsafe(ref from, bytes - 32);
                v.StoreUnsafe(ref to, bytes - 32);
                any |= v;
            }
            return any != Vector256<byte>.Zero;
        }
        if (Vector128.IsHardwareAccelerated && bytes > 32)
        {
            var any = Vector128<byte>.Zero;
            nuint i = 0;
            for (; i + 16 <= bytes; i += 16)
            {
                var v = Vector128.LoadUnsafe(ref from, i);
                v.StoreUnsafe(ref to, i);
                any |= v;
            }
            if (i < bytes)
            {
                var v = Vector128.LoadUnsafe(ref from, bytes - 16);
                v.StoreUnsafe(ref to, bytes - 16);
                any |= v;
            }
            return any != Vector128<byte>.Zero;
        }

        ulong bits = 0;
        nuint j = 0;
        for (; j + 8 <= bytes; j += 8)
        {
            ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref from, j));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, j), v);
            bits |= v;
        }
        for (; j < bytes; j++)
        {
            byte v = Unsafe.Add(ref from, j);
            Unsafe.Add(ref to, j) = v;
            bits |= v;
        }
        return bits != 0;
    }

    /// <summary><paramref name="count"/> cells of <paramref name="fill"/> (<paramref name="zero"/>: it is zero); whether it is not zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Fill<TCell>(ref TCell target, int count, TCell fill, bool zero) where TCell : unmanaged
    {
        if (zero) Unsafe.InitBlockUnaligned(ref Unsafe.As<TCell, byte>(ref target), 0, (uint)(count * Unsafe.SizeOf<TCell>()));
        else MemoryMarshal.CreateSpan(ref target, count).Fill(fill);
        return !zero;
    }

    /// <summary>Whether every bit of <paramref name="cell"/> is zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsZero<TCell>(TCell cell) where TCell : unmanaged
    {
        if (Unsafe.SizeOf<TCell>() == 1) return Unsafe.As<TCell, byte>(ref cell) == 0;
        if (Unsafe.SizeOf<TCell>() == 2) return Unsafe.As<TCell, ushort>(ref cell) == 0;
        if (Unsafe.SizeOf<TCell>() == 4) return Unsafe.As<TCell, uint>(ref cell) == 0;
        if (Unsafe.SizeOf<TCell>() == 8) return Unsafe.As<TCell, ulong>(ref cell) == 0;
        return MemoryMarshal.AsBytes(new ReadOnlySpan<TCell>(in cell)).IndexOfAnyExcept((byte)0) < 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowOffset() => throw new ArgumentOutOfRangeException(null, "Outside the padded tile: a cell, row or layer past the halo");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowNoMask() => throw new InvalidOperationException("The tile was loaded without its mask (mask: false): it has no active rows");
}
