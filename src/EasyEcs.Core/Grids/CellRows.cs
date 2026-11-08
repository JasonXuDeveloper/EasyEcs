using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// The halo of a 2D tile read in place by a cell pass (<see cref="Tile{TCell, TTile}.Plane"/>): the tile's buffer and its
/// eight neighbors', each row of the padded plane found where it lives (<see cref="Row"/>), and the vectors that cross a
/// row's ends shifted in registers from the neighbor's edge (<see cref="CellRow"/>), so nothing is copied into the
/// texture. Generated code uses this. Valid for the pass it is made in (a ref struct).
/// </summary>
/// <typeparam name="TCell">The cell.</typeparam>
/// <typeparam name="TTile">The tile's component and shape.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly ref struct TilePlane<TCell, TTile> where TCell : unmanaged where TTile : struct, ITileShape
{
    // The first cell of each buffer, by where it lies from the tile (the blank, of zeros, where there is none).
    private readonly ref TCell _self, _north, _south, _west, _east, _northWest, _northEast, _southWest, _southEast;
    private readonly ref TileMask _masks;

    internal TilePlane(TCell[] self, TCell[] north, TCell[] south, TCell[] west, TCell[] east, TCell[] northWest, TCell[] northEast,
        TCell[] southWest, TCell[] southEast, ref TileMask masks)
    {
        _self = ref MemoryMarshal.GetArrayDataReference(self);
        _north = ref MemoryMarshal.GetArrayDataReference(north);
        _south = ref MemoryMarshal.GetArrayDataReference(south);
        _west = ref MemoryMarshal.GetArrayDataReference(west);
        _east = ref MemoryMarshal.GetArrayDataReference(east);
        _northWest = ref MemoryMarshal.GetArrayDataReference(northWest);
        _northEast = ref MemoryMarshal.GetArrayDataReference(northEast);
        _southWest = ref MemoryMarshal.GetArrayDataReference(southWest);
        _southEast = ref MemoryMarshal.GetArrayDataReference(southEast);
        _masks = ref masks;
    }

    /// <summary>
    /// Whether a pass may read <typeparamref name="TTile"/>'s halo in place with lanes of <typeparamref name="TLane"/> (a
    /// JIT constant): a 2D tile with a border of zeros, cells of the lane's size, rows of whole vectors at every width
    /// that runs (16 bytes, and 32 or 64 where the row is that long), a halo no wider than 16 bytes, and 128-bit vectors.
    /// </summary>
    public static bool Fits<TLane>() where TLane : unmanaged
    {
        int bytes = TTile.Width * Unsafe.SizeOf<TCell>();
        return Vector128.IsHardwareAccelerated && TTile.Height == 1 && TTile.Boundary == TileBoundary.Border
            && TTile.Halo <= TTile.Width && TTile.Halo <= TTile.Depth && Unsafe.SizeOf<TCell>() == Unsafe.SizeOf<TLane>()
            && TTile.Halo * Unsafe.SizeOf<TCell>() <= 16 && bytes % 16 == 0 && (bytes < 32 || bytes % 32 == 0) && (bytes < 64 || bytes % 64 == 0);
    }

    /// <summary>
    /// The first cell of row <paramref name="z"/> (−Halo to Depth + Halo − 1) of the padded plane's column: a row of the
    /// tile, or of the tile north or south of it. O(1); unchecked, the pass reads only its offsets' rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TCell Row(int z)
    {
        int depth = TTile.Depth, width = TTile.Width;
        // A row of the tile first, in one unsigned compare (below 0 wraps above Depth): the rows a stencil reads are
        // mostly its own, the bands only within the halo of an edge.
        if ((uint)z < (uint)depth) return ref Unsafe.Add(ref _self, z * width);
        return ref z < 0 ? ref Unsafe.Add(ref _north, (depth + z) * width) : ref Unsafe.Add(ref _south, (z - depth) * width);
    }

    /// <summary>
    /// Row <paramref name="z"/> of the tile (0 to Depth − 1, unchecked: the row a pass walks), or of the west or east
    /// neighbor beside it (<paramref name="side"/> −1 or 1), whose edge vectors a shift along x reads. O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TCell Own(int z, [ConstantExpected] int side)
    {
        int width = TTile.Width;
        if (side < 0) return ref Unsafe.Add(ref _west, z * width);
        return ref Unsafe.Add(ref side > 0 ? ref _east : ref _self, z * width);
    }

    /// <summary>The rows a stencil reading <paramref name="reads"/> shades, from the masks <see cref="Mask"/> recorded: <see cref="Tile{TCell, TTile}.Rows"/>.</summary>
    public TileRows<TTile> Rows(TileStencil reads) => new(ref _masks, 0, 0, reads);

    /// <summary>
    /// The row masks <see cref="Tile{TCell, TTile}.ExchangeHalo(int, int, TCell, bool)"/> records for a 2D tile with a
    /// border of zeros, from the cells in place: bit z for a row that holds a cell that is not zero with the halo cells
    /// beside it, and the halo rows north and south with their corners.
    /// </summary>
    internal void Mask()
    {
        int width = TTile.Width, depth = TTile.Depth, r = TTile.Halo;
        ulong rows = 0;
        for (int z = 0; z < depth; z++)
        {
            int at = z * width;
            if (TileLayout.Any(ref Unsafe.Add(ref _self, at), width) | TileLayout.Any(ref Unsafe.Add(ref _west, at + width - r), r)
                | TileLayout.Any(ref Unsafe.Add(ref _east, at), r))
                rows |= 1UL << z;
        }

        ulong bands = 0;
        for (int k = 1; k <= r; k++)
        {
            int at = (depth - k) * width;
            if (TileLayout.Any(ref Unsafe.Add(ref _north, at), width) | TileLayout.Any(ref Unsafe.Add(ref _northWest, at + width - r), r)
                | TileLayout.Any(ref Unsafe.Add(ref _northEast, at), r))
                bands |= depth + 2 * r <= 64 ? 1UL << (r - k) : 1;
            at = (k - 1) * width;
            if (TileLayout.Any(ref Unsafe.Add(ref _south, at), width) | TileLayout.Any(ref Unsafe.Add(ref _southWest, at + width - r), r)
                | TileLayout.Any(ref Unsafe.Add(ref _southEast, at), r))
                bands |= depth + 2 * r <= 64 ? 1UL << (depth - 1 + k + r) : 2;
        }
        _masks = TileMask.Of<TTile>(rows, bands);
    }
}

