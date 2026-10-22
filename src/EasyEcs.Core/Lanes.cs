using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// Stream compaction for batch methods, a GPU's ballot and compaction on a CPU: a vector compare decides a lane mask
/// (<c>Vector256.LessThan(hp, limit).ExtractMostSignificantBits()</c>: CUDA's <c>__ballot_sync</c>; its
/// <c>__popc</c> is <see cref="BitOperations.PopCount(ulong)"/>), and <see cref="Compact{T}"/> writes the selected
/// elements side by side: the ids of the rows a batch method picked (<c>entities.Ids</c>), their values, their
/// indexes. With AVX-512 a lane of 4 or 8 bytes moves 16 or 8 at a time (<c>VPCOMPRESSD</c> / <c>VPCOMPRESSQ</c>);
/// otherwise, and for other sizes, one at a time without a branch per element.
/// </summary>
public static class Lanes
{
    // Up to this many selected elements, one step per set bit beats the vector steps up to the last one.
    private const int Few = 4;

    // Below this many selected elements of 64, the scalar form visits the set bits (TZCNT); above, it stores every
    // element up to the last selected one and advances by its bit, which no misprediction slows.
    private const int Sparse = 12;

    /// <summary>
    /// Write the elements of <paramref name="source"/> whose bit is set in <paramref name="mask"/> (bit <c>i</c> selects
    /// <c>source[i]</c>, so at most 64 elements) to the start of <paramref name="destination"/>, in order, and return
    /// how many: <c>PopCount(mask)</c>. Only selected elements are read and only that many are written. O(64 / lanes
    /// per vector) with AVX-512 for elements of 4 or 8 bytes, else O(selected); allocation-free.
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
        else Advance(ref from, mask, ref into);
        return count;
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
}
