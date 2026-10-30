using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

/// <summary>
/// The faces of a tile (<see cref="ITileShape"/>): north is −z, south +z, west −x, east +x, below −y, above +y. One
/// face names a neighbor (<see cref="ITile{TKey}.Beside"/>, <see cref="Tile{TCell, TTile}.Beside(TileFaces)"/>); a set of
/// them is <see cref="ITileShape.Faces"/>, the faces that have neighbors.
/// </summary>
[Flags]
public enum TileFaces : byte
{
    /// <summary>No face.</summary>
    None = 0,
    /// <summary>−z: the rows before row 0.</summary>
    North = 1,
    /// <summary>+z: the rows after the last row.</summary>
    South = 2,
    /// <summary>−x: the cells before cell 0 of each row.</summary>
    West = 4,
    /// <summary>+x: the cells after the last cell of each row.</summary>
    East = 8,
    /// <summary>−y: the layers under layer 0.</summary>
    Below = 16,
    /// <summary>+y: the layers over the last layer.</summary>
    Above = 32,
    /// <summary>North, south, west and east: the neighbors of a 2D grid, or of the columns of a voxel world.</summary>
    Sides = North | South | West | East,
    /// <summary>The six faces: the neighbors of a 3D grid of cubes.</summary>
    All = Sides | Below | Above,
}

/// <summary>
/// Which tiles around a tile are its neighbors, by how many axes their offset moves along (<see cref="ITileShape.Neighborhood"/>):
/// the tiles across a face (von Neumann), also across an edge, or also across a corner (Moore). In 2D a tile has 4,
/// 8 and 8; in 3D 6, 18 and 26. Only the axes of <see cref="ITileShape.Faces"/> count: a voxel column with neighbors
/// on its sides only has 4, 8 and 8.
/// </summary>
public enum TileNeighborhood : byte
{
    /// <summary>The tiles across a face (offsets along one axis): a five- or seven-point stencil reads no more.</summary>
    Faces,
    /// <summary>Also the tiles across an edge (offsets along two axes): the corners of a 2D grid, a 19-point stencil.</summary>
    FacesAndEdges,
    /// <summary>Every tile around (offsets along up to three axes): a 9-point 2D or 27-point 3D stencil.</summary>
    All,
}

/// <summary>
/// What the halo holds where a tile has no neighbor (<see cref="ITileShape.Boundary"/>), the boundary condition of
/// the grid. A periodic grid needs none: its <see cref="ITile{TKey}.Beside"/> wraps, so every tile has neighbors.
/// </summary>
public enum TileBoundary : byte
{
    /// <summary>The border value of <see cref="Tile{TCell, TTile}.ExchangeHalo(int, int, TCell, bool)"/> (default zero): a Dirichlet boundary.</summary>
    Border,
    /// <summary>The tile's own edge cell, repeated: the halo cell at −k is cell 0.</summary>
    Clamp,
    /// <summary>The tile's own cells, mirrored at its edge: the halo cell at −k is cell k − 1 (a zero-flux, Neumann boundary).</summary>
    Mirror,
}

/// <summary>
/// The shape of a tile, on the component that makes an entity a tile of a grid (<see cref="ITile{TKey}"/>): the
/// entity's cells, <see cref="Width"/> × <see cref="Depth"/> × <see cref="Height"/> of them, are in its
/// <c>DynamicBuffer&lt;TCell&gt;</c> in rows of <see cref="Width"/> (x), <see cref="Depth"/> rows (z) per layer and
/// <see cref="Height"/> layers (y): cell (x, y, z) at <c>(y * Depth + z) * Width + x</c>. Static members, read once
/// by the JIT: a tile's code is compiled for its shape, every size and branch on it folded to a constant.
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
    /// The faces that have neighbors (default <see cref="TileFaces.Sides"/>); the halo of the others follows
    /// <see cref="Boundary"/>. <see cref="TileFaces.Below"/> and <see cref="TileFaces.Above"/> count only for a tile of
    /// more than one layer; a 1D tile (one row) declares <c>West | East</c>.
    /// </summary>
    static virtual TileFaces Faces => TileFaces.Sides;

    /// <summary>
    /// The width of the halo, in cells (default 1): the ghost cells of an MPI halo exchange, the apron of a CUDA tile.
    /// A stencil reaching r cells, rows or layers away needs r. At most the tile's extent along every axis with
    /// neighbors (and along every axis for <see cref="TileBoundary.Mirror"/>): a halo never reaches past the tile beside.
    /// </summary>
    static virtual int Halo => 1;

    /// <summary>Which tiles around are found and read into the halo's edges and corners (default <see cref="TileNeighborhood.Faces"/>).</summary>
    static virtual TileNeighborhood Neighborhood => TileNeighborhood.Faces;

    /// <summary>What the halo holds where there is no neighbor (default <see cref="TileBoundary.Border"/>).</summary>
    static virtual TileBoundary Boundary => TileBoundary.Border;
}

/// <summary>
/// A component that makes its entity a tile of a grid, found by its key (an indexed component,
/// <see cref="IIndexed{TKey}"/>): <c>record struct Chunk(ChunkPos Pos) : IComponent, ITile&lt;ChunkPos&gt;</c> with its
/// shape (<see cref="ITileShape"/>) and the key of each neighbor (<see cref="Beside"/>). A <c>[ForEach]</c> method
/// taking a <see cref="Tile{TCell, TTile}"/> of it gets the entity's cells with those of its neighbors, found through
/// the index.
/// </summary>
/// <typeparam name="TKey">The key: the tile's position in the grid.</typeparam>
public interface ITile<TKey> : ITileShape, IIndexed<TKey> where TKey : IEquatable<TKey>
{
    /// <summary>
    /// The key of the tile beside the one at <paramref name="key"/> across <paramref name="face"/> (one face of
    /// <see cref="ITileShape.Faces"/>). The tiles across an edge or a corner are found by stepping across one face at a
    /// time, x first, then z, then y. On a periodic grid it wraps (the last tile's east is the first).
    /// </summary>
    static abstract TKey Beside(TKey key, TileFaces face);
}

/// <summary>
/// What a stencil reads around a cell, which decides the rows <see cref="Tile{TCell, TTile}.Rows"/> visits: a row
/// is visited when one of the rows it reads holds a cell that is not zero (a row counts its halo cells, so a stencil
/// reading along x needs nothing more than the row). A stencil whose result is zero wherever everything it reads is
/// zero (water that does not flow out of nothing, heat that does not rise from cold) then skips the quiet rows and gets
/// the same result. A stencil's radius is the shape's <see cref="ITileShape.Halo"/>.
/// </summary>
[Flags]
public enum TileStencil : byte
{
    /// <summary>The row itself, with the cells before and after it (west and east).</summary>
    Self = 1,
    /// <summary>The rows north and south of it in its layer, with their halo cells (the 2D diagonals too).</summary>
    NorthSouth = 2,
    /// <summary>The rows over it, in the layers above.</summary>
    Above = 4,
    /// <summary>The rows under it, in the layers below.</summary>
    Below = 8,
    /// <summary>The rows north and south of it in the layers over and under it: the 3D edges and corners.</summary>
    Diagonals = 16,
    /// <summary>The row and its four sides: a 2D five- or nine-point stencil.</summary>
    Layer = Self | NorthSouth,
    /// <summary>The row, its sides and the rows over and under it: a 3D seven-point stencil.</summary>
    Cross = Self | NorthSouth | Above | Below,
    /// <summary>Every row around: a 3D 27-point stencil, and the safe default.</summary>
    All = Cross | Diagonals,
}

/// <summary>
/// The neighbors of a tile, found once per entity by generated code and shared by the method's
/// <see cref="Tile{TCell, TTile}"/> parameters of one grid: one id per offset (dx, dy, dz) in −1..1, default where there
/// is none. Generated code uses it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[InlineArray(27)]
public struct TileNeighbors
{
    private EntityId _first;