/// <summary>
/// The loads of a cell pass reading its halo in place (<see cref="TilePlane{TCell, TTile}"/>): the vector of a row's
/// cells from <c>i + dx</c>, one load where it lies inside the row, else the vector at <c>i</c> shifted against the one
/// before it (the west row's last at the row's start) or after it (the east row's first at its end) in registers:
/// PALIGNR, VPERM2I128 + VPALIGNR, VALIGND, NEON EXT. A shift through memory (two stores and a load across them)
/// waits for the stores to reach the cache, which costs more than the copy it saves (TileInPlaceBenchmark). Generated
/// code calls these; <c>dx</c> is a constant, <c>i</c> a multiple of the vector's lanes, the row's
/// <c>width</c> lanes a multiple of the vector's (<see cref="TilePlane{TCell, TTile}.Fits{TLane}"/>), |dx| at most the lanes.
/// </summary>
// The shifts' immediates come from dx, a constant at every call the generator writes: constants once these inline (the
// JIT folds them before it imports the intrinsics), which CA1857 cannot see through the parameters.
#pragma warning disable CA1857
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CellRow
{
    /// <summary>The 128 bits of a row's lanes from <paramref name="i"/> + <paramref name="dx"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Load128<T>(ref T row, ref T west, ref T east, nint i, [ConstantExpected] int dx, int width) where T : unmanaged
    {
        int lanes = Vector128<T>.Count;
        if (dx == 0) return Vector128.LoadUnsafe(ref row, (nuint)i);
        // One compare: i is a multiple of the lanes and |dx| at most the lanes, so a load before the row's start is
        // only the first vector's, one past its end only the last's.
        if (dx < 0)
        {
            if (i > 0) return Vector128.LoadUnsafe(ref row, (nuint)(i + dx));
            return Shift128(Vector128.LoadUnsafe(ref west, (nuint)(width - lanes)), Vector128.LoadUnsafe(ref row), (lanes + dx) * Unsafe.SizeOf<T>());
        }
        if (i + lanes < width) return Vector128.LoadUnsafe(ref row, (nuint)(i + dx));
        return Shift128(Vector128.LoadUnsafe(ref row, (nuint)i), Vector128.LoadUnsafe(ref east), dx * Unsafe.SizeOf<T>());
    }

    /// <summary>The 256 bits of a row's lanes from <paramref name="i"/> + <paramref name="dx"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Load256<T>(ref T row, ref T west, ref T east, nint i, [ConstantExpected] int dx, int width) where T : unmanaged
    {
        int lanes = Vector256<T>.Count;
        if (dx == 0) return Vector256.LoadUnsafe(ref row, (nuint)i);
        // One compare: i is a multiple of the lanes and |dx| at most the lanes, so a load before the row's start is
        // only the first vector's, one past its end only the last's.
        if (dx < 0)
        {
            if (i > 0) return Vector256.LoadUnsafe(ref row, (nuint)(i + dx));
            return Shift256(Vector256.LoadUnsafe(ref west, (nuint)(width - lanes)), Vector256.LoadUnsafe(ref row), (lanes + dx) * Unsafe.SizeOf<T>());
        }
        if (i + lanes < width) return Vector256.LoadUnsafe(ref row, (nuint)(i + dx));
        return Shift256(Vector256.LoadUnsafe(ref row, (nuint)i), Vector256.LoadUnsafe(ref east), dx * Unsafe.SizeOf<T>());
    }

    /// <summary>The 512 bits of a row's lanes from <paramref name="i"/> + <paramref name="dx"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<T> Load512<T>(ref T row, ref T west, ref T east, nint i, [ConstantExpected] int dx, int width) where T : unmanaged
    {
        int lanes = Vector512<T>.Count;
        if (dx == 0) return Vector512.LoadUnsafe(ref row, (nuint)i);
        // One compare: i is a multiple of the lanes and |dx| at most the lanes, so a load before the row's start is
        // only the first vector's, one past its end only the last's.
        if (dx < 0)
        {
            if (i > 0) return Vector512.LoadUnsafe(ref row, (nuint)(i + dx));
            return Shift512(Vector512.LoadUnsafe(ref west, (nuint)(width - lanes)), Vector512.LoadUnsafe(ref row), (lanes + dx) * Unsafe.SizeOf<T>());
        }
        if (i + lanes < width) return Vector512.LoadUnsafe(ref row, (nuint)(i + dx));
        return Shift512(Vector512.LoadUnsafe(ref row, (nuint)i), Vector512.LoadUnsafe(ref east), dx * Unsafe.SizeOf<T>());
    }

    /// <summary>The <see cref="Vector{T}"/> of a row's lanes from <paramref name="i"/> + <paramref name="dx"/>: one of the three by its size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> LoadVector<T>(ref T row, ref T west, ref T east, nint i, [ConstantExpected] int dx, int width) where T : unmanaged
    {
        if (Vector<byte>.Count == 64) return Load512(ref row, ref west, ref east, i, dx, width).AsVector();
        if (Vector<byte>.Count == 32) return Load256(ref row, ref west, ref east, i, dx, width).AsVector();
        return Load128(ref row, ref west, ref east, i, dx, width).AsVector();
    }

    /// <summary>Bytes <paramref name="s"/> to <paramref name="s"/> + 15 of <paramref name="low"/> followed by <paramref name="high"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> Shift128<T>(Vector128<T> low, Vector128<T> high, [ConstantExpected(Min = 0, Max = 16)] int s) where T : unmanaged
    {
        if (s == 0) return low;
        if (s == 16) return high;
        if (Ssse3.IsSupported) return Ssse3.AlignRight(high.AsByte(), low.AsByte(), (byte)s).As<byte, T>();
        if (AdvSimd.IsSupported) return AdvSimd.ExtractVector128(low.AsByte(), high.AsByte(), (byte)s).As<byte, T>();
        return Through(low, high, s);
    }

    /// <summary>Bytes <paramref name="s"/> to <paramref name="s"/> + 31 of <paramref name="low"/> followed by <paramref name="high"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Shift256<T>(Vector256<T> low, Vector256<T> high, [ConstantExpected(Min = 0, Max = 32)] int s) where T : unmanaged
    {
        if (s == 0) return low;
        if (s == 32) return high;
        if (Avx2.IsSupported)
        {
            // AVX2 shifts within each 128-bit half: the halves that straddle (low's high half, high's low half) first.
            var middle = Avx2.Permute2x128(low.AsByte(), high.AsByte(), (byte)0x21);
            if (s == 16) return middle.As<byte, T>();
            return s < 16
                ? Avx2.AlignRight(middle, low.AsByte(), (byte)s).As<byte, T>()
                : Avx2.AlignRight(high.AsByte(), middle, (byte)(s - 16)).As<byte, T>();
        }
        return Through(low, high, s);
    }

    /// <summary>Bytes <paramref name="s"/> to <paramref name="s"/> + 63 of <paramref name="low"/> followed by <paramref name="high"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> Shift512<T>(Vector512<T> low, Vector512<T> high, [ConstantExpected(Min = 0, Max = 64)] int s) where T : unmanaged
    {
        if (s == 0) return low;
        if (s == 64) return high;
        if (Avx512F.IsSupported && s % 4 == 0) return Avx512F.AlignRight32(high.AsUInt32(), low.AsUInt32(), (byte)(s / 4)).As<uint, T>();
        if (Avx512BW.IsSupported)
        {
            // Whole 16-byte steps across the register (VALIGND), the rest within each 128-bit lane (VPALIGNR).
            int q = s / 16, rest = s % 16;
            var from = q == 0 ? low.AsUInt32() : Avx512F.AlignRight32(high.AsUInt32(), low.AsUInt32(), (byte)(4 * q));
            var next = q == 3 ? high.AsUInt32() : Avx512F.AlignRight32(high.AsUInt32(), low.AsUInt32(), (byte)(4 * q + 4));
            return Avx512BW.AlignRight(next.AsByte(), from.AsByte(), (byte)rest).As<byte, T>();
        }
        return Through(low, high, s);
    }

    /// <summary>The shift through memory, where no instruction does it: correct, and slower (a load across two stores).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TVector Through<TVector>(TVector low, TVector high, int s) where TVector : unmanaged
    {
        var pair = new Pair<TVector>(low, high);
        return Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref Unsafe.As<Pair<TVector>, byte>(ref pair), s));
    }

    private struct Pair<TVector>(TVector low, TVector high) where TVector : unmanaged
    {
        public TVector Low = low;
        public TVector High = high;
    }
}
#pragma warning restore CA1857
