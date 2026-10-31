using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Element loops (inner-loops.md WP2): a [ForEach] method that runs row by row and loops over its buffer, as written
/// (Scalar: SimdMode.Off), with the loop's vector form the generator writes (Generated: the same body) and with the
/// Vector&lt;T&gt; loop written by hand (HandWritten). <c>Decay</c>: each float of a buffer scaled and offset in place.
/// <c>Widen</c>: ints read from one buffer, written as longs into another (two spans, the bound and alias guards).
/// <c>SumInts</c>: the ints of a buffer summed into a variable of the method (exact: integer sums wrap).
/// <c>SumFloats</c>: the floats summed under FloatMode.Fast (one sum per lane, two chains).
/// <c>Smooth</c>: ints read at three neighboring indexes (i - 1, i, i + 1: a stream per offset), weighted into longs.
/// Elements = 4 is the call per row on a buffer shorter than a vector; it must stay within noise of Scalar.
/// </summary>
[MemoryDiagnoser]
public class ElementLoopBenchmark
{
    public record struct Fade(float Value) : IComponent;
    public record struct Tally(int Sum, float Total) : IComponent;

    [Params(10_000)] public int Entities;
    [Params(4, 64, 1024)] public int Elements;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.Reserve<DynamicBuffer<float>, DynamicBuffer<int>, DynamicBuffer<long>, Fade, Tally>(Entities);
        var random = new Random(7);
        for (int n = 0; n < Entities; n++)
        {
            var e = _world.CreateEntity(new DynamicBuffer<float>(), new DynamicBuffer<int>(), new DynamicBuffer<long>(), new Fade(0.5f + n % 7 * 0.0625f), new Tally());
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
    [Benchmark] public void SumIntsScalar() => ElementPasses.SumIntsScalar(_world);
    [Benchmark] public void SumIntsGenerated() => ElementPasses.SumIntsGenerated(_world);
    [Benchmark] public void SumIntsHandWritten() => ElementPasses.SumIntsHandWritten(_world);
    [Benchmark] public void SumFloatsScalar() => ElementPasses.SumFloatsScalar(_world);
    [Benchmark] public void SumFloatsGenerated() => ElementPasses.SumFloatsGenerated(_world);
    [Benchmark] public void SumFloatsHandWritten() => ElementPasses.SumFloatsHandWritten(_world);
    [Benchmark] public void SmoothScalar() => ElementPasses.SmoothScalar(_world);
    [Benchmark] public void SmoothGenerated() => ElementPasses.SmoothGenerated(_world);
    [Benchmark] public void SmoothHandWritten() => ElementPasses.SmoothHandWritten(_world);
}

/// <summary>
/// VoxelServer's height map (samples/VoxelServer/Systems/Water/HeightMap.cs): per chunk of 16 x 16 x 32 blocks, the top
/// solid block of each column, layer by layer into 256 bytes on the stack. Scalar: the plain loop over a layer's columns
/// as written (SimdMode.Off); Generated: the same loop, which the generator gives a vector loop (the sample's form);
/// HandWritten: the Vector128 loop the sample had before.
/// </summary>
[MemoryDiagnoser]
public class HeightMapLoopBenchmark
{
    public enum Cube : byte { Air, Stone }

    [Params(1_000)] public int Chunks;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Chunks });
        _world.Reserve<DynamicBuffer<Cube>, DynamicBuffer<byte>>(Chunks);
        var random = new Random(3);
        for (int n = 0; n < Chunks; n++)
        {
            var e = _world.CreateEntity(new DynamicBuffer<Cube>(), new DynamicBuffer<byte>());
            var cubes = e.Get<DynamicBuffer<Cube>>().SetCount(ElementPasses.Columns * ElementPasses.Layers);
            for (int c = 0; c < ElementPasses.Columns; c++)
            {
                int ground = random.Next(8, 28); // terrain with caves: solid below the ground, a hole now and then
                for (int y = 0; y < ElementPasses.Layers; y++)
                    cubes[y * ElementPasses.Columns + c] = y == 0 || y < ground && random.Next(8) != 0 ? Cube.Stone : Cube.Air;
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)] public void HeightMapScalar() => ElementPasses.HeightMapScalar(_world);
    [Benchmark] public void HeightMapGenerated() => ElementPasses.HeightMapGenerated(_world);
    [Benchmark] public void HeightMapHandWritten() => ElementPasses.HeightMapHandWritten(_world);
}

