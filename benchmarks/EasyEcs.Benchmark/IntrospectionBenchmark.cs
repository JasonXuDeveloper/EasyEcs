using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Showcase;

namespace EasyEcs.Benchmark;

/// <summary>
/// Introspection for dashboards and debuggers, on the battle server's world (10,000 units, every status table
/// populated): <c>world.DescribeTables()</c> lists every table with its columns, rows and bytes; <c>entity.Describe()</c>
/// lists one entity's components with their values (boxed: a debugging aid, not a tick path);
/// <c>world.DescribeSchedule()</c> reports the stages of every group with each system's access and waits.
/// </summary>
[MemoryDiagnoser]
public class IntrospectionBenchmark
{
    private BattleServer _server;
    private Entity _player;

    [GlobalSetup]
    public void Setup()
    {
        _server = new BattleServer(10_000);
        for (int i = 0; i < 300; i++) _server.Tick();
        _player = _server.NearestPlayer(default);
    }

    [GlobalCleanup]
    public void Cleanup() => _server.Dispose();

    [Benchmark]
    public int Tables() => _server.Tables().Length;

    [Benchmark]
    public int Entity() => _player.Describe().Length;

    [Benchmark]
    public int Schedule() => _server.World.DescribeSchedule().Groups.Length;
}
