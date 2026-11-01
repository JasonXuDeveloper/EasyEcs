using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// One cell of a tile with its neighbors, the parameter of a [ForEach] method that computes one cell: the shader form
/// of a stencil. The method reads the cell (<see cref="Value"/>) and its neighbors at constant offsets
/// (<c>w[-1, 0, 0]</c>, within the shape's <see cref="ITileShape.Halo"/>) and assigns its <c>out</c> parameter, the
/// cell of the entity's <c>DynamicBuffer</c> of that type at the same place:
/// <code>
/// [ForEach(Parallel = true)]
/// static void Blur(Cell&lt;Warmth, Plate&gt; w, out NextWarmth next) =&gt;
///     next = new((w[-1, 0, 0].T + w.Value.T + w[1, 0, 0].T) * (1f / 3));
/// </code>
/// The generator writes the pass: the halo exchanged per tile, the rows its offsets read walked (the quiet ones get
/// what the method computes from zeros), and each row as a vector loop over its cells, the neighbors unaligned loads
/// of the padded texture; a cell the vector loop cannot take runs the method as written. Treat it as a tile parameter
/// (<see cref="Tile{TCell, TTile}"/>): the rows need the tile component and the cells' buffer, the neighbors come
/// from the grid's index.
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
public readonly ref struct Cell<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    private readonly ref readonly TCell _at;

    /// <summary>The cell at <paramref name="at"/> in a tile's padded texture, at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>). Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Cell(ref readonly TCell at, int x, int y, int z)
    {
        _at = ref at;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The cell itself (<c>this[0, 0, 0]</c>). O(1).</summary>
    public TCell Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _at;
    }

    /// <summary>
    /// The cell at (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>) from this one, each within the
    /// shape's halo (dy 0 for a tile of one layer): a neighbor in the tile, or in the halo the neighbors (or the
    /// boundary) gave. Constants, so the generator reads them as loads at fixed offsets. O(1); throws past the halo.
    /// </summary>
    public TCell this[int dx, int dy, int dz]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Check(dx, dy, dz);
            return Unsafe.Add(ref Unsafe.AsRef(in _at), Offset(dx, dy, dz));
        }
    }

    /// <summary>The cell's column in its row, 0 to Width − 1.</summary>
    public int X { get; }

    /// <summary>The cell's layer, 0 to Height − 1.</summary>
    public int Y { get; }

    /// <summary>The cell's row in its layer, 0 to Depth − 1.</summary>
    public int Z { get; }

    /// <summary>How far cell (dx, dy, dz) lies from a cell in the padded texture, in cells: a constant for constant offsets. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Offset(int dx, int dy, int dz) => (nint)dx + (dy * TileLayout.Layer<TTile>() + dz * TileLayout.Row<TTile>());

    /// <summary>Throws unless (dx, dy, dz) lies within the shape's halo: a constant check for constant offsets. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(int dx, int dy, int dz)
    {
        int r = TTile.Halo;
        if ((uint)(dx + r) > (uint)(2 * r) || (uint)(dz + r) > (uint)(2 * r) || (TTile.Height > 1 ? (uint)(dy + r) > (uint)(2 * r) : dy != 0))
            TileLayout.ThrowOffset();
    }

    /// <summary>The cells of a row: the shape's Width. Generated code reads this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int Width
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TTile.Width;
    }

    /// <summary>The rows of a layer: the shape's Depth. Generated code reads this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int Depth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TTile.Depth;
    }

    /// <summary>The layers: the shape's Height. Generated code reads this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int Height
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TTile.Height;
    }

    /// <summary>The cells of a tile: Width × Depth × Height. Generated code reads this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TileLayout.Cells<TTile>();
    }

    /// <summary>
    /// A cell in a texture of zeros, its neighbors within the halo zero too, at (0, 0, 0): what the method computes there
    /// is what a row the pass skips (every cell its offsets read zero) gets. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Cell<TCell, TTile> Blank
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(in CellBlank<TCell, TTile>.Center, 0, 0, 0);
    }
}

/// <summary>A padded texture of zeros per cell and tile type, for <see cref="Cell{TCell, TTile}.Blank"/>.</summary>
internal static class CellBlank<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    private static readonly TCell[] Texels = new TCell[TileLayout.Layers<TTile>() * TileLayout.Layer<TTile>()];

    /// <summary>Cell (0, 0, 0) of the texture: a halo of zeros on every side.</summary>
    public static ref readonly TCell Center
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int r = TTile.Halo, hy = TTile.Height > 1 ? r : 0;
            return ref Texels[hy * TileLayout.Layer<TTile>() + r * TileLayout.Row<TTile>() + r];
        }
    }
}
