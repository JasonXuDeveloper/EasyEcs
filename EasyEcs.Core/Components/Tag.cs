using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs.Core.Components;

/// <summary>
/// Component archetype tag using SIMD-optimized bitset.
/// Supports unlimited component types with inline 256-bit storage + overflow.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Tag : IEquatable<Tag>, IComparable<Tag>
{
    // Inline 256 bits using 2x Vector128 (covers first 256 component types)
    private Vector128<long> _bits0;  // Bits 0-127
    private Vector128<long> _bits1;  // Bits 128-255

    // Overflow for >256 components (allocated on demand)
    private Vector128<long>[] _overflow;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void SetBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 7;  // Divide by 128
        int bitOffset = bitIndex & 127;   // Modulo 128

        if (vectorIndex < 2)
        {
            // Fast path: inline vectors
            ref var vector = ref (vectorIndex == 0 ? ref _bits0 : ref _bits1);
            ref long longValue = ref Unsafe.As<Vector128<long>, long>(ref vector);
            Unsafe.Add(ref longValue, bitOffset >> 6) |= 1L << (bitOffset & 63);
        }
        else
        {
            // Overflow path
            EnsureOverflow(vectorIndex - 1);
            ref long longValue = ref Unsafe.As<Vector128<long>, long>(ref _overflow[vectorIndex - 2]);
            Unsafe.Add(ref longValue, bitOffset >> 6) |= 1L << (bitOffset & 63);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ClearBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 7;
        int bitOffset = bitIndex & 127;

        if (vectorIndex < 2)
        {
            ref var vector = ref (vectorIndex == 0 ? ref _bits0 : ref _bits1);
            ref long longValue = ref Unsafe.As<Vector128<long>, long>(ref vector);
            Unsafe.Add(ref longValue, bitOffset >> 6) &= ~(1L << (bitOffset & 63));
        }
        else if (_overflow != null && vectorIndex - 2 < _overflow.Length)
        {
            ref long longValue = ref Unsafe.As<Vector128<long>, long>(ref _overflow[vectorIndex - 2]);
            Unsafe.Add(ref longValue, bitOffset >> 6) &= ~(1L << (bitOffset & 63));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool HasBit(int bitIndex)
    {
        int vectorIndex = bitIndex >> 7;
        int bitOffset = bitIndex & 127;

        ref Vector128<long> vector = ref (vectorIndex == 0 ? ref _bits0 : ref _bits1);

        if (vectorIndex == 0)
            vector = ref _bits0;
        else if (vectorIndex == 1)
            vector = ref _bits1;
        else if (_overflow == null || vectorIndex - 2 >= _overflow.Length)
            return false;
        else
            vector = ref _overflow[vectorIndex - 2];

        ref long longValue = ref Unsafe.As<Vector128<long>, long>(ref vector);
        return (Unsafe.Add(ref longValue, bitOffset >> 6) & (1L << (bitOffset & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator &(Tag a, Tag b)
    {
        var result = new Tag
        {
            _bits0 = SimdOps.And(a._bits0, b._bits0),
            _bits1 = SimdOps.And(a._bits1, b._bits1)
        };

        if (a._overflow != null && b._overflow != null)
        {
            int minLen = Math.Min(a._overflow.Length, b._overflow.Length);
            result._overflow = new Vector128<long>[minLen];

            for (int i = 0; i < minLen; i++)
                result._overflow[i] = SimdOps.And(a._overflow[i], b._overflow[i]);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator |(Tag a, Tag b)
    {
        var result = new Tag
        {
            _bits0 = SimdOps.Or(a._bits0, b._bits0),
            _bits1 = SimdOps.Or(a._bits1, b._bits1)
        };

        if (a._overflow != null || b._overflow != null)
        {
            int maxLen = Math.Max(a._overflow?.Length ?? 0, b._overflow?.Length ?? 0);
            result._overflow = new Vector128<long>[maxLen];

            for (int i = 0; i < maxLen; i++)
            {
                var aVec = a._overflow != null && i < a._overflow.Length ? a._overflow[i] : Vector128<long>.Zero;
                var bVec = b._overflow != null && i < b._overflow.Length ? b._overflow[i] : Vector128<long>.Zero;
                result._overflow[i] = SimdOps.Or(aVec, bVec);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static Tag operator ^(Tag a, Tag b)
    {
        var result = new Tag
        {
            _bits0 = SimdOps.Xor(a._bits0, b._bits0),
            _bits1 = SimdOps.Xor(a._bits1, b._bits1)
        };

        if (a._overflow != null || b._overflow != null)
        {
            int maxLen = Math.Max(a._overflow?.Length ?? 0, b._overflow?.Length ?? 0);
            result._overflow = new Vector128<long>[maxLen];

            for (int i = 0; i < maxLen; i++)
            {
                var aVec = a._overflow != null && i < a._overflow.Length ? a._overflow[i] : Vector128<long>.Zero;
                var bVec = b._overflow != null && i < b._overflow.Length ? b._overflow[i] : Vector128<long>.Zero;
                result._overflow[i] = SimdOps.Xor(aVec, bVec);
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Tag operator ~(Tag a)
    {
        return new Tag
        {
            _bits0 = Vector128.OnesComplement(a._bits0),
            _bits1 = Vector128.OnesComplement(a._bits1),
            _overflow = a._overflow != null ? InvertOverflow(a._overflow) : null
        };
    }

    private static Vector128<long>[] InvertOverflow(Vector128<long>[] overflow)
    {
        var result = new Vector128<long>[overflow.Length];
        for (int i = 0; i < overflow.Length; i++)
            result[i] = Vector128.OnesComplement(overflow[i]);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool operator ==(Tag a, Tag b) => a.Equals(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool operator !=(Tag a, Tag b) => !a.Equals(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Equals(Tag other)
    {
        if (!SimdOps.AreEqual(_bits0, other._bits0) || !SimdOps.AreEqual(_bits1, other._bits1))
            return false;

        if (_overflow == null && other._overflow == null) return true;
        if (_overflow == null || other._overflow == null) return false;
        if (_overflow.Length != other._overflow.Length) return false;

        for (int i = 0; i < _overflow.Length; i++)
        {
            if (!SimdOps.AreEqual(_overflow[i], other._overflow[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object obj)
    {
        return obj is Tag tag && Equals(tag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        // Extract first int from vector for hash
        return Unsafe.As<Vector128<long>, int>(ref Unsafe.AsRef(in _bits0));
    }

    public override string ToString()
    {
        ref long bits0 = ref Unsafe.As<Vector128<long>, long>(ref Unsafe.AsRef(in _bits0));
        ref long bits1 = ref Unsafe.As<Vector128<long>, long>(ref Unsafe.AsRef(in _bits1));

        return $"Tag {Unsafe.Add(ref bits0, 0):x16} {Unsafe.Add(ref bits0, 1):x16} {Unsafe.Add(ref bits1, 0):x16} {Unsafe.Add(ref bits1, 1):x16}";
    }

    public int CompareTo(Tag other)
    {
        ref long thisBits = ref Unsafe.As<Vector128<long>, long>(ref Unsafe.AsRef(in _bits0));
        ref long otherBits = ref Unsafe.As<Vector128<long>, long>(ref Unsafe.AsRef(in other._bits0));

        for (int i = 0; i < 4; i++)
        {
            var cmp = Unsafe.Add(ref thisBits, i).CompareTo(Unsafe.Add(ref otherBits, i));
            if (cmp != 0) return cmp;
        }

        // Compare overflow if present
        int thisLen = _overflow?.Length ?? 0;
        int otherLen = other._overflow?.Length ?? 0;

        if (thisLen != otherLen)
            return thisLen.CompareTo(otherLen);

        for (int i = 0; i < thisLen; i++)
        {
            ref long thisOverflow = ref Unsafe.As<Vector128<long>, long>(ref _overflow[i]);
            ref long otherOverflow = ref Unsafe.As<Vector128<long>, long>(ref other._overflow[i]);

            for (int j = 0; j < 2; j++)
            {
                var cmp = Unsafe.Add(ref thisOverflow, j).CompareTo(Unsafe.Add(ref otherOverflow, j));
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EnsureOverflow(int requiredCount)
    {
        if (_overflow == null)
        {
            _overflow = new Vector128<long>[Math.Max(4, requiredCount)];
        }
        else if (_overflow.Length < requiredCount)
        {
            var newArray = new Vector128<long>[Math.Max(_overflow.Length * 2, requiredCount)];
            Array.Copy(_overflow, newArray, _overflow.Length);
            _overflow = newArray;
        }
    }
}