    /// <summary>
    /// The neighbors of <paramref name="entity"/> in grid <typeparamref name="TGrid"/> of shape
    /// <typeparamref name="TShape"/>'s faces and neighborhood (the grid's component, or <see cref="Columns{TTile}"/> of
    /// it), found in <paramref name="index"/> (its built value index, <see cref="LookupExtensions.Prepared{T, TKey}"/>):
    /// one index lookup per neighbor. Generated code calls this; compiled on its own, for the shape.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Of<TShape, TGrid, TKey>(object index, Entity entity, out TileNeighbors around)
        where TShape : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        var grid = index as ValueIndex<TGrid, TKey> ?? Unprepared<TGrid, TKey>();
        Find<TShape, TGrid, TKey>(grid, entity.Find<TGrid>(true).Key, out around);
    }

    /// <summary>The neighbors of the tile at <paramref name="key"/> in <paramref name="grid"/>, by <typeparamref name="TShape"/>'s faces and neighborhood.</summary>
    internal static void Find<TShape, TGrid, TKey>(ValueIndex<TGrid, TKey> grid, TKey key, out TileNeighbors around)
        where TShape : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        around = default;
        if (TShape.Neighborhood == TileNeighborhood.Faces)
        {
            // The faces alone (a constant): one Beside each, unrolled, the faces the shape lacks folded away.
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.North, 10, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.South, 16, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.West, 12, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.East, 14, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.Below, 4, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.Above, 22, ref around);
            return;
        }
        if ((TileOffsets<TShape>.SlotMask & ~TileOffsets<TShape>.PlaneSlots) == 0)
        {
            // A plane (a constant): each corner one Beside from the key across its west or east face (KeyAt's order, x
            // then z), unrolled with constant slots; 8 Besides for 8 neighbors instead of 12 and the slot arithmetic.
            // Beside only across a face the shape has (constant tests: slots 9, 12, 15 lie west, 11, 14, 17 east).
            var west = (TileOffsets<TShape>.SlotMask & 0b1001001u << 9) != 0 ? TGrid.Beside(key, TileFaces.West) : key;
            var east = (TileOffsets<TShape>.SlotMask & 0b1001001u << 11) != 0 ? TGrid.Beside(key, TileFaces.East) : key;
            Slot<TShape, TGrid, TKey>(grid, west, 12, ref around);
            Slot<TShape, TGrid, TKey>(grid, east, 14, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.North, 10, ref around);
            Face<TShape, TGrid, TKey>(grid, key, TileFaces.South, 16, ref around);
            Face<TShape, TGrid, TKey>(grid, west, TileFaces.North, 9, ref around);
            Face<TShape, TGrid, TKey>(grid, east, TileFaces.North, 11, ref around);
            Face<TShape, TGrid, TKey>(grid, west, TileFaces.South, 15, ref around);
            Face<TShape, TGrid, TKey>(grid, east, TileFaces.South, 17, ref around);
            return;
        }
        for (uint slots = TileOffsets<TShape>.SlotMask; slots != 0; slots &= slots - 1)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            if (grid.TryFindId(TileOffsets<TShape>.KeyAt<TGrid, TKey>(key, slot), out var found)) around[slot] = found;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Face<TShape, TGrid, TKey>(ValueIndex<TGrid, TKey> grid, TKey key, TileFaces face, int slot, ref TileNeighbors around)
        where TShape : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        if ((TileOffsets<TShape>.SlotMask >> slot & 1) != 0 && grid.TryFindId(TGrid.Beside(key, face), out var found)) around[slot] = found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Slot<TShape, TGrid, TKey>(ValueIndex<TGrid, TKey> grid, TKey at, int slot, ref TileNeighbors around)
        where TShape : struct, ITileShape where TGrid : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        if ((TileOffsets<TShape>.SlotMask >> slot & 1) != 0 && grid.TryFindId(at, out var found)) around[slot] = found;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueIndex<TTile, TKey> Unprepared<TTile, TKey>() where TTile : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        throw new ArgumentException($"The index passed is not the built index of {typeof(TTile).Name} by {typeof(TKey).Name}");
}

/// <summary>
/// The buffers of a grid's cells, sized from the tile's shape: generated code calls <see cref="Pool{TCell, TTile}"/> when
/// its assembly loads, once for each <see cref="Tile{TCell, TTile}"/> a method of it takes, so the pool of
/// <c>DynamicBuffer&lt;TCell&gt;</c> keeps lists of a tile's cells (a chunk unloaded hands its lists to the next one
/// loaded) and <see cref="DynamicBuffer{T}.FillPool"/> fills it with lists of that length, with no
/// <see cref="DynamicBuffer{T}.MaxPooledLength"/> line in user code. Generated code uses it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TileCells
{
    /// <summary>
    /// <see cref="DynamicBuffer{T}.MaxPooledLength"/> of <typeparamref name="TCell"/> raised to the cells of a
    /// <typeparamref name="TTile"/> tile (never lowered; nothing for a shape EECS072 rejects). O(1).
    /// </summary>
    public static void Pool<TCell, TTile>() where TCell : unmanaged where TTile : struct, ITileShape
    {
        if (TileShapeCheck<TTile>.Validate() != null) return; // the first exchange throws, with the reason
        long cells = (long)TTile.Width * TTile.Depth * TTile.Height;
        if (cells > Array.MaxLength) return;
        if (BufferStore<TCell>.MaxPooledLength < cells) BufferStore<TCell>.MaxPooledLength = (int)cells;
    }
}

/// <summary>The offsets around a tile of <typeparamref name="TTile"/>'s shape: slot (dx + 1) + 3 (dz + 1) + 9 (dy + 1).</summary>
internal static class TileOffsets<TTile> where TTile : struct, ITileShape
{
    /// <summary>The slots of the neighbors the shape has (its faces' axes, its neighborhood), not the tile's own (13).</summary>
    public static readonly uint SlotMask = Compute();

    /// <summary>The slots of the plane of the tile's own layer (dy = 0): 9 to 17, the tile's own (13) left out.</summary>
    public const uint PlaneSlots = 0b111_101_111u << 9;

    /// <summary>The key of the tile at <paramref name="slot"/> from the one at <paramref name="key"/>: one face at a time, x first, then z, then y.</summary>
    public static TKey KeyAt<TGrid, TKey>(TKey key, int slot) where TGrid : ITile<TKey> where TKey : IEquatable<TKey>
    {
        int dx = slot % 3 - 1, dz = slot / 3 % 3 - 1, dy = slot / 9 - 1;
        if (dx != 0) key = TGrid.Beside(key, dx < 0 ? TileFaces.West : TileFaces.East);
        if (dz != 0) key = TGrid.Beside(key, dz < 0 ? TileFaces.North : TileFaces.South);
        if (dy != 0) key = TGrid.Beside(key, dy < 0 ? TileFaces.Below : TileFaces.Above);
        return key;
    }

    /// <summary>
    /// The slots of the neighbors that read an edge of <paramref name="edges"/> into their halo: the tile across each
    /// face, and, in the shape's neighborhood, across each edge and corner of two or three of those faces. A table of
    /// the 64 sets of faces: a stepping pass reads it per tile that changed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Facing(TileFaces edges) => FacingSlots[(int)edges & 63];

    private static readonly uint[] FacingSlots = ComputeFacing();

    private static uint[] ComputeFacing()
    {
        var table = new uint[64];
        for (int edges = 0; edges < 64; edges++) table[edges] = FacingOf((TileFaces)edges);
        return table;
    }

    private static uint FacingOf(TileFaces edges)
    {
        uint slots = 0;
        for (uint all = SlotMask; all != 0; all &= all - 1)
        {
            int slot = BitOperations.TrailingZeroCount(all);
            int dx = slot % 3 - 1, dz = slot / 3 % 3 - 1, dy = slot / 9 - 1;
            if (dx != 0 && (edges & (dx < 0 ? TileFaces.West : TileFaces.East)) == 0) continue;
            if (dz != 0 && (edges & (dz < 0 ? TileFaces.North : TileFaces.South)) == 0) continue;
            if (dy != 0 && (edges & (dy < 0 ? TileFaces.Below : TileFaces.Above)) == 0) continue;
            slots |= 1u << slot;
        }
        return slots;
    }

    /// <summary>The slot of offset (dx, dy, dz).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Slot(int dx, int dy, int dz) => dx + 1 + 3 * (dz + 1) + 9 * (dy + 1);

    /// <summary>The slot of one face.</summary>
    public static int Slot(TileFaces face) => face switch
    {
        TileFaces.North => Slot(0, 0, -1),
        TileFaces.South => Slot(0, 0, 1),
        TileFaces.West => Slot(-1, 0, 0),
        TileFaces.East => Slot(1, 0, 0),
        TileFaces.Below => Slot(0, -1, 0),
        TileFaces.Above => Slot(0, 1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(face), face, "One face: North, South, West, East, Below or Above"),
    };

    private static uint Compute()
    {
        var faces = TTile.Faces;
        if (TTile.Height == 1) faces &= TileFaces.Sides;
        int axes = (int)TTile.Neighborhood + 1;
        uint slots = 0;
        for (int slot = 0; slot < 27; slot++)
        {
            int dx = slot % 3 - 1, dz = slot / 3 % 3 - 1, dy = slot / 9 - 1;
            if ((dx | dy | dz) == 0 || Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > axes) continue;
            if (dx != 0 && (faces & (dx < 0 ? TileFaces.West : TileFaces.East)) == 0) continue;
            if (dz != 0 && (faces & (dz < 0 ? TileFaces.North : TileFaces.South)) == 0) continue;
            if (dy != 0 && (faces & (dy < 0 ? TileFaces.Below : TileFaces.Above)) == 0) continue;
            slots |= 1u << slot;
        }
        return slots;
    }
}

/// <summary>The buffers of a tile and its neighbors, by slot (<see cref="TileOffsets{TTile}"/>): null where there are no cells.</summary>
[InlineArray(27)]
internal struct TileSources<TCell>
{
    private TCell[] _first;
}

