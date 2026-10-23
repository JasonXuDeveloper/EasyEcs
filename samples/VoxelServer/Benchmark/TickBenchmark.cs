using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using EasyEcs;
using Nino.Core;
using VoxelServer;

BenchmarkSwitcher.FromAssembly(typeof(TickBenchmark).Assembly).Run(args);

/// <summary>
/// A hundred ticks of the server (five seconds of game time) from tick 900 of a run, its second night: zombies
/// hunting, water spread by a day and a half of digging, chunks loaded and unloaded as the bots walk. Every iteration
/// starts from the same state (the world loaded from a save of tick 900, the region files copied back), so each runs
/// the same ticks; the world is built as Program.cs builds it (Startup), without MeasureSystems. A hundred ticks hold
/// every cadence of the simulation: twenty water steps, one spawn, the slices of the systems that look at a share of
/// their rows per tick. Run: <c>dotnet run -c Release --project samples/VoxelServer/Benchmark -- --filter '*'</c>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 2, warmupCount: 4, iterationCount: 12, invocationCount: 1)]
public class TickBenchmark
{
    const int From = 900, Ticks = 100;

    [Params(50, 500, 2000)]
    public int Players;

    World _world = null!;
    RegionFiles _regions = null!, _start = null!;
    readonly NinoArrayBufferWriter _save = new();
    ComponentSet _startup;

    [GlobalSetup]
    public void Setup()
    {
        var capacity = Rules.CapacityFor(Players);
        _regions = new RegionFiles(capacity.Chunks * 4);
        _start = new RegionFiles(capacity.Chunks * 4);
        _world = Startup.Create(_regions, capacity, measure: false);
        Startup.AddPlayers(_world, Players);
        for (int i = 0; i < From; i++) Server.Tick(_world);
        _startup = ComponentSet.Create().WithSingleton<Prefabs>().WithSingleton<Regions>();
        _world.SaveWithout(_save, in _startup);
        _regions.CopyTo(_start);
    }

    [IterationSetup]
    public void Rewind()
    {
        _start.CopyTo(_regions);
        _world.LoadKeeping(_save.WrittenSpan, in _startup, markWritten: false);
    }

    [Benchmark]
    public void HundredTicks()
    {
        for (int i = 0; i < Ticks; i++) Server.Tick(_world);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        _regions.Dispose();
        _start.Dispose();
    }
}
