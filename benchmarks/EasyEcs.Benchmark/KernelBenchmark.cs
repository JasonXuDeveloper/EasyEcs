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
/// <c>Move4</c> (a position of 2, 3 or 4 floats moved by a velocity: each field alike, a flat loop over the columns' lanes;
/// <c>Move2HandCast</c> is the same by hand, the columns cast to floats), <c>Seek</c> (3 floats: a direction, its length, a
/// division) and <c>Travel</c> (3 doubles) take components of several fields, which the vector loop splits into one
/// vector per field (Interleaved), in a world of their own. <c>Clamp</c> (Math.Clamp by a singleton's bounds, checked
/// per vector), <c>Quantize</c> (float to int and back) and <c>Gate</c> (bool locals joined by &amp;&amp;, || and !, a
/// pattern) are the calls, conversions and masks a vector loop takes. <c>Tally</c> (an int count, sum, Min and Max) and
/// <c>Energy</c> (a float sum, FloatMode.Fast) reduce into a singleton: one accumulator per lane, folded in lane order.
/// <c>Optional</c> takes a <c>Maybe&lt;Mass&gt;</c> over <c>_holes</c> (a lane mask of the rows that have it).
/// <c>Hurt</c> / <c>HurtHot</c> have a
/// cold branch (a call when a health reaches 0) that no row / one row in a hundred takes. <c>MoveVector</c> is the Godot sample's move (a
/// component of one Vector2 assigned whole: a flat loop). <c>Steer</c> uses 3 of the 12 fields of a wide component (permutes
/// gather them into lanes and back on AVX-512, the other 9 kept as loaded). <c>Brighten</c> runs on byte lanes (a saturating sum and a wrapping one, 64 rows per Vector512).
/// <c>Wave</c> calls Sin and Exp per row (FloatMode.Fast: .NET's vector forms). <c>StepParallel</c> / <c>SeekParallel</c> run
/// <c>Step</c> / <c>Seek</c> on several threads, the per-row parallel job against the vector loop in each chunk. <c>Holes</c> / <c>Halves</c> run <c>Step</c> over
/// a table where every third row / a random half of the rows lacks <c>Mass</c> (removed in place): rows the vector loop
/// leaves out (a lane mask per word of 64 rows). 0 bytes either way.
/// </summary>
[MemoryDiagnoser]
public class KernelBenchmark
{
    [Params(1_000, 100_000)] public int Entities;

    private World _world, _fields, _holes, _halves, _pixels, _vectors, _calm, _hot, _craft;

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

        _pixels = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _pixels.Reserve<Lum, Glow>(Entities);
        for (int i = 0; i < Entities; i++)
            _pixels.CreateEntity(new Lum { V = (byte)random.Next(256) }, new Glow { V = (byte)random.Next(256) });

        _calm = Wounded(Entities, 0);
        _hot = Wounded(Entities, 0.01);

