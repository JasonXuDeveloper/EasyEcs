using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// A table of up to 16 values read at a lane index in generated vector loops: a uniform <c>[InlineArray]</c> (a
/// singleton's, a value's) indexed by a value of the row, <c>waypoints.Points[goal.Waypoint]</c>. Each field of the
/// elements is one column, loaded once per run into the vectors it fills (<see cref="Load{TVector}"/>); a vector of
/// indexes picks its lanes from them with one permute per vector of the column (<see cref="Pick{TVector}"/>: AVX-512
/// VPERMD over 16, AVX2 VPERMD over 8, a byte shuffle over 4: PSHUFB, NEON TBL). The generator calls them with the vector
/// type of 4-byte int lanes it loops over as <c>TVector</c>, and sends the rows whose index is outside the table to the
/// method (which throws there); a [ForEach] method never calls them.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TableLanes
{
    /// <summary>The most elements a table read at a lane index holds: one 512-bit vector, two 256-bit, four 128-bit.</summary>
    public const int MaxLength = 16;

    /// <summary>A column of a table: its values in the vectors of 4-byte lanes they fill, the rest zero.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Column<TVector> where TVector : struct
    {
        /// <summary>The vectors of the column, lowest elements first; those past its length are zero.</summary>
        public TVector C0, C1, C2, C3;
    }

    [InlineArray(MaxLength)]
    private struct Sixteen
    {
        private int _first;
    }

    /// <summary>
    /// The column of <paramref name="length"/> values (4-byte bits) at <paramref name="offset"/>, <paramref name="offset"/>
    /// + <paramref name="stride"/>, ... of <paramref name="table"/>: one field of each element of a table of structs.
    /// </summary>
    public static Column<TVector> Load<TVector>(ReadOnlySpan<int> table, int offset, int stride, int length) where TVector : struct
    {
        Sixteen values = default;
        for (int k = 0; k < length; k++) values[k] = table[offset + k * stride];
        var column = default(Column<TVector>);
        int lanes = Unsafe.SizeOf<TVector>() / sizeof(int);
        ref byte source = ref Unsafe.As<Sixteen, byte>(ref values);
        for (int k = 0; k * lanes < length; k++)
            Unsafe.Add(ref column.C0, k) = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, k * lanes * sizeof(int)));
        return column;
    }

    /// <summary>
    /// The values of <paramref name="column"/> at each lane's <paramref name="index"/> (in [0, <paramref name="length"/>):
    /// the caller sends the other rows to the method). <paramref name="length"/> is the generator's constant, so the
    /// permutes of vectors the table does not reach fold away.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Pick<TVector>(in Column<TVector> column, TVector index, int length) where TVector : struct
    {
        if (typeof(TVector) == typeof(Vector512<int>) || typeof(TVector) == typeof(Vector<int>) && Vector<int>.Count == 16)
            return Unsafe.BitCast<Vector512<int>, TVector>(Pick512(Unsafe.BitCast<TVector, Vector512<int>>(column.C0), Unsafe.BitCast<TVector, Vector512<int>>(index)));
        if (typeof(TVector) == typeof(Vector256<int>) || typeof(TVector) == typeof(Vector<int>) && Vector<int>.Count == 8)
            return Unsafe.BitCast<Vector256<int>, TVector>(Pick256(ref Unsafe.As<Column<TVector>, Column<Vector256<int>>>(ref Unsafe.AsRef(in column)), Unsafe.BitCast<TVector, Vector256<int>>(index), length));
        if (typeof(TVector) == typeof(Vector128<int>) || typeof(TVector) == typeof(Vector<int>) && Vector<int>.Count == 4)
            return Unsafe.BitCast<Vector128<int>, TVector>(Pick128(ref Unsafe.As<Column<TVector>, Column<Vector128<int>>>(ref Unsafe.AsRef(in column)), Unsafe.BitCast<TVector, Vector128<int>>(index), length));
        throw new NotSupportedException();
    }

    /// <summary>16 values in one vector: VPERMD reads the index's low 4 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<int> Pick512(Vector512<int> column, Vector512<int> index) =>
        Avx512F.IsSupported ? Avx512F.PermuteVar16x32(column, index) : Vector512.Shuffle(column, index & Vector512.Create(15));

    /// <summary>
    /// Up to 16 values in two vectors: VPERMD reads the index's low 3 bits, so each vector's pick is kept where the index's
    /// fourth bit names that vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Pick256(ref Column<Vector256<int>> column, Vector256<int> index, int length)
    {
        if (!Avx2.IsSupported)
        {
            // Shuffle gives 0 for an index outside its vector, so each vector's pick holds only its own elements
            var any = Vector256.Shuffle(column.C0, index);
            return length > 8 ? any | Vector256.Shuffle(column.C1, index - Vector256.Create(8)) : any;
        }
        var low = Avx2.PermuteVar8x32(column.C0, index);
        if (length <= 8) return low;
        var high = Avx2.PermuteVar8x32(column.C1, index);
        return Vector256.ConditionalSelect(Vector256.GreaterThan(index, Vector256.Create(7)), high, low);
    }

    /// <summary>Up to 16 values in four vectors of 4: a shuffle per vector the table reaches, each 0 outside its own elements.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Pick128(ref Column<Vector128<int>> column, Vector128<int> index, int length)
    {
        var picked = Vector128.Shuffle(column.C0, index);
        if (length > 4) picked |= Vector128.Shuffle(column.C1, index - Vector128.Create(4));
        if (length > 8) picked |= Vector128.Shuffle(column.C2, index - Vector128.Create(8));
        if (length > 12) picked |= Vector128.Shuffle(column.C3, index - Vector128.Create(12));
        return picked;
    }
}
