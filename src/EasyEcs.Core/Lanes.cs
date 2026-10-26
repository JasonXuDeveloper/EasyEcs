using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// Stream compaction for batch methods, a GPU's ballot and compaction on a CPU: a vector compare decides a lane mask
/// (<c>Vector256.LessThan(hp, limit).ExtractMostSignificantBits()</c>: CUDA's <c>__ballot_sync</c>; its
/// <c>__popc</c> is <see cref="BitOperations.PopCount(ulong)"/>), and <see cref="Compact{T}"/> writes the selected
/// elements side by side: the ids of the rows a batch method picked (<c>entities.Ids</c>), their values, their
/// indexes. With AVX-512 a lane of 4 or 8 bytes moves 16 or 8 at a time (<c>VPCOMPRESSD</c> / <c>VPCOMPRESSQ</c>);
/// with AVX2 8 or 4 at a time through a permutation table (Polychroniou, Raghavan and Ross's selective store: one
/// shuffle per vector, chosen by the vector's bits of the mask); with 128-bit vectors (NEON's <c>TBL</c> on ARM64,
/// SSSE3's <c>PSHUFB</c>) a lane of 4 bytes moves 4 at a time through a byte shuffle table; otherwise, and for other
/// sizes, one at a time without a branch per element.
/// </summary>
public static class Lanes
{
    // Up to this many selected elements, one step per set bit beats the vector steps up to the last one.
    private const int Few = 4;

    // Below this many selected elements of 64, a step per set bit (TZCNT) beats the AVX2 and 128-bit steps (8 to 32 per
    // word) and the scalar form that stores every element up to the last selected one and advances by its bit.
    private const int Sparse = 12;

    /// <summary>
    /// Write the elements of <paramref name="source"/> whose bit is set in <paramref name="mask"/> (bit <c>i</c> selects
    /// <c>source[i]</c>, so at most 64 elements) to the start of <paramref name="destination"/>, in order, and return
    /// how many: <c>PopCount(mask)</c>. Nothing past the end of <paramref name="source"/> is read and only that many
    /// elements are written. O(64 / lanes per vector) for elements of 4 or 8 bytes with AVX-512 or AVX2 and of 4 bytes
    /// with SSSE3 or ARM64's NEON, else O(selected); allocation-free.
    /// </summary>
    /// <param name="source">The elements to choose from: a run of a batch method's span or of its <c>EntitySpan.Ids</c>.</param>
    /// <param name="mask">The selected elements; no bit at or past <c>source.Length</c> may be set.</param>
    /// <param name="destination">Where the selected elements go; at least <c>PopCount(mask)</c> long.</param>
    /// <returns>The number of elements written.</returns>
    /// <exception cref="ArgumentException">A bit of <paramref name="mask"/> selects no element of <paramref name="source"/>, or <paramref name="destination"/> is too short.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compact<T>(ReadOnlySpan<T> source, ulong mask, Span<T> destination) where T : unmanaged
    {
        int count = BitOperations.PopCount(mask);
        if (source.Length < 64 && mask >> source.Length != 0 || destination.Length < count) ThrowOutside(source.Length, destination.Length, count);
        if (count == 0) return 0;
        ref T from = ref MemoryMarshal.GetReference(source);
        ref T into = ref MemoryMarshal.GetReference(destination);
        if (count <= Few) Visit(ref from, mask, ref into);
        else if (Unsafe.SizeOf<T>() == 8 && Avx512F.IsSupported) Compress8(ref Unsafe.As<T, long>(ref from), mask, ref Unsafe.As<T, long>(ref into));
        else if (Unsafe.SizeOf<T>() == 4 && Avx512F.IsSupported) Compress4(ref Unsafe.As<T, int>(ref from), mask, ref Unsafe.As<T, int>(ref into));
        else if (count <= Sparse) Visit(ref from, mask, ref into);
        else if (Unsafe.SizeOf<T>() == 8 && Avx2.IsSupported) Permute8(ref Unsafe.As<T, long>(ref from), source.Length, mask, ref Unsafe.As<T, long>(ref into), count);
        else if (Unsafe.SizeOf<T>() == 4 && Avx2.IsSupported) Permute4(ref Unsafe.As<T, int>(ref from), source.Length, mask, ref Unsafe.As<T, int>(ref into), count);
        else if (Unsafe.SizeOf<T>() == 4 && Shuffles) Shuffle4(ref Unsafe.As<T, int>(ref from), source.Length, mask, ref Unsafe.As<T, int>(ref into), count);
        else Advance(ref from, mask, ref into);
        return count;
    }

