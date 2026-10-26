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
/// SSSE3's <c>PSHUFB</c>) 4 or 2 at a time through a byte shuffle table; otherwise, and for other sizes, one at a time
/// without a branch per element.
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
    /// how many: <c>PopCount(mask)</c>. Only selected elements are read and only that many are written. O(64 / lanes
    /// per vector) for elements of 4 or 8 bytes with AVX-512, AVX2, SSSE3 or ARM64's NEON, else O(selected);
    /// allocation-free.
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
        else if (Unsafe.SizeOf<T>() == 8 && Shuffles) Shuffle8(ref Unsafe.As<T, long>(ref from), source.Length, mask, ref Unsafe.As<T, long>(ref into), count);
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
    public static int Rows(ulong mask, int first, Span<int> destination)
    {
        int n = Compact<int>(Numbers, mask, destination);
        if (first != 0 && n != 0) Offset(destination.Slice(0, n), first);
        return n;
    }

    // 0 to 63: the rows of a word Rows compacts.
    private static readonly int[] Numbers = CreateNumbers();

    private static int[] CreateNumbers()
    {
        var numbers = new int[64];
        for (int i = 0; i < 64; i++) numbers[i] = i;
        return numbers;
    }

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
    // pairs (Pairs8), and stored whole while the destination has room for 4 more, else masked (only the selected
    // lanes reach the destination's last places). No branch depends on the mask's bits: a step whose nibble is 0
    // stores nothing it keeps (the next step writes the same places), so a mask of mixed bits mispredicts nothing.
    private static unsafe void Permute8(ref long from, int length, ulong mask, ref long into, int count)
    {
        var lanes = Vector256.Create(1L, 2, 4, 8);
        fixed (long* source = &from, destination = &into)
        fixed (int* table = Pairs8)
        {
            long* to = destination, end = destination + count;
            for (int at = 0; mask != 0; at += 4, mask >>= 4)
            {
                int m = (int)(mask & 0xF);
                var values = at + 4 <= length
                    ? Avx.LoadVector256(source + at)
                    : Avx2.MaskLoad(source + at, Vector256.Equals(Vector256.Create((long)m) & lanes, lanes));
                var packed = Avx2.PermuteVar8x32(values.AsInt32(), Avx.LoadVector256(table + m * 8)).AsInt64();
                int n = BitOperations.PopCount((uint)m);
                if (end - to >= 4) Avx.Store(to, packed);
                else Avx2.MaskStore(to, Vector256.LessThan(Vector256.Create(0L, 1, 2, 3), Vector256.Create((long)n)), packed);
                to += n;
            }
        }
    }

    // AVX2, 8 lanes of 4 bytes per step, a byte of the mask each, as Permute8 (Lanes8: one permutation per byte).
    private static unsafe void Permute4(ref int from, int length, ulong mask, ref int into, int count)
    {
        var lanes = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
        fixed (int* source = &from, destination = &into)
        fixed (int* table = Lanes8)
        {
            int* to = destination, end = destination + count;
            for (int at = 0; mask != 0; at += 8, mask >>= 8)
            {
                int m = (int)(mask & 0xFF);
                var values = at + 8 <= length
                    ? Avx.LoadVector256(source + at)
                    : Avx2.MaskLoad(source + at, Vector256.Equals(Vector256.Create(m) & lanes, lanes));
                var packed = Avx2.PermuteVar8x32(values, Avx.LoadVector256(table + m * 8));
                int n = BitOperations.PopCount((uint)m);
                if (end - to >= 8) Avx.Store(to, packed);
                else Avx2.MaskStore(to, Vector256.LessThan(Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7), Vector256.Create(n)), packed);
                to += n;
            }
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
    // one byte shuffle (Bytes4), stored whole while the destination has room for 4 more, else only the selected ones.
    // Branch-free per step as Permute8: at 50 % of the rows selected, a skip of empty nibbles or of the identity
    // shuffle mispredicted enough to make the loop slower than the scalar walk.
    private static void Shuffle4(ref int from, int length, ulong mask, ref int into, int count)
    {
        ref byte table = ref MemoryMarshal.GetArrayDataReference(Bytes4);
        int to = 0;
        for (int at = 0; mask != 0; at += 4, mask >>= 4)
        {
            int m = (int)(mask & 0xF);
            var values = at + 4 <= length ? Vector128.LoadUnsafe(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref from, at))) : Tail(ref Unsafe.As<int, byte>(ref Unsafe.Add(ref from, at)), (length - at) * 4);
            var packed = Shuffle(values, Vector128.LoadUnsafe(ref table, (nuint)(m * 16)));
            int n = BitOperations.PopCount((uint)m);
            ref byte place = ref Unsafe.As<int, byte>(ref Unsafe.Add(ref into, to));
            if (count - to >= 4) packed.StoreUnsafe(ref place);
            else Store(packed, ref place, n * 4);
            to += n;
        }
    }

    // 128-bit vectors, 2 lanes of 8 bytes per step, 2 bits of the mask each, as Shuffle4 (Bytes2).
    private static void Shuffle8(ref long from, int length, ulong mask, ref long into, int count)
    {
        ref byte table = ref MemoryMarshal.GetArrayDataReference(Bytes2);
        int to = 0;
        for (int at = 0; mask != 0; at += 2, mask >>= 2)
        {
            int m = (int)(mask & 0x3);
            var values = at + 2 <= length ? Vector128.LoadUnsafe(ref Unsafe.As<long, byte>(ref Unsafe.Add(ref from, at))) : Tail(ref Unsafe.As<long, byte>(ref Unsafe.Add(ref from, at)), 8);
            var packed = Shuffle(values, Vector128.LoadUnsafe(ref table, (nuint)(m * 16)));
            int n = BitOperations.PopCount((uint)m);
            ref byte place = ref Unsafe.As<long, byte>(ref Unsafe.Add(ref into, to));
            if (count - to >= 2) packed.StoreUnsafe(ref place);
            else Store(packed, ref place, n * 8);
            to += n;
        }
    }

    // The first bytes of a vector at the source's end (the rest 0): nothing past the source is read.
    private static Vector128<byte> Tail(ref byte from, int bytes)
    {
        var values = Vector128<byte>.Zero;
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<Vector128<byte>, byte>(ref values), ref from, (uint)bytes);
        return values;
    }

    // The first bytes of a vector at the destination's end: nothing past the selected elements is written.
    private static void Store(Vector128<byte> values, ref byte into, int bytes) =>
        Unsafe.CopyBlockUnaligned(ref into, ref Unsafe.As<Vector128<byte>, byte>(ref values), (uint)bytes);

    // Per nibble of a mask, the bytes of the 32-bit lanes it selects first (the rest 0x80: zero). 256 bytes.
    private static readonly byte[] Bytes4 = ByteTable(4, 4);

    // Per 2 bits of a mask, the bytes of the 64-bit lanes it selects first. 64 bytes.
    private static readonly byte[] Bytes2 = ByteTable(2, 8);

    private static byte[] ByteTable(int lanes, int width)
    {
        var table = new byte[(1 << lanes) * 16];
        table.AsSpan().Fill(0x80);
        for (int mask = 0; mask < 1 << lanes; mask++)
            for (int lane = 0, n = 0; lane < lanes; lane++)
                if ((mask >> lane & 1) != 0)
                {
                    for (int part = 0; part < width; part++) table[mask * 16 + n * width + part] = (byte)(lane * width + part);
                    n++;
                }

        return table;
    }

    // Per byte of a mask, the 32-bit lanes it selects first, in order (the rest 0): PermuteVar8x32's indices. 8 KiB.
    private static readonly int[] Lanes8 = Table(8, 1);

    // Per nibble of a mask, the 64-bit lanes it selects first as pairs of 32-bit lanes. 512 bytes.
    private static readonly int[] Pairs8 = Table(4, 2);

    private static int[] Table(int lanes, int width)
    {
        var table = new int[(1 << lanes) * 8];
        for (int mask = 0; mask < 1 << lanes; mask++)
            for (int lane = 0, n = 0; lane < lanes; lane++)
                if ((mask >> lane & 1) != 0)
                {
                    for (int part = 0; part < width; part++) table[mask * 8 + n * width + part] = lane * width + part;
                    n++;
                }

        return table;
    }

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
