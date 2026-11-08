using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct Heat : IComponent { public float Value; }
public struct Speed : IComponent { public float Value; }
public struct Mass : IComponent { public float Value; }
public record struct Bits(int Value) : IComponent;     // a positional record struct: its lane is an auto-property
public record struct Mask(int Value) : IComponent;
public record struct Gravity(float Pull) : ISingletonComponent;

// Components of several fields of one lane type: the vector loop splits them into one vector per field (Interleaved).
public record struct Spot2 : IComponent { public float X, Y; }
public record struct Drift2 : IComponent { public float X, Y; }
public record struct Spot3 : IComponent { public float X, Y, Z; }
public record struct Drift3 : IComponent { public float X, Y, Z; }
public record struct Color4(float R, float G, float B, float A) : IComponent;
public record struct Far3 : IComponent { public double X, Y, Z; }
public record struct Pace : IComponent { public double Value; }
public record struct Cell2 : IComponent { public long X, Y; }
public record struct Hash4 : IComponent { public uint A, B, C, D; }
public record struct Locus2(float X, float Y) : IComponent;
public record struct Shift2(float X, float Y) : IComponent;
public record struct Hue4(float R, float G, float B, float A) : IComponent;
public record struct Step2 : IComponent { public long X, Y; }

/// <summary>Kernels written for one row; the generator runs them as Vector&lt;T&gt; loops (Kernel.cs).</summary>
public static class Kernels
{
    public const float Limit = 10f;
    public static readonly float Drag = 0.25f;

    [ForEach]
    internal static void Step(ref Heat heat, ref Speed speed, in Mass mass, in Gravity gravity)
    {
        float force = heat.Value * 0.5f - mass.Value * gravity.Pull;
        speed.Value = MathF.Max(MathF.Min(speed.Value + force - Drag, Limit), -Limit);
        if (heat.Value > 100f) heat.Value = 100f;
        else heat.Value += speed.Value * 0.125f;
    }

    [ForEach]
    internal static void Guard(ref Heat heat, in Mass mass, float scale)
    {
        if (mass.Value <= 0 || float.IsNaN(mass.Value)) return; // NaN: a return clears its lanes
        heat.Value = mass.Value > 1 ? MathF.Sqrt(mass.Value) * scale : heat.Value / mass.Value;
        heat.Value -= 1;
    }

    [ForEach]
    internal static void Shift(ref Bits bits, in Mask mask)
    {
        var mixed = (bits.Value << 3) ^ (bits.Value >> 2) ^ ~mask.Value;
        bits.Value = mixed > 0 && (mask.Value & 1) == 0 ? mixed * 7 - 3 : Math.Max(mixed, mask.Value);
        bits.Value++;
    }
}

// Lanes of several types of one size: int <-> float, uint <-> float, long <-> double, ulong <-> double.
public record struct Level : IComponent { public float Value; }
public record struct Rank : IComponent { public int Value; }
public record struct Code32 : IComponent { public uint Value; }
public record struct Depth : IComponent { public double Value; }
public record struct Stamp : IComponent { public long Value; }
public record struct Serial : IComponent { public ulong Value; }
public record struct Bounds(float Min, float Max) : ISingletonComponent;

/// <summary>Kernels of Math.Clamp, Lerp, the MathF / float forms, conversions, patterns and masks.</summary>
public static class MoreKernels
{
    [ForEach]
    internal static void Clamp(ref Level level, in Rank tally, in Bounds bounds)
    {
        level.Value = Math.Clamp(level.Value, bounds.Min, bounds.Max); // uniform bounds: checked per vector
        if (tally.Value > 0) level.Value = Math.Clamp(level.Value, -1f, 1f);
    }

    [ForEach]
    internal static void ClampLanes(ref Spot2 spot, in Level level) => spot.X = Math.Clamp(spot.X, level.Value, spot.Y); // bounds per lane

    [ForEach]
    internal static void ClampInts(ref Rank tally, in Code32 code) => tally.Value = Math.Clamp(tally.Value, -100, (int)(code.Value & 255));

    [ForEach]
    internal static void Blend(ref Level level, in Rank tally)
    {
        float t = tally.Value * 0.001f;                  // int to float
        float a = level.Value;
        level.Value = float.Lerp(a, 10f, 0.25f) + (a + (5f - a) * t);
    }

    [ForEach]
    internal static void Rounding(ref Spot3 spot)
    {
        spot.X = MathF.Round(spot.X) + MathF.Truncate(spot.Y);
        spot.Y = MathF.CopySign(spot.Y, spot.Z) - float.Max(spot.X, spot.Z);
        spot.Z = float.Min(MathF.Abs(spot.Z), float.Sqrt(spot.Y)) + float.Floor(spot.X) + MathF.Ceiling(spot.Z);
    }

    [ForEach]
    internal static void Convert(ref Level level, ref Rank tally, ref Code32 code)
    {
        int whole = (int)level.Value;                    // saturates, NaN to 0
        uint positive = (uint)level.Value;               // saturates at 0 and uint.MaxValue
        float back = code.Value;                         // uint to float, rounded
        tally.Value = whole + (int)code.Value;           // uint to int: the same bits
        code.Value = positive ^ (uint)tally.Value;
        level.Value = back * 0.5f + tally.Value;
    }

    [ForEach]
    internal static void ConvertWide(ref Depth depth, ref Stamp stamp, ref Serial serial)
    {
        long whole = (long)depth.Value;
        ulong positive = (ulong)depth.Value;
        stamp.Value = whole - (long)serial.Value;
        serial.Value = positive + (ulong)stamp.Value;
        depth.Value = stamp.Value * 0.25 + (double)serial.Value;
    }

    [ForEach]
    internal static void Masks(ref Level level, ref Rank tally)
    {
        bool hot = level.Value > 5;
        bool cold = tally.Value < 0;
        if (hot == cold) tally.Value++;
        if (!(hot || cold) && level.Value is not > 1) level.Value = -level.Value;
        if (level.Value is > 0 and < 10 or float.NaN or -0f) tally.Value += 2;
        else if (tally.Value is 3 or < -50) level.Value = 7;
        if (float.IsNaN(level.Value)) level.Value = 0;
        bool both = hot && !cold;
        level.Value = both != (tally.Value >= 100) ? level.Value + 1 : level.Value;
    }
}

/// <summary>A count, a sum, a minimum and a maximum of ints: exact in any order.</summary>
public record struct Headcount : ISingletonComponent, IReduction<Headcount>
{
    public int Count, Sum, Low, High;

    public static Headcount Identity => new() { Low = int.MaxValue, High = int.MinValue };
    public static Headcount Combine(Headcount left, Headcount right) => new()
    {
        Count = left.Count + right.Count, Sum = left.Sum + right.Sum, Low = Math.Min(left.Low, right.Low), High = Math.Max(left.High, right.High),
    };
}

/// <summary>The extent of floats: Min / Max, exact in any order (NaN and -0 included).</summary>
public struct Extent : ISingletonComponent, IReduction<Extent>
{
    public float Low, High;

    public static Extent Identity => new() { Low = float.PositiveInfinity, High = float.NegativeInfinity };
    public static Extent Combine(Extent left, Extent right) => new() { Low = MathF.Min(left.Low, right.Low), High = MathF.Max(right.High, left.High) };
}

/// <summary>A float sum: its rounding depends on the order of the rows.</summary>
public struct Energy : ISingletonComponent, IReduction<Energy>
{
    public float Total;

    public static Energy Identity => default;
    public static Energy Combine(Energy left, Energy right) => new() { Total = left.Total + right.Total };
}

/// <summary>A sum and a maximum of 8-byte integers.</summary>
public record struct Ledger : ISingletonComponent, IReduction<Ledger>
{
    public long Sum, Max;

    public static Ledger Identity => new() { Max = long.MinValue };
    public static Ledger Combine(Ledger left, Ledger right) => new() { Sum = left.Sum + right.Sum, Max = Math.Max(left.Max, right.Max) };
}

/// <summary>Headcount declared by attributes: PartGenerator writes its Identity and Combine.</summary>
public partial record struct Tallies : ISingletonComponent
{
    [Sum] public int Count;
    [Sum] public int Sum;
    [Min] public int Low;
    [Max] public int High;
}

/// <summary>Counts of rows under conditions (a scalar of popcounts per field), and one field also summed (both kept).</summary>
public partial record struct Counts : ISingletonComponent
{
    [Sum] public int Positive;
    [Sum] public int NotPositive;
    [Sum] public long Odd;
    [Sum] public int Mixed;
}

/// <summary>Extent declared by attributes.</summary>
public partial record struct Reach : ISingletonComponent
{
    [Min] public float Low;
    [Max] public float High;
}

/// <summary>Ledger declared by attributes.</summary>
public partial record struct Books : ISingletonComponent
{
    [Sum] public long Sum;
    [Max] public long Max;
}

/// <summary>Kernels that reduce into a singleton: one accumulator per lane, folded in lane order.</summary>
public static class ReduceKernels
{
    // The reductions of Count, Span and Book declared by [Sum] / [Min] / [Max]: the same vector loops.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Tallies(in Rank rank, ref Reduce<Tallies> tallies)
    {
        tallies.Value.Count++;
        if (rank.Value > 0) tallies.Value.Sum += rank.Value;
        tallies.Value.Low = Math.Min(tallies.Value.Low, rank.Value);
        tallies.Value.High = Math.Max(rank.Value, tallies.Value.High);
    }