/// <summary>
/// A batch method's loop over its rows (inner-loops.md WP2): the plain for over the run's component span as written
/// (Scalar: SimdMode.Off), with the vector form the generator gives it (Generated), and the method for one row whose
/// vector loop the generator writes (PerRow, what EECS103 points a hand-written batch loop to).
/// </summary>
[MemoryDiagnoser]
public class BatchLoopBenchmark
{
    public record struct Drift(float Value) : IComponent;

    [Params(100_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.Reserve<Drift>(Entities);
        for (int n = 0; n < Entities; n++) _world.CreateEntity(new Drift(n % 100 * 0.5f));
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)] public void BatchLoopScalar() => ElementPasses.BatchLoopScalar(_world);
    [Benchmark] public void BatchLoopGenerated() => ElementPasses.BatchLoopGenerated(_world);
    [Benchmark] public void BatchLoopPerRow() => ElementPasses.BatchLoopPerRow(_world);
}

/// <summary>The passes of <see cref="ElementLoopBenchmark"/>, <see cref="HeightMapLoopBenchmark"/> and <see cref="BatchLoopBenchmark"/>.</summary>
public static class ElementPasses
{
    internal const int Columns = 256, Layers = 32;

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void BatchLoopScalar(Span<BatchLoopBenchmark.Drift> drift)
    {
        for (int i = 0; i < drift.Length; i++) drift[i].Value = drift[i].Value * 0.99f + 0.5f;
    }

    [ForEach]
    internal static void BatchLoopGenerated(Span<BatchLoopBenchmark.Drift> drift)
    {
        for (int i = 0; i < drift.Length; i++) drift[i].Value = drift[i].Value * 0.99f + 0.5f;
    }

    [ForEach]
    internal static void BatchLoopPerRow(ref BatchLoopBenchmark.Drift drift) => drift.Value = drift.Value * 0.99f + 0.5f;

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void HeightMapScalar(in DynamicBuffer<HeightMapLoopBenchmark.Cube> cubes, ref DynamicBuffer<byte> map)
    {
        var solid = cubes.AsReadOnlySpan();
        Span<byte> top = stackalloc byte[Columns];
        for (int y = 1; y < Layers; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            for (int i = 0; i < Columns; i++)
                if (layer[i] != HeightMapLoopBenchmark.Cube.Air) top[i] = (byte)y;
        }
        top.CopyTo(map.SetCount(Columns));
    }

    [ForEach]
    internal static void HeightMapGenerated(in DynamicBuffer<HeightMapLoopBenchmark.Cube> cubes, ref DynamicBuffer<byte> map)
    {
        var solid = cubes.AsReadOnlySpan();
        Span<byte> top = stackalloc byte[Columns];
        for (int y = 1; y < Layers; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            for (int i = 0; i < Columns; i++)
                if (layer[i] != HeightMapLoopBenchmark.Cube.Air) top[i] = (byte)y;
        }
        top.CopyTo(map.SetCount(Columns));
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void HeightMapHandWritten(in DynamicBuffer<HeightMapLoopBenchmark.Cube> cubes, ref DynamicBuffer<byte> map)
    {
        var solid = MemoryMarshal.Cast<HeightMapLoopBenchmark.Cube, byte>(cubes.AsReadOnlySpan());
        Span<byte> top = stackalloc byte[Columns];
        for (int y = 1; y < Layers; y++)
        {
            var layer = solid.Slice(y * Columns, Columns);
            var height = Vector128.Create((byte)y);
            for (int i = 0; i < Columns; i += Vector128<byte>.Count)
            {
                var air = Vector128.Equals(Vector128.Create(layer.Slice(i, Vector128<byte>.Count)), Vector128<byte>.Zero);
                var below = Vector128.Create((ReadOnlySpan<byte>)top.Slice(i, Vector128<byte>.Count));
                Vector128.ConditionalSelect(air, below, height).CopyTo(top.Slice(i, Vector128<byte>.Count));
            }
        }
        top.CopyTo(map.SetCount(Columns));
    }

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

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SmoothScalar(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 1; i < src.Length - 1; i++) dst[i] = src[i - 1] + 2L * src[i] + src[i + 1];
    }

