using System;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// docs/plans/borrowed-ideas.md item 2, phase 0: 100,000 waiting entities, 1 % waking per tick (<see cref="Timers"/>),
/// per tick: a [ForEach] comparing every wake tick with the clock (the baseline; the generator's vector loop), the same
/// row by row, a [ForEach] counting countdowns down, and a timer wheel visiting only this tick's bucket (the work run
/// through the ids, or a tag set for a [ForEach] over it). Each invocation runs 10 ticks. Setup checks every version did
/// the same work after 400 ticks.
/// </summary>
[MemoryDiagnoser]
public class TimerWheelBenchmark
{
    private const int Ticks = 10;

    [Params(100_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        int[] expected = null;
        foreach (var tick in new Action<World>[] { Timers.ScanTick, Timers.ScanScalarTick, Timers.CountdownTick, Timers.WheelTick, Timers.WheelTagTick })
        {
            using var world = Timers.Create(Entities);
            for (int t = 0; t < 400; t++) tick(world);
            var got = Timers.Counts(world);
            expected ??= got;
            if (!got.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"{tick.Method.Name} did other work");
        }

        _world = Timers.Create(Entities);
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = Ticks)]
    public void Scan()
    {
        for (int t = 0; t < Ticks; t++) Timers.ScanTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void ScanRowByRow()
    {
        for (int t = 0; t < Ticks; t++) Timers.ScanScalarTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Countdown()
    {
        for (int t = 0; t < Ticks; t++) Timers.CountdownTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void Wheel()
    {
        for (int t = 0; t < Ticks; t++) Timers.WheelTick(_world);
    }

    [Benchmark(OperationsPerInvoke = Ticks)]
    public void WheelTag()
    {
        for (int t = 0; t < Ticks; t++) Timers.WheelTagTick(_world);
    }
}