/// <summary>
/// The cells of a tile (an entity whose <typeparamref name="TTile"/> makes it one, <see cref="ITile{TKey}"/>) with those
/// of its neighbors: a <c>[ForEach]</c> parameter, as a CUDA block's tile and its apron, or an MPI rank's subdomain
/// and its ghost cells. The generated job finds the neighbors by key (one search per neighbor and entity for every tile
/// of the grid), and <see cref="ExchangeHalo(int, int, TCell, bool)"/> copies the layers the stencil shades into the
/// tile's padded texture: the entity's cells in the middle, a halo of <see cref="ITileShape.Halo"/> cells around them
/// from the neighbors' facing cells, and the boundary (<see cref="ITileShape.Boundary"/>) where there is no neighbor,
/// so the stencil has no edge cases. <see cref="Rows"/> walks the rows to shade, <see cref="At"/> samples around one.
/// <para>
/// Taken by value (<c>Tile&lt;Heat, Plate&gt; heat</c>) it reads: the system is scheduled as a reader of
/// <c>DynamicBuffer&lt;TCell&gt;</c> and of the grid's component (and builds its index before the pass), so every
/// neighbor is read as it was before the pass, and the method may run in parallel. Taken by <c>ref</c>
/// (<c>static void Diffuse(ref Tile&lt;Heat, Plate&gt; heat)</c>) the method steps the tile: it writes the step's rows
/// through <see cref="Write"/>, into a second buffer the generated pass keeps beside the cells and swaps with them once
/// every tile stepped (Jacobi, MPI's u / v swap: the rows it skips are written as zero), or in place, color by color,
/// sweeping until the tile settles (<see cref="GaussSeidelAttribute">[GaussSeidel]</see>). The pass finds the faces
/// whose edge changed from the rows written, and steps only the tiles that may change: those whose cells or whose
/// neighbors' facing edges changed since they last stepped.
/// </para>
/// Valid for the call it is passed to (a ref struct).
/// </summary>
/// <typeparam name="TCell">The cell: the element of the entity's <c>DynamicBuffer&lt;TCell&gt;</c>.</typeparam>
/// <typeparam name="TTile">The tile's shape: the component that places the entity in the grid, or <see cref="Columns{TTile}"/> of it.</typeparam>
public ref struct Tile<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    private readonly Entity _entity;
    private readonly ref readonly TileNeighbors _around;
    private readonly TileFrame<TCell, TTile> _frame;

    // What ExchangeHalo left in this thread's texture, kept in the tile itself (registers in a stencil's row loop): the
    // texture at cell (0, from, 0), and the layers exchanged. Null until this tile (this copy of it) exchanged its halo:
    // reads then go through the frame, which checks that the texture is this tile's.
    private ref TCell _origin;
    private int _from, _to;

    /// <summary>The tile of <paramref name="entity"/>, whose neighbors are <paramref name="around"/>. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Tile(Entity entity, ref readonly TileNeighbors around)
    {
        _entity = entity;
        _around = ref around;
        _frame = TileFrame<TCell, TTile>.Current;
    }

    /// <summary>The tile's own cells, in the layout of <see cref="ITileShape"/>. O(1).</summary>
    public readonly ReadOnlySpan<TCell> Cells
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entity.Find<DynamicBuffer<TCell>>(true).AsReadOnlySpan();
    }

    /// <summary>
    /// The neighbor across <paramref name="face"/> (one face), or default when the grid has no tile there or
    /// <typeparamref name="TTile"/> declares no neighbor on that face: read its other components through a
    /// <see cref="Lookup{T}"/>. O(1).
    /// </summary>
    public readonly EntityId Beside(TileFaces face) => _around[TileOffsets<TTile>.Slot(face)];

    /// <summary>
    /// The neighbor at offset (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>), each −1 to 1 (the
    /// tile across a face, an edge or a corner), or default when there is none or the shape's neighborhood leaves it
    /// out. O(1).
    /// </summary>
    public readonly EntityId Beside(int dx, int dy, int dz)
    {
        if ((uint)(dx + 1) > 2u || (uint)(dy + 1) > 2u || (uint)(dz + 1) > 2u) TileLayout.ThrowOffset();
        return _around[TileOffsets<TTile>.Slot(dx, dy, dz)];
    }

    /// <summary>All the layers with their halo, the halo of a missing neighbor zero: <see cref="ExchangeHalo(int, int, TCell, bool)"/>.</summary>
    public void ExchangeHalo() => ExchangeHalo(0, TTile.Height - 1);

    /// <summary>
    /// Layers <paramref name="from"/> to <paramref name="to"/> copied with their halo into this thread's texture of the
    /// tile, which <see cref="Rows"/>, <see cref="At"/> and the indexer read: each layer's cells in the middle, a ring of
    /// <see cref="ITileShape.Halo"/> (r) cells around each layer (the facing columns of the west and east neighbors, the
    /// facing rows of the north and south ones, the corners from the tiles across the edges when the neighborhood has
    /// them), and, for a tile of more than one layer, r layers under <paramref name="from"/> and over
    /// <paramref name="to"/> (the tile's own, or the facing layers of the neighbors below or above). Where there is no
    /// neighbor (none in the grid, an empty buffer, or outside the shape's faces and neighborhood) the halo follows
    /// <see cref="ITileShape.Boundary"/>: <paramref name="border"/>, or the tile's own edge clamped or mirrored (along the
    /// axis that has no neighbor; a tile across an edge or corner whose axis is clamped is read from the tile beside).
    /// With <paramref name="mask"/>, it records which rows hold a cell that is not zero (every bit), for
    /// <see cref="Rows"/>. In a <see cref="GaussSeidelAttribute">[GaussSeidel]</see> step, the sweeps after the first
    /// keep the texture they wrote (the neighbors, of other colors, do not change meanwhile): the call copies nothing.
    /// <para>
    /// O(layers × (Width + 2r) × (Depth + 2r)), no allocation once the thread has its texture (allocated on its first
    /// exchange, for all the layers). The texture belongs to the thread and to this tile type: the next exchange of
    /// <typeparamref name="TCell"/> and <typeparamref name="TTile"/> on the thread replaces it, so a method takes one tile
    /// per cell type and grid (EECS073). Throws when the shape is invalid (EECS072), the entity's buffer or a neighbor's
    /// that is not empty holds fewer cells than the shape, or the layers are not 0 ≤ from ≤ to &lt; Height.
    /// </para>
    /// </summary>
    public void ExchangeHalo(int from, int to, TCell border = default, bool mask = true)
    {
        TileShapeCheck<TTile>.Check();
        if (from < 0 || from > to || to >= TTile.Height) ThrowLayers(from, to);
        var frame = _frame;
        if (frame.Sweep > 0) // a Gauss-Seidel sweep after the first: the texture is the tile as the sweeps left it
        {
            if (frame.From != from || frame.To != to || frame.Masked != mask) TileFrame<TCell, TTile>.ThrowSweepLayers();
            Keep(frame);
            return;
        }
        frame.Loaded = default; // a copy that throws leaves no texture behind
        // The common shape (a five- or seven-point stencil: one cell of halo, the faces, a border boundary) has its own
        // loop, which the JIT keeps for that shape alone (the condition is a constant): no corner, no remapping.
        if (TTile.Halo == 1 && TTile.Neighborhood == TileNeighborhood.Faces && TTile.Boundary == TileBoundary.Border)
        {
            if (mask) LoadFaces<Masked>(frame, from, to, border);
            else LoadFaces<Unmasked>(frame, from, to, border);
        }
        // A 2D tile with a border (a blur, a wave, Life): its own loop, with no table of the 27 sources.
        else if (TTile.Height == 1 && TTile.Boundary == TileBoundary.Border && TTile.Halo <= TTile.Width && TTile.Halo <= TTile.Depth)
        {
            if (mask) LoadPlane<Masked>(frame, border);
            else LoadPlane<Unmasked>(frame, border);
        }
        else LoadAny(frame, from, to, border, mask);
        frame.From = from;
        frame.To = to;
        frame.Masked = mask;
        frame.Loaded = _entity.Id;
        Keep(frame);
    }

    /// <summary>The texture's place and layers, kept in the tile for <see cref="At"/> and the indexer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Keep(TileFrame<TCell, TTile> frame)
    {
        _origin = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frame.Texels), frame.Texel(0, frame.From, 0));
        _from = frame.From;
        _to = frame.To;
    }

    private readonly void LoadAny(TileFrame<TCell, TTile> frame, int from, int to, TCell border, bool mask)
    {
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, r = TTile.Halo, hy = height > 1 ? r : 0;
        int row = width + 2 * r, layer = row * (depth + 2 * r), cells = width * depth * height;
        int layers = to - from + 1 + 2 * hy;
        ref TCell texels = ref MemoryMarshal.GetArrayDataReference(frame.Texels);
        ref TileMask masks = ref MemoryMarshal.GetArrayDataReference(frame.Masks);

        // The buffers around: a missing one (no tile there, an empty buffer, outside the neighborhood) is the blank,
        // cells of the border, so every row below is copied without a branch on which neighbors are there.
        bool zero = TileLayout.IsZero(border);
        TCell[] blank = TileMemory<TCell, TTile>.Blank(border, zero);
        var sources = default(TileSources<TCell>);
        for (int slot = 0; slot < 27; slot++) sources[slot] = blank;
        sources[13] = Own(cells);
        uint present = 1u << 13;
        for (uint slots = TileOffsets<TTile>.SlotMask; slots != 0; slots &= slots - 1)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            if (Of(_around[slot], cells) is { } there)
            {
                sources[slot] = there;
                present |= 1u << slot;
            }
        }

        // Along x, the west and east halo of every row: the neighbor's cells, or the tile's own (clamped, mirrored).
        bool clampWest = TTile.Boundary != TileBoundary.Border && (present >> 12 & 1) == 0;
        bool clampEast = TTile.Boundary != TileBoundary.Border && (present >> 14 & 1) == 0;

        // The bounds: every read below is a row of a buffer in sources, each of which holds at least `cells` (Own / Of
        // checked it), at a layer 0 <= y < Height, a row 0 <= z < Depth and cells 0 <= x < Width (Map keeps the mapped
        // coordinate in the tile it names; Shape.Check holds r <= the extent it reaches across); every write is inside
        // layer t < layers of the texture, which holds (Height + 2 hy) layers of (Depth + 2r) rows of (Width + 2r) cells.
        for (int t = 0; t < layers; t++)
        {
            int y = from - hy + t;
            int dy = Map(y, height, (present >> 4 & 1) != 0, (present >> 22 & 1) != 0, out int sy);
            ref TCell at = ref Unsafe.Add(ref texels, t * layer + r * row + r); // the layer's cell (0, 0)

            // The rows of the layer, each with its west and east halo: the hot loop, a constant-size copy per row.
            // The layer's first cell in each buffer: the row loop walks three refs, a row apart.
            int start = sy * depth * width;
            ref TCell mid = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(0, dy, 0)]), start);
            ref TCell west = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(clampWest ? 0 : -1, dy, 0)]), start);
            ref TCell east = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(clampEast ? 0 : 1, dy, 0)]), start);
            ulong rows = 0;
            for (int z = 0; z < depth; z++)
                if (Row(ref Unsafe.Add(ref at, z * row), ref Unsafe.Add(ref mid, z * width), ref Unsafe.Add(ref west, z * width),
                        ref Unsafe.Add(ref east, z * width), clampWest, clampEast))
                    rows |= 1UL << z;

            // The halo rows north and south, with their corners.
            // Bands: bit z + r of each halo row that is not zero, where the texture's rows fit a word (TileMask.Of).
            ulong bands = 0;
            for (int k = 1; k <= r; k++)
            {
                if (Band(ref Unsafe.Add(ref at, -k * row), ref sources, present, dy, sy, -k, clampWest, clampEast))
                    bands |= depth + 2 * r <= 64 ? 1UL << (r - k) : 1;
                if (Band(ref Unsafe.Add(ref at, (depth - 1 + k) * row), ref sources, present, dy, sy, depth - 1 + k, clampWest, clampEast))
                    bands |= depth + 2 * r <= 64 ? 1UL << (depth - 1 + k + r) : 2;
            }

            if (mask) Unsafe.Add(ref masks, t) = TileMask.Of<TTile>(rows, bands);
        }
    }

    /// <summary>
    /// <see cref="ExchangeHalo(int, int, TCell, bool)"/> of a 2D shape with a border boundary and a halo of at most its
    /// extent, of any neighborhood: the nine buffers in locals (the general load keeps them in a table of 27, whose
    /// stores the JIT compiles as checked write barriers, one call each, since it cannot see the table is on the stack),
    /// each row one copy of Width cells and of the r cells on each side, the bands north and south with their corners
    /// (the tiles across the corners, or the border where the neighborhood has none). The same texture and masks.
    /// </summary>
    private readonly void LoadPlane<TMask>(TileFrame<TCell, TTile> frame, TCell border) where TMask : struct
    {
        bool mask = typeof(TMask) == typeof(Masked);
        int width = TTile.Width, depth = TTile.Depth, r = TTile.Halo, row = width + 2 * r, cells = width * depth;
        bool zero = TileLayout.IsZero(border);
        TCell[] blank = TileMemory<TCell, TTile>.Blank(border, zero);
        ref TCell s = ref MemoryMarshal.GetArrayDataReference(Own(cells));
        ref TCell n = ref MemoryMarshal.GetArrayDataReference(Beside(10, cells, blank));
        ref TCell so = ref MemoryMarshal.GetArrayDataReference(Beside(16, cells, blank));
        ref TCell w = ref MemoryMarshal.GetArrayDataReference(Beside(12, cells, blank));
        ref TCell e = ref MemoryMarshal.GetArrayDataReference(Beside(14, cells, blank));
        ref TCell nw = ref MemoryMarshal.GetArrayDataReference(Beside(9, cells, blank));
        ref TCell ne = ref MemoryMarshal.GetArrayDataReference(Beside(11, cells, blank));
        ref TCell sw = ref MemoryMarshal.GetArrayDataReference(Beside(15, cells, blank));
        ref TCell se = ref MemoryMarshal.GetArrayDataReference(Beside(17, cells, blank));
        ref TCell at = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frame.Texels), r * row + r); // the texture's cell (0, 0)

        // The bounds: every read is inside a buffer of at least `cells` (Own and Beside checked it, the blank has
        // `cells`): row z < Depth (r <= Depth for the bands), cells x < Width (r <= Width for the sides); every write is
        // inside the texture's one layer of (Depth + 2r) rows of (Width + 2r) cells.
        ulong rows = 0;
        for (int z = 0; z < depth; z++)
        {
            int source = z * width;
            ref TCell into = ref Unsafe.Add(ref at, z * row);
            bool any = TileLayout.Copy(ref into, ref Unsafe.Add(ref s, source), width);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, -r), ref Unsafe.Add(ref w, source + width - r), r);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, width), ref Unsafe.Add(ref e, source), r);
            if (mask && any) rows |= 1UL << z;
        }

        // Halo row −k is row Depth − k of the tiles north (and its corners of those north-west and north-east), halo
        // row Depth − 1 + k row k − 1 of those south. Bit z + r of each that is not zero, where the texture's rows fit
        // a word (TileMask.Of).
        ulong bands = 0;
        for (int k = 1; k <= r; k++)
        {
            int source = (depth - k) * width;
            ref TCell into = ref Unsafe.Add(ref at, -k * row);
            bool any = TileLayout.Copy(ref into, ref Unsafe.Add(ref n, source), width);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, -r), ref Unsafe.Add(ref nw, source + width - r), r);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, width), ref Unsafe.Add(ref ne, source), r);
            if (any) bands |= depth + 2 * r <= 64 ? 1UL << (r - k) : 1;
            source = (k - 1) * width;
            into = ref Unsafe.Add(ref at, (depth - 1 + k) * row);
            any = TileLayout.Copy(ref into, ref Unsafe.Add(ref so, source), width);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, -r), ref Unsafe.Add(ref sw, source + width - r), r);
            any |= TileLayout.Copy(ref Unsafe.Add(ref into, width), ref Unsafe.Add(ref se, source), r);
            if (any) bands |= depth + 2 * r <= 64 ? 1UL << (depth - 1 + k + r) : 2;
        }
        if (mask) MemoryMarshal.GetArrayDataReference(frame.Masks) = TileMask.Of<TTile>(rows, bands);
    }

    /// <summary>
    /// <see cref="ExchangeHalo(int, int, TCell, bool)"/> of a shape with a halo of one cell, the faces and a border
    /// boundary: per row one copy of Width cells and the two cells beside it, the corners the border. The same texture
    /// and masks. <typeparamref name="TMask"/> is <see cref="Masked"/> or <see cref="Unmasked"/>: the JIT compiles a
    /// loop for each, and the unmasked one (the blocks a stencil only samples) tests no cell.
    /// </summary>
    private readonly void LoadFaces<TMask>(TileFrame<TCell, TTile> frame, int from, int to, TCell border) where TMask : struct
    {
        bool mask = typeof(TMask) == typeof(Masked);
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, hy = height > 1 ? 1 : 0;
        int row = width + 2, layer = row * (depth + 2), cells = width * depth * height;
        int layers = to - from + 1 + 2 * hy;
        ref TCell texels = ref MemoryMarshal.GetArrayDataReference(frame.Texels);
        ref TileMask masks = ref MemoryMarshal.GetArrayDataReference(frame.Masks);
        bool zero = TileLayout.IsZero(border);
        TCell[] blank = TileMemory<TCell, TTile>.Blank(border, zero);

        // A missing neighbor reads the blank (cells of the border), so the row loop copies without a branch on it.
        TCell[] north = Beside(10, cells, blank), south = Beside(16, cells, blank);
        TCell[] west = Beside(12, cells, blank), east = Beside(14, cells, blank);
        ref TCell s = ref MemoryMarshal.GetArrayDataReference(Own(cells));
        ref TCell n = ref MemoryMarshal.GetArrayDataReference(north), so = ref MemoryMarshal.GetArrayDataReference(south);
        ref TCell w = ref MemoryMarshal.GetArrayDataReference(west), e = ref MemoryMarshal.GetArrayDataReference(east);
        // Bit z + 1 of a texture row, for the south halo row: TileMask.Of reads the bands that way where they fit a word.
        ulong southBand = depth + 2 <= 64 ? 1UL << (depth + 1) : 2;

        // The bounds: every read is a row of a buffer that holds at least `cells` (Own and Beside checked it, the blank
        // has `cells`), at a layer 0 <= y < Height and a row 0 <= z < Depth; every write is inside layer t < layers of
        // the texture.
        for (int t = 0; t < layers; t++)
        {
            int y = from - hy + t;
            ref TCell at = ref Unsafe.Add(ref texels, t * layer);
            if ((uint)y >= (uint)height)
            {
                // Under layer 0 or over the last: the facing layer of the tile below or above (the blank when there is
                // none), its ring the border.
                var across = Beside(y < 0 ? 4 : 22, cells, blank);
                var edge = Edge(ref at, ref MemoryMarshal.GetArrayDataReference(across), y < 0 ? height - 1 : 0, border, zero);
                if (mask) Unsafe.Add(ref masks, t) = edge;
                continue;
            }

            ulong rows = 0;
            int first = y * depth * width;
            for (int z = 0; z < depth; z++)
            {
                int at0 = first + z * width;
                ref TCell into = ref Unsafe.Add(ref at, (z + 1) * row + 1);
                bool any = TileLayout.Copy(ref into, ref Unsafe.Add(ref s, at0), width);
                any |= TileLayout.Copy(ref Unsafe.Add(ref into, -1), ref Unsafe.Add(ref w, at0 + width - 1), 1);
                any |= TileLayout.Copy(ref Unsafe.Add(ref into, width), ref Unsafe.Add(ref e, at0), 1);
                if (mask && any) rows |= 1UL << z;
            }

            // The north neighbor's last row and the south neighbor's first, and the four corners (the border).
            ref TCell top = ref Unsafe.Add(ref at, 1);
            ref TCell bottom = ref Unsafe.Add(ref at, (depth + 1) * row + 1);
            bool northWet = TileLayout.Copy(ref top, ref Unsafe.Add(ref n, first + (depth - 1) * width), width) | !zero;
            bool southWet = TileLayout.Copy(ref bottom, ref Unsafe.Add(ref so, first), width) | !zero;
            at = border;
            Unsafe.Add(ref top, width) = border;
            Unsafe.Add(ref bottom, -1) = border;
            Unsafe.Add(ref bottom, width) = border;
            if (mask) Unsafe.Add(ref masks, t) = TileMask.Of<TTile>(rows, (northWet ? 1UL : 0) | (southWet ? southBand : 0));
        }
    }

    /// <summary>
    /// The rows of the layers exchanged that a stencil reading <paramref name="reads"/> must shade, layer by layer, in
    /// order (<c>foreach (var row in heat.Rows(TileStencil.Layer))</c>): those with a cell that is not zero among the
    /// rows it reads, up to the halo away. O(1) per row visited and per layer. Throws when the method did not
    /// exchange the tile's halo first, or exchanged it without its mask.
    /// </summary>
    public readonly TileRows<TTile> Rows(TileStencil reads = TileStencil.All)
    {
        var frame = Exchanged();
        if (!frame.Masked) TileLayout.ThrowNoMask();
        return new TileRows<TTile>(ref MemoryMarshal.GetArrayDataReference(frame.Masks), frame.From, frame.To, reads);
    }

    /// <summary>
    /// The texture at the first cell of <paramref name="row"/> (a row of the layers exchanged, from <see cref="Rows"/> of
    /// this tile or of another exchanged with the same layers): its samples. Throws for a row outside, or when the
    /// method did not exchange the tile's halo first. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly TileCursor<TCell, TTile> At(TileRow<TTile> row)
    {
        if (Unsafe.IsNullRef(ref _origin)) return AtFrame(row);
        if ((uint)(row.Y - _from) > (uint)(_to - _from) || (uint)row.Z >= (uint)TTile.Depth) TileLayout.ThrowOffset();
        return new TileCursor<TCell, TTile>(ref Unsafe.Add(ref _origin, (row.Y - _from) * TileLayout.Layer<TTile>() + row.Z * TileLayout.Row<TTile>()));
    }

    /// <summary><see cref="At"/> of a copy of the tile that did not exchange its halo itself: through the frame, checked.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly TileCursor<TCell, TTile> AtFrame(TileRow<TTile> row)
    {
        var frame = Exchanged();
        if ((uint)(row.Y - frame.From) > (uint)(frame.To - frame.From) || (uint)row.Z >= (uint)TTile.Depth) TileLayout.ThrowOffset();
        return new TileCursor<TCell, TTile>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frame.Texels), frame.Texel(0, row.Y, row.Z)));
    }

    /// <summary>
    /// Cell (x, y, z) of the texture: x from −r to Width + r − 1, z from −r to Depth + r − 1, y of the layers exchanged
    /// and r under and over them (those layers alone for a 2D tile), r the halo. Throws outside, or when the method did
    /// not exchange the tile's halo first. O(1).
    /// </summary>
    public readonly ref readonly TCell this[int x, int y, int z]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Unsafe.IsNullRef(ref _origin)) return ref CellFrame(x, y, z);
            int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
            if ((uint)(x + r) >= (uint)(TTile.Width + 2 * r) || (uint)(z + r) >= (uint)(TTile.Depth + 2 * r)
                || (uint)(y - _from + hy) > (uint)(_to - _from + 2 * hy))
                TileLayout.ThrowOffset();
            return ref Unsafe.Add(ref _origin, (y - _from) * TileLayout.Layer<TTile>() + z * TileLayout.Row<TTile>() + x);
        }
    }

    /// <summary>The indexer of a copy of the tile that did not exchange its halo itself: through the frame, checked.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly ref readonly TCell CellFrame(int x, int y, int z)
    {
        var frame = Exchanged();
        int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        if ((uint)(x + r) >= (uint)(TTile.Width + 2 * r) || (uint)(z + r) >= (uint)(TTile.Depth + 2 * r)
            || (uint)(y - frame.From + hy) > (uint)(frame.To - frame.From + 2 * hy))
            TileLayout.ThrowOffset();
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(frame.Texels), frame.Texel(x, y, z));
    }

    /// <summary>
    /// The cells of <paramref name="row"/> in the step, Width of them, for a stepping tile (a <c>ref Tile</c> parameter):
    /// the row of the second buffer, which becomes the cells once every tile stepped (Jacobi), or, in a
    /// <see cref="GaussSeidelAttribute">[GaussSeidel]</see> step, the row of the texture itself (a row of the layers
    /// exchanged), which the next rows and sweeps read. Write every cell of the row: the rows the step does not write are
    /// zero after it. O(1); throws for a tile taken by value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<TCell> Write(TileRow<TTile> row)
    {
        var frame = _frame;
        if ((uint)row.Y >= (uint)TTile.Height || (uint)row.Z >= (uint)TTile.Depth) TileLayout.ThrowOffset();
        if (frame.Stepping != _entity.Id) TileFrame<TCell, TTile>.ThrowNotStepping();
        if (frame.Next is { } next)
        {
            frame.Written[row.Y] |= 1UL << row.Z;
            return new Span<TCell>(next, row.Index, TTile.Width);
        }
        return frame.WriteInPlace(row.Y, row.Z, _entity.Id);
    }

    /// <summary>
    /// The sweep a <see cref="GaussSeidelAttribute">[GaussSeidel]</see> step of the tile is in: 0 in its first, one more
    /// in each sweep after it (0 in a Jacobi step, which is one sweep). For what a step does once (a mask of the ground
    /// read in its first sweep into a <see cref="Scratch{T}"/>, the cells it starts from: <see cref="Restart"/>) and for
    /// sweeps in turns of direction (odd sweeps from the last row back to the first: a field crosses the tile both ways
    /// in two sweeps, the fast sweeping method of distance fields). Counted per step: a tile that runs out of
    /// <see cref="GaussSeidelAttribute.MaxSweeps"/> starts at 0 in its next. O(1); throws for a tile the method does not
    /// step (taken by value).
    /// </summary>
    public readonly int Sweep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var frame = _frame;
            if (frame.Stepping != _entity.Id) TileFrame<TCell, TTile>.ThrowNotStepping();
            return frame.Sweep;
        }
    }

    /// <summary>
    /// A <see cref="GaussSeidelAttribute">[GaussSeidel]</see> step that settles the tile from <paramref name="value"/>
    /// instead of from its cells: the cells of the layers exchanged are <paramref name="value"/> in the texture (its halo
    /// stays the neighbors'), and the sweeps go on until the field reached from there settles. Call it after
    /// <see cref="ExchangeHalo(int, int, TCell, bool)"/>; like the exchange, it acts in the first sweep and the sweeps
    /// after it keep what they wrote. For a field whose sources come and go (a distance field, a scent): relaxed in place
    /// from its last values, a value whose source left lingers and only fades a step per sweep (distance-vector routing's
    /// count to infinity); from nothing, the tile settles in as many sweeps as the paths through it turn. The pass compares
    /// the settled rows with the cells, so a tile whose field came out the same sleeps as before. A restarted tile that
    /// runs out of <see cref="GaussSeidelAttribute.MaxSweeps"/> starts again in its next step: give it the sweeps to
    /// settle. O(cells of the layers exchanged); throws for a tile that is not the one a [GaussSeidel] step sweeps, or
    /// before its halo is exchanged.
    /// </summary>
    /// <param name="value">What the tile's cells start from: <c>default</c> (no scent, unreached), or the far value of a
    /// minimum (<c>int.MaxValue</c>).</param>
    public readonly void Restart(TCell value = default)
    {
        var frame = Exchanged();
        if (frame.Stepping != _entity.Id || frame.Next != null) TileFrame<TCell, TTile>.ThrowNotSweeping();
        if (frame.Sweep == 0) frame.Restart(value);
    }

    /// <summary>The frame, once this tile's halo is in it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly TileFrame<TCell, TTile> Exchanged()
    {
        var frame = _frame;
        if (frame.Loaded != _entity.Id) TileFrame<TCell, TTile>.ThrowNotExchanged();
        return frame;
    }

    /// <summary>The cells of the neighbor in <paramref name="slot"/>, or the blank.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly TCell[] Beside(int slot, int cells, TCell[] blank) =>
        (TileOffsets<TTile>.SlotMask >> slot & 1) != 0 && Of(_around[slot], cells) is { } there ? there : blank;

    /// <summary>
    /// A layer under or over the tile for <see cref="LoadFaces"/>: <paramref name="source"/>'s layer <paramref name="y"/>
    /// in the middle, the ring the border; its mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TileMask Edge(ref TCell at, ref TCell source, int y, TCell border, bool zero)
    {
        int width = TTile.Width, depth = TTile.Depth, row = width + 2;
        ulong rows = 0;
        for (int z = 0; z < depth; z++)
        {
            ref TCell into = ref Unsafe.Add(ref at, (z + 1) * row + 1);
            if (TileLayout.Copy(ref into, ref Unsafe.Add(ref source, (y * depth + z) * width), width) | !zero) rows |= 1UL << z;
            Unsafe.Add(ref into, -1) = border;
            Unsafe.Add(ref into, width) = border;
        }
        MemoryMarshal.CreateSpan(ref at, row).Fill(border);
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref at, (depth + 1) * row), row).Fill(border);
        return TileMask.Of<TTile>(rows, zero ? 0 : 1UL | (depth + 2 <= 64 ? 1UL << (depth + 1) : 2));
    }

    /// <summary>
    /// Whether <paramref name="next"/> (the tile's step, in the layout of its cells) differs from the tile's cells, and
    /// the faces whose edge changed: a cell up to <see cref="ITileShape.Halo"/> cells from the face, which the neighbor
    /// across it reads into its halo. A stepping tile (<c>ref Tile</c>) needs none of this: its pass finds the edges from
    /// the rows written and lets the tiles sleep. O(cells), compared row by row as bytes.
    /// </summary>
    /// <param name="next">The step: at least the tile's cells.</param>
    /// <param name="changed">Whether any cell changed.</param>
    /// <returns>The faces whose edge changed (<see cref="TileFaces.Below"/> and <see cref="TileFaces.Above"/> only for a tile of more than one layer).</returns>
    public readonly TileFaces ChangedEdges(ReadOnlySpan<TCell> next, out bool changed)
    {
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height;
        var own = Cells;
        int cells = width * depth * height;
        if (own.Length < cells) ThrowCells(_entity.Id, own.Length, cells);
        if (next.Length < cells) ThrowCells(default, next.Length, cells);
        var edges = TileFaces.None;
        changed = false;
        for (int y = 0; y < height; y++)
        for (int z = 0; z < depth; z++)
        {
            int at = (y * depth + z) * width;
            if (!TileLayout.RowEdges<TCell, TTile>(own.Slice(at, width), next.Slice(at, width), y, z, ref edges)) continue;
            changed = true;
        }

        return edges;
    }

    /// <summary>
    /// The neighbors that read an edge of <paramref name="edges"/> into their halo (from <see cref="ChangedEdges"/>): the
    /// tile across each face, and, in the shape's neighborhood, across each edge and corner of two or three of those
    /// faces; the tiles there are (<c>foreach (var id in tile.Facing(edges))</c>). O(1) per neighbor.
    /// </summary>
    public readonly TileIds Facing(TileFaces edges)
    {
        uint slots = 0;
        for (uint all = TileOffsets<TTile>.Facing(edges); all != 0; all &= all - 1)
        {
            int slot = BitOperations.TrailingZeroCount(all);
            if (_around[slot] != default) slots |= 1u << slot;
        }

        return new TileIds(in _around, slots);
    }

    /// <summary>
    /// The halo row <paramref name="z"/> (−r to −1, or Depth to Depth + r − 1) of a texture layer, into <paramref name="into"/>
    /// (the row's cell 0): from the tiles north or south (or the tile's own, clamped or mirrored); whether a cell is not zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Band(ref TCell into, ref TileSources<TCell> sources, uint present, int dy, int sy, int z, bool clampWest, bool clampEast)
    {
        int depth = TTile.Depth, width = TTile.Width;
        int dz = Map(z, depth, (present >> 10 & 1) != 0, (present >> 16 & 1) != 0, out int sz);
        int at = (sy * depth + sz) * width;
        return Row(ref into, ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(0, dy, dz)]), at),
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(clampWest ? 0 : -1, dy, dz)]), at),
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(sources[TileOffsets<TTile>.Slot(clampEast ? 0 : 1, dy, dz)]), at),
            clampWest, clampEast);
    }

    /// <summary>
    /// One row of the texture, at its cell 0: Width cells from <paramref name="mid"/> (the row in its buffer), the r cells
    /// before them from <paramref name="west"/>'s row (its last r, or its first clamped or mirrored), the r after
    /// from <paramref name="east"/> (the blank of border cells where there is no neighbor). Whether a cell is not zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Row(ref TCell into, ref TCell mid, ref TCell west, ref TCell east, bool clampWest, bool clampEast)
    {
        int width = TTile.Width, r = TTile.Halo;
        bool any = TileLayout.Copy(ref into, ref mid, width);
        ref TCell before = ref Unsafe.Add(ref into, -r);
        ref TCell after = ref Unsafe.Add(ref into, width);
        // clampWest / clampEast are false for a border boundary, a constant: the copies are the whole row then.
        if (!clampWest) any |= TileLayout.Copy(ref before, ref Unsafe.Add(ref west, width - r), r);
        else any |= TileLayout.Edge(ref before, ref west, r, TTile.Boundary == TileBoundary.Mirror, true);
        if (!clampEast) any |= TileLayout.Copy(ref after, ref east, r);
        else any |= TileLayout.Edge(ref after, ref Unsafe.Add(ref east, width - 1), r, TTile.Boundary == TileBoundary.Mirror, false);
        return any;
    }

    /// <summary>
    /// Coordinate <paramref name="c"/> along an axis of <paramref name="n"/> cells: the offset of the tile it is in (−1, 0
    /// or 1) and its coordinate there (<paramref name="s"/>), or, where that tile is missing and the boundary clamps or
    /// mirrors, the tile's own (offset 0) at the clamped or mirrored coordinate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Map(int c, int n, bool before, bool after, out int s)
    {
        if ((uint)c < (uint)n)
        {
            s = c;
            return 0;
        }
        bool low = c < 0;
        if (TTile.Boundary == TileBoundary.Border || (low ? before : after))
        {
            // Past the tile beside too (a halo wider than an axis without faces, which Shape.Check allows): that tile
            // is the blank, the same cell everywhere, so any coordinate in it reads the border.
            s = low ? c + n : c - n;
            if ((uint)s >= (uint)n) s = 0;
            return low ? -1 : 1;
        }
        s = TTile.Boundary == TileBoundary.Clamp ? (low ? 0 : n - 1) : (low ? -1 - c : 2 * n - 1 - c);
        return 0;
    }

    private readonly TCell[] Own(int cells)
    {
        var own = _entity.Find<DynamicBuffer<TCell>>(true).Store;
        int count = own?.Count ?? 0;
        if (count < cells) ThrowCells(_entity.Id, count, cells);
        return own!.Items;
    }

    /// <summary>The cells of <paramref name="id"/>: null when there is no such tile, or it has no cells.</summary>
    private readonly TCell[] Of(EntityId id, int cells)
    {
        if (id == default) return null;
        ref var buffer = ref new Entity(_entity.World, id).Find<DynamicBuffer<TCell>>(false);
        if (Unsafe.IsNullRef(ref buffer) || buffer.Store is not { Count: > 0 } store) return null;
        if (store.Count < cells) ThrowCells(id, store.Count, cells);
        return store.Items;
    }

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see ThrowOffset
    private static void ThrowLayers(int from, int to) =>
        throw new ArgumentOutOfRangeException(nameof(from), $"Layers {from} to {to} of a {typeof(TTile).Name} tile of {TTile.Height} layers");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see ThrowOffset
    internal static void ThrowCells(EntityId id, int length, int cells) =>
        throw new InvalidOperationException($"The DynamicBuffer<{typeof(TCell).Name}> of {id} holds {length} cells; a {typeof(TTile).Name} tile has {cells}");
}

