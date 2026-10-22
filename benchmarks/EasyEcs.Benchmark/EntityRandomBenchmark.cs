using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Random numbers for every entity of a pass: <see cref="EntityRandom"/> (four rounds of SplitMix64's finalizer)
/// against the counter-based generators it was chosen over, Philox-4x32 with 10 rounds (cuRAND's default) and 7
/// (the fewest that pass BigCrush), and against what methods use without it, <c>Random.Shared</c> (a xoshiro256** per
/// thread, not repeatable) and one seeded <c>new Random(seed)</c> (repeatable on one thread only). The loops draw one
/// float per entity id into a sum; the passes draw one roll per row in a [ForEach] method, in parallel.
/// </summary>
[MemoryDiagnoser]
public class EntityRandomBenchmark
{
    [Params(100_000)] public int Entities;

    private World _world;
    private readonly Random _seeded = new(42);

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.CreateEntities(new Entity[Entities], new Roll(0));
        _world.AddSingleton<EntityRandom>() = new EntityRandom(42, 7);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public float SplitMix()
    {
        float sum = 0;
        for (int i = 0; i < Entities; i++) sum += EntityRandom.Float(42, new EntityId(i, 1), 7, 0);
        return sum;
    }

    [Benchmark]
    public float Philox10()
    {
        float sum = 0;
        for (int i = 0; i < Entities; i++) sum += (Philox.Bits(42, new EntityId(i, 1), 7, 0, 10) >> 40) * (1f / (1 << 24));
        return sum;
    }

    [Benchmark]
    public float Philox7()
    {
        float sum = 0;
        for (int i = 0; i < Entities; i++) sum += (Philox.Bits(42, new EntityId(i, 1), 7, 0, 7) >> 40) * (1f / (1 << 24));
        return sum;
    }

    [Benchmark]
    public float RandomShared()
    {
        float sum = 0;
        for (int i = 0; i < Entities; i++) sum += Random.Shared.NextSingle();
        return sum;
    }

    [Benchmark]
    public float RandomSeeded()
    {
        float sum = 0;
        for (int i = 0; i < Entities; i++) sum += _seeded.NextSingle();
        return sum;
    }

    [Benchmark]
    public int PassEntityRandom()
    {
        Rolls.Deterministic(_world);
        return _world.Count<Roll>();
    }

    [Benchmark]
    public int PassRandomShared()
    {
        Rolls.Shared(_world);
        return _world.Count<Roll>();
    }

    public record struct Roll(int Value) : IComponent;

    public static class Rolls
    {
        [ForEach(Parallel = true)]
        public static void Deterministic(ref Roll roll, Entity entity, in EntityRandom random) => roll.Value = random.Range(entity.Id, 0, 1, 7);

        [ForEach(Parallel = true)]
#pragma warning disable EECS071 // the comparison: not repeatable
        public static void Shared(ref Roll roll) => roll.Value = Random.Shared.Next(1, 7);
#pragma warning restore EECS071
    }

    /// <summary>Philox-4x32 (Salmon et al., 2011): the counter is the entity and the tick (its high half xor the stream), the key the seed.</summary>
    private static class Philox
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Bits(ulong seed, EntityId entity, ulong tick, uint stream, int rounds)
        {
            uint c0 = (uint)entity.Index, c1 = (uint)entity.Version, c2 = (uint)tick, c3 = (uint)(tick >> 32) ^ stream;
            uint k0 = (uint)seed, k1 = (uint)(seed >> 32);
            for (int r = 0; r < rounds; r++)
            {
                ulong p0 = 0xD2511F53UL * c0, p1 = 0xCD9E8D57UL * c2;
                uint n0 = (uint)(p1 >> 32) ^ c1 ^ k0, n2 = (uint)(p0 >> 32) ^ c3 ^ k1;
                c1 = (uint)p1;
                c3 = (uint)p0;
                c0 = n0;
                c2 = n2;
                k0 += 0x9E3779B9;
                k1 += 0xBB67AE85;
            }

            return (ulong)c0 << 32 | c1;
        }
    }
}
