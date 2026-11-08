using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// A column written and never read by the pass (<c>dst = src * k + c</c>), plain stores against streaming stores
/// (Locality/Streams.cs), at 1M floats (4 MB a column: in the last-level cache) and 64M floats (256 MB a column: two
/// columns past a 260 MB last-level cache). Decides microarch.md candidate 8: whether generated loops should stream
/// a write-only column past the cache.
/// </summary>
[MemoryDiagnoser, IsaColumn]
public unsafe class StreamingStoreBenchmark
{
    [Params(1 << 20, 1 << 26)] public int Floats;

    private float* _src, _dst;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _src = (float*)NativeMemory.AlignedAlloc((nuint)Floats * sizeof(float), 64);
        _dst = (float*)NativeMemory.AlignedAlloc((nuint)Floats * sizeof(float), 64);
        for (int i = 0; i < Floats; i++)
        {
            _src[i] = i & 1023;
            _dst[i] = 0;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_src);
        NativeMemory.AlignedFree(_dst);
    }

    [Benchmark(Baseline = true)]
    public void Plain() => Streams.Plain(_src, _dst, Floats, 1.5f, 2f);

    [Benchmark]
    public void NonTemporal() => Streams.NonTemporal(_src, _dst, Floats, 1.5f, 2f);
}
