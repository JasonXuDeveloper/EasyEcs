using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// The loads and stores of a generated vector loop over components of several fields (<c>Position { X, Y, Z }</c>):
/// a column holds rows as <c>x0 y0 z0 x1 y1 z1 ...</c>, and the loop computes on one vector per field
/// (<c>x0 x1 x2 ...</c>). <c>Load2</c> / <c>Load3</c> / <c>Load4</c> split the rows of one vector into one vector per
/// field with shuffles, <c>Store2</c> / <c>Store3</c> / <c>Store4</c> put them back. The generator calls them; a
/// [ForEach] method never does. Lanes of 4 bytes (float, int, uint) or 8 bytes (double, long, ulong). <c>Lanes32</c> /
/// <c>Lanes64</c> turn the passing rows of a word (IForEachMasked) into the lane mask of one vector.
/// </summary>
/// <remarks>
/// Each call reads or writes exactly the rows of one vector (<c>Vector&lt;T&gt;.Count</c> or
/// <c>Vector512&lt;T&gt;.Count</c> rows from <c>row</c>): the caller guarantees the column holds them.
/// Shuffles only move bits, so a lane of 4 bytes is moved as a float and one of 8 bytes as a double, whatever its type.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Interleaved
{
    /// <summary>
    /// Whether the loads and stores of <c>Vector&lt;T&gt;</c> run as shuffles on this hardware (AVX2 at 256 bits,
    /// AVX-512 at 512); otherwise a generated loop leaves components of several fields to its rows one at a time.
    /// </summary>
    public static bool IsAccelerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector<byte>.Count == 32 ? Avx2.IsSupported : Vector<byte>.Count == 64 && Avx512F.IsSupported;
    }

    /// <summary>Whether the loads and stores of <c>Vector512&lt;T&gt;</c> run as shuffles on this hardware (AVX-512).</summary>
    public static bool IsAccelerated512
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Avx512F.IsSupported;
    }

    /// <summary>
    /// The lane mask of the <c>Vector&lt;int&gt;.Count</c> rows from <paramref name="first"/> of a word whose passing
    /// rows are the bits of <paramref name="rows"/>: all bits set in lane j when row <c>first + j</c> passes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<int> Lanes32(ulong rows, int first)
    {
        var bits = Masks.Bits32;
        return Vector.Equals(new Vector<int>((int)(rows >> first)) & bits, bits);
    }

    /// <summary><see cref="Lanes32"/> for lanes of 8 bytes: <c>Vector&lt;long&gt;.Count</c> rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<long> Lanes64(ulong rows, int first)
    {
        var bits = Masks.Bits64;
        return Vector.Equals(new Vector<long>((long)(rows >> first)) & bits, bits);
    }

    /// <summary><see cref="Lanes32"/> for <c>Vector512&lt;int&gt;</c>: 16 rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<int> Lanes32In512(ulong rows, int first)
    {
        var bits = Masks.Bits32x16;
        return Vector512.Equals(Vector512.Create((int)(rows >> first)) & bits, bits);
    }

    /// <summary><see cref="Lanes64"/> for <c>Vector512&lt;long&gt;</c>: 8 rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<long> Lanes64In512(ulong rows, int first)
    {
        var bits = Masks.Bits64x8;
        return Vector512.Equals(Vector512.Create((long)(rows >> first)) & bits, bits);
    }

    /// <summary>
    /// The last <paramref name="count"/> rows of a run (fewer than a vector's) copied from <paramref name="rows"/> into
    /// this thread's copy of 64 rows of <typeparamref name="T"/>, which a vector loop then reads whole: a loop whose rows
    /// must all take the vector form (it marks rows for a change after the pass) runs its last vector there, the lanes
    /// past <paramref name="count"/> masked off. <see cref="StoreTail{T}"/> writes the rows back. O(count).
    /// </summary>
    public static ref T LoadTail<T>(ref T rows, int count) where T : struct
    {
        var copy = Tail<T>.Rows ??= new T[64];
        MemoryMarshal.CreateReadOnlySpan(ref rows, count).CopyTo(copy);
        return ref MemoryMarshal.GetArrayDataReference(copy);
    }

    /// <summary>Write the <paramref name="count"/> rows of this thread's copy (<see cref="LoadTail{T}"/>) back to <paramref name="rows"/>. O(count).</summary>
    public static void StoreTail<T>(ref T rows, int count) where T : struct =>
        Tail<T>.Rows.AsSpan(0, count).CopyTo(MemoryMarshal.CreateSpan(ref rows, count));

    // One copy per thread and type: a vector loop's columns are of distinct types, and it runs no other loop meanwhile.
    private static class Tail<T>
    {
        [ThreadStatic] public static T[] Rows;
    }

    /// <summary>Lane j holds bit j: the row of each lane in a word's bits.</summary>
    private static class Masks
    {
        public static readonly Vector<int> Bits32 = Make32();
        public static readonly Vector<long> Bits64 = Make64();
        public static readonly Vector512<int> Bits32x16 = Vector512.Create(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
        public static readonly Vector512<long> Bits64x8 = Vector512.Create(1L, 2, 4, 8, 16, 32, 64, 128);

        private static Vector<int> Make32()
        {
            Span<int> bits = stackalloc int[Vector<int>.Count];
            for (int j = 0; j < bits.Length; j++) bits[j] = 1 << j;
            return new Vector<int>(bits);
        }

        private static Vector<long> Make64()
        {
            Span<long> bits = stackalloc long[Vector<long>.Count];
            for (int j = 0; j < bits.Length; j++) bits[j] = 1L << j;
            return new Vector<long>(bits);
        }
    }

    // ---------------- Vector<T>: the 256-bit form, or the 512-bit one when Vector<T> is that wide ----------------

    /// <summary>The two fields of <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 2-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load2<T>(ref T column, nuint row, out Vector<T> f0, out Vector<T> f1)
    {
        if (Vector<byte>.Count == 64)
        {
            Load2(ref column, row, out Vector512<T> a, out Vector512<T> b);
            (f0, f1) = (a.AsVector(), b.AsVector());
            return;
        }
        Load2(ref column, row, out Vector256<T> x, out Vector256<T> y);
        (f0, f1) = (x.AsVector(), y.AsVector());
    }

    /// <summary>The three fields of <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 3-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load3<T>(ref T column, nuint row, out Vector<T> f0, out Vector<T> f1, out Vector<T> f2)
    {
        if (Vector<byte>.Count == 64)
        {
            Load3(ref column, row, out Vector512<T> a, out Vector512<T> b, out Vector512<T> c);
            (f0, f1, f2) = (a.AsVector(), b.AsVector(), c.AsVector());
            return;
        }
        Load3(ref column, row, out Vector256<T> x, out Vector256<T> y, out Vector256<T> z);
        (f0, f1, f2) = (x.AsVector(), y.AsVector(), z.AsVector());
    }

    /// <summary>The four fields of <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 4-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load4<T>(ref T column, nuint row, out Vector<T> f0, out Vector<T> f1, out Vector<T> f2, out Vector<T> f3)
    {
        if (Vector<byte>.Count == 64)
        {
            Load4(ref column, row, out Vector512<T> a, out Vector512<T> b, out Vector512<T> c, out Vector512<T> d);
            (f0, f1, f2, f3) = (a.AsVector(), b.AsVector(), c.AsVector(), d.AsVector());
            return;
        }
        Load4(ref column, row, out Vector256<T> x, out Vector256<T> y, out Vector256<T> z, out Vector256<T> w);
        (f0, f1, f2, f3) = (x.AsVector(), y.AsVector(), z.AsVector(), w.AsVector());
    }

    /// <summary>Writes the two fields back to <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store2<T>(Vector<T> f0, Vector<T> f1, ref T column, nuint row)
    {
        if (Vector<byte>.Count == 64) Store2(f0.AsVector512(), f1.AsVector512(), ref column, row);
        else Store2(f0.AsVector256(), f1.AsVector256(), ref column, row);
    }

    /// <summary>Writes the three fields back to <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store3<T>(Vector<T> f0, Vector<T> f1, Vector<T> f2, ref T column, nuint row)
    {
        if (Vector<byte>.Count == 64) Store3(f0.AsVector512(), f1.AsVector512(), f2.AsVector512(), ref column, row);
        else Store3(f0.AsVector256(), f1.AsVector256(), f2.AsVector256(), ref column, row);
    }

    /// <summary>Writes the four fields back to <c>Vector&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store4<T>(Vector<T> f0, Vector<T> f1, Vector<T> f2, Vector<T> f3, ref T column, nuint row)
    {
        if (Vector<byte>.Count == 64) Store4(f0.AsVector512(), f1.AsVector512(), f2.AsVector512(), f3.AsVector512(), ref column, row);
        else Store4(f0.AsVector256(), f1.AsVector256(), f2.AsVector256(), f3.AsVector256(), ref column, row);
    }

    // ---------------- 256 bits (AVX2) ----------------

    // The 256-bit forms run only behind IsAccelerated (AVX2): Avx / Avx2 calls have no software fallback here.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load2<T>(ref T column, nuint row, out Vector256<T> f0, out Vector256<T> f1)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var a = Vector256.LoadUnsafe(ref s, row * 2);     // x0 y0 x1 y1 | x2 y2 x3 y3
            var b = Vector256.LoadUnsafe(ref s, row * 2 + 8); // x4 y4 x5 y5 | x6 y6 x7 y7
            // x0 x1 x4 x5 | x2 x3 x6 x7, then the 64-bit pairs in order 0 2 1 3
            f0 = Avx2.Permute4x64(Avx.Shuffle(a, b, 0b10_00_10_00).AsDouble(), 0b11_01_10_00).As<double, T>();
            f1 = Avx2.Permute4x64(Avx.Shuffle(a, b, 0b11_01_11_01).AsDouble(), 0b11_01_10_00).As<double, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector256.LoadUnsafe(ref s, row * 2);     // x0 y0 | x1 y1
            var b = Vector256.LoadUnsafe(ref s, row * 2 + 4); // x2 y2 | x3 y3
            f0 = Avx2.Permute4x64(Avx.UnpackLow(a, b), 0b11_01_10_00).As<double, T>();  // x0 x2 | x1 x3 -> 0 1 2 3
            f1 = Avx2.Permute4x64(Avx.UnpackHigh(a, b), 0b11_01_10_00).As<double, T>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store2<T>(Vector256<T> f0, Vector256<T> f1, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            var lo = Avx.UnpackLow(f0.AsSingle(), f1.AsSingle());  // x0 y0 x1 y1 | x4 y4 x5 y5
            var hi = Avx.UnpackHigh(f0.AsSingle(), f1.AsSingle()); // x2 y2 x3 y3 | x6 y6 x7 y7
            Avx.Permute2x128(lo, hi, 0x20).StoreUnsafe(ref d, row * 2);
            Avx.Permute2x128(lo, hi, 0x31).StoreUnsafe(ref d, row * 2 + 8);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            var x = Avx2.Permute4x64(f0.AsDouble(), 0b11_01_10_00); // x0 x2 | x1 x3 (the order 0 2 1 3 is its own inverse)
            var y = Avx2.Permute4x64(f1.AsDouble(), 0b11_01_10_00);
            Avx.UnpackLow(x, y).StoreUnsafe(ref d, row * 2);
            Avx.UnpackHigh(x, y).StoreUnsafe(ref d, row * 2 + 4);
        }
    }

    // Three fields: the elements of one field sit in distinct lanes of the three loaded vectors (3 and the lane count
    // are coprime), so two blends gather them into one vector and one permute puts them in row order.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load3<T>(ref T column, nuint row, out Vector256<T> f0, out Vector256<T> f1, out Vector256<T> f2)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var a = Vector256.LoadUnsafe(ref s, row * 3);      // x0 y0 z0 x1 y1 z1 x2 y2
            var b = Vector256.LoadUnsafe(ref s, row * 3 + 8);  // z2 x3 y3 z3 x4 y4 z4 x5
            var c = Vector256.LoadUnsafe(ref s, row * 3 + 16); // y5 z5 x6 y6 z6 x7 y7 z7
            f0 = Avx2.PermuteVar8x32(Avx.Blend(Avx.Blend(a, b, 0b1001_0010), c, 0b0010_0100), Permute3X).As<float, T>();
            f1 = Avx2.PermuteVar8x32(Avx.Blend(Avx.Blend(a, b, 0b0010_0100), c, 0b0100_1001), Permute3Y).As<float, T>();
            f2 = Avx2.PermuteVar8x32(Avx.Blend(Avx.Blend(a, b, 0b0100_1001), c, 0b1001_0010), Permute3Z).As<float, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector256.LoadUnsafe(ref s, row * 3);     // x0 y0 z0 x1
            var b = Vector256.LoadUnsafe(ref s, row * 3 + 4); // y1 z1 x2 y2
            var c = Vector256.LoadUnsafe(ref s, row * 3 + 8); // z2 x3 y3 z3
            f0 = Avx2.Permute4x64(Avx.Blend(Avx.Blend(a, b, 0b0100), c, 0b0010), 0b01_10_11_00).As<double, T>(); // x0 x3 x2 x1
            f1 = Avx2.Permute4x64(Avx.Blend(Avx.Blend(a, b, 0b1001), c, 0b0100), 0b10_11_00_01).As<double, T>(); // y1 y0 y3 y2
            f2 = Avx2.Permute4x64(Avx.Blend(Avx.Blend(a, b, 0b0010), c, 0b1001), 0b11_00_01_10).As<double, T>(); // z2 z1 z0 z3
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store3<T>(Vector256<T> f0, Vector256<T> f1, Vector256<T> f2, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            var x = Avx2.PermuteVar8x32(f0.AsSingle(), Permute3X); // the permutes of Load3, inverted
            var y = Avx2.PermuteVar8x32(f1.AsSingle(), Unpermute3Y);
            var z = Avx2.PermuteVar8x32(f2.AsSingle(), Permute3Z);
            Avx.Blend(Avx.Blend(x, y, 0b1001_0010), z, 0b0010_0100).StoreUnsafe(ref d, row * 3);
            Avx.Blend(Avx.Blend(x, y, 0b0010_0100), z, 0b0100_1001).StoreUnsafe(ref d, row * 3 + 8);
            Avx.Blend(Avx.Blend(x, y, 0b0100_1001), z, 0b1001_0010).StoreUnsafe(ref d, row * 3 + 16);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            var x = Avx2.Permute4x64(f0.AsDouble(), 0b01_10_11_00); // each permute of Load3 is its own inverse
            var y = Avx2.Permute4x64(f1.AsDouble(), 0b10_11_00_01);
            var z = Avx2.Permute4x64(f2.AsDouble(), 0b11_00_01_10);
            Avx.Blend(Avx.Blend(x, y, 0b0010), z, 0b0100).StoreUnsafe(ref d, row * 3);
            Avx.Blend(Avx.Blend(y, z, 0b0010), x, 0b0100).StoreUnsafe(ref d, row * 3 + 4);
            Avx.Blend(Avx.Blend(z, x, 0b0010), y, 0b0100).StoreUnsafe(ref d, row * 3 + 8);
        }
    }

    private static readonly Vector256<int> Permute3X = Vector256.Create(0, 3, 6, 1, 4, 7, 2, 5); // its own inverse
    private static readonly Vector256<int> Permute3Y = Vector256.Create(1, 4, 7, 2, 5, 0, 3, 6);
    private static readonly Vector256<int> Unpermute3Y = Vector256.Create(5, 0, 3, 6, 1, 4, 7, 2);
    private static readonly Vector256<int> Permute3Z = Vector256.Create(2, 5, 0, 3, 6, 1, 4, 7); // its own inverse

    // Four fields: a 4 x 4 transpose (per 128-bit half for 4-byte lanes).

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load4<T>(ref T column, nuint row, out Vector256<T> f0, out Vector256<T> f1, out Vector256<T> f2, out Vector256<T> f3)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var r0 = Vector256.LoadUnsafe(ref s, row * 4);      // row 0 | row 1
            var r1 = Vector256.LoadUnsafe(ref s, row * 4 + 8);  // row 2 | row 3
            var r2 = Vector256.LoadUnsafe(ref s, row * 4 + 16); // row 4 | row 5
            var r3 = Vector256.LoadUnsafe(ref s, row * 4 + 24); // row 6 | row 7
            var q0 = Avx.Permute2x128(r0, r2, 0x20); // row 0 | row 4
            var q1 = Avx.Permute2x128(r0, r2, 0x31); // row 1 | row 5
            var q2 = Avx.Permute2x128(r1, r3, 0x20); // row 2 | row 6
            var q3 = Avx.Permute2x128(r1, r3, 0x31); // row 3 | row 7
            var t0 = Avx.UnpackLow(q0, q1);  // x0 x1 y0 y1 | x4 x5 y4 y5
            var t1 = Avx.UnpackHigh(q0, q1); // z0 z1 w0 w1 | z4 z5 w4 w5
            var t2 = Avx.UnpackLow(q2, q3);
            var t3 = Avx.UnpackHigh(q2, q3);
            f0 = Avx.Shuffle(t0, t2, 0b01_00_01_00).As<float, T>();
            f1 = Avx.Shuffle(t0, t2, 0b11_10_11_10).As<float, T>();
            f2 = Avx.Shuffle(t1, t3, 0b01_00_01_00).As<float, T>();
            f3 = Avx.Shuffle(t1, t3, 0b11_10_11_10).As<float, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector256.LoadUnsafe(ref s, row * 4);
            var b = Vector256.LoadUnsafe(ref s, row * 4 + 4);
            var c = Vector256.LoadUnsafe(ref s, row * 4 + 8);
            var d = Vector256.LoadUnsafe(ref s, row * 4 + 12);
            var t0 = Avx.UnpackLow(a, b);  // x0 x1 | z0 z1
            var t1 = Avx.UnpackHigh(a, b); // y0 y1 | w0 w1
            var t2 = Avx.UnpackLow(c, d);
            var t3 = Avx.UnpackHigh(c, d);
            f0 = Avx.Permute2x128(t0, t2, 0x20).As<double, T>();
            f1 = Avx.Permute2x128(t1, t3, 0x20).As<double, T>();
            f2 = Avx.Permute2x128(t0, t2, 0x31).As<double, T>();
            f3 = Avx.Permute2x128(t1, t3, 0x31).As<double, T>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4<T>(Vector256<T> f0, Vector256<T> f1, Vector256<T> f2, Vector256<T> f3, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            var t0 = Avx.UnpackLow(f0.AsSingle(), f1.AsSingle());  // x0 y0 x1 y1 | x4 y4 x5 y5
            var t1 = Avx.UnpackHigh(f0.AsSingle(), f1.AsSingle()); // x2 y2 x3 y3 | x6 y6 x7 y7
            var t2 = Avx.UnpackLow(f2.AsSingle(), f3.AsSingle());
            var t3 = Avx.UnpackHigh(f2.AsSingle(), f3.AsSingle());
            var q0 = Avx.Shuffle(t0, t2, 0b01_00_01_00); // row 0 | row 4
            var q1 = Avx.Shuffle(t0, t2, 0b11_10_11_10); // row 1 | row 5
            var q2 = Avx.Shuffle(t1, t3, 0b01_00_01_00); // row 2 | row 6
            var q3 = Avx.Shuffle(t1, t3, 0b11_10_11_10); // row 3 | row 7
            Avx.Permute2x128(q0, q1, 0x20).StoreUnsafe(ref d, row * 4);
            Avx.Permute2x128(q2, q3, 0x20).StoreUnsafe(ref d, row * 4 + 8);
            Avx.Permute2x128(q0, q1, 0x31).StoreUnsafe(ref d, row * 4 + 16);
            Avx.Permute2x128(q2, q3, 0x31).StoreUnsafe(ref d, row * 4 + 24);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            var t0 = Avx.Permute2x128(f0.AsDouble(), f2.AsDouble(), 0x20); // x0 x1 | z0 z1
            var t2 = Avx.Permute2x128(f0.AsDouble(), f2.AsDouble(), 0x31); // x2 x3 | z2 z3
            var t1 = Avx.Permute2x128(f1.AsDouble(), f3.AsDouble(), 0x20);
            var t3 = Avx.Permute2x128(f1.AsDouble(), f3.AsDouble(), 0x31);
            Avx.UnpackLow(t0, t1).StoreUnsafe(ref d, row * 4);
            Avx.UnpackHigh(t0, t1).StoreUnsafe(ref d, row * 4 + 4);
            Avx.UnpackLow(t2, t3).StoreUnsafe(ref d, row * 4 + 8);
            Avx.UnpackHigh(t2, t3).StoreUnsafe(ref d, row * 4 + 12);
        }
    }

    // ---------------- 512 bits (AVX-512F): two-source permutes (vpermt2ps / vpermt2pd) ----------------
    // Index tables are static readonly vectors (constants once the JIT optimizes the loop), one set per lane size.

    /// <summary>The two fields of <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 2-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load2<T>(ref T column, nuint row, out Vector512<T> f0, out Vector512<T> f1)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 2);
            var b = Vector512.LoadUnsafe(ref s, row * 2 + 16);
            f0 = Avx512F.PermuteVar16x32x2(a, Tables32.Load2A, b).As<float, T>();
            f1 = Avx512F.PermuteVar16x32x2(a, Tables32.Load2B, b).As<float, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 2);
            var b = Vector512.LoadUnsafe(ref s, row * 2 + 8);
            f0 = Avx512F.PermuteVar8x64x2(a, Tables64.Load2A, b).As<double, T>();
            f1 = Avx512F.PermuteVar8x64x2(a, Tables64.Load2B, b).As<double, T>();
        }
    }

    /// <summary>Writes the two fields back to <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store2<T>(Vector512<T> f0, Vector512<T> f1, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            Avx512F.PermuteVar16x32x2(f0.AsSingle(), Tables32.Store2A, f1.AsSingle()).StoreUnsafe(ref d, row * 2);
            Avx512F.PermuteVar16x32x2(f0.AsSingle(), Tables32.Store2B, f1.AsSingle()).StoreUnsafe(ref d, row * 2 + 16);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            Avx512F.PermuteVar8x64x2(f0.AsDouble(), Tables64.Store2A, f1.AsDouble()).StoreUnsafe(ref d, row * 2);
            Avx512F.PermuteVar8x64x2(f0.AsDouble(), Tables64.Store2B, f1.AsDouble()).StoreUnsafe(ref d, row * 2 + 8);
        }
    }

    /// <summary>The three fields of <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 3-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load3<T>(ref T column, nuint row, out Vector512<T> f0, out Vector512<T> f1, out Vector512<T> f2)
    {
        // Each field: a permute of the first two vectors, then one that takes the third's elements into it.
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 3);
            var b = Vector512.LoadUnsafe(ref s, row * 3 + 16);
            var c = Vector512.LoadUnsafe(ref s, row * 3 + 32);
            f0 = Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(a, Tables32.Load3A, b), Tables32.Load3B, c).As<float, T>();
            f1 = Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(a, Tables32.Load3C, b), Tables32.Load3D, c).As<float, T>();
            f2 = Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(a, Tables32.Load3E, b), Tables32.Load3F, c).As<float, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 3);
            var b = Vector512.LoadUnsafe(ref s, row * 3 + 8);
            var c = Vector512.LoadUnsafe(ref s, row * 3 + 16);
            f0 = Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(a, Tables64.Load3A, b), Tables64.Load3B, c).As<double, T>();
            f1 = Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(a, Tables64.Load3C, b), Tables64.Load3D, c).As<double, T>();
            f2 = Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(a, Tables64.Load3E, b), Tables64.Load3F, c).As<double, T>();
        }
    }

    /// <summary>Writes the three fields back to <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store3<T>(Vector512<T> f0, Vector512<T> f1, Vector512<T> f2, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            Vector512<float> x = f0.AsSingle(), y = f1.AsSingle(), z = f2.AsSingle();
            Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(x, Tables32.Store3A, y), Tables32.Store3B, z).StoreUnsafe(ref d, row * 3);
            Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(x, Tables32.Store3C, y), Tables32.Store3D, z).StoreUnsafe(ref d, row * 3 + 16);
            Avx512F.PermuteVar16x32x2(Avx512F.PermuteVar16x32x2(x, Tables32.Store3E, y), Tables32.Store3F, z).StoreUnsafe(ref d, row * 3 + 32);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            Vector512<double> x = f0.AsDouble(), y = f1.AsDouble(), z = f2.AsDouble();
            Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(x, Tables64.Store3A, y), Tables64.Store3B, z).StoreUnsafe(ref d, row * 3);
            Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(x, Tables64.Store3C, y), Tables64.Store3D, z).StoreUnsafe(ref d, row * 3 + 8);
            Avx512F.PermuteVar8x64x2(Avx512F.PermuteVar8x64x2(x, Tables64.Store3E, y), Tables64.Store3F, z).StoreUnsafe(ref d, row * 3 + 16);
        }
    }

    /// <summary>The four fields of <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/> of a column of 4-field structs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Load4<T>(ref T column, nuint row, out Vector512<T> f0, out Vector512<T> f1, out Vector512<T> f2, out Vector512<T> f3)
    {
        // Two rounds: the first two vectors to (x y) and (z w) halves of their rows, the same for the last two, then
        // the halves of both joined per field.
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float s = ref Unsafe.As<T, float>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 4);
            var b = Vector512.LoadUnsafe(ref s, row * 4 + 16);
            var c = Vector512.LoadUnsafe(ref s, row * 4 + 32);
            var d = Vector512.LoadUnsafe(ref s, row * 4 + 48);
            var xy0 = Avx512F.PermuteVar16x32x2(a, Tables32.Load4A, b);
            var zw0 = Avx512F.PermuteVar16x32x2(a, Tables32.Load4B, b);
            var xy1 = Avx512F.PermuteVar16x32x2(c, Tables32.Load4A, d);
            var zw1 = Avx512F.PermuteVar16x32x2(c, Tables32.Load4B, d);
            f0 = Avx512F.PermuteVar16x32x2(xy0, Tables32.Load4C, xy1).As<float, T>();
            f1 = Avx512F.PermuteVar16x32x2(xy0, Tables32.Load4D, xy1).As<float, T>();
            f2 = Avx512F.PermuteVar16x32x2(zw0, Tables32.Load4C, zw1).As<float, T>();
            f3 = Avx512F.PermuteVar16x32x2(zw0, Tables32.Load4D, zw1).As<float, T>();
        }
        else
        {
            ref double s = ref Unsafe.As<T, double>(ref column);
            var a = Vector512.LoadUnsafe(ref s, row * 4);
            var b = Vector512.LoadUnsafe(ref s, row * 4 + 8);
            var c = Vector512.LoadUnsafe(ref s, row * 4 + 16);
            var d = Vector512.LoadUnsafe(ref s, row * 4 + 24);
            var xy0 = Avx512F.PermuteVar8x64x2(a, Tables64.Load4A, b);
            var zw0 = Avx512F.PermuteVar8x64x2(a, Tables64.Load4B, b);
            var xy1 = Avx512F.PermuteVar8x64x2(c, Tables64.Load4A, d);
            var zw1 = Avx512F.PermuteVar8x64x2(c, Tables64.Load4B, d);
            f0 = Avx512F.PermuteVar8x64x2(xy0, Tables64.Load4C, xy1).As<double, T>();
            f1 = Avx512F.PermuteVar8x64x2(xy0, Tables64.Load4D, xy1).As<double, T>();
            f2 = Avx512F.PermuteVar8x64x2(zw0, Tables64.Load4C, zw1).As<double, T>();
            f3 = Avx512F.PermuteVar8x64x2(zw0, Tables64.Load4D, zw1).As<double, T>();
        }
    }

    /// <summary>Writes the four fields back to <c>Vector512&lt;T&gt;.Count</c> rows from <paramref name="row"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store4<T>(Vector512<T> f0, Vector512<T> f1, Vector512<T> f2, Vector512<T> f3, ref T column, nuint row)
    {
        if (Unsafe.SizeOf<T>() == 4)
        {
            ref float d = ref Unsafe.As<T, float>(ref column);
            Vector512<float> x = f0.AsSingle(), y = f1.AsSingle(), z = f2.AsSingle(), w = f3.AsSingle();
            var xy0 = Avx512F.PermuteVar16x32x2(x, Tables32.Store4A, y);
            var xy1 = Avx512F.PermuteVar16x32x2(x, Tables32.Store4B, y);
            var zw0 = Avx512F.PermuteVar16x32x2(z, Tables32.Store4A, w);
            var zw1 = Avx512F.PermuteVar16x32x2(z, Tables32.Store4B, w);
            Avx512F.PermuteVar16x32x2(xy0, Tables32.Store4C, zw0).StoreUnsafe(ref d, row * 4);
            Avx512F.PermuteVar16x32x2(xy0, Tables32.Store4D, zw0).StoreUnsafe(ref d, row * 4 + 16);
            Avx512F.PermuteVar16x32x2(xy1, Tables32.Store4C, zw1).StoreUnsafe(ref d, row * 4 + 32);
            Avx512F.PermuteVar16x32x2(xy1, Tables32.Store4D, zw1).StoreUnsafe(ref d, row * 4 + 48);
        }
        else
        {
            ref double d = ref Unsafe.As<T, double>(ref column);
            Vector512<double> x = f0.AsDouble(), y = f1.AsDouble(), z = f2.AsDouble(), w = f3.AsDouble();
            var xy0 = Avx512F.PermuteVar8x64x2(x, Tables64.Store4A, y);
            var xy1 = Avx512F.PermuteVar8x64x2(x, Tables64.Store4B, y);
            var zw0 = Avx512F.PermuteVar8x64x2(z, Tables64.Store4A, w);
            var zw1 = Avx512F.PermuteVar8x64x2(z, Tables64.Store4B, w);
            Avx512F.PermuteVar8x64x2(xy0, Tables64.Store4C, zw0).StoreUnsafe(ref d, row * 4);
            Avx512F.PermuteVar8x64x2(xy0, Tables64.Store4D, zw0).StoreUnsafe(ref d, row * 4 + 8);
            Avx512F.PermuteVar8x64x2(xy1, Tables64.Store4C, zw1).StoreUnsafe(ref d, row * 4 + 16);
            Avx512F.PermuteVar8x64x2(xy1, Tables64.Store4D, zw1).StoreUnsafe(ref d, row * 4 + 24);
        }
    }

    /// <summary>
    /// The permute indices of the 512-bit forms for vectors of <c>lanes</c> lanes (16 of 4 bytes, 8 of 8). In
    /// a two-source permute, index i &lt; lanes takes lane i of the first vector, lanes + i lane i of the second.
    /// </summary>
    private static class Tables
    {
        /// <summary>Load2: field f of row j is element 2j + f of the two vectors.</summary>
        public static int[][] Load2(int lanes) => [Fill(lanes, j => 2 * j), Fill(lanes, j => 2 * j + 1)];

        /// <summary>Store2: element q of the two output vectors is field q % 2 of row q / 2.</summary>
        public static int[][] Store2(int lanes) =>
            [Fill(lanes, q => (q % 2) * lanes + q / 2), Fill(lanes, q => (q % 2) * lanes + (lanes + q) / 2)];

        /// <summary>
        /// Load3, per field f: element 3j + f of the three vectors; the first permute takes those of the first two
        /// (any lane for the others), the second keeps them and takes those of the third.
        /// </summary>
        public static int[][] Load3(int lanes)
        {
            var t = new int[6][];
            for (int f = 0; f < 3; f++)
            {
                t[2 * f] = Fill(lanes, j => 3 * j + f < 2 * lanes ? 3 * j + f : 0);
                t[2 * f + 1] = Fill(lanes, j => 3 * j + f < 2 * lanes ? j : lanes + 3 * j + f - 2 * lanes);
            }
            return t;
        }

        /// <summary>
        /// Store3, per output vector k: element q (of all three, 16k + q overall) is field q % 3 of row q / 3; the first
        /// permute takes those of fields 0 and 1, the second keeps them and takes those of field 2.
        /// </summary>
        public static int[][] Store3(int lanes)
        {
            var t = new int[6][];
            for (int k = 0; k < 3; k++)
            {
                t[2 * k] = Fill(lanes, j => ((k * lanes + j) % 3) switch { 0 => (k * lanes + j) / 3, 1 => lanes + (k * lanes + j) / 3, _ => 0 });
                t[2 * k + 1] = Fill(lanes, j => (k * lanes + j) % 3 == 2 ? lanes + (k * lanes + j) / 3 : j);
            }
            return t;
        }

        /// <summary>
        /// Load4: the first round splits two vectors (rows 0 to lanes / 4 * 2 - 1) into the x and y fields of their rows
        /// (one half each) and the z and w fields; the second joins the halves of two such vectors per field.
        /// </summary>
        public static int[][] Load4(int lanes)
        {
            int half = lanes / 2; // rows of two vectors
            return [Fill(lanes, j => j < half ? 4 * j : 4 * (j - half) + 1), Fill(lanes, j => j < half ? 4 * j + 2 : 4 * (j - half) + 3),
                Fill(lanes, j => j < half ? j : lanes + j - half), Fill(lanes, j => j < half ? half + j : lanes + j)];
        }

        /// <summary>Store4: interleave fields pairwise (x with y, z with w), then pairs of pairs into rows.</summary>
        public static int[][] Store4(int lanes)
        {
            int half = lanes / 2;
            return [Fill(lanes, q => (q % 2) * lanes + q / 2), Fill(lanes, q => (q % 2) * lanes + half + q / 2),
                Fill(lanes, q => (q / 2 % 2) * lanes + (q / 4) * 2 + q % 2), Fill(lanes, q => (q / 2 % 2) * lanes + half + (q / 4) * 2 + q % 2)];
        }

        private static int[] Fill(int lanes, System.Func<int, int> index)
        {
            var values = new int[lanes];
            for (int i = 0; i < lanes; i++) values[i] = index(i);
            return values;
        }
    }

    // One static readonly vector per permute: constants once the JIT optimizes the loop (no array, no bounds check).
    private static class Tables32
    {
        public static readonly Vector512<int> Load2A = Vector512.Create(Tables.Load2(16)[0]),
            Load2B = Vector512.Create(Tables.Load2(16)[1]);
        public static readonly Vector512<int> Store2A = Vector512.Create(Tables.Store2(16)[0]),
            Store2B = Vector512.Create(Tables.Store2(16)[1]);
        public static readonly Vector512<int> Load3A = Vector512.Create(Tables.Load3(16)[0]),
            Load3B = Vector512.Create(Tables.Load3(16)[1]),
            Load3C = Vector512.Create(Tables.Load3(16)[2]),
            Load3D = Vector512.Create(Tables.Load3(16)[3]),
            Load3E = Vector512.Create(Tables.Load3(16)[4]),
            Load3F = Vector512.Create(Tables.Load3(16)[5]);
        public static readonly Vector512<int> Store3A = Vector512.Create(Tables.Store3(16)[0]),
            Store3B = Vector512.Create(Tables.Store3(16)[1]),
            Store3C = Vector512.Create(Tables.Store3(16)[2]),
            Store3D = Vector512.Create(Tables.Store3(16)[3]),
            Store3E = Vector512.Create(Tables.Store3(16)[4]),
            Store3F = Vector512.Create(Tables.Store3(16)[5]);
        public static readonly Vector512<int> Load4A = Vector512.Create(Tables.Load4(16)[0]),
            Load4B = Vector512.Create(Tables.Load4(16)[1]),
            Load4C = Vector512.Create(Tables.Load4(16)[2]),
            Load4D = Vector512.Create(Tables.Load4(16)[3]);
        public static readonly Vector512<int> Store4A = Vector512.Create(Tables.Store4(16)[0]),
            Store4B = Vector512.Create(Tables.Store4(16)[1]),
            Store4C = Vector512.Create(Tables.Store4(16)[2]),
            Store4D = Vector512.Create(Tables.Store4(16)[3]);
    }

    private static class Tables64
    {
        public static readonly Vector512<long> Load2A = Wide(Tables.Load2(8)[0]),
            Load2B = Wide(Tables.Load2(8)[1]);
        public static readonly Vector512<long> Store2A = Wide(Tables.Store2(8)[0]),
            Store2B = Wide(Tables.Store2(8)[1]);
        public static readonly Vector512<long> Load3A = Wide(Tables.Load3(8)[0]),
            Load3B = Wide(Tables.Load3(8)[1]),
            Load3C = Wide(Tables.Load3(8)[2]),
            Load3D = Wide(Tables.Load3(8)[3]),
            Load3E = Wide(Tables.Load3(8)[4]),
            Load3F = Wide(Tables.Load3(8)[5]);
        public static readonly Vector512<long> Store3A = Wide(Tables.Store3(8)[0]),
            Store3B = Wide(Tables.Store3(8)[1]),
            Store3C = Wide(Tables.Store3(8)[2]),
            Store3D = Wide(Tables.Store3(8)[3]),
            Store3E = Wide(Tables.Store3(8)[4]),
            Store3F = Wide(Tables.Store3(8)[5]);
        public static readonly Vector512<long> Load4A = Wide(Tables.Load4(8)[0]),
            Load4B = Wide(Tables.Load4(8)[1]),
            Load4C = Wide(Tables.Load4(8)[2]),
            Load4D = Wide(Tables.Load4(8)[3]);
        public static readonly Vector512<long> Store4A = Wide(Tables.Store4(8)[0]),
            Store4B = Wide(Tables.Store4(8)[1]),
            Store4C = Wide(Tables.Store4(8)[2]),
            Store4D = Wide(Tables.Store4(8)[3]);

        private static Vector512<long> Wide(int[] table)
        {
            var wide = new long[table.Length];
            for (int j = 0; j < wide.Length; j++) wide[j] = table[j];
            return Vector512.Create(wide);
        }
    }
}
