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
/// face names a neighbour (<see cref="ITile{TKey}.Beside"/>, <see cref="Tile{TCell, TTile}.Beside(TileFaces)"/>); a set of
/// them is <see cref="ITileShape.Faces"/>, the faces that have neighbours.
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
    /// <summary>North, south, west and east: the neighbours of a 2D grid, or of the columns of a voxel world.</summary>
    Sides = North | South | West | East,
    /// <summary>The six faces: the neighbours of a 3D grid of cubes.</summary>
    All = Sides | Below | Above,
}

/// <summary>
/// Which tiles around a tile are its neighbours, by how many axes their offset moves along (<see cref="ITileShape.Neighbourhood"/>):
/// the tiles across a face (von Neumann), also across an edge, or also across a corner (Moore). In 2D a tile has 4,
/// 8 and 8; in 3D 6, 18 and 26. Only the axes of <see cref="ITileShape.Faces"/> count: a voxel column with neighbours
/// on its sides only has 4, 8 and 8.
/// </summary>
public enum TileNeighbourhood : byte
{
    /// <summary>The tiles across a face (offsets along one axis): a five- or seven-point stencil reads no more.</summary>
    Faces,
    /// <summary>Also the tiles across an edge (offsets along two axes): the corners of a 2D grid, a 19-point stencil.</summary>
    FacesAndEdges,
    /// <summary>Every tile around (offsets along up to three axes): a 9-point 2D or 27-point 3D stencil.</summary>
    All,
}

/// <summary>
/// What the halo holds where a tile has no neighbour (<see cref="ITileShape.Boundary"/>), the boundary condition of
/// the grid. A periodic grid needs none: its <see cref="ITile{TKey}.Beside"/> wraps, so every tile has neighbours.
/// </summary>
public enum TileBoundary : byte
{
    /// <summary>The fill value of <see cref="Tile{TCell, TTile}.Load(int, int, TCell, bool)"/> (default zero): a Dirichlet boundary.</summary>
    Fill,
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
    /// The faces that have neighbours (default <see cref="TileFaces.Sides"/>); the halo of the others follows
    /// <see cref="Boundary"/>. <see cref="TileFaces.Below"/> and <see cref="TileFaces.Above"/> count only for a tile of
    /// more than one layer; a 1D tile (one row) declares <c>West | East</c>.
    /// </summary>
    static virtual TileFaces Faces => TileFaces.Sides;

    /// <summary>
    /// The width of the halo, in cells (default 1): the ghost cells of an MPI halo exchange, the apron of a CUDA tile.
    /// A stencil reaching r cells, rows or layers away needs r. At most the tile's extent along every axis with
    /// neighbours (and along every axis for <see cref="TileBoundary.Mirror"/>): a halo never reaches past the tile beside.
    /// </summary>
    static virtual int Halo => 1;

    /// <summary>Which tiles around are found and read into the halo's edges and corners (default <see cref="TileNeighbourhood.Faces"/>).</summary>
    static virtual TileNeighbourhood Neighbourhood => TileNeighbourhood.Faces;

    /// <summary>What the halo holds where there is no neighbour (default <see cref="TileBoundary.Fill"/>).</summary>
    static virtual TileBoundary Boundary => TileBoundary.Fill;
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
    /// <summary>
    /// The key of the tile beside the one at <paramref name="key"/> across <paramref name="face"/> (one face of
    /// <see cref="ITileShape.Faces"/>). The tiles across an edge or a corner are found by stepping across one face at a
    /// time, x first, then z, then y. On a periodic grid it wraps (the last tile's east is the first).
    /// </summary>
    static abstract TKey Beside(TKey key, TileFaces face);
}

/// <summary>
/// What a stencil reads around a cell, which decides the rows <see cref="PaddedTile{TCell, TTile}.Rows"/> visits: a row
/// is visited when one of the rows it reads holds a cell that is not zero (a row counts its halo cells, so a stencil
/// reading along x needs nothing more than the row). A stencil whose result is zero wherever everything it reads is
/// zero (water that does not flow out of nothing, heat that does not rise from cold) then skips the quiet rows and gets
/// the same result. A row's reach is the shape's <see cref="ITileShape.Halo"/>.
/// </summary>
[Flags]
public enum TileReads : byte
{
    /// <summary>The row itself, with the cells before and after it (west and east).</summary>
    Self = 1,
    /// <summary>The rows north and south of it in its layer, with their halo cells (the 2D diagonals too).</summary>
    Sides = 2,
    /// <summary>The rows over it, in the layers above.</summary>
    Above = 4,
    /// <summary>The rows under it, in the layers below.</summary>
    Below = 8,
    /// <summary>The rows north and south of it in the layers over and under it: the 3D edges and corners.</summary>
    Diagonals = 16,
    /// <summary>The row and its four sides: a 2D five- or nine-point stencil.</summary>
    Layer = Self | Sides,
    /// <summary>The row, its sides and the rows over and under it: a 3D seven-point stencil.</summary>
    Cross = Self | Sides | Above | Below,
    /// <summary>Every row around: a 3D 27-point stencil, and the safe default.</summary>
    All = Cross | Diagonals,
}

/// <summary>
/// The neighbours of a tile, found once per entity by generated code and shared by the method's
/// <see cref="Tile{TCell, TTile}"/> parameters of one grid: one id per offset (dx, dy, dz) in −1..1, default where there
/// is none. Generated code uses it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[InlineArray(27)]
public struct TileNeighbours
{
    private EntityId _first;

