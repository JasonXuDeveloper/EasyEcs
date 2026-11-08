using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

// The word picks of a generated vector loop over a wide component (Kernel.Wide.cs): a component of up to 64 bytes per
// row whose method uses a few of its 4-byte (or 8-byte) words. On ARM64 the loop loads the rows of a Vector<T> (4 rows
// of 4-byte words at 128 bits) as whole vectors, picks each used word of the rows into one vector with NEON TBL over the
// (at most 4) vectors that hold them, and writes the vectors holding written words back with TBX (the words it does not
// write kept as loaded). On AVX2 (256-bit Vector<T>) it gathers them with a tree of two-source permutes, each two
// VPERMD and a VPBLENDD (Permute256, Merge256).
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

    /// <summary>
    /// Whether <see cref="Permute256{T}(Vector{T}, Vector256{int})"/> and its two-source forms run as AVX2 permutes on
    /// <c>Vector&lt;T&gt;</c> (x64 where it is 256 bits); otherwise a generated loop leaves a wide component to its other
    /// forms or to its rows one at a time.
    /// </summary>
    public static bool IsWide256
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector<byte>.Count == 32 && Avx2.IsSupported;
    }

    /// <summary>
    /// The 32-bit words of <paramref name="a"/> that <paramref name="indexes"/> names (word j of the result from word
    /// <c>indexes[j] &amp; 7</c>): VPERMD. Only behind <see cref="IsWide256"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Permute256<T>(Vector<T> a, Vector256<int> indexes) where T : struct
    {
        if (IsWide256) return Avx2.PermuteVar8x32(a.AsVector256().AsInt32(), indexes).As<int, T>().AsVector();
        return Software256(a, a, indexes, 0, false);
    }

    /// <summary>
    /// Word j from <paramref name="b"/> where bit j of <paramref name="blend"/> is set, else from <paramref name="a"/>,
    /// each at its <paramref name="indexes"/>: two VPERMD and a VPBLENDD (the two-source permute AVX2 lacks).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Permute256<T>(Vector<T> a, Vector<T> b, Vector256<int> indexes, [ConstantExpected] byte blend) where T : struct
    {
        if (IsWide256)
            return Avx2.Blend(Avx2.PermuteVar8x32(a.AsVector256().AsInt32(), indexes), Avx2.PermuteVar8x32(b.AsVector256().AsInt32(), indexes), blend)
                .As<int, T>().AsVector();
        return Software256(a, b, indexes, blend, false);
    }

    /// <summary>
    /// <paramref name="into"/> with word j taken from <paramref name="b"/> at its <paramref name="indexes"/> where bit j
    /// of <paramref name="blend"/> is set, its own words kept elsewhere: one VPERMD and a VPBLENDD (a store's write-back).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<T> Merge256<T>(Vector<T> into, Vector<T> b, Vector256<int> indexes, [ConstantExpected] byte blend) where T : struct
    {
        if (IsWide256)
            return Avx2.Blend(into.AsVector256().AsInt32(), Avx2.PermuteVar8x32(b.AsVector256().AsInt32(), indexes), blend).As<int, T>().AsVector();
        return Software256(into, b, indexes, blend, true);
    }

    /// <summary>
    /// <see cref="Permute256{T}(Vector{T}, Vector{T}, Vector256{int}, byte)"/> (and Merge256: <paramref name="keep"/>) in
    /// software over the first 32 bytes of each vector (the generated loops never get here: they run only where
    /// <see cref="IsWide256"/> holds); the bytes past 32 of the result are 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector<T> Software256<T>(Vector<T> a, Vector<T> b, Vector256<int> indexes, byte blend, bool keep) where T : struct
    {
        Span<int> words = stackalloc int[16];
        var bytes = MemoryMarshal.AsBytes(words);
        bytes.Clear();
        var ab = MemoryMarshal.AsBytes(new ReadOnlySpan<Vector<T>>(in a));
        var bb = MemoryMarshal.AsBytes(new ReadOnlySpan<Vector<T>>(in b));
        ab.Slice(0, Math.Min(32, ab.Length)).CopyTo(bytes);
        bb.Slice(0, Math.Min(32, bb.Length)).CopyTo(bytes.Slice(32));
        Span<int> result = stackalloc int[Math.Max(8, Vector<byte>.Count / 4)];
        result.Clear();
        for (int j = 0; j < 8; j++)
        {
            bool second = ((blend >> j) & 1) != 0;
            result[j] = keep && !second ? words[j] : words[(second ? 8 : 0) + (indexes.GetElement(j) & 7)];
        }
        return new Vector<T>(MemoryMarshal.Cast<int, T>(result));
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
