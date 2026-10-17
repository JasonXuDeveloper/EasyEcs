using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// Component archetype tag using SIMD-optimized bitset.
/// Supports unlimited component types with inline 256-bit storage + overflow.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Tag : IEquatable<Tag>
{
    // Inline 256 bits (covers first 256 component types)
    private Vector256<long> _bits;

    // Overflow for >256 components (allocated on demand)
    private Vector256<long>[] _overflow;

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
    public readonly bool ContainsAll(in Tag query)
    {
        // query & ~this == 0 means this contains all bits of query
        // Vector256.AndNot(a, b) = a & ~b → maps to x86 VPANDN, ARM BIC
        if (!IsVectorZero(Vector256.AndNot(query._bits, _bits)))
            return false;

        int queryLen = query._overflow?.Length ?? 0;
        if (queryLen == 0) return true;

        int thisLen = _overflow?.Length ?? 0;
        for (int i = 0; i < queryLen; i++)
        {
            var thisVec = i < thisLen ? _overflow![i] : Vector256<long>.Zero;
            if (!IsVectorZero(Vector256.AndNot(query._overflow![i], thisVec)))
                return false;
        }

        return true;
    }

    /// <summary>True when this tag and <paramref name="other"/> share at least one bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly bool Intersects(in Tag other)
    {
        if (!IsVectorZero(Vector256.BitwiseAnd(_bits, other._bits)))
            return true;

        int len = Math.Min(_overflow?.Length ?? 0, other._overflow?.Length ?? 0);
        for (int i = 0; i < len; i++)
        {
            if (!IsVectorZero(Vector256.BitwiseAnd(_overflow![i], other._overflow![i])))
                return true;
        }

        return false;
    }

    /// <summary>True when no bit is set.</summary>
    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!IsVectorZero(_bits)) return false;
            if (_overflow != null)
                for (int i = 0; i < _overflow.Length; i++)
                    if (!IsVectorZero(_overflow[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// Copy with its own overflow storage (a plain struct copy shares the overflow array, so mutating the copy
    /// beyond bit 255 would also mutate the original).
    /// </summary>
    public readonly Tag Clone()
    {
        var copy = this;
        if (_overflow != null)
            copy._overflow = (Vector256<long>[])_overflow.Clone();
        return copy;
    }

    /// <summary>True when bits past 255 have storage.</summary>
    public readonly bool HasOverflow => _overflow != null;

    /// <summary>
    /// Copy that can take bits up to <paramref name="maxBit"/> without allocating: its overflow (only when needed) is
    /// <paramref name="scratch"/>, cleared and grown on demand. Valid until the caller reuses the scratch buffer.
    /// </summary>
    public readonly Tag CopyWithScratch(int maxBit, ref Vector256<long>[] scratch)
    {
        var copy = this;
        int needed = Math.Max(_overflow?.Length ?? 0, maxBit >> 8);
        if (needed == 0) return copy;
        if (scratch == null || scratch.Length < needed) scratch = new Vector256<long>[Math.Max(4, needed)];
        Array.Clear(scratch);
        _overflow?.CopyTo(scratch, 0);
        copy._overflow = scratch;
        return copy;
    }

    /// <summary>Indices of the set bits in ascending order (cold path: archetype creation).</summary>
    public readonly ushort[] ToBitArray()
    {
        int capacity = 256 * (1 + (_overflow?.Length ?? 0));
        var bits = new System.Collections.Generic.List<ushort>();
        for (int i = 0; i < capacity; i++)
            if (HasBit(i)) bits.Add((ushort)i);
        return bits.ToArray();
    }

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
        // x86 AVX: Single TestZ instruction (~1 cycle)
        if (Avx.IsSupported)
        {
            return Avx.TestZ(vec, vec);
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