/// <summary>
/// The columns of a tile: a tile of one layer, Width × Depth, on the grid of <typeparamref name="TTile"/> (a tile of
/// several layers), with its sides as faces, its halo, neighborhood and boundary. A second field on the same entities
/// and keys: <c>Tile&lt;Scent, Columns&lt;Chunk&gt;&gt;</c> is a chunk's ground beside the
/// <c>Tile&lt;Water, Chunk&gt;</c> of its blocks, its cells a <c>DynamicBuffer&lt;Scent&gt;</c> of Width × Depth.
/// </summary>
/// <typeparam name="TTile">The grid's tile component.</typeparam>
public readonly struct Columns<TTile> : ITileShape where TTile : struct, ITileShape
{
    /// <inheritdoc cref="ITileShape.Width"/>
    public static int Width => TTile.Width;

    /// <inheritdoc cref="ITileShape.Depth"/>
    public static int Depth => TTile.Depth;

    /// <summary>One layer.</summary>
    public static int Height => 1;

    /// <summary>The sides among <typeparamref name="TTile"/>'s faces.</summary>
    public static TileFaces Faces => TTile.Faces & TileFaces.Sides;

    /// <inheritdoc cref="ITileShape.Halo"/>
    public static int Halo => TTile.Halo;

    /// <inheritdoc cref="ITileShape.Neighborhood"/>
    public static TileNeighborhood Neighborhood => TTile.Neighborhood;

    /// <inheritdoc cref="ITileShape.Boundary"/>
    public static TileBoundary Boundary => TTile.Boundary;
}

