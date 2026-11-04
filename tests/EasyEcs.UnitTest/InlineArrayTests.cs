using System;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Eight gains of a band, an inline array.</summary>
[InlineArray(8)]
public struct Bands
{
    private float _b;
}

/// <summary>Four gains, an inline array.</summary>
[InlineArray(4)]
public struct Bands4
{
    private float _b;
}

/// <summary>Sixteen gains, an inline array.</summary>
[InlineArray(16)]
public struct Bands16
{
    private float _b;
}

/// <summary>A component holding an inline array beside a plain field.</summary>
public struct Spectrum : IComponent
{
    public float Level;
    public Bands Gain;
}

/// <summary>Components holding only an inline array: 4, 8 and 16 lanes.</summary>
public struct Gains4 : IComponent { public Bands4 G; }

public struct Gains8 : IComponent { public Bands G; }

public struct Gains16 : IComponent { public Bands16 G; }

/// <summary>What a pass adds to the gains: another component of the same shapes.</summary>
public struct Feed4 : IComponent { public Bands4 G; }

public struct Feed8 : IComponent { public Bands G; }

public struct Feed16 : IComponent { public Bands16 G; }

/// <summary>A component that is an inline array itself.</summary>
[InlineArray(4)]
public struct Quad : IComponent
{
    private int _q;
}

/// <summary>A place of the route and the route: eight places, a value of the pass.</summary>
public struct Stop { public float X, Y; }

[InlineArray(8)]
public struct Stops
{
    private Stop _p;
}

public struct Route { public Stops Points; }

public struct Here : IComponent { public float X, Y; }

public struct Aimed : IComponent { public int Index; }

/// <summary>A method writing the plain field of a component that also holds an inline array (scalar: EECS055 says why).</summary>
[EcsSystem]
public ref struct SpectrumFade
{
#pragma warning disable EECS055 // the scalar form of a component holding an inline array beside the field it writes is what this checks
    [ForEach] static void Fade(ref Spectrum s) => s.Level = s.Level * 0.5f + 1;
#pragma warning restore EECS055
}

/// <summary><see cref="SpectrumFade"/> one row at a time.</summary>
[EcsSystem]
public ref struct SpectrumFadeByRow
{
    [ForEach(SimdMode = SimdMode.Off)] static void Fade(ref Spectrum s) => s.Level = s.Level * 0.5f + 1;
}

/// <summary>Methods reading inline arrays and loops of a constant trip count (Kernel.Unrolled.cs).</summary>
public static class ArrayKernels
{
    // Each element alike: the flat loop over rows * 4, 8, 16 lanes (8 and 16: the only loop; masked words run the method).
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Fade4(ref Gains4 s, in Feed4 by, float fade) { for (int b = 0; b < 4; b++) s.G[b] = s.G[b] * fade + by.G[b]; }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Fade8(ref Gains8 s, in Feed8 by, float fade) { for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * fade + by.G[b]; }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Fade16(ref Gains16 s, in Feed16 by, float fade) { for (int b = 0; b <= 15; b++) s.G[b] = s.G[b] * fade + by.G[b]; }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Fade8Parallel(ref Gains8 s, in Feed8 by, float fade) { for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * fade + by.G[b]; }

    // Elements used apart: the row loop of 4 lanes, a running sum carried from step to step, as the row runs it.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Mix(ref Gains4 g, in Level l)
    {
        float sum = l.Value;
        for (int k = 0; k < 4; k++) sum = sum * 0.5f + g.G[k];
        g.G[0] = sum;
        g.G[3] = g.G[1] - g.G[2] * l.Value;
    }

    // A component that is an inline array, walked by foreach: by ref (written) and by value (summed).
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bump(ref Quad q, in Rank r)
    {
        foreach (ref int v in q) v += r.Value;
        int total = 0;
        foreach (int v in q) total += v;
        q[3] = total;
    }

    // An argmin over a value's inline array (one broadcast per place and field): the lanes run the steps in order.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Nearest(in Here h, ref Aimed aim, in Route route)
    {
        int next = -1;
        float nearest = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float dx = route.Points[i].X - h.X, dy = route.Points[i].Y - h.Y, d = dx * dx + dy * dy;
            if (d > 4 && d < nearest) (nearest, next) = (d, i);
        }
        aim.Index = next;
    }
}