    /// <summary>
    /// The neighbours of <paramref name="entity"/> of <typeparamref name="TTile"/>'s neighbourhood, found in
    /// <paramref name="index"/> (its built value index, <see cref="LookupExtensions.Prepared{T, TKey}"/>): one index
    /// lookup per neighbour. Generated code calls this; compiled on its own, for the shape.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Of<TTile, TKey>(object index, Entity entity, out TileNeighbours around)
        where TTile : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        var grid = index as ValueIndex<TTile, TKey> ?? Unprepared<TTile, TKey>();
        TKey key = entity.Find<TTile>(true).Key;
        around = default;
        if (TTile.Neighbourhood == TileNeighbourhood.Faces)
        {
            // The faces alone (a constant): one Beside each, unrolled, the faces the shape lacks folded away.
            Face(grid, key, TileFaces.North, 10, ref around);
            Face(grid, key, TileFaces.South, 16, ref around);
            Face(grid, key, TileFaces.West, 12, ref around);
            Face(grid, key, TileFaces.East, 14, ref around);
            Face(grid, key, TileFaces.Below, 4, ref around);
            Face(grid, key, TileFaces.Above, 22, ref around);
            return;
        }
        for (uint slots = TileOffsets<TTile>.Slots; slots != 0; slots &= slots - 1)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            int dx = slot % 3 - 1, dz = slot / 3 % 3 - 1, dy = slot / 9 - 1;
            TKey at = key;
            if (dx != 0) at = TTile.Beside(at, dx < 0 ? TileFaces.West : TileFaces.East);
            if (dz != 0) at = TTile.Beside(at, dz < 0 ? TileFaces.North : TileFaces.South);
            if (dy != 0) at = TTile.Beside(at, dy < 0 ? TileFaces.Below : TileFaces.Above);
            if (grid.TryFind(at, out var found)) around[slot] = found.Id;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Face<TTile, TKey>(ValueIndex<TTile, TKey> grid, TKey key, TileFaces face, int slot, ref TileNeighbours around)
        where TTile : struct, IComponent, ITile<TKey> where TKey : IEquatable<TKey>
    {
        if ((TileOffsets<TTile>.Slots >> slot & 1) != 0 && grid.TryFind(TTile.Beside(key, face), out var found)) around[slot] = found.Id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueIndex<TTile, TKey> Unprepared<TTile, TKey>() where TTile : struct, IComponent, IIndexed<TKey> where TKey : IEquatable<TKey> =>
        throw new ArgumentException($"The index passed is not the built index of {typeof(TTile).Name} by {typeof(TKey).Name}");
}

/// <summary>The offsets around a tile of <typeparamref name="TTile"/>'s shape: slot (dx + 1) + 3 (dz + 1) + 9 (dy + 1).</summary>
internal static class TileOffsets<TTile> where TTile : struct, ITileShape
{
    /// <summary>The slots of the neighbours the shape has (its faces' axes, its neighbourhood), not the tile's own (13).</summary>
    public static readonly uint Slots = Compute();

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
        int axes = (int)TTile.Neighbourhood + 1;
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

/// <summary>The buffers of a tile and its neighbours, by slot (<see cref="TileOffsets{TTile}"/>): null where there are no cells.</summary>
[InlineArray(27)]
internal struct TileSources<TCell>
{
    private TCell[] _first;
}

/// <summary>
/// The cells of a tile (an entity whose <typeparamref name="TTile"/> makes it one, <see cref="ITile{TKey}"/>) with those
/// of its neighbours: a <c>[ForEach]</c> parameter, as a CUDA block's tile and its apron, or an MPI rank's subdomain
/// and its ghost cells. <c>static void Diffuse(Tile&lt;Heat, Plate&gt; heat, Mut&lt;DynamicBuffer&lt;NextHeat&gt;&gt; next)</c>:
/// the generated job finds the neighbours by key (one search per neighbour and entity for every tile of the grid), and
/// <see cref="Load(int, int, TCell, bool)"/> copies the layers the stencil shades into a padded tile
/// (<see cref="PaddedTile{TCell, TTile}"/>): the entity's cells in the middle, a halo of
/// <see cref="ITileShape.Halo"/> cells around them from the neighbours' facing cells, and the boundary
/// (<see cref="ITileShape.Boundary"/>) where there is no neighbour, so the stencil has no edge cases.
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
    public EntityId Beside(TileFaces face) => _around[TileOffsets<TTile>.Slot(face)];

    /// <summary>
    /// The neighbour at offset (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>), each −1 to 1 (the
    /// tile across a face, an edge or a corner), or default when there is none or the shape's neighbourhood leaves it
    /// out. O(1).
    /// </summary>
    public EntityId Beside(int dx, int dy, int dz)
    {
        if ((uint)(dx + 1) > 2u || (uint)(dy + 1) > 2u || (uint)(dz + 1) > 2u) Cell.ThrowOffset();
        return _around[TileOffsets<TTile>.Slot(dx, dy, dz)];
    }

    /// <summary>All the layers padded, the halo of a missing neighbour zero: <see cref="Load(int, int, TCell, bool)"/>.</summary>
    public PaddedTile<TCell, TTile> Load() => Load(0, TTile.Height - 1);

