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
/// <c>Count</c> stream memory (one float multiply-add, one int increment per row). <c>Move2</c> / <c>Move3</c> /
/// <c>Move4</c> (a position of 2, 3 or 4 floats moved by a velocity), <c>Seek</c> (3 floats: a direction, its length, a
/// division) and <c>Travel</c> (3 doubles) take components of several fields, which the vector loop splits into one
/// vector per field (Interleaved), in a world of their own. <c>Clamp</c> (Math.Clamp by a singleton's bounds, checked
/// per vector), <c>Quantize</c> (float to int and back) and <c>Gate</c> (bool locals joined by &amp;&amp;, || and !, a
/// pattern) are the calls, conversions and masks a vector loop takes. <c>Holes</c> / <c>Halves</c> run <c>Step</c> over
/// a table where every third row / a random half of the rows lacks <c>Mass</c> (removed in place): rows the vector loop
/// leaves out (a lane mask per word of 64 rows). 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class KernelBenchmark
{
    [Params(1_000, 100_000)] public int Entities;

    private World _world, _fields, _holes, _halves;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _world.SetPull(0.75f);
        _world.SetWindow(-40f, 120f);
        _world.Reserve<Heat, Speed, Mass, Ticks>(Entities);
        var random = new Random(1);
        for (int i = 0; i < Entities; i++)
            _world.CreateEntity(new Heat { Value = (float)random.NextDouble() * 200 - 50 },
                new Speed { Value = (float)random.NextDouble() * 20 - 10 }, new Mass { Value = (float)random.NextDouble() * 5 },
                new Ticks { Value = i });

        _holes = Holes(Entities, i => i % 3 == 0);
        _halves = Holes(Entities, i => random.Next(2) == 0);

        _fields = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _fields.Reserve<P2, V2, P3, V3, P4, V4, D3>(Entities);
        float F() => (float)random.NextDouble() * 10 - 5;
        for (int i = 0; i < Entities; i++)
            _fields.CreateEntity(new P2 { X = F(), Y = F() }, new V2 { X = F(), Y = F() }, new P3 { X = F(), Y = F(), Z = F() },
                new V3 { X = F(), Y = F(), Z = F() }, new P4 { X = F(), Y = F(), Z = F(), W = F() }, new V4 { X = F(), Y = F(), Z = F(), W = F() },
                new D3 { X = F(), Y = F(), Z = F() });
    }

    /// <summary>A world of Heat, Speed and Mass whose rows in <paramref name="hole"/> lose Mass (in place: a presence bit).</summary>
    private static World Holes(int entities, Func<int, bool> hole)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = entities });
        world.SetPull(0.75f);
        world.Reserve<Heat, Speed, Mass>(entities);
        var random = new Random(2);
        for (int i = 0; i < entities; i++)
        {
            var entity = world.CreateEntity(new Heat { Value = (float)random.NextDouble() * 200 - 50 },
                new Speed { Value = (float)random.NextDouble() * 20 - 10 }, new Mass { Value = (float)random.NextDouble() * 5 });
            if (hole(i)) entity.Remove<Mass>();
        }

        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
        _fields.Dispose();
        _holes.Dispose();
        _halves.Dispose();
    }

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
    public void HolesScalar()
    {
        var job = new StepRows { Pull = _holes.Pull };
        _holes.ForEach<Heat, Speed, Mass, StepRows>(ref job);
    }

    [Benchmark]
    public void HolesGenerated() => Kernels.Step(_holes);

    [Benchmark]
    public void HalvesScalar()
    {
        var job = new StepRows { Pull = _halves.Pull };
        _halves.ForEach<Heat, Speed, Mass, StepRows>(ref job);
    }

    [Benchmark]
    public void HalvesGenerated() => Kernels.Step(_halves);

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

    [Benchmark]
    public void Move2Scalar()
    {
        var job = new Move2Rows();
        _fields.ForEach<P2, V2, Move2Rows>(ref job);
    }

    [Benchmark]
    public void Move2Generated() => Kernels.Move2(_fields);

    [Benchmark]
    public void Move3Scalar()
    {
        var job = new Move3Rows();
        _fields.ForEach<P3, V3, Move3Rows>(ref job);
    }

    [Benchmark]
    public void Move3Generated() => Kernels.Move3(_fields);

    [Benchmark]
    public void Move4Scalar()
    {
        var job = new Move4Rows();
        _fields.ForEach<P4, V4, Move4Rows>(ref job);
    }

    [Benchmark]
    public void Move4Generated() => Kernels.Move4(_fields);

    [Benchmark]
    public void SeekScalar()
    {
        var job = new SeekRows();
        _fields.ForEach<P3, V3, SeekRows>(ref job);
    }

    [Benchmark]
    public void SeekGenerated() => Kernels.Seek(_fields);

    [Benchmark]
    public void TravelScalar()
    {
        var job = new TravelRows();
        _fields.ForEach<D3, TravelRows>(ref job);
    }

    [Benchmark]
    public void TravelGenerated() => Kernels.Travel(_fields);

    [Benchmark]
    public void ClampScalar()
    {
        var job = new ClampRows { Window = _world.Window };
        _world.ForEach<Heat, Speed, ClampRows>(ref job);
    }

    [Benchmark]
    public void ClampGenerated() => Kernels.Clamp(_world);

    [Benchmark]
    public void QuantizeScalar()
    {
        var job = new QuantizeRows();
        _world.ForEach<Heat, Ticks, QuantizeRows>(ref job);
    }

    [Benchmark]
    public void QuantizeGenerated() => Kernels.Quantize(_world);

    [Benchmark]
    public void GateScalar()
    {
        var job = new GateRows();
        _world.ForEach<Heat, Speed, Mass, GateRows>(ref job);
    }

    [Benchmark]
    public void GateGenerated() => Kernels.Gate(_world);

    public record struct Window(float Min, float Max) : ISingletonComponent;

    private struct ClampRows : IForEach<Heat, Speed>, INoStructuralChanges
    {
        public Window Window;
        public void Execute(ref Heat heat, ref Speed speed) => Kernels.Clamp(ref heat, in speed, in Window);
        static int IForEach<Heat, Speed>.WriteMask => 1;
    }

    private struct QuantizeRows : IForEach<Heat, Ticks>, INoStructuralChanges
    {
        public void Execute(ref Heat heat, ref Ticks ticks) => Kernels.Quantize(ref heat, ref ticks);
        static int IForEach<Heat, Ticks>.WriteMask => 3;
    }

    private struct GateRows : IForEach<Heat, Speed, Mass>, INoStructuralChanges
    {
        public void Execute(ref Heat heat, ref Speed speed, ref Mass mass) => Kernels.Gate(ref heat, in speed, in mass);
        static int IForEach<Heat, Speed, Mass>.WriteMask => 1;
    }

    public struct P2 : IComponent { public float X, Y; }
    public struct V2 : IComponent { public float X, Y; }
    public struct P3 : IComponent { public float X, Y, Z; }
    public struct V3 : IComponent { public float X, Y, Z; }
    public struct P4 : IComponent { public float X, Y, Z, W; }
    public struct V4 : IComponent { public float X, Y, Z, W; }
    public struct D3 : IComponent { public double X, Y, Z; }

    private struct Move2Rows : IForEach<P2, V2>, INoStructuralChanges
    {
        public void Execute(ref P2 p, ref V2 v) => Kernels.Move2(ref p, in v);
        static int IForEach<P2, V2>.WriteMask => 1;
    }

    private struct Move3Rows : IForEach<P3, V3>, INoStructuralChanges
    {
        public void Execute(ref P3 p, ref V3 v) => Kernels.Move3(ref p, in v);
        static int IForEach<P3, V3>.WriteMask => 1;
    }

    private struct Move4Rows : IForEach<P4, V4>, INoStructuralChanges
    {
        public void Execute(ref P4 p, ref V4 v) => Kernels.Move4(ref p, in v);
        static int IForEach<P4, V4>.WriteMask => 1;
    }

    private struct SeekRows : IForEach<P3, V3>, INoStructuralChanges
    {
        public void Execute(ref P3 p, ref V3 v) => Kernels.Seek(ref p, ref v);
        static int IForEach<P3, V3>.WriteMask => 3;
    }

    private struct TravelRows : IForEach<D3>, INoStructuralChanges
    {
        public void Execute(ref D3 d) => Kernels.Travel(ref d);
        static int IForEach<D3>.WriteMask => 1;
    }

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

        [ForEach]
        internal static void Move2(ref P2 p, in V2 v)
        {
            p.X += v.X * Dt;
            p.Y += v.Y * Dt;
        }

        [ForEach]
        internal static void Move3(ref P3 p, in V3 v)
        {
            p.X += v.X * Dt;
            p.Y += v.Y * Dt;
            p.Z += v.Z * Dt;
        }

        [ForEach]
        internal static void Move4(ref P4 p, in V4 v)
        {
            p.X += v.X * Dt;
            p.Y += v.Y * Dt;
            p.Z += v.Z * Dt;
            p.W += v.W * Dt;
        }

        // Steers toward a point at a fixed speed: a length, a division, writes to both components.
        [ForEach]
        internal static void Seek(ref P3 p, ref V3 v)
        {
            float dx = 1 - p.X, dy = 2 - p.Y, dz = 3 - p.Z;
            float scale = 4 / MathF.Max(MathF.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6f);
            v.X = dx * scale;
            v.Y = dy * scale;
            v.Z = dz * scale;
            p.X += v.X * Dt;
            p.Y += v.Y * Dt;
            p.Z += v.Z * Dt;
        }

        [ForEach]
        internal static void Travel(ref D3 d)
        {
            d.X = Math.FusedMultiplyAdd(d.Y, 0.5, d.X);
            d.Y = Math.Max(d.Y - d.Z * 0.25, -100);
            d.Z = d.Z > 10 ? -d.Z : d.Z + 1;
        }

        [ForEach]
        internal static void Clamp(ref Heat heat, in Speed speed, in Window window) =>
            heat.Value = Math.Clamp(heat.Value + speed.Value * Dt, window.Min, window.Max);

        // A float truncated to an int, the int back to a float: a fixed-point step.
        [ForEach]
        internal static void Quantize(ref Heat heat, ref Ticks ticks)
        {
            int whole = (int)heat.Value;
            ticks.Value = (ticks.Value & 1023) + whole;
            heat.Value = whole + (heat.Value - whole) * 0.5f + ticks.Value * 1e-4f;
        }

        [ForEach]
        internal static void Gate(ref Heat heat, in Speed speed, in Mass mass)
        {
            bool fast = speed.Value > 2 || speed.Value < -2;
            bool heavy = mass.Value > 2.5f;
            if (fast && !heavy) heat.Value = Math.Clamp(heat.Value, -5f, 5f);
            else if (!fast || heat.Value is > 50 and < 100) heat.Value = MathF.Max(heat.Value - 1, -50);
        }

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
