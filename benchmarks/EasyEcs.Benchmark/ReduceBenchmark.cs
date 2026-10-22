using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// An integer sum, a float sum and a bounding box over every entity, the reductions a server computes each tick (the
/// total of a resource, the mass of a crowd, its extent): <see cref="Reduce{T}"/> on several threads and on one,
/// against what a pass writes without it: a <c>ref</c> context value on one thread; on several threads, one shared
/// total updated per row (<see cref="Interlocked"/>, or a lock), and .NET's thread-local pattern,
/// <c>Parallel.For&lt;TLocal&gt;(from, to, localInit, body, localFinally)</c> over a copy of the column, as the docs
/// write it (per row) and with a cached <c>ParallelOptions</c>, and <c>Parallel.For</c> over the chunks a parallel pass
/// would cut, one partial result per chunk combined in chunk order (what a generator could emit). Parallel.For runs
/// static lambdas over static state (no closure, delegates cached: the best a user can write, though not reentrant);
/// the copy is made once, outside the measurement. Steady state of the world's passes allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class ReduceBenchmark
{
    [Params(1_000, 10_000, 100_000, 1_000_000)] public int Entities;

    private World _world;
    private readonly StrongBox<long> _total = new();
    private readonly Lock _lock = new();
    private readonly StrongBox<Area> _box = new();
    private readonly StrongBox<float> _mass = new();

    // Parallel.For reaches its data through statics: its delegates are then cached, never built per call.
    private static int[] s_scores = [];
    private static float[] s_weights = [];
    private static long s_total;
    private static float s_mass;
    private static readonly Lock s_massLock = new();
    private static readonly Func<long> NoTotal = static () => 0L;
    private static readonly Func<int, ParallelLoopState, long, long> AddScore = static (i, _, sub) => sub + s_scores[i];
    private static readonly Action<long> AddTotal = static sub => Interlocked.Add(ref s_total, sub);
    private static readonly Func<float> NoMass = static () => 0f;
    private static readonly Func<int, ParallelLoopState, float, float> AddWeight = static (i, _, sub) => sub + s_weights[i];
    private static readonly Action<float> AddMass = static sub => { lock (s_massLock) s_mass += sub; };
    private static readonly ParallelOptions Options = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };
    private static int s_chunkRows, s_chunks;
    private static long[] s_totals = [];
    private static float[] s_masses = [];
    private static readonly Action<int> SumChunk = static chunk =>
    {
        long sum = 0;
        var rows = s_scores.AsSpan(chunk * s_chunkRows, Math.Min(s_chunkRows, s_scores.Length - chunk * s_chunkRows));
        for (int i = 0; i < rows.Length; i++) sum += rows[i];
        s_totals[chunk] = sum;
    };
    private static readonly Action<int> MassChunk = static chunk =>
    {
        float sum = 0;
        var rows = s_weights.AsSpan(chunk * s_chunkRows, Math.Min(s_chunkRows, s_weights.Length - chunk * s_chunkRows));
        for (int i = 0; i < rows.Length; i++) sum += rows[i];
        s_masses[chunk] = sum;
    };

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var entities = new Entity[Entities];
        _world.CreateEntities(entities, new Score(0), new Spot(0, 0));
        var random = new Random(3);
        s_scores = new int[Entities];
        s_weights = new float[Entities];
        int row = 0;
        foreach (var item in _world.Query<Score, Spot>())
        {
            item.Component1 = new Score(random.Next(1000));
            item.Component2 = new Spot(random.NextSingle() * 10_000, random.NextSingle() * 10_000);
            s_scores[row] = item.Component1.Value;
            s_weights[row++] = item.Component2.X;
        }

        // The chunks a parallel pass cuts (World.ChunkRows): about four per thread, 1,024 to ParallelChunkRows rows.
        long share = Entities / (Environment.ProcessorCount * 4L);
        s_chunkRows = (int)Math.Clamp((share + 63) & ~63L, 1024, World.ParallelChunkRows);
        s_chunks = (Entities + s_chunkRows - 1) / s_chunkRows;
        s_totals = new long[s_chunks];
        s_masses = new float[s_chunks];
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public long SumSerialRef()
    {
        long total = 0;
        Totals.SumByRef(_world, ref total);
        return total;
    }

    [Benchmark]
    public long SumSerialReduce()
    {
        Totals.SumSerial(_world);
        return _world.ScoreTotal.Value;
    }

    [Benchmark]
    public long SumParallelReduce()
    {
        Totals.SumParallel(_world);
        return _world.ScoreTotal.Value;
    }

    [Benchmark]
    public long SumParallelInterlocked()
    {
        _total.Value = 0;
        var job = new InterlockedSum { Total = _total };
        _world.ForEachParallel<Score, InterlockedSum>(ref job);
        return _total.Value;
    }

    [Benchmark]
    public long SumParallelFor()
    {
        s_total = 0;
        Parallel.For(0, Entities, NoTotal, AddScore, AddTotal);
        return s_total;
    }

    [Benchmark]
    public long SumParallelForOptions()
    {
        s_total = 0;
        Parallel.For(0, Entities, Options, NoTotal, AddScore, AddTotal);
        return s_total;
    }

    [Benchmark]
    public long SumParallelForChunks()
    {
        Parallel.For(0, s_chunks, SumChunk);
        long total = 0;
        for (int i = 0; i < s_chunks; i++) total += s_totals[i];
        return total;
    }

    [Benchmark]
    public float MassSerialRef()
    {
        float mass = 0;
        Totals.MassByRef(_world, ref mass);
        return mass;
    }

    [Benchmark]
    public float MassParallelReduce()
    {
        Totals.MassParallel(_world);
        return _world.SpotMass.Value;
    }

    [Benchmark]
    public float MassParallelInterlocked()
    {
        _mass.Value = 0;
        var job = new InterlockedMass { Mass = _mass };
        _world.ForEachParallel<Spot, InterlockedMass>(ref job);
        return _mass.Value;
    }

    [Benchmark]
    public float MassParallelFor()
    {
        s_mass = 0;
        Parallel.For(0, Entities, NoMass, AddWeight, AddMass);
        return s_mass;
    }

    [Benchmark]
    public float MassParallelForChunks()
    {
        Parallel.For(0, s_chunks, MassChunk);
        float total = 0;
        for (int i = 0; i < s_chunks; i++) total += s_masses[i];
        return total;
    }

    [Benchmark]
    public float BoundsSerialRef()
    {
        var area = Area.Empty;
        Totals.BoundsByRef(_world, ref area);
        return area.MaxX;
    }

    [Benchmark]
    public float BoundsParallelReduce()
    {
        Totals.BoundsParallel(_world);
        return _world.Extent.Area.MaxX;
    }

    [Benchmark]
    public float BoundsParallelLock()
    {
        _box.Value = Area.Empty;
        var job = new LockedBounds { Box = _box, Lock = _lock };
        _world.ForEachParallel<Spot, LockedBounds>(ref job);
        return _box.Value.MaxX;
    }

    public record struct Score(int Value) : IComponent;
    public record struct Spot(float X, float Y) : IComponent;

    public struct ScoreTotal : ISingletonComponent, IReduction<ScoreTotal>
    {
        public long Value;

        public static ScoreTotal Identity => default;
        public static ScoreTotal Combine(ScoreTotal left, ScoreTotal right) => new() { Value = left.Value + right.Value };
    }

    /// <summary>A bounding box.</summary>
    public struct Area
    {
        public float MinX, MinY, MaxX, MaxY;

        public static Area Empty => new() { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };

        public void Include(in Spot spot)
        {
            MinX = MathF.Min(MinX, spot.X);
            MinY = MathF.Min(MinY, spot.Y);
            MaxX = MathF.Max(MaxX, spot.X);
            MaxY = MathF.Max(MaxY, spot.Y);
        }
    }

    /// <summary>The summed X of every spot (a float: rounded per partial result).</summary>
    public struct SpotMass : ISingletonComponent, IReduction<SpotMass>
    {
        public float Value;

        public static SpotMass Identity => default;
        public static SpotMass Combine(SpotMass left, SpotMass right) => new() { Value = left.Value + right.Value };
    }

    /// <summary>The bounding box of every spot: a singleton that combines with itself.</summary>
    public struct Extent : ISingletonComponent, IReduction<Extent>
    {
        public Area Area;

        public static Extent Identity => new() { Area = Area.Empty };

        public static Extent Combine(Extent left, Extent right) => new()
        {
            Area = new Area
            {
                MinX = MathF.Min(left.Area.MinX, right.Area.MinX), MinY = MathF.Min(left.Area.MinY, right.Area.MinY),
                MaxX = MathF.Max(left.Area.MaxX, right.Area.MaxX), MaxY = MathF.Max(left.Area.MaxY, right.Area.MaxY),
            },
        };
    }

    public static class Totals
    {
        [ForEach]
        public static void SumByRef(in Score score, ref long total) => total += score.Value;

        [ForEach]
        public static void SumSerial(in Score score, ref Reduce<ScoreTotal> total) => total.Value.Value += score.Value;

        [ForEach(Parallel = true)]
        public static void SumParallel(in Score score, ref Reduce<ScoreTotal> total) => total.Value.Value += score.Value;

        [ForEach]
        public static void MassByRef(in Spot spot, ref float mass) => mass += spot.X;

        [ForEach(Parallel = true)]
        public static void MassParallel(in Spot spot, ref Reduce<SpotMass> mass) => mass.Value.Value += spot.X;

        [ForEach]
        public static void BoundsByRef(in Spot spot, ref Area area) => area.Include(in spot);

        [ForEach(Parallel = true)]
        public static void BoundsParallel(in Spot spot, ref Reduce<Extent> extent) => extent.Value.Area.Include(in spot);
    }

    private struct InterlockedSum : IForEach<Score>, INoStructuralChanges
    {
        public StrongBox<long> Total;

        public void Execute(ref Score score) => Interlocked.Add(ref Total.Value, score.Value);

        static int IForEach<Score>.WriteMask => 0;
    }

    private struct InterlockedMass : IForEach<Spot>, INoStructuralChanges
    {
        public StrongBox<float> Mass;

        public void Execute(ref Spot spot)
        {
            // Interlocked has no float add: a compare-exchange loop, the usual hand-written one.
            float seen = Volatile.Read(ref Mass.Value);
            while (true)
            {
                float found = Interlocked.CompareExchange(ref Mass.Value, seen + spot.X, seen);
                if (found.Equals(seen)) return;
                seen = found;
            }
        }

        static int IForEach<Spot>.WriteMask => 0;
    }

    private struct LockedBounds : IForEach<Spot>, INoStructuralChanges
    {
        public StrongBox<Area> Box;
        public Lock Lock;

        public void Execute(ref Spot spot)
        {
            lock (Lock) Box.Value.Include(in spot);
        }

        static int IForEach<Spot>.WriteMask => 0;
    }
}
