using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// Which keys a value index hashes by their bytes: a tuple of integers of one size (<c>(int, int)</c>,
/// <c>(long, long, long)</c>, ...), whose bytes are its value and hold no padding (<see cref="BytesKey{TKey}"/>).
/// </summary>
internal static class TupleKeys<TKey>
{
    /// <summary>Whether <typeparamref name="TKey"/>'s bytes are its value (a JIT-time constant at Tier1).</summary>
    public static readonly bool Bytes = IsIntegerTuple();

    /// <summary>The bytes of one element of the tuple (2, 4 or 8), when <see cref="Bytes"/>.</summary>
    public static readonly int ElementSize = Bytes ? ElementOf(typeof(TKey).GetGenericArguments()[0]) : 0;

    private static int ElementOf(Type element) =>
        element == typeof(long) || element == typeof(ulong) ? 8 : element == typeof(int) || element == typeof(uint) ? 4 : 2;

    // A ValueTuple of 2 to 7 elements of one integer type: no padding between them, so equal bytes are equal keys.
    // Only the generic arguments are read (no member reflection: Native AOT keeps them).
    private static bool IsIntegerTuple()
    {
        var type = typeof(TKey);
        if (!type.IsValueType || !type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(ValueTuple<,>) && definition != typeof(ValueTuple<,,>) && definition != typeof(ValueTuple<,,,>)
            && definition != typeof(ValueTuple<,,,,>) && definition != typeof(ValueTuple<,,,,,>) && definition != typeof(ValueTuple<,,,,,,>))
            return false;
        var arguments = type.GetGenericArguments();
        var first = arguments[0];
        if (first != typeof(int) && first != typeof(uint) && first != typeof(long) && first != typeof(ulong)
            && first != typeof(short) && first != typeof(ushort)) return false;
        for (int i = 1; i < arguments.Length; i++)
            if (arguments[i] != first) return false;
        return true;
    }
}

/// <summary>
/// An integer tuple key hashed by its bytes: each 8 bytes (a 4- or 2-byte tail zero-extended) added to the running value
/// and multiplied by the golden ratio's 64-bit constant, the product's halves folded (high XOR low). The fold keeps the
/// bits the multiplication spreads into both halves: the dictionary takes the hash modulo a prime, so the high half alone
/// (Fibonacci hashing's) put 600 grid keys into 266 of 631 buckets, the fold into 380 (a random hash: 387). Equality is
/// the bytes' (the tuple has no padding: <see cref="TupleKeys{TKey}"/>). The dictionary's key itself, not a comparer it
/// is given: its default comparer calls these members directly, where a comparer it is given is an interface call per
/// hash and per compare (IndexBenchmark LookupUnique 2.4-3.4x slower through one).
/// </summary>
internal readonly struct BytesKey<TKey>(TKey value) : IEquatable<BytesKey<TKey>>
{
    private const ulong Golden = 0x9E3779B97F4A7C15;

    public readonly TKey Value = value;

    // The key is read element by element, each load the size of the store that wrote it: a key built in registers is
    // stored field by field, and one wider load over two narrower stores cannot be forwarded from the store buffer (an
    // 8-byte read of an (int, int) stalled every probe: LookupUnique 1.8-2.6x slower that way). Straight-line code for
    // keys up to 16 bytes: the sizes are JIT-time constants, so the branches fold.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(BytesKey<TKey> other)
    {
        int size = Unsafe.SizeOf<TKey>();
        ref byte a = ref Unsafe.As<TKey, byte>(ref Unsafe.AsRef(in Value));
        ref byte b = ref Unsafe.As<TKey, byte>(ref Unsafe.AsRef(in other.Value));
        if (size > 16) return MemoryMarshal.CreateReadOnlySpan(ref a, size).SequenceEqual(MemoryMarshal.CreateReadOnlySpan(ref b, size));
        if (Word(ref a, 0) != Word(ref b, 0)) return false;
        return size <= 8 || Word(ref a, 8) == Word(ref b, 8);
    }

    public override bool Equals(object obj) => obj is BytesKey<TKey> other && Equals(other);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        int size = Unsafe.SizeOf<TKey>();
        ref byte p = ref Unsafe.As<TKey, byte>(ref Unsafe.AsRef(in Value));
        ulong h = Word(ref p, 0) * Golden;
        if (size > 8) h = (h + Word(ref p, 8)) * Golden;
        for (int at = 16; at < size; at += 8) h = (h + Word(ref p, at)) * Golden; // keys past 16 bytes only
        return (int)(h ^ (h >> 32));
    }

    /// <summary>
    /// The up to 8 bytes of the key from <paramref name="at"/>, zero-extended, read one element at a time
    /// (<see cref="TupleKeys{TKey}.ElementSize"/>) and joined in registers, little-endian as the bytes lie.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(ref byte p, int at)
    {
        int left = Unsafe.SizeOf<TKey>() - at;
        ref byte from = ref Unsafe.Add(ref p, at);
        int element = TupleKeys<TKey>.ElementSize;
        if (element == 8) return Unsafe.ReadUnaligned<ulong>(ref from);
        if (element == 4)
        {
            ulong low = Unsafe.ReadUnaligned<uint>(ref from);
            return left >= 8 ? low | (ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref from, 4)) << 32 : low;
        }

        ulong word = Unsafe.ReadUnaligned<ushort>(ref from);
        if (left >= 4) word |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref from, 2)) << 16;
        if (left >= 6) word |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref from, 4)) << 32;
        if (left >= 8) word |= (ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref from, 6)) << 48;
        return word;
    }
}

