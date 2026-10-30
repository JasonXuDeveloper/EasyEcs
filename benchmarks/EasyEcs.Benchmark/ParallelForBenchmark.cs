using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The records a parallel pass emitted, processed in place (an <see cref="Emitted{T}"/> singleton's list): a light
/// step per record (scale an amount) and a heavy one (a few square roots), with <see cref="World.ParallelFor{T, TJob}"/>
/// on the world's runner, against a plain loop on one thread and <c>Parallel.For</c> over the same chunks with a
/// static delegate over static state (no closure: the best a user writes without the runner).
/// </summary>
[MemoryDiagnoser]
public class ParallelForBenchmark
{
    [Params(10_000, 100_000)] public int Records;

    private World _world;
    private static Hit[] s_items = [];
    private static int s_chunk, s_count;
    private static readonly Action<int> LightChunk = static c =>
    {
        int start = c * s_chunk;
        Light(s_items.AsSpan(start, Math.Min(s_chunk, s_count - start)));
    };
    private static readonly Action<int> HeavyChunk = static c =>
    {
        int start = c * s_chunk;
        Heavy(s_items.AsSpan(start, Math.Min(s_chunk, s_count - start)));
    };

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        ref var hits = ref _world.GetOrAddSingleton<Emitted<Hit>>().Items;
        var span = hits.SetCount(Records);
        for (int i = 0; i < span.Length; i++) span[i] = new Hit(i % 100, i);
        s_items = new Hit[Records];
        span.CopyTo(s_items);
        s_count = Records;
        long share = Records / (Environment.ProcessorCount * 4L); // the runner's default chunk
        s_chunk = (int)Math.Clamp((share + 63) & ~63L, 1024, World.MaxChunkRows);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void LightSerial() => Light(_world.GetSingleton<Emitted<Hit>>().Items.AsSpan());

    [Benchmark]
    public void LightParallelFor()
    {
        var job = new LightJob();
        _world.ParallelFor(ref _world.GetSingleton<Emitted<Hit>>().Items, ref job);
    }

    [Benchmark]
    public void LightDotNetParallelFor() => Parallel.For(0, (s_count + s_chunk - 1) / s_chunk, LightChunk);

    [Benchmark]
    public void HeavySerial() => Heavy(_world.GetSingleton<Emitted<Hit>>().Items.AsSpan());

    [Benchmark]
    public void HeavyParallelFor()
    {
        var job = new HeavyJob();
        _world.ParallelFor(ref _world.GetSingleton<Emitted<Hit>>().Items, ref job);
    }

    [Benchmark]
    public void HeavyDotNetParallelFor() => Parallel.For(0, (s_count + s_chunk - 1) / s_chunk, HeavyChunk);

    public record struct Hit(int Target, float Amount);

    private static void Light(Span<Hit> items)
    {
        for (int i = 0; i < items.Length; i++) items[i].Amount = items[i].Amount * 0.999f + 1;
    }

    private static void Heavy(Span<Hit> items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            float a = items[i].Amount;
            for (int k = 0; k < 8; k++) a = MathF.Sqrt(a * a + k);
            items[i].Amount = a * 0.5f;
        }
    }

    private struct LightJob : IForSpan<Hit>
    {
        public void Execute(int start, Span<Hit> items) => Light(items);
    }

    private struct HeavyJob : IForSpan<Hit>
    {
        public void Execute(int start, Span<Hit> items) => Heavy(items);
    }
}