/// <summary>Methods of <see cref="ArrayKernels"/> in a system, so the schedule names the loops they get.</summary>
[EcsSystem]
public ref struct ArrayLoops
{
    [ForEach] static void Fade4(ref Gains4 s, in Feed4 by) { for (int b = 0; b < 4; b++) s.G[b] = s.G[b] * 0.5f + by.G[b]; }

    [ForEach] static void Fade8(ref Gains8 s, in Feed8 by) { for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * 0.5f + by.G[b]; }

    [ForEach] static void Fade16(ref Gains16 s, in Feed16 by) { for (int b = 0; b <= 15; b++) s.G[b] = s.G[b] * 0.5f + by.G[b]; }

    [ForEach] static void Mix(ref Gains4 g, in Level l)
    {
        float sum = l.Value;
        for (int k = 0; k < 4; k++) sum = sum * 0.5f + g.G[k];
        g.G[0] = sum;
    }
}

/// <summary>The same methods row by row (<see cref="SimdMode.Off"/>), to compare against.</summary>
public static class ArrayRows
{
    [ForEach(SimdMode = SimdMode.Off)]
    internal static void Fade8Parallel(ref Gains8 s, in Feed8 by, float fade) { for (int b = 0; b < 8; b++) s.G[b] = s.G[b] * fade + by.G[b]; }
}

