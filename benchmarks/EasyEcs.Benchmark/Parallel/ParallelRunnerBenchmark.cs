using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The runner of a parallel [ForEach] against <c>Parallel.For</c> as the generator could emit it: the steering of
/// <see cref="ParallelPassBenchmark"/> (the nearest of 16 waypoints, then a step) over 1,000 to 100,000 rows, cut
/// into the same chunks (<c>World.ChunkRows</c>'s rule: about four per thread, 1,024 to 16,384 rows). Parallel.For runs
/// over a copy of the column (the same layout) through static lambdas: with no state argument, they reach the rows, the
/// chunk size and the waypoints through static fields, which a generator could emit but which is not reentrant (two
/// worlds updating on two threads at once would share them). Variants: plain, with a cached <c>ParallelOptions</c>
/// (MaxDegreeOfParallelism), and <c>Parallel.For&lt;TLocal&gt;</c> with localInit / localFinally (a count of moved
/// agents per thread). Small passes show each runner's fixed cost.
/// </summary>
[MemoryDiagnoser]
public class ParallelRunnerBenchmark
{
    [Params(1_000, 10_000, 100_000)] public int Entities;

    private World _world;
    private SteerJob _job;

    private static ParallelPassBenchmark.Agent[] s_agents = [];
    private static ParallelPassBenchmark.Waypoints s_waypoints;
    private static int s_chunkRows, s_chunks, s_moved;
    private static readonly ParallelOptions Options = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };
    private static readonly Action<int> SteerChunkBody = static chunk => SteerChunk(chunk);
    private static readonly Func<int> NoMoves = static () => 0;
    private static readonly Func<int, ParallelLoopState, int, int> CountChunk = static (chunk, _, moved) => moved + SteerChunk(chunk);
    private static readonly Action<int> AddMoves = static moved => Interlocked.Add(ref s_moved, moved);

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        var waypoints = new ParallelPassBenchmark.Waypoints();
        for (int i = 0; i < ParallelPassBenchmark.Waypoints.Count; i++) waypoints[i] = (MathF.Cos(i) * 5_000, MathF.Sin(i) * 5_000);
        _world.CreateEntities(new Entity[Entities], new ParallelPassBenchmark.Agent(0, 0, 1, 0));
        s_agents = new ParallelPassBenchmark.Agent[Entities];
        int row = 0;
        foreach (var item in _world.Query<ParallelPassBenchmark.Agent>())
        {
            item.Component1 = new ParallelPassBenchmark.Agent(row % 10_000, row / 10, 1, 0);
            s_agents[row++] = item.Component1;
        }

        s_waypoints = waypoints;
        _job = new SteerJob { Waypoints = waypoints };
        // World.ChunkRows: about four chunks per thread, a multiple of 64 rows, 1,024 to MaxChunkRows.
        long share = Entities / (Environment.ProcessorCount * 4L);
        s_chunkRows = (int)Math.Clamp((share + 63) & ~63L, 1024, World.MaxChunkRows);
        s_chunks = (Entities + s_chunkRows - 1) / s_chunkRows;
        for (int i = 0; i < 20; i++) Runner(); // the runner's choice of helpers settles
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void Serial()
    {
        var job = _job;
        _world.ForEach<ParallelPassBenchmark.Agent, SteerJob>(ref job);
    }

    [Benchmark]
    public void Runner()
    {
        var job = _job;
        _world.ForEachParallel<ParallelPassBenchmark.Agent, SteerJob>(ref job);
    }

    [Benchmark]
    public void DotNetParallelFor() => Parallel.For(0, s_chunks, SteerChunkBody);

    [Benchmark]
    public void DotNetParallelForOptions() => Parallel.For(0, s_chunks, Options, SteerChunkBody);

    [Benchmark]
    public int DotNetParallelForLocal()
    {
        s_moved = 0;
        Parallel.For(0, s_chunks, NoMoves, CountChunk, AddMoves);
        return s_moved;
    }

    private static int SteerChunk(int chunk)
    {
        var rows = s_agents.AsSpan(chunk * s_chunkRows, Math.Min(s_chunkRows, s_agents.Length - chunk * s_chunkRows));
        for (int i = 0; i < rows.Length; i++) ParallelPassBenchmark.SteerAgent(ref rows[i], in s_waypoints);
        return rows.Length;
    }

    private struct SteerJob : IForEach<ParallelPassBenchmark.Agent>, INoStructuralChanges
    {
        public ParallelPassBenchmark.Waypoints Waypoints;

        public void Execute(ref ParallelPassBenchmark.Agent agent) => ParallelPassBenchmark.SteerAgent(ref agent, in Waypoints);
    }
}
