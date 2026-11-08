using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace EasyEcs;

/// <summary>
/// <see cref="EntityRandom"/> draws of generated vector loops: the numbers of a vector's rows at once, each lane the bits
/// of the scalar draw for the row's entity (the same four rounds of SplitMix64's finalizer, in 8-byte lanes). The
/// generator calls them, with the vector type it loops over as <c>TVector</c>; a [ForEach] method never does.
/// </summary>
/// <remarks>
/// The ids are the run's entity rows read as 8-byte lanes: an <see cref="EntityId"/> holds its version, then its
/// index, so the draw's id (the index in the low half) is each lane with its halves swapped. The last rows of a run
/// (a vector the run does not fill) are copied first, so no read passes the run's rows. Float, Range of ints and
/// Chance narrow the two vectors of 8-byte lanes a vector of 4-byte rows takes into one; every result is the scalar
/// draw's exactly (integer operations, and a float of 24 bits converted and scaled by a power of two).
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RandomLanes
{
    private const ulong Mix1 = 0xBF58476D1CE4E5B9, Mix2 = 0x94D049BB133111EB;

    /// <summary>
    /// Whether a <c>Vector&lt;T&gt;</c> loop draws as lanes on this hardware: vectors of 256 bits or more. A draw is six
    /// 8-byte multiplies per lane, which only AVX-512 has as one instruction; at 128 bits (SSE, NEON) two lanes of
    /// emulated multiplies lose to the scalar draw, so a generated loop leaves its rows to the method.
    /// </summary>
    public static bool IsAccelerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector.IsHardwareAccelerated && Vector<byte>.Count >= 32;
    }

    /// <summary>Whether a <c>Vector512&lt;T&gt;</c> loop draws as lanes on this hardware.</summary>
    public static bool IsAccelerated512
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector512.IsHardwareAccelerated;
    }

    /// <summary>Floats in [0, 1) of the rows <paramref name="row"/> on: <see cref="EntityRandom.Float(EntityId, uint)"/> per lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Float<TVector>(in EntityRandom random, in EntitySpan entities, nint row, uint stream) =>
        Float<TVector>(random.Seed, random.Tick, entities, row, stream);

    /// <summary>Floats in [0, 1) of the rows <paramref name="row"/> on: <see cref="EntityRandom.Float(ulong, EntityId, ulong, uint)"/> per lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Float<TVector>(ulong seed, ulong tick, in EntitySpan entities, nint row, uint stream)
    {
        ulong mixed = Mix(seed);
        Sixteen copy;
        if (typeof(TVector) == typeof(Vector512<float>))
            return Unsafe.BitCast<Vector512<float>, TVector>(Float512(ref Ids(entities, row, 16, out copy), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector256<float>) || typeof(TVector) == typeof(Vector<float>) && Vector<float>.Count == 8)
            return Unsafe.BitCast<Vector256<float>, TVector>(Float256(ref Ids(entities, row, 8, out copy), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector128<float>) || typeof(TVector) == typeof(Vector<float>) && Vector<float>.Count == 4)
            return Unsafe.BitCast<Vector128<float>, TVector>(Float128(ref Ids(entities, row, 4, out copy), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector<float>))
            return Unsafe.BitCast<Vector512<float>, TVector>(Float512(ref Ids(entities, row, 16, out copy), mixed, tick, stream));
        throw new NotSupportedException();
    }

    /// <summary>
    /// Ints in [<paramref name="min"/>, <paramref name="max"/>) of the rows <paramref name="row"/> on:
    /// <see cref="EntityRandom.Range(EntityId, uint, int, int)"/> per lane. The caller checks <paramref name="max"/> &gt;=
    /// <paramref name="min"/> (else the rows run the method, which throws).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Range<TVector>(in EntityRandom random, in EntitySpan entities, nint row, uint stream, int min, int max) =>
        Range<TVector>(random.Seed, random.Tick, entities, row, stream, min, max);

    /// <summary>
    /// Ints in [<paramref name="min"/>, <paramref name="max"/>) of the rows <paramref name="row"/> on:
    /// <see cref="EntityRandom.Range(ulong, EntityId, ulong, uint, int, int)"/> per lane; <paramref name="max"/> &gt;= <paramref name="min"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Range<TVector>(ulong seed, ulong tick, in EntitySpan entities, nint row, uint stream, int min, int max)
    {
        ulong mixed = Mix(seed), range = (ulong)((long)max - min);
        Sixteen copy;
        if (typeof(TVector) == typeof(Vector512<int>))
            return Unsafe.BitCast<Vector512<int>, TVector>(Range512(ref Ids(entities, row, 16, out copy), mixed, tick, stream, range, min));
        if (typeof(TVector) == typeof(Vector256<int>) || typeof(TVector) == typeof(Vector<int>) && Vector<int>.Count == 8)
            return Unsafe.BitCast<Vector256<int>, TVector>(Range256(ref Ids(entities, row, 8, out copy), mixed, tick, stream, range, min));
        if (typeof(TVector) == typeof(Vector128<int>) || typeof(TVector) == typeof(Vector<int>) && Vector<int>.Count == 4)
            return Unsafe.BitCast<Vector128<int>, TVector>(Range128(ref Ids(entities, row, 4, out copy), mixed, tick, stream, range, min));
        if (typeof(TVector) == typeof(Vector<int>))
            return Unsafe.BitCast<Vector512<int>, TVector>(Range512(ref Ids(entities, row, 16, out copy), mixed, tick, stream, range, min));
        throw new NotSupportedException();
    }

    /// <summary>64 random bits of the rows <paramref name="row"/> on (8-byte lanes): <see cref="EntityRandom.Bits(EntityId, uint)"/> per lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Bits<TVector>(in EntityRandom random, in EntitySpan entities, nint row, uint stream) =>
        Bits<TVector>(random.Seed, random.Tick, entities, row, stream);

    /// <summary>64 random bits of the rows <paramref name="row"/> on (8-byte lanes): <see cref="EntityRandom.Bits(ulong, EntityId, ulong, uint)"/> per lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TVector Bits<TVector>(ulong seed, ulong tick, in EntitySpan entities, nint row, uint stream)
    {
        ulong mixed = Mix(seed);
        Sixteen copy;
        if (typeof(TVector) == typeof(Vector512<ulong>))
            return Unsafe.BitCast<Vector512<ulong>, TVector>(Hash(Vector512.LoadUnsafe(ref Ids(entities, row, 8, out copy)), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector256<ulong>) || typeof(TVector) == typeof(Vector<ulong>) && Vector<ulong>.Count == 4)
            return Unsafe.BitCast<Vector256<ulong>, TVector>(Hash(Vector256.LoadUnsafe(ref Ids(entities, row, 4, out copy)), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector128<ulong>) || typeof(TVector) == typeof(Vector<ulong>) && Vector<ulong>.Count == 2)
            return Unsafe.BitCast<Vector128<ulong>, TVector>(Hash(Vector128.LoadUnsafe(ref Ids(entities, row, 2, out copy)), mixed, tick, stream));
        if (typeof(TVector) == typeof(Vector<ulong>))
            return Unsafe.BitCast<Vector512<ulong>, TVector>(Hash(Vector512.LoadUnsafe(ref Ids(entities, row, 8, out copy)), mixed, tick, stream));
        throw new NotSupportedException();
    }

    /// <summary>
    /// The run's entity rows from <paramref name="row"/> as 8-byte lanes: in place when the <paramref name="count"/> a
    /// vector takes lie inside the run, else copied into <paramref name="copy"/> (the last rows of a run, which a tail
    /// copy runs as a vector): no read passes the run's rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong Ids(in EntitySpan entities, nint row, int count, [UnscopedRef] out Sixteen copy)
    {
        var slots = entities.Slots;
        Unsafe.SkipInit(out copy);
        if ((nuint)row + (nuint)count <= (nuint)slots.Length)
            return ref Unsafe.As<EntityId, ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(slots), row));
        return ref Partial(slots, row, ref copy);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ref ulong Partial(ReadOnlySpan<EntityId> slots, nint row, ref Sixteen copy)
    {
        Span<ulong> lanes = copy;
        lanes.Clear();
        if ((nuint)row < (nuint)slots.Length)
            MemoryMarshal.Cast<EntityId, ulong>(slots.Slice((int)row, Math.Min(16, slots.Length - (int)row))).CopyTo(lanes);
        return ref copy[0];
    }

    /// <summary>The rows' copy when fewer than 16 of them are left in the run.</summary>
    [InlineArray(16)]
    private struct Sixteen
    {
        private ulong _element;
    }

    // The scalar draw's float: the top 24 bits, exact as an int and as a float, times 2^-24 (a power of two: exact).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<float> Float512(ref ulong ids, ulong mixed, ulong tick, uint stream) =>
        Vector512.ConvertToSingle(Vector512.AsInt32(Vector512.Narrow(
            Vector512.ShiftRightLogical(Hash(Vector512.LoadUnsafe(ref ids), mixed, tick, stream), 40),
            Vector512.ShiftRightLogical(Hash(Vector512.LoadUnsafe(ref ids, 8), mixed, tick, stream), 40)))) * (1f / (1 << 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Float256(ref ulong ids, ulong mixed, ulong tick, uint stream) =>
        Vector256.ConvertToSingle(Vector256.AsInt32(Vector256.Narrow(
            Vector256.ShiftRightLogical(Hash(Vector256.LoadUnsafe(ref ids), mixed, tick, stream), 40),
            Vector256.ShiftRightLogical(Hash(Vector256.LoadUnsafe(ref ids, 4), mixed, tick, stream), 40)))) * (1f / (1 << 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Float128(ref ulong ids, ulong mixed, ulong tick, uint stream) =>
        Vector128.ConvertToSingle(Vector128.AsInt32(Vector128.Narrow(
            Vector128.ShiftRightLogical(Hash(Vector128.LoadUnsafe(ref ids), mixed, tick, stream), 40),
            Vector128.ShiftRightLogical(Hash(Vector128.LoadUnsafe(ref ids, 2), mixed, tick, stream), 40)))) * (1f / (1 << 24));

    // The scalar draw's int: min + the high half of bits × range, range below 2^32, from two products of 32-bit halves
    // (bits = hi·2^32 + lo: hi·range + (lo·range >> 32) is below 2^64, and its high half is the product's); the sum
    // with min wraps in 32 bits as the scalar's long sum does when cast to int.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<int> Range512(ref ulong ids, ulong mixed, ulong tick, uint stream, ulong range, int min) =>
        Vector512.AsInt32(Vector512.Narrow(High(Hash(Vector512.LoadUnsafe(ref ids), mixed, tick, stream), range),
            High(Hash(Vector512.LoadUnsafe(ref ids, 8), mixed, tick, stream), range))) + Vector512.Create(min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Range256(ref ulong ids, ulong mixed, ulong tick, uint stream, ulong range, int min) =>
        Vector256.AsInt32(Vector256.Narrow(High(Hash(Vector256.LoadUnsafe(ref ids), mixed, tick, stream), range),
            High(Hash(Vector256.LoadUnsafe(ref ids, 4), mixed, tick, stream), range))) + Vector256.Create(min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Range128(ref ulong ids, ulong mixed, ulong tick, uint stream, ulong range, int min) =>
        Vector128.AsInt32(Vector128.Narrow(High(Hash(Vector128.LoadUnsafe(ref ids), mixed, tick, stream), range),
            High(Hash(Vector128.LoadUnsafe(ref ids, 2), mixed, tick, stream), range))) + Vector128.Create(min);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> High(Vector512<ulong> bits, ulong range)
    {
        var r = Vector512.Create(range);
        var low = (bits & Vector512.Create(0xFFFFFFFFUL)) * r;
        return Vector512.ShiftRightLogical(Vector512.ShiftRightLogical(bits, 32) * r + Vector512.ShiftRightLogical(low, 32), 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> High(Vector256<ulong> bits, ulong range)
    {
        var r = Vector256.Create(range);
        var low = (bits & Vector256.Create(0xFFFFFFFFUL)) * r;
        return Vector256.ShiftRightLogical(Vector256.ShiftRightLogical(bits, 32) * r + Vector256.ShiftRightLogical(low, 32), 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> High(Vector128<ulong> bits, ulong range)
    {
        var r = Vector128.Create(range);
        var low = (bits & Vector128.Create(0xFFFFFFFFUL)) * r;
        return Vector128.ShiftRightLogical(Vector128.ShiftRightLogical(bits, 32) * r + Vector128.ShiftRightLogical(low, 32), 32);
    }

    // EntityRandom.Bits per lane: the id (the row's halves swapped), then the tick and the stream, each mixed in.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> Hash(Vector512<ulong> rows, ulong mixed, ulong tick, uint stream)
    {
        var id = Vector512.ShiftRightLogical(rows, 32) | Vector512.ShiftLeft(rows, 32);
        var x = Mix(Vector512.Create(mixed) ^ id);
        x = Mix(x ^ Vector512.Create(tick));
        return Mix(x ^ Vector512.Create((ulong)stream));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Hash(Vector256<ulong> rows, ulong mixed, ulong tick, uint stream)
    {
        var id = Vector256.ShiftRightLogical(rows, 32) | Vector256.ShiftLeft(rows, 32);
        var x = Mix(Vector256.Create(mixed) ^ id);
        x = Mix(x ^ Vector256.Create(tick));
        return Mix(x ^ Vector256.Create((ulong)stream));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Hash(Vector128<ulong> rows, ulong mixed, ulong tick, uint stream)
    {
        var id = Vector128.ShiftRightLogical(rows, 32) | Vector128.ShiftLeft(rows, 32);
        var x = Mix(Vector128.Create(mixed) ^ id);
        x = Mix(x ^ Vector128.Create(tick));
        return Mix(x ^ Vector128.Create((ulong)stream));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<ulong> Mix(Vector512<ulong> x)
    {
        x = (x ^ Vector512.ShiftRightLogical(x, 30)) * Vector512.Create(Mix1);
        x = (x ^ Vector512.ShiftRightLogical(x, 27)) * Vector512.Create(Mix2);
        return x ^ Vector512.ShiftRightLogical(x, 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Mix(Vector256<ulong> x)
    {
        x = (x ^ Vector256.ShiftRightLogical(x, 30)) * Vector256.Create(Mix1);
        x = (x ^ Vector256.ShiftRightLogical(x, 27)) * Vector256.Create(Mix2);
        return x ^ Vector256.ShiftRightLogical(x, 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Mix(Vector128<ulong> x)
    {
        x = (x ^ Vector128.ShiftRightLogical(x, 30)) * Vector128.Create(Mix1);
        x = (x ^ Vector128.ShiftRightLogical(x, 27)) * Vector128.Create(Mix2);
        return x ^ Vector128.ShiftRightLogical(x, 31);
    }

    // EntityRandom's Mix, for the seed (once per vector, the same for every lane).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * Mix1;
        x = (x ^ (x >> 27)) * Mix2;
        return x ^ (x >> 31);
    }
}
