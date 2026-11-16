using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// A layer of a tall tile, named by a type: the type argument of <see cref="Layer{TCell, TTile, TLayer}"/>, so the
/// layer a ground cell reads is a constant the generator and the JIT see.
/// <code>
/// public struct Top : ILayer { public static int Index =&gt; 3; }
/// </code>
/// </summary>
public interface ILayer
{
    /// <summary>The layer: 0 to the tile's Height − 1.</summary>
    static abstract int Index { get; }
}

/// <summary>
/// The cell of layer <typeparamref name="TLayer"/> of a tall tile under a cell of its ground, the parameter of a cell
/// method over a <see cref="Ground{TTile}"/> (<c>Cell&lt;TCell, Ground&lt;TTile&gt;&gt;</c>) that also reads one layer of the
/// tile it is the ground of. It reads like a cell of that layer: the cell itself (<see cref="Value"/>) and its neighbors
/// in the layer at constant offsets (<c>rock[dx, 0, dz]</c>, within the shape's halo), lined up with the ground's cell in
/// x and z:
/// <code>
/// [ForEach(Parallel = true)]
/// static void Diffuse(Cell&lt;Heat, Ground&lt;Chunk&gt;&gt; w, Layer&lt;Rock, Chunk, Top&gt; rock, out Heat next) =&gt;
///     next = new(rock.Value.Solid != 0 ? 0 : w.Value.T + (w[-1, 0, 0].T + w[1, 0, 0].T + w[0, 0, -1].T + w[0, 0, 1].T - 4 * w.Value.T) * 0.2f);
/// </code>
/// The generator exchanges the halo of that layer alone (not every layer of the tile) and reads it as one more stream
/// of the row's vector loop. Treat it as a tile parameter (<see cref="Tile{TCell, TTile}"/> by value): the rows need
/// the entity's <c>DynamicBuffer&lt;TCell&gt;</c>, the neighbors come from the grid's index (EECS115 reports a layer of
/// another tile than the ground's, one past the tile's layers, or one in a method without a ground cell).
/// </summary>
/// <typeparam name="TCell">The cell of the tall tile.</typeparam>
/// <typeparam name="TTile">The tall tile's component and shape: the one the method's ground is of.</typeparam>
/// <typeparam name="TLayer">The layer read.</typeparam>
public readonly ref struct Layer<TCell, TTile, TLayer> where TCell : unmanaged where TTile : struct, ITileShape where TLayer : ILayer
{
    private readonly ref readonly TCell _at;

    /// <summary>The cell at <paramref name="at"/> in the padded texture of the tile's layer. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Layer(ref readonly TCell at) => _at = ref at;

    /// <summary>The cell of the layer under the ground's cell (<c>this[0, 0, 0]</c>). O(1).</summary>
    public TCell Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _at;
    }

    /// <summary>
    /// The cell of the layer at (<paramref name="dx"/>, 0, <paramref name="dz"/>) from this one, each within the shape's
    /// halo (<paramref name="dy"/> 0: one layer is read): a neighbor in the tile, or in the halo the neighbors (or the
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

    /// <summary>How far cell (dx, 0, dz) lies from a cell in the layer's padded texture: a constant for constant offsets. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint Offset(int dx, int dy, int dz) => Cell<TCell, TTile>.Offset(dx, dy, dz);

    /// <summary>
    /// Throws unless (dx, dy, dz) lies within the layer's halo (dy 0) and the layer within the tile (0 ≤ Index &lt; Height):
    /// a constant check for constant offsets. Generated code calls this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Check(int dx, int dy, int dz)
    {
        int r = TTile.Halo;
        if ((uint)(dx + r) > (uint)(2 * r) || (uint)(dz + r) > (uint)(2 * r) || dy != 0 || (uint)TLayer.Index >= (uint)TTile.Height)
            TileLayout.ThrowOffset();
    }

    /// <summary>The layer read: <typeparamref name="TLayer"/>'s Index. Generated code reads this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static int Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => TLayer.Index;
    }

    /// <summary>A cell in a texture of zeros, its neighbors within the halo zero too: what a blank row reads. Generated code calls this.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Layer<TCell, TTile, TLayer> Blank
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(in CellBlank<TCell, TTile>.Center);
    }
}