/// <summary>The neighbors of a tile that <see cref="Tile{TCell, TTile}.Facing"/> names, walked with <c>foreach</c>.</summary>
public ref struct TileIds
{
    private readonly ref readonly TileNeighbors _around;
    private uint _slots;
    private int _slot;

    internal TileIds(ref readonly TileNeighbors around, uint slots)
    {
        _around = ref around;
        _slots = slots;
        _slot = -1;
    }

    /// <summary>Itself: <c>foreach</c> walks it.</summary>
    public readonly TileIds GetEnumerator() => this;

    /// <summary>The neighbor the walk is at.</summary>
    public readonly EntityId Current => _around[_slot];

    /// <summary>Moves to the next neighbor; false after the last.</summary>
    public bool MoveNext()
    {
        if (_slots == 0) return false;
        _slot = BitOperations.TrailingZeroCount(_slots);
        _slots &= _slots - 1;
        return true;
    }
}

/// <summary>
/// A layer's active rows: bit z for row z (with its halo cells), and the rows a halo row north or south that is not
/// zero reaches (<see cref="Ends"/>: rows 0 to r − 1, rows Depth − r to Depth − 1).
/// </summary>
internal readonly record struct TileMask(ulong Rows, ulong Ends)
{
    /// <summary>
    /// The mask of a layer whose rows <paramref name="rows"/> (bit z) and halo rows <paramref name="bands"/> (bit z + r
    /// where Depth + 2r fits a word; else bit 0 for a north halo row, bit 1 for a south one) hold a cell that is not zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TileMask Of<TTile>(ulong rows, ulong bands) where TTile : ITileShape
    {
        int depth = TTile.Depth, r = TTile.Halo;
        ulong north = r == 64 ? ulong.MaxValue : (1UL << r) - 1, south = north << (depth - r);
        bool wetNorth, wetSouth;
        if (depth + 2 * r <= 64)
        {
            wetNorth = (bands & north) != 0;
            wetSouth = (bands >> (depth + r)) != 0;
        }
        else
        {
            wetNorth = (bands & 1) != 0;
            wetSouth = (bands & 2) != 0;
        }
        return new TileMask(rows, (wetNorth ? north : 0) | (wetSouth ? south : 0));
    }
}

