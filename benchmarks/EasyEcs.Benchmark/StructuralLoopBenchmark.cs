using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A tag set on the rows a condition passes (<c>if (health.Value &lt; cutoff.Value) unit.SetDown();</c>), at a pass rate of
/// <see cref="PassPercent"/> percent over <see cref="Rows"/> rows, then lifted from all of them in bulk (the same
/// <c>RemoveAll</c> in every case, so each operation starts from the same tables). <c>Scalar</c> is the method one row
/// at a time (SimdMode.Off: a per-row Set during the pass); <c>Vector</c> the generated vector loop, which marks the
/// passing rows and sets the tag on them in bulk after the pass; <c>CommandBuffer</c> the hand-written deferral (the
/// passing ids recorded, then a Set per id); <c>SetAll</c> the bound of a bulk change whose rows are already known (a
/// tag kept on them, <c>world.SetAll</c> over it): no condition evaluated. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class StructuralLoopBenchmark
{
    [Params(10_000, 100_000)] public int Rows;

    [Params(1, 10, 50)] public int PassPercent;

    private World _world;
    private QueryFilter _down, _low;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _world.Reserve<Health>(Rows);
        _world.AddSingleton<Cutoff>() = new Cutoff(PassPercent);
        var random = new System.Random(7);
        for (int i = 0; i < Rows; i++)
        {
            var unit = _world.CreateEntity(new Health(random.Next(100)));
            if (unit.Read<Health>().Value < PassPercent) unit.Set(new Low());
        }
        _down = QueryFilter.Create().With<Down>();
        _low = QueryFilter.Create().With<Low>();
        for (int i = 0; i < 10; i++)
        {
            Scalar();
            Vector();
            CommandBuffer();
            SetAll();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public int Scalar()
    {
        Falls.FallEach(_world);
        return _world.RemoveAll<Down>(in _down);
    }

    [Benchmark]
    public int Vector()
    {
        Falls.Fall(_world);
        return _world.RemoveAll<Down>(in _down);
    }

    [Benchmark]
    public int CommandBuffer()
    {
        Falls.Collect(_world);
        ref var pending = ref _world.GetSingleton<Emitted<Pending>>().Items;
        foreach (ref readonly var unit in pending.AsReadOnlySpan()) _world.GetEntity(unit.Id).Set(new Down());
        pending.Clear();
        return _world.RemoveAll<Down>(in _down);
    }

    [Benchmark]
    public int SetAll()
    {
        _world.SetAll(new Down(), in _low);
        return _world.RemoveAll<Down>(in _down);
    }

    public record struct Health(int Value) : IComponent;
    public record struct Cutoff(int Value) : ISingletonComponent;
    public struct Down : IComponent;
    public struct Low : IComponent;
    public record struct Pending(EntityId Id);
}

public static class Falls
{
    [ForEach(SimdMode = SimdMode.Require), Without<StructuralLoopBenchmark.Down>]
    static void Fall(Entity unit, in StructuralLoopBenchmark.Health health, in StructuralLoopBenchmark.Cutoff cutoff)
    {
        if (health.Value < cutoff.Value) unit.Set(new StructuralLoopBenchmark.Down());
    }

    [ForEach(SimdMode = SimdMode.Off), Without<StructuralLoopBenchmark.Down>]
    static void FallEach(Entity unit, in StructuralLoopBenchmark.Health health, in StructuralLoopBenchmark.Cutoff cutoff)
    {
        if (health.Value < cutoff.Value) unit.Set(new StructuralLoopBenchmark.Down());
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Collect(Entity unit, in StructuralLoopBenchmark.Health health, in StructuralLoopBenchmark.Cutoff cutoff, Emit<StructuralLoopBenchmark.Pending> pending)
    {
        if (health.Value < cutoff.Value) pending.Add(new StructuralLoopBenchmark.Pending(unit.Id));
    }
}