    // 1 added on the lanes of a condition, in each form a method writes it: the popcount of the condition's mask.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Counted(in Rank rank, ref Reduce<Counts> counts)
    {
        counts.Value.Positive += rank.Value > 0 ? 1 : 0;
        counts.Value.NotPositive += rank.Value > 0 ? 0 : 1;
        if ((rank.Value & 1) != 0) counts.Value.Odd++;
        if (rank.Value > 5) counts.Value.Mixed += 1;
        counts.Value.Mixed += rank.Value;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Reach(in Level level, ref Reduce<Reach> reach)
    {
        reach.Value.Low = MathF.Min(reach.Value.Low, level.Value);
        reach.Value.High = float.Max(reach.Value.High, level.Value);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Books(in Stamp stamp, ref Reduce<Books> books)
    {
        books.Value.Sum += stamp.Value;
        books.Value.Max = Math.Max(books.Value.Max, stamp.Value);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Count(in Rank rank, ref Reduce<Headcount> headcount)
    {
        headcount.Value.Count++;
        if (rank.Value > 0) headcount.Value.Sum += rank.Value; // under a mask: only the lanes that pass
        headcount.Value.Low = Math.Min(headcount.Value.Low, rank.Value);
        headcount.Value.High = Math.Max(rank.Value, headcount.Value.High);
    }

    // The same through Combine, and a write to the component in the same pass.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Tally(ref Rank rank, ref Reduce<Headcount> headcount)
    {
        rank.Value = rank.Value * 3 - 1;
        headcount.Combine(new Headcount { Count = 1, Sum = rank.Value, Low = rank.Value, High = rank.Value });
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Span(in Level level, ref Reduce<Extent> extent)
    {
        extent.Value.Low = MathF.Min(extent.Value.Low, level.Value);
        extent.Value.High = float.Max(extent.Value.High, level.Value);
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Sum(in Level level, ref Reduce<Energy> energy) => energy.Value.Total += level.Value * 0.5f;

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Book(in Stamp stamp, ref Reduce<Ledger> ledger)
    {
        ledger.Value.Sum += stamp.Value;
        ledger.Value.Max = Math.Max(ledger.Value.Max, stamp.Value);
    }

    // Clamp bounds out of order send a vector's rows to the method, which throws as written.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bounded(ref Spot2 spot, ref Reduce<Headcount> headcount)
    {
        spot.X = Math.Clamp(spot.X, 0, spot.Y);
        headcount.Value.Count++;
    }
}

/// <summary>Sums of int rows into longs: exact in any order, no FloatMode needed.</summary>
public record struct Wallet : ISingletonComponent, IReduction<Wallet>
{
    public long Sum, Squares, Max;
    public int Count;

    public static Wallet Identity => new() { Max = long.MinValue };
    public static Wallet Combine(Wallet left, Wallet right) => new()
    {
        Sum = left.Sum + right.Sum, Squares = left.Squares + right.Squares, Max = Math.Max(left.Max, right.Max), Count = left.Count + right.Count,
    };
}

/// <summary>Sums, a minimum and a maximum of 8-byte integers over 4-byte rows, declared by attributes.</summary>
public partial record struct Purses : ISingletonComponent
{
    [Sum] public long Sum;
    [Min] public long Low;
    [Max] public ulong High;
    [Sum] public int Count;
}

/// <summary>A double sum of float rows: its rounding depends on the order of the rows.</summary>
public struct Fluid : ISingletonComponent, IReduction<Fluid>
{
    public double Total;

    public static Fluid Identity => default;
    public static Fluid Combine(Fluid left, Fluid right) => new() { Total = left.Total + right.Total };
}

/// <summary>An int sum and maximum of short rows.</summary>
public partial record struct Loudness : ISingletonComponent
{
    [Sum] public int Sum;
    [Max] public int Peak;
}

/// <summary>Kernels over lanes of two sizes: a value twice as wide as the lanes is a pair of vectors (Kernel.Pairs.cs).</summary>
public static class PairKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Spend(in Rank rank, ref Reduce<Wallet> wallet)
    {
        wallet.Value.Sum += rank.Value;                         // int to long: each half widened
        wallet.Value.Squares += (long)rank.Value * rank.Value;  // a product of longs: no int overflow
        wallet.Value.Max = Math.Max(wallet.Value.Max, rank.Value * 3L - 1);
        if (rank.Value > 0) wallet.Value.Count++;              // an int field beside the long ones
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Combined(in Rank rank, ref Reduce<Wallet> wallet) =>
        wallet.Combine(new Wallet { Sum = rank.Value, Squares = (long)rank.Value * rank.Value, Max = rank.Value, Count = 1 });

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Purse(in Rank rank, in Code32 code, ref Reduce<Purses> purses)
    {
        long net = (long)rank.Value - code.Value;               // uint to long: zero-extended
        if (net < 0) purses.Value.Sum += net;                   // a comparison of longs: a mask of the lanes
        purses.Value.Low = Math.Min(purses.Value.Low, net);
        purses.Value.High = Math.Max(purses.Value.High, (ulong)rank.Value + code.Value); // int to ulong: sign-extended
        purses.Value.Count++;
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Pour(in Level level, ref Reduce<Fluid> fluid) => fluid.Value.Total += level.Value * 0.5;

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Loud(in Wave16 wave, in Gain16 gain, ref Reduce<Loudness> loud)
    {
        loud.Value.Sum += wave.V;
        loud.Value.Peak = Math.Max(loud.Value.Peak, wave.V * gain.V);
    }

    // A component of long lanes beside int ones: written in place, compared, narrowed back.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Accrue(ref Stamp stamp, ref Rank rank, in Code32 code)
    {
        long next = stamp.Value + rank.Value * 2L - code.Value;
        if (next > 1000) next = -next;
        stamp.Value = next;
        rank.Value = (int)(next >> 1) ^ rank.Value;             // the low bits, as unchecked C# keeps them
        ulong mixed = (ulong)rank.Value + code.Value;
        if (mixed < 100 || stamp.Value == long.MinValue) rank.Value++;
    }

    // A condition on longs is a mask of each half, narrowed; its halves serve the writes of longs under it again.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Settle(ref Stamp stamp, in Rank rank)
    {
        bool big = stamp.Value > 100;
        if (big) stamp.Value -= rank.Value;
        if (rank.Value < 0) big = !big; // written: the halves it held are not its value any more
        stamp.Value = stamp.Value * 2 + (big ? 1L : 0L);
        if (!big && stamp.Value < -100) return;
        stamp.Value ^= rank.Value;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Earn(ref Stamp stamp, in Rank rank)
    {
        stamp.Value += rank.Value;
        if (rank.Value < 0) stamp.Value -= (long)rank.Value * rank.Value;
    }

    // Floats and doubles: float to double is exact, double to float rounds once, as C# converts.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Refine(ref Depth depth, ref Level level, in Rank rank)
    {
        double precise = level.Value * 0.1 + depth.Value;
        depth.Value = precise > 50 ? precise - rank.Value : Math.Sqrt(Math.Abs(precise)); // int to double: exact
        level.Value = (float)(precise * 2) + level.Value;
        if (double.IsNaN(precise) || level.Value < -5) level.Value = 0;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Soak(ref Depth depth, in Level level) => depth.Value = depth.Value * 0.5 + level.Value;

    // Wide components of several fields: two deinterleaved loads per step.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Sway(ref Far3 far, in Level level)
    {
        far.X += level.Value;
        far.Y = far.X * level.Value - far.Z;
        if (far.Y > 10) far.Z = (float)far.Y;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Walk(ref Cell2 cell, in Rank rank)
    {
        cell.X += rank.Value;
        cell.Y = (cell.Y << 1) ^ cell.X;
    }

    // Short lanes into int lanes: the int local is a pair, exact, as the method writes pairs.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Gather(ref Rank total, in Wave16 wave, in Gain16 gain)
    {
        int product = wave.V * gain.V;
        total.Value += product - (wave.V >> 2);
        if (total.Value > 1 << 24) total.Value = (short)total.Value; // narrowed and widened back
    }

    // Byte lanes into ushort lanes: C#'s int arithmetic, its low 16 bits.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Count(ref Sum16 sum, in Shade a, in Tone b)
    {
        sum.V += a.V;
        sum.V -= (ushort)(b.V * 3);
        if (sum.V > 60000) sum.V = (ushort)(a.V + b.V);
    }
}

/// <summary>Kernels over components of several fields.</summary>
public static class FieldKernels
{
    public const float Dt = 1f / 30;

    // A component assigned whole, each field from the same field of the others: a flat loop, masked words included.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Push(ref Locus2 p, in Shift2 v) => p = new Locus2(p.X + v.X, p.Y + v.Y);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Tint(ref Color4 c, in Hue4 h)
    {
        c = new Color4(MathF.Min(c.R + h.R * 0.5f, 1), MathF.Min(c.G + h.G * 0.5f, 1), MathF.Min(c.B + h.B * 0.5f, 1), MathF.Min(c.A + h.A * 0.5f, 1));
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Walk(ref Cell2 c, in Step2 s)
    {
        if (s.X > 0) c.X += s.X * 3; // wraps
        if (s.Y > 0) c.Y += s.Y * 3;
    }

    [ForEach]
    internal static void Drift(ref Spot2 p, in Drift2 v)
    {
        p.X += v.X * Dt;
        p.Y = v.X < 0 ? -p.Y : p.Y + v.Y; // a field written under a mask, another read in it
    }

    [ForEach]
    internal static void Seek(ref Spot3 p, ref Drift3 v, float speed)
    {
        float dx = 1 - p.X, dy = 2 - p.Y, dz = 3 - p.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 0) return;                               // 0 and NaN rows keep their values
        float scale = speed / MathF.Max(length, 1e-6f);
        v.X = dx * scale;
        v.Y = dy * scale;
        v.Z = dz * scale;
        p.X += v.X * Dt;
        p.Y += v.Y * Dt;
        p.Z += v.Z * Dt;
    }

    [ForEach]
    internal static void FadeLanes(ref Color4 c)
    {
        c.A = MathF.Max(c.A - 0.25f, 0);
        if (c.A == 0) c.R = c.G = c.B = 0;
    }

    [ForEach]
    internal static void Travel(ref Far3 p, in Pace pace)
    {
        p.X = Math.FusedMultiplyAdd(p.X, pace.Value, 1.5);
        p.Y = Math.Floor(p.Y * pace.Value);
        p.Z = Math.Abs(p.Z) > 1e10 ? double.NaN : p.Z - pace.Value;
    }

    [ForEach]
    internal static void Step(ref Cell2 c)
    {
        c.X = c.X * 6364136223846793005L + 1442695040888963407L; // wraps
        c.Y ^= c.X >> 7;
    }

    // Each field alike: a flat loop over the columns' lanes (no shuffles).
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Glide(ref Spot2 p, in Drift2 v)
    {
        p.X += v.X * Dt;
        p.Y += v.Y * Dt;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bounce(ref Spot3 p, ref Drift3 v, float speed)
    {
        if (p.X > 5 || p.X < -5) v.X = -v.X; // a branch per field
        if (p.Y > 5 || p.Y < -5) v.Y = -v.Y;
        if (p.Z > 5 || p.Z < -5) v.Z = -v.Z;
        p.X = MathF.FusedMultiplyAdd(v.X, speed, p.X);
        p.Y = MathF.FusedMultiplyAdd(v.Y, speed, p.Y);
        p.Z = MathF.FusedMultiplyAdd(v.Z, speed, p.Z);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Shade(ref Color4 c)
    {
        c.R = MathF.Min(c.R * 0.5f, 1);
        c.G = MathF.Min(c.G * 0.5f, 1);
        c.B = MathF.Min(c.B * 0.5f, 1);
        c.A = MathF.Min(c.A * 0.5f, 1);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Scramble(ref Cell2 c)
    {
        c.X = c.X * 6364136223846793005L + 1442695040888963407L; // wraps
        c.Y = c.Y * 6364136223846793005L + 1442695040888963407L;
    }

    [ForEach]
    internal static void Mix(ref Hash4 h)
    {
        h.A += h.B;
        h.D ^= h.A;
        h.D = (h.D << 16) | (h.D >> 16);
        h.C += h.D;
        h.B ^= h.C;
    }
}

// A tracked component a parallel vector loop writes: every chunk stamps its rows, and chunks share blocks of ticks.
public record struct Warmth : IComponent, ITracked { public float Value; }

/// <summary>
/// Parallel [ForEach] methods with a vector loop (OpenMP's parallel for simd): each chunk's runs of rows go through the
/// loop, the change ticks are stamped by the parallel batch pass, and the chunks' partial results combine in chunk order.
/// </summary>
public static class ParallelKernels
{
    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Spend(in Rank rank, ref Reduce<Wallet> wallet)
    {
        wallet.Value.Sum += rank.Value;
        wallet.Value.Max = Math.Max(wallet.Value.Max, (long)rank.Value * rank.Value);
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Earn(ref Stamp stamp, in Rank rank) => stamp.Value = stamp.Value * 3 + rank.Value;

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Seek(ref Spot3 p, ref Drift3 v, in Gravity gravity, float speed)
    {
        float dx = 1 - p.X, dy = gravity.Pull - p.Y, dz = 3 - p.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length <= 0) return;
        float scale = speed / MathF.Max(length, 1e-6f);
        v.X = dx * scale;
        v.Y = dy * scale;
        v.Z = dz * scale;
        p.X += v.X * FieldKernels.Dt;
        p.Y += v.Y * FieldKernels.Dt;
        p.Z += v.Z * FieldKernels.Dt;
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 64)] // chunks split the blocks of 4,096 rows: opened, shared
    internal static void Glide(ref Spot2 p, in Drift2 v)
    {
        p.X += v.X * FieldKernels.Dt;
        p.Y += v.Y * FieldKernels.Dt;
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Warm(ref Warmth warmth, in Mass mass) => warmth.Value = MathF.Min(warmth.Value + mass.Value * 0.5f, 100f);

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 64)]
    internal static void WarmFine(ref Warmth warmth, in Mass mass) => warmth.Value = MathF.Max(warmth.Value - mass.Value, -100f);

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 128)]
    internal static void Count(in Rank rank, ref Reduce<Headcount> headcount)
    {
        headcount.Value.Count++;
        if (rank.Value > 0) headcount.Value.Sum += rank.Value;
        headcount.Value.Low = Math.Min(headcount.Value.Low, rank.Value);
        headcount.Value.High = Math.Max(rank.Value, headcount.Value.High);
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast, ChunkSize = 256)]
    internal static void Sum(in Level level, ref Reduce<Energy> energy) => energy.Value.Total += level.Value * 0.5f;
}

/// <summary>
/// Math functions in vector loops: degrees to radians and a uniform Sin exactly; Sin, Cos, Exp, Log, Log2 and Hypot per
/// lane only under FloatMode.Fast, as .NET's vector approximations.
/// </summary>
public static class MathKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Turn(ref Level level, in Heat heat, in Gravity gravity) =>
        level.Value = float.DegreesToRadians(level.Value) * MathF.Sin(gravity.Pull) + float.RadiansToDegrees(heat.Value);

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Waves(ref Color4 c, in Level level)
    {
        c.R = MathF.Sin(level.Value);
        c.G = MathF.Cos(level.Value);
        c.B = MathF.Exp(level.Value);
        c.A = MathF.Log(MathF.Abs(level.Value));
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Lengths(ref Spot2 spot, in Level level)
    {
        spot.X = MathF.Log2(MathF.Abs(level.Value));
        spot.Y = float.Hypot(level.Value, spot.Y);
    }

    [ForEach(SimdMode = SimdMode.Require, FloatMode = FloatMode.Fast)]
    internal static void Far(ref Far3 p, in Pace pace)
    {
        p.X = Math.Sin(pace.Value);
        p.Y = Math.Exp(pace.Value * 0.01);
        p.Z = Math.Log(Math.Abs(pace.Value));
    }
}

// Optional components of vector loops: Maybe<T> is a lane mask of the rows that have T, and T's fields are lanes.
public record struct Boost : IComponent { public float Value; }
public record struct Wind2 : IComponent, ITracked { public float X, Y; }

/// <summary>Kernels taking a Maybe&lt;T&gt;: HasValue is a lane mask, Value's fields are lanes read (and written) where rows have T.</summary>
public static class OptionalKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Push(ref Heat heat, ReadOnlyMaybe<Boost> boost)
    {
        if (boost.HasValue) heat.Value += boost.Value.Value * 2;
        else heat.Value -= 1;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Blow(ref Spot2 p, Maybe<Wind2> wind)
    {
        if (!wind.HasValue) return;      // the lanes left have Wind2: no check per read
        wind.Value.X *= 0.5f;
        p.X += wind.Value.X;
        p.Y += wind.Value.Y;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Peek(ref Level level, in Heat heat, ReadOnlyMaybe<Boost> boost) =>
        level.Value = boost.HasValue && boost.Value.Value > 0 ? boost.Value.Value : heat.Value;

    // Reads Value without HasValue: a row without Boost throws, as the method does.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Careless(ref Level level, ReadOnlyMaybe<Boost> boost) => level.Value = boost.Value.Value + 1;
}

/// <summary>
/// <see cref="OptionalKernels"/> in parallel: a Maybe&lt;T&gt;'s presence bits per chunk's run, and the words of 64 rows of
/// which some pass (ForEachMaskedParallel) as a lane mask in each chunk.
/// </summary>
public static class ParallelOptionalKernels
{
    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 64)]
    internal static void Push(ref Heat heat, ReadOnlyMaybe<Boost> boost)
    {
        if (boost.HasValue) heat.Value += boost.Value.Value * 2;
        else heat.Value -= 1;
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require, ChunkSize = 128)]
    internal static void Blow(ref Spot2 p, Maybe<Wind2> wind)
    {
        if (!wind.HasValue) return;
        wind.Value.X *= 0.5f;
        p.X += wind.Value.X;
        p.Y += wind.Value.Y;
    }

    [ForEach(Parallel = true, SimdMode = SimdMode.Require)]
    internal static void Peek(ref Level level, in Heat heat, ReadOnlyMaybe<Boost> boost) =>
        level.Value = boost.HasValue && boost.Value.Value > 0 ? boost.Value.Value : heat.Value;
}

// Struct fields as lanes: Vector2 / Vector3 inside components, whole components assigned, component-wise operators.
public record struct Place2(Vector2 Value) : IComponent;
public record struct Pace2(Vector2 Value) : IComponent;
public record struct Place3(Vector3 Value) : IComponent;
public record struct Pace3(Vector3 Value) : IComponent;
public struct Body3 : IComponent { public Vector3 P; public float M; }
public struct Plain2 : IComponent
{
    public float X, Y;
    public Plain2(float x, float y) { X = x; Y = y; }
}
public record struct Dt2(float Seconds) : ISingletonComponent;
public record struct Gust3(Vector3 Value) : ISingletonComponent;

/// <summary>Kernels over struct values: each lane the IEEE operation the scalar code performs on it.</summary>
public static class StructKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Move(ref Place2 position, in Pace2 velocity, in Dt2 step) =>
        position = new Place2(position.Value + velocity.Value * step.Seconds);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Drift(ref Place3 position, in Pace3 velocity, in Dt2 step) => position.Value += velocity.Value * step.Seconds;

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Blow(ref Place3 position, in Pace3 velocity, in Gust3 gust) =>
        position = position with { Value = position.Value * 0.5f - velocity.Value / 3f + gust.Value };

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bounce(ref Place2 position, ref Pace2 velocity)
    {
        Vector2 p = position.Value, v = velocity.Value;
        if (p.X < 0 || p.X > 10) v.X = -v.X;
        if (p.Y < 0 || p.Y > 10) v.Y = -v.Y;
        position = new Place2(Vector2.Min(Vector2.Max(p, Vector2.Zero), new Vector2(10)));
        velocity = new Pace2(v);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Hold(ref Place2 position, in Pace2 bound) => position = new Place2(Vector2.Clamp(position.Value, -bound.Value, bound.Value));

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Swap(ref Plain2 a) => a = new Plain2(a.Y, a.X);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Weigh(ref Body3 body, in Pace3 velocity)
    {
        body = body with { M = body.M * 0.99f };
        body.P += velocity.Value / body.M;
        body.P.Y = -body.P.Y;
    }
}

/// <summary>
/// Kernels with a cold branch: a call the vector loop cannot express under a condition it can. A vector with a lane
/// that takes it runs its rows through the method, so the calls happen as in the method, in row order.
/// </summary>
public static class ColdKernels
{
    /// <summary>What the calls saw, in order (a test's record: not a pattern for systems, which keep state in data).</summary>
    internal static readonly System.Collections.Generic.List<float> Calls = new();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void Died(float value) => Calls.Add(value);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Hurt(ref Heat health, in Level damage)
    {
        health.Value -= damage.Value;
        if (health.Value <= 0)
        {
            Died(health.Value);
            health.Value = 0;
        }
    }

    // A cold else, a reduction and a return: the reduction's lanes go back to before the vector when it bails.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Census(in Rank rank, ref Reduce<Headcount> count)
    {
        if (rank.Value < -150) return;
        if (rank.Value < 100) count.Value.Count++;
        else for (int i = 0; i < 2; i++) Died(rank.Value + i);
        count.Value.Sum += rank.Value;
    }
}

// Wide components: more fields than a vector loop splits, or fields of other types beside the 4-byte lanes a method uses.
#pragma warning disable EECS091 // the wide rows are what these tests check
public record struct Craft12(float X, float Y, float VX, float VY, float Fuel, float Mass, float Drag, float Heat, float Spin, float Tilt, float Yaw, float Roll) : IComponent;
public struct Mixed : IComponent
{
    public float Speed;
    public bool Alive;
    public byte Team;
    public short Rank;
    public int Hp;
    public double Score;
    public float Armor;
}
/// <summary>Five words of three lane types, every one used and written: a 128-bit vector of 4 rows takes all 4 words of each it stores.</summary>
public struct Five : IComponent
{
    public float A;
    public int B;
    public float C;
    public uint D;
    public float E;
}
/// <summary>Two words a row, a float beside two shorts: the 4 rows' floats lie in 2 vectors, and each store keeps the shorts.</summary>
public struct Slim : IComponent
{
    public float Speed;
    public short Lo, Hi;
}
/// <summary>Eight words a row, three of them used: each field of 4 rows lies in 4 vectors (a 4-register table lookup).</summary>
public record struct Rig8(float X, float A1, float A2, float P, float A4, float A5, float A6, float Q) : IComponent;
/// <summary>
/// Sixteen words a row, three of them used: each row is one Vector512, so the AVX-512 form loads 16 vectors per 16 rows
/// and merges them in a tree before picking the fields (the most IL locals a wide loop declares).
/// </summary>
public record struct Rig16(float X, float A1, float A2, float A3, float A4, float P, float A6, float A7,
    float A8, float A9, float A10, float A11, float A12, float A13, float A14, float Q) : IComponent;
/// <summary>Eight 8-byte words a row (doubles and longs), a few used: a Vector512 of 8 rows per 8 vectors, AVX2's 4 rows per 8.</summary>
public record struct Probe8(double X, long Id, double A2, double P, long Tag, double A5, double A6, double Q) : IComponent;
/// <summary>Doubles beside an int and a short, then a long: 40 bytes a row, the doubles on their 8-byte boundaries.</summary>
public struct Hull : IComponent
{
    public double Speed;
    public int Crew;
    public short Deck;
    public double Mass;
    public double Drag;
    public long Id;
}
#pragma warning restore EECS091

/// <summary>A system with a wide component's method: its schedule line says whether the vector loop runs on this hardware.</summary>
[EcsSystem]
public ref struct WideFormsSystem
{
    [ForEach]
    static void Glide(ref Slim s, in Level drag) => s.Speed = MathF.Max(s.Speed * 0.75f - drag.Value, 0) + drag.Value * drag.Value;
}

/// <summary>
/// Kernels over wide components: the lanes they use gathered by permutes (AVX-512), the rest of each row stored as
/// loaded, so results are the method's bit for bit.
/// </summary>
public static class WideKernels
{
    [ForEach]
    internal static void Steer(ref Craft12 c, in Dt2 step)
    {
        float d = c.VX * step.Seconds;
        float s = MathF.Sqrt(c.VY * c.VY + d * d);
        c.X = MathF.Max(c.X + s * step.Seconds, 0f);
        c.VX = c.VX * c.VY - s;
    }

    [ForEach]
    internal static void Wear(ref Mixed m, in Level damage)
    {
        if (m.Speed > 1) m.Hp -= 1;
        m.Armor = MathF.Max(m.Armor - m.Speed * damage.Value, 0) * 0.5f + damage.Value * damage.Value;
        m.Speed = m.Speed * 0.75f - m.Armor / 8;
    }

    [ForEach]
    internal static void Shift(ref Five f, in Level l)
    {
        f.A = f.A * l.Value + f.C;
        f.B += 3;
        f.C = MathF.Max(f.C, f.E) - l.Value;
        f.D ^= 0x55u;
        f.E = f.E * 0.5f + f.A * f.A - MathF.Min(f.C, l.Value) * 0.25f;
    }

    [ForEach]
    internal static void Coast(ref Slim s, in Level drag) => s.Speed = MathF.Max(s.Speed * 0.75f - drag.Value, 0) + drag.Value * drag.Value;

    [ForEach]
    internal static void Brake(ref Rig8 r, in Level l)
    {
        r.X = r.X - r.P * l.Value;
        r.Q = MathF.Min(r.Q + r.X, 10) * 0.5f + l.Value * l.Value;
    }

    // Brake with every wide form opened where the gate refuses some (ARM64's table lookups): SimdMode.Require.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void BrakeForced(ref Rig8 r, in Level l)
    {
        r.X = r.X - r.P * l.Value;
        r.Q = MathF.Min(r.Q + r.X, 10) * 0.5f + l.Value * l.Value;
    }

    [ForEach]
    internal static void Haul(ref Rig16 r, in Level l)
    {
        r.X = r.X - r.P * l.Value + MathF.Sqrt(MathF.Abs(r.Q));
        r.Q = MathF.Min(r.Q + r.X * r.P, 10) * 0.5f - MathF.Max(r.P - l.Value, 0) * l.Value + l.Value * l.Value;
    }

    // 8-byte words: heavy math over doubles (a square root, divisions), light math over longs.
    [ForEach]
    internal static void Dive(ref Probe8 p, in Depth d)
    {
        p.X = p.X - p.P * d.Value + Math.Sqrt(Math.Abs(p.Q));
        p.Q = Math.Min(p.Q + p.X * p.P, 10) * 0.5 - p.Q / (d.Value + 3);
    }

    // Light math over 8-byte words runs rows on x64 (the wide gate: EECS055 says so) and 128-bit lanes on ARM64.
#pragma warning disable EECS055
    [ForEach]
    internal static void Tally(ref Probe8 p, in Stamp s)
    {
        p.Id = (p.Id + p.Tag * 3 + s.Value) ^ (p.Tag >> 2);
        p.Tag = (p.Tag ^ 0x55) + p.Id - 7;
    }
#pragma warning restore EECS055

    [ForEach]
    internal static void Sail(ref Hull h, in Depth d)
    {
        h.Speed = h.Speed * 0.75 - h.Drag / h.Mass + d.Value;
        h.Mass = Math.Max(h.Mass - Math.Sqrt(Math.Abs(h.Speed)), 1);
    }
}

/// <summary>A system of three methods for the schedule's loop lines: a flat vector loop, a cold branch, one scalar.</summary>
[EcsSystem]
public ref struct LoopsSystem
{
    [ForEach]
    static void Move(ref Place2 position, in Pace2 velocity) => position = new Place2(position.Value + velocity.Value);

    [ForEach]
    static void Hurt(ref Heat health, in Level damage)
    {
        if (health.Value < damage.Value) Console.WriteLine(health.Value);
        health.Value -= damage.Value;
    }

    // Scalar with a reason on purpose: the schedule names it (TheScheduleNamesEachMethodsLoop), and so does EECS055.
#pragma warning disable EECS055
    [ForEach]
    static void Mark(Entity entity, ref Heat health) => health.Value = entity.Id.Index;
#pragma warning restore EECS055
}

/// <summary>
/// ++ / -- used as values (timers, counters), a tag taken as a parameter (a filter, and a Maybe of one: its presence),
/// running totals of a singleton taken by ref: each method has its twin with SimdMode.Off.
/// </summary>
public record struct Census4 : ISingletonComponent { public int Count; public long Sum; public int Low; public int High; }

public static class StepKernels
{
    // Prefix and postfix, under && and ||, in a branch of ?:, after an early return.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Tick(ref Bits bits, in Mask mask) => TickRow(ref bits, in mask);

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void TickScalar(ref Bits bits, in Mask mask) => TickRow(ref bits, in mask);

    internal static void TickRow(ref Bits bits, in Mask mask)
    {
        if (mask.Value == int.MinValue) return;
        if ((mask.Value & 1) == 0 && --bits.Value < 0) bits.Value += 100;
        int before = bits.Value++;
        bits.Value = (mask.Value & 2) != 0 ? ++bits.Value * 3 : before - mask.Value;
        if (bits.Value-- > 50 || ++bits.Value == 7) bits.Value ^= mask.Value;
    }

    // A tag as a parameter: a filter (no column read). EECS026 would have it be [With<Frozen>]; the parameter is the form tested.
#pragma warning disable EECS026
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Chill(ref Heat heat, in Speed speed, in Frozen frozen) => heat.Value = heat.Value * 0.5f + speed.Value;

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void ChillScalar(ref Heat heat, in Speed speed, in Frozen frozen) => heat.Value = heat.Value * 0.5f + speed.Value;
#pragma warning restore EECS026

    // A Maybe of a tag: its presence as the lanes' mask.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Thaw(ref Heat heat, in Speed speed, ReadOnlyMaybe<Frozen> frozen) => heat.Value = frozen.HasValue ? heat.Value - 1 : heat.Value + speed.Value;

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void ThawScalar(ref Heat heat, in Speed speed, ReadOnlyMaybe<Frozen> frozen) => heat.Value = frozen.HasValue ? heat.Value - 1 : heat.Value + speed.Value;

    // Running totals of a singleton taken by ref: a masked count, a long sum, a Min and a Max.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Survey(in Bits bits, ref Census4 census) => SurveyRow(in bits, ref census);

    [ForEach(SimdMode = SimdMode.Off)]
    internal static void SurveyScalar(in Bits bits, ref Census4 census) => SurveyRow(in bits, ref census);

    internal static void SurveyRow(in Bits bits, ref Census4 census)
    {
        if (bits.Value > 0) census.Count++;
        census.Sum += Math.Abs(bits.Value);
        census.Low = Math.Min(census.Low, bits.Value);
        census.High = Math.Max(bits.Value, census.High);
    }
}

// The interop habit: [StructLayout(LayoutKind.Sequential)] is the layout C# gives a struct anyway, so the loop keeps it.
[StructLayout(LayoutKind.Sequential)]
public struct Laid2 : IComponent { public float X, Y; }

/// <summary>A method over a component declared [StructLayout(LayoutKind.Sequential)]: a vector loop like any other.</summary>
public static class LaidKernels
{
    [ForEach]
    internal static void Lay(ref Laid2 l, in Level s)
    {
        l.X = l.X * 0.5f + s.Value;
        l.Y -= l.X * s.Value;
    }
}

/// <summary>The same method in a system, for its schedule line.</summary>
[EcsSystem]
public ref struct LaidSystem
{
    [ForEach]
    static void Lay(ref Laid2 l, in Level s) => LaidKernels.Lay(ref l, in s);
}

// Narrow lanes: C# computes on bytes and shorts in int; the vector loop follows the int's low bits and its range.
public record struct Shade : IComponent { public byte V; }
public record struct Tone : IComponent { public byte V; }
public record struct Ink : IComponent { public byte V; }
public record struct Volume : IComponent { public sbyte V; }
public record struct Pitch : IComponent { public sbyte V; }
public record struct Echo : IComponent { public sbyte V; }
public record struct Wave16 : IComponent { public short V; }
public record struct Gain16 : IComponent { public short V; }
public record struct Mix16 : IComponent { public short V; }
public record struct Word16 : IComponent { public ushort V; }
public record struct Bias16 : IComponent { public ushort V; }
public record struct Sum16 : IComponent { public ushort V; }
public record struct Rgba : IComponent { public byte R, G, B, A; }
public record struct Tint4 : IComponent { public byte R, G, B, A; }

// Enums and bools: an enum's lanes are its underlying integer's, a bool component's one byte per row read as a mask.
public enum Mood : byte { Calm, Wary, Angry, Fled = 200 }
public enum Stance { Idle, Walk = 3, Run = 7, Dead = -1 }
public record struct Temper : IComponent { public Mood V; }
public record struct Posture : IComponent { public Stance V; }
public record struct Roused : IComponent { public bool V; }
public record struct Spotted : IComponent { public bool V; }

/// <summary>Kernels of enum lanes (compared, converted to and from their integer, set to a constant) and of bool components.</summary>
public static class FlagKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Rage(ref Temper t, in Shade a, in Tone b)
    {
        if (a.V > b.V) t.V = Mood.Angry;
        else if (t.V == Mood.Angry) t.V = Mood.Wary;
        else if (t.V != Mood.Fled && b.V > 250) t.V = Mood.Fled;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Cycle(ref Temper t, in Shade a, in Tone b) => t.V = (Mood)(((byte)t.V + a.V) & 3 | (b.V & 0x80));

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Step(ref Posture p, in Rank r, in Level l)
    {
        if (p.V == Stance.Dead) return;
        p.V = r.Value > 100 ? Stance.Run : l.Value > 0.5f ? Stance.Walk : (Stance)((int)p.V & r.Value);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Wake(ref Roused w, in Shade a, in Tone b) => w.V = a.V > b.V || w.V && b.V != 0;

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Toggle(ref Roused w, in Spotted s, in Shade a)
    {
        if (s.V) w.V = !w.V;
        if (a.V == 0) w.V = true;
        else if (a.V == 255) w.V = false;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Paint(ref Ink o, in Roused w, in Shade a)
    {
        if (w.V) o.V = a.V;
        else o.V = (byte)(o.V >> 1);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Calm(ref Temper t, in Roused w, in Spotted s)
    {
        if (!w.V && !s.V) t.V = Mood.Calm;
    }
}

/// <summary>Kernels of narrow lanes: wrap-around arithmetic, saturating Min / Max idioms, comparisons, shifts, locals.</summary>
public static class NarrowKernels
{
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Tint(ref Ink o, in Shade a, ReadOnlyMaybe<Tone> b) =>
        o.V = b.HasValue ? (byte)(a.V + b.Value.V) : (byte)(o.V ^ a.V); // a Maybe<T> of byte lanes: Interleaved.Lanes8 of its presence bits

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Swell(ref Wave16 o, in Gain16 a, ReadOnlyMaybe<Mix16> b)
    {
        if (b.HasValue) o.V = (short)(o.V + a.V * b.Value.V); // of short lanes: Lanes16
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Wrap(ref Ink o, in Shade a, in Tone b) => o.V = (byte)(a.V + b.V * 3 - 7);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Times(ref Ink o, in Shade a, in Tone b) => o.V = (byte)(a.V * b.V ^ ~a.V);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void AddSat(ref Ink o, in Shade a, in Tone b) => o.V = (byte)Math.Min(a.V + b.V, 255);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SubSat(ref Ink o, in Shade a, in Tone b) => o.V = (byte)Math.Max(a.V - b.V, 0);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Band(ref Ink o, in Shade a, in Tone b) => o.V = (byte)Math.Clamp(a.V + b.V, 16, 235);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Pick(ref Ink o, in Shade a, in Tone b)
    {
        if (a.V + b.V > 200) o.V = 1;           // the saturated sum compares alike against 200
        else if (a.V == 7 || b.V is > 250 or < 3) o.V = 2;
        else o.V = a.V > b.V ? a.V : (byte)(b.V >> 1);
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Bits(ref Ink o, in Shade a, in Tone b) => o.V = (byte)((a.V << 3) | (b.V >> 5) ^ (a.V & 0x0F) << 9);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Least(ref Ink o, in Shade a, in Tone b) => o.V = Math.Max(Math.Min(a.V, b.V), (byte)10);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Compound(ref Ink o, in Shade a, in Tone b)
    {
        o.V += a.V;
        o.V -= b.V;
        o.V ^= 0x5A;
        o.V >>= 1;
        o.V++;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Distance(ref Ink o, in Shade a, in Tone b)
    {
        int d = a.V - b.V;
        if (d < 0) d = -d;
        o.V = (byte)d;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SignedSat(ref Echo o, in Volume a, in Pitch b) => o.V = (sbyte)Math.Clamp(a.V + b.V, -128, 127);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SignedSubSat(ref Echo o, in Volume a, in Pitch b) => o.V = (sbyte)Math.Max(Math.Min(a.V - b.V, 127), -128);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SignedShift(ref Echo o, in Volume a, in Pitch b) => o.V = (sbyte)((a.V >> 2) + (b.V >> 9) + (b.V << 1));

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void SignedAbs(ref Echo o, in Volume a, in Pitch b)
    {
        int x = Math.Abs((int)a.V);
        o.V = a.V - b.V > 50 ? (sbyte)x : a.V < b.V ? a.V : b.V;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Mix(ref Mix16 o, in Wave16 a, in Gain16 b) => o.V = (short)Math.Clamp(a.V + b.V, short.MinValue, short.MaxValue);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Scale(ref Mix16 o, in Wave16 a, in Gain16 b) => o.V = (short)(a.V * b.V + (a.V >> 3));

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Drain(ref Sum16 o, in Word16 a, in Bias16 b) => o.V = (ushort)Math.Max(a.V - b.V, 0);

    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Fill(ref Sum16 o, in Word16 a, in Bias16 b) => o.V = a.V > b.V ? (ushort)Math.Min(a.V + b.V, ushort.MaxValue) : (ushort)(b.V >>> 4);

    // Each field alike: the flat loop of narrow lanes.
    [ForEach(SimdMode = SimdMode.Require)]
    internal static void Blend(ref Rgba c, in Tint4 t)
    {
        c.R = (byte)Math.Min(c.R + t.R, 255);
        c.G = (byte)Math.Min(c.G + t.G, 255);
        c.B = (byte)Math.Min(c.B + t.B, 255);
        c.A = (byte)Math.Min(c.A + t.A, 255);
    }
}

public class KernelTests
{
    /// <summary>The lengths that cut a vector short or leave a tail, at both vector widths, and a long run.</summary>
    internal static readonly int[] Lengths = [0, 1, Vector<float>.Count - 1, Vector<float>.Count, Vector<float>.Count + 1,
        Vector512<float>.Count - 1, Vector512<float>.Count + 1, Vector<double>.Count + 1, 2 * Vector<float>.Count, 3 * Vector512<float>.Count + 1, 1000];

    private static readonly float[] Special = [float.NaN, -0f, 0f, 1f, -1f, 2f, 3f, 0.5f, 2.5f, -2.5f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f, float.MaxValue,
        2147483648f, -2147483904f, 4294967296f, 3e9f, -3e9f, float.Epsilon];

    internal static float NextFloat(Random random, int i) => i % 5 == 0 ? Special[random.Next(Special.Length)] : (float)(random.NextDouble() * 20 - 10);

    private static double NextDouble(Random random, int i) =>
        i % 7 == 0 ? new[] { double.NaN, -0d, 1e11, -1e11, double.NegativeInfinity, 9.3e18, 1.9e19, -9.3e18, 2.5 }[random.Next(9)] : random.NextDouble() * 200 - 100;

    private static Level NextLevel(Random r, int i) => new() { Value = NextFloat(r, i) };
    private static Rank NextTally(Random r, int i) => new() { Value = i % 4 == 0 ? new[] { int.MinValue, int.MaxValue, 0, 3, -51 }[r.Next(5)] : r.Next(-200, 200) };
    private static Code32 NextCode(Random r, int i) => new() { Value = i % 4 == 1 ? new[] { uint.MaxValue, 0u, 16777217u, 2147483648u }[r.Next(4)] : (uint)r.Next() };

    private static string Bits<T>(in T a) where T : struct => System.Convert.ToHexString(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)));

    /// <summary>
    /// The same bits, except that any NaN equals any NaN: which NaN an operation of two NaN operands returns is not
    /// defined in .NET (x86 returns the first operand, and the JIT may swap the operands of + and *), so the method
    /// itself can return either; every value that is not NaN must match bit for bit.
    /// </summary>
    private static bool Same<T>(in T a, in T b) where T : struct
    {
        if (MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)))) return true;
        if (Floats(typeof(T))) // inline arrays of floats (InlineArrayTests): lane by lane, any NaN equal to any NaN
        {
            var x = MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in a)));
            var y = MemoryMarshal.Cast<byte, float>(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in b)));
            for (int k = 0; k < x.Length; k++)
                if (float.IsNaN(x[k]) ? !float.IsNaN(y[k]) : BitConverter.SingleToInt32Bits(x[k]) != BitConverter.SingleToInt32Bits(y[k])) return false;
            return true;
        }
        foreach (var field in typeof(T).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            object x = field.GetValue(a)!, y = field.GetValue(b)!;
            bool same = (x, y) switch
            {
                (float f, float g) => float.IsNaN(f) ? float.IsNaN(g) : BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits(g),
                (double f, double g) => double.IsNaN(f) ? double.IsNaN(g) : BitConverter.DoubleToInt64Bits(f) == BitConverter.DoubleToInt64Bits(g),
                _ => x.Equals(y),
            };
            if (!same) return false;
        }
        return true;
    }

    /// <summary>A struct holding an inline array whose every value is a float (no padding: its bytes are floats).</summary>
    private static bool Floats(Type type, bool inline = false)
    {
        if (type == typeof(float)) return inline;
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum) return false;
        inline |= type.IsDefined(typeof(System.Runtime.CompilerServices.InlineArrayAttribute), false);
        var fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return fields.Length > 0 && fields.All(f => Floats(f.FieldType, inline)) && (inline || fields.Any(f => f.FieldType != typeof(float)));
    }

    /// <summary>Components of <paramref name="count"/> entities in two tables, the generated loop, and the method itself per row.</summary>
    private static void Check<T1, T2>(int count, Func<Random, int, T1> make1, Func<Random, int, T2> make2,
        Action<World> generated, ScalarRow<T1, T2> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        using var world = new World();
        world.Reserve<T1, T2>(count);
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = make1(random, i);
            b[i] = make2(random, i);
            entities[i] = world.CreateEntity(a[i], b[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) scalar(ref a[i], ref b[i]);
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {Bits(entities[i].Get<T1>())} instead of {Bits(a[i])} ({b[i]})");
            Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {entities[i].Get<T2>()} instead of {b[i]}");
        }
    }

    internal delegate void ScalarRow<T1, T2>(ref T1 a, ref T2 b);

    private static Vector2 NextVector2(Random r, int i) => new(NextFloat(r, i), NextFloat(r, i + 1));
    private static Vector3 NextVector3(Random r, int i) => new(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2));

    /// <summary>
    /// Components holding Vector2 / Vector3, assigned whole (<c>new T(...)</c>, <c>with</c>), through compound operators,
    /// struct locals and constructors that set fields: lane by lane the method's results, bit for bit.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void StructValuesMatchTheMethodBitForBit(int count)
    {
        foreach (float dt in new[] { 1f / 60, float.NaN, -3e7f })
        {
            Check(count, (r, i) => new Place2(NextVector2(r, i)), (r, i) => new Pace2(NextVector2(r, i + 2)), w => { w.SetDt2(dt); StructKernels.Move(w); },
                (ref Place2 p, ref Pace2 v) => StructKernels.Move(ref p, in v, new Dt2(dt)));
            Check(count, (r, i) => new Place3(NextVector3(r, i)), (r, i) => new Pace3(NextVector3(r, i + 3)), w => { w.SetDt2(dt); StructKernels.Drift(w); },
                (ref Place3 p, ref Pace3 v) => StructKernels.Drift(ref p, in v, new Dt2(dt)));
            var gust = new Gust3(new Vector3(dt, 1, -2));
            Check(count, (r, i) => new Place3(NextVector3(r, i)), (r, i) => new Pace3(NextVector3(r, i + 3)), w => { w.SetGust3(gust.Value); StructKernels.Blow(w); },
                (ref Place3 p, ref Pace3 v) => StructKernels.Blow(ref p, in v, in gust));
        }
        Check(count, (r, i) => new Place2(NextVector2(r, i) * 2), (r, i) => new Pace2(NextVector2(r, i + 2)), StructKernels.Bounce,
            (ref Place2 p, ref Pace2 v) => StructKernels.Bounce(ref p, ref v));
        // Clamp with min above max on the rows of a negative bound, NaN and the zeros among the values.
        Check(count, (r, i) => new Place2(NextVector2(r, i) * 2), (r, i) => new Pace2(NextVector2(r, i + 2)), StructKernels.Hold,
            (ref Place2 p, ref Pace2 v) => StructKernels.Hold(ref p, in v));
        Check(count, (r, i) => new Plain2(NextFloat(r, i), NextFloat(r, i + 1)), (r, i) => new Heat(), StructKernels.Swap,
            (ref Plain2 a, ref Heat _) => StructKernels.Swap(ref a));
        Check(count, (r, i) => new Body3 { P = NextVector3(r, i), M = NextFloat(r, i + 3) }, (r, i) => new Pace3(NextVector3(r, i + 4)), StructKernels.Weigh,
            (ref Body3 b, ref Pace3 v) => StructKernels.Weigh(ref b, in v));
    }

    private static Craft12 NextCraft(Random r, int i) => new(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3), i, -i, 2, 3, 4, 5, 6, 7);