/// <summary>A row of a tile: layer <see cref="Y"/>, row <see cref="Z"/>, from <see cref="Tile{TCell, TTile}.Rows"/>.</summary>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly struct TileRow<TTile> where TTile : struct, ITileShape
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
/// The active rows of a tile (<see cref="Tile{TCell, TTile}.Rows"/>), walked with <c>foreach</c>: per layer
/// a word of the rows the stencil may change, then a trailing-zero count per row.
/// </summary>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public ref struct TileRows<TTile> where TTile : struct, ITileShape
{
    private readonly ref TileMask _masks;
    private readonly int _base, _last;
    private readonly TileStencil _reads;
    private int _layer;
    private ulong _rows;
    private int _z;

    internal TileRows(ref TileMask masks, int from, int to, TileStencil reads)
    {
        int hy = TTile.Height > 1 ? TTile.Halo : 0;
        _masks = ref masks;
        _base = from - hy;            // the layer of texture layer 0
        _layer = hy - 1;              // the texture layer before From's
        _last = to - from + hy;       // To's
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

    /// <summary>The rows of texture layer <paramref name="layer"/> the stencil reads something in (its neighbors are in the texture: halo layers).</summary>
    private readonly ulong Visit(int layer)
    {
        int depth = TTile.Depth, hy = TTile.Height > 1 ? TTile.Halo : 0;
        ref var here = ref Unsafe.Add(ref _masks, layer);
        ulong visit = 0;
        if ((_reads & TileStencil.Self) != 0) visit |= here.Rows;
        if ((_reads & TileStencil.NorthSouth) != 0) visit |= Spread(here);
        for (int k = 1; k <= hy; k++)
        {
            ref var up = ref Unsafe.Add(ref here, k);
            ref var down = ref Unsafe.Add(ref here, -k);
            if ((_reads & TileStencil.Above) != 0) visit |= up.Rows;
            if ((_reads & TileStencil.Below) != 0) visit |= down.Rows;
            if ((_reads & TileStencil.Diagonals) != 0) visit |= Spread(up) | Spread(down);
        }

        return depth == 64 ? visit : visit & ((1UL << depth) - 1);
    }

    /// <summary>The rows within the halo north or south of a row of <paramref name="mask"/>, or of a halo row that is not zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Spread(in TileMask mask)
    {
        ulong rows = mask.Rows, spread = mask.Ends | rows << 1 | rows >> 1;
        for (int k = 2; k <= TTile.Halo; k++) spread |= rows << k | rows >> k;
        return spread;
    }
}

/// <summary>
/// A tile's texture at the first cell of a row (<see cref="Tile{TCell, TTile}.At"/>): the cells around it, one or
/// a vector at a time. An offset is in cells: dx from −r (the first halo cell before the row) to Width + r − 1 (the last
/// after it), dy and dz from −r to r (dy 0 for a 2D tile), r the shape's <see cref="ITileShape.Halo"/>; a vector of
/// cells must end by the last halo cell after the row. Constant offsets are checked by the JIT, others by a compare.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly ref struct TileCursor<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
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
    /// (cells −r to Width + r − 1), in the rows and layers the texture holds: the invariant that lets the loads skip
    /// their bounds check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Offset(int dx, int dy, int dz, int bytes)
    {
        int size = Unsafe.SizeOf<TCell>(), r = TTile.Halo;
        // The last first cell a load of `bytes` may start at, counted from the row's first halo cell: a constant, so
        // each axis is one unsigned compare (a negative dx + r wraps above it). The x check stands alone: dy and dz are
        // constants in a stencil, so the first check folds away, and the loads of one column (x - 1 in three rows)
        // repeat the same compare, which the JIT then drops (one check per column, not per load: 9-point blur ~25% faster).
        int last = TileLayout.Row<TTile>() * size < bytes ? -1 : (TileLayout.Row<TTile>() * size - bytes) / size;
        if (last < 0 || (uint)(dz + r) > (uint)(2 * r)
            || (TTile.Height > 1 ? (uint)(dy + r) > (uint)(2 * r) : dy != 0))
            TileLayout.ThrowOffset();
        if ((uint)(dx + r) > (uint)last) TileLayout.ThrowOffset();
        // dx widened alone, the rows' and layers' part (a constant in a stencil) added after: it folds into each load's
        // displacement, so the loads of one column share one sign extension instead of one each.
        return (nint)dx + (dy * TileLayout.Layer<TTile>() + dz * TileLayout.Row<TTile>());
    }
}

