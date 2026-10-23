using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Stream compaction: the ids of the entities whose health is under a limit (1 %, 50 % or every row), collected into
/// a singleton's array, as a server collects the dead, the hurt or the ones in range before a structural pass. What a
/// user writes: a per-entity method with a branch per row (<see cref="PerRow"/>), and a batch method that compares a
/// vector of rows and visits the set bits (<see cref="Ballot"/>); against <see cref="Lanes.Compact{T}"/>, which writes
/// the selected ids of 64 rows side by side (AVX-512 compress stores). Then <c>CollectIds</c> with a filter that
/// presence bits decide (a third of the rows frozen), which compacts whole words of ids the same way.
/// </summary>
[MemoryDiagnoser]
public class CompactBenchmark
{
    [Params(1_000_000)] public int Entities;
    [Params(1, 50, 100)] public int Percent;

    private World _world;
    private EntityId[] _collected;
    private static readonly QueryFilter Awake = QueryFilter.Create().Without<Frozen>();

    public record struct Health(int Value) : IComponent;
    public struct Frozen : IComponent;

    /// <summary>The ids found, and the health under which an entity is picked.</summary>
    [NotSaved]
    public struct Hurt : ISingletonComponent
    {
        public EntityId[] Ids;
        public int Count;
        public int Below;
    }

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var all = new Entity[Entities];
        _world.CreateEntities(all, new Health(0), new Frozen());
        var random = new Random(5);
        foreach (var item in _world.Query<Health>()) item.Component1 = new Health(random.Next(100));
        for (int i = 0; i < all.Length; i++)
            if (i % 3 != 0) all[i].Remove<Frozen>(); // a presence bit: the row keeps its place in the one table

        _world.AddSingleton<Hurt>() = new Hurt { Ids = new EntityId[Entities], Below = Percent };
        _collected = new EntityId[Entities];
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int PerRow()
    {
        _world.Hurt.Count = 0;
        Picks.PerRow(_world);
        return _world.Hurt.Count;
    }

    [Benchmark]
    public int Ballot()
    {
        _world.Hurt.Count = 0;
        Picks.Ballot(_world);
        return _world.Hurt.Count;
    }

    [Benchmark]
    public int Compact()
    {
        _world.Hurt.Count = 0;
        Picks.Compact(_world);
        return _world.Hurt.Count;
    }

    /// <summary>The awake entities' ids (two of every three rows, decided by presence bits).</summary>
    [Benchmark]
    public int CollectIds() => _world.CollectIds<Health>(_collected, Awake);

    public static class Picks
    {
        [ForEach]
        public static void PerRow(Entity entity, in Health health, ref Hurt hurt)
        {
            if (health.Value < hurt.Below) hurt.Ids[hurt.Count++] = entity.Id;
        }

        // A vector of rows compared at once, then one step per set bit of the lane mask.
        [ForEach]
        public static void Ballot(EntitySpan entities, ReadOnlySpan<Health> health, ref Hurt hurt)
        {
            var values = MemoryMarshal.Cast<Health, int>(health);
            var ids = entities.Ids;
            var into = hurt.Ids;
            var below = Vector256.Create(hurt.Below);
            int n = hurt.Count, i = 0;
            for (; i <= values.Length - Vector256<int>.Count; i += Vector256<int>.Count)
                for (uint mask = Vector256.LessThan(Vector256.Create(values.Slice(i, Vector256<int>.Count)), below).ExtractMostSignificantBits();
                     mask != 0; mask &= mask - 1)
                    into[n++] = ids[i + System.Numerics.BitOperations.TrailingZeroCount(mask)];
            for (; i < values.Length; i++)
                if (values[i] < hurt.Below) into[n++] = ids[i];
            hurt.Count = n;
        }

        // A mask of 64 rows from four compares, then their ids side by side.
        [ForEach]
        public static void Compact(EntitySpan entities, ReadOnlySpan<Health> health, ref Hurt hurt)
        {
            var values = MemoryMarshal.Cast<Health, int>(health);
            var ids = entities.Ids;
            var into = hurt.Ids.AsSpan();
            var below = Vector512.Create(hurt.Below);
            ref int at = ref MemoryMarshal.GetReference(values);
            int n = hurt.Count, i = 0;
            for (; i <= values.Length - 64; i += 64)
            {
                ulong mask = Vector512.LessThan(Vector512.LoadUnsafe(ref at, (nuint)i), below).ExtractMostSignificantBits()
                    | Vector512.LessThan(Vector512.LoadUnsafe(ref at, (nuint)i + 16), below).ExtractMostSignificantBits() << 16
                    | Vector512.LessThan(Vector512.LoadUnsafe(ref at, (nuint)i + 32), below).ExtractMostSignificantBits() << 32
                    | Vector512.LessThan(Vector512.LoadUnsafe(ref at, (nuint)i + 48), below).ExtractMostSignificantBits() << 48;
                n += Lanes.Compact(ids.Slice(i, 64), mask, into.Slice(n));
            }
            for (; i < values.Length; i++)
                if (values[i] < hurt.Below) into[n++] = ids[i];
            hurt.Count = n;
        }
    }
}
