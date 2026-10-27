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
}
