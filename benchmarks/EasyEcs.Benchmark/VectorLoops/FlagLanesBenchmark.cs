using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Methods over enum and bool components. Before enums and bools had lanes these methods ran row by row (<c>*Scalar</c>:
/// the same method with <c>SimdMode.Off</c>, the loop they had); now each is a vector loop of 1-byte lanes
/// (<c>*Vector</c>). <c>Enum</c> sets a byte enum from two byte components; <c>Bool</c> writes a bool from a comparison
/// and itself; <c>BoolRead</c> writes a byte under a bool. Both forms end bit for bit equal (KernelTests checks every
/// pair of operands). Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public class FlagLanesBenchmark
{
    [Params(100_000)] public int Rows;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _world.Reserve<Mood, Fear, Nerve, Alert, Glow>(Rows);
        for (int i = 0; i < Rows; i++)
            _world.CreateEntity(new Mood((Moods)(i % 3)), new Fear((byte)(i * 7)), new Nerve((byte)(i * 13)), new Alert(i % 2 == 0), new Glow((byte)i));
        for (int i = 0; i < 10; i++)
        {
            EnumScalar();
            EnumVector();
            BoolScalar();
            BoolVector();
            BoolReadScalar();
            BoolReadVector();
        }
    }

    [GlobalCleanup]
    public void Dispose() => _world.Dispose();

    [Benchmark(Baseline = true)] public void EnumScalar() => Flags.RageScalar(_world);
    [Benchmark] public void EnumVector() => Flags.Rage(_world);
    [Benchmark] public void BoolScalar() => Flags.WakeScalar(_world);
    [Benchmark] public void BoolVector() => Flags.Wake(_world);
    [Benchmark] public void BoolReadScalar() => Flags.PaintScalar(_world);
    [Benchmark] public void BoolReadVector() => Flags.Paint(_world);

    public enum Moods : byte { Calm, Wary, Angry }
    public record struct Mood(Moods Value) : IComponent;
    public record struct Fear(byte Value) : IComponent;
    public record struct Nerve(byte Value) : IComponent;
    public record struct Alert(bool Value) : IComponent;
    public record struct Glow(byte Value) : IComponent;
}

public static class Flags
{
    [ForEach(SimdMode = SimdMode.Require)]
    public static void Rage(ref FlagLanesBenchmark.Mood mood, in FlagLanesBenchmark.Fear fear, in FlagLanesBenchmark.Nerve nerve)
    {
        if (fear.Value > nerve.Value) mood.Value = FlagLanesBenchmark.Moods.Angry;
        else if (mood.Value == FlagLanesBenchmark.Moods.Angry) mood.Value = FlagLanesBenchmark.Moods.Wary;
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void RageScalar(ref FlagLanesBenchmark.Mood mood, in FlagLanesBenchmark.Fear fear, in FlagLanesBenchmark.Nerve nerve)
    {
        if (fear.Value > nerve.Value) mood.Value = FlagLanesBenchmark.Moods.Angry;
        else if (mood.Value == FlagLanesBenchmark.Moods.Angry) mood.Value = FlagLanesBenchmark.Moods.Wary;
    }

    [ForEach(SimdMode = SimdMode.Require)]
    public static void Wake(ref FlagLanesBenchmark.Alert alert, in FlagLanesBenchmark.Fear fear, in FlagLanesBenchmark.Nerve nerve) =>
        alert.Value = fear.Value > nerve.Value || alert.Value && nerve.Value != 0;

    [ForEach(SimdMode = SimdMode.Off)]
    public static void WakeScalar(ref FlagLanesBenchmark.Alert alert, in FlagLanesBenchmark.Fear fear, in FlagLanesBenchmark.Nerve nerve) =>
        alert.Value = fear.Value > nerve.Value || alert.Value && nerve.Value != 0;

    [ForEach(SimdMode = SimdMode.Require)]
    public static void Paint(ref FlagLanesBenchmark.Glow glow, in FlagLanesBenchmark.Alert alert, in FlagLanesBenchmark.Fear fear)
    {
        if (alert.Value) glow.Value = fear.Value;
        else glow.Value = (byte)(glow.Value >> 1);
    }

    [ForEach(SimdMode = SimdMode.Off)]
    public static void PaintScalar(ref FlagLanesBenchmark.Glow glow, in FlagLanesBenchmark.Alert alert, in FlagLanesBenchmark.Fear fear)
    {
        if (alert.Value) glow.Value = fear.Value;
        else glow.Value = (byte)(glow.Value >> 1);
    }
}
