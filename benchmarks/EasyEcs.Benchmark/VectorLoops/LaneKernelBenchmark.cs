using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using static EasyEcs.Benchmark.KernelBenchmark;

namespace EasyEcs.Benchmark;

/// <summary>
/// Package C's prototype (docs/plans/designs/lane-kernel.md): three of KernelBenchmark's methods as the generator would
/// write them over Core's LaneKernel, each job's body once over a width type (W512 / W256 / W128) and the width chain,
/// tails and masked words in Core, against the loops the generator writes today. Integrate is a flat float job, Step a
/// job with a condition and a uniform (its masked words on Holes), Tally a reduction. Setup checks every kernel job
/// against today's, bit for bit.
/// </summary>
[MemoryDiagnoser]
public class LaneKernelBenchmark
{
    [Params(37, 1_000, 100_000)] public int Entities;

    private World _world, _holes;

    [GlobalSetup]
    public void Setup()
    {
        _world = Make(Entities, _ => true);
        _holes = Make(Entities, i => i % 3 != 0);
        Check();
    }

    private static World Make(int entities, Func<int, bool> mass)
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = entities });
        world.SetPull(0.75f);
        world.Reserve<Heat, Speed, Ticks>(entities);
        var random = new Random(1);
        for (int i = 0; i < entities; i++)
        {
            var e = world.CreateEntity(new Heat { Value = (float)random.NextDouble() * 200 - 50 }, new Speed { Value = (float)random.NextDouble() * 20 - 10 },
                new Ticks { Value = random.Next(-1000, 1000) });
            if (mass(i)) e.Set(new Mass { Value = (float)random.NextDouble() * 5 });
        }
        return world;
    }

    [Benchmark(Baseline = true)]
    public void IntegrateGenerated() => Kernels.Integrate(_world);

    [Benchmark]
    public void IntegrateKernel() => LaneJobs.Integrate(_world);

    [Benchmark]
    public void IntegrateTyped() => TypedJobs.Integrate(_world);

    [Benchmark]
    public void StepGenerated() => Kernels.Step(_world);

    [Benchmark]
    public void StepKernel() => LaneJobs.Step(_world);

    [Benchmark]
    public void StepTyped() => TypedJobs.Step(_world);

    [Benchmark]
    public void StepWalk() => WalkJobs.Step(_world);

    [Benchmark]
    public void HolesGenerated() => Kernels.Step(_holes);

    [Benchmark]
    public void HolesKernel() => LaneJobs.Step(_holes);

    [Benchmark]
    public void HolesTyped() => TypedJobs.Step(_holes);

    [Benchmark]
    public void HolesWalk() => WalkJobs.Step(_holes);

    [Benchmark]
    public void TallyGenerated() => Kernels.Tally(_world);

    [Benchmark]
    public void TallyKernel() => LaneJobs.Tally(_world);

    [Benchmark]
    public void TallyTyped() => TypedJobs.Tally(_world);

    // Each kernel job against today's generated one over worlds made alike: every column and the total bit for bit.
    private void Check()
    {
        for (int round = 0; round < 3; round++)
        {
            var (a, b, c) = (Make(Entities, i => i % 3 != 0), Make(Entities, i => i % 3 != 0), Make(Entities, i => i % 3 != 0));
            Kernels.Integrate(a); LaneJobs.Integrate(b); TypedJobs.Integrate(c);
            Kernels.Step(a); LaneJobs.Step(b); TypedJobs.Step(c);
            var d = Make(Entities, i => i % 3 != 0);
            Kernels.Integrate(d); WalkJobs.Step(d);
            if (Columns(a) != Columns(d)) throw new InvalidOperationException("The walking Step differs from the generated one.");
            Kernels.Tally(a); LaneJobs.Tally(b); TypedJobs.Tally(c);
            if (Columns(a) != Columns(b) || !a.GetOrAddSingleton<Tally>().Equals(b.GetOrAddSingleton<Tally>())
                || Columns(a) != Columns(c) || !a.GetOrAddSingleton<Tally>().Equals(c.GetOrAddSingleton<Tally>()))
                throw new InvalidOperationException("A kernel job differs from the generated one.");
        }
    }

    private static string Columns(World world)
    {
        var text = new System.Text.StringBuilder();
        foreach (var item in world.Query<Heat, Speed>())
            text.Append(BitConverter.SingleToInt32Bits(item.Component1.Value)).Append(',').Append(BitConverter.SingleToInt32Bits(item.Component2.Value)).Append(';');
        return text.ToString();
    }
}

