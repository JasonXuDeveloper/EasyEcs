using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace EasyEcs;

// The word picks of a generated vector loop over a wide component on ARM64 (Kernel.Wide.cs): a component of up to 16
// four-byte words per row whose method uses a few of them. The loop loads the rows of a Vector<T> (4 rows at 128 bits)
// as whole vectors, picks each used word of the 4 rows into one vector with NEON TBL over the (at most 4) vectors that
// hold them, and writes the vectors holding written words back with TBX (the words it does not write kept as loaded).
public static partial class Interleaved
{
    /// <summary>
    /// Whether <see cref="Pick{T}(Vector{T}, Vector128{byte})"/> and <see cref="PickInto{T}(Vector{T}, Vector{T}, Vector128{byte})"/>
    /// run as table lookups on <c>Vector&lt;T&gt;</c> (NEON TBL / TBX, ARM64, where it is 128 bits); otherwise a generated
    /// loop leaves a wide component to the AVX-512 permutes or to its rows one at a time.
    /// </summary>
    public static bool IsWideAccelerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector<byte>.Count == 16 && AdvSimd.Arm64.IsSupported;
    }

    /// <summary>
    /// The bytes of <paramref name="a"/> that <paramref name="indexes"/> names, byte j of the result from byte
    /// <c>indexes[j]</c> (0 past the table): NEON TBL. Only behind <see cref="IsWideAccelerated"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Pick<T>(Vector<T> a, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookup(Bytes(a), indexes).As<byte, T>().AsVector();
        return Software(default, indexes, false, a, a, a, a, 1);
    }

    /// <summary><see cref="Pick{T}(Vector{T}, Vector128{byte})"/> over the 32 bytes of two vectors (<paramref name="b"/>'s from 16).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Pick<T>(Vector<T> a, Vector<T> b, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookup((Bytes(a), Bytes(b)), indexes).As<byte, T>().AsVector();
        return Software(default, indexes, false, a, b, a, a, 2);
    }

    /// <summary><see cref="Pick{T}(Vector{T}, Vector128{byte})"/> over the 48 bytes of three vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Pick<T>(Vector<T> a, Vector<T> b, Vector<T> c, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookup((Bytes(a), Bytes(b), Bytes(c)), indexes).As<byte, T>().AsVector();
        return Software(default, indexes, false, a, b, c, a, 3);
    }

    /// <summary><see cref="Pick{T}(Vector{T}, Vector128{byte})"/> over the 64 bytes of four vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Pick<T>(Vector<T> a, Vector<T> b, Vector<T> c, Vector<T> d, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookup((Bytes(a), Bytes(b), Bytes(c), Bytes(d)), indexes).As<byte, T>().AsVector();
        return Software(default, indexes, false, a, b, c, d, 4);
    }

    /// <summary>
    /// <paramref name="into"/> with the bytes <paramref name="indexes"/> names in <paramref name="a"/> in place of its own,
    /// byte j kept where <c>indexes[j]</c> is past the table: NEON TBX. Only behind <see cref="IsWideAccelerated"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> PickInto<T>(Vector<T> into, Vector<T> a, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookupExtension(Bytes(into), Bytes(a), indexes).As<byte, T>().AsVector();
        return Software(into, indexes, true, a, a, a, a, 1);
    }

    /// <summary><see cref="PickInto{T}(Vector{T}, Vector{T}, Vector128{byte})"/> over two vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> PickInto<T>(Vector<T> into, Vector<T> a, Vector<T> b, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookupExtension(Bytes(into), (Bytes(a), Bytes(b)), indexes).As<byte, T>().AsVector();
        return Software(into, indexes, true, a, b, a, a, 2);
    }

    /// <summary><see cref="PickInto{T}(Vector{T}, Vector{T}, Vector128{byte})"/> over three vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> PickInto<T>(Vector<T> into, Vector<T> a, Vector<T> b, Vector<T> c, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookupExtension(Bytes(into), (Bytes(a), Bytes(b), Bytes(c)), indexes).As<byte, T>().AsVector();
        return Software(into, indexes, true, a, b, c, a, 3);
    }

    /// <summary><see cref="PickInto{T}(Vector{T}, Vector{T}, Vector128{byte})"/> over four vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> PickInto<T>(Vector<T> into, Vector<T> a, Vector<T> b, Vector<T> c, Vector<T> d, Vector128<byte> indexes) where T : struct
    {
        if (IsWideAccelerated) return AdvSimd.Arm64.VectorTableLookupExtension(Bytes(into), (Bytes(a), Bytes(b), Bytes(c), Bytes(d)), indexes).As<byte, T>().AsVector();
        return Software(into, indexes, true, a, b, c, d, 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Bytes<T>(Vector<T> v) where T : struct => v.AsVector128().AsByte();

    /// <summary>
    /// TBL / TBX in software over the first 16 bytes of each vector (the generated loops never get here: they run the
    /// picks only where <see cref="IsWideAccelerated"/> holds); the bytes past 16 of the result are 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector<T> Software<T>(Vector<T> into, Vector128<byte> indexes, bool keep, Vector<T> a, Vector<T> b, Vector<T> c, Vector<T> d, int count) where T : struct
    {
        Span<byte> table = stackalloc byte[64];
        Bytes(a).CopyTo(table);
        Bytes(b).CopyTo(table.Slice(16));
        Bytes(c).CopyTo(table.Slice(32));
        Bytes(d).CopyTo(table.Slice(48));
        Span<byte> result = stackalloc byte[Vector<byte>.Count];
        result.Clear();
        for (int j = 0; j < 16; j++)
        {
            int at = indexes.GetElement(j);
            result[j] = at < 16 * count ? table[at] : keep ? Bytes(into).GetElement(j) : (byte)0;
        }
        return new Vector<T>(MemoryMarshal.Cast<byte, T>(result));
    }
}
