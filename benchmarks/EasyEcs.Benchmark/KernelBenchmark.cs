using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A [ForEach] method written for one row, over components of one float each: as the per-row loop (<c>Scalar</c>, the
/// job the generator wrote before it vectorized such methods: RyuJIT does not vectorize loops itself), as a batch method
/// written by hand with <c>Vector&lt;float&gt;</c> (<c>HandWritten</c>) and with <c>Vector512&lt;float&gt;</c>
/// (<c>HandWritten512</c>, the widest the hardware may have), and as the generator's vector loop of the same per-row
/// method (<c>Generated</c>, Kernel.cs). <c>Step</c> computes (a clamp, a branch on random data); <c>Integrate</c> and
/// <c>Count</c> stream memory (one float multiply-add, one int increment per row). 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class KernelBenchmark
{
    [Params(1_000, 100_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.SetPull(0.75f);
        _world.Reserve<Heat, Speed, Mass, Ticks>(Entities);
        var random = new Random(1);
        for (int i = 0; i < Entities; i++)
            _world.CreateEntity(new Heat { Value = (float)random.NextDouble() * 200 - 50 },
                new Speed { Value = (float)random.NextDouble() * 20 - 10 }, new Mass { Value = (float)random.NextDouble() * 5 },
                new Ticks { Value = i });
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void StepScalar()
    {
        var job = new StepRows { Pull = _world.Pull };
        _world.ForEach<Heat, Speed, Mass, StepRows>(ref job);
    }

    [Benchmark]
    public void StepHandWritten() => Kernels.StepBatch(_world);

    [Benchmark]
    public void StepHandWritten512() => Kernels.StepBatch512(_world);

    [Benchmark]
    public void StepGenerated() => Kernels.Step(_world);

    [Benchmark]
    public void IntegrateScalar()
    {
        var job = new IntegrateRows();
        _world.ForEach<Heat, Speed, IntegrateRows>(ref job);
    }

    [Benchmark]
    public void IntegrateHandWritten() => Kernels.IntegrateBatch(_world);

    [Benchmark]
    public void IntegrateGenerated() => Kernels.Integrate(_world);

    [Benchmark]
    public void CountScalar()
    {
        var job = new CountRows();
        _world.ForEach<Ticks, CountRows>(ref job);
    }

    [Benchmark]
    public void CountGenerated() => Kernels.Count(_world);

    public struct Heat : IComponent { public float Value; }
    public struct Ticks : IComponent { public int Value; }
    public struct Speed : IComponent { public float Value; }
    public struct Mass : IComponent { public float Value; }
    public record struct Pull(float Value) : ISingletonComponent;

    /// <summary>The per-row job [ForEach] compiled to before: one call of the method per row, inlined.</summary>
    private struct StepRows : IForEach<Heat, Speed, Mass>, INoStructuralChanges
    {
        public Pull Pull;
        public void Execute(ref Heat heat, ref Speed speed, ref Mass mass) => Kernels.Step(ref heat, ref speed, in mass, in Pull);
        static int IForEach<Heat, Speed, Mass>.WriteMask => 3;
    }

    private struct IntegrateRows : IForEach<Heat, Speed>, INoStructuralChanges
    {
        public void Execute(ref Heat heat, ref Speed speed) => Kernels.Integrate(ref heat, in speed);
        static int IForEach<Heat, Speed>.WriteMask => 1;
    }

    private struct CountRows : IForEach<Ticks>, INoStructuralChanges
    {
        public void Execute(ref Ticks ticks) => Kernels.Count(ref ticks);
        static int IForEach<Ticks>.WriteMask => 1;
    }

    public static class Kernels
    {
        private const float Limit = 10f, Drag = 0.25f, Dt = 1f / 30;

        // The per-row methods: the generator writes their vector loops; the Scalar jobs call them per row.
        [ForEach]
        internal static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Pull pull)
        {
            float force = heat.Value * 0.5f - mass.Value * pull.Value;
            speed.Value = MathF.Max(MathF.Min(speed.Value + force - Drag, Limit), -Limit);
            if (heat.Value > 100f) heat.Value = 100f;
            else heat.Value += speed.Value * 0.125f;
        }

        [ForEach]
        internal static void Integrate(ref Heat heat, in Speed speed) => heat.Value += speed.Value * Dt;

        [ForEach]
        internal static void Count(ref Ticks ticks) => ticks.Value++;

        // What a person writes by hand for the same work: a batch method over the runs of rows.
        [ForEach]
        internal static void StepBatch(Span<Heat> heat, Span<Speed> speed, ReadOnlySpan<Mass> mass, in Pull pull)
        {
            var h = MemoryMarshal.Cast<Heat, float>(heat);
            var s = MemoryMarshal.Cast<Speed, float>(speed);
            var m = MemoryMarshal.Cast<Mass, float>(mass);
            int i = 0;
            var half = new Vector<float>(0.5f);
            var p = new Vector<float>(pull.Value);
            var drag = new Vector<float>(Drag);
            var limit = new Vector<float>(Limit);
            var hot = new Vector<float>(100f);
            var eighth = new Vector<float>(0.125f);
            for (; i <= h.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vh = new Vector<float>(h.Slice(i));
                var vs = new Vector<float>(s.Slice(i));
                var vm = new Vector<float>(m.Slice(i));
                var force = vh * half - vm * p;
                vs = Vector.Max(Vector.Min(vs + force - drag, limit), -limit);
                vh = Vector.ConditionalSelect(Vector.GreaterThan<float>(vh, hot), hot, vh + vs * eighth);
                vh.CopyTo(h.Slice(i));
                vs.CopyTo(s.Slice(i));
            }
            for (; i < h.Length; i++) Step(ref heat[i], ref speed[i], in mass[i], in pull);
        }

        [ForEach]
        internal static void StepBatch512(Span<Heat> heat, Span<Speed> speed, ReadOnlySpan<Mass> mass, in Pull pull)
        {
            ref float h = ref Unsafe.As<Heat, float>(ref MemoryMarshal.GetReference(heat));
            ref float s = ref Unsafe.As<Speed, float>(ref MemoryMarshal.GetReference(speed));
            ref float m = ref Unsafe.As<Mass, float>(ref MemoryMarshal.GetReference(mass));
            nint i = 0, n = heat.Length;
            if (Vector512.IsHardwareAccelerated)
            {
                var half = Vector512.Create(0.5f);
                var p = Vector512.Create(pull.Value);
                var drag = Vector512.Create(Drag);
                var limit = Vector512.Create(Limit);
                var hot = Vector512.Create(100f);
                var eighth = Vector512.Create(0.125f);
                for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
                {
                    var vh = Vector512.LoadUnsafe(ref h, (nuint)i);
                    var vs = Vector512.LoadUnsafe(ref s, (nuint)i);
                    var vm = Vector512.LoadUnsafe(ref m, (nuint)i);
                    var force = vh * half - vm * p;
                    vs = Vector512.Max(Vector512.Min(vs + force - drag, limit), -limit);
                    vh = Vector512.ConditionalSelect(Vector512.GreaterThan(vh, hot), hot, vh + vs * eighth);
                    vh.StoreUnsafe(ref h, (nuint)i);
                    vs.StoreUnsafe(ref s, (nuint)i);
                }
            }
            for (; i < n; i++) Step(ref heat[(int)i], ref speed[(int)i], in mass[(int)i], in pull);
        }

        [ForEach]
        internal static void IntegrateBatch(Span<Heat> heat, ReadOnlySpan<Speed> speed)
        {
            var h = MemoryMarshal.Cast<Heat, float>(heat);
            var s = MemoryMarshal.Cast<Speed, float>(speed);
            var dt = new Vector<float>(Dt);
            int i = 0;
            for (; i <= h.Length - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(h.Slice(i)) + new Vector<float>(s.Slice(i)) * dt).CopyTo(h.Slice(i));
            for (; i < h.Length; i++) Integrate(ref heat[i], in speed[i]);
        }
    }
}