    /// <summary>
    /// Write the numbers of the rows <paramref name="mask"/> selects, <paramref name="first"/> + the index of each set
    /// bit, to the start of <paramref name="destination"/> in order, and return how many: the row list of a word of
    /// rows a vector condition passed, which generated loops and the core write with this one call whatever the
    /// machine (<see cref="Compact{T}"/> over the numbers 0 to 63, then <paramref name="first"/> added in vectors).
    /// O(64 / lanes per vector); allocation-free.
    /// </summary>
    /// <param name="mask">The selected rows of the word: bit <c>i</c> is row <paramref name="first"/> + i.</param>
    /// <param name="first">The word's first row.</param>
    /// <param name="destination">Where the rows go; at least <c>PopCount(mask)</c> long.</param>
    /// <returns>The number of rows written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RowNumbers(ulong mask, int first, Span<int> destination)
    {
        int n = Compact<int>(Numbers, mask, destination);
        if (first != 0 && n != 0) Offset(destination.Slice(0, n), first);
        return n;
    }

    // 0 to 63: the rows of a word RowNumbers compacts. Literal data in the assembly (RVA), so no static to initialize: under
    // Native AOT a static readonly array would cost a class-initialization check per call.
    private static ReadOnlySpan<int> Numbers =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63,
    ];

    // rows[i] += first, a vector at a time (Vector<int>: 16, 8 or 4 lanes), the rest one by one.
    private static void Offset(Span<int> rows, int first)
    {
        ref int at = ref MemoryMarshal.GetReference(rows);
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var add = new Vector<int>(first);
            for (; i <= rows.Length - Vector<int>.Count; i += Vector<int>.Count)
                (Vector.LoadUnsafe(ref at, (nuint)i) + add).StoreUnsafe(ref at, (nuint)i);
        }

        for (; i < rows.Length; i++) Unsafe.Add(ref at, i) += first;
    }

    // 8 lanes of 8 bytes per step, a byte of the mask each: a masked load (only the selected lanes are read, so a
    // mask that ends inside a vector reads nothing past the source) and a compressing store (only the selected lanes
    // are written, side by side). Steps whose byte is 0 are skipped; the loop stops after the last set bit.
    private static unsafe void Compress8(ref long from, ulong mask, ref long into)
    {
        var bits = Vector512.Create(1L, 2, 4, 8, 16, 32, 64, 128);
        fixed (long* source = &from, destination = &into)
        {
            long* to = destination;
            for (long* at = source; mask != 0; at += 8, mask >>= 8)
            {
                long m = (long)(mask & 0xFF);
                if (m == 0) continue;
                var lanes = Vector512.Equals(Vector512.Create(m) & bits, bits);
                Avx512F.CompressStore(to, lanes, Avx512F.MaskLoad(at, lanes, Vector512<long>.Zero));
                to += BitOperations.PopCount((ulong)m);
            }
        }
    }

    // 16 lanes of 4 bytes per step, as Compress8.
    private static unsafe void Compress4(ref int from, ulong mask, ref int into)
    {
        var bits = Vector512.Create(1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768);
        fixed (int* source = &from, destination = &into)
        {
            int* to = destination;
            for (int* at = source; mask != 0; at += 16, mask >>= 16)
            {
                int m = (int)(mask & 0xFFFF);
                if (m == 0) continue;
                var lanes = Vector512.Equals(Vector512.Create(m) & bits, bits);
                Avx512F.CompressStore(to, lanes, Avx512F.MaskLoad(at, lanes, Vector512<int>.Zero));
                to += BitOperations.PopCount((uint)m);
            }
        }
    }

    // AVX2, 4 lanes of 8 bytes per step, a nibble of the mask each: the step's lanes are loaded whole when the source
    // holds them (masked past its end, so nothing beyond it is read), moved to the front by one permutation of 32-bit
    // pairs (Pairs8) and stored whole, while the destination has room for 4 more; the last selected elements (3 or
    // fewer) are then written one per set bit, so nothing past the selected count is written. No branch in the vector
    // steps depends on the mask's bits: a step whose nibble is 0 stores nothing it keeps (the next step writes the same
    // places), so a mask of mixed bits mispredicts nothing. The source and destination are pinned once per call (they
    // may move; the loop takes no pin).
    private static unsafe void Permute8(ref long from, int length, ulong mask, ref long into, int count)
    {
        var lanes = Vector256.Create(1L, 2, 4, 8);
        byte* table = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Pairs8)); // assembly data: never moves
        fixed (long* source = &from, destination = &into)
        {
            long* to = destination, last = destination + count - 4;
            int at = 0;
            for (; mask != 0 && to <= last; at += 4, mask >>= 4)
            {
                int m = (int)(mask & 0xF);
                var values = at + 4 <= length
                    ? Avx.LoadVector256(source + at)
                    : Avx2.MaskLoad(source + at, Vector256.Equals(Vector256.Create((long)m) & lanes, lanes));
                Avx.Store(to, Avx2.PermuteVar8x32(values.AsInt32(), Avx2.ConvertToVector256Int32(table + m * 8)).AsInt64());
                to += BitOperations.PopCount((uint)m);
            }

            if (mask != 0) Visit(ref Unsafe.Add(ref from, at), mask, ref Unsafe.AsRef<long>(to));
        }
    }

    // AVX2, 8 lanes of 4 bytes per step, a byte of the mask each, as Permute8 (Lanes8: one permutation per byte).
    private static unsafe void Permute4(ref int from, int length, ulong mask, ref int into, int count)
    {
        var lanes = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        byte* table = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(Lanes8)); // assembly data: never moves
        fixed (int* source = &from, destination = &into)
        {
            int* to = destination, last = destination + count - 8;
            int at = 0;
            for (; mask != 0 && to <= last; at += 8, mask >>= 8)
            {
                int m = (int)(mask & 0xFF);
                var values = at + 8 <= length
                    ? Avx.LoadVector256(source + at)
                    : Avx2.MaskLoad(source + at, Vector256.Equals(Vector256.Create(m) & lanes, lanes));
                Avx.Store(to, Avx2.PermuteVar8x32(values, Avx2.ConvertToVector256Int32(table + m * 8)));
                to += BitOperations.PopCount((uint)m);
            }

            if (mask != 0) Visit(ref Unsafe.Add(ref from, at), mask, ref Unsafe.AsRef<int>(to));
        }
    }

    // Whether a byte shuffle with indices from memory is one instruction: TBL (ARM64) or PSHUFB (SSSE3).
    private static bool Shuffles => AdvSimd.Arm64.IsSupported || Ssse3.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> values, Vector128<byte> indices) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.VectorTableLookup(values, indices)
        : Ssse3.IsSupported ? Ssse3.Shuffle(values, indices)
        : Vector128.Shuffle(values, indices);

    // 128-bit vectors, 4 lanes of 4 bytes per step, a nibble of the mask each: the step's lanes loaded whole when the
    // source holds them (else only the elements before its end, so nothing beyond it is read), moved to the front by
    // one byte shuffle (Bytes4) and stored whole while the destination has room for 4 more, then the last selected
    // ones (3 or fewer) one per set bit, as Permute8. A partial store per step at the destination's end (a copy of a
    // variable length) made the steps after the first selected few cost a call each at low density. Branch-free per
    // step: at 50 % of the rows selected, a skip of empty nibbles or of the identity shuffle mispredicted enough to
    // make the loop slower than the scalar walk.
    private static void Shuffle4(ref int from, int length, ulong mask, ref int into, int count)
    {
        ref byte table = ref MemoryMarshal.GetReference(Bytes4);
        int to = 0, at = 0;
        for (; mask != 0 && to <= count - 4; at += 4, mask >>= 4)
        {
            int m = (int)(mask & 0xF);
            var values = at + 4 <= length ? Vector128.LoadUnsafe(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref from, at))) : Tail(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref from, at)), (length - at) * 4);
            Shuffle(values, Vector128.LoadUnsafe(ref table, (nuint)(m * 16))).StoreUnsafe(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref into, to)));
            to += BitOperations.PopCount((uint)m);
        }

        if (mask != 0) Visit(ref Unsafe.Add(ref from, at), mask, ref Unsafe.Add(ref into, to));
    }

    // The first bytes of a vector at the source's end (the rest 0): nothing past the source is read.
    private static Vector128<byte> Tail(ref byte from, int bytes)
    {
        var values = Vector128<byte>.Zero;
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<Vector128<byte>, byte>(ref values), ref from, (uint)bytes);
        return values;
    }

    // The tables are literal data in the assembly (RVA): nothing to initialize, nothing that moves, so under Native AOT
    // a loop reading them checks no class initialization (a static readonly array would).

    // Per nibble of a mask, the bytes of the 32-bit lanes it selects first (the rest 0x80: zero). 256 bytes. Lanes of 8
    // bytes have no 128-bit form: 2 per step (32 steps a word) lost to the scalar walk (CompactBenchmark without AVX2,
    // 1M ids at 50 %: 1.19 ms against 1.00 ms).
    private static ReadOnlySpan<byte> Bytes4 =>
    [
        128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        4, 5, 6, 7, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 4, 5, 6, 7, 128, 128, 128, 128, 128, 128, 128, 128,
        8, 9, 10, 11, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 8, 9, 10, 11, 128, 128, 128, 128, 128, 128, 128, 128,
        4, 5, 6, 7, 8, 9, 10, 11, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 128, 128, 128, 128,
        12, 13, 14, 15, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 12, 13, 14, 15, 128, 128, 128, 128, 128, 128, 128, 128,
        4, 5, 6, 7, 12, 13, 14, 15, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 4, 5, 6, 7, 12, 13, 14, 15, 128, 128, 128, 128,
        8, 9, 10, 11, 12, 13, 14, 15, 128, 128, 128, 128, 128, 128, 128, 128,
        0, 1, 2, 3, 8, 9, 10, 11, 12, 13, 14, 15, 128, 128, 128, 128,
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 128, 128, 128, 128,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    // Per byte of a mask, the 32-bit lanes it selects first, in order (the rest 0), a byte each: PermuteVar8x32's
    // indices once widened (VPMOVZXBD loads and widens in one instruction). 2 KiB.
    private static ReadOnlySpan<byte> Lanes8 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
        2, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0,
        3, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0,
        2, 3, 0, 0, 0, 0, 0, 0, 0, 2, 3, 0, 0, 0, 0, 0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 1, 2, 3, 0, 0, 0, 0,
        4, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 1, 4, 0, 0, 0, 0, 0, 0, 0, 1, 4, 0, 0, 0, 0, 0,
        2, 4, 0, 0, 0, 0, 0, 0, 0, 2, 4, 0, 0, 0, 0, 0, 1, 2, 4, 0, 0, 0, 0, 0, 0, 1, 2, 4, 0, 0, 0, 0,
        3, 4, 0, 0, 0, 0, 0, 0, 0, 3, 4, 0, 0, 0, 0, 0, 1, 3, 4, 0, 0, 0, 0, 0, 0, 1, 3, 4, 0, 0, 0, 0,
        2, 3, 4, 0, 0, 0, 0, 0, 0, 2, 3, 4, 0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0,
        5, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 1, 5, 0, 0, 0, 0, 0, 0, 0, 1, 5, 0, 0, 0, 0, 0,
        2, 5, 0, 0, 0, 0, 0, 0, 0, 2, 5, 0, 0, 0, 0, 0, 1, 2, 5, 0, 0, 0, 0, 0, 0, 1, 2, 5, 0, 0, 0, 0,
        3, 5, 0, 0, 0, 0, 0, 0, 0, 3, 5, 0, 0, 0, 0, 0, 1, 3, 5, 0, 0, 0, 0, 0, 0, 1, 3, 5, 0, 0, 0, 0,
        2, 3, 5, 0, 0, 0, 0, 0, 0, 2, 3, 5, 0, 0, 0, 0, 1, 2, 3, 5, 0, 0, 0, 0, 0, 1, 2, 3, 5, 0, 0, 0,
        4, 5, 0, 0, 0, 0, 0, 0, 0, 4, 5, 0, 0, 0, 0, 0, 1, 4, 5, 0, 0, 0, 0, 0, 0, 1, 4, 5, 0, 0, 0, 0,
        2, 4, 5, 0, 0, 0, 0, 0, 0, 2, 4, 5, 0, 0, 0, 0, 1, 2, 4, 5, 0, 0, 0, 0, 0, 1, 2, 4, 5, 0, 0, 0,
        3, 4, 5, 0, 0, 0, 0, 0, 0, 3, 4, 5, 0, 0, 0, 0, 1, 3, 4, 5, 0, 0, 0, 0, 0, 1, 3, 4, 5, 0, 0, 0,
        2, 3, 4, 5, 0, 0, 0, 0, 0, 2, 3, 4, 5, 0, 0, 0, 1, 2, 3, 4, 5, 0, 0, 0, 0, 1, 2, 3, 4, 5, 0, 0,
        6, 0, 0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 1, 6, 0, 0, 0, 0, 0, 0, 0, 1, 6, 0, 0, 0, 0, 0,
        2, 6, 0, 0, 0, 0, 0, 0, 0, 2, 6, 0, 0, 0, 0, 0, 1, 2, 6, 0, 0, 0, 0, 0, 0, 1, 2, 6, 0, 0, 0, 0,
        3, 6, 0, 0, 0, 0, 0, 0, 0, 3, 6, 0, 0, 0, 0, 0, 1, 3, 6, 0, 0, 0, 0, 0, 0, 1, 3, 6, 0, 0, 0, 0,
        2, 3, 6, 0, 0, 0, 0, 0, 0, 2, 3, 6, 0, 0, 0, 0, 1, 2, 3, 6, 0, 0, 0, 0, 0, 1, 2, 3, 6, 0, 0, 0,
        4, 6, 0, 0, 0, 0, 0, 0, 0, 4, 6, 0, 0, 0, 0, 0, 1, 4, 6, 0, 0, 0, 0, 0, 0, 1, 4, 6, 0, 0, 0, 0,
        2, 4, 6, 0, 0, 0, 0, 0, 0, 2, 4, 6, 0, 0, 0, 0, 1, 2, 4, 6, 0, 0, 0, 0, 0, 1, 2, 4, 6, 0, 0, 0,
        3, 4, 6, 0, 0, 0, 0, 0, 0, 3, 4, 6, 0, 0, 0, 0, 1, 3, 4, 6, 0, 0, 0, 0, 0, 1, 3, 4, 6, 0, 0, 0,
        2, 3, 4, 6, 0, 0, 0, 0, 0, 2, 3, 4, 6, 0, 0, 0, 1, 2, 3, 4, 6, 0, 0, 0, 0, 1, 2, 3, 4, 6, 0, 0,
        5, 6, 0, 0, 0, 0, 0, 0, 0, 5, 6, 0, 0, 0, 0, 0, 1, 5, 6, 0, 0, 0, 0, 0, 0, 1, 5, 6, 0, 0, 0, 0,
        2, 5, 6, 0, 0, 0, 0, 0, 0, 2, 5, 6, 0, 0, 0, 0, 1, 2, 5, 6, 0, 0, 0, 0, 0, 1, 2, 5, 6, 0, 0, 0,
        3, 5, 6, 0, 0, 0, 0, 0, 0, 3, 5, 6, 0, 0, 0, 0, 1, 3, 5, 6, 0, 0, 0, 0, 0, 1, 3, 5, 6, 0, 0, 0,
        2, 3, 5, 6, 0, 0, 0, 0, 0, 2, 3, 5, 6, 0, 0, 0, 1, 2, 3, 5, 6, 0, 0, 0, 0, 1, 2, 3, 5, 6, 0, 0,
        4, 5, 6, 0, 0, 0, 0, 0, 0, 4, 5, 6, 0, 0, 0, 0, 1, 4, 5, 6, 0, 0, 0, 0, 0, 1, 4, 5, 6, 0, 0, 0,
        2, 4, 5, 6, 0, 0, 0, 0, 0, 2, 4, 5, 6, 0, 0, 0, 1, 2, 4, 5, 6, 0, 0, 0, 0, 1, 2, 4, 5, 6, 0, 0,
        3, 4, 5, 6, 0, 0, 0, 0, 0, 3, 4, 5, 6, 0, 0, 0, 1, 3, 4, 5, 6, 0, 0, 0, 0, 1, 3, 4, 5, 6, 0, 0,
        2, 3, 4, 5, 6, 0, 0, 0, 0, 2, 3, 4, 5, 6, 0, 0, 1, 2, 3, 4, 5, 6, 0, 0, 0, 1, 2, 3, 4, 5, 6, 0,
        7, 0, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0, 0, 0, 1, 7, 0, 0, 0, 0, 0,
        2, 7, 0, 0, 0, 0, 0, 0, 0, 2, 7, 0, 0, 0, 0, 0, 1, 2, 7, 0, 0, 0, 0, 0, 0, 1, 2, 7, 0, 0, 0, 0,
        3, 7, 0, 0, 0, 0, 0, 0, 0, 3, 7, 0, 0, 0, 0, 0, 1, 3, 7, 0, 0, 0, 0, 0, 0, 1, 3, 7, 0, 0, 0, 0,
        2, 3, 7, 0, 0, 0, 0, 0, 0, 2, 3, 7, 0, 0, 0, 0, 1, 2, 3, 7, 0, 0, 0, 0, 0, 1, 2, 3, 7, 0, 0, 0,
        4, 7, 0, 0, 0, 0, 0, 0, 0, 4, 7, 0, 0, 0, 0, 0, 1, 4, 7, 0, 0, 0, 0, 0, 0, 1, 4, 7, 0, 0, 0, 0,
        2, 4, 7, 0, 0, 0, 0, 0, 0, 2, 4, 7, 0, 0, 0, 0, 1, 2, 4, 7, 0, 0, 0, 0, 0, 1, 2, 4, 7, 0, 0, 0,
        3, 4, 7, 0, 0, 0, 0, 0, 0, 3, 4, 7, 0, 0, 0, 0, 1, 3, 4, 7, 0, 0, 0, 0, 0, 1, 3, 4, 7, 0, 0, 0,
        2, 3, 4, 7, 0, 0, 0, 0, 0, 2, 3, 4, 7, 0, 0, 0, 1, 2, 3, 4, 7, 0, 0, 0, 0, 1, 2, 3, 4, 7, 0, 0,
        5, 7, 0, 0, 0, 0, 0, 0, 0, 5, 7, 0, 0, 0, 0, 0, 1, 5, 7, 0, 0, 0, 0, 0, 0, 1, 5, 7, 0, 0, 0, 0,
        2, 5, 7, 0, 0, 0, 0, 0, 0, 2, 5, 7, 0, 0, 0, 0, 1, 2, 5, 7, 0, 0, 0, 0, 0, 1, 2, 5, 7, 0, 0, 0,
        3, 5, 7, 0, 0, 0, 0, 0, 0, 3, 5, 7, 0, 0, 0, 0, 1, 3, 5, 7, 0, 0, 0, 0, 0, 1, 3, 5, 7, 0, 0, 0,
        2, 3, 5, 7, 0, 0, 0, 0, 0, 2, 3, 5, 7, 0, 0, 0, 1, 2, 3, 5, 7, 0, 0, 0, 0, 1, 2, 3, 5, 7, 0, 0,
        4, 5, 7, 0, 0, 0, 0, 0, 0, 4, 5, 7, 0, 0, 0, 0, 1, 4, 5, 7, 0, 0, 0, 0, 0, 1, 4, 5, 7, 0, 0, 0,
        2, 4, 5, 7, 0, 0, 0, 0, 0, 2, 4, 5, 7, 0, 0, 0, 1, 2, 4, 5, 7, 0, 0, 0, 0, 1, 2, 4, 5, 7, 0, 0,
        3, 4, 5, 7, 0, 0, 0, 0, 0, 3, 4, 5, 7, 0, 0, 0, 1, 3, 4, 5, 7, 0, 0, 0, 0, 1, 3, 4, 5, 7, 0, 0,
        2, 3, 4, 5, 7, 0, 0, 0, 0, 2, 3, 4, 5, 7, 0, 0, 1, 2, 3, 4, 5, 7, 0, 0, 0, 1, 2, 3, 4, 5, 7, 0,
        6, 7, 0, 0, 0, 0, 0, 0, 0, 6, 7, 0, 0, 0, 0, 0, 1, 6, 7, 0, 0, 0, 0, 0, 0, 1, 6, 7, 0, 0, 0, 0,
        2, 6, 7, 0, 0, 0, 0, 0, 0, 2, 6, 7, 0, 0, 0, 0, 1, 2, 6, 7, 0, 0, 0, 0, 0, 1, 2, 6, 7, 0, 0, 0,
        3, 6, 7, 0, 0, 0, 0, 0, 0, 3, 6, 7, 0, 0, 0, 0, 1, 3, 6, 7, 0, 0, 0, 0, 0, 1, 3, 6, 7, 0, 0, 0,
        2, 3, 6, 7, 0, 0, 0, 0, 0, 2, 3, 6, 7, 0, 0, 0, 1, 2, 3, 6, 7, 0, 0, 0, 0, 1, 2, 3, 6, 7, 0, 0,
        4, 6, 7, 0, 0, 0, 0, 0, 0, 4, 6, 7, 0, 0, 0, 0, 1, 4, 6, 7, 0, 0, 0, 0, 0, 1, 4, 6, 7, 0, 0, 0,
        2, 4, 6, 7, 0, 0, 0, 0, 0, 2, 4, 6, 7, 0, 0, 0, 1, 2, 4, 6, 7, 0, 0, 0, 0, 1, 2, 4, 6, 7, 0, 0,
        3, 4, 6, 7, 0, 0, 0, 0, 0, 3, 4, 6, 7, 0, 0, 0, 1, 3, 4, 6, 7, 0, 0, 0, 0, 1, 3, 4, 6, 7, 0, 0,
        2, 3, 4, 6, 7, 0, 0, 0, 0, 2, 3, 4, 6, 7, 0, 0, 1, 2, 3, 4, 6, 7, 0, 0, 0, 1, 2, 3, 4, 6, 7, 0,
        5, 6, 7, 0, 0, 0, 0, 0, 0, 5, 6, 7, 0, 0, 0, 0, 1, 5, 6, 7, 0, 0, 0, 0, 0, 1, 5, 6, 7, 0, 0, 0,
        2, 5, 6, 7, 0, 0, 0, 0, 0, 2, 5, 6, 7, 0, 0, 0, 1, 2, 5, 6, 7, 0, 0, 0, 0, 1, 2, 5, 6, 7, 0, 0,
        3, 5, 6, 7, 0, 0, 0, 0, 0, 3, 5, 6, 7, 0, 0, 0, 1, 3, 5, 6, 7, 0, 0, 0, 0, 1, 3, 5, 6, 7, 0, 0,
        2, 3, 5, 6, 7, 0, 0, 0, 0, 2, 3, 5, 6, 7, 0, 0, 1, 2, 3, 5, 6, 7, 0, 0, 0, 1, 2, 3, 5, 6, 7, 0,
        4, 5, 6, 7, 0, 0, 0, 0, 0, 4, 5, 6, 7, 0, 0, 0, 1, 4, 5, 6, 7, 0, 0, 0, 0, 1, 4, 5, 6, 7, 0, 0,
        2, 4, 5, 6, 7, 0, 0, 0, 0, 2, 4, 5, 6, 7, 0, 0, 1, 2, 4, 5, 6, 7, 0, 0, 0, 1, 2, 4, 5, 6, 7, 0,
        3, 4, 5, 6, 7, 0, 0, 0, 0, 3, 4, 5, 6, 7, 0, 0, 1, 3, 4, 5, 6, 7, 0, 0, 0, 1, 3, 4, 5, 6, 7, 0,
        2, 3, 4, 5, 6, 7, 0, 0, 0, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 1, 2, 3, 4, 5, 6, 7,
    ];

    // Per nibble of a mask, the 64-bit lanes it selects first as pairs of 32-bit lanes, a byte each. 128 bytes.
    private static ReadOnlySpan<byte> Pairs8 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0,
        2, 3, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 0, 0, 0, 0,
        4, 5, 0, 0, 0, 0, 0, 0, 0, 1, 4, 5, 0, 0, 0, 0,
        2, 3, 4, 5, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 0, 0,
        6, 7, 0, 0, 0, 0, 0, 0, 0, 1, 6, 7, 0, 0, 0, 0,
        2, 3, 6, 7, 0, 0, 0, 0, 0, 1, 2, 3, 6, 7, 0, 0,
        4, 5, 6, 7, 0, 0, 0, 0, 0, 1, 4, 5, 6, 7, 0, 0,
        2, 3, 4, 5, 6, 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7,
    ];

    // Few selected: one step per set bit.
    private static void Visit<T>(ref T from, ulong mask, ref T into)
    {
        for (nint n = 0; mask != 0; mask &= mask - 1, n++)
            Unsafe.Add(ref into, n) = Unsafe.Add(ref from, BitOperations.TrailingZeroCount(mask));
    }

    // Many selected: every element up to the last selected one is stored at the next free place, which advances by the
    // element's bit. The invariant that keeps it inside the destination: before the last selected element, fewer than
    // PopCount(mask) were selected, so the place written is below PopCount(mask).
    private static void Advance<T>(ref T from, ulong mask, ref T into)
    {
        int end = 64 - BitOperations.LeadingZeroCount(mask);
        nint n = 0;
        for (int i = 0; i < end; i++)
        {
            Unsafe.Add(ref into, n) = Unsafe.Add(ref from, i);
            n += (nint)((mask >> i) & 1);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutside(int source, int destination, int count) =>
        throw new ArgumentException(destination < count
            ? $"The mask selects {count} elements; the destination holds {destination}"
            : $"The mask selects elements at or past the end of the source ({source} elements)");

    /// <summary>
    /// The lane mask of a vector loop's condition as bits, lane j in bit j (all bits set where true): the rows a
    /// generated loop marks or emits for. Generated code uses this.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bits(Vector<int> mask) => Vector<byte>.Count switch
    {
        64 => mask.AsVector512().ExtractMostSignificantBits(),
        32 => mask.AsVector256().ExtractMostSignificantBits(),
        _ => mask.AsVector128().ExtractMostSignificantBits(),
    };

    /// <inheritdoc cref="Bits(Vector{int})"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bits(Vector<long> mask) => Vector<byte>.Count switch
    {
        64 => mask.AsVector512().ExtractMostSignificantBits(),
        32 => mask.AsVector256().ExtractMostSignificantBits(),
        _ => mask.AsVector128().ExtractMostSignificantBits(),
    };

    /// <inheritdoc cref="Bits(Vector{int})"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bits(Vector512<int> mask) => mask.ExtractMostSignificantBits();

    /// <inheritdoc cref="Bits(Vector{int})"/>
    [EditorBrowsable(EditorBrowsableState.Never), MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bits(Vector512<long> mask) => mask.ExtractMostSignificantBits();
}
