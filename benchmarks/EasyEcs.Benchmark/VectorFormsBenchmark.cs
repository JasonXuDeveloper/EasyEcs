using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The vector-loop forms of D90's roadmap item (feat-inner-loops-rest (g)), each the plain method (Vector) against the
/// same method under SimdMode.Off (Scalar), over <see cref="Rows"/> rows of which every <see cref="HoleEvery"/>-th lacks
/// a queried component (presence bits: the words those rows sit in are masked words). Measured base against head too:
/// before these forms a parallel method with masked words ran its runs of passing rows (ForEachBatchParallel), a
/// parallel or narrow method with a Maybe&lt;T&gt; was scalar, narrow lanes' masked words ran the method.
/// <list type="bullet">
/// <item><c>MaskedParallel</c>: a parallel float method, masked words in each chunk (ForEachMaskedParallel).</item>
/// <item><c>MaybeParallel</c>: a parallel float method taking a ReadOnlyMaybe&lt;T&gt; that half the rows have.</item>
/// <item><c>MaskedNarrow</c>: a byte method on one thread, masked words of 64 byte lanes (Interleaved.Lanes8).</item>
/// <item><c>MaybeNarrow</c>: a byte method taking a ReadOnlyMaybe&lt;T&gt; of bytes.</item>
/// <item><c>Pow</c>: MathF.Pow(x, 1.5f) under FloatMode.Fast (LaneMath.Pow), no holes.</item>
/// <item><c>MarkNarrow</c>: a tag set under a condition of byte lanes (marked rows, applied in bulk), then cleared by
/// RemoveAll for the next run; before, a structural change with narrow lanes kept the method scalar.</item>
/// </list>
/// The vector and scalar worlds end alike (checked in setup). Allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class VectorFormsBenchmark
{
    [Params(100_000)] public int Rows;

    [Params(8)] public int HoleEvery;

    [Params("MaskedParallel", "MaybeParallel", "MaskedNarrow", "MaybeNarrow", "Pow", "MarkNarrow")] public string Case;

    private World _vector, _scalar;

    public record struct Charge(float Value) : IComponent;
    public record struct Drain(float Value) : IComponent;
    public record struct Boost(float Value) : IComponent;
    public record struct Dye(byte Value) : IComponent;
    public record struct Tint(byte Value) : IComponent;
    public record struct Gloss(byte Value) : IComponent;
    public struct Lit : IComponent;

    [GlobalSetup]
    public void Setup()
    {
        _vector = Populate();
        _scalar = Populate();
        for (int i = 0; i < 3; i++)
        {
            Vector();
            Scalar();
        }

        var scalar = _scalar.Read<Charge, Dye>().GetEnumerator();
        foreach (var row in _vector.Read<Charge, Dye>())
            if (!scalar.MoveNext() || scalar.Current.Component2 != row.Component2
                || MathF.Abs(scalar.Current.Component1.Value - row.Component1.Value) > 1e-5f * MathF.Abs(row.Component1.Value)) // Pow: FloatMode.Fast, within 2 ulps
                throw new InvalidOperationException($"{Case}: the vector loop differs from the method");
    }

    private World Populate()
    {
        var world = new World(new WorldOptions { InitialEntityCapacity = Rows });
        world.Reserve<Charge, Drain, Boost, Dye, Tint, Gloss>(Rows);
        var entities = new Entity[Rows];
        for (int i = 0; i < Rows; i++)
            entities[i] = world.CreateEntity(new Charge(i % 101 * 0.5f + 1), new Drain(i % 7 * 0.25f), new Boost(i % 5 + 1),
                new Dye((byte)(i * 37)), new Tint((byte)(i * 11)), new Gloss((byte)i));
        bool holes = Case != "Pow";
        for (int i = 0; i < Rows; i++)
        {
            if (holes && i % HoleEvery == 3) // presence holes of queried components: masked words
            {
                entities[i].Remove<Drain>();
                entities[i].Remove<Tint>();
            }
            if (i % 2 == 1) // the optional components on half the rows
            {
                entities[i].Remove<Boost>();
                entities[i].Remove<Gloss>();
            }
        }
        return world;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _vector.Dispose();
        _scalar.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Scalar()
    {
        switch (Case)
        {
            case "MaskedParallel": Forms.MaskedParallelScalar(_scalar); break;
            case "MaybeParallel": Forms.MaybeParallelScalar(_scalar); break;
            case "MaskedNarrow": Forms.MaskedNarrowScalar(_scalar); break;
            case "MaybeNarrow": Forms.MaybeNarrowScalar(_scalar); break;
            case "MarkNarrow": Forms.MarkNarrowScalar(_scalar); _scalar.RemoveAll<Lit>(); break;
            default: Forms.PowScalar(_scalar); break;
        }
    }

    [Benchmark]
    public void Vector()
    {
        switch (Case)
        {
            case "MaskedParallel": Forms.MaskedParallel(_vector); break;
            case "MaybeParallel": Forms.MaybeParallel(_vector); break;
            case "MaskedNarrow": Forms.MaskedNarrow(_vector); break;
            case "MaybeNarrow": Forms.MaybeNarrow(_vector); break;
            case "MarkNarrow": Forms.MarkNarrow(_vector); _vector.RemoveAll<Lit>(); break;
            default: Forms.Pow(_vector); break;
        }
    }

    /// <summary>The passes of <see cref="VectorFormsBenchmark"/>: each method twice, as a vector loop and under SimdMode.Off.</summary>
    public static class Forms
    {
        [ForEach(Parallel = true)]
        internal static void MaskedParallel(ref Charge charge, in Drain drain) => charge = new Charge(charge.Value * 0.999f - drain.Value + 0.25f);

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        internal static void MaskedParallelScalar(ref Charge charge, in Drain drain) => charge = new Charge(charge.Value * 0.999f - drain.Value + 0.25f);

        // Every row settles (at 250 without Boost, Boost is 1 to 5) instead of decaying: a value decaying by 0.999 per
        // call reaches the subnormal floats, which slow every lane and row alike and measure nothing of the loop.
        [ForEach(Parallel = true)]
        internal static void MaybeParallel(ref Charge charge, ReadOnlyMaybe<Boost> boost) =>
            charge = new Charge(boost.HasValue ? charge.Value * 0.999f + boost.Value.Value : charge.Value * 0.999f + 0.25f);

        [ForEach(Parallel = true, SimdMode = SimdMode.Off)]
        internal static void MaybeParallelScalar(ref Charge charge, ReadOnlyMaybe<Boost> boost) =>
            charge = new Charge(boost.HasValue ? charge.Value * 0.999f + boost.Value.Value : charge.Value * 0.999f + 0.25f);

        [ForEach]
        internal static void MaskedNarrow(ref Dye dye, in Tint tint) => dye = new Dye((byte)Math.Min(dye.Value + tint.Value, 255));

        [ForEach(SimdMode = SimdMode.Off)]
        internal static void MaskedNarrowScalar(ref Dye dye, in Tint tint) => dye = new Dye((byte)Math.Min(dye.Value + tint.Value, 255));

        [ForEach]
        internal static void MaybeNarrow(ref Dye dye, ReadOnlyMaybe<Gloss> gloss) =>
            dye = new Dye(gloss.HasValue ? (byte)(dye.Value ^ gloss.Value.Value) : (byte)(dye.Value + 1));

        [ForEach(SimdMode = SimdMode.Off)]
        internal static void MaybeNarrowScalar(ref Dye dye, ReadOnlyMaybe<Gloss> gloss) =>
            dye = new Dye(gloss.HasValue ? (byte)(dye.Value ^ gloss.Value.Value) : (byte)(dye.Value + 1));

        [ForEach, Without<Lit>]
        internal static void MarkNarrow(Entity e, in Dye dye, in Tint tint)
        {
            if (dye.Value > 250 && tint.Value < 128) e.Set(new Lit());
        }

        [ForEach(SimdMode = SimdMode.Off), Without<Lit>]
        internal static void MarkNarrowScalar(Entity e, in Dye dye, in Tint tint)
        {
            if (dye.Value > 250 && tint.Value < 128) e.Set(new Lit());
        }

        [ForEach(FloatMode = FloatMode.Fast)]
        internal static void Pow(ref Charge charge) => charge = new Charge(MathF.Pow(charge.Value, 1.5f) * 0.001f + 1);

        [ForEach(FloatMode = FloatMode.Fast, SimdMode = SimdMode.Off)]
        internal static void PowScalar(ref Charge charge) => charge = new Charge(MathF.Pow(charge.Value, 1.5f) * 0.001f + 1);
    }
}