    private static Mixed NextMixed(Random r, int i) => new()
    {
        Speed = NextFloat(r, i), Alive = i % 2 == 0, Team = (byte)i, Rank = (short)-i, Hp = r.Next(-5, 5), Score = i * 0.5, Armor = NextFloat(r, i + 1),
    };

    /// <summary>
    /// Wide components (12 fields of a record struct, fields of five types): the lanes a method uses are gathered and
    /// written back by permutes, every other field kept as loaded, in tables of any length and with rows that lack the
    /// component (their masked words run the method).
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void WideComponentsMatchTheMethodBitForBit(int count)
    {
        foreach (float dt in new[] { 1f / 60, float.NaN, -3e7f })
            Check(count, NextCraft, (r, i) => new Heat(), w => { w.SetDt2(dt); WideKernels.Steer(w); }, (ref Craft12 c, ref Heat _) => WideKernels.Steer(ref c, new Dt2(dt)));
        Check(count, NextMixed, NextLevel, WideKernels.Wear, (ref Mixed m, ref Level l) => WideKernels.Wear(ref m, in l));
        CheckHoles(count, i => i % 3 == 1, NextMixed, NextLevel, WideKernels.Wear, (ref Mixed m, ref Level l) => WideKernels.Wear(ref m, in l));
    }

