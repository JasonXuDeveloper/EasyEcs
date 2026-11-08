using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// Lane functions of generated vector loops that .NET has no vector form of: the quotient of integer lanes (through
/// doubles, exact for lanes of 4 bytes) and, under FloatMode.Fast, Log10 and the hyperbolic functions of float lanes
/// (through double Log and Exp). The generator calls them; a [ForEach] method never does.
/// </summary>
/// <remarks>
/// The quotient of lanes of 4 bytes through doubles is the integer quotient, truncated toward zero: both operands are
/// exact doubles, and the rounded double quotient of x / y lies within |x / y| * 2^-53 &lt;= 2^-21 / |y| of the true
/// one, nearer than the next integer whenever x / y is not one (a fraction of y is at least 1 / |y| away from an
/// integer). The caller replaces a divisor of 0 (and -1 of int.MinValue) before the call: those lanes would throw in
/// the method, and their rows run the method itself.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static partial class LaneMath
{
    /// <summary>x / y per lane, truncated toward zero; no lane of <paramref name="y"/> is 0, none is -1 where x is int.MinValue.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<int> Quotient(Vector<int> x, Vector<int> y)
    {
        var lower = Vector.ConvertToDouble(Vector.WidenLower(x)) / Vector.ConvertToDouble(Vector.WidenLower(y));
        var upper = Vector.ConvertToDouble(Vector.WidenUpper(x)) / Vector.ConvertToDouble(Vector.WidenUpper(y));
        return Vector.Narrow(Vector.ConvertToInt64(lower), Vector.ConvertToInt64(upper));
    }

    /// <inheritdoc cref="Quotient(Vector{int}, Vector{int})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<int> Quotient(Vector512<int> x, Vector512<int> y)
    {
        var (xl, xu) = Vector512.Widen(x);
        var (yl, yu) = Vector512.Widen(y);
        var lower = Vector512.ConvertToDouble(xl) / Vector512.ConvertToDouble(yl);
        var upper = Vector512.ConvertToDouble(xu) / Vector512.ConvertToDouble(yu);
        return Vector512.Narrow(Vector512.ConvertToInt64(lower), Vector512.ConvertToInt64(upper));
    }

    /// <summary>x / y per lane; no lane of <paramref name="y"/> is 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<uint> Quotient(Vector<uint> x, Vector<uint> y)
    {
        var lower = Vector.ConvertToDouble(Vector.WidenLower(x)) / Vector.ConvertToDouble(Vector.WidenLower(y));
        var upper = Vector.ConvertToDouble(Vector.WidenUpper(x)) / Vector.ConvertToDouble(Vector.WidenUpper(y));
        return Vector.Narrow(Vector.ConvertToUInt64(lower), Vector.ConvertToUInt64(upper));
    }

    /// <inheritdoc cref="Quotient(Vector{uint}, Vector{uint})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<uint> Quotient(Vector512<uint> x, Vector512<uint> y)
    {
        var (xl, xu) = Vector512.Widen(x);
        var (yl, yu) = Vector512.Widen(y);
        var lower = Vector512.ConvertToDouble(xl) / Vector512.ConvertToDouble(yl);
        var upper = Vector512.ConvertToDouble(xu) / Vector512.ConvertToDouble(yu);
        return Vector512.Narrow(Vector512.ConvertToUInt64(lower), Vector512.ConvertToUInt64(upper));
    }

    // Vector128 (the rows of a cell method narrower than Vector<T>): with AVX, 4 int lanes widen into one Vector256 of
    // doubles (vcvtdq2pd), one division, and vcvttpd2dq truncates back; long-to-double conversions, which AVX2 lacks
    // (AVX-512 DQ has them), never run. Without AVX (Arm64 converts longs natively) the lanes go through longs. The
    // float functions widen the same way: 4 lanes are one Vector256 of doubles, one Exp or Log instead of two of 2.

    /// <inheritdoc cref="Quotient(Vector{int}, Vector{int})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Quotient(Vector128<int> x, Vector128<int> y)
    {
        if (Avx.IsSupported)
            return Avx.ConvertToVector128Int32WithTruncation(Avx.ConvertToVector256Double(x) / Avx.ConvertToVector256Double(y));
        var (xl, xu) = Vector128.Widen(x);
        var (yl, yu) = Vector128.Widen(y);
        var lower = Vector128.ConvertToDouble(xl) / Vector128.ConvertToDouble(yl);
        var upper = Vector128.ConvertToDouble(xu) / Vector128.ConvertToDouble(yu);
        return Vector128.Narrow(Vector128.ConvertToInt64(lower), Vector128.ConvertToInt64(upper));
    }

    /// <inheritdoc cref="Quotient(Vector{uint}, Vector{uint})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Quotient(Vector128<uint> x, Vector128<uint> y)
    {
        if (Avx.IsSupported)
        {
            // AVX converts signed lanes only: x - 2^31 as an int, converted, plus 2^31 is x exactly. The quotient
            // (non-negative) is truncated first, then shifted back down by 2^31 into the signed range to convert.
            var half = Vector256.Create(2147483648.0);
            var q = Avx.RoundToZero(Unsigned(x, half) / Unsigned(y, half)) - half;
            return Avx.ConvertToVector128Int32WithTruncation(q).AsUInt32() ^ Vector128.Create(0x80000000u);
        }
        var (xl, xu) = Vector128.Widen(x);
        var (yl, yu) = Vector128.Widen(y);
        var lower = Vector128.ConvertToDouble(xl) / Vector128.ConvertToDouble(yl);
        var upper = Vector128.ConvertToDouble(xu) / Vector128.ConvertToDouble(yu);
        return Vector128.Narrow(Vector128.ConvertToUInt64(lower), Vector128.ConvertToUInt64(upper));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Unsigned(Vector128<uint> x, Vector256<double> half) =>
        Avx.ConvertToVector256Double((x ^ Vector128.Create(0x80000000u)).AsInt32()) + half;

    // ---------------- FloatMode.Fast: Log10 and the hyperbolic functions of float lanes ----------------
    // Each computes in doubles through .NET's vector Exp (within a few double ulps), so e^x - e^-x near 0 loses about
    // ten of 53 bits, far below a float's 24; below 2^-12 the float result is x itself (sinh, tanh: x^3 / 6 is under
    // half an ulp), which also keeps -0. Above 10, tanh is 1 in float (1 - 4e-9 rounds to it) and e^2x would overflow.

    /// <summary>MathF.Sinh per lane, within an ulp or two (FloatMode.Fast).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Sinh(Vector<float> x)
    {
        Vector.Widen(x, out var lower, out var upper);
        var value = Vector.Narrow(SinhOf(lower), SinhOf(upper));
        return Vector.ConditionalSelect(Vector.LessThan(Vector.Abs(x), new Vector<float>(Tiny)), x, value);
    }

    /// <inheritdoc cref="Sinh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<float> Sinh(Vector512<float> x)
    {
        var (lower, upper) = Vector512.Widen(x);
        var value = Vector512.Narrow(SinhOf(lower), SinhOf(upper));
        return Vector512.ConditionalSelect(Vector512.LessThan(Vector512.Abs(x), Vector512.Create(Tiny)), x, value);
    }

    /// <inheritdoc cref="Sinh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Sinh(Vector128<float> x)
    {
        Vector128<float> value;
        if (Avx.IsSupported)
            value = Avx.ConvertToVector128Single(SinhOf(Avx.ConvertToVector256Double(x)));
        else
        {
            var (lower, upper) = Vector128.Widen(x);
            value = Vector128.Narrow(SinhOf(lower), SinhOf(upper));
        }
        return Vector128.ConditionalSelect(Vector128.LessThan(Vector128.Abs(x), Vector128.Create(Tiny)), x, value);
    }

    /// <summary>MathF.Cosh per lane, within an ulp or two (FloatMode.Fast).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Cosh(Vector<float> x)
    {
        Vector.Widen(x, out var lower, out var upper);
        return Vector.Narrow(CoshOf(lower), CoshOf(upper));
    }

    /// <inheritdoc cref="Cosh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<float> Cosh(Vector512<float> x)
    {
        var (lower, upper) = Vector512.Widen(x);
        return Vector512.Narrow(CoshOf(lower), CoshOf(upper));
    }

    /// <inheritdoc cref="Cosh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Cosh(Vector128<float> x)
    {
        if (Avx.IsSupported) return Avx.ConvertToVector128Single(CoshOf(Avx.ConvertToVector256Double(x)));
        var (lower, upper) = Vector128.Widen(x);
        return Vector128.Narrow(CoshOf(lower), CoshOf(upper));
    }

    /// <summary>MathF.Tanh per lane, within an ulp or two (FloatMode.Fast).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Tanh(Vector<float> x)
    {
        Vector.Widen(x, out var lower, out var upper);
        var value = Vector.Narrow(TanhOf(lower), TanhOf(upper));
        var abs = Vector.Abs(x);
        value = Vector.ConditionalSelect(Vector.GreaterThan(abs, new Vector<float>(10f)), Vector.CopySign(Vector<float>.One, x), value);
        return Vector.ConditionalSelect(Vector.LessThan(abs, new Vector<float>(Tiny)), x, value);
    }

    /// <inheritdoc cref="Tanh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<float> Tanh(Vector512<float> x)
    {
        var (lower, upper) = Vector512.Widen(x);
        var value = Vector512.Narrow(TanhOf(lower), TanhOf(upper));
        var abs = Vector512.Abs(x);
        value = Vector512.ConditionalSelect(Vector512.GreaterThan(abs, Vector512.Create(10f)), Vector512.CopySign(Vector512<float>.One, x), value);
        return Vector512.ConditionalSelect(Vector512.LessThan(abs, Vector512.Create(Tiny)), x, value);
    }

    /// <inheritdoc cref="Tanh(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Tanh(Vector128<float> x)
    {
        Vector128<float> value;
        if (Avx.IsSupported)
            value = Avx.ConvertToVector128Single(TanhOf(Avx.ConvertToVector256Double(x)));
        else
        {
            var (lower, upper) = Vector128.Widen(x);
            value = Vector128.Narrow(TanhOf(lower), TanhOf(upper));
        }
        var abs = Vector128.Abs(x);
        value = Vector128.ConditionalSelect(Vector128.GreaterThan(abs, Vector128.Create(10f)), Vector128.CopySign(Vector128<float>.One, x), value);
        return Vector128.ConditionalSelect(Vector128.LessThan(abs, Vector128.Create(Tiny)), x, value);
    }

    /// <summary>MathF.Log10 per lane, within an ulp (FloatMode.Fast): ln x in doubles times log10(e).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Log10(Vector<float> x)
    {
        Vector.Widen(x, out var lower, out var upper);
        var scale = new Vector<double>(0.43429448190325182765);
        return Vector.Narrow(Vector.Log(lower) * scale, Vector.Log(upper) * scale);
    }

    /// <inheritdoc cref="Log10(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<float> Log10(Vector512<float> x)
    {
        var (lower, upper) = Vector512.Widen(x);
        var scale = Vector512.Create(0.43429448190325182765);
        return Vector512.Narrow(Vector512.Log(lower) * scale, Vector512.Log(upper) * scale);
    }

    /// <inheritdoc cref="Log10(Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Log10(Vector128<float> x)
    {
        if (Avx.IsSupported)
            return Avx.ConvertToVector128Single(Vector256.Log(Avx.ConvertToVector256Double(x)) * Vector256.Create(0.43429448190325182765));
        var (lower, upper) = Vector128.Widen(x);
        var scale = Vector128.Create(0.43429448190325182765);
        return Vector128.Narrow(Vector128.Log(lower) * scale, Vector128.Log(upper) * scale);
    }

    /// <summary>
    /// MathF.Pow per lane, within an ulp (FloatMode.Fast): e^(y ln x) in doubles, rounded once to a float. The caller
    /// passes only finite x &gt; 0 and finite y (pow's special cases run the method). The double's relative error is a
    /// few times 2^-52 even where |y ln x| reaches the float range's 88 (2^-52 * 88 is far below a float's 2^-24), so
    /// the float result is the rounded true value or its neighbor; results past the float range round to infinity or
    /// to a subnormal (or 0) as pow's.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<float> Pow(Vector<float> x, Vector<float> y)
    {
        Vector.Widen(x, out var xl, out var xu);
        Vector.Widen(y, out var yl, out var yu);
        return Vector.Narrow(Vector.Exp(yl * Vector.Log(xl)), Vector.Exp(yu * Vector.Log(xu)));
    }

    /// <inheritdoc cref="Pow(Vector{float}, Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<float> Pow(Vector512<float> x, Vector512<float> y)
    {
        var (xl, xu) = Vector512.Widen(x);
        var (yl, yu) = Vector512.Widen(y);
        return Vector512.Narrow(Vector512.Exp(yl * Vector512.Log(xl)), Vector512.Exp(yu * Vector512.Log(xu)));
    }

    /// <inheritdoc cref="Pow(Vector{float}, Vector{float})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Pow(Vector128<float> x, Vector128<float> y)
    {
        if (Avx.IsSupported)
            return Avx.ConvertToVector128Single(Vector256.Exp(Avx.ConvertToVector256Double(y) * Vector256.Log(Avx.ConvertToVector256Double(x))));
        var (xl, xu) = Vector128.Widen(x);
        var (yl, yu) = Vector128.Widen(y);
        return Vector128.Narrow(Vector128.Exp(yl * Vector128.Log(xl)), Vector128.Exp(yu * Vector128.Log(xu)));
    }

    private const float Tiny = 1f / 4096;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<double> SinhOf(Vector<double> x)
    {
        var e = Vector.Exp(x);
        return (e - Vector<double>.One / e) * new Vector<double>(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> SinhOf(Vector512<double> x)
    {
        var e = Vector512.Exp(x);
        return (e - Vector512<double>.One / e) * Vector512.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<double> CoshOf(Vector<double> x)
    {
        var e = Vector.Exp(x);
        return (e + Vector<double>.One / e) * new Vector<double>(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> CoshOf(Vector512<double> x)
    {
        var e = Vector512.Exp(x);
        return (e + Vector512<double>.One / e) * Vector512.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<double> TanhOf(Vector<double> x)
    {
        var e = Vector.Exp(x + x);
        return (e - Vector<double>.One) / (e + Vector<double>.One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> TanhOf(Vector512<double> x)
    {
        var e = Vector512.Exp(x + x);
        return (e - Vector512<double>.One) / (e + Vector512<double>.One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> SinhOf(Vector128<double> x)
    {
        var e = Vector128.Exp(x);
        return (e - Vector128<double>.One / e) * Vector128.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> CoshOf(Vector128<double> x)
    {
        var e = Vector128.Exp(x);
        return (e + Vector128<double>.One / e) * Vector128.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> TanhOf(Vector128<double> x)
    {
        var e = Vector128.Exp(x + x);
        return (e - Vector128<double>.One) / (e + Vector128<double>.One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> SinhOf(Vector256<double> x)
    {
        var e = Vector256.Exp(x);
        return (e - Vector256<double>.One / e) * Vector256.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> CoshOf(Vector256<double> x)
    {
        var e = Vector256.Exp(x);
        return (e + Vector256<double>.One / e) * Vector256.Create(0.5);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> TanhOf(Vector256<double> x)
    {
        var e = Vector256.Exp(x + x);
        return (e - Vector256<double>.One) / (e + Vector256<double>.One);
    }
}
