using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Core.Components;

/// <summary>
/// Component archetype tag using SIMD-optimized bitset.
/// Supports unlimited component types with inline 256-bit storage + overflow.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Tag : IEquatable<Tag>, IComparable<Tag>
{
    // Inline 256 bits (covers first 256 component types)
    private Vector256<long> _bits;

    // Overflow for >256 components (allocated on demand)
    private Vector256<long>[] _overflow;

    /// <summary>
    /// Static empty tag to avoid allocations when creating entities with no components.
    /// </summary>
    internal static readonly Tag Empty = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void SetBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 8; // Divide by 256
        int bitOffset = bitIndex & 255; // Modulo 256

        if (vectorIndex == 0)
        {
            // Fast path: inline vector
            ref long longValue = ref Unsafe.As<Vector256<long>, long>(ref _bits);
            Unsafe.Add(ref longValue, bitOffset >> 6) |= 1L << (bitOffset & 63);
        }
        else
        {
            // Overflow path
            EnsureOverflow(vectorIndex);
            ref long longValue = ref Unsafe.As<Vector256<long>, long>(ref _overflow[vectorIndex - 1]);
            Unsafe.Add(ref longValue, bitOffset >> 6) |= 1L << (bitOffset & 63);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ClearBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 8;
        int bitOffset = bitIndex & 255;

        if (vectorIndex == 0)
        {
            ref long longValue = ref Unsafe.As<Vector256<long>, long>(ref _bits);
            Unsafe.Add(ref longValue, bitOffset >> 6) &= ~(1L << (bitOffset & 63));
        }
        else if (_overflow != null && vectorIndex - 1 < _overflow.Length)
        {
            ref long longValue = ref Unsafe.As<Vector256<long>, long>(ref _overflow[vectorIndex - 1]);
            Unsafe.Add(ref longValue, bitOffset >> 6) &= ~(1L << (bitOffset & 63));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly bool HasBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 8;
        int bitOffset = bitIndex & 255;

        if (vectorIndex == 0)
        {
            ref long longValue0 = ref Unsafe.As<Vector256<long>, long>(ref Unsafe.AsRef(in _bits));
            return (Unsafe.Add(ref longValue0, bitOffset >> 6) & (1L << (bitOffset & 63))) != 0;
        }

        if (_overflow == null || vectorIndex - 1 >= _overflow.Length)
        {
            return false;
        }

        ref long longValue = ref Unsafe.As<Vector256<long>, long>(ref _overflow[vectorIndex - 1]);
        return (Unsafe.Add(ref longValue, bitOffset >> 6) & (1L << (bitOffset & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator &(in Tag a, in Tag b)
    {
        var result = new Tag
        {
            _bits = Vector256.BitwiseAnd(a._bits, b._bits)
        };

        if (a._overflow != null && b._overflow != null)
        {
            int minLen = Math.Min(a._overflow.Length, b._overflow.Length);
            result._overflow = new Vector256<long>[minLen];

            for (int i = 0; i < minLen; i++)
                result._overflow[i] = Vector256.BitwiseAnd(a._overflow[i], b._overflow[i]);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator |(in Tag a, in Tag b)
    {
        var result = new Tag
        {
            _bits = Vector256.BitwiseOr(a._bits, b._bits)
        };

        if (a._overflow != null || b._overflow != null)
        {
            int maxLen = Math.Max(a._overflow?.Length ?? 0, b._overflow?.Length ?? 0);
            result._overflow = new Vector256<long>[maxLen];

            for (int i = 0; i < maxLen; i++)
            {
                var aVec = a._overflow != null && i < a._overflow.Length ? a._overflow[i] : Vector256<long>.Zero;
                var bVec = b._overflow != null && i < b._overflow.Length ? b._overflow[i] : Vector256<long>.Zero;
                result._overflow[i] = Vector256.BitwiseOr(aVec, bVec);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator ^(in Tag a, in Tag b)
    {
        var result = new Tag
        {
            _bits = Vector256.Xor(a._bits, b._bits)
        };

        if (a._overflow != null || b._overflow != null)
        {
            int maxLen = Math.Max(a._overflow?.Length ?? 0, b._overflow?.Length ?? 0);
            result._overflow = new Vector256<long>[maxLen];

            for (int i = 0; i < maxLen; i++)
            {
                var aVec = a._overflow != null && i < a._overflow.Length ? a._overflow[i] : Vector256<long>.Zero;
                var bVec = b._overflow != null && i < b._overflow.Length ? b._overflow[i] : Vector256<long>.Zero;
                result._overflow[i] = Vector256.Xor(aVec, bVec);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Tag operator ~(in Tag a)
    {
        return new Tag
        {
            _bits = Vector256.OnesComplement(a._bits),
            _overflow = a._overflow != null ? InvertOverflow(a._overflow) : null
        };
    }

    private static Vector256<long>[] InvertOverflow(Vector256<long>[] overflow)
    {
        var result = new Vector256<long>[overflow.Length];
        for (int i = 0; i < overflow.Length; i++)
            result[i] = Vector256.OnesComplement(overflow[i]);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool operator ==(in Tag a, in Tag b) => a.Equals(in b);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool operator !=(in Tag a, in Tag b) => !a.Equals(in b);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly bool Equals(in Tag other)
    {
        // XOR-based equality: a == b iff (a ^ b) == 0
        var xor = Vector256.Xor(_bits, other._bits);

        // Fast zero check using platform-specific intrinsics
        if (!IsVectorZero(xor))
            return false;

        // Handle overflow (rare case - >256 components)
        int thisLen = _overflow?.Length ?? 0;
        int otherLen = other._overflow?.Length ?? 0;
        int maxLen = Math.Max(thisLen, otherLen);

        for (int i = 0; i < maxLen; i++)
        {
            var thisVec = i < thisLen ? _overflow![i] : Vector256<long>.Zero;
            var otherVec = i < otherLen ? other._overflow![i] : Vector256<long>.Zero;

            if (!IsVectorZero(Vector256.Xor(thisVec, otherVec)))
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVectorZero(Vector256<long> vec)
    {
        // x86 AVX2: Single TestZ instruction (~1 cycle)
        if (Avx2.IsSupported)
        {
            return Avx2.TestZ(vec, vec);
        }

        // ARM NEON: Vector256 is emulated on ARM, so work with 128-bit halves (~2 cycles)
        if (AdvSimd.Arm64.IsSupported)
        {
            // Split into two 128-bit vectors and OR them together
            var lower = vec.GetLower();
            var upper = vec.GetUpper();
            var combined = AdvSimd.Or(lower, upper);

            // OR both lanes using direct memory access (faster than GetElement)
            ref long combinedRef = ref Unsafe.As<Vector128<long>, long>(ref combined);
            return (combinedRef | Unsafe.Add(ref combinedRef, 1)) == 0;
        }

        // Fallback: OR all elements and check if zero (~4 cycles)
        ref long longRef = ref Unsafe.As<Vector256<long>, long>(ref vec);
        return (longRef | Unsafe.Add(ref longRef, 1) |
                Unsafe.Add(ref longRef, 2) | Unsafe.Add(ref longRef, 3)) == 0;
    }

    // IEquatable<Tag> implementation for interface compatibility
    bool IEquatable<Tag>.Equals(Tag other) => Equals(in other);

    public readonly override bool Equals(object obj)
    {
        return obj is Tag tag && Equals(in tag);
    }

    public readonly override int GetHashCode()
    {
        // Use SIMD to quickly fold Vector256 into a hash
        // XOR upper and lower halves
        var lower = _bits.GetLower();
        var upper = _bits.GetUpper();
        var combined = Vector128.Xor(lower, upper);

        // Extract both longs using Unsafe (faster than GetElement)
        ref long combinedRef = ref Unsafe.As<Vector128<long>, long>(ref combined);
        long hash = combinedRef ^ Unsafe.Add(ref combinedRef, 1);

        // Include overflow in hash (rare case - >256 components)
        if (_overflow != null)
        {
            for (int i = 0; i < _overflow.Length; i++)
            {
                lower = _overflow[i].GetLower();
                upper = _overflow[i].GetUpper();
                combined = Vector128.Xor(lower, upper);
                combinedRef = ref Unsafe.As<Vector128<long>, long>(ref combined);
                hash ^= combinedRef ^ Unsafe.Add(ref combinedRef, 1);
            }
        }

        // Fold the 64-bit hash into 32 bits
        return (int)(hash ^ (hash >> 32));
    }

    public readonly override string ToString()
    {
        ref long bits = ref Unsafe.As<Vector256<long>, long>(ref Unsafe.AsRef(in _bits));

        return
            $"Tag {Unsafe.Add(ref bits, 0):x16} {Unsafe.Add(ref bits, 1):x16} {Unsafe.Add(ref bits, 2):x16} {Unsafe.Add(ref bits, 3):x16}";
    }

    public readonly int CompareTo(in Tag other)
    {
        // Fast path: Use SIMD to check equality first
        var xor = Vector256.Xor(_bits, other._bits);
        if (IsVectorZero(xor))
        {
            // Inline bits are equal, check overflow
            int thisLen = _overflow?.Length ?? 0;
            int otherLen = other._overflow?.Length ?? 0;

            if (thisLen != otherLen)
                return thisLen.CompareTo(otherLen);

            // Check overflow vectors
            for (int i = 0; i < thisLen; i++)
            {
                xor = Vector256.Xor(_overflow![i], other._overflow![i]);
                if (!IsVectorZero(xor))
                {
                    // Use XOR to find first differing long
                    ref long xorRef = ref Unsafe.As<Vector256<long>, long>(ref xor);
                    ref long thisRef = ref Unsafe.As<Vector256<long>, long>(ref _overflow[i]);
                    ref long otherRef = ref Unsafe.As<Vector256<long>, long>(ref Unsafe.AsRef(in other._overflow[i]));

                    for (int j = 0; j < 4; j++)
                    {
                        if (Unsafe.Add(ref xorRef, j) != 0)
                            return Unsafe.Add(ref thisRef, j).CompareTo(Unsafe.Add(ref otherRef, j));
                    }
                }
            }

            return 0; // Fully equal
        }

        // Not equal - use XOR to find first differing long in inline storage
        ref long xorRef2 = ref Unsafe.As<Vector256<long>, long>(ref xor);
        ref long thisRef2 = ref Unsafe.As<Vector256<long>, long>(ref Unsafe.AsRef(in _bits));
        ref long otherRef2 = ref Unsafe.As<Vector256<long>, long>(ref Unsafe.AsRef(in other._bits));

        for (int i = 0; i < 4; i++)
        {
            if (Unsafe.Add(ref xorRef2, i) != 0)
                return Unsafe.Add(ref thisRef2, i).CompareTo(Unsafe.Add(ref otherRef2, i));
        }

        return 0;
    }

    // IComparable<Tag> implementation for interface compatibility
    int IComparable<Tag>.CompareTo(Tag other) => CompareTo(in other);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EnsureOverflow(int requiredCount)
    {
        if (_overflow == null)
        {
            _overflow = new Vector256<long>[Math.Max(4, requiredCount)];
        }
        else if (_overflow.Length < requiredCount)
        {
            var newArray = new Vector256<long>[Math.Max(_overflow.Length * 2, requiredCount)];
            Array.Copy(_overflow, newArray, _overflow.Length);
            _overflow = newArray;
        }
    }
}