/// <summary>The three jobs as the generator would write them over LaneKernel, and their entry points.</summary>
internal static class LaneJobs
{
    public static void Integrate(World world)
    {
        var job = new IntegrateJob();
        world.ForEachMasked<Heat, Speed, IntegrateJob>(ref job, in Unfiltered);
    }

    public static void Step(World world)
    {
        var job = new StepJob { s3 = ref world.SingletonRef<Pull>().Value };
        world.ForEachMasked<Heat, Speed, Mass, StepJob>(ref job, in Unfiltered);
    }

    public static void Tally(World world)
    {
        var job = new TallyJob { a1 = new Reduce<Tally> { Value = KernelBenchmark.Tally.Identity } };
        world.ForEachMasked<Ticks, TallyJob>(ref job, in Unfiltered);
        world.GetOrAddSingleton<Tally>() = job.a1.Value;
    }

    private static readonly QueryFilter Unfiltered = default;

    // heat.Value += speed.Value * Dt
    internal ref struct IntegrateJob : IForEachMasked<Heat, Speed>, IWidthStep
    {
        private ref Heat r1;
        private ref Speed r2;

        public static int LaneBytes => 4;

        public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            WidthKernel.Run(ref this, c1.Length);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            WidthKernel.RunMasked(ref this, pass, c1.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step<W, M>(nint i, Lane<W, int> pass) where W : struct, IWidth<W> where M : struct, IMode
        {
            ref float e1 = ref Unsafe.As<Heat, float>(ref r1);
            var c1 = Lane<W, float>.Load(ref e1, i);
            var c2 = Lane<W, float>.Load(ref Unsafe.As<Speed, float>(ref r2), i);
            c1 = Lane<W, float>.Keep<M>(pass, c1 + c2 * Lane<W, float>.Create(0.033333335f), c1);
            c1.Store(ref e1, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Integrate(ref Unsafe.Add(ref r1, i), in Unsafe.Add(ref r2, i));

        static int IForEachBatch<Heat, Speed>.WriteMask => 1;

        static bool IForEachBatch<Heat, Speed>.OneQuery => true;
    }

    // float force = heat * 0.5f - mass * pull; speed = Max(Min(speed + force - Drag, Limit), -Limit);
    // if (heat > 100f) heat = 100f; else heat += speed * 0.125f;
    internal ref struct StepJob : IForEachMasked<Heat, Speed, Mass>, IWidthStep
    {
        public ref Pull s3;
        private float u3; // the singleton read once per run, as today's loop broadcasts it once
        private ref Heat r1;
        private ref Speed r2;
        private ref Mass r3;

        public static int LaneBytes => 4;

        public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            u3 = s3.Value;
            WidthKernel.Run(ref this, c1.Length);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            u3 = s3.Value;
            WidthKernel.RunMasked(ref this, pass, c1.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step<W, M>(nint i, Lane<W, int> pass) where W : struct, IWidth<W> where M : struct, IMode
        {
            ref float e1 = ref Unsafe.As<Heat, float>(ref r1), e2 = ref Unsafe.As<Speed, float>(ref r2);
            var c1 = Lane<W, float>.Load(ref e1, i);
            var c2 = Lane<W, float>.Load(ref e2, i);
            var c3 = Lane<W, float>.Load(ref Unsafe.As<Mass, float>(ref r3), i);
            var l0 = c1 * Lane<W, float>.Create(0.5f) - c3 * Lane<W, float>.Create(u3);
            c2 = Lane<W, float>.Keep<M>(pass, Lane<W, float>.Max(Lane<W, float>.Min(c2 + l0 - Lane<W, float>.Create(0.25f), Lane<W, float>.Create(10f)), Lane<W, float>.Create(-10f)), c2);
            var m6 = Lane<W, float>.GreaterThan(c1, Lane<W, float>.Create(100f));
            c1 = Lane<W, float>.Keep<M>(pass, Lane<W, float>.Select(m6, Lane<W, float>.Create(100f), c1 + c2 * Lane<W, float>.Create(0.125f)), c1);
            c1.Store(ref e1, i);
            c2.Store(ref e2, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Step(ref Unsafe.Add(ref r1, i), ref Unsafe.Add(ref r2, i), in Unsafe.Add(ref r3, i), in s3);

        static int IForEachBatch<Heat, Speed, Mass>.WriteMask => 3;

        static bool IForEachBatch<Heat, Speed, Mass>.OneQuery => true;
    }

    // tally.Count++; if ((ticks & 1) == 0) tally.Sum += ticks; tally.Low = Min(Low, ticks); tally.High = Max(High, ticks)
    internal ref struct TallyJob : IForEachMasked<Ticks>, IWidthWalk
    {
        public Reduce<Tally> a1;
        private ref Ticks r1;

        public static int LaneBytes => 4;

        public void Execute(EntitySpan entities, Span<Ticks> c1)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            WidthKernel.RunWalk(ref this, c1.Length);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Ticks> c1)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            WidthKernel.RunWalkMasked(ref this, pass, c1.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint Walk<W, M>(nint i, nint rows, ulong pass) where W : struct, IWidth<W> where M : struct, IMode
        {
            ref int e1 = ref Unsafe.As<Ticks, int>(ref r1);
            var id = KernelBenchmark.Tally.Identity;
            var a0 = Lane<W, int>.Create(id.Count);
            var a1s = Lane<W, int>.Create(id.Sum);
            var a2 = Lane<W, int>.Create(id.Low);
            var a3 = Lane<W, int>.Create(id.High);
            for (nint last = rows - Lane<W, int>.Count; i <= last; i += Lane<W, int>.Count)
            {
                if (!WidthKernel.Lanes<W, M>(pass, i, out var p)) continue;
                var c1 = Lane<W, int>.Load(ref e1, i);
                a0 = Lane<W, int>.Keep<M>(p, a0 + Lane<W, int>.One, a0);
                var m0 = Lane<W, int>.Equal(c1 & Lane<W, int>.Create(1), Lane<W, int>.Create(0));
                a1s = Lane<W, int>.Keep<M>(p, Lane<W, int>.Select(m0, a1s + c1, a1s), a1s);
                a2 = Lane<W, int>.Keep<M>(p, Lane<W, int>.Min(a2, c1), a2);
                a3 = Lane<W, int>.Keep<M>(p, Lane<W, int>.Max(a3, c1), a3);
            }
            a1.Value.Count = unchecked(a1.Value.Count + a0.Sum());
            a1.Value.Sum = unchecked(a1.Value.Sum + a1s.Sum());
            for (int j = 0; j < Lane<W, int>.Count; j++) a1.Value.Low = Math.Min(a1.Value.Low, a2[j]);
            for (int j = 0; j < Lane<W, int>.Count; j++) a1.Value.High = Math.Max(a1.Value.High, a3[j]);
            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Tally(in Unsafe.Add(ref r1, i), ref a1);

        static int IForEachBatch<Ticks>.WriteMask => 0;

        static bool IForEachBatch<Ticks>.OneQuery => true;
    }
}

/// <summary>
/// The three jobs in the typed form (ILane: one lane type per width and element type, item l2), a step per vector
/// (LaneStep4: package D's form, whose uniform the JIT left broadcast in the loop).
/// </summary>
internal static class TypedJobs
{
    public static void Integrate(World world)
    {
        var job = new IntegrateJob();
        world.ForEachMasked<Heat, Speed, IntegrateJob>(ref job, in Unfiltered);
    }

    public static void Step(World world)
    {
        var job = new StepJob { s3 = ref world.SingletonRef<Pull>().Value };
        world.ForEachMasked<Heat, Speed, Mass, StepJob>(ref job, in Unfiltered);
    }

    public static void Tally(World world)
    {
        var job = new TallyJob { a1 = new Reduce<Tally> { Value = KernelBenchmark.Tally.Identity } };
        world.ForEachMasked<Ticks, TallyJob>(ref job, in Unfiltered);
        world.GetOrAddSingleton<Tally>() = job.a1.Value;
    }

    private static readonly QueryFilter Unfiltered = default;

    internal ref struct IntegrateJob : IForEachMasked<Heat, Speed>, ILaneStep4
    {
        private ref Heat r1;
        private ref Speed r2;

        public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            nint i = LaneStep4.Run(ref this, c1.Length);
            for (; i < c1.Length; i++) Row(i);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            nint i = LaneStep4.RunMasked(ref this, pass, c1.Length);
            for (; i < c1.Length; i++)
                if (((pass >> (int)i) & 1) != 0) Row(i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step<LF, LI, M>(nint i, LI pass, LF passF) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
        {
            ref float e1 = ref Unsafe.As<Heat, float>(ref r1);
            var c1 = LF.Load(ref e1, i);
            var c2 = LF.Load(ref Unsafe.As<Speed, float>(ref r2), i);
            c1 = LF.Keep<M>(passF, c1 + c2 * LF.Create(0.033333335f), c1);
            LF.Store(c1, ref e1, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Integrate(ref Unsafe.Add(ref r1, i), in Unsafe.Add(ref r2, i));

        static int IForEachBatch<Heat, Speed>.WriteMask => 1;

        static bool IForEachBatch<Heat, Speed>.OneQuery => true;
    }

    internal ref struct StepJob : IForEachMasked<Heat, Speed, Mass>, ILaneStep4
    {
        public ref Pull s3;
        private float u3;
        private ref Heat r1;
        private ref Speed r2;
        private ref Mass r3;

        public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            u3 = s3.Value;
            nint i = LaneStep4.Run(ref this, c1.Length);
            for (; i < c1.Length; i++) Row(i);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            u3 = s3.Value;
            nint i = LaneStep4.RunMasked(ref this, pass, c1.Length);
            for (; i < c1.Length; i++)
                if (((pass >> (int)i) & 1) != 0) Row(i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step<LF, LI, M>(nint i, LI pass, LF passF) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
        {
            ref float e1 = ref Unsafe.As<Heat, float>(ref r1), e2 = ref Unsafe.As<Speed, float>(ref r2);
            var c1 = LF.Load(ref e1, i);
            var c2 = LF.Load(ref e2, i);
            var c3 = LF.Load(ref Unsafe.As<Mass, float>(ref r3), i);
            var l0 = c1 * LF.Create(0.5f) - c3 * LF.Create(u3);
            c2 = LF.Keep<M>(passF, LF.Max(LF.Min(c2 + l0 - LF.Create(0.25f), LF.Create(10f)), LF.Create(-10f)), c2);
            var m6 = LF.GreaterThan(c1, LF.Create(100f));
            c1 = LF.Keep<M>(passF, LF.Select(m6, LF.Create(100f), c1 + c2 * LF.Create(0.125f)), c1);
            LF.Store(c1, ref e1, i);
            LF.Store(c2, ref e2, i);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Step(ref Unsafe.Add(ref r1, i), ref Unsafe.Add(ref r2, i), in Unsafe.Add(ref r3, i), in s3);

        static int IForEachBatch<Heat, Speed, Mass>.WriteMask => 3;

        static bool IForEachBatch<Heat, Speed, Mass>.OneQuery => true;
    }

    internal ref struct TallyJob : IForEachMasked<Ticks>, IStudyWalk4
    {
        public Reduce<Tally> a1;
        private ref Ticks r1;

        public void Execute(EntitySpan entities, Span<Ticks> c1)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            StudyWalk4.RunWalk(ref this, c1.Length);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Ticks> c1)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            StudyWalk4.RunWalkMasked(ref this, pass, c1.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint Walk<LF, LI, M>(nint i, nint rows, ulong pass) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
        {
            ref int e1 = ref Unsafe.As<Ticks, int>(ref r1);
            var id = KernelBenchmark.Tally.Identity;
            var a0 = LI.Create(id.Count);
            var a1s = LI.Create(id.Sum);
            var a2 = LI.Create(id.Low);
            var a3 = LI.Create(id.High);
            for (nint last = rows - LI.Count; i <= last; i += LI.Count)
            {
                if (!LaneKernel.Lanes<LI, M>(pass, i, out var p)) continue;
                var c1 = LI.Load(ref e1, i);
                a0 = LI.Keep<M>(p, a0 + LI.One, a0);
                a1s = LI.Keep<M>(p, LI.AddWhereEqual(a1s, c1, c1 & LI.Create(1), LI.Create(0)), a1s);
                a2 = LI.Keep<M>(p, LI.Min(a2, c1), a2);
                a3 = LI.Keep<M>(p, LI.Max(a3, c1), a3);
            }
            a1.Value.Count = unchecked(a1.Value.Count + LI.Sum(a0));
            a1.Value.Sum = unchecked(a1.Value.Sum + LI.Sum(a1s));
            for (int j = 0; j < LI.Count; j++) a1.Value.Low = Math.Min(a1.Value.Low, LI.Element(a2, j));
            for (int j = 0; j < LI.Count; j++) a1.Value.High = Math.Max(a1.Value.High, LI.Element(a3, j));
            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Tally(in Unsafe.Add(ref r1, i), ref a1);

        static int IForEachBatch<Ticks>.WriteMask => 0;

        static bool IForEachBatch<Ticks>.OneQuery => true;
    }
}

/// <summary>Step in the typed form walking its width itself: its uniforms broadcast before the loop, as today's text does.</summary>
internal static class WalkJobs
{
    public static void Step(World world)
    {
        var job = new StepJob { s3 = ref world.SingletonRef<Pull>().Value };
        world.ForEachMasked<Heat, Speed, Mass, StepJob>(ref job, in Unfiltered);
    }

    private static readonly QueryFilter Unfiltered = default;

    internal ref struct StepJob : IForEachMasked<Heat, Speed, Mass>, IStudyWalk4
    {
        public ref Pull s3;
        private ref Heat r1;
        private ref Speed r2;
        private ref Mass r3;

        public void Execute(EntitySpan entities, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            StudyWalk4.RunWalk(ref this, c1.Length);
        }

        public void Execute(EntitySpan entities, ulong pass, Span<Heat> c1, Span<Speed> c2, Span<Mass> c3)
        {
            r1 = ref MemoryMarshal.GetReference(c1);
            r2 = ref MemoryMarshal.GetReference(c2);
            r3 = ref MemoryMarshal.GetReference(c3);
            StudyWalk4.RunWalkMasked(ref this, pass, c1.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public nint Walk<LF, LI, M>(nint i, nint rows, ulong pass) where LF : struct, ILane<LF, float> where LI : struct, ILane<LI, int> where M : struct, IMode
        {
            ref float e1 = ref Unsafe.As<Heat, float>(ref r1), e2 = ref Unsafe.As<Speed, float>(ref r2), e3 = ref Unsafe.As<Mass, float>(ref r3);
            var u2 = LF.Create(s3.Value);
            for (nint last = rows - LF.Count; i <= last; i += LF.Count)
            {
                if (!LaneKernel.Lanes<LI, M>(pass, i, out var p)) continue;
                var c1 = LF.Load(ref e1, i);
                var c2 = LF.Load(ref e2, i);
                var c3 = LF.Load(ref e3, i);
                var l0 = c1 * LF.Create(0.5f) - c3 * u2;
                c2 = LF.Keep<M>(LI.As<LF, float>(p), LF.Max(LF.Min(c2 + l0 - LF.Create(0.25f), 10f), -10f), c2);
                c1 = LF.Keep<M>(LI.As<LF, float>(p), LF.SelectGreaterThan(c1, LF.Create(100f), LF.Create(100f), c1 + c2 * LF.Create(0.125f)), c1);
                LF.Store(c1, ref e1, i);
                LF.Store(c2, ref e2, i);
            }
            return i;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Row(nint i) => Kernels.Step(ref Unsafe.Add(ref r1, i), ref Unsafe.Add(ref r2, i), in Unsafe.Add(ref r3, i), in s3);

        static int IForEachBatch<Heat, Speed, Mass>.WriteMask => 3;

        static bool IForEachBatch<Heat, Speed, Mass>.OneQuery => true;
    }
}