    /// <summary>
    /// Layers <paramref name="from"/> to <paramref name="to"/> padded into this thread's texture: each layer's cells in
    /// the middle, a ring of <see cref="ITileShape.Halo"/> (r) cells around each layer (the facing columns of the west and
    /// east neighbours, the facing rows of the north and south ones, the corners from the tiles across the edges when the
    /// neighbourhood has them), and, for a tile of more than one layer, r layers under <paramref name="from"/> and over
    /// <paramref name="to"/> (the tile's own, or the facing layers of the neighbours below or above). Where there is no
    /// neighbour (none in the grid, an empty buffer, or outside the shape's faces and neighbourhood) the halo follows
    /// <see cref="ITileShape.Boundary"/>: <paramref name="fill"/>, or the tile's own edge clamped or mirrored (along the
    /// axis that has no neighbour; a tile across an edge or corner whose axis is clamped is read from the tile beside).
    /// With <paramref name="mask"/>, it records which rows hold a cell that is not zero (every bit), for
    /// <see cref="PaddedTile{TCell, TTile}.Rows"/>.
    /// <para>
    /// O(layers × (Width + 2r) × (Depth + 2r)), no allocation once the thread has its texture (allocated on its first
    /// load, for all the layers). The texture belongs to the thread and to this tile type: the next load of
    /// <typeparamref name="TCell"/> and <typeparamref name="TTile"/> on the thread replaces it, so a method takes one tile
    /// per cell type and grid (EECS073). Throws when the shape is invalid (EECS072), the entity's buffer or a neighbour's
    /// that is not empty holds fewer cells than the shape, or the layers are not 0 ≤ from ≤ to &lt; Height.
    /// </para>
    /// </summary>
    public PaddedTile<TCell, TTile> Load(int from, int to, TCell fill = default, bool mask = true)
    {
        Shape<TTile>.Check();
        if (from < 0 || from > to || to >= TTile.Height) ThrowLayers(from, to);
        // The common shape (a five- or seven-point stencil: one cell of halo, the faces, a fill boundary) has its own
        // loop, which the JIT keeps for that shape alone (the condition is a constant): no corner, no remapping.
        if (TTile.Halo == 1 && TTile.Neighbourhood == TileNeighbourhood.Faces && TTile.Boundary == TileBoundary.Fill)
            return LoadFaces(from, to, fill, mask);
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, r = TTile.Halo, hy = height > 1 ? r : 0;
        int row = width + 2 * r, layer = row * (depth + 2 * r), cells = width * depth * height;
        int layers = to - from + 1 + 2 * hy;
        ref TCell texels = ref TileMemory<TCell, TTile>.Texels();
        ref TileMask masks = ref TileMemory<TCell, TTile>.Masks();

        // The buffers around: a missing one (no tile there, an empty buffer, outside the neighbourhood) is the blank,
        // cells of the fill, so every row below is copied without a branch on which neighbours are there.
        bool zero = Cell.IsZero(fill);
        TCell[] blank = TileMemory<TCell, TTile>.Blank(fill, zero);
        var sources = default(TileSources<TCell>);
        for (int slot = 0; slot < 27; slot++) sources[slot] = blank;
        sources[13] = Own(cells);
        uint present = 1u << 13;
        for (uint slots = TileOffsets<TTile>.Slots; slots != 0; slots &= slots - 1)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            if (Of(_around[slot], cells) is { } there)
            {
                sources[slot] = there;
                present |= 1u << slot;
            }
        }

        // Along x, the west and east halo of every row: the neighbour's cells, or the tile's own (clamped, mirrored).
        bool clampWest = TTile.Boundary != TileBoundary.Fill && (present >> 12 & 1) == 0;
        bool clampEast = TTile.Boundary != TileBoundary.Fill && (present >> 14 & 1) == 0;

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
            // Bands: bit z + r of each halo row that is not zero, where the texture's rows fit a word (Steps reads them).
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

        return new PaddedTile<TCell, TTile>(ref texels, ref (mask ? ref masks : ref Unsafe.NullRef<TileMask>()), from, to, present, fill);
    }

    /// <summary>
    /// <see cref="Load(int, int, TCell, bool)"/> of a shape with a halo of one cell, the faces and a fill boundary: per row
    /// one copy of Width cells and the two cells beside it, the corners the fill. The same texture and masks.
    /// </summary>
    private PaddedTile<TCell, TTile> LoadFaces(int from, int to, TCell fill, bool mask)
    {
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, hy = height > 1 ? 1 : 0;
        int row = width + 2, layer = row * (depth + 2), cells = width * depth * height;
        int layers = to - from + 1 + 2 * hy;
        ref TCell texels = ref TileMemory<TCell, TTile>.Texels();
        ref TileMask masks = ref TileMemory<TCell, TTile>.Masks();
        bool zero = Cell.IsZero(fill);
        TCell[] blank = TileMemory<TCell, TTile>.Blank(fill, zero);

        // A missing neighbour reads the blank (cells of the fill), so the row loop copies without a branch on it.
        uint present = 1u << 13;
        TCell[] north = Beside(10, cells, blank, ref present), south = Beside(16, cells, blank, ref present);
        TCell[] west = Beside(12, cells, blank, ref present), east = Beside(14, cells, blank, ref present);
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
                // none), its ring the fill.
                var across = Beside(y < 0 ? 4 : 22, cells, blank, ref present);
                var edge = Edge(ref at, ref MemoryMarshal.GetArrayDataReference(across), y < 0 ? height - 1 : 0, fill, zero);
                if (mask) Unsafe.Add(ref masks, t) = edge;
                continue;
            }

            ulong rows = 0;
            int first = y * depth * width;
            for (int z = 0; z < depth; z++)
            {
                int at0 = first + z * width;
                ref TCell into = ref Unsafe.Add(ref at, (z + 1) * row + 1);
                bool any = Cell.Copy(ref into, ref Unsafe.Add(ref s, at0), width);
                any |= Cell.Copy(ref Unsafe.Add(ref into, -1), ref Unsafe.Add(ref w, at0 + width - 1), 1);
                any |= Cell.Copy(ref Unsafe.Add(ref into, width), ref Unsafe.Add(ref e, at0), 1);
                if (any) rows |= 1UL << z;
            }