    /// <summary>
    /// Wide components of every shape the 128-bit form (ARM64's table lookups) meets: every word written (each stored
    /// vector taken whole), a float beside shorts (its 4 rows in 2 vectors, the shorts kept), 16 words a row (each field of
    /// 4 rows in 4 vectors); results the method's bit for bit wherever a form runs, and the method's own elsewhere.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void WideComponentsOfEveryShapeMatchTheMethodBitForBit(int count)
    {
        Check(count, (r, i) => new Five { A = NextFloat(r, i), B = r.Next(), C = NextFloat(r, i + 1), D = (uint)r.Next(), E = NextFloat(r, i + 2) }, NextLevel,
            WideKernels.Shift, (ref Five f, ref Level l) => WideKernels.Shift(ref f, in l));
        Check(count, (r, i) => new Slim { Speed = NextFloat(r, i), Lo = (short)i, Hi = (short)-i }, NextLevel,
            WideKernels.Coast, (ref Slim s, ref Level l) => WideKernels.Coast(ref s, in l));
        CheckHoles(count, i => i % 4 == 2, (r, i) => new Slim { Speed = NextFloat(r, i), Lo = (short)i, Hi = (short)-i }, NextLevel,
            WideKernels.Coast, (ref Slim s, ref Level l) => WideKernels.Coast(ref s, in l));
        Check(count, (r, i) => new Rig8(NextFloat(r, i), 1, 2, NextFloat(r, i + 1), 4, 5, 6, NextFloat(r, i + 2)), NextLevel,
            WideKernels.Brake, (ref Rig8 g, ref Level l) => WideKernels.Brake(ref g, in l));
        Check(count, (r, i) => new Rig8(NextFloat(r, i), 1, 2, NextFloat(r, i + 1), 4, 5, 6, NextFloat(r, i + 2)), NextLevel,
            WideKernels.BrakeForced, (ref Rig8 g, ref Level l) => WideKernels.BrakeForced(ref g, in l));
        CheckHoles(count, i => i % 3 == 1, (r, i) => new Rig8(NextFloat(r, i), 1, 2, NextFloat(r, i + 1), 4, 5, 6, NextFloat(r, i + 2)), NextLevel,
            WideKernels.BrakeForced, (ref Rig8 g, ref Level l) => WideKernels.BrakeForced(ref g, in l));
        Check(count, (r, i) => new Rig16(NextFloat(r, i), 1, 2, 3, 4, NextFloat(r, i + 1), 6, 7, 8, 9, 10, 11, 12, 13, 14, NextFloat(r, i + 2)), NextLevel,
            WideKernels.Haul, (ref Rig16 g, ref Level l) => WideKernels.Haul(ref g, in l));
        CheckHoles(count, i => i % 5 == 3, (r, i) => new Rig16(NextFloat(r, i), 1, 2, 3, 4, NextFloat(r, i + 1), 6, 7, 8, 9, 10, 11, 12, 13, 14, NextFloat(r, i + 2)), NextLevel,
            WideKernels.Haul, (ref Rig16 g, ref Level l) => WideKernels.Haul(ref g, in l));
    }

    /// <summary>
    /// Wide components of 8-byte words (doubles and longs, a row of 64 bytes and one of 40 with an int and a short among
    /// its doubles): heavy math (square roots, divisions, NaN) and light (longs), gathered by AVX-512's 8-lane permutes,
    /// AVX2's and the 128-bit table lookups where each pays; results the method's bit for bit, holes included.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void WideComponentsOfEightByteWordsMatchTheMethodBitForBit(int count)
    {
        Probe8 Probe(Random r, int i) => new(NextDouble(r, i), r.NextInt64(), 2, NextDouble(r, i + 1), r.NextInt64(), 5, 6, NextDouble(r, i + 2));
        Depth NextDepth(Random r, int i) => new() { Value = i % 7 == 0 ? double.NaN : NextDouble(r, i + 3) };
        Check(count, Probe, NextDepth, WideKernels.Dive, (ref Probe8 p, ref Depth d) => WideKernels.Dive(ref p, in d));
        CheckHoles(count, i => i % 3 == 1, Probe, NextDepth, WideKernels.Dive, (ref Probe8 p, ref Depth d) => WideKernels.Dive(ref p, in d));
        Check(count, Probe, (r, i) => new Stamp { Value = r.NextInt64() }, WideKernels.Tally, (ref Probe8 p, ref Stamp s) => WideKernels.Tally(ref p, in s));
        Hull NextHull(Random r, int i) => new() { Speed = NextDouble(r, i), Crew = i, Deck = (short)-i, Mass = i % 5 == 0 ? 0 : NextDouble(r, i + 1), Drag = NextDouble(r, i + 2), Id = i };
        Check(count, NextHull, NextDepth, WideKernels.Sail, (ref Hull h, ref Depth d) => WideKernels.Sail(ref h, in d));
        CheckHoles(count, i => i % 4 == 2, NextHull, NextDepth, WideKernels.Sail, (ref Hull h, ref Depth d) => WideKernels.Sail(ref h, in d));
    }

