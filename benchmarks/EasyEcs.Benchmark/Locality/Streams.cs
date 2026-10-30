using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// Streaming stores (borrowed-ideas.md item 5, microarch.md candidate 8): a pass that writes a column it never reads
/// (<c>dst = src * k + c</c>) with plain stores, which read each line into the cache before writing it (read for
/// ownership) and evict other lines, against non-temporal stores, which write whole lines around the caches. The
/// columns are 64-byte aligned and a whole number of vectors long, as a table's unmanaged columns are.
/// </summary>
public static unsafe class Streams
{
    /// <summary><paramref name="dst"/>[i] = <paramref name="src"/>[i] * k + c with plain vector stores.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Plain(float* src, float* dst, nint n, float k, float c)
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var vk = Vector512.Create(k);
            var vc = Vector512.Create(c);
            for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
                Vector512.StoreAligned(Vector512.LoadAligned(src + i) * vk + vc, dst + i);
        }
        else
        {
            var vk = Vector128.Create(k);
            var vc = Vector128.Create(c);
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
                Vector128.StoreAligned(Vector128.LoadAligned(src + i) * vk + vc, dst + i);
        }
        for (; i < n; i++) dst[i] = src[i] * k + c;
    }

    /// <summary>The same with non-temporal stores (x86 MOVNTPS; ARM64 STNP of two 128-bit vectors), then a fence.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NonTemporal(float* src, float* dst, nint n, float k, float c)
    {
        nint i = 0;
        if (Avx512F.IsSupported)
        {
            var vk = Vector512.Create(k);
            var vc = Vector512.Create(c);
            for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
                Avx512F.StoreAlignedNonTemporal(dst + i, Vector512.LoadAligned(src + i) * vk + vc);
            Sse.StoreFence();
        }
        else if (Avx.IsSupported)
        {
            var vk = Vector256.Create(k);
            var vc = Vector256.Create(c);
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
                Avx.StoreAlignedNonTemporal(dst + i, Vector256.LoadAligned(src + i) * vk + vc);
            Sse.StoreFence();
        }
        else if (Sse.IsSupported)
        {
            var vk = Vector128.Create(k);
            var vc = Vector128.Create(c);
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
                Sse.StoreAlignedNonTemporal(dst + i, Vector128.LoadAligned(src + i) * vk + vc);
            Sse.StoreFence();
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            var vk = Vector128.Create(k);
            var vc = Vector128.Create(c);
            for (; i <= n - 2 * Vector128<float>.Count; i += 2 * Vector128<float>.Count)
                AdvSimd.Arm64.StorePairNonTemporal(dst + i, Vector128.LoadAligned(src + i) * vk + vc,
                    Vector128.LoadAligned(src + i + Vector128<float>.Count) * vk + vc);
        }
        for (; i < n; i++) dst[i] = src[i] * k + c;
    }

    /// <summary>
    /// <paramref name="bytes"/> bytes from <paramref name="src"/> to <paramref name="dst"/> (no overlap) as the core
    /// copies a column today: <c>Unsafe.CopyBlockUnaligned</c>, which .NET hands to the C runtime's <c>memmove</c> past
    /// 2 KB; glibc's streams itself past its <c>x86_non_temporal_threshold</c> (<c>ld.so --list-tunables</c>: 131 MiB
    /// on the 260 MiB last-level cache of the reference VM).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Memmove(byte* src, byte* dst, nuint bytes) => Unsafe.CopyBlockUnaligned(dst, src, (uint)bytes);

    /// <summary>
    /// The same copy with non-temporal stores (borrowed-ideas.md item 5): the bytes up to the first 64-byte aligned
    /// address of <paramref name="dst"/> plainly, then four vectors a step loaded unaligned and stored around the
    /// caches (x86 VMOVNTPS of 64, 32 or 16 bytes; ARM64 STNP), the tail plainly, a fence after.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NonTemporalCopy(byte* src, byte* dst, nuint bytes)
    {
        nuint head = Math.Min((64 - ((nuint)dst & 63)) & 63, bytes);
        Unsafe.CopyBlockUnaligned(dst, src, (uint)head);
        nuint i = head;
        if (Avx512F.IsSupported)
        {
            for (; i + 256 <= bytes; i += 256)
            {
                var a = Vector512.Load(src + i);
                var b = Vector512.Load(src + i + 64);
                var c = Vector512.Load(src + i + 128);
                var d = Vector512.Load(src + i + 192);
                Avx512F.StoreAlignedNonTemporal(dst + i, a);
                Avx512F.StoreAlignedNonTemporal(dst + i + 64, b);
                Avx512F.StoreAlignedNonTemporal(dst + i + 128, c);
                Avx512F.StoreAlignedNonTemporal(dst + i + 192, d);
            }
            Sse.StoreFence();
        }
        else if (Avx.IsSupported)
        {
            for (; i + 128 <= bytes; i += 128)
            {
                var a = Vector256.Load(src + i);
                var b = Vector256.Load(src + i + 32);
                var c = Vector256.Load(src + i + 64);
                var d = Vector256.Load(src + i + 96);
                Avx.StoreAlignedNonTemporal(dst + i, a);
                Avx.StoreAlignedNonTemporal(dst + i + 32, b);
                Avx.StoreAlignedNonTemporal(dst + i + 64, c);
                Avx.StoreAlignedNonTemporal(dst + i + 96, d);
            }
            Sse.StoreFence();
        }
        else if (Sse2.IsSupported)
        {
            for (; i + 64 <= bytes; i += 64)
            {
                var a = Vector128.Load(src + i);
                var b = Vector128.Load(src + i + 16);
                var c = Vector128.Load(src + i + 32);
                var d = Vector128.Load(src + i + 48);
                Sse2.StoreAlignedNonTemporal(dst + i, a);
                Sse2.StoreAlignedNonTemporal(dst + i + 16, b);
                Sse2.StoreAlignedNonTemporal(dst + i + 32, c);
                Sse2.StoreAlignedNonTemporal(dst + i + 48, d);
            }
            Sse.StoreFence();
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            for (; i + 64 <= bytes; i += 64)
            {
                AdvSimd.Arm64.StorePairNonTemporal(dst + i, Vector128.Load(src + i), Vector128.Load(src + i + 16));
                AdvSimd.Arm64.StorePairNonTemporal(dst + i + 32, Vector128.Load(src + i + 32), Vector128.Load(src + i + 48));
            }
            System.Threading.Interlocked.MemoryBarrier();
        }

        Unsafe.CopyBlockUnaligned(dst + i, src + i, (uint)(bytes - i));
    }

    /// <summary>
    /// <see cref="NonTemporalCopy"/> shaped like glibc's large copy (<c>large_memcpy_4x</c>): four pages at a time, a
    /// line of each page in turn, each source line prefetched four lines ahead, so the loads keep four streams of the
    /// memory's pages open and the stores fill four write-combining buffers. Falls back to
    /// <see cref="NonTemporalCopy"/> without AVX-512 (a prototype for this machine: is a loop of our own ever faster
    /// than memmove past the cache?).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NonTemporalCopyPages(byte* src, byte* dst, nuint bytes)
    {
        if (!Avx512F.IsSupported)
        {
            NonTemporalCopy(src, dst, bytes);
            return;
        }

        const int Page = 4096;
        nuint head = Math.Min((64 - ((nuint)dst & 63)) & 63, bytes);
        Unsafe.CopyBlockUnaligned(dst, src, (uint)head);
        nuint i = head;
        for (; i + 4 * Page <= bytes; i += 4 * Page)
        {
            byte* s = src + i, d = dst + i;
            for (int line = 0; line < Page; line += 64)
            {
                Sse.Prefetch0(s + line + 256);
                Sse.Prefetch0(s + Page + line + 256);
                Sse.Prefetch0(s + 2 * Page + line + 256);
                Sse.Prefetch0(s + 3 * Page + line + 256);
                var a = Vector512.Load(s + line);
                var b = Vector512.Load(s + Page + line);
                var c = Vector512.Load(s + 2 * Page + line);
                var e = Vector512.Load(s + 3 * Page + line);
                Avx512F.StoreAlignedNonTemporal(d + line, a);
                Avx512F.StoreAlignedNonTemporal(d + Page + line, b);
                Avx512F.StoreAlignedNonTemporal(d + 2 * Page + line, c);
                Avx512F.StoreAlignedNonTemporal(d + 3 * Page + line, e);
            }
        }

        Sse.StoreFence();
        NonTemporalCopy(src + i, dst + i, bytes - i);
    }
}
