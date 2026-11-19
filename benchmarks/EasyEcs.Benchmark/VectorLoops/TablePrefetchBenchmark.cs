using System;
using BenchmarkDotNet.Attributes;
using static EasyEcs.Benchmark.KernelBenchmark;

namespace EasyEcs.Benchmark;

/// <summary>
/// Many small tables: KernelBenchmark's fragmented world (tables of 5 to 20 rows of Heat, Speed, P2, V2, P3, V3, told
/// apart by tags) at 1,000 tables, in the L2, and at 16,384, past it: what a generated pass pays per table when each
/// table's columns are cold starts. It measured a prefetch of the next table's first lines while walking one, which
/// ran slower in both (microarch.md, "perf-align-prefetch"). Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class TablePrefetchBenchmark
{
    [Params(1_000, 16_384)] public int Tables;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        var random = new Random(5);
        float F() => (float)random.NextDouble() * 10 - 5;
        for (int table = 0; table < Tables; table++)
        {
            int rows = 5 + table % 16;
            _world.Reserve<Heat, Speed, P2, V2, P3, V3>(rows);
            for (int row = 0; row < rows; row++)
            {
                var e = _world.CreateEntity(new Heat { Value = F() }, new Speed { Value = F() }, new P2 { X = F(), Y = F() }, new V2 { X = F(), Y = F() },
                    new P3 { X = F(), Y = F(), Z = F() }, new V3 { X = F(), Y = F(), Z = F() });
                if ((table & 1) != 0) e.Set(new Tag0());
                if ((table & 2) != 0) e.Set(new Tag1());
                if ((table & 4) != 0) e.Set(new Tag2());
                if ((table & 8) != 0) e.Set(new Tag3());
                if ((table & 16) != 0) e.Set(new Tag4());
                if ((table & 32) != 0) e.Set(new Tag5());
                if ((table & 64) != 0) e.Set(new Tag6());
                if ((table & 128) != 0) e.Set(new Tag7());
                if ((table & 256) != 0) e.Set(new Tag8());
                if ((table & 512) != 0) e.Set(new Tag9());
                if ((table & 1024) != 0) e.Set(new TagA());
                if ((table & 2048) != 0) e.Set(new TagB());
                if ((table & 4096) != 0) e.Set(new TagC());
                if ((table & 8192) != 0) e.Set(new TagD());
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public void Integrate() => Kernels.Integrate(_world);

    [Benchmark]
    public void Move2() => Kernels.Move2(_world);

    [Benchmark]
    public void Move3() => Kernels.Move3(_world);

    public struct TagA : IComponent;
    public struct TagB : IComponent;
    public struct TagC : IComponent;
    public struct TagD : IComponent;
}