            // The north neighbour's last row and the south neighbour's first, and the four corners (the fill).
            ref TCell top = ref Unsafe.Add(ref at, 1);
            ref TCell bottom = ref Unsafe.Add(ref at, (depth + 1) * row + 1);
            bool northWet = Cell.Copy(ref top, ref Unsafe.Add(ref n, first + (depth - 1) * width), width) | !zero;
            bool southWet = Cell.Copy(ref bottom, ref Unsafe.Add(ref so, first), width) | !zero;
            at = fill;
            Unsafe.Add(ref top, width) = fill;
            Unsafe.Add(ref bottom, -1) = fill;
            Unsafe.Add(ref bottom, width) = fill;
            if (mask) Unsafe.Add(ref masks, t) = TileMask.Of<TTile>(rows, (northWet ? 1UL : 0) | (southWet ? southBand : 0));
        }

        return new PaddedTile<TCell, TTile>(ref texels, ref (mask ? ref masks : ref Unsafe.NullRef<TileMask>()), from, to, present, fill);
    }

    /// <summary>The cells of the neighbour in <paramref name="slot"/> (marked in <paramref name="present"/>), or the blank.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TCell[] Beside(int slot, int cells, TCell[] blank, ref uint present)
    {
        if ((TileOffsets<TTile>.Slots >> slot & 1) == 0 || Of(_around[slot], cells) is not { } there) return blank;
        present |= 1u << slot;
        return there;
    }

    /// <summary>
    /// A layer under or over the tile for <see cref="LoadFaces"/>: <paramref name="source"/>'s layer <paramref name="y"/>
    /// in the middle, the ring the fill; its mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TileMask Edge(ref TCell at, ref TCell source, int y, TCell fill, bool zero)
    {
        int width = TTile.Width, depth = TTile.Depth, row = width + 2;
        ulong rows = 0;
        for (int z = 0; z < depth; z++)
        {
            ref TCell into = ref Unsafe.Add(ref at, (z + 1) * row + 1);
            if (Cell.Copy(ref into, ref Unsafe.Add(ref source, (y * depth + z) * width), width) | !zero) rows |= 1UL << z;
            Unsafe.Add(ref into, -1) = fill;
            Unsafe.Add(ref into, width) = fill;
        }
        MemoryMarshal.CreateSpan(ref at, row).Fill(fill);
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref at, (depth + 1) * row), row).Fill(fill);
        return TileMask.Of<TTile>(rows, zero ? 0 : 1UL | (depth + 2 <= 64 ? 1UL << (depth + 1) : 2));
    }

    /// <summary>
    /// Whether <paramref name="next"/> (the tile's step, in the layout of its cells) differs from the tile's cells, and
    /// the faces whose edge changed: a cell up to <see cref="ITileShape.Halo"/> cells from the face, which the neighbour
    /// across it reads into its halo. A tile that did not change, beside tiles whose facing edges did not change,
    /// computes the same step again: let it sleep, and wake the tiles <see cref="Facing"/> the edges that changed, so a
    /// sparse grid costs what its active tiles cost. O(cells), compared row by row as bytes.
    /// </summary>
    /// <param name="next">The step: at least the tile's cells.</param>
    /// <param name="changed">Whether any cell changed.</param>
    /// <returns>The faces whose edge changed (<see cref="TileFaces.Below"/> and <see cref="TileFaces.Above"/> only for a tile of more than one layer).</returns>
    public TileFaces Changes(ReadOnlySpan<TCell> next, out bool changed)
    {
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, r = TTile.Halo;
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
            var before = MemoryMarshal.AsBytes(own.Slice(at, width));
            var after = MemoryMarshal.AsBytes(next.Slice(at, width));
            if (before.SequenceEqual(after)) continue;
            changed = true;
            int edge = Math.Min(r, width) * Unsafe.SizeOf<TCell>();
            if (z < r) edges |= TileFaces.North;
            if (z >= depth - r) edges |= TileFaces.South;
            if (height > 1 && y < r) edges |= TileFaces.Below;
            if (height > 1 && y >= height - r) edges |= TileFaces.Above;
            if (!before[..edge].SequenceEqual(after[..edge])) edges |= TileFaces.West;
            if (!before[^edge..].SequenceEqual(after[^edge..])) edges |= TileFaces.East;
        }

        return edges;
    }

    /// <summary>
    /// The neighbours that read an edge of <paramref name="edges"/> into their halo (from <see cref="Changes"/>): the
    /// tile across each face, and, in the shape's neighbourhood, across each edge and corner of two or three of those
    /// faces; the tiles there are (<c>foreach (var id in tile.Facing(edges))</c>). O(1) per neighbour.
    /// </summary>
    public TileIds Facing(TileFaces edges)
    {
        uint slots = 0;
        for (uint all = TileOffsets<TTile>.Slots; all != 0; all &= all - 1)
        {
            int slot = BitOperations.TrailingZeroCount(all);
            int dx = slot % 3 - 1, dz = slot / 3 % 3 - 1, dy = slot / 9 - 1;
            if (dx != 0 && (edges & (dx < 0 ? TileFaces.West : TileFaces.East)) == 0) continue;
            if (dz != 0 && (edges & (dz < 0 ? TileFaces.North : TileFaces.South)) == 0) continue;
            if (dy != 0 && (edges & (dy < 0 ? TileFaces.Below : TileFaces.Above)) == 0) continue;
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
    /// from <paramref name="east"/> (the blank of fill cells where there is no neighbour). Whether a cell is not zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Row(ref TCell into, ref TCell mid, ref TCell west, ref TCell east, bool clampWest, bool clampEast)
    {
        int width = TTile.Width, r = TTile.Halo;
        bool any = Cell.Copy(ref into, ref mid, width);
        ref TCell before = ref Unsafe.Add(ref into, -r);
        ref TCell after = ref Unsafe.Add(ref into, width);
        // clampWest / clampEast are false for a fill boundary, a constant: the copies are the whole row then.
        if (!clampWest) any |= Cell.Copy(ref before, ref Unsafe.Add(ref west, width - r), r);
        else any |= Cell.Edge(ref before, ref west, r, TTile.Boundary == TileBoundary.Mirror, true);
        if (!clampEast) any |= Cell.Copy(ref after, ref east, r);
        else any |= Cell.Edge(ref after, ref Unsafe.Add(ref east, width - 1), r, TTile.Boundary == TileBoundary.Mirror, false);
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
        if (TTile.Boundary == TileBoundary.Fill || (low ? before : after))
        {
            // Past the tile beside too (a halo wider than an axis without faces, which Shape.Check allows): that tile
            // is the blank, the same cell everywhere, so any coordinate in it reads the fill.
            s = low ? c + n : c - n;
            if ((uint)s >= (uint)n) s = 0;
            return low ? -1 : 1;
        }
        s = TTile.Boundary == TileBoundary.Clamp ? (low ? 0 : n - 1) : (low ? -1 - c : 2 * n - 1 - c);
        return 0;
    }

    private TCell[] Own(int cells)
    {
        var own = _entity.Find<DynamicBuffer<TCell>>(true).Store;
        int count = own?.Count ?? 0;
        if (count < cells) ThrowCells(_entity.Id, count, cells);
        return own!.Items;
    }

    /// <summary>The cells of <paramref name="id"/>: null when there is no such tile, or it has no cells.</summary>
    private TCell[] Of(EntityId id, int cells)
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
    private static void ThrowCells(EntityId id, int length, int cells) =>
        throw new InvalidOperationException($"The DynamicBuffer<{typeof(TCell).Name}> of {id} holds {length} cells; a {typeof(TTile).Name} tile has {cells}");
}

/// <summary>The neighbours of a tile that <see cref="Tile{TCell, TTile}.Facing"/> names, walked with <c>foreach</c>.</summary>
public ref struct TileIds
{
    private readonly ref readonly TileNeighbours _around;
    private uint _slots;
    private int _slot;

    internal TileIds(ref readonly TileNeighbours around, uint slots)
    {
        _around = ref around;
        _slots = slots;
        _slot = -1;
    }

    /// <summary>Itself: <c>foreach</c> walks it.</summary>
    public readonly TileIds GetEnumerator() => this;

    /// <summary>The neighbour the walk is at.</summary>
    public readonly EntityId Current => _around[_slot];

    /// <summary>Moves to the next neighbour; false after the last.</summary>
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
internal readonly record struct TileMask(ulong Rows, ulong Ends, ulong Bands)
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
        return new TileMask(rows, (wetNorth ? north : 0) | (wetSouth ? south : 0), bands);
    }

    /// <summary>The rows of the texture (bit z + r, halo rows too) that hold a cell that is not zero: Depth + 2r ≤ 64 only.</summary>
    public ulong Texture<TTile>() where TTile : ITileShape => Rows << TTile.Halo | Bands;
}

