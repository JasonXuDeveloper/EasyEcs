using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// The roofs of this machine's roofline (borrowed-ideas.md item 6, the method in audit-perf): the bandwidth one thread
/// reaches from the L2 (two columns of 256 KiB: past a 48 KiB L1, inside a 2 MiB L2 with room to spare) and from memory
/// (two of 512 MiB, past the last-level cache) reading a float column (<c>Read</c>: 4 bytes a float) and updating one
/// from another (<c>Update</c>, <c>a += b * k</c>: 12 bytes a float, the shape of a pass that reads two columns and
/// writes one). Bytes divided by the mean are the roof in bytes per second.
/// <see cref="RooflinePeakBenchmark"/> is the compute roof.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public unsafe class RooflineBenchmark
{
    [Params(256 << 10, 512 << 20)] public int Bytes;

    private float* _a, _b;
    private int _floats;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _floats = Bytes / sizeof(float);
        _a = (float*)NativeMemory.AlignedAlloc((nuint)Bytes, 64);
        _b = (float*)NativeMemory.AlignedAlloc((nuint)Bytes, 64);
        for (int i = 0; i < _floats; i++)
        {
            _a[i] = i & 1023;
            _b[i] = (i & 511) * 0.5f;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_a);
        NativeMemory.AlignedFree(_b);
    }

    /// <summary>Sums the column: four vector accumulators, so the adds' latency never bounds the loop.</summary>
    [Benchmark]
    public float Read()
    {
        float* a = _a;
        nint n = _floats, w = Vector256<float>.Count, i = 0;
        Vector256<float> s0 = default, s1 = default, s2 = default, s3 = default;
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<float> t0 = default, t1 = default, t2 = default, t3 = default;
            for (; i <= n - 64; i += 64)
            {
                t0 += Vector512.LoadAligned(a + i);
                t1 += Vector512.LoadAligned(a + i + 16);
                t2 += Vector512.LoadAligned(a + i + 32);
                t3 += Vector512.LoadAligned(a + i + 48);
            }
            return Vector512.Sum(t0 + t1 + t2 + t3);
        }

        for (; i <= n - 4 * w; i += 4 * w)
        {
            s0 += Vector256.LoadAligned(a + i);
            s1 += Vector256.LoadAligned(a + i + w);
            s2 += Vector256.LoadAligned(a + i + 2 * w);
            s3 += Vector256.LoadAligned(a + i + 3 * w);
        }
        return Vector256.Sum(s0 + s1 + s2 + s3);
    }

    /// <summary><c>a[i] += b[i] * k</c>: two loads and a store per float, plain stores (the read for ownership counted).</summary>
    [Benchmark]
    public void Update() => Update(_a, _b, _floats, 0.25f);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Update(float* a, float* b, nint n, float k)
    {
        nint i = 0;
        if (Vector512.IsHardwareAccelerated)
        {
            var vk = Vector512.Create(k);
            for (; i <= n - 16; i += 16) Vector512.StoreAligned(Vector512.LoadAligned(a + i) + Vector512.LoadAligned(b + i) * vk, a + i);
        }
        else
        {
            var vk = Vector256.Create(k);
            for (; i <= n - 8; i += 8) Vector256.StoreAligned(Vector256.LoadAligned(a + i) + Vector256.LoadAligned(b + i) * vk, a + i);
        }
        for (; i < n; i++) a[i] += b[i] * k;
    }
}

/// <summary>
/// The compute roof of one thread: <see cref="Steps"/> steps of 8 independent fused multiply-adds, no memory, on the
/// widest vector (<c>Fma</c>: 16 floats with AVX-512) and on <c>Vector&lt;float&gt;</c> (<c>FmaVector</c>: the width of
/// the generated vector loops, 8 floats on x86 even with AVX-512, since .NET keeps Vector&lt;T&gt; at 256 bits there).
/// Flops = Steps x 8 x lanes x 2 (a multiply and an add each); divided by the mean, the peak in flops per second. Eight
/// chains cover the FMA's latency (4 cycles) on two ports.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public class RooflinePeakBenchmark
{
    /// <summary>Steps of the loop: 8 x 16 x 2 x 1M = 268M flops with AVX-512, 134M on 8 lanes.</summary>
    public const int Steps = 1 << 20;

    [Benchmark]
    public float Fma() => Vector512.IsHardwareAccelerated ? Fma512(1.0000001f) : FmaVector(1.0000001f);

    [Benchmark]
    public float FmaVector() => FmaVector(1.0000001f);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float Fma512(float k)
    {
        var m = Vector512.Create(k);
        var c = Vector512.Create(1e-7f);
        Vector512<float> a0 = Vector512.Create(1f), a1 = a0, a2 = a0, a3 = a0, a4 = a0, a5 = a0, a6 = a0, a7 = a0;
        for (int i = 0; i < Steps; i++)
        {
            a0 = Vector512.FusedMultiplyAdd(a0, m, c);
            a1 = Vector512.FusedMultiplyAdd(a1, m, c);
            a2 = Vector512.FusedMultiplyAdd(a2, m, c);
            a3 = Vector512.FusedMultiplyAdd(a3, m, c);
            a4 = Vector512.FusedMultiplyAdd(a4, m, c);
            a5 = Vector512.FusedMultiplyAdd(a5, m, c);
            a6 = Vector512.FusedMultiplyAdd(a6, m, c);
            a7 = Vector512.FusedMultiplyAdd(a7, m, c);
        }
        return Vector512.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float FmaVector(float k)
    {
        var m = new Vector<float>(k);
        var c = new Vector<float>(1e-7f);
        Vector<float> a0 = Vector<float>.One, a1 = a0, a2 = a0, a3 = a0, a4 = a0, a5 = a0, a6 = a0, a7 = a0;
        for (int i = 0; i < Steps; i++)
        {
            a0 = Vector.FusedMultiplyAdd(a0, m, c);
            a1 = Vector.FusedMultiplyAdd(a1, m, c);
            a2 = Vector.FusedMultiplyAdd(a2, m, c);
            a3 = Vector.FusedMultiplyAdd(a3, m, c);
            a4 = Vector.FusedMultiplyAdd(a4, m, c);
            a5 = Vector.FusedMultiplyAdd(a5, m, c);
            a6 = Vector.FusedMultiplyAdd(a6, m, c);
            a7 = Vector.FusedMultiplyAdd(a7, m, c);
        }
        return Vector.Sum(a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7);
    }
}
