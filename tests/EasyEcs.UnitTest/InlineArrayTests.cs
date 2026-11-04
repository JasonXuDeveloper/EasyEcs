using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Eight gains of a band, an inline array.</summary>
[InlineArray(8)]
public struct Bands
{
    private float _b;
}

/// <summary>A component holding an inline array beside a plain field.</summary>
public struct Spectrum : IComponent
{
    public float Level;
    public Bands Gain;
}

/// <summary>A method writing the plain field of a component that also holds an inline array (scalar: EECS055 says why).</summary>
[EcsSystem]
public ref struct SpectrumFade
{
    [ForEach] static void Fade(ref Spectrum s) => s.Level = s.Level * 0.5f + 1;
}

/// <summary><see cref="SpectrumFade"/> one row at a time.</summary>
[EcsSystem]
public ref struct SpectrumFadeByRow
{
    [ForEach(SimdMode = SimdMode.Off)] static void Fade(ref Spectrum s) => s.Level = s.Level * 0.5f + 1;
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
}