        _vectors = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _vectors.SetPull(1f / 60);
        _vectors.Reserve<Pos2D, Vel2D>(Entities);
        for (int i = 0; i < Entities; i++)
            _vectors.CreateEntity(new Pos2D(new Vector2((float)random.NextDouble(), (float)random.NextDouble())),
                new Vel2D(new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f)));

        _craft = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _craft.SetPull(1f / 60);
        _craft.Reserve<Craft>(Entities);
        for (int i = 0; i < Entities; i++)
            _craft.CreateEntity(new Craft { X = (float)random.NextDouble(), VX = (float)random.NextDouble() - 0.5f, VY = (float)random.NextDouble() - 0.5f, Fuel = i });

        _fields = new World(new WorldOptions { InitialEntityCapacity = Entities });
        _fields.Reserve<P2, V2, P3, V3, P4, V4, D3>(Entities);
        float F() => (float)random.NextDouble() * 10 - 5;
        for (int i = 0; i < Entities; i++)
            _fields.CreateEntity(new P2 { X = F(), Y = F() }, new V2 { X = F(), Y = F() }, new P3 { X = F(), Y = F(), Z = F() },
                new V3 { X = F(), Y = F(), Z = F() }, new P4 { X = F(), Y = F(), Z = F(), W = F() }, new V4 { X = F(), Y = F(), Z = F(), W = F() },
                new D3 { X = F(), Y = F(), Z = F() });
    }

    /// <summary>Healths that take no damage, a share <paramref name="rate"/> of them at 0: those take the cold branch every pass.</summary>
    private static World Wounded(int entities, double rate)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = entities });
        world.Reserve<Heat, Speed>(entities);
        var random = new Random(3);
        for (int i = 0; i < entities; i++)
            world.CreateEntity(new Heat { Value = random.NextDouble() < rate ? 0 : 100 }, new Speed());
        return world;
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
        _pixels.Dispose();
        _vectors.Dispose();
        _craft.Dispose();
        _calm.Dispose();
        _hot.Dispose();
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
    public void Move2HandCast() => Kernels.Move2Cast(_fields);

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

    [Benchmark]
    public void TallyScalar()
    {
        var job = new TallyRows { Tally = new Reduce<Tally> { Value = Tally.Identity } };
        _world.ForEachBatch<Ticks, TallyRows>(ref job);
        _world.GetOrAddSingleton<Tally>() = job.Tally.Value;
    }

    [Benchmark]
    public void TallyGenerated() => Kernels.Tally(_world);

    [Benchmark]
    public void EnergyScalar()
    {
        var job = new EnergyRows { Energy = new Reduce<Energy> { Value = Energy.Identity } };
        _world.ForEachBatch<Heat, Speed, EnergyRows>(ref job);
        _world.GetOrAddSingleton<Energy>() = job.Energy.Value;
    }

    [Benchmark]
    public void EnergyGenerated() => Kernels.Energy(_world);

    [Benchmark]
    public void OptionalScalar()
    {
        var job = new OptionalRows();
        _holes.ForEachBatch<Heat, Speed, OptionalRows>(ref job);
    }

    [Benchmark]
    public void OptionalGenerated() => Kernels.Optional(_holes);

    [Benchmark]
    public void WaveScalar()
    {
        var job = new WaveRows();
        _world.ForEach<Heat, Speed, Mass, WaveRows>(ref job);
    }

    [Benchmark]
    public void WaveGenerated() => Kernels.Wave(_world);

    [Benchmark]
    public void HurtScalar()
    {
        var job = new HurtRows();
        _calm.ForEach<Heat, Speed, HurtRows>(ref job);
    }

    [Benchmark]
    public void HurtGenerated() => Kernels.Hurt(_calm);

    [Benchmark]
    public void HurtHotScalar()
    {
        var job = new HurtRows();
        _hot.ForEach<Heat, Speed, HurtRows>(ref job);
    }

    [Benchmark]
    public void HurtHotGenerated() => Kernels.Hurt(_hot);

    [Benchmark]
    public void MoveVectorScalar()
    {
        var job = new MoveVectorRows { Pull = _vectors.Pull };
        _vectors.ForEach<Pos2D, Vel2D, MoveVectorRows>(ref job);
    }

    [Benchmark]
    public void MoveVectorGenerated() => Kernels.MoveVector(_vectors);

    [Benchmark]
    public void SteerScalar()
    {
        var job = new SteerRows { Pull = _craft.Pull };
        _craft.ForEach<Craft, SteerRows>(ref job);
    }

    [Benchmark]
    public void SteerGenerated() => Kernels.Steer(_craft);

    [Benchmark]
    public void BrightenScalar()
    {
        var job = new BrightenRows();
        _pixels.ForEach<Lum, Glow, BrightenRows>(ref job);
    }

    [Benchmark]
    public void BrightenGenerated() => Kernels.Brighten(_pixels);

    [Benchmark]
    public void StepParallelScalar()
    {
        var job = new StepRows { Pull = _world.Pull };
        _world.ForEachParallel<Heat, Speed, Mass, StepRows>(ref job);
    }

    [Benchmark]
    public void StepParallelGenerated() => Kernels.StepParallel(_world);

    [Benchmark]
    public void SeekParallelScalar()
    {
        var job = new SeekRows();
        _fields.ForEachParallel<P3, V3, SeekRows>(ref job);
    }

    [Benchmark]
    public void SeekParallelGenerated() => Kernels.SeekParallel(_fields);

    public record struct Window(float Min, float Max) : ISingletonComponent;

    /// <summary>A count, a sum, a minimum and a maximum of ints: exact in any order of the rows.</summary>
    public struct Tally : ISingletonComponent, IReduction<Tally>
    {
        public int Count, Sum, Low, High;

        public static Tally Identity => new() { Low = int.MaxValue, High = int.MinValue };
        public static Tally Combine(Tally left, Tally right) => new()
        {
            Count = left.Count + right.Count, Sum = left.Sum + right.Sum, Low = Math.Min(left.Low, right.Low), High = Math.Max(left.High, right.High),
        };
    }

    /// <summary>A float sum: vectorized only under FloatMode.Fast, since one sum per lane rounds in another order.</summary>
    public struct Energy : ISingletonComponent, IReduction<Energy>
    {
        public float Total;

        public static Energy Identity => default;
        public static Energy Combine(Energy left, Energy right) => new() { Total = left.Total + right.Total };
    }

    /// <summary>The batch job [ForEach] compiled a reduction to before: the partial result in a local, the method per row.</summary>
    private struct TallyRows : IForEachBatch<Ticks>
    {
        public Reduce<Tally> Tally;

        public void Execute(EntitySpan entities, Span<Ticks> ticks)
        {
            var tally = Tally;
            for (int i = 0; i < ticks.Length; i++) Kernels.Tally(in ticks[i], ref tally);
            Tally = tally;
        }

        static int IForEachBatch<Ticks>.WriteMask => 0;
    }

    private struct EnergyRows : IForEachBatch<Heat, Speed>
    {
        public Reduce<Energy> Energy;

        public void Execute(EntitySpan entities, Span<Heat> heat, Span<Speed> speed)
        {
            var energy = Energy;
            for (int i = 0; i < heat.Length; i++) Kernels.Energy(in heat[i], in speed[i], ref energy);
            Energy = energy;
        }

        static int IForEachBatch<Heat, Speed>.WriteMask => 0;
    }

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

    /// <summary>The batch job a method taking a Maybe compiled to before: the optional column once per run, a presence bit per row.</summary>
    private struct OptionalRows : IForEachBatch<Heat, Speed>
    {
        public void Execute(EntitySpan entities, Span<Heat> heat, Span<Speed> speed)
        {
            var mass = new MaybeColumn<Mass>(in entities, false);
            for (int i = 0; i < heat.Length; i++) Kernels.Optional(ref heat[i], in speed[i], mass[i]);
        }
    }

    private struct HurtRows : IForEach<Heat, Speed>, INoStructuralChanges
    {
        public void Execute(ref Heat heat, ref Speed speed) => Kernels.Hurt(ref heat, in speed);
        static int IForEach<Heat, Speed>.WriteMask => 1;
    }

    public record struct Pos2D(Vector2 Value) : IComponent;
    public record struct Vel2D(Vector2 Value) : IComponent;

    private struct MoveVectorRows : IForEach<Pos2D, Vel2D>, INoStructuralChanges
    {
        public Pull Pull;
        public void Execute(ref Pos2D place, ref Vel2D motion) => Kernels.MoveVector(ref place, in motion, in Pull);
        static int IForEach<Pos2D, Vel2D>.WriteMask => 1;
    }

    /// <summary>A wide component: 12 floats a row, of which Steer uses 3.</summary>
    public struct Craft : IComponent
    {
        public float X, Y, VX, VY, Fuel, Mass, Drag, Heat, Spin, Tilt, Yaw, Roll;
    }

    private struct SteerRows : IForEach<Craft>, INoStructuralChanges
    {
        public Pull Pull;
        public void Execute(ref Craft craft) => Kernels.Steer(ref craft, in Pull);
        static int IForEach<Craft>.WriteMask => 1;
    }

    public struct Lum : IComponent { public byte V; }
    public struct Glow : IComponent { public byte V; }

    private struct BrightenRows : IForEach<Lum, Glow>, INoStructuralChanges
    {
        public void Execute(ref Lum lum, ref Glow glow) => Kernels.Brighten(ref lum, in glow);
        static int IForEach<Lum, Glow>.WriteMask => 1;
    }

    private struct WaveRows : IForEach<Heat, Speed, Mass>
    {
        public void Execute(ref Heat heat, ref Speed speed, ref Mass mass) => Kernels.Wave(ref heat, in speed, in mass);
    }

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

        // Reductions into a singleton: integers exact, so vectorized as written; a float sum only under FloatMode.Fast.
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void Tally(in Ticks ticks, ref Reduce<Tally> tally)
        {
            tally.Value.Count++;
            if ((ticks.Value & 1) == 0) tally.Value.Sum += ticks.Value;
            tally.Value.Low = Math.Min(tally.Value.Low, ticks.Value);
            tally.Value.High = Math.Max(tally.Value.High, ticks.Value);
        }

        [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
        internal static void Energy(in Heat heat, in Speed speed, ref Reduce<Energy> energy) => energy.Value.Total += heat.Value * speed.Value * speed.Value;

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

        // An optional component: Maybe<Mass> as a lane mask of the rows that have it (every third row of _holes lacks it).
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void Optional(ref Heat heat, in Speed speed, Maybe<Mass> mass)
        {
            if (mass.HasValue) heat.Value += speed.Value * Dt / MathF.Max(mass.Value.Value, 0.5f);
            else heat.Value += speed.Value * Dt;
        }

        // A cold branch: a call under a condition few rows meet. A vector with such a row runs its rows through the method.
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void Hurt(ref Heat health, in Speed damage)
        {
            health.Value -= damage.Value;
            if (health.Value <= 0)
            {
                health.Value = 0;
                Died();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Died() => Deaths++;

        internal static int Deaths;

        // The Godot sample's move: a component of a Vector2 assigned whole, Vector2's operators lane by lane (a flat loop).
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void MoveVector(ref Pos2D place, in Vel2D motion, in Pull pull) =>
            place = new Pos2D(place.Value + motion.Value * pull.Value);

        // 3 of a wide component's 12 fields: gathered by permutes into lanes (AVX-512), a few dependent operations each.
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void Steer(ref Craft craft, in Pull pull)
        {
            float d = craft.VX * pull.Value;
            float s = MathF.Sqrt(craft.VY * craft.VY + d * d);
            craft.X = MathF.Max(craft.X + s * pull.Value, 0f);
            craft.VX = craft.VX * craft.VY - s;
        }

        // Narrow lanes: a saturating byte sum (AddSaturate), then a wrapping one past a threshold: 64 rows per Vector512.
        [ForEach(SimdMode = SimdMode.Require)]
        internal static void Brighten(ref Lum lum, in Glow glow)
        {
            lum.V = (byte)Math.Min(lum.V + (glow.V >> 2), 255);
            if (lum.V > 250) lum.V = (byte)(lum.V - glow.V);
        }

        // Sin and Exp per row: .NET's vector approximations under FloatMode.Fast, the CRT's sinf / expf per row otherwise.
        [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
        internal static void Wave(ref Heat heat, in Speed speed, in Mass mass) =>
            heat.Value = MathF.Sin(speed.Value) * 0.5f + MathF.Exp(-mass.Value);

        // Step and Seek on several threads: each chunk's runs of rows through the vector loop (parallel for simd).
        [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
        internal static void StepParallel(ref Heat heat, ref Speed speed, in Mass mass, in Pull pull)
        {
            float force = heat.Value * 0.5f - mass.Value * pull.Value;
            speed.Value = MathF.Max(MathF.Min(speed.Value + force - Drag, Limit), -Limit);
            if (heat.Value > 100f) heat.Value = 100f;
            else heat.Value += speed.Value * 0.125f;
        }

        [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
        internal static void SeekParallel(ref P3 p, ref V3 v)
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

        // Move2 by hand: both columns cast to flat float lanes (a row's X and Y are consecutive), one vector per step.
        [ForEach]
        internal static void Move2Cast(Span<P2> p, ReadOnlySpan<V2> v)
        {
            var pf = MemoryMarshal.Cast<P2, float>(p);
            var vf = MemoryMarshal.Cast<V2, float>(v);
            var dt = new Vector<float>(Dt);
            int i = 0;
            for (; i <= pf.Length - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(pf.Slice(i)) + new Vector<float>(vf.Slice(i)) * dt).CopyTo(pf.Slice(i));
            for (; i < pf.Length; i++) pf[i] += vf[i] * Dt;
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