    [ForEach]
    internal static void SmoothGenerated(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        for (int i = 1; i < src.Length - 1; i++) dst[i] = src[i - 1] + 2L * src[i] + src[i + 1];
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SmoothHandWritten(in DynamicBuffer<int> from, ref DynamicBuffer<long> to)
    {
        var src = from.AsReadOnlySpan();
        var dst = to.AsSpan();
        int i = 1;
        if (Vector.IsHardwareAccelerated && dst.Length >= src.Length)
        {
            for (; i <= src.Length - 1 - Vector<int>.Count; i += Vector<int>.Count)
            {
                Vector.Widen(new Vector<int>(src.Slice(i - 1)), out var l0, out var h0);
                Vector.Widen(new Vector<int>(src.Slice(i)), out var l1, out var h1);
                Vector.Widen(new Vector<int>(src.Slice(i + 1)), out var l2, out var h2);
                (l0 + l1 + l1 + l2).CopyTo(dst.Slice(i));
                (h0 + h1 + h1 + h2).CopyTo(dst.Slice(i + Vector<long>.Count));
            }
        }
        for (; i < src.Length - 1; i++) dst[i] = src[i - 1] + 2L * src[i] + src[i + 1];
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SumIntsScalar(in DynamicBuffer<int> from, ref ElementLoopBenchmark.Tally tally)
    {
        int sum = 0;
        foreach (int x in from.AsReadOnlySpan()) sum += x;
        tally.Sum = sum;
    }

    [ForEach]
    internal static void SumIntsGenerated(in DynamicBuffer<int> from, ref ElementLoopBenchmark.Tally tally)
    {
        int sum = 0;
        foreach (int x in from.AsReadOnlySpan()) sum += x;
        tally.Sum = sum;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SumIntsHandWritten(in DynamicBuffer<int> from, ref ElementLoopBenchmark.Tally tally)
    {
        var s = from.AsReadOnlySpan();
        int sum = 0, i = 0;
        if (Vector.IsHardwareAccelerated && s.Length >= Vector<int>.Count)
        {
            var acc = Vector<int>.Zero;
            for (; i <= s.Length - Vector<int>.Count; i += Vector<int>.Count) acc += new Vector<int>(s.Slice(i));
            sum = Vector.Sum(acc);
        }
        for (; i < s.Length; i++) sum += s[i];
        tally.Sum = sum;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SumFloatsScalar(in DynamicBuffer<float> heat, ref ElementLoopBenchmark.Tally tally)
    {
        float t = 0;
        foreach (float h in heat.AsReadOnlySpan()) t += h;
        tally.Total = t;
    }

    [ForEach(FloatMode = FloatMode.Fast)]
    internal static void SumFloatsGenerated(in DynamicBuffer<float> heat, ref ElementLoopBenchmark.Tally tally)
    {
        float t = 0;
        foreach (float h in heat.AsReadOnlySpan()) t += h;
        tally.Total = t;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SumFloatsHandWritten(in DynamicBuffer<float> heat, ref ElementLoopBenchmark.Tally tally)
    {
        var s = heat.AsReadOnlySpan();
        float t = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && s.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= s.Length - Vector<float>.Count; i += Vector<float>.Count) acc += new Vector<float>(s.Slice(i));
            t = Vector.Sum(acc);
        }
        for (; i < s.Length; i++) t += s[i];
        tally.Total = t;
    }
}
