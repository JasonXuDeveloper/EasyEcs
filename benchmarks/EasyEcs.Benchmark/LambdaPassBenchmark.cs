using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// One pass over every unit, written three ways: a lambda pass (<c>world.ForEach(split, static (ref Health h, in Regen r,
/// int split) =&gt; …)</c>, intercepted by the job the generator writes for it), the same body as a <c>[ForEach]</c>
/// method of a static class, and the struct job a user writes by hand. The scalar body divides as a long by a value (no
/// vector loop for the integer division of 8-byte lanes); the vector body multiplies and adds (a vector loop for the lambda and the method, the
/// hand-written job one row at a time). 0 bytes every way.
/// </summary>
[MemoryDiagnoser]
public class LambdaPassBenchmark
{
    public record struct Health(int Value) : IComponent;
    public record struct Regen(int Value) : IComponent;

    [Params(1_000, 100_000)] public int Entities;

    private World _world;
    private int _split = 3;
    private int _rate = 2;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _world.Reserve<Health, Regen>(Entities);
        for (int i = 0; i < Entities; i++) _world.CreateEntity(new Health(i), new Regen(i % 17));
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    // ---------------- a scalar body ----------------

    [Benchmark(Baseline = true), BenchmarkCategory("Scalar")]
    public void ScalarMethod() => Healing.Divided(_world, _split);

    [Benchmark, BenchmarkCategory("Scalar")]
    public void ScalarLambda() => _world.ForEach(_split, static (ref Health h, in Regen r, int split) => h.Value += (int)((long)r.Value / split));

    [Benchmark, BenchmarkCategory("Scalar")]
    public void ScalarHandWritten()
    {
        var job = new DividedRows { Split = _split };
        _world.ForEach<Health, Regen, DividedRows>(ref job);
    }

    // ---------------- a vector body ----------------

    [Benchmark, BenchmarkCategory("Vector")]
    public void VectorMethod() => Healing.Scaled(_world, _rate);

    [Benchmark, BenchmarkCategory("Vector")]
    public void VectorLambda() => _world.ForEach(_rate, static (ref Health h, in Regen r, int rate) => h.Value += r.Value * rate + 1);

    [Benchmark, BenchmarkCategory("Vector")]
    public void VectorHandWritten()
    {
        var job = new ScaledRows { Rate = _rate };
        _world.ForEach<Health, Regen, ScaledRows>(ref job);
    }

    private struct DividedRows : IForEach<Health, Regen>, INoStructuralChanges
    {
        public int Split;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(ref Health h, ref Regen r) => h.Value += (int)((long)r.Value / Split);
        static int IForEach<Health, Regen>.WriteMask => 1;
    }

    private struct ScaledRows : IForEach<Health, Regen>, INoStructuralChanges
    {
        public int Rate;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(ref Health h, ref Regen r) => h.Value += r.Value * Rate + 1;
        static int IForEach<Health, Regen>.WriteMask => 1;
    }
}

/// <summary>The bodies of <see cref="LambdaPassBenchmark"/> as [ForEach] methods, run through their generated entry points.</summary>
public static class Healing
{
    [ForEach]
    static void Divided(ref LambdaPassBenchmark.Health h, in LambdaPassBenchmark.Regen r, int split) => h.Value += (int)((long)r.Value / split);

    [ForEach]
    static void Scaled(ref LambdaPassBenchmark.Health h, in LambdaPassBenchmark.Regen r, int rate) => h.Value += r.Value * rate + 1;
}
