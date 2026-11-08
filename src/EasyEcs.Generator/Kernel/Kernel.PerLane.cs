using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace EasyEcs.Generator;

/// <summary>
/// Math with no vector form in .NET 10 (<c>Asin</c>, <c>Acos</c>, <c>Atan</c>, <c>Atan2</c>, <c>Cbrt</c>, the hyperbolic
/// functions of doubles, <c>Pow</c> by a value), or none bit-exact without <c>FloatMode.Fast</c> (<c>Sin</c>, <c>Cos</c>,
/// <c>Tan</c>, <c>Exp</c>, <c>Log</c>, <c>Log2</c>, <c>Log10</c>, the hyperbolic functions of floats), called once per
/// lane inside the vector loop (<c>LaneMath.PerLane</c>): each lane is the scalar function's result, bit for bit, and
/// the rest of the body stays in vectors. The calls cost what the method's do, plus a round trip of the lanes through
/// memory, so the loop pays only when the body does enough besides them (<see cref="GatePerLane"/>).
/// </summary>
internal sealed partial class Kernel
{
    /// <summary>The calls made once per lane in the body translated (<see cref="PerLaneCall"/>).</summary>
    private int _perLaneCalls;

    /// <summary>The functions called once per lane, as the reason names them (MathF.Atan2), in the order met.</summary>
    private readonly System.Collections.Generic.List<string> _perLaneNames = [];

    /// <summary>Whether one of them has a vector form under FloatMode.Fast (Sin, Exp, Pow of floats, ...).</summary>
    private bool _perLaneFast;

    /// <summary>
    /// The vector operations the body must do per call made once per lane for its vector loop to pay: below it, a
    /// method's rows run as written (KernelBenchmark's Aim pair against its Bearing pair).
    /// </summary>
    internal const int PerLaneWork = 8;

    /// <summary>The lanes of a math call through <c>LaneMath.PerLane</c>, or null when the call has a vector form here.</summary>
    private string? PerLaneCall(IInvocationOperation call)
    {
        var m = call.TargetMethod;
        var type = m.ReturnType;
        if (!IsFloat(type) || !m.Parameters.All(p => SymbolEqualityComparer.Default.Equals(p.Type, type))) return null;
        bool single = type.SpecialType == SpecialType.System_Single;
        bool perLane = m.Name switch
        {
            "Asin" or "Acos" or "Atan" or "Cbrt" => m.Parameters.Length == 1,
            "Atan2" => m.Parameters.Length == 2,
            "Sinh" or "Cosh" or "Tanh" => m.Parameters.Length == 1 && (!_fast || !single),
            "Sin" or "Cos" or "Tan" or "Exp" or "Log" or "Log2" or "Log10" => m.Parameters.Length == 1 && !_fast,
            "Pow" => m.Parameters.Length == 2 && PowPerLane(call.Arguments.First(a => a.Parameter?.Ordinal == 1).Value, single),
            _ => false,
        };
        if (!perLane) return null;
        _perLaneCalls++;
        string named = $"{m.ContainingType.Name}.{m.Name}";
        if (!_perLaneNames.Contains(named)) _perLaneNames.Add(named);
        _perLaneFast |= !_fast && (m.Name is "Sin" or "Cos" or "Tan" or "Exp" or "Log" or "Log2" or "Log10" || single && m.Name is "Sinh" or "Cosh" or "Tanh" or "Pow");
        var args = call.Arguments.OrderBy(a => a.Parameter?.Ordinal).Select(a => Expr(a.Value));
        return $"global::EasyEcs.LaneMath.PerLane.Of<global::EasyEcs.LaneMath.PerLane.{m.Name}, {VecOf(type)}>({string.Join(", ", args)})";
    }

    /// <summary>
    /// Whether <c>Pow</c> by <paramref name="exponent"/> goes lane by lane: never by 0 or 1 (exact as vectors), under
    /// FloatMode.Fast only where it has no fast form (doubles by an exponent other than 2, 3, 4, -1, -2, 0.5).
    /// </summary>
    private bool PowPerLane(IOperation exponent, bool single)
    {
        double? e = exponent.ConstantValue is { HasValue: true, Value: { } c } ? System.Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (e is 0 or 1) return false;
        return !_fast || !single && e is not (2 or 3 or 4 or -1 or -2 or 0.5);
    }

    /// <summary>
    /// A body making calls once per lane keeps its vector loop only when it does at least <see cref="PerLaneWork"/>
    /// vector operations per call (SimdMode.Require keeps it regardless): a lighter body runs faster as written, its
    /// calls the same and no lanes moved through memory.
    /// </summary>
    private void GatePerLane(string text, bool require)
    {
        if (_perLaneCalls == 0 || require) return;
        int work = System.Math.Max(0, Operations(text) - _perLaneCalls);
        if (work >= PerLaneWork * _perLaneCalls) return;
        string names = string.Join(", ", _perLaneNames);
        throw new Unsupported($"{names} {(_perLaneNames.Count == 1 ? "has" : "have")} no vector form{(_perLaneFast ? " bit for bit" : " in .NET 10")}, and the rest of the body is too light for a vector loop calling {(_perLaneNames.Count == 1 ? "it" : "them")} once per lane to pay ({work} vector operations, {PerLaneWork} per call needed): "
            + (_perLaneFast ? "set FloatMode = FloatMode.Fast (vector forms within a few ulps), or SimdMode = SimdMode.Require" : "set SimdMode = SimdMode.Require to take the loop anyway"));
    }

    /// <summary>The vector operations of a loop's text: its calls of the vector types and its arithmetic operators.</summary>
    private static int Operations(string text)
    {
        int n = 0;
        for (int at = text.IndexOf(V, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf(V, at + V.Length, System.StringComparison.Ordinal))
            if (at + V.Length < text.Length && text[at + V.Length] == '.') n++;
        foreach (var op in new[] { " + ", " - ", " * ", " / ", " & ", " | ", " ^ " })
            for (int at = text.IndexOf(op, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf(op, at + op.Length, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
