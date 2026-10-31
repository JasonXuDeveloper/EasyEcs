using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Element loops (inner-loops.md WP2): a [ForEach] method that runs row by row and loops over its buffer, as written
/// (Scalar: SimdMode.Off), with the loop's vector form the generator writes (Generated: the same body) and with the
/// Vector&lt;T&gt; loop written by hand (HandWritten). <c>Decay</c>: each float of a buffer scaled and offset in place.
/// <c>Widen</c>: ints read from one buffer, written as longs into another (two spans, the bound and alias guards).
/// Elements = 4 is the call per row on a buffer shorter than a vector; it must stay within noise of Scalar.
/// </summary>
[MemoryDiagnoser]
public class ElementLoopBenchmark
{
    public record struct Fade(float Value) : IComponent;

    [Params(10_000)] public int Entities;
    [Params(4, 64, 1024)] public int Elements;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.Reserve<DynamicBuffer<float>, DynamicBuffer<int>, DynamicBuffer<long>, Fade>(Entities);
        var random = new Random(7);
        for (int n = 0; n < Entities; n++)
        {
            var e = _world.CreateEntity(new DynamicBuffer<float>(), new DynamicBuffer<int>(), new DynamicBuffer<long>(), new Fade(0.5f + n % 7 * 0.0625f));
            var heat = e.Get<DynamicBuffer<float>>().SetCount(Elements);
            var from = e.Get<DynamicBuffer<int>>().SetCount(Elements);
            e.Get<DynamicBuffer<long>>().SetCount(Elements);
            for (int i = 0; i < Elements; i++)
            {
                heat[i] = random.Next(-100, 100) * 0.25f;
                from[i] = random.Next();
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)] public void DecayScalar() => ElementPasses.DecayScalar(_world);
    [Benchmark] public void DecayGenerated() => ElementPasses.DecayGenerated(_world);
    [Benchmark] public void DecayHandWritten() => ElementPasses.DecayHandWritten(_world);
    [Benchmark] public void WidenScalar() => ElementPasses.WidenScalar(_world);
    [Benchmark] public void WidenGenerated() => ElementPasses.WidenGenerated(_world);
    [Benchmark] public void WidenHandWritten() => ElementPasses.WidenHandWritten(_world);
}

/// <summary>The passes of <see cref="ElementLoopBenchmark"/>.</summary>
public static class ElementPasses
{
    [ForEach(SimdMode = SimdMode.Off)]
    internal static void DecayScalar(ref DynamicBuffer<float> heat, in ElementLoopBenchmark.Fade fade)
    {
        float k = fade.Value;
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * k + 0.25f;
    }

    [ForEach]
    internal static void DecayGenerated(ref DynamicBuffer<float> heat, in ElementLoopBenchmark.Fade fade)
    {
        float k = fade.Value;
        var s = heat.AsSpan();
        for (int i = 0; i < s.Length; i++) s[i] = s[i] * k + 0.25f;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void DecayHandWritten(ref DynamicBuffer<float> heat, in ElementLoopBenchmark.Fade fade)
    {
        float k = fade.Value;
        var s = heat.AsSpan();
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vs = MemoryMarshal.Cast<float, Vector<float>>(s);
            var vk = new Vector<float>(k);
            var add = new Vector<float>(0.25f);
            for (int v = 0; v < vs.Length; v++) vs[v] = vs[v] * vk + add;
            i = vs.Length * Vector<float>.Count;
        }
        for (; i < s.Length; i++) s[i] = s[i] * k + 0.25f;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void WidenScalar(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * 3L + 1;
    }

    [ForEach]
    internal static void WidenGenerated(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * 3L + 1;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void WidenHandWritten(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        int i = 0;
        if (Vector.IsHardwareAccelerated && dst.Length >= src.Length)
        {
            var three = new Vector<long>(3);
            for (; i <= src.Length - Vector<int>.Count; i += Vector<int>.Count)
            {
                Vector.Widen(new Vector<int>(src.Slice(i)), out var low, out var high);
                (low * three + Vector<long>.One).CopyTo(dst.Slice(i));
                (high * three + Vector<long>.One).CopyTo(dst.Slice(i + Vector<long>.Count));
            }
        }
        for (; i < src.Length; i++) dst[i] = src[i] * 3L + 1;
    }
}