/// <summary>A load that records the row masks: the typeof test on it is a JIT-time constant.</summary>
internal struct Masked;

/// <summary>...and one that does not (a tile only sampled).</summary>
internal struct Unmasked;

/// <summary>
/// The tile memory of a thread for a tile type: the texture <see cref="Tile{TCell, TTile}.ExchangeHalo(int, int, TCell, bool)"/>
/// fills, with its row masks, and the state of the tile this thread steps (<see cref="TileSteps{TCell, TTile, TGrid, TKey, TColors}"/>).
/// Created on the thread's first tile, for all the layers, then reused; the first thread to create one has every helper
/// thread create its own (<see cref="ThreadSetup"/>), so a pass allocates nothing once warm.
/// </summary>
internal sealed class TileFrame<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    [ThreadStatic] private static TileFrame<TCell, TTile> t_current;
    private static int _setUp; // the ThreadSetup step added

    /// <summary>The texture: (Height + 2 hy) layers of (Depth + 2r) rows of (Width + 2r) cells, the layers exchanged first.</summary>
    public readonly TCell[] Texels = GC.AllocateUninitializedArray<TCell>(TileLayout.Layers<TTile>() * TileLayout.Layer<TTile>());

    /// <summary>The rows of each texture layer that hold a cell that is not zero.</summary>
    public readonly TileMask[] Masks = new TileMask[TileLayout.Layers<TTile>()];

    /// <summary>The layers exchanged, and whether with their masks.</summary>
    public int From, To;

    public bool Masked;

    /// <summary>The tile whose halo the texture holds; default when none does.</summary>
    public EntityId Loaded;

    /// <summary>The tile this thread steps (a <c>ref Tile</c>); default outside a step.</summary>
    public EntityId Stepping;

    /// <summary>A Jacobi step: the cells of the next buffer, which <see cref="Tile{TCell, TTile}.Write"/> hands out; null in place.</summary>
    public TCell[] Next;

    /// <summary>The rows the step wrote (a Gauss-Seidel step: this sweep), a word per layer.</summary>
    public readonly ulong[] Written = new ulong[TTile.Height];

    /// <summary>A Gauss-Seidel step: the rows written in any sweep, a word per layer.</summary>
    public readonly ulong[] Touched = new ulong[TTile.Height];

    /// <summary>A Gauss-Seidel step: each row written this sweep as it was before, in the layout of the cells.</summary>
    public readonly TCell[] Before = new TCell[TileLayout.Cells<TTile>()];

    /// <summary>The sweeps of a Gauss-Seidel step done so far (0 in its first, and in any other step).</summary>
    public int Sweep;

    /// <summary>This thread's frame.</summary>
    public static TileFrame<TCell, TTile> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current ?? Create();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TileFrame<TCell, TTile> Create()
    {
        TileShapeCheck<TTile>.Check();
        ThreadSetup.Add(ref _setUp, static () => t_current ??= new TileFrame<TCell, TTile>(), typeof(TileFrame<TCell, TTile>));
        return t_current ??= new TileFrame<TCell, TTile>();
    }

    /// <summary>Where cell (x, y, z) of the tile is in the texture (y a layer exchanged, or within the halo of one).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Texel(int x, int y, int z)
    {
        int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        return (y - From + hy) * TileLayout.Layer<TTile>() + (z + r) * TileLayout.Row<TTile>() + x + r;
    }

    /// <summary>
    /// A Gauss-Seidel write of row (<paramref name="y"/>, <paramref name="z"/>) of <paramref name="tile"/>: the row of the
    /// texture, kept as it was the first time this sweep writes it (to tell whether the sweep changed it).
    /// </summary>
    public Span<TCell> WriteInPlace(int y, int z, EntityId tile)
    {
        if (Loaded != tile) ThrowNotExchanged();
        if ((uint)(y - From) > (uint)(To - From)) TileLayout.ThrowOffset();
        var row = new Span<TCell>(Texels, Texel(0, y, z), TTile.Width);
        ulong bit = 1UL << z;
        if ((Written[y] & bit) == 0)
        {
            row.CopyTo(Before.AsSpan((y * TTile.Depth + z) * TTile.Width, TTile.Width));
            Written[y] |= bit;
        }
        return row;
    }

    /// <summary>
    /// The first sweep of a Gauss-Seidel step from <paramref name="value"/> (<see cref="Tile{TCell, TTile}.Restart"/>): the
    /// tile's cells in the layers exchanged set to it, and the sweep compared with it, not with the cells: every row
    /// counts as written (this sweep and the step: <see cref="Before"/> holds the value, the pass compares every row with
    /// the cells when the step ends). The row masks keep the rows the cells had for the value default (more rows than
    /// hold a value now, which is safe, and the rows the field is likely to reach again in this sweep: a mask of none would
    /// let the field into the tile one row per sweep), every row for another value.
    /// </summary>
    public void Restart(TCell value)
    {
        int width = TTile.Width, depth = TTile.Depth, hy = TTile.Height > 1 ? TTile.Halo : 0;
        bool zero = TileLayout.IsZero(value);
        ulong all = depth == 64 ? ulong.MaxValue : (1UL << depth) - 1;
        for (int y = From; y <= To; y++)
        {
            for (int z = 0; z < depth; z++) new Span<TCell>(Texels, Texel(0, y, z), width).Fill(value);
            Before.AsSpan(y * depth * width, depth * width).Fill(value);
            Written[y] = all;
            if (Masked && !zero)
            {
                ref var mask = ref Masks[y - From + hy];
                mask = mask with { Rows = all };
            }
        }
    }

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see TileLayout.ThrowOffset
    public static void ThrowNotSweeping() => throw new InvalidOperationException(
        $"Restart is for the tile a [GaussSeidel] step sweeps: take Tile<{typeof(TCell).Name}, {typeof(TTile).Name}> by ref in a [GaussSeidel] method");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see TileLayout.ThrowOffset
    public static void ThrowNotStepping() => throw new InvalidOperationException(
        $"Write and Sweep are for a tile the method steps: take Tile<{typeof(TCell).Name}, {typeof(TTile).Name}> by ref");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see TileLayout.ThrowOffset
    public static void ThrowNotExchanged() => throw new InvalidOperationException(
        $"The method reads the texture of a Tile<{typeof(TCell).Name}, {typeof(TTile).Name}> before it exchanged its halo: call ExchangeHalo first");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see TileLayout.ThrowOffset
    public static void ThrowSweepLayers() => throw new InvalidOperationException(
        $"Each sweep of a [GaussSeidel] step exchanges the halo of the same layers, with the same mask: the sweeps after the first keep the texture the first exchanged");
}