/// <summary>
/// A value index's lists by key: a dictionary keyed by <typeparamref name="TKey"/>, or, for a key whose bytes are its
/// value, one keyed by <see cref="BytesKey{TKey}"/> (<see cref="TupleKeys{TKey}.Bytes"/>, a JIT-time constant at Tier1, so
/// each member is one dictionary's code). Owner thread, like the index.
/// </summary>
internal readonly struct HeadMap<TKey> where TKey : notnull
{
    private static readonly bool ByBytes = TupleKeys<TKey>.Bytes;

    private readonly Dictionary<TKey, IndexHead> _plain;
    private readonly Dictionary<BytesKey<TKey>, IndexHead> _bytes;

    private HeadMap(bool bytes)
    {
        if (bytes) _bytes = new Dictionary<BytesKey<TKey>, IndexHead>();
        else _plain = new Dictionary<TKey, IndexHead>();
    }

    public static HeadMap<TKey> Create() => new(ByBytes);

    public int Count => ByBytes ? _bytes.Count : _plain.Count;

    /// <summary>The list of <paramref name="key"/>, or a null ref.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref IndexHead Find(TKey key)
    {
        if (ByBytes) return ref CollectionsMarshal.GetValueRefOrNullRef(_bytes, new BytesKey<TKey>(key));
        return ref CollectionsMarshal.GetValueRefOrNullRef(_plain, key);
    }

    /// <summary>The list of <paramref name="key"/>, added empty when it has none.</summary>
    public ref IndexHead GetOrAdd(TKey key, out bool exists)
    {
        if (ByBytes) return ref CollectionsMarshal.GetValueRefOrAddDefault(_bytes, new BytesKey<TKey>(key), out exists);
        return ref CollectionsMarshal.GetValueRefOrAddDefault(_plain, key, out exists);
    }

    public bool ContainsKey(TKey key) => ByBytes ? _bytes.ContainsKey(new BytesKey<TKey>(key)) : _plain.ContainsKey(key);

    public bool Remove(TKey key) => ByBytes ? _bytes.Remove(new BytesKey<TKey>(key)) : _plain.Remove(key);

    public void EnsureCapacity(int capacity)
    {
        if (ByBytes) _bytes.EnsureCapacity(capacity);
        else _plain.EnsureCapacity(capacity);
    }

    /// <summary>Every key into <paramref name="keys"/> from 0 (an ordered walk's first sort).</summary>
    public void CopyKeys(TKey[] keys)
    {
        if (!ByBytes)
        {
            _plain.Keys.CopyTo(keys, 0);
            return;
        }

        int i = 0;
        foreach (var key in _bytes.Keys) keys[i++] = key.Value;
    }

    /// <summary>Forget every list, keeping their arrays in <paramref name="spares"/>.</summary>
    public void Clear(SpareIds spares)
    {
        if (ByBytes) spares.Clear(_bytes);
        else spares.Clear(_plain);
    }

    /// <summary>
    /// The hash's buckets and entries for a probe's prefetch (<see cref="HashSlots{TKey, TValue}"/>) and the bytes of an
    /// entry, 0 when they cannot be read; <paramref name="comparer"/> is the plain dictionary's (null: the default).
    /// </summary>
    public int Stride(out int[] buckets, out Array entries, out IEqualityComparer<TKey> comparer)
    {
        comparer = null;
        buckets = null;
        entries = null;
        if (!ByBytes)
            return HashSlots<TKey, IndexHead>.Works ? HashSlots<TKey, IndexHead>.Stride(_plain, out buckets, out entries, out comparer) : 0;
        // BytesKey hashes with its own members, through the default comparer (null here).
        if (!HashSlots<BytesKey<TKey>, IndexHead>.Works) return 0;
        int stride = HashSlots<BytesKey<TKey>, IndexHead>.Stride(_bytes, out buckets, out entries, out var own);
        return own == null ? stride : 0;
    }

    /// <summary>The hash the dictionary files <paramref name="key"/> under (a probe's prefetch; the plain one's comparer given).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashOf(TKey key, IEqualityComparer<TKey> comparer) => ByBytes
        ? (uint)new BytesKey<TKey>(key).GetHashCode()
        : (uint)(typeof(TKey).IsValueType && comparer == null ? key.GetHashCode() : comparer!.GetHashCode(key));
}