/// <summary>
/// Layers of a tile padded with their halo (<see cref="Tile{TCell, TTile}.Load(int, int, TCell, bool)"/>): the texture a
/// stencil samples. Walk the rows to shade with <see cref="Rows"/> (the active ones, by counting trailing zeros) and
/// sample around a row with <see cref="At"/>: every sample up to <see cref="ITileShape.Halo"/> cells, rows or layers
/// away stays inside the texture, which is checked when the offsets are not constants and folded away when they are.
/// Valid until the thread loads this tile type again.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly ref struct PaddedTile<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    private readonly ref TCell _texels;
    private readonly ref TileMask _masks;
    private readonly int _from, _to;
    private readonly uint _present; // the slots whose cells the halo holds (TileOffsets), the tile's own (13) too
    private readonly TCell _fill;

    internal PaddedTile(ref TCell texels, ref TileMask masks, int from, int to, uint present, TCell fill)
    {
        _texels = ref texels;
        _masks = ref masks;
        _from = from;
        _to = to;
        _present = present;
        _fill = fill;
    }

    /// <summary>The first layer loaded (the halo adds r under it).</summary>
    public int From => _from;

    /// <summary>The last layer loaded (the halo adds r over it).</summary>
    public int To => _to;

    /// <summary>
    /// Cell (x, y, z) of the texture: x from −r to Width + r − 1, z from −r to Depth + r − 1, y from From − r to To + r
    /// (From to To for a 2D tile), r the halo. Throws outside. O(1).
    /// </summary>
    public ref readonly TCell this[int x, int y, int z]
    {
        get
        {
            int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
            if ((uint)(x + r) >= (uint)(TTile.Width + 2 * r) || (uint)(z + r) >= (uint)(TTile.Depth + 2 * r)
                || (uint)(y - _from + hy) > (uint)(_to - _from + 2 * hy))
                Cell.ThrowOffset();
            return ref Unsafe.Add(ref _texels, (y - _from + hy) * Cell.Layer<TTile>() + (z + r) * Cell.Row<TTile>() + x + r);
        }
    }

    /// <summary>
    /// The rows of layers From to To that a stencil reading <paramref name="reads"/> must shade, layer by layer, in
    /// order (<c>foreach (var row in padded.Rows(TileReads.Layer))</c>): those with a cell that is not zero among the
    /// rows it reads, up to the halo away. O(1) per row visited and per layer. Throws when the tile was loaded without
    /// its mask.
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
        int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        return new TileCursor<TCell, TTile>(ref Unsafe.Add(ref _texels,
            (row.Y - _from + hy) * Cell.Layer<TTile>() + (row.Z + r) * Cell.Row<TTile>() + r));
    }

    /// <summary>
    /// The loaded layers' cells (From to To, no halo) into <paramref name="cells"/>, in the layout of the tile's buffer:
    /// the result of <see cref="Steps{TStep, TLane}"/> into the step's buffer. O(layers × Width × Depth).
    /// </summary>
    public void CopyTo(Span<TCell> cells)
    {
        int width = TTile.Width, depth = TTile.Depth, r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        if (cells.Length < width * depth * TTile.Height) Cell.ThrowOffset();
        for (int y = _from; y <= _to; y++)
        for (int z = 0; z < depth; z++)
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _texels, (y - _from + hy) * Cell.Layer<TTile>() + (z + r) * Cell.Row<TTile>() + r), width)
                .CopyTo(cells.Slice((y * depth + z) * width, width));
    }

    /// <summary>
    /// <see cref="ITileShape.Halo"/> / <c>TStep.Reach</c> steps of a stencil in one pass (temporal blocking): each
    /// step computes the texture with <typeparamref name="TStep"/> into this thread's second texture and the next step
    /// reads it, ping-pong, the halo shrinking by the reach each step (the cells further out no longer have what they
    /// read), so after the last the tile's own cells hold the stencil applied that many times, from one load. The rows
    /// a step skips (<see cref="ITileStep{TCell, TTile, TLane}.Reads"/>) are recomputed every step, and where there is
    /// no neighbour the halo keeps following the boundary (<see cref="ITileShape.Boundary"/>). Write the result with
    /// <see cref="CopyTo"/>. Needs the tile's mask, a neighbourhood of <see cref="TileNeighbourhood.All"/> for more than
    /// one step (the cells a few steps reach lie across corners), Depth + 2r ≤ 64 and rows of at least 16 bytes.
    /// <para>
    /// O(steps × layers × (Width + 2r) × (Depth + 2r)) at most, no allocation once the thread has its second texture.
    /// The result is valid until the thread loads or steps this tile type again.
    /// </para>
    /// </summary>
    /// <typeparam name="TStep">The step: a struct, its <c>Step</c> compiled into the loop.</typeparam>
    /// <typeparam name="TLane">The lanes the step computes in.</typeparam>
    public PaddedTile<TCell, TTile> Steps<TStep, TLane>() where TStep : struct, ITileStep<TCell, TTile, TLane>
    {
        if (Unsafe.IsNullRef(ref _masks)) Cell.ThrowNoMask();
        TileSteps<TCell, TTile, TStep, TLane>.Check();
        int width = TTile.Width, depth = TTile.Depth, r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
        int reach = TStep.Reach, steps = r / reach, row = Cell.Row<TTile>(), layer = Cell.Layer<TTile>();
        int layers = _to - _from + 1 + 2 * hy, lanes = Vector128<byte>.Count / Unsafe.SizeOf<TCell>();
        ulong interior = (depth == 64 ? ulong.MaxValue : (1UL << depth) - 1) << r;
        ref TCell from = ref _texels;
        ref TileMask fromMasks = ref _masks;
        ref TCell back = ref TileMemory<TCell, TTile>.Back();
        bool onBack = Unsafe.AreSame(ref from, ref back); // stepping a result again: ping-pong with the first texture
        ref TCell into = ref onBack ? ref TileMemory<TCell, TTile>.Texels() : ref back;
        ref TileMask intoMasks = ref onBack ? ref TileMemory<TCell, TTile>.Masks() : ref TileMemory<TCell, TTile>.BackMasks();

        // The bounds: at step s the texels computed are those up to m = (steps - s) * reach from the tile (layers
        // too, for a tall tile), and each reads its own up to reach away, so inside m + reach <= r: the texture. A Step
        // that samples further (up to r, which TileCursor checks) stays inside the guard around it (TileMemory.Guard).
        for (int s = 1; s <= steps; s++)
        {
            int m = (steps - s) * reach, my = hy > 0 ? m : 0;
            for (int t = hy - my; t <= layers - 1 - hy + my; t++)
            {
                ulong need = Need<TStep, TLane>(ref fromMasks, t, reach), active = 0;
                ref TCell source = ref Unsafe.Add(ref from, t * layer + r * row + r);
                ref TCell target = ref Unsafe.Add(ref into, t * layer + r * row + r);
                for (int z = -m; z < depth + m; z++)
                {
                    ref TCell at = ref Unsafe.Add(ref target, z * row);
                    if ((need >> (z + r) & 1) == 0)
                    {
                        // Nothing it reads is set: zero, as the step of zeros is; the next step reads this row.
                        Unsafe.InitBlockUnaligned(ref Unsafe.As<TCell, byte>(ref Unsafe.Add(ref at, -m)), 0, (uint)((width + 2 * m) * Unsafe.SizeOf<TCell>()));
                        continue;
                    }

                    var cursor = new TileCursor<TCell, TTile>(ref Unsafe.Add(ref source, z * row));
                    var any = Vector128<byte>.Zero;
                    for (int x = -m, last = width + m - lanes; ; x = Math.Min(x + lanes, last))
                    {
                        var next = TStep.Step(cursor, x).AsByte();
                        next.StoreUnsafe(ref Unsafe.As<TCell, byte>(ref Unsafe.Add(ref at, x)));
                        any |= next;
                        if (x == last) break;
                    }
                    if (any != Vector128<byte>.Zero) active |= 1UL << (z + r);
                }

                Unsafe.Add(ref intoMasks, t) = TileMask.Of<TTile>((active & interior) >> r, active & ~interior);
            }

            Boundary(ref into, ref intoMasks, m, layers);
            ref TCell texels = ref from;
            from = ref into;
            into = ref texels;
            ref TileMask masks = ref fromMasks;
            fromMasks = ref intoMasks;
            intoMasks = ref masks;
        }

        return new PaddedTile<TCell, TTile>(ref from, ref fromMasks, _from, _to, _present, _fill);
    }

    /// <summary>
    /// The halo texels up to <paramref name="m"/> from the tile that no neighbour holds, after a step computed them:
    /// the fill again, or the tile's own clamped or mirrored (along the axes with no neighbour, as Load put them), so a
    /// boundary stays a boundary from step to step. The rows written are marked active (their cells may not be zero).
    /// O(the boundary's texels); nothing where every tile around is there.
    /// </summary>
    private void Boundary(ref TCell texels, ref TileMask masks, int m, int layers)
    {
        int width = TTile.Width, depth = TTile.Depth, height = TTile.Height, r = TTile.Halo, hy = height > 1 ? r : 0;
        int row = Cell.Row<TTile>(), layer = Cell.Layer<TTile>(), my = hy > 0 ? m : 0;
        int bottom = _from - hy; // the tile layer of texture layer 0
        for (int oy = -1; oy <= 1; oy++)
        for (int oz = -1; oz <= 1; oz++)
        for (int ox = -1; ox <= 1; ox++)
        {
            if ((ox | oy | oz) == 0 || oy != 0 && hy == 0 || m == 0 && (ox | oz) != 0) continue;
            // The region's tile: present and read as it is (a real region, computed by the step), or remapped.
            int fx = Axis(_present, ox, TileOffsets<TTile>.Slot(-1, 0, 0), TileOffsets<TTile>.Slot(1, 0, 0));
            int fz = Axis(_present, oz, TileOffsets<TTile>.Slot(0, 0, -1), TileOffsets<TTile>.Slot(0, 0, 1));
            int fy = Axis(_present, oy, TileOffsets<TTile>.Slot(0, -1, 0), TileOffsets<TTile>.Slot(0, 1, 0));
            bool remapped = fx != ox || fy != oy || fz != oz;
            int final = TileOffsets<TTile>.Slot(fx, fy, fz);
            bool real = !remapped && (_present >> final & 1) != 0;
            if (real) continue;
            bool fill = TTile.Boundary == TileBoundary.Fill || (_present >> final & 1) == 0;

            // The region's texels this step computed: up to m past the tile along the axes it is outside.
            int x0 = ox < 0 ? -m : ox == 0 ? 0 : width, x1 = ox < 0 ? 0 : ox == 0 ? width : width + m;
            int z0 = oz < 0 ? -m : oz == 0 ? 0 : depth, z1 = oz < 0 ? 0 : oz == 0 ? depth : depth + m;
            int t0 = hy - my, t1 = layers - 1 - hy + my;
            for (int t = t0; t <= t1; t++)
            {
                int y = bottom + t;
                if ((y < 0 ? -1 : y >= height ? 1 : 0) != oy) continue;
                ulong active = 0;
                for (int z = z0; z < z1; z++)
                {
                    bool any = false;
                    for (int x = x0; x < x1; x++)
                    {
                        TCell cell = fill ? _fill : Unsafe.Add(ref texels,
                            (Map(y, height, fy != oy) - bottom) * layer + (Map(z, depth, fz != oz) + r) * row + Map(x, width, fx != ox) + r);
                        Unsafe.Add(ref texels, t * layer + (z + r) * row + x + r) = cell;
                        any |= !Cell.IsZero(cell);
                    }
                    if (any) active |= 1UL << (z + r);
                }

                if (active == 0) continue;
                ref var mask = ref Unsafe.Add(ref masks, t);
                ulong all = mask.Texture<TTile>() | active, interior = (depth == 64 ? ulong.MaxValue : (1UL << depth) - 1) << r;
                mask = TileMask.Of<TTile>((all & interior) >> r, all & ~interior);
            }
        }

        // An axis's offset as the halo reads it: the tile across, or 0 (the tile's own) where none is and the boundary remaps.
        static int Axis(uint present, int o, int before, int after)
        {
            if (o == 0 || TTile.Boundary == TileBoundary.Fill) return o;
            return (present >> (o < 0 ? before : after) & 1) != 0 ? o : 0;
        }

        // A coordinate, clamped or mirrored into the tile when its axis is remapped.
        static int Map(int c, int n, bool remap) =>
            !remap || (uint)c < (uint)n ? c
            : TTile.Boundary == TileBoundary.Clamp ? (c < 0 ? 0 : n - 1)
            : c < 0 ? -1 - c : 2 * n - 1 - c;
    }

    /// <summary>The texture rows (bit z + r) of layer <paramref name="t"/> a step must compute: those whose reads hold a cell that is not zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Need<TStep, TLane>(ref TileMask masks, int t, int reach) where TStep : struct, ITileStep<TCell, TTile, TLane>
    {
        var reads = TStep.Reads;
        ulong here = Unsafe.Add(ref masks, t).Texture<TTile>(), need = (reads & TileReads.Self) != 0 ? here : 0;
        if ((reads & TileReads.Sides) != 0) need |= Spread(here, reach);
        if (TTile.Height > 1)
            for (int k = 1; k <= reach; k++)
            {
                ulong up = Unsafe.Add(ref masks, t + k).Texture<TTile>(), down = Unsafe.Add(ref masks, t - k).Texture<TTile>();
                if ((reads & TileReads.Above) != 0) need |= up;
                if ((reads & TileReads.Below) != 0) need |= down;
                if ((reads & TileReads.Diagonals) != 0) need |= Spread(up, reach) | Spread(down, reach);
            }
        return need;

        static ulong Spread(ulong rows, int reach)
        {
            ulong spread = 0;
            for (int j = 1; j <= reach; j++) spread |= rows << j | rows >> j;
            return spread;
        }
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

    /// <summary>The rows of texture layer <paramref name="layer"/> the stencil reads something in (its neighbours are in the texture: halo layers).</summary>
    private readonly ulong Visit(int layer)
    {
        int depth = TTile.Depth, hy = TTile.Height > 1 ? TTile.Halo : 0;
        ref var here = ref Unsafe.Add(ref _masks, layer);
        ulong visit = 0;
        if ((_reads & TileReads.Self) != 0) visit |= here.Rows;
        if ((_reads & TileReads.Sides) != 0) visit |= Spread(here);
        for (int k = 1; k <= hy; k++)
        {
            ref var up = ref Unsafe.Add(ref here, k);
            ref var down = ref Unsafe.Add(ref here, -k);
            if ((_reads & TileReads.Above) != 0) visit |= up.Rows;
            if ((_reads & TileReads.Below) != 0) visit |= down.Rows;
            if ((_reads & TileReads.Diagonals) != 0) visit |= Spread(up) | Spread(down);
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
/// A padded tile at the first cell of a row (<see cref="PaddedTile{TCell, TTile}.At"/>): the cells around it, one or
/// a vector at a time. An offset is in cells: dx from −r (the first halo cell before the row) to Width + r − 1 (the last
/// after it), dy and dz from −r to r (dy 0 for a 2D tile), r the shape's <see cref="ITileShape.Halo"/>; a vector of
/// cells must end by the last halo cell after the row. Constant offsets are checked by the JIT, others by a compare.
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
    /// (cells −r to Width + r − 1), in the rows and layers the texture holds: the invariant that lets the loads skip
    /// their bounds check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Offset(int dx, int dy, int dz, int bytes)
    {
        int size = Unsafe.SizeOf<TCell>(), r = TTile.Halo;
        // The last first cell a load of `bytes` may start at, counted from the row's first halo cell: a constant, so
        // each axis is one unsigned compare (a negative dx + r wraps above it). The x check stands alone: dy and dz are
        // constants in a stencil, so the first check folds away, and the loads of one column (x - 1 in three rows)
        // repeat the same compare, which the JIT then drops (one check per column, not per load: 9-point blur ~25% faster).
        int last = Cell.Row<TTile>() * size < bytes ? -1 : (Cell.Row<TTile>() * size - bytes) / size;
        if (last < 0 || (uint)(dz + r) > (uint)(2 * r)
            || (TTile.Height > 1 ? (uint)(dy + r) > (uint)(2 * r) : dy != 0))
            Cell.ThrowOffset();
        if ((uint)(dx + r) > (uint)last) Cell.ThrowOffset();
        return dx + dy * Cell.Layer<TTile>() + dz * Cell.Row<TTile>();
    }
}

/// <summary>A thread's texture and masks for a tile type: allocated on its first load for all the layers, then reused.</summary>
internal static class TileMemory<TCell, TTile> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    [ThreadStatic] private static TCell[] t_texels, t_back;
    [ThreadStatic] private static TileMask[] t_masks, t_backMasks;

    /// <summary>
    /// The cells before a texture and after it in its array: r layers and r rows, so a sample up to r away from any
    /// texel of the texture (a halo texel too, as <see cref="PaddedTile{TCell, TTile}.Steps{TStep, TLane}"/> computes
    /// them) stays inside the array.
    /// </summary>
    public static int Guard => TTile.Halo * (Cell.Layer<TTile>() + Cell.Row<TTile>());

    /// <summary>This thread's texture (created on its first load).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TCell Texels() => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t_texels ?? Create(ref t_texels, ref t_masks)), Guard);

    /// <summary>This thread's masks, a layer each: after <see cref="Texels"/>, which creates them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TileMask Masks() => ref MemoryMarshal.GetArrayDataReference(t_masks);

    /// <summary>This thread's second texture, which steps ping-pong with (created on its first use).</summary>
    public static ref TCell Back() => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t_back ?? Create(ref t_back, ref t_backMasks)), Guard);

    /// <summary>The masks of the second texture: after <see cref="Back"/>.</summary>
    public static ref TileMask BackMasks() => ref MemoryMarshal.GetArrayDataReference(t_backMasks);

    [ThreadStatic] private static TCell[] t_blank;
    private static readonly TCell[] Zeros = new TCell[Cell.Cells<TTile>()];

    /// <summary>
    /// A buffer of the tile's cell count holding <paramref name="fill"/> (<paramref name="zero"/>: it is zero): what a
    /// load reads where there is no neighbour. One array of zeros per tile type; another fill, this thread's array,
    /// filled again when the fill changes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TCell[] Blank(TCell fill, bool zero)
    {
        if (zero) return Zeros;
        var blank = t_blank;
        if (blank != null && Cell.Same(blank[0], fill)) return blank;
        return Refill(fill);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TCell[] Refill(TCell fill)
    {
        var blank = t_blank ??= new TCell[Cell.Cells<TTile>()];
        blank.AsSpan().Fill(fill);
        return blank;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TCell[] Create(ref TCell[] texels, ref TileMask[] masks)
    {
        int layers = Cell.Layers<TTile>();
        masks = new TileMask[layers];
        return texels = GC.AllocateUninitializedArray<TCell>(layers * Cell.Layer<TTile>() + 2 * Guard);
    }
}

/// <summary>
/// One step of a stencil, for <see cref="PaddedTile{TCell, TTile}.Steps{TStep, TLane}"/> (temporal blocking): a struct
/// whose static <see cref="Step"/> computes the next value of 16 bytes of cells from the cells around them. Steps runs
/// it over every texel the step can still compute, so the body is the one a single step would have.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
/// <typeparam name="TLane">The lanes the step computes in: <c>float</c> for float cells, <c>byte</c> for byte ones.</typeparam>
public interface ITileStep<TCell, TTile, TLane> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
{
    /// <summary>
    /// How far the step reads, in cells, rows and layers (1 for a five-, nine-, seven- or 27-point stencil): Steps runs
    /// <see cref="ITileShape.Halo"/> / Reach steps, the halo a whole number of reaches.
    /// </summary>
    static abstract int Reach { get; }

    /// <summary>What the step reads around a cell, for the rows it skips (default <see cref="TileReads.All"/>).</summary>
    static virtual TileReads Reads => TileReads.All;

    /// <summary>
    /// The next value of the 16 bytes of cells from cell <paramref name="x"/> of the cursor's row: read them and their
    /// neighbours with <c>cells.Load128&lt;TLane&gt;(x + dx, dy, dz)</c>, |dx|, |dy|, |dz| ≤ Reach.
    /// </summary>
    static abstract Vector128<TLane> Step(TileCursor<TCell, TTile> cells, int x);
}

/// <summary>The checks of a stepped stencil, once per step type.</summary>
internal static class TileSteps<TCell, TTile, TStep, TLane> where TCell : unmanaged where TTile : struct, IComponent, ITileShape
    where TStep : struct, ITileStep<TCell, TTile, TLane>
{
    private static readonly string Problem = Validate();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check()
    {
        if (Problem != null) Throw();
    }

    private static string Validate()
    {
        int r = TTile.Halo, reach = TStep.Reach, size = Unsafe.SizeOf<TCell>();
        if (reach < 1 || r % reach != 0) return $"the halo of {r} is not a whole number of reaches of {reach}";
        if (r / reach > 1 && TTile.Neighbourhood != TileNeighbourhood.All) return "more than one step reads the tiles across corners: TileNeighbourhood.All";
        if (TTile.Depth + 2 * r > 64) return $"{TTile.Depth} rows and a halo of {r} are more than 64 rows";
        if (Vector128<byte>.Count % size != 0 || TTile.Width * size < Vector128<byte>.Count) return "a row of cells is less than 16 bytes, or a cell does not divide them";
        if (Unsafe.SizeOf<TLane>() > Vector128<byte>.Count) return "the lanes are wider than 16 bytes";
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw() => throw new InvalidOperationException($"{typeof(TStep).Name} on a {typeof(TTile).Name} tile: {Problem}");
}

/// <summary>The checks of a tile shape, once per shape (EECS072 reports them at build time where it can read the shape).</summary>
internal static class Shape<TTile> where TTile : struct, IComponent, ITileShape
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
internal static class Cell
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
        if (count == 1) // a halo of one cell: one move
        {
            target = source;
            return !IsZero(source);
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
    public static void ThrowOffset() => throw new ArgumentOutOfRangeException(null, "Outside the padded tile: a cell, row or layer past the halo");

    [DoesNotReturn, StackTraceHidden] // a call that never returns: see ThrowOffset
    public static void ThrowNoMask() => throw new InvalidOperationException("The tile was loaded without its mask (mask: false): it has no active rows");
}
