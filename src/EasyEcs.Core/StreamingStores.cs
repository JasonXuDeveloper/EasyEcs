using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The non-temporal stores of a generated vector loop (microarch.md candidate 8): a column the method writes without
/// reading (<c>heat = new Heat(speed.Value * k)</c>) is written around the caches, whole lines through the
/// write-combining buffers, instead of read into the cache first (read for ownership) and evicting what is there. The
/// generator calls them; a [ForEach] method never does. They pay only when the pass's columns are past the last-level
/// cache (<see cref="Pays"/>, once per run): there a plain store reads every line it writes, and the line is gone again
/// before the next pass reads it (StreamingStoreBenchmark: 1.47x past the cache, a tie inside). In the cache they would
/// evict what the next pass reads, so the loop keeps plain stores.
/// </summary>
/// <remarks>
/// x86: <c>VMOVNTPS</c> of 64, 32 or 16 bytes (AVX-512, AVX, SSE2), which faults on an address that is not aligned
/// to its width, so each store checks its address and stores plainly where it is not (a table's columns start on a
/// cache line, and the loop runs the rows before the first aligned one through the method: <see cref="Head{T}"/>).
/// ARM64: <c>STNP</c> of the vector's two halves (no alignment needed). Elsewhere (WebAssembly) a plain store, and
/// <see cref="IsSupported"/> is false, so the loop never takes this form. The stores of a run are fenced after its loop
/// (<see cref="Fence"/>), so the pass's writes are ordered before whatever follows it, as plain stores are.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class StreamingStores
{
    /// <summary>
    /// The fewest rows a run streams: the head (up to a vector of rows through the method) and the fence after the
    /// loop (a drain of the write-combining buffers, some hundred cycles) amortize over at least this many.
    /// </summary>
    public const int MinRows = 1024;

    /// <summary>Whether this machine has a non-temporal store (a JIT-time constant): x86, ARM64.</summary>
    public static bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    /// <summary>
    /// Whether a run of <paramref name="rows"/> rows of <paramref name="entities"/>'s table streams its write-only
    /// columns: the machine has the stores, the run has at least <see cref="MinRows"/> rows, and the table's rows of
    /// the columns the pass moves (<paramref name="rowBytes"/> per row, read and written) are past the last-level cache
    /// (<c>CacheSizes.LastLevel</c>; never when it is unknown). Then no line the pass writes is still in the cache when
    /// the next pass reads it, and a plain store only adds the read of each line. Once per run: O(1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Pays(in EntitySpan entities, nint rows, int rowBytes)
    {
        long cache = CacheSizes.LastLevel;
        return IsSupported && rows >= MinRows && cache > 0 && entities.Table is { } table && (long)table.Count * rowBytes > cache;
    }

    /// <summary>
    /// The rows from lane <paramref name="lane"/> of <paramref name="column"/> (rows of <paramref name="lanesPerRow"/>
    /// lanes) up to the first row whose address is a multiple of <paramref name="vectorBytes"/>, where the loop's
    /// non-temporal stores start; 0 when no row within one vector's bytes is (a column that does not start on a cache
    /// line: every store then checks and stores plainly). The column is pinned (a table's unmanaged column), so its
    /// address holds. O(<paramref name="vectorBytes"/>) at most, once per run.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nint Head<T>(ref T column, nint lane, int lanesPerRow, int vectorBytes)
    {
        nuint at = (nuint)Unsafe.AsPointer(ref Unsafe.Add(ref column, lane)), mask = (nuint)vectorBytes - 1;
        nuint rowBytes = (nuint)(Unsafe.SizeOf<T>() * lanesPerRow);
        for (nint row = 0; row < vectorBytes; row++)
            if (((at + (nuint)row * rowBytes) & mask) == 0) return row;
        return 0;
    }

    /// <summary>
    /// <paramref name="value"/> into lanes <paramref name="lane"/>.. of <paramref name="column"/> around the caches
    /// (AVX-512's 64-byte <c>VMOVNTPS</c>), plainly where the address is not aligned or the machine has no such store.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store<T>(Vector512<T> value, ref T column, nuint lane)
    {
        ref T at = ref Unsafe.Add(ref column, lane);
        byte* address = (byte*)Unsafe.AsPointer(ref at);
        if (Avx512F.IsSupported && ((nuint)address & 63) == 0) Avx512F.StoreAlignedNonTemporal(address, value.AsByte());
        else value.StoreUnsafe(ref at);
    }

    /// <summary>
    /// <paramref name="value"/> into lanes <paramref name="lane"/>.. of <paramref name="column"/> around the caches: x86's
    /// non-temporal store of <c>Vector&lt;T&gt;</c>'s width where the address is aligned to it, ARM64's <c>STNP</c> of its
    /// two halves, a plain store otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store<T>(Vector<T> value, ref T column, nuint lane)
    {
        ref T at = ref Unsafe.Add(ref column, lane);
        byte* address = (byte*)Unsafe.AsPointer(ref at);
        if (Vector<byte>.Count == 32 && Avx.IsSupported && ((nuint)address & 31) == 0)
            Avx.StoreAlignedNonTemporal(address, value.AsVector256().AsByte());
        else if (Vector<byte>.Count == 64 && Avx512F.IsSupported && ((nuint)address & 63) == 0)
            Avx512F.StoreAlignedNonTemporal(address, value.AsVector512().AsByte());
        else if (Vector<byte>.Count == 16 && Sse2.IsSupported && ((nuint)address & 15) == 0)
            Sse2.StoreAlignedNonTemporal(address, value.AsVector128().AsByte());
        else if (Vector<byte>.Count == 16 && AdvSimd.Arm64.IsSupported)
        {
            var halves = value.AsVector128().AsUInt64();
            AdvSimd.Arm64.StorePairNonTemporal((ulong*)address, halves.GetLower(), halves.GetUpper());
        }
        else value.StoreUnsafe(ref at);
    }

    /// <summary>
    /// Orders the non-temporal stores of a run before every later memory access, as plain stores are ordered: x86's
    /// <c>SFENCE</c> (non-temporal stores are weakly ordered), a full barrier on ARM64. Once per run.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fence()
    {
        if (Sse.IsSupported) Sse.StoreFence();
        else if (AdvSimd.Arm64.IsSupported) Interlocked.MemoryBarrier();
    }

    /// <summary>
    /// Whether an operation that copies <paramref name="bytes"/> bytes in all (every column of a table a capture,
    /// restore or resize copies) streams its stores (<see cref="Copy(ref byte, ref byte, nuint)"/>): the machine has AVX (VMOVNTPS of 32 or 64
    /// bytes) and the bytes are past the last-level cache (<c>CacheSizes.LastLevel</c>; never when it is unknown). Then
    /// none of the target's lines is still in the cache when the copy ends, so reading each line before writing it
    /// (read for ownership) is wasted bandwidth. O(1).
    /// </summary>
    internal static bool CopyPays(long bytes)
    {
        long cache = CacheSizes.LastLevel;
        return Avx.IsSupported && cache > 0 && bytes > cache;
    }

    /// <summary>
    /// <paramref name="bytes"/> bytes from <paramref name="from"/> to <paramref name="to"/> (no overlap) around the
    /// caches, shaped like glibc's large copy (<c>large_memcpy_4x</c>): the bytes up to the target's first 64-byte line
    /// plainly, then four pages at a time, a line of each page in turn, each source line prefetched four lines ahead
    /// (four streams of the memory's pages open, four write-combining buffers filling), the rest plainly, a fence
    /// after. Callers check <see cref="CopyPays"/> first. O(<paramref name="bytes"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Copy(ref byte to, ref byte from, nuint bytes)
    {
        fixed (byte* d = &to, s = &from) // a snapshot's byte[] is not pinned, unlike a table's columns
            Copy(d, s, bytes);
    }

    /// <summary><see cref="Copy(ref byte, ref byte, nuint)"/> between pinned addresses.</summary>
    private static void Copy(byte* d, byte* s, nuint bytes)
    {
        const int Page = 4096, Ahead = 256; // a page per stream; prefetched four lines ahead
        nuint head = Math.Min((64 - ((nuint)d & 63)) & 63, bytes), i = head;
        Unsafe.CopyBlockUnaligned(d, s, (uint)head);
        for (; i + 4 * Page <= bytes; i += 4 * Page)
        {
            byte* source = s + i, target = d + i;
            for (int line = 0; line < Page; line += 64)
                for (int page = 0; page < 4 * Page; page += Page)
                {
                    byte* at = source + page + line;
                    Sse.Prefetch0(at + Ahead);
                    if (Avx512F.IsSupported) Avx512F.StoreAlignedNonTemporal(target + page + line, Vector512.Load(at));
                    else
                    {
                        Avx.StoreAlignedNonTemporal(target + page + line, Vector256.Load(at));
                        Avx.StoreAlignedNonTemporal(target + page + line + 32, Vector256.Load(at + 32));
                    }
                }
        }

        Sse.StoreFence();
        for (; i < bytes; i += uint.MaxValue) Unsafe.CopyBlockUnaligned(d + i, s + i, (uint)Math.Min(bytes - i, uint.MaxValue));
    }
}