/// <summary>
/// The blank of a tile type: a buffer of its cell count holding the border, what an exchange reads where there is no
/// neighbor. One array of zeros per tile type; another border, the thread's array.
/// </summary>
internal static class TileMemory<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    [ThreadStatic] private static TCell[] t_blank;
    private static readonly TCell[] Zeros = new TCell[TileLayout.Cells<TTile>()];

    /// <summary>
    /// A buffer of the tile's cell count holding <paramref name="border"/> (<paramref name="zero"/>: it is zero): what a
    /// load reads where there is no neighbor. One array of zeros per tile type; another border, this thread's array,
    /// filled again when the border changes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TCell[] Blank(TCell border, bool zero)
    {
        if (zero) return Zeros;
        var blank = t_blank;
        if (blank != null && TileLayout.Same(blank[0], border)) return blank;
        return Refill(border);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TCell[] Refill(TCell border)
    {
        var blank = t_blank ?? CreateBlank();
        blank.AsSpan().Fill(border);
        return blank;
    }

    private static int _blankSetUp; // the ThreadSetup step added

    private static TCell[] CreateBlank()
    {
        ThreadSetup.Add(ref _blankSetUp, static () => t_blank ??= new TCell[TileLayout.Cells<TTile>()], typeof(TileMemory<TCell, TTile>));
        return t_blank ??= new TCell[TileLayout.Cells<TTile>()];
    }
}

/// <summary>The checks of a tile shape, once per shape (EECS072 reports them at build time where it can read the shape).</summary>
internal static class TileShapeCheck<TTile> where TTile : struct, ITileShape
{
    private static readonly string Problem = Validate();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check()
    {
        if (Problem != null) Throw();
    }

    /// <summary>What is wrong with the shape, or null.</summary>
    internal static string Validate()
    {
        int w = TTile.Width, d = TTile.Depth, h = TTile.Height, r = TTile.Halo;
        if (w < 1 || d is < 1 or > 64 || h < 1) return "Width and Height must be 1 or more, Depth 1 to 64";
        if (r < 1) return "the halo must be 1 or more";
        var faces = TTile.Faces;
        bool mirror = TTile.Boundary == TileBoundary.Mirror;
        if (r > w && (mirror || (faces & (TileFaces.West | TileFaces.East)) != 0)) return $"a halo of {r} is wider than its rows ({w} cells)";
        if (r > d && (mirror || (faces & (TileFaces.North | TileFaces.South)) != 0)) return $"a halo of {r} is deeper than its layers ({d} rows)";
        if (h > 1 && r > h && (mirror || (faces & (TileFaces.Below | TileFaces.Above)) != 0)) return $"a halo of {r} is taller than the tile ({h} layers)";
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw() => throw new InvalidOperationException(
        $"{typeof(TTile).Name} is a {TTile.Width} x {TTile.Depth} x {TTile.Height} tile with a halo of {TTile.Halo}: {Problem}");
}

/// <summary>Row copies and tests of cells, as bytes: whole vectors where the row has them.</summary>
internal static class TileLayout
{
    /// <summary>The cells of a padded row of <typeparamref name="TTile"/>: Width and the halo on both sides.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Row<TTile>() where TTile : ITileShape => TTile.Width + 2 * TTile.Halo;

    /// <summary>The cells of a padded layer of <typeparamref name="TTile"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Layer<TTile>() where TTile : ITileShape => Row<TTile>() * (TTile.Depth + 2 * TTile.Halo);

    /// <summary>The layers of a texture of <typeparamref name="TTile"/>: all of the tile's, and the halo under and over a tall one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Layers<TTile>() where TTile : ITileShape => TTile.Height + (TTile.Height > 1 ? 2 * TTile.Halo : 0);

    /// <summary>The cells of a tile of <typeparamref name="TTile"/> (its buffer's count).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Cells<TTile>() where TTile : ITileShape => TTile.Width * TTile.Depth * TTile.Height;

    /// <summary>
    /// Whether row (<paramref name="y"/>, <paramref name="z"/>) of a tile of <typeparamref name="TTile"/> changed from
    /// <paramref name="before"/> to <paramref name="after"/> (Width cells each, compared as bytes), and when it did, the
    /// faces whose edge it holds a changed cell of (<paramref name="edges"/>): up to the halo from the face, which the
    /// neighbor across reads.
    /// </summary>
    public static bool RowEdges<TCell, TTile>(ReadOnlySpan<TCell> before, ReadOnlySpan<TCell> after, int y, int z, ref TileFaces edges)
        where TCell : unmanaged where TTile : ITileShape
    {
        var was = MemoryMarshal.AsBytes(before);
        var now = MemoryMarshal.AsBytes(after);
        if (was.SequenceEqual(now)) return false;
        int r = TTile.Halo, depth = TTile.Depth, height = TTile.Height;
        int edge = Math.Min(r, TTile.Width) * Unsafe.SizeOf<TCell>();
        if (z < r) edges |= TileFaces.North;
        if (z >= depth - r) edges |= TileFaces.South;
        if (height > 1 && y < r) edges |= TileFaces.Below;
        if (height > 1 && y >= height - r) edges |= TileFaces.Above;
        if (!was[..edge].SequenceEqual(now[..edge])) edges |= TileFaces.West;
        if (!was[^edge..].SequenceEqual(now[^edge..])) edges |= TileFaces.East;
        return true;
    }

    /// <summary>Whether two rows of cells hold the same bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equal<TCell>(ReadOnlySpan<TCell> a, ReadOnlySpan<TCell> b) where TCell : unmanaged =>
        MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));

    /// <summary>Whether a cell of <paramref name="cells"/> has a bit set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AnyNotZero<TCell>(ReadOnlySpan<TCell> cells) where TCell : unmanaged =>
        MemoryMarshal.AsBytes(cells).IndexOfAnyExcept((byte)0) >= 0;

    /// <summary>Whether two cells hold the same bits.</summary>
    public static bool Same<TCell>(TCell a, TCell b) where TCell : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<TCell>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TCell>(in b)));

    /// <summary>
    /// The <paramref name="count"/> halo cells beside a tile's edge cell <paramref name="edge"/>: that cell repeated, or
    /// with <paramref name="mirror"/> the cells inward from it, the first beside the edge (<paramref name="before"/>:
    /// <paramref name="target"/> ends at the edge, the cells after it are read; else it starts there and the cells before
    /// are read); whether one is not zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Edge<TCell>(ref TCell target, ref TCell edge, int count, bool mirror, bool before) where TCell : unmanaged
    {
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            TCell cell = mirror ? Unsafe.Add(ref edge, before ? count - 1 - i : -i) : edge;
            Unsafe.Add(ref target, i) = cell;
            any |= !IsZero(cell);
        }
        return any;
    }

    /// <summary>
    /// Copies <paramref name="count"/> cells from <paramref name="source"/> to <paramref name="target"/>; whether one of
    /// them is not zero. Unchecked: the caller holds both ranges in bounds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Copy<TCell>(ref TCell target, ref TCell source, int count) where TCell : unmanaged
    {
        if (count == 1) // a halo of one cell: one move, tested in its register (not read again after the store)
        {
            TCell cell = source;
            target = cell;
            return !IsZero(cell);
        }
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

    // Not NoInlining: the JIT then sees that the method only throws and compiles its calls as calls that never return,
    // so the vectors a stencil loaded before a check stay in registers (a call that may return clobbers every one).
    [DoesNotReturn, StackTraceHidden]
    public static void ThrowOffset() => throw new ArgumentOutOfRangeException(null, "Outside the tile and its halo: a cell, row or layer past the halo");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see ThrowOffset
    public static void ThrowNoMask() => throw new InvalidOperationException("The tile's halo was exchanged without its mask (mask: false): it has no active rows");
}