    /// <summary>
    /// Every vector loop of this assembly's systems runs as one on this hardware, as the skill's table of ARM64 forms says
    /// (audit-vector-loops): each schedule line is printed (the ARM64 CI job shows them), and on ARM64 none says "rows on
    /// this hardware" except the gathers of other entities (no gather on ARM64 in .NET 10: D131) and a wide component
    /// whose table lookups do not pay (the gate left it "Vector512 only").
    /// </summary>
    [Test]
    public void EveryVectorFormRunsAsAVectorLoopOnARM64()
    {
        var lines = typeof(KernelTests).Assembly.GetTypes()
            .Where(t => !t.ContainsGenericParameters && t.GetInterfaces().Any(i => i.Name == nameof(ISystemCode)))
            .SelectMany(t => (string[])t.GetProperty(nameof(ISystemCode.Loops))!.GetValue(null)!)
            .Where(l => l.Contains(": vector", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        foreach (var line in lines) TestContext.Out.WriteLine(line);
        Assert.That(lines.Single(l => l.StartsWith("Glide:", StringComparison.Ordinal)), Does.Contain("wide component"));
        if (!System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported) Assert.Ignore("ARM64 only: the lines above are this hardware's");
        var rows = lines.Where(l => l.Contains("rows on this hardware", StringComparison.Ordinal)
            && !l.Contains("gathers", StringComparison.Ordinal) && !l.Contains("(Vector512 only)", StringComparison.Ordinal));
        Assert.That(rows, Is.Empty);
        Assert.That(lines.Single(l => l.StartsWith("Glide:", StringComparison.Ordinal)), Does.Not.Contain("rows on this hardware"));
    }

    /// <summary>
    /// A cold branch: the rows of a vector where some lane takes it run the method, so the results and the calls (their
    /// order included) are the method's, whether no row, a few or all take it.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void ColdBranchesRunTheirVectorsThroughTheMethod(int count)
    {
        foreach (int every in new[] { 0, 97, 7, 1 })
        {
            using var world = new World();
            var random = new Random(count + every);
            var healths = new Heat[count];
            var damages = new Level[count];
            var entities = new Entity[count];
            world.Reserve<Heat, Level>(count);
            for (int i = 0; i < count; i++)
            {
                healths[i] = new Heat { Value = every > 0 && i % every == 0 ? 1 : 100 + i };
                damages[i] = new Level { Value = i % 3 == 0 ? NextFloat(random, i) : 2 };
                entities[i] = world.CreateEntity(healths[i], damages[i]);
            }
            ColdKernels.Calls.Clear();
            ColdKernels.Hurt(world);
            var vector = ColdKernels.Calls.ToArray();
            ColdKernels.Calls.Clear();
            for (int i = 0; i < count; i++) ColdKernels.Hurt(ref healths[i], in damages[i]);
            Assert.That(vector, Is.EqualTo(ColdKernels.Calls.ToArray()), $"the calls, one row in {every}");
            for (int i = 0; i < count; i++)
                Assert.That(Same(entities[i].Read<Heat>(), healths[i]), $"Heat of row {i}: {entities[i].Read<Heat>()} instead of {healths[i]}");
        }

        using var census = new World();
        var r = new Random(count);
        var ranks = new Rank[count];
        census.Reserve<Rank>(count);
        for (int i = 0; i < count; i++)
        {
            ranks[i] = NextTally(r, i);
            census.CreateEntity(ranks[i]);
        }
        ColdKernels.Calls.Clear();
        ColdKernels.Census(census);
        var calls = ColdKernels.Calls.ToArray();
        ColdKernels.Calls.Clear();
        var headcount = new Reduce<Headcount> { Value = Headcount.Identity };
        for (int i = 0; i < count; i++) ColdKernels.Census(in ranks[i], ref headcount);
        Assert.That(census.GetSingleton<Headcount>(), Is.EqualTo(headcount.Value));
        Assert.That(calls, Is.EqualTo(ColdKernels.Calls.ToArray()));
    }

    /// <summary>DescribeSchedule names the loop each [ForEach] method got, and why a scalar one is scalar (EECS055's reason).</summary>
    [Test]
    public void TheScheduleNamesEachMethodsLoop()
    {
        using var world = new World();
        world.AddSystem<LoopsSystem>();
        var system = world.DescribeSchedule().Groups[0].Stages[0].Systems[0];
        Assert.That(system.Loops, Is.EqualTo(new[]
        {
            "Move: vector (Vector512 or Vector<float>), flat, masked words vector",
            "Hurt: vector (Vector512 or Vector<float>), masked words vector, cold branch (it calls Console.WriteLine, which is defined in another assembly)",
            "Mark: scalar (it uses the Entity in 'entity.Id.Index': a vector loop takes the entity's id in a record and its own Set, Remove, Disable, Enable or Destroy; read components as parameters (a tag as [With<T>] / [Without<T>], or a Maybe<T>))",
        }));
        Assert.That(world.DescribeSchedule().ToString(), Does.Contain("      loop Mark: scalar (it uses the Entity in 'entity.Id.Index'"));
    }

    /// <summary>
    /// ++ / -- as values: written in the lanes that evaluate them (under &amp;&amp;, ||, a branch of ?:, past an early
    /// return), the value before or after the write: every row ends as the method leaves it, edge values included.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void StepsAsValuesMatchTheMethod(int count)
    {
        static Bits NextBits(Random r, int i) => new(i % 5 == 0 ? new[] { int.MinValue, int.MaxValue, 0, 7, 51, -1 }[r.Next(6)] : r.Next(-200, 200));
        static Mask NextMask(Random r, int i) => new(i % 7 == 0 ? new[] { int.MinValue, int.MaxValue, 0, 3 }[r.Next(4)] : r.Next(-100, 100));
        Check(count, NextBits, NextMask, StepKernels.Tick, (ref Bits b, ref Mask m) => StepKernels.TickRow(ref b, in m));
    }

    /// <summary>
    /// A tag taken as a parameter is a filter (the rows with it, no column read); a Maybe of a tag is its presence: both
    /// end as their twins row by row leave the rows.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void TagsAsParametersMatchTheMethod(int count)
    {
        foreach (var (vector, scalar) in new (Action<World>, Action<World>)[] { (StepKernels.Chill, StepKernels.ChillScalar), (StepKernels.Thaw, StepKernels.ThawScalar) })
        {
            using var a = new World();
            using var b = new World();
            var random = new Random(count);
            var xs = new Entity[count];
            var ys = new Entity[count];
            for (int i = 0; i < count; i++)
            {
                var (heat, speed) = (new Heat { Value = NextFloat(random, i) }, new Speed { Value = NextFloat(random, i + 1) });
                xs[i] = a.CreateEntity(heat, speed);
                ys[i] = b.CreateEntity(heat, speed);
                if (i % 3 != 1)
                {
                    xs[i].Set(new Frozen());
                    ys[i].Set(new Frozen());
                }
            }
            for (int round = 0; round < 3; round++)
            {
                vector(a);
                scalar(b);
            }
            for (int i = 0; i < count; i++)
                Assert.That(Same(xs[i].Read<Heat>(), ys[i].Read<Heat>()), $"row {i} of {count}: {xs[i].Read<Heat>()} instead of {ys[i].Read<Heat>()}");
        }
    }

    /// <summary>
    /// Running totals of a singleton taken by ref (a masked count, a long sum of absolute values, a Min, a Max) from a
    /// total that is not the identity, rows at int.MaxValue among them: the singleton ends where the method leaves it.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void RunningTotalsOfASingletonMatchTheMethod(int count)
    {
        using var a = new World();
        using var b = new World();
        var random = new Random(count);
        for (int i = 0; i < count; i++)
        {
            var bits = new Bits(i % 97 == 5 ? int.MaxValue : random.Next(-1000, 1000));
            a.CreateEntity(bits, new Mask(i));
            b.CreateEntity(bits, new Mask(i));
            if (i % 3 == 0) // two tables
            {
                a.CreateEntity(bits);
                b.CreateEntity(bits);
            }
        }
        var start = new Census4 { Count = 5, Sum = -7, Low = 3, High = -3 };
        a.GetOrAddSingleton(start);
        b.GetOrAddSingleton(start);
        for (int round = 0; round < 2; round++)
        {
            StepKernels.Survey(a);
            StepKernels.SurveyScalar(b);
            Assert.That(a.GetSingleton<Census4>(), Is.EqualTo(b.GetSingleton<Census4>()), $"round {round}");
        }
    }

    /// <summary>
    /// A component declared <c>[StructLayout(LayoutKind.Sequential)]</c> with no Pack or Size lies as C# lays out any
    /// struct: its method gets the vector loop, bit for bit the method's.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void ASequentialLayoutKeepsTheVectorLoop(int count)
    {
        using (var world = new World())
        {
            world.AddSystem<LaidSystem>();
            Assert.That(world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops.Single(), Does.StartWith("Lay: vector"));
        }
        Check(count, (r, i) => new Laid2 { X = NextFloat(r, i), Y = NextFloat(r, i + 1) }, NextLevel, LaidKernels.Lay,
            (ref Laid2 l, ref Level s) => LaidKernels.Lay(ref l, in s));
    }

    private delegate void NarrowRow<TO, TA, TB>(ref TO o, in TA a, in TB b);

    /// <summary>
    /// A kernel of three components run over <paramref name="count"/> rows in two tables, then by the method row by row:
    /// every row ends bit for bit as the method leaves it. With <paramref name="holes"/>, every fifth row lacks
    /// <typeparamref name="TB"/> (masked words: their passing rows run the method).
    /// </summary>
    private static void CheckNarrow<TO, TA, TB>(int count, bool holes, Func<int, TO> o, Func<int, TA> a, Func<int, TB> b,
        Action<World> generated, NarrowRow<TO, TA, TB> scalar, string name)
        where TO : struct, IComponent where TA : struct, IComponent where TB : struct, IComponent
    {
        using var world = new World();
        world.Reserve<TO, TA, TB>(count);
        var os = new TO[count];
        var @as = new TA[count];
        var bs = new TB[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            (os[i], @as[i], bs[i]) = (o(i), a(i), b(i));
            entities[i] = world.CreateEntity(os[i], @as[i], bs[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
            if (holes && i % 5 == 0) entities[i].Remove<TB>();
        }
        for (int round = 0; round < 2; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++)
                if (!(holes && i % 5 == 0)) scalar(ref os[i], in @as[i], in bs[i]);
        }
        int wrong = 0, first = -1;
        for (int i = 0; i < count; i++)
            if (!Same(entities[i].Read<TO>(), os[i]) && wrong++ == 0) first = i;
        Assert.That(wrong, Is.Zero, () => $"{name}: {wrong} of {count} rows differ, first row {first}: {entities[first].Read<TO>()} instead of {os[first]} ({@as[first]}, {bs[first]})");
    }

    private static readonly int Pairs = 65536 + 13; // every pair of bytes, and a tail

    /// <summary>
    /// Byte kernels over every pair of operands (and holes for the masked words): wrap-around arithmetic, the saturating
    /// Min / Max / Clamp idioms, comparisons (a saturated sum compared with a value inside the range), shifts, bitwise
    /// operators, Min / Max of bytes, compound assignments and an int local: the method's result, lane by lane.
    /// </summary>
    /// <summary>
    /// Maybe&lt;T&gt; of byte and short lanes (presence bits as Lanes8 / Lanes16 masks), in tables whose rows also lack a
    /// queried component (masked words of narrow lanes): every row ends as the method leaves it, across the vector loop,
    /// its masked words and the rows after the last whole vector.
    /// </summary>
    [TestCase(70)]
    [TestCase(1000)]
    public void OptionalComponentsOfNarrowLanesAreLaneMasks(int count)
    {
        using var world = new World();
        var entities = new Entity[count];
        var ink = new byte[count];
        var shade = new byte[count];
        var tone = new byte?[count];
        var wave = new short[count];
        var gain = new short[count];
        var mix = new short?[count];
        world.Reserve<Ink, Shade, Tone, Wave16, Gain16, Mix16>(count);
        for (int i = 0; i < count; i++)
        {
            (ink[i], shade[i], tone[i]) = ((byte)(i * 37), (byte)(i * 11 + 3), (byte)(i * 5));
            (wave[i], gain[i], mix[i]) = ((short)(i * 977), (short)(i * 31 - 500), (short)(i * 7 - 300));
            var e = entities[i] = world.CreateEntity(new Ink { V = ink[i] }, new Shade { V = shade[i] }, new Tone { V = tone[i]!.Value },
                new Wave16 { V = wave[i] }, new Gain16 { V = gain[i] }, new Mix16 { V = mix[i]!.Value });
            if (i % 3 == 1) { e.Remove<Tone>(); tone[i] = null; }   // presence holes of the optional columns
            if (i % 4 == 2) { e.Remove<Mix16>(); mix[i] = null; }
        }
        var noShade = new bool[count];
        var noGain = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (i % 7 == 3) { entities[i].Remove<Shade>(); noShade[i] = true; } // masked words: rows the query leaves out
            if (i % 6 == 5) { entities[i].Remove<Gain16>(); noGain[i] = true; }
        }

        for (int round = 0; round < 2; round++)
        {
            NarrowKernels.Tint(world);
            NarrowKernels.Swell(world);
            for (int i = 0; i < count; i++)
            {
                if (!noShade[i]) ink[i] = tone[i] is { } t ? (byte)(shade[i] + t) : (byte)(ink[i] ^ shade[i]);
                if (!noGain[i] && mix[i] is { } m) wave[i] = (short)(wave[i] + gain[i] * m);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(entities[i].Read<Ink>().V, Is.EqualTo(ink[i]), $"Ink of row {i}");
            Assert.That(entities[i].Read<Wave16>().V, Is.EqualTo(wave[i]), $"Wave16 of row {i}");
        }
    }

    /// <summary>The narrow lane masks against the word's bits at every first row a vector of them starts at.</summary>
    [Test]
    public void NarrowLaneMasksAreTheWordsBits()
    {
        var random = new Random(9);
        for (int round = 0; round < 200; round++)
        {
            ulong rows = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            for (int first = 0; first + Vector<sbyte>.Count <= 64; first += Vector<sbyte>.Count)
            {
                var m = Interleaved.Lanes8(rows, first);
                for (int j = 0; j < Vector<sbyte>.Count; j++) Assert.That(m[j], Is.EqualTo(((rows >> (first + j)) & 1) != 0 ? -1 : 0), $"Lanes8 {first} {j}");
            }
            for (int first = 0; first + Vector<short>.Count <= 64; first += Vector<short>.Count)
            {
                var m = Interleaved.Lanes16(rows, first);
                for (int j = 0; j < Vector<short>.Count; j++) Assert.That(m[j], Is.EqualTo(((rows >> (first + j)) & 1) != 0 ? -1 : 0), $"Lanes16 {first} {j}");
            }
            var b = Interleaved.Lanes8In512(rows, 0);
            for (int j = 0; j < 64; j++) Assert.That(b[j], Is.EqualTo(((rows >> j) & 1) != 0 ? -1 : 0), $"Lanes8In512 {j}");
            for (int first = 0; first < 64; first += 32)
            {
                var h = Interleaved.Lanes16In512(rows, first);
                for (int j = 0; j < 32; j++) Assert.That(h[j], Is.EqualTo(((rows >> (first + j)) & 1) != 0 ? -1 : 0), $"Lanes16In512 {first} {j}");
            }
        }
    }

    [TestCase(false), TestCase(true)]
    public void ByteLanesMatchTheMethodForEveryPairOfOperands(bool holes)
    {
        static Ink O(int i) => new() { V = (byte)(i * 37) };
        static Shade A(int i) => new() { V = (byte)i };
        static Tone B(int i) => new() { V = (byte)(i >> 8) };
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Wrap, NarrowKernels.Wrap, "Wrap");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Times, NarrowKernels.Times, "Times");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.AddSat, NarrowKernels.AddSat, "AddSat");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.SubSat, NarrowKernels.SubSat, "SubSat");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Band, NarrowKernels.Band, "Band");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Pick, NarrowKernels.Pick, "Pick");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Bits, NarrowKernels.Bits, "Bits");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Least, NarrowKernels.Least, "Least");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Compound, NarrowKernels.Compound, "Compound");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.Distance, NarrowKernels.Distance, "Distance");
    }

    /// <summary>
    /// Enum lanes over every pair of byte operands: compared with constants, converted to and from their integer, set to a
    /// constant on some rows; an int enum against random ranks and levels with its early return.
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void EnumLanesMatchTheMethod(bool holes)
    {
        static Temper O(int i) => new() { V = (Mood)(i * 37 % 5 == 4 ? 200 : i * 37 % 5) };
        static Shade A(int i) => new() { V = (byte)i };
        static Tone B(int i) => new() { V = (byte)(i >> 8) };
        CheckNarrow(Pairs, holes, O, A, B, FlagKernels.Rage, FlagKernels.Rage, "Rage");
        CheckNarrow(Pairs, holes, O, A, B, FlagKernels.Cycle, FlagKernels.Cycle, "Cycle");
        var random = new Random(7);
        var ranks = new int[30000];
        var levels = new float[30000];
        for (int i = 0; i < ranks.Length; i++) (ranks[i], levels[i]) = (random.Next(-20, 140), random.NextSingle());
        Stance[] stances = [Stance.Idle, Stance.Walk, Stance.Run, Stance.Dead, (Stance)12345];
        CheckNarrow(30000, holes, i => new Posture { V = stances[i % stances.Length] }, i => new Rank { Value = ranks[i] }, i => new Level { Value = levels[i] },
            FlagKernels.Step, FlagKernels.Step, "Step");
    }

    /// <summary>
    /// Bool components over every pair of operands: a bool written from comparisons and itself, negated, set to a
    /// constant, and read as the condition of byte and enum writes. A bool is stored as exactly 0 or 1.
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void BoolComponentsMatchTheMethod(bool holes)
    {
        CheckNarrow(Pairs, holes, i => new Roused { V = i % 3 == 0 }, i => new Shade { V = (byte)i }, i => new Tone { V = (byte)(i >> 8) },
            FlagKernels.Wake, FlagKernels.Wake, "Wake");
        CheckNarrow(Pairs, holes, i => new Roused { V = i % 3 == 1 }, i => new Spotted { V = (i >> 8) % 2 == 0 }, i => new Shade { V = (byte)i },
            FlagKernels.Toggle, FlagKernels.Toggle, "Toggle");
        CheckNarrow(Pairs, holes, i => new Ink { V = (byte)(i * 37) }, i => new Roused { V = i % 7 < 3 }, i => new Shade { V = (byte)(i >> 8) },
            FlagKernels.Paint, FlagKernels.Paint, "Paint");
        CheckNarrow(Pairs, holes, i => new Temper { V = (Mood)(i % 4) }, i => new Roused { V = i % 2 == 0 }, i => new Spotted { V = i % 3 == 0 },
            FlagKernels.Calm, FlagKernels.Calm, "Calm");
        using var world = new World();
        var e = world.CreateEntity(new Roused(), new Shade { V = 9 }, new Tone { V = 2 });
        FlagKernels.Wake(world);
        Assert.That(MemoryMarshal.AsBytes(new ReadOnlySpan<Roused>(in e.Read<Roused>()))[0], Is.EqualTo(1), "a true bool is stored as 1");
    }

    /// <summary>Signed bytes over every pair: signed saturation, arithmetic shifts (counts past the lane), Abs of an int, compares.</summary>
    [TestCase(false), TestCase(true)]
    public void SignedByteLanesMatchTheMethodForEveryPairOfOperands(bool holes)
    {
        static Echo O(int i) => new() { V = (sbyte)(i * 37) };
        static Volume A(int i) => new() { V = (sbyte)i };
        static Pitch B(int i) => new() { V = (sbyte)(i >> 8) };
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.SignedSat, NarrowKernels.SignedSat, "SignedSat");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.SignedSubSat, NarrowKernels.SignedSubSat, "SignedSubSat");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.SignedShift, NarrowKernels.SignedShift, "SignedShift");
        CheckNarrow(Pairs, holes, O, A, B, NarrowKernels.SignedAbs, NarrowKernels.SignedAbs, "SignedAbs");
    }

    /// <summary>Lanes of 2 bytes over random operands and the edges of both ranges.</summary>
    [TestCase(false), TestCase(true)]
    public void ShortLanesMatchTheMethodOnRandomAndEdgeValues(bool holes)
    {
        int[] edges = [short.MinValue, short.MinValue + 1, -32767, -256, -129, -128, -1, 0, 1, 127, 128, 255, 256, 32766, short.MaxValue, 32768, 65534, 65535];
        var random = new Random(16);
        var values = new int[40000];
        for (int i = 0; i < values.Length; i++) values[i] = i % 4 == 0 ? edges[random.Next(edges.Length)] : random.Next(0, 65536);
        int V(int i) => values[i % values.Length];
        CheckNarrow(30000, holes, i => new Mix16 { V = (short)V(i * 3) }, i => new Wave16 { V = (short)V(i * 3 + 1) }, i => new Gain16 { V = (short)V(i * 3 + 2) },
            NarrowKernels.Mix, NarrowKernels.Mix, "Mix");
        CheckNarrow(30000, holes, i => new Mix16 { V = (short)V(i * 3) }, i => new Wave16 { V = (short)V(i * 3 + 1) }, i => new Gain16 { V = (short)V(i * 3 + 2) },
            NarrowKernels.Scale, NarrowKernels.Scale, "Scale");
        CheckNarrow(30000, holes, i => new Sum16 { V = (ushort)V(i * 3) }, i => new Word16 { V = (ushort)V(i * 3 + 1) }, i => new Bias16 { V = (ushort)V(i * 3 + 2) },
            NarrowKernels.Drain, NarrowKernels.Drain, "Drain");
        CheckNarrow(30000, holes, i => new Sum16 { V = (ushort)V(i * 3) }, i => new Word16 { V = (ushort)V(i * 3 + 1) }, i => new Bias16 { V = (ushort)V(i * 3 + 2) },
            NarrowKernels.Fill, NarrowKernels.Fill, "Fill");
    }

    /// <summary>A component of four byte fields treated alike: the flat loop, every field saturating as the method does.</summary>
    [TestCaseSource(nameof(Lengths))]
    public void ByteFieldsTreatedAlikeTakeTheFlatLoop(int count)
    {
        var random = new Random(count);
        Rgba Next() => new() { R = (byte)random.Next(256), G = (byte)random.Next(256), B = (byte)random.Next(256), A = (byte)random.Next(256) };
        Check(count, (_, _) => Next(), (_, _) => { var c = Next(); return new Tint4 { R = c.R, G = c.G, B = c.B, A = c.A }; },
            NarrowKernels.Blend, (ref Rgba c, ref Tint4 t) => NarrowKernels.Blend(ref c, in t));
    }

    [TestCaseSource(nameof(Lengths))]
    public void ComponentsOfSeveralFieldsMatchTheMethodBitForBit(int count)
    {
        Check(count, (r, i) => new Spot2 { X = NextFloat(r, i), Y = NextFloat(r, i + 1) }, (r, i) => new Drift2 { X = NextFloat(r, i + 2), Y = NextFloat(r, i + 3) },
            FieldKernels.Drift, (ref Spot2 p, ref Drift2 v) => FieldKernels.Drift(ref p, in v));
        Check(count, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Seek(w, 4f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Seek(ref p, ref v, 4f));
        Check(count, (r, i) => new Color4(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3) % 1),
            (r, i) => new Heat { Value = NextFloat(r, i) }, FieldKernels.FadeLanes, (ref Color4 c, ref Heat _) => FieldKernels.FadeLanes(ref c));
        Check(count, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) },
            (r, i) => new Pace { Value = NextDouble(r, i + 3) }, FieldKernels.Travel, (ref Far3 p, ref Pace v) => FieldKernels.Travel(ref p, in v));
        Check(count, (r, i) => new Cell2 { X = r.NextInt64(long.MinValue, long.MaxValue), Y = r.NextInt64() }, (r, i) => new Heat(),
            FieldKernels.Step, (ref Cell2 c, ref Heat _) => FieldKernels.Step(ref c));
        Check(count, (r, i) => new Hash4 { A = (uint)r.Next(), B = uint.MaxValue - (uint)i, C = (uint)r.Next(), D = (uint)i }, (r, i) => new Heat(),
            FieldKernels.Mix, (ref Hash4 h, ref Heat _) => FieldKernels.Mix(ref h));
        Check(count, (r, i) => new Spot2 { X = NextFloat(r, i), Y = NextFloat(r, i + 1) }, (r, i) => new Drift2 { X = NextFloat(r, i + 2), Y = NextFloat(r, i + 3) },
            FieldKernels.Glide, (ref Spot2 p, ref Drift2 v) => FieldKernels.Glide(ref p, in v));
        Check(count, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Bounce(w, 0.25f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Bounce(ref p, ref v, 0.25f));
        Check(count, (r, i) => new Color4(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3)),
            (r, i) => new Heat(), FieldKernels.Shade, (ref Color4 c, ref Heat _) => FieldKernels.Shade(ref c));
        Check(count, (r, i) => new Cell2 { X = r.NextInt64(long.MinValue, long.MaxValue), Y = r.NextInt64() }, (r, i) => new Heat(),
            FieldKernels.Scramble, (ref Cell2 c, ref Heat _) => FieldKernels.Scramble(ref c));
    }

    [TestCaseSource(nameof(Lengths))]
    public void ClampLerpMathConversionsAndMasksMatchTheMethodBitForBit(int count)
    {
        // bounds in order, bounds of NaN, bounds equal
        foreach (var bounds in new[] { new Bounds(-5, 5), new Bounds(float.NaN, 2), new Bounds(3, 3) })
            Check(count, NextLevel, NextTally, w => { w.SetBounds(bounds.Min, bounds.Max); MoreKernels.Clamp(w); },
                (ref Level l, ref Rank t) => MoreKernels.Clamp(ref l, in t, in bounds));
        Check(count, (r, i) => new Spot2 { X = NextFloat(r, i), Y = 100 + i }, (r, i) => new Level { Value = i % 9 == 0 ? float.NaN : -100 - i },
            MoreKernels.ClampLanes, (ref Spot2 s, ref Level l) => MoreKernels.ClampLanes(ref s, in l));
        Check(count, NextTally, NextCode, MoreKernels.ClampInts, (ref Rank t, ref Code32 c) => MoreKernels.ClampInts(ref t, in c));
        Check(count, NextLevel, NextTally, MoreKernels.Blend, (ref Level l, ref Rank t) => MoreKernels.Blend(ref l, in t));
        Check(count, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) }, NextLevel,
            MoreKernels.Rounding, (ref Spot3 s, ref Level _) => MoreKernels.Rounding(ref s));
        Check3(count, NextLevel, NextTally, NextCode, MoreKernels.Convert, MoreKernels.Convert);
        Check3(count, (r, i) => new Depth { Value = NextDouble(r, i) }, (r, i) => new Stamp { Value = i % 3 == 0 ? long.MinValue + i : r.NextInt64() },
            (r, i) => new Serial { Value = i % 5 == 0 ? ulong.MaxValue : (ulong)r.NextInt64() }, MoreKernels.ConvertWide, MoreKernels.ConvertWide);
        Check(count, NextLevel, NextTally, MoreKernels.Masks, (ref Level l, ref Rank t) => MoreKernels.Masks(ref l, ref t));
    }

    /// <summary>Math.Clamp with a min above its max throws for the row that has it, after the rows before it, as written.</summary>
    [Test]
    public void AClampThatThrowsLeavesTheRowsTheMethodLeaves()
    {
        const int count = 100, bad = 37;
        using var vector = new World();
        var spots = new Spot2[count];
        var levels = new Level[count];
        for (int i = 0; i < count; i++)
        {
            spots[i] = new Spot2 { X = i, Y = i == bad ? -1000 : 1000 };
            levels[i] = new Level { Value = 10 };
            vector.CreateEntity(spots[i], levels[i]);
        }

        Assert.Throws<ArgumentException>(() => MoreKernels.ClampLanes(vector));
        Assert.Throws<ArgumentException>(() => { for (int i = 0; i < count; i++) MoreKernels.ClampLanes(ref spots[i], in levels[i]); });
        int row = 0;
        foreach (var item in vector.Query<Spot2>()) Assert.That(Same(item.Spot2, spots[row++]), $"row {row - 1}");
    }

    /// <summary>Which rows lose a component (removed in place: the row keeps its place, its presence bit clears).</summary>
    public static readonly (string Name, Func<int, bool> Hole)[] Holes =
    [
        ("every third", i => i % 3 == 0),
        ("random half", i => (uint)(i * 2654435761u) >> 31 == 1),
        ("every other word", i => (i >> 6) % 2 == 1),
        ("one per word", i => (i & 63) == 5),
        ("all but one per word", i => (i & 63) != 40),
        ("after row 70", i => i >= 70),
        ("one per 300 rows", i => i % 300 != 7), // words without a passing row between them: skipped
    ];

    internal static readonly int[] HoleCounts = [1, 8, 17, 64, 65, 130, 1000];

    private static System.Collections.Generic.IEnumerable<TestCaseData> HoleCases() =>
        from hole in Holes from count in HoleCounts select new TestCaseData(hole.Name, count);

    /// <summary>
    /// Rows lacking a component (removed in place) are lanes the vector loop leaves out: a word of 64 rows with some
    /// passing runs as a masked vector loop, and every row ends as the method leaves it (the rows it does not visit as
    /// they were).
    /// </summary>
    [TestCaseSource(nameof(HoleCases))]
    public void RowsWithoutAComponentAreLanesTheLoopLeavesOut(string pattern, int count)
    {
        var hole = Holes.First(h => h.Name == pattern).Hole;
        CheckHoles(count, hole, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Seek(w, 4f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Seek(ref p, ref v, 4f));
        CheckHoles(count, hole, NextLevel, NextTally, MoreKernels.Masks, (ref Level l, ref Rank t) => MoreKernels.Masks(ref l, ref t));
        var bounds = new Bounds(-5, 5);
        CheckHoles(count, hole, NextLevel, NextTally, w => { w.SetBounds(bounds.Min, bounds.Max); MoreKernels.Clamp(w); },
            (ref Level l, ref Rank t) => MoreKernels.Clamp(ref l, in t, in bounds));
        CheckHoles(count, hole, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) },
            (r, i) => new Pace { Value = NextDouble(r, i + 3) }, FieldKernels.Travel, (ref Far3 p, ref Pace v) => FieldKernels.Travel(ref p, in v));
        // A flat loop's job: its masked words take the deinterleaving loop.
        CheckHoles(count, hole, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Bounce(w, 0.25f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Bounce(ref p, ref v, 0.25f));
    }

    /// <summary>
    /// Methods with a flat loop (2 and 4 fields of 4 bytes, 2 of 8, written whole or field by field) in masked words,
    /// which split their fields: the passing rows as the method computes them, the others as they were.
    /// </summary>
    [TestCaseSource(nameof(HoleCases))]
    public void FlatMethodsKeepTheRowsAMaskedWordLeaves(string pattern, int count)
    {
        var hole = Holes.First(h => h.Name == pattern).Hole;
        CheckHoles(count, hole, (r, i) => new Locus2(NextFloat(r, i), NextFloat(r, i + 1)), (r, i) => new Shift2(NextFloat(r, i + 2), NextFloat(r, i + 3)),
            FieldKernels.Push, (ref Locus2 p, ref Shift2 v) => FieldKernels.Push(ref p, in v));
        CheckHoles(count, hole, (r, i) => new Color4(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3)),
            (r, i) => new Hue4(NextFloat(r, i + 4), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3)),
            FieldKernels.Tint, (ref Color4 c, ref Hue4 h) => FieldKernels.Tint(ref c, in h));
        CheckHoles(count, hole, (r, i) => new Cell2 { X = r.NextInt64(), Y = r.NextInt64() }, (r, i) => new Step2 { X = r.NextInt64() - long.MaxValue / 2, Y = r.NextInt64() - long.MaxValue / 2 },
            FieldKernels.Walk, (ref Cell2 c, ref Step2 s) => FieldKernels.Walk(ref c, in s));
    }

    private static System.Collections.Generic.IEnumerable<TestCaseData> TailCases() =>
        from count in Enumerable.Range(1, 40) from hole in new[] { "none", "every third" } select new TestCaseData(count, hole);

    /// <summary>
    /// The last rows of a run, every length from 1 to 40 (each count of rows after the last whole vector, at both
    /// widths, run by the method after the vector loops): interleaved fields of 4 and 8 bytes, flat ones, pairs of 8-byte
    /// lanes, masks and clamps, reductions, in tables whole and with rows that lack a component; every row as the method
    /// leaves it.
    /// </summary>
    [TestCaseSource(nameof(TailCases))]
    public void TheLastRowsOfEveryLengthAreTheMethodsRows(int count, string pattern)
    {
        Func<int, bool> hole = pattern == "none" ? _ => false : i => i % 3 == 0;
        CheckHoles(count, hole, (r, i) => new Spot3 { X = NextFloat(r, i), Y = NextFloat(r, i + 1), Z = NextFloat(r, i + 2) },
            (r, i) => new Drift3 { X = NextFloat(r, i + 3), Y = NextFloat(r, i + 4), Z = NextFloat(r, i + 5) },
            w => FieldKernels.Seek(w, 4f), (ref Spot3 p, ref Drift3 v) => FieldKernels.Seek(ref p, ref v, 4f));
        CheckHoles(count, hole, NextLevel, NextTally, MoreKernels.Masks, (ref Level l, ref Rank t) => MoreKernels.Masks(ref l, ref t));
        var bounds = new Bounds(-5, 5);
        CheckHoles(count, hole, NextLevel, NextTally, w => { w.SetBounds(bounds.Min, bounds.Max); MoreKernels.Clamp(w); },
            (ref Level l, ref Rank t) => MoreKernels.Clamp(ref l, in t, in bounds));
        CheckHoles(count, hole, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) },
            (r, i) => new Pace { Value = NextDouble(r, i + 3) }, FieldKernels.Travel, (ref Far3 p, ref Pace v) => FieldKernels.Travel(ref p, in v));
        CheckHoles(count, hole, (r, i) => new Locus2(NextFloat(r, i), NextFloat(r, i + 1)), (r, i) => new Shift2(NextFloat(r, i + 2), NextFloat(r, i + 3)),
            FieldKernels.Push, (ref Locus2 p, ref Shift2 v) => FieldKernels.Push(ref p, in v));
        CheckHoles(count, hole, (r, i) => new Color4(NextFloat(r, i), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3)),
            (r, i) => new Hue4(NextFloat(r, i + 4), NextFloat(r, i + 1), NextFloat(r, i + 2), NextFloat(r, i + 3)),
            FieldKernels.Tint, (ref Color4 c, ref Hue4 h) => FieldKernels.Tint(ref c, in h));
        CheckHoles(count, hole, (r, i) => new Cell2 { X = r.NextInt64(), Y = r.NextInt64() }, (r, i) => new Step2 { X = r.NextInt64() - long.MaxValue / 2, Y = r.NextInt64() - long.MaxValue / 2 },
            FieldKernels.Walk, (ref Cell2 c, ref Step2 s) => FieldKernels.Walk(ref c, in s));
        if (pattern == "none") ReductionsMatchTheMethodBitForBit(count);
    }

    /// <summary>A Math.Clamp that throws in a masked word throws for the row that has it, after the passing rows before it.</summary>
    [Test]
    public void AClampThatThrowsInAMaskedWordLeavesTheRowsTheMethodLeaves()
    {
        const int count = 100;
        using var vector = new World();
        var spots = new Spot2[count];
        var levels = new Level[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            // Rows 36 and 38 lack Level but hold bounds that would throw: only row 37 throws.
            spots[i] = new Spot2 { X = i, Y = i is >= 36 and <= 38 ? -1000 : 1000 };
            levels[i] = new Level { Value = 10 };
            entities[i] = vector.CreateEntity(spots[i], levels[i]);
        }

        bool Hole(int i) => i % 2 == 0;
        for (int i = 0; i < count; i++) if (Hole(i)) entities[i].Remove<Level>();
        Assert.Throws<ArgumentException>(() => MoreKernels.ClampLanes(vector));
        Assert.Throws<ArgumentException>(() => { for (int i = 0; i < count; i++) if (!Hole(i)) MoreKernels.ClampLanes(ref spots[i], in levels[i]); });
        for (int i = 0; i < count; i++) Assert.That(Same(entities[i].Get<Spot2>(), spots[i]), $"row {i}");
    }

    /// <summary>Rows destroyed during an outer pass (tombstones until it ends) are lanes the vector loop leaves out.</summary>
    [Test]
    public void RowsDestroyedDuringAPassAreLanesTheLoopLeavesOut()
    {
        const int count = 200;
        using var world = new World();
        var random = new Random(3);
        var levels = new Level[count];
        var ranks = new Rank[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            (levels[i], ranks[i]) = (NextLevel(random, i), NextTally(random, i));
            entities[i] = world.CreateEntity(levels[i], ranks[i]);
            if (i % 5 == 0) entities[i].Remove<Rank>(); // a test of presence bits: the masked path
        }

        world.CreateEntity(new Heat());
        foreach (var _ in world.Query<Heat>())
        {
            for (int i = 0; i < count; i += 7) entities[i].Destroy();
            MoreKernels.Masks(world);
        }

        for (int i = 0; i < count; i++)
        {
            if (i % 7 == 0) continue;
            if (i % 5 != 0) MoreKernels.Masks(ref levels[i], ref ranks[i]);
            Assert.That(Same(entities[i].Get<Level>(), levels[i]), $"Level of row {i}: {Bits(entities[i].Get<Level>())} instead of {Bits(levels[i])}");
        }
    }

    /// <summary>
    /// <see cref="Check{T1, T2}"/> over one table whose rows in <paramref name="hole"/> lack one of the two components
    /// (the first for odd rows, the second for even ones): the method runs on the rows that have both.
    /// </summary>
    internal static void CheckHoles<T1, T2>(int count, Func<int, bool> hole, Func<Random, int, T1> make1, Func<Random, int, T2> make2,
        Action<World> generated, ScalarRow<T1, T2> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent
    {
        using var world = new World();
        world.Reserve<T1, T2>(count);
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = make1(random, i);
            b[i] = make2(random, i);
            entities[i] = world.CreateEntity(a[i], b[i]);
        }

        for (int i = 0; i < count; i++)
            if (hole(i))
            {
                if (i % 2 == 1) entities[i].Remove<T1>();
                else entities[i].Remove<T2>();
            }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) if (!hole(i)) scalar(ref a[i], ref b[i]);
        }

        for (int i = 0; i < count; i++)
        {
            if (entities[i].Has<T1>()) Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {Bits(entities[i].Get<T1>())} instead of {Bits(a[i])}");
            if (entities[i].Has<T2>()) Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {Bits(entities[i].Get<T2>())} instead of {Bits(b[i])}");
        }
    }

    /// <summary>
    /// A Reduce&lt;T&gt; carried in lanes (one accumulator per lane, from T.Identity) and folded with T.Combine in lane
    /// order: integer sums, counts, Min and Max match the method row by row bit for bit, so do float Min and Max, in
    /// tables with and without rows to test.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void ReductionsMatchTheMethodBitForBit(int count)
    {
        foreach (var hole in new Func<int, bool>[] { _ => false, i => i % 3 == 0, i => (uint)(i * 2654435761u) >> 31 == 1 })
        {
            using var world = new World();
            var random = new Random(count);
            var ranks = new Rank[count];
            var levels = new Level[count];
            var stamps = new Stamp[count];
            var spots = new Spot2[count];
            var entities = new Entity[count];
            world.Reserve<Rank, Level, Stamp, Spot2>(count);
            for (int i = 0; i < count; i++)
            {
                (ranks[i], levels[i]) = (NextTally(random, i), NextLevel(random, i));
                stamps[i] = new Stamp { Value = i % 3 == 0 ? long.MaxValue - i : random.NextInt64(long.MinValue, long.MaxValue) };
                spots[i] = new Spot2 { X = NextFloat(random, i), Y = 1000 };
                entities[i] = world.CreateEntity(ranks[i], levels[i], stamps[i], spots[i]);
            }
            for (int i = 0; i < count; i++)
            {
                if (i % 7 == 0) entities[i].Set(new Frozen()); // a second table: a second run
                if (hole(i)) entities[i].Remove<Rank>();
            }

            // The method, row by row in the order the pass visits them (table by table).
            var order = Enumerable.Range(0, count).OrderBy(i => i % 7 == 0 ? 1 : 0).ToArray();
            var headcount = new Reduce<Headcount> { Value = Headcount.Identity };
            foreach (int i in order) if (!hole(i)) ReduceKernels.Count(in ranks[i], ref headcount);
            ReduceKernels.Count(world);
            Assert.That(world.GetSingleton<Headcount>(), Is.EqualTo(headcount.Value), "Count");

            var tallies = new Reduce<Tallies> { Value = Tallies.Identity };
            foreach (int i in order) if (!hole(i)) ReduceKernels.Tallies(in ranks[i], ref tallies);
            ReduceKernels.Tallies(world);
            Assert.That(world.GetSingleton<Tallies>(), Is.EqualTo(tallies.Value), "Tallies");
            Assert.That((tallies.Value.Count, tallies.Value.Sum, tallies.Value.Low, tallies.Value.High), Is.EqualTo((headcount.Value.Count, headcount.Value.Sum, headcount.Value.Low, headcount.Value.High)), "Tallies is Count");

            var counts = new Reduce<Counts> { Value = Counts.Identity };
            foreach (int i in order) if (!hole(i)) ReduceKernels.Counted(in ranks[i], ref counts);
            ReduceKernels.Counted(world);
            Assert.That(world.GetSingleton<Counts>(), Is.EqualTo(counts.Value), "Counted");

            headcount = new Reduce<Headcount> { Value = Headcount.Identity };
            foreach (int i in order) if (!hole(i)) ReduceKernels.Tally(ref ranks[i], ref headcount);
            ReduceKernels.Tally(world);
            Assert.That(world.GetSingleton<Headcount>(), Is.EqualTo(headcount.Value), "Tally");
            for (int i = 0; i < count; i++) if (!hole(i)) Assert.That(entities[i].Get<Rank>(), Is.EqualTo(ranks[i]), $"Rank of row {i}");

            var extent = new Reduce<Extent> { Value = Extent.Identity };
            foreach (int i in order) ReduceKernels.Span(in levels[i], ref extent);
            ReduceKernels.Span(world);
            Assert.That(Same(world.GetSingleton<Extent>(), extent.Value), $"Span: {Bits(world.GetSingleton<Extent>())} instead of {Bits(extent.Value)}");

            var reach = new Reduce<Reach> { Value = Reach.Identity };
            foreach (int i in order) ReduceKernels.Reach(in levels[i], ref reach);
            ReduceKernels.Reach(world);
            var vector = world.GetSingleton<Reach>();
            Assert.That(Same(new Extent { Low = vector.Low, High = vector.High }, new Extent { Low = reach.Value.Low, High = reach.Value.High }), "Reach");

            var ledger = new Reduce<Ledger> { Value = Ledger.Identity };
            foreach (int i in order) ReduceKernels.Book(in stamps[i], ref ledger);
            ReduceKernels.Book(world);
            Assert.That(world.GetSingleton<Ledger>(), Is.EqualTo(ledger.Value), "Book");

            var books = new Reduce<Books> { Value = Books.Identity };
            foreach (int i in order) ReduceKernels.Books(in stamps[i], ref books);
            ReduceKernels.Books(world);
            Assert.That(world.GetSingleton<Books>(), Is.EqualTo(books.Value), "Books");

            headcount = new Reduce<Headcount> { Value = Headcount.Identity };
            foreach (int i in order) ReduceKernels.Bounded(ref spots[i], ref headcount);
            ReduceKernels.Bounded(world);
            Assert.That(world.GetSingleton<Headcount>(), Is.EqualTo(headcount.Value), "Bounded");
        }
    }

    /// <summary>
    /// A float sum under FloatMode.Fast: the same rows, one sum per lane, so it rounds differently from the method's
    /// row by row sum, within the error bound of a sum of that many terms (n * u * the sum of the magnitudes).
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void AFastFloatSumIsTheSumOfTheRows(int count)
    {
        using var world = new World();
        world.Reserve<Level>(count);
        var random = new Random(count);
        double exact = 0, magnitude = 0;
        float scalar = 0;
        for (int i = 0; i < count; i++)
        {
            float value = (float)(random.NextDouble() * 200 - 100);
            world.CreateEntity(new Level { Value = value });
            exact += value * 0.5f;
            magnitude += Math.Abs(value * 0.5f);
            scalar += value * 0.5f;
        }

        ReduceKernels.Sum(world);
        float total = world.GetSingleton<Energy>().Total;
        double bound = count * 6e-8 * magnitude;
        Assert.That(Math.Abs(total - exact), Is.LessThanOrEqualTo(bound), $"{total} against {exact} (row by row {scalar})");
    }

    /// <summary>A Clamp that throws inside a reducing vector loop throws for its row; the pass replaces no result.</summary>
    [Test]
    public void AReductionThatThrowsReplacesNoResult()
    {
        using var world = new World();
        world.Reserve<Spot2>(100);
        for (int i = 0; i < 100; i++) world.CreateEntity(new Spot2 { X = i, Y = i == 37 ? -1 : 1000 });
        Assert.Throws<ArgumentException>(() => ReduceKernels.Bounded(world));
        Assert.That(world.HasSingleton<Headcount>(), Is.False);
    }

    private delegate void ScalarRow3<T1, T2, T3>(ref T1 a, ref T2 b, ref T3 c);

    private static void Check3<T1, T2, T3>(int count, Func<Random, int, T1> make1, Func<Random, int, T2> make2, Func<Random, int, T3> make3,
        Action<World> generated, ScalarRow3<T1, T2, T3> scalar)
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
    {
        using var world = new World();
        var random = new Random(count);
        var a = new T1[count];
        var b = new T2[count];
        var c = new T3[count];
        var entities = new Entity[count];
        world.Reserve<T1, T2, T3>(count);
        for (int i = 0; i < count; i++)
        {
            (a[i], b[i], c[i]) = (make1(random, i), make2(random, i), make3(random, i));
            entities[i] = world.CreateEntity(a[i], b[i], c[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        for (int round = 0; round < 3; round++)
        {
            generated(world);
            for (int i = 0; i < count; i++) scalar(ref a[i], ref b[i], ref c[i]);
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(Same(entities[i].Get<T1>(), a[i]), $"{typeof(T1).Name} of row {i} of {count}: {entities[i].Get<T1>()} instead of {a[i]} ({b[i]})");
            Assert.That(Same(entities[i].Get<T2>(), b[i]), $"{typeof(T2).Name} of row {i} of {count}: {entities[i].Get<T2>()} instead of {b[i]}");
            Assert.That(Same(entities[i].Get<T3>(), c[i]), $"{typeof(T3).Name} of row {i} of {count}: {entities[i].Get<T3>()} instead of {c[i]}");
        }
    }

    /// <summary>Interleaved.LoadN splits exactly the rows of one vector into fields; StoreN writes exactly those rows back.</summary>
    [Test]
    public void InterleavedLoadsAndStoresMoveExactlyTheRowsOfOneVector()
    {
        Assume.That(Interleaved.IsAccelerated, "no NEON, AVX2 or AVX-512");
        RoundTrip<float>(); RoundTrip<int>(); RoundTrip<uint>();
        RoundTrip<double>(); RoundTrip<long>(); RoundTrip<ulong>();
    }

    private static void RoundTrip<T>() where T : unmanaged, INumber<T>
    {
        foreach (int fields in new[] { 2, 3, 4 })
        foreach (bool wide in new[] { false, true })
        {
            if (wide && !Interleaved.IsAccelerated512) continue;
            int lanes = wide ? Vector512<T>.Count : Vector<T>.Count, rows = lanes * 3, at = lanes + 1; // an unaligned row
            var source = new T[rows * fields];
            for (int i = 0; i < source.Length; i++) source[i] = T.CreateTruncating(i + 1);
            var target = new T[source.Length]; // zeros: rows outside the vector must stay 0
            var split = new T[fields][];
            ref T column = ref source[0], into = ref target[0];
            if (wide) Wide(fields, ref column, ref into, (nuint)at, split);
            else Narrow(fields, ref column, ref into, (nuint)at, split);
            for (int f = 0; f < fields; f++)
            for (int l = 0; l < lanes; l++)
                Assert.That(split[f][l], Is.EqualTo(source[(at + l) * fields + f]), $"{typeof(T).Name} {fields} fields {(wide ? 512 : Vector<byte>.Count * 8)}: field {f} lane {l}");
            for (int i = 0; i < target.Length; i++)
                Assert.That(target[i], Is.EqualTo(i >= at * fields && i < (at + lanes) * fields ? source[i] : T.Zero), $"{typeof(T).Name} {fields} fields: element {i}");
        }
    }

    private static void Narrow<T>(int fields, ref T column, ref T into, nuint row, T[][] split) where T : unmanaged
    {
        switch (fields)
        {
            case 2:
                Interleaved.Load2(ref column, row, out Vector<T> a, out Vector<T> b);
                Interleaved.Store2(a, b, ref into, row);
                (split[0], split[1]) = (Lanes(a), Lanes(b));
                break;
            case 3:
                Interleaved.Load3(ref column, row, out Vector<T> c, out Vector<T> d, out Vector<T> e);
                Interleaved.Store3(c, d, e, ref into, row);
                (split[0], split[1], split[2]) = (Lanes(c), Lanes(d), Lanes(e));
                break;
            default:
                Interleaved.Load4(ref column, row, out Vector<T> f, out Vector<T> g, out Vector<T> h, out Vector<T> k);
                Interleaved.Store4(f, g, h, k, ref into, row);
                (split[0], split[1], split[2], split[3]) = (Lanes(f), Lanes(g), Lanes(h), Lanes(k));
                break;
        }
    }

    private static void Wide<T>(int fields, ref T column, ref T into, nuint row, T[][] split) where T : unmanaged
    {
        switch (fields)
        {
            case 2:
                Interleaved.Load2(ref column, row, out Vector512<T> a, out Vector512<T> b);
                Interleaved.Store2(a, b, ref into, row);
                (split[0], split[1]) = (Lanes(a), Lanes(b));
                break;
            case 3:
                Interleaved.Load3(ref column, row, out Vector512<T> c, out Vector512<T> d, out Vector512<T> e);
                Interleaved.Store3(c, d, e, ref into, row);
                (split[0], split[1], split[2]) = (Lanes(c), Lanes(d), Lanes(e));
                break;
            default:
                Interleaved.Load4(ref column, row, out Vector512<T> f, out Vector512<T> g, out Vector512<T> h, out Vector512<T> k);
                Interleaved.Store4(f, g, h, k, ref into, row);
                (split[0], split[1], split[2], split[3]) = (Lanes(f), Lanes(g), Lanes(h), Lanes(k));
                break;
        }
    }

    private static T[] Lanes<T>(Vector<T> v) where T : unmanaged
    {
        var lanes = new T[Vector<T>.Count];
        v.CopyTo(lanes);
        return lanes;
    }

    private static T[] Lanes<T>(Vector512<T> v) where T : unmanaged
    {
        var lanes = new T[Vector512<T>.Count];
        v.CopyTo(lanes);
        return lanes;
    }

    /// <summary>
    /// A parallel vector loop at 1 and 4 threads: the same bits as the method row by row (each row's lanes run the
    /// method's operations, wherever its chunk begins), in tables with rows to test (presence holes) and several runs.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public void ParallelVectorLoopsMatchTheMethodBitForBit(int threads)
    {
        const int count = 20_011;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.SetGravity(2f);
        var random = new Random(7);
        var spots = new Spot3[count];
        var drifts = new Drift3[count];
        var flats = new Spot2[count];
        var flows = new Drift2[count];
        var entities = new Entity[count];
        world.Reserve<Spot3, Drift3, Spot2, Drift2>(count);
        for (int i = 0; i < count; i++)
        {
            spots[i] = new Spot3 { X = NextFloat(random, i), Y = NextFloat(random, i + 1), Z = NextFloat(random, i + 2) };
            drifts[i] = new Drift3 { X = NextFloat(random, i + 3) };
            flats[i] = new Spot2 { X = NextFloat(random, i), Y = NextFloat(random, i + 4) };
            flows[i] = new Drift2 { X = NextFloat(random, i + 1), Y = NextFloat(random, i + 2) };
            entities[i] = world.CreateEntity(spots[i], drifts[i], flats[i], flows[i]);
            if (i % 11 == 0) entities[i].Set(new Frozen()); // a second table
        }
        for (int i = 0; i < count; i += 5) entities[i].Remove<Drift2>(); // rows the flat loop's pass tests

        var gravity = world.Gravity;
        for (int round = 0; round < 3; round++)
        {
            ParallelKernels.Seek(world, 3f);
            ParallelKernels.Glide(world);
            for (int i = 0; i < count; i++)
            {
                ParallelKernels.Seek(ref spots[i], ref drifts[i], in gravity, 3f);
                if (i % 5 != 0) ParallelKernels.Glide(ref flats[i], in flows[i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(Same(entities[i].Get<Spot3>(), spots[i]), $"Spot3 of row {i}: {Bits(entities[i].Get<Spot3>())} instead of {Bits(spots[i])}");
            Assert.That(Same(entities[i].Get<Drift3>(), drifts[i]), $"Drift3 of row {i}");
            Assert.That(Same(entities[i].Get<Spot2>(), flats[i]), $"Spot2 of row {i}: {Bits(entities[i].Get<Spot2>())} instead of {Bits(flats[i])}");
        }
    }

    /// <summary>Parallel loops over lanes of two sizes: a long column beside an int one and a long sum of ints, at any thread count.</summary>
    [TestCase(1)]
    [TestCase(4)]
    public void ParallelPairsMatchTheMethodBitForBit(int threads)
    {
        const int count = 30_017;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(9);
        var stamps = new Stamp[count];
        var ranks = new Rank[count];
        var entities = new Entity[count];
        world.Reserve<Stamp, Rank>(count);
        for (int i = 0; i < count; i++)
        {
            (stamps[i], ranks[i]) = (NextStamp(random, i), NextTally(random, i));
            entities[i] = world.CreateEntity(stamps[i], ranks[i]);
            if (i % 11 == 0) entities[i].Set(new Frozen());
        }

        var wallet = new Reduce<Wallet> { Value = Wallet.Identity };
        for (int i = 0; i < count; i++) ParallelKernels.Spend(in ranks[i], ref wallet);
        ParallelKernels.Spend(world);
        Assert.That(world.GetSingleton<Wallet>(), Is.EqualTo(wallet.Value), $"Spend at {threads} threads");
        for (int round = 0; round < 3; round++)
        {
            ParallelKernels.Earn(world);
            for (int i = 0; i < count; i++) ParallelKernels.Earn(ref stamps[i], in ranks[i]);
        }
        for (int i = 0; i < count; i++) Assert.That(entities[i].Get<Stamp>(), Is.EqualTo(stamps[i]), $"Stamp of row {i}");
    }

    /// <summary>
    /// The rows a parallel vector loop writes are stamped, those it does not visit are not, so a [Changed&lt;T&gt;]
    /// filter (which skips words and blocks by their summaries) finds exactly them: chunks of whole blocks and chunks
    /// that split blocks (opened before the pass, raised atomically).
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public void ParallelVectorLoopsStampTheRowsTheyWrite(int threads)
    {
        const int count = 30_000;
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var entities = new Entity[count];
        world.Reserve<Warmth, Mass>(count);
        for (int i = 0; i < count; i++) entities[i] = world.CreateEntity(new Warmth { Value = i % 50 }, new Mass { Value = i % 7 });
        for (int i = 0; i < count; i += 3) entities[i].Remove<Mass>(); // not visited: not stamped
        for (int i = 1; i < count; i += 1000) entities[i].Set(new Frozen());

        foreach (Action<World> pass in new Action<World>[] { ParallelKernels.Warm, ParallelKernels.WarmFine })
        {
            world.Update(); // a new tick: the rows created before it are older
            ulong mark = world.ChangeTick;
            pass(world);
            int visited = 0;
            for (int i = 0; i < count; i++) if (entities[i].Has<Mass>()) visited++;
            Assert.That(world.Count<Warmth>(QueryFilter.Create().Changed<Warmth>(mark)), Is.EqualTo(visited));
            foreach (var item in world.Read<Warmth>(QueryFilter.Create().Changed<Warmth>(mark)))
                Assert.That(item.Entity.Has<Mass>(), $"{item.Entity} has no Mass but was stamped");
        }
    }

    /// <summary>
    /// A parallel vector reduction: the chunks' lanes fold into each chunk's partial result, the chunks combine in chunk
    /// order. Integer sums, counts, Min and Max are exact at any thread count; a Fast float sum with fixed chunks is the
    /// same at 1 and 4 threads.
    /// </summary>
    private static Stamp NextStamp(Random r, int i) => new()
    {
        Value = i % 6 == 0 ? new[] { long.MinValue, long.MaxValue, 0, -1, 1001, 999 }[r.Next(6)] : r.Next(3) == 0 ? r.NextInt64(long.MinValue, long.MaxValue) : r.Next(-5000, 5000),
    };

    /// <summary>
    /// Lanes of two sizes: a column of long or double lanes beside int or float ones is a pair of vectors per step,
    /// widened, compared, narrowed back and stored as the method does, bit for bit, at every length and in two tables.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void LanesOfTwiceTheWidthMatchTheMethodBitForBit(int count)
    {
        Check3(count, NextStamp, NextTally, NextCode, PairKernels.Accrue, (ref Stamp s, ref Rank r, ref Code32 c) => PairKernels.Accrue(ref s, ref r, in c));
        Check3(count, (r, i) => new Depth { Value = NextDouble(r, i) }, NextLevel, NextTally, PairKernels.Refine,
            (ref Depth d, ref Level l, ref Rank r) => PairKernels.Refine(ref d, ref l, in r));
        Check(count, NextStamp, NextTally, PairKernels.Earn, (ref Stamp s, ref Rank r) => PairKernels.Earn(ref s, in r));
        Check(count, NextStamp, NextTally, PairKernels.Settle, (ref Stamp s, ref Rank r) => PairKernels.Settle(ref s, in r));
        Check(count, (r, i) => new Depth { Value = NextDouble(r, i) }, NextLevel, PairKernels.Soak, (ref Depth d, ref Level l) => PairKernels.Soak(ref d, in l));
        Check(count, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) }, NextLevel,
            PairKernels.Sway, (ref Far3 f, ref Level l) => PairKernels.Sway(ref f, in l));
        Check(count, (r, i) => new Cell2 { X = r.NextInt64(long.MinValue, long.MaxValue), Y = r.NextInt64() }, NextTally,
            PairKernels.Walk, (ref Cell2 c, ref Rank r) => PairKernels.Walk(ref c, in r));
    }

    /// <summary>A pair's masked words: the halves of the passing rows' mask keep the other rows as they were.</summary>
    [TestCaseSource(nameof(HoleCases))]
    public void PairsLeaveTheRowsWithoutAComponentAlone(string pattern, int count)
    {
        var hole = Holes.First(h => h.Name == pattern).Hole;
        CheckHoles(count, hole, NextStamp, NextTally, PairKernels.Earn, (ref Stamp s, ref Rank r) => PairKernels.Earn(ref s, in r));
        CheckHoles(count, hole, NextStamp, NextTally, PairKernels.Settle, (ref Stamp s, ref Rank r) => PairKernels.Settle(ref s, in r));
        CheckHoles(count, hole, (r, i) => new Depth { Value = NextDouble(r, i) }, NextLevel, PairKernels.Soak, (ref Depth d, ref Level l) => PairKernels.Soak(ref d, in l));
        CheckHoles(count, hole, (r, i) => new Far3 { X = NextDouble(r, i), Y = NextDouble(r, i + 1), Z = NextDouble(r, i + 2) }, NextLevel,
            PairKernels.Sway, (ref Far3 f, ref Level l) => PairKernels.Sway(ref f, in l));
        CheckHoles(count, hole, (r, i) => new Cell2 { X = r.NextInt64(long.MinValue, long.MaxValue), Y = r.NextInt64() }, NextTally,
            PairKernels.Walk, (ref Cell2 c, ref Rank r) => PairKernels.Walk(ref c, in r));
    }

    /// <summary>
    /// Reductions of int rows into long fields (by hand, through Combine, by attributes) and of short rows into int ones:
    /// one pair of accumulators per field, folded in lane order, the method's result bit for bit, with rows to test.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void ReductionsIntoWiderFieldsMatchTheMethodBitForBit(int count)
    {
        foreach (var hole in new Func<int, bool>[] { _ => false, i => i % 3 == 0, i => (uint)(i * 2654435761u) >> 31 == 1 })
        {
            using var world = new World();
            var random = new Random(count);
            var ranks = new Rank[count];
            var codes = new Code32[count];
            var waves = new Wave16[count];
            var gains = new Gain16[count];
            var entities = new Entity[count];
            world.Reserve<Rank, Code32, Wave16, Gain16>(count);
            for (int i = 0; i < count; i++)
            {
                (ranks[i], codes[i]) = (NextTally(random, i), NextCode(random, i));
                (waves[i], gains[i]) = (new Wave16 { V = (short)random.Next() }, new Gain16 { V = i % 5 == 0 ? short.MinValue : (short)random.Next() });
                entities[i] = world.CreateEntity(ranks[i], codes[i], waves[i], gains[i]);
            }
            for (int i = 0; i < count; i++)
            {
                if (i % 7 == 0) entities[i].Set(new Frozen());
                if (hole(i))
                {
                    entities[i].Remove<Code32>();
                    entities[i].Remove<Gain16>();
                }
            }

            var order = Enumerable.Range(0, count).OrderBy(i => i % 7 == 0 ? 1 : 0).ToArray();
            var wallet = new Reduce<Wallet> { Value = Wallet.Identity };
            foreach (int i in order) PairKernels.Spend(in ranks[i], ref wallet);
            PairKernels.Spend(world);
            Assert.That(world.GetSingleton<Wallet>(), Is.EqualTo(wallet.Value), "Spend");

            wallet = new Reduce<Wallet> { Value = Wallet.Identity };
            foreach (int i in order) PairKernels.Combined(in ranks[i], ref wallet);
            PairKernels.Combined(world);
            Assert.That(world.GetSingleton<Wallet>(), Is.EqualTo(wallet.Value), "Combined");

            var purses = new Reduce<Purses> { Value = Purses.Identity };
            foreach (int i in order) if (!hole(i)) PairKernels.Purse(in ranks[i], in codes[i], ref purses);
            PairKernels.Purse(world);
            Assert.That(world.GetSingleton<Purses>(), Is.EqualTo(purses.Value), "Purse");

            var loud = new Reduce<Loudness> { Value = Loudness.Identity };
            foreach (int i in order) if (!hole(i)) PairKernels.Loud(in waves[i], in gains[i], ref loud);
            PairKernels.Loud(world);
            Assert.That(world.GetSingleton<Loudness>(), Is.EqualTo(loud.Value), "Loud");
        }
    }

    /// <summary>A double sum of float rows under FloatMode.Fast: one sum per lane, within the error bound of the sum.</summary>
    [TestCaseSource(nameof(Lengths))]
    public void AFastDoubleSumOfFloatRowsIsTheSumOfTheRows(int count)
    {
        using var world = new World();
        world.Reserve<Level>(count);
        var random = new Random(count);
        decimal exact = 0;
        double magnitude = 0;
        for (int i = 0; i < count; i++)
        {
            float value = (float)(random.NextDouble() * 2e6 - 1e6);
            world.CreateEntity(new Level { Value = value });
            exact += (decimal)(value * 0.5);
            magnitude += Math.Abs(value * 0.5);
        }

        PairKernels.Pour(world);
        double total = world.GetSingleton<Fluid>().Total;
        Assert.That(Math.Abs(total - (double)exact), Is.LessThanOrEqualTo(count * 1.2e-16 * magnitude), $"{total} against {exact}");
    }

    /// <summary>
    /// Short lanes widened into int lanes (a pair local, a narrowing and a widening back) over random values and the
    /// edges, and byte lanes into ushort lanes over every pair of bytes: the method's result, row by row.
    /// </summary>
    [TestCase(false), TestCase(true)]
    public void NarrowLanesWidenedMatchTheMethod(bool holes)
    {
        int[] edges = [short.MinValue, short.MinValue + 1, -256, -1, 0, 1, 255, short.MaxValue];
        var random = new Random(17);
        CheckNarrow(30000, holes, i => new Rank { Value = i % 9 == 0 ? (1 << 24) - i : random.Next(-1 << 20, 1 << 20) },
            i => new Wave16 { V = (short)(i % 4 == 0 ? edges[random.Next(edges.Length)] : random.Next()) },
            i => new Gain16 { V = (short)(i % 4 == 1 ? edges[random.Next(edges.Length)] : random.Next()) }, PairKernels.Gather, PairKernels.Gather, "Gather");
        CheckNarrow(Pairs, holes, i => new Sum16 { V = (ushort)(i * 977) }, i => new Shade { V = (byte)i }, i => new Tone { V = (byte)(i >> 8) },
            PairKernels.Count, PairKernels.Count, "Count");
    }

    [Test]
    public void ParallelVectorReductionsAreExactAndDoNotDependOnTheThreads()
    {
        (Headcount, float) Run(int threads)
        {
            const int count = 50_003;
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var random = new Random(3);
            var expected = new Reduce<Headcount> { Value = Headcount.Identity };
            world.Reserve<Rank, Level>(count);
            for (int i = 0; i < count; i++)
            {
                var rank = NextTally(random, i);
                var e = world.CreateEntity(rank, new Level { Value = (float)(random.NextDouble() * 200 - 100) });
                if (i % 13 == 0) e.Set(new Frozen());
                if (i % 4 == 1) e.Remove<Rank>();
                else ParallelKernels.Count(in rank, ref expected);
            }

            ParallelKernels.Count(world);
            Assert.That(world.GetSingleton<Headcount>(), Is.EqualTo(expected.Value), $"Count at {threads} threads");
            ParallelKernels.Sum(world);
            return (world.GetSingleton<Headcount>(), world.GetSingleton<Energy>().Total);
        }

        var (one, sum1) = Run(1);
        var (four, sum4) = Run(4);
        Assert.That(four, Is.EqualTo(one));
        Assert.That(BitConverter.SingleToInt32Bits(sum4), Is.EqualTo(BitConverter.SingleToInt32Bits(sum1)), $"{sum4} against {sum1}");
    }

    /// <summary>
    /// Degrees to radians (and back) are the scalar's two IEEE operations, and a uniform Sin is the scalar call once per
    /// run: bit for bit the method's, under FloatMode.Exact.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void DegreesAndUniformCallsMatchTheMethodBitForBit(int count)
    {
        foreach (float pull in new[] { 0.3f, float.NaN, -1e7f })
            Check(count, NextLevel, (r, i) => new Heat { Value = NextFloat(r, i + 1) }, w => { w.SetGravity(pull); MathKernels.Turn(w); },
                (ref Level l, ref Heat h) => MathKernels.Turn(ref l, in h, new Gravity(pull)));
    }

    /// <summary>
    /// Sin, Cos, Exp, Log, Log2 and Hypot under FloatMode.Fast: .NET's vector forms, within 2 ulps of the scalar
    /// function (NaN where it is NaN, infinities and zeros where it has them), lane by lane; the rows after the last
    /// vector are the scalar function's.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void FastMathIsWithinTwoUlpsOfTheScalarFunctions(int count)
    {
        static long Ulps(double a, double b, bool single)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b) ? 0 : long.MaxValue;
            if (a == b) return 0;
            if (double.IsInfinity(a) || double.IsInfinity(b) || Math.Sign(a) != Math.Sign(b)) return long.MaxValue;
            return single ? Math.Abs(BitConverter.SingleToInt32Bits((float)a) - (long)BitConverter.SingleToInt32Bits((float)b))
                : Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));
        }

        using var world = new World();
        var random = new Random(count);
        var levels = new Level[count];
        var paces = new Pace[count];
        var spots = new Spot2[count];
        var entities = new Entity[count];
        world.Reserve<Color4, Level, Spot2, Far3, Pace>(count);
        for (int i = 0; i < count; i++)
        {
            float x = i % 3 == 0 ? NextFloat(random, i) : (float)(random.NextDouble() * 200 - 100);
            levels[i] = new Level { Value = x };
            paces[i] = new Pace { Value = NextDouble(random, i) };
            spots[i] = new Spot2 { Y = NextFloat(random, i + 1) };
            entities[i] = world.CreateEntity(new Color4(), levels[i], spots[i], new Far3(), paces[i]);
            if (i % 7 == 0) entities[i].Set(new Frozen());
        }

        MathKernels.Waves(world);
        MathKernels.Lengths(world);
        MathKernels.Far(world);
        for (int i = 0; i < count; i++)
        {
            float x = levels[i].Value;
            double d = paces[i].Value;
            var c = entities[i].Get<Color4>();
            var s = entities[i].Get<Spot2>();
            var f = entities[i].Get<Far3>();
            (string Name, double Got, double Want, bool Single)[] lanes =
            [
                ("Sin", c.R, MathF.Sin(x), true), ("Cos", c.G, MathF.Cos(x), true), ("Exp", c.B, MathF.Exp(x), true),
                ("Log", c.A, MathF.Log(MathF.Abs(x)), true), ("Log2", s.X, MathF.Log2(MathF.Abs(x)), true),
                ("Hypot", s.Y, float.Hypot(x, spots[i].Y), true), ("Math.Sin", f.X, Math.Sin(d), false),
                ("Math.Exp", f.Y, Math.Exp(d * 0.01), false), ("Math.Log", f.Z, Math.Log(Math.Abs(d)), false),
            ];
            foreach (var (name, got, want, single) in lanes)
                Assert.That(Ulps(got, want, single), Is.LessThanOrEqualTo(2), $"{name} of row {i} ({x}, {d}): {got} instead of {want}");
        }
    }

    /// <summary>
    /// Maybe&lt;T&gt; as a lane mask, bit for bit the method's: tables with the optional column (some rows without it),
    /// without it, and rows that lack a queried component (masked words); written through, stored only where rows have
    /// it, and stamped where they have it.
    /// </summary>
    [TestCaseSource(nameof(Lengths))]
    public void OptionalComponentsAreLaneMasks(int count)
    {
        using var world = new World();
        var random = new Random(count);
        var heats = new Heat[count];
        var levels = new Level[count];
        var spots = new Spot2[count];
        var boosts = new Boost[count];
        var winds = new Wind2[count];
        bool[] hasBoost = new bool[count], hasWind = new bool[count], hasHeat = new bool[count], hasSpot = new bool[count];
        var entities = new Entity[count];
        world.Reserve<Heat, Level, Spot2, Boost, Wind2>(count);
        for (int i = 0; i < count; i++)
        {
            heats[i] = new Heat { Value = NextFloat(random, i) };
            levels[i] = NextLevel(random, i + 1);
            spots[i] = new Spot2 { X = NextFloat(random, i), Y = NextFloat(random, i + 2) };
            boosts[i] = new Boost { Value = NextFloat(random, i + 3) };
            winds[i] = new Wind2 { X = NextFloat(random, i + 1), Y = NextFloat(random, i + 4) };
            var e = entities[i] = world.CreateEntity(heats[i], levels[i], spots[i]);
            hasHeat[i] = hasSpot[i] = true;
            int table = i % 5;
            if (table != 4) // the fifth table has neither optional column
            {
                e.Set(boosts[i]);
                e.Set(winds[i]);
                hasBoost[i] = hasWind[i] = true;
                if (table == 3) e.Set(new Frozen());
            }
        }
        for (int i = 0; i < count; i++)
        {
            int table = i % 5;
            if (table == 1 && i % 3 == 0) { entities[i].Remove<Boost>(); hasBoost[i] = false; }  // presence holes
            if (table == 1 && i % 7 == 0) { entities[i].Remove<Wind2>(); hasWind[i] = false; }
            if (table == 2 && i % 4 == 0) { entities[i].Remove<Heat>(); hasHeat[i] = false; }    // masked words
            if (table == 2 && i % 6 == 0) { entities[i].Remove<Spot2>(); hasSpot[i] = false; }
        }

        world.Update();
        ulong mark = world.ChangeTick;
        for (int round = 0; round < 2; round++)
        {
            OptionalKernels.Push(world);
            OptionalKernels.Blow(world);
            OptionalKernels.Peek(world);
            for (int i = 0; i < count; i++)
            {
                var boost = hasBoost[i] ? new Maybe<Boost>(ref boosts[i]) : default;
                var wind = hasWind[i] ? new Maybe<Wind2>(ref winds[i]) : default;
                if (hasHeat[i]) OptionalKernels.Push(ref heats[i], boost);
                if (hasSpot[i]) OptionalKernels.Blow(ref spots[i], wind);
                if (hasHeat[i]) OptionalKernels.Peek(ref levels[i], in heats[i], boost);
            }
        }

        // Rows Blow reached with a Wind2 are stamped (the indexer's stamps), the others are not (checked before Get,
        // which writes).
        int stamped = 0;
        for (int i = 0; i < count; i++) if (hasWind[i] && hasSpot[i]) stamped++;
        Assert.That(world.Count<Wind2>(QueryFilter.Create().Changed<Wind2>(mark)), Is.EqualTo(stamped));
        foreach (var item in world.Read<Wind2>(QueryFilter.Create().Changed<Wind2>(mark)))
            Assert.That(item.Entity.Has<Spot2>(), $"{item.Entity} was stamped without being visited");
        for (int i = 0; i < count; i++)
        {
            if (hasHeat[i]) Assert.That(Same(entities[i].Get<Heat>(), heats[i]), $"Heat of row {i}: {Bits(entities[i].Get<Heat>())} instead of {Bits(heats[i])}");
            Assert.That(Same(entities[i].Get<Level>(), levels[i]), $"Level of row {i}: {Bits(entities[i].Get<Level>())} instead of {Bits(levels[i])}");
            if (hasSpot[i]) Assert.That(Same(entities[i].Get<Spot2>(), spots[i]), $"Spot2 of row {i}");
            if (hasBoost[i]) Assert.That(Same(entities[i].Get<Boost>(), boosts[i]), $"Boost of row {i}");
            if (hasWind[i]) Assert.That(Same(entities[i].Read<Wind2>(), winds[i]), $"Wind2 of row {i}: {Bits(entities[i].Read<Wind2>())} instead of {Bits(winds[i])}");
        }
    }

    /// <summary>The world of <see cref="OptionalComponentsInParallelChunksAreTheSerialLoops"/>: optional columns with holes, rows lacking a queried component.</summary>
    private static (World World, Entity[] Entities) OptionalWorld(int count, int threads)
    {
        var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        var random = new Random(count);
        var entities = new Entity[count];
        world.Reserve<Heat, Level, Spot2, Boost, Wind2>(count);
        for (int i = 0; i < count; i++)
        {
            var e = entities[i] = world.CreateEntity(new Heat { Value = NextFloat(random, i) }, NextLevel(random, i + 1),
                new Spot2 { X = NextFloat(random, i), Y = NextFloat(random, i + 2) });
            if (i % 5 != 4) // the fifth table has neither optional column
            {
                e.Set(new Boost { Value = NextFloat(random, i + 3) });
                e.Set(new Wind2 { X = NextFloat(random, i + 1), Y = NextFloat(random, i + 4) });
                if (i % 5 == 3) e.Set(new Frozen());
            }
        }
        for (int i = 0; i < count; i++)
        {
            if (i % 5 == 1 && i % 3 == 0) entities[i].Remove<Boost>(); // presence holes
            if (i % 5 == 1 && i % 7 == 0) entities[i].Remove<Wind2>();
            if (i % 5 == 2 && i % 4 == 0) entities[i].Remove<Heat>();  // masked words
            if (i % 5 == 2 && i % 6 == 0) entities[i].Remove<Spot2>();
        }

        world.Update();
        return (world, entities);
    }

    /// <summary>
    /// Maybe&lt;T&gt; and masked words in parallel chunks: the same rows, bits and stamps as the vector loops on one thread
    /// (proven against the method by <see cref="OptionalComponentsAreLaneMasks"/>), at 1 and 4 threads.
    /// </summary>
    [TestCase(300, 1)]
    [TestCase(20_000, 1)]
    [TestCase(20_000, 4)]
    public void OptionalComponentsInParallelChunksAreTheSerialLoops(int count, int threads)
    {
        var (serial, a) = OptionalWorld(count, 1);
        var (parallel, b) = OptionalWorld(count, threads);
        using var _ = serial;
        using var __ = parallel;
        // SimdMode.Require: each method has its vector loop (GeneratorTests: the parallel job walks masked words).
        ulong mark = parallel.ChangeTick;
        for (int round = 0; round < 2; round++)
        {
            OptionalKernels.Push(serial);
            OptionalKernels.Blow(serial);
            OptionalKernels.Peek(serial);
            ParallelOptionalKernels.Push(parallel);
            ParallelOptionalKernels.Blow(parallel);
            ParallelOptionalKernels.Peek(parallel);
        }

        Assert.That(parallel.Count<Wind2>(QueryFilter.Create().Changed<Wind2>(mark)), Is.EqualTo(serial.Count<Wind2>(QueryFilter.Create().Changed<Wind2>(mark))));
        for (int i = 0; i < count; i++)
        {
            if (a[i].Has<Heat>()) Assert.That(Same(b[i].Read<Heat>(), a[i].Read<Heat>()), $"Heat of row {i}");
            Assert.That(Same(b[i].Read<Level>(), a[i].Read<Level>()), $"Level of row {i}");
            if (a[i].Has<Spot2>()) Assert.That(Same(b[i].Read<Spot2>(), a[i].Read<Spot2>()), $"Spot2 of row {i}");
            if (a[i].Has<Wind2>()) Assert.That(Same(b[i].Read<Wind2>(), a[i].Read<Wind2>()), $"Wind2 of row {i}");
        }
    }

    /// <summary>A read of Value for a row without the component throws for that row, after the rows before it, as the method does.</summary>
    [Test]
    public void AnOptionalReadWithoutHasValueThrowsForItsRow()
    {
        const int count = 100;
        using var world = new World();
        var entities = new Entity[count];
        for (int i = 0; i < count; i++) entities[i] = world.CreateEntity(new Level { Value = i }, new Boost { Value = i * 10 });
        OptionalKernels.Careless(world); // every row has Boost
        for (int i = 0; i < count; i++) Assert.That(entities[i].Get<Level>().Value, Is.EqualTo(i * 10 + 1));

        for (int i = 0; i < count; i++) entities[i].SetBoost(i * 100);
        entities[37].Remove<Boost>();
        Assert.Throws<InvalidOperationException>(() => OptionalKernels.Careless(world));
        for (int i = 0; i < count; i++)
            Assert.That(entities[i].Get<Level>().Value, Is.EqualTo(i < 37 ? i * 100 + 1 : i * 10 + 1), $"row {i}");
    }

    [Test]
    public void VectorLoopsDoNotAllocate()
    {
        using var world = new World();
        world.SetGravity(0.5f);
        for (int i = 0; i < 1000; i++)
        {
            world.CreateEntity(new Heat { Value = i }, new Speed(), new Mass { Value = 1 }, new Bits(i), new Mask(i));
            world.CreateEntity(new Spot3 { X = i }, new Drift3(), new Spot2(), new Drift2(), new Far3(), new Pace { Value = 2 });
            var e = world.CreateEntity(new Level { Value = i }, new Rank { Value = i - 500 }, new Code32 { Value = (uint)i }, new Spot2 { Y = float.PositiveInfinity }); // no Clamp throws
            if (i % 3 == 0) e.Remove<Rank>(); // rows to test: masked words
            var n = world.CreateEntity(new Ink(), new Shade { V = (byte)i }, new Tone { V = (byte)(i * 7) }, new Rgba(), new Tint4());
            if (i % 4 == 0) n.Remove<Tone>(); // narrow masked words: the method on their passing rows
            world.CreateEntity(new Place2(new Vector2(i, 1)), new Pace2(Vector2.One), new Body3 { M = 2 }, new Pace3(Vector3.One));
            var w = world.CreateEntity(new Craft12 { VX = i }, new Mixed { Speed = i }, new Level());
            if (i % 5 == 0) w.Remove<Craft12>(); // wide masked words: the method on their passing rows
            var p = world.CreateEntity(new Stamp { Value = i }, new Depth { Value = i }, new Rank { Value = i }, new Code32 { Value = (uint)i }, new Level { Value = i });
            if (i % 6 == 0) p.Remove<Rank>(); // the masked words of pairs
        }
        world.SetBounds(-10, 10);
        world.SetDt2(0.5f);

        void Frame()
        {
            Kernels.Step(world);
            Kernels.Guard(world, 2f);
            Kernels.Shift(world);
            FieldKernels.Drift(world);
            FieldKernels.Seek(world, 4f);
            FieldKernels.Travel(world);
            FieldKernels.Glide(world);
            FieldKernels.Bounce(world, 0.25f);
            MoreKernels.Clamp(world);
            MoreKernels.ClampLanes(world);
            MoreKernels.Blend(world);
            MoreKernels.Convert(world);
            MoreKernels.Masks(world);
            ReduceKernels.Count(world);
            ReduceKernels.Span(world);
            ReduceKernels.Sum(world);
            MathKernels.Turn(world);
            MathKernels.Lengths(world);
            ParallelKernels.Glide(world);
            ParallelKernels.Count(world);
            OptionalKernels.Push(world); // tables without the optional column
            NarrowKernels.Band(world);
            NarrowKernels.Pick(world);
            NarrowKernels.Blend(world);
            StructKernels.Move(world);
            StructKernels.Bounce(world);
            WideKernels.Steer(world);
            WideKernels.Wear(world);
            StructKernels.Weigh(world);
            PairKernels.Spend(world);
            PairKernels.Purse(world);
            PairKernels.Accrue(world);
            PairKernels.Refine(world);
            PairKernels.Earn(world);
            ParallelKernels.Spend(world);
        }

        Allocations.WarmUp(Frame, 50); // the query cache, the static index tables of Interleaved, until the JIT settles
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 10; i++) Frame(); }), Is.Zero); // parallel kernels wake helpers
    }

    // Lane counts that leave a tail, and two tables (the Frozen tag) so a pass sees several runs.
    [TestCase(1)]
    [TestCase(7)]
    [TestCase(1003)]
    public void VectorLoopsMatchTheMethodBitForBit(int count)
    {
        float[] special = [float.NaN, -0f, 0f, 1f, -1f, 100f, 101f, float.PositiveInfinity, float.NegativeInfinity, 1e-30f];
        var random = new Random(count);
        float Next(int i) => i % 5 == 0 ? special[random.Next(special.Length)] : (float)(random.NextDouble() * 300 - 100);

        using var world = new World();
        world.SetGravity(0.75f);
        var heat = new Heat[count];
        var speed = new Speed[count];
        var mass = new Mass[count];
        var bits = new Bits[count];
        var masks = new Mask[count];
        var entities = new Entity[count];
        world.Reserve<Heat, Speed, Mass, Bits, Mask>(count);
        for (int i = 0; i < count; i++)
        {
            heat[i].Value = Next(i);
            speed[i].Value = Next(i + 1);
            mass[i].Value = Next(i + 2);
            bits[i] = new Bits(random.Next(int.MinValue, int.MaxValue));
            masks[i] = new Mask(random.Next(int.MinValue, int.MaxValue));
            entities[i] = world.CreateEntity(heat[i], speed[i], mass[i], bits[i], masks[i]);
            if (i % 3 == 0) entities[i].Set(new Frozen());
        }

        var gravity = world.Gravity;
        for (int round = 0; round < 3; round++)
        {
            Kernels.Step(world);
            Kernels.Guard(world, 1.5f);
            Kernels.Shift(world);
            for (int i = 0; i < count; i++)
            {
                Kernels.Step(ref heat[i], ref speed[i], in mass[i], in gravity);
                Kernels.Guard(ref heat[i], in mass[i], 1.5f);
                Kernels.Shift(ref bits[i], in masks[i]);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Heat.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(heat[i].Value)), $"heat {i}");
            Assert.That(BitConverter.SingleToInt32Bits(entities[i].Speed.Value), Is.EqualTo(BitConverter.SingleToInt32Bits(speed[i].Value)), $"speed {i}");
            Assert.That(entities[i].Bits, Is.EqualTo(bits[i]), $"bits {i}");
        }
    }
}
