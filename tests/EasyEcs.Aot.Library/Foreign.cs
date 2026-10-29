using EasyEcs;

namespace EasyEcs.Aot.Library;

// Components and helpers of a library whose [ForEach] callers live in other assemblies (EasyEcs.UnitTest's
// ForeignBodyTests, EasyEcs.Aot): this assembly's generator records what their constructors set and their bodies, so
// the callers' vector loops take them as if declared beside them.

/// <summary>A positional record struct: <c>new Ember(x, y)</c> sets X and Y.</summary>
public record struct Ember(float X, float Y) : IComponent;

/// <summary>A component whose field is private: its members' recorded bodies reach it through placeholders.</summary>
public struct Furnace : IComponent
{
    private float _degrees;

    public Furnace(float degrees) => _degrees = degrees;

    public readonly float Degrees => _degrees;

    /// <summary>Writes the private field, scaled by an internal type's static readonly field.</summary>
    public void Warm(float by) => _degrees += by * Tables.Gain;
}

/// <summary>Small public helpers, some calling what the library hides.</summary>
public static class Physics
{
    private static readonly float s_scale = 0.5f;

    /// <summary>A private static readonly field and a private helper.</summary>
    public static float Scaled(float v) => Clamp01(v * s_scale);

    private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>An extension method reading an internal type's field.</summary>
    public static float Cool(this float v, float by) => v - by * Tables.Gain;

    /// <summary>A loop: recorded with its reason, the caller stays scalar.</summary>
    public static float Steps(float n)
    {
        float s = 0;
        for (int i = 0; i < n; i++) s += i;
        return s;
    }
}

internal static class Tables
{
    public static readonly float Gain = 2f;
}

/// <summary>A constructor that computes its field: recorded with the reason.</summary>
public struct Warmth : IComponent
{
    public float Value;

    public Warmth(float value) => Value = value * 2;
}

/// <summary>A positional property whose initializer changes it: recorded with the reason.</summary>
public record struct Shifted(float X) : IComponent
{
    public float X { get; set; } = X + 1;
}
