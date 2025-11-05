using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Core.Components;

/// <summary>
/// Cross-platform SIMD operations with fallback support.
/// Automatically uses AVX2 (x86/x64), AdvSimd (ARM), or scalar fallback.
/// </summary>
internal static class SimdOps
{
    private static readonly bool UseAvx2 = Avx2.IsSupported;
    private static readonly bool UseAdvSimd = AdvSimd.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> And(Vector128<long> left, Vector128<long> right)
    {
        if (UseAvx2)
            return Avx2.And(left, right);
        if (UseAdvSimd)
            return AdvSimd.And(left, right);
        return Vector128.BitwiseAnd(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> Or(Vector128<long> left, Vector128<long> right)
    {
        if (UseAvx2)
            return Avx2.Or(left, right);
        if (UseAdvSimd)
            return AdvSimd.Or(left, right);
        return Vector128.BitwiseOr(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> Xor(Vector128<long> left, Vector128<long> right)
    {
        if (UseAvx2)
            return Avx2.Xor(left, right);
        if (UseAdvSimd)
            return AdvSimd.Xor(left, right);
        return Vector128.Xor(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<long> AndNot(Vector128<long> left, Vector128<long> right)
    {
        if (UseAvx2)
            return Avx2.AndNot(left, right);
        if (UseAdvSimd)
            return AdvSimd.BitwiseClear(right, left); // Note: ARM has reversed operands
        return Vector128.AndNot(left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AreEqual(Vector128<long> left, Vector128<long> right)
    {
        if (UseAvx2)
        {
            var cmp = Avx2.CompareEqual(left, right);
            return Avx2.MoveMask(cmp.AsByte()) == -1;
        }
        if (UseAdvSimd)
        {
            var cmp = AdvSimd.CompareEqual(left, right);
            return cmp.Equals(Vector128<long>.AllBitsSet);
        }
        return left.Equals(right);
    }
}