public class InlineArrayTests
{
    // An inline array is as many lanes as its length, not one: read as one, a vector loop over the component's other
    // fields took the row for 8 bytes where it is 36 and wrote over the gains of other rows.
    [Test]
    public void AComponentHoldingAnInlineArrayMatchesTheMethodRowByRow()
    {
        using var vector = new World();
        using var scalar = new World();
        vector.AddSystem<SpectrumFade>();
        scalar.AddSystem<SpectrumFadeByRow>();
        var a = new Entity[1000];
        var b = new Entity[1000];
        for (int i = 0; i < a.Length; i++)
        {
            var s = new Spectrum { Level = i };
            for (int k = 0; k < 8; k++) s.Gain[k] = i * 10 + k;
            a[i] = vector.CreateEntity(s);
            b[i] = scalar.CreateEntity(s);
        }
        vector.Update();
        scalar.Update();
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i].Read<Spectrum>();
            var y = b[i].Read<Spectrum>();
            Assert.That(x.Level, Is.EqualTo(y.Level), $"row {i}");
            for (int k = 0; k < 8; k++) Assert.That(x.Gain[k], Is.EqualTo(y.Gain[k]), $"row {i}, gain {k}");
        }
    }

    private static readonly (string Name, Func<int, bool> Hole)[] Patterns = [("none", _ => false), .. KernelTests.Holes];

    private static System.Collections.Generic.IEnumerable<TestCaseData> Cases() =>
        from hole in Patterns from count in KernelTests.Lengths.Concat(KernelTests.HoleCounts).Distinct() select new TestCaseData(hole.Name, count);

    private static T Fill<T>(Random r, int i, int lanes) where T : struct
    {
        var value = default(T);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref value, 1));
        for (int k = 0; k < lanes; k++) floats[k] = KernelTests.NextFloat(r, i * lanes + k);
        return value;
    }

    /// <summary>
    /// Elements of 4, 8 and 16 lanes against the method row by row, bit for bit (NaN, ±0, ±∞ among them), with every row
    /// passing and with rows lacking a component (masked words), over short tables, edges of a vector and 1,000 rows.
    /// </summary>
    [TestCaseSource(nameof(Cases))]
    public void InlineArrayElementsMatchTheMethodBitForBit(string pattern, int count)
    {
        var hole = Patterns.First(h => h.Name == pattern).Hole;
        KernelTests.CheckHoles(count, hole, (r, i) => Fill<Gains4>(r, i, 4), (r, i) => Fill<Feed4>(r, i + 1, 4),
            w => ArrayKernels.Fade4(w, 0.75f), (ref Gains4 s, ref Feed4 by) => ArrayKernels.Fade4(ref s, in by, 0.75f));
        KernelTests.CheckHoles(count, hole, (r, i) => Fill<Gains8>(r, i, 8), (r, i) => Fill<Feed8>(r, i + 1, 8),
            w => ArrayKernels.Fade8(w, -1.5f), (ref Gains8 s, ref Feed8 by) => ArrayKernels.Fade8(ref s, in by, -1.5f));
        KernelTests.CheckHoles(count, hole, (r, i) => Fill<Gains16>(r, i, 16), (r, i) => Fill<Feed16>(r, i + 1, 16),
            w => ArrayKernels.Fade16(w, 3f), (ref Gains16 s, ref Feed16 by) => ArrayKernels.Fade16(ref s, in by, 3f));
        KernelTests.CheckHoles(count, hole, (r, i) => Fill<Gains4>(r, i, 4), (r, i) => new Level { Value = KernelTests.NextFloat(r, i + 7) },
            ArrayKernels.Mix, (ref Gains4 g, ref Level l) => ArrayKernels.Mix(ref g, in l));
        KernelTests.CheckHoles(count, hole, (r, i) => { var q = default(Quad); for (int k = 0; k < 4; k++) q[k] = i % 4 == 0 ? int.MaxValue - k : r.Next(-1000, 1000); return q; },
            (r, i) => new Rank { Value = i % 3 == 0 ? int.MinValue : r.Next(-50, 50) },
            ArrayKernels.Bump, (ref Quad q, ref Rank r) => ArrayKernels.Bump(ref q, in r));
    }

    /// <summary>
    /// A loop of eight steps over a value's inline array, carrying the nearest distance and its index from step to step
    /// (an argmin, ties to the first): every lane runs the steps in order, so each row's answer is the method's.
    /// </summary>
    [TestCaseSource(nameof(Cases))]
    public void UnrolledLoopsKeepLoopCarriedLocals(string pattern, int count)
    {
        var hole = Patterns.First(h => h.Name == pattern).Hole;
        var route = new Route();
        for (int k = 0; k < 8; k++) route.Points[k] = new Stop { X = k * 3 - 10, Y = k % 3 == 0 ? 2 : -k }; // repeated distances: ties
        route.Points[5] = new Stop { X = float.NaN, Y = 0 };
        KernelTests.CheckHoles(count, hole, (r, i) => new Here { X = KernelTests.NextFloat(r, i), Y = (float)Math.Round(KernelTests.NextFloat(r, i + 1)) },
            (r, i) => new Aimed { Index = r.Next() }, w => ArrayKernels.Nearest(w, route), (ref Here h, ref Aimed a) => ArrayKernels.Nearest(in h, ref a, in route));
    }

    /// <summary>The parallel flat loop at 1 and 4 threads ends bit for bit where the method row by row does.</summary>
    [TestCase(1)]
    [TestCase(4)]
    public void ParallelFlatLoopsMatchTheMethodAtOneAndFourThreads(int threads)
    {
        using var vector = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        using var scalar = new World();
        var random = new Random(threads);
        var a = new Entity[20_000];
        var b = new Entity[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            var (s, by) = (Fill<Gains8>(random, i, 8), Fill<Feed8>(random, i + 1, 8));
            a[i] = vector.CreateEntity(s, by);
            b[i] = scalar.CreateEntity(s, by);
        }
        ArrayKernels.Fade8Parallel(vector, 0.5f);
        ArrayRows.Fade8Parallel(scalar, 0.5f);
        for (int i = 0; i < a.Length; i++)
            Assert.That(Bits(a[i].Read<Gains8>()), Is.EqualTo(Bits(b[i].Read<Gains8>())), $"row {i}");
    }

    private static string Bits<T>(in T value) where T : struct =>
        Convert.ToHexString(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>The loops the methods got, as the schedule names them.</summary>
    [Test]
    public void TheScheduleNamesTheLoops()
    {
        using var world = new World();
        world.AddSystem<ArrayLoops>();
        Assert.That(world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops, Is.EqualTo(new[]
        {
            "Fade4: vector (Vector512 or Vector<float>), flat, masked words vector",
            "Fade8: vector (Vector512 or Vector<float>), flat, masked words scalar",
            "Fade16: vector (Vector512 or Vector<float>), flat, masked words scalar",
            "Mix: vector (Vector512 or Vector<float>), interleaved fields, masked words vector",
        }));
    }

    /// <summary>Unrolled loops and flat loops of inline arrays allocate nothing once warm.</summary>
    [Test]
    public void InlineArrayLoopsAllocateNothing()
    {
        using var world = new World();
        var route = new Route();
        world.Reserve<Gains8, Feed8, Here, Aimed>(5_000);
        for (int i = 0; i < 5_000; i++) world.CreateEntity(new Gains8(), new Feed8(), new Here { X = i }, new Aimed());
        void Frame()
        {
            ArrayKernels.Fade8(world, 0.5f);
            ArrayKernels.Nearest(world, route);
        }
        Allocations.WarmUp(Frame, 50);
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero);
    }
}
