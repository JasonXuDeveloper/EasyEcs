using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// The selective store of docs/plans/locality.md 3.3 / 4.4 (Polychroniou, Raghavan and Ross, SIGMOD 2015): the rows of
/// a column whose value passes a condition, written side by side as a row list. A branch per row; a store per row with
/// the count advanced by the condition (no branch); a permutation table: 8 rows per AVX2 compare (256 entries of 8
/// lanes, one per mask byte), or without AVX2 4 rows per 128-bit compare and one byte shuffle (16 entries of 16
/// bytes, one per mask nibble: NEON's TBL on ARM64, SSSE3's PSHUFB on x86); 16 rows per AVX-512 compare with a
/// compressing store; and the core's
/// <see cref="Lanes.Compact{T}"/> over masks of 64 rows. The column is a plain array standing for a component column
/// (the operator does not depend on the world).
/// </summary>
public static class Selection
{
    private static readonly Vector256<int>[] Permutations = BuildPermutations();

    // For each mask of 8 lanes, the lanes it selects first, in order (the rest any order): PermuteVar8x32's indices.
    private static Vector256<int>[] BuildPermutations()
    {
        var table = new Vector256<int>[256];
        Span<int> lanes = stackalloc int[8];
        for (int mask = 0; mask < 256; mask++)
        {
            int n = 0;
            for (int lane = 0; lane < 8; lane++)
                if ((mask >> lane & 1) != 0) lanes[n++] = lane;
            for (; n < 8; n++) lanes[n] = 0;
            table[mask] = Vector256.Create<int>(lanes);
        }

        return table;
    }

    // For each mask of 4 lanes, the bytes of the 32-bit lanes it selects first (the rest 0x80, which both shuffles zero).
    private static readonly Vector128<byte>[] Shuffles = BuildShuffles();

    private static Vector128<byte>[] BuildShuffles()
    {
        var table = new Vector128<byte>[16];
        Span<byte> bytes = stackalloc byte[16];
        for (int mask = 0; mask < 16; mask++)
        {
            bytes.Fill(0x80);
            for (int lane = 0, n = 0; lane < 4; lane++)
                if ((mask >> lane & 1) != 0)
                {
                    for (int b = 0; b < 4; b++) bytes[n * 4 + b] = (byte)(lane * 4 + b);
                    n++;
                }

            table[mask] = Vector128.Create<byte>(bytes);
        }

        return table;
    }

    /// <summary>The rows' values: uniform in [0, 1), so a limit of p selects a fraction p.</summary>
    public static float[] Column(int rows, int seed = 42)
    {
        var random = new Random(seed);
        var values = new float[rows];
        for (int i = 0; i < rows; i++) values[i] = random.NextSingle();
        return values;
    }

    /// <summary>Room for the row list: the rows plus 16 (the vector forms store whole vectors past the count).</summary>
    public static int[] RowList(int rows) => new int[rows + 16];

    public static int Branchy(float[] values, float limit, int[] into)
    {
        int n = 0;
        for (int i = 0; i < values.Length; i++)
            if (values[i] < limit) into[n++] = i;
        return n;
    }

    public static int Branchless(float[] values, float limit, int[] into)
    {
        int n = 0;
        ref int to = ref MemoryMarshal.GetArrayDataReference(into);
        for (int i = 0; i < values.Length; i++)
        {
            Unsafe.Add(ref to, n) = i;
            bool pass = values[i] < limit;
            n += Unsafe.As<bool, byte>(ref pass);
        }

        return n;
    }

    public static int PermutationTable(float[] values, float limit, int[] into)
    {
        if (!Avx2.IsSupported) return ShuffleTable(values, limit, into);
        int n = 0, i = 0;
        ref float from = ref MemoryMarshal.GetArrayDataReference(values);
        ref int to = ref MemoryMarshal.GetArrayDataReference(into);
        ref var table = ref MemoryMarshal.GetArrayDataReference(Permutations);
        var bound = Vector256.Create(limit);
        var rows = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var step = Vector256.Create(8);
        for (; i <= values.Length - 8; i += 8, rows += step)
        {
            uint mask = Vector256.LessThan(Vector256.LoadUnsafe(ref from, (nuint)i), bound).ExtractMostSignificantBits();
            var packed = Avx2.PermuteVar8x32(rows, Unsafe.Add(ref table, (int)mask));
            packed.StoreUnsafe(ref to, (nuint)n);
            n += BitOperations.PopCount(mask);
        }

        for (; i < values.Length; i++)
            if (values[i] < limit) into[n++] = i;
        return n;
    }

    /// <summary>The permutation table with 128-bit vectors: 4 rows per compare, one byte shuffle per 4 rows.</summary>
    public static int ShuffleTable(float[] values, float limit, int[] into)
    {
        int n = 0, i = 0;
        ref float from = ref MemoryMarshal.GetArrayDataReference(values);
        ref int to = ref MemoryMarshal.GetArrayDataReference(into);
        ref var table = ref MemoryMarshal.GetArrayDataReference(Shuffles);
        var bound = Vector128.Create(limit);
        var rows = Vector128.Create(0, 1, 2, 3);
        var step = Vector128.Create(4);
        for (; i <= values.Length - 4; i += 4, rows += step)
        {
            uint mask = Vector128.LessThan(Vector128.LoadUnsafe(ref from, (nuint)i), bound).ExtractMostSignificantBits();
            Shuffle(rows.AsByte(), Unsafe.Add(ref table, (int)mask)).AsInt32().StoreUnsafe(ref to, (nuint)n);
            n += BitOperations.PopCount(mask);
        }

        for (; i < values.Length; i++)
            if (values[i] < limit) into[n++] = i;
        return n;
    }

    // A byte shuffle with indices from memory in one instruction where there is one; 0x80 indices give 0 in every form.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> values, Vector128<byte> indices) =>
        AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.VectorTableLookup(values, indices)
        : Ssse3.IsSupported ? Ssse3.Shuffle(values, indices)
        : Vector128.Shuffle(values, indices);

    public static unsafe int Compress512(float[] values, float limit, int[] into)
    {
        if (!Avx512F.IsSupported) return PermutationTable(values, limit, into);
        int n = 0, i = 0;
        var bound = Vector512.Create(limit);
        var rows = Vector512.Create(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
        var step = Vector512.Create(16);
        fixed (float* from = values)
        fixed (int* to = into)
        {
            for (; i <= values.Length - 16; i += 16, rows += step)
            {
                var pass = Avx512F.CompareLessThan(Avx512F.LoadVector512(from + i), bound).AsInt32();
                Avx512F.CompressStore(to + n, pass, rows);
                n += BitOperations.PopCount((uint)pass.ExtractMostSignificantBits());
            }
        }

        for (; i < values.Length; i++)
            if (values[i] < limit) into[n++] = i;
        return n;
    }

    /// <summary>The core's <see cref="Lanes.Compact{T}"/> over masks of 64 rows (the row numbers as its source).</summary>
    public static int CoreCompact(float[] values, float limit, int[] rowNumbers, int[] into)
    {
        int n = 0, i = 0;
        ref float from = ref MemoryMarshal.GetArrayDataReference(values);
        var bound = Vector256.Create(limit);
        var bound4 = Vector128.Create(limit);
        bool wide = Vector256.IsHardwareAccelerated;
        for (; i <= values.Length - 64; i += 64)
        {
            ulong mask = 0;
            if (wide)
                for (int part = 0; part < 8; part++)
                    mask |= (ulong)Vector256.LessThan(Vector256.LoadUnsafe(ref from, (nuint)(i + part * 8)), bound)
                        .ExtractMostSignificantBits() << (part * 8);
            else
                for (int part = 0; part < 16; part++)
                    mask |= (ulong)Vector128.LessThan(Vector128.LoadUnsafe(ref from, (nuint)(i + part * 4)), bound4)
                        .ExtractMostSignificantBits() << (part * 4);
            n += Lanes.Compact(new ReadOnlySpan<int>(rowNumbers, i, 64), mask, into.AsSpan(n));
        }

        for (; i < values.Length; i++)
            if (values[i] < limit) into[n++] = i;
        return n;
    }
}

/// <summary>
/// The batch probe of docs/plans/locality.md 3.3 / 4.4: an open-addressing int to int table (linear probing, a
/// power-of-two capacity at most half full, keys > 0, 0 an empty slot) probed for a batch of keys. One key at a time;
/// vertically, 8 keys per AVX2 vector, the vector kept until all its lanes finished; and vertically with refill
/// (Polychroniou: a finished lane takes the next key at once, AVX-512VL expanding loads, the finished lanes'
/// results compressed out, then placed by query).
/// </summary>
public sealed class ProbeTable
{
    public readonly int[] Keys;
    public readonly int[] Values;
    private readonly int _shift;
    private readonly int _mask;

    public ProbeTable(int capacity)
    {
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)capacity * 2);
        Keys = new int[slots];
        Values = new int[slots];
        _mask = slots - 1;
        _shift = 32 - BitOperations.Log2((uint)slots);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(int key) => (int)((uint)key * 0x9E3779B1u >> _shift);

    public void Add(int key, int value)
    {
        int h = Slot(key);
        while (Keys[h] != 0 && Keys[h] != key) h = h + 1 & _mask;
        Keys[h] = key;
        Values[h] = value;
    }

    /// <summary>A table of <paramref name="count"/> random keys (value = key * 3) and a batch of queries, 90 % of them
    /// keys in the table.</summary>
    public static (ProbeTable Table, int[] Queries) Create(int count, int queries, int seed = 42)
    {
        var random = new Random(seed);
        var table = new ProbeTable(count);
        var keys = new int[count];
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int key;
            do key = random.Next(1, int.MaxValue / 4); while (!seen.Add(key));
            keys[i] = key;
            table.Add(key, key * 3);
        }

        var batch = new int[queries];
        for (int i = 0; i < queries; i++)
        {
            if (random.Next(10) != 0) batch[i] = keys[random.Next(count)];
            else
            {
                int key;
                do key = random.Next(1, int.MaxValue / 4); while (seen.Contains(key));
                batch[i] = key;
            }
        }

        return (table, batch);
    }

    public void OneAtATime(int[] queries, int[] results)
    {
        int[] keys = Keys, values = Values;
        for (int q = 0; q < queries.Length; q++)
        {
            int key = queries[q], h = Slot(key), found = -1;
            while (true)
            {
                int k = keys[h];
                if (k == key)
                {
                    found = values[h];
                    break;
                }

                if (k == 0) break;
                h = h + 1 & _mask;
            }

            results[q] = found;
        }
    }

    /// <summary>8 keys per vector; the vector loops until every lane met its key or an empty slot (lanes that finished
    /// early idle). Falls back to <see cref="OneAtATime"/> without AVX2.</summary>
    public unsafe void Vertical(int[] queries, int[] results)
    {
        if (!Avx2.IsSupported)
        {
            OneAtATime(queries, results);
            return;
        }

        var multiplier = Vector256.Create(unchecked((int)0x9E3779B1u));
        var mask = Vector256.Create(_mask);
        var one = Vector256.Create(1);
        var missing = Vector256.Create(-1);
        int q = 0;
        fixed (int* keys = Keys, values = Values, from = queries, to = results)
        {
            for (; q <= queries.Length - 8; q += 8)
            {
                var key = Vector256.Load(from + q);
                var h = Vector256.ShiftRightLogical(key * multiplier, _shift);
                var open = Vector256<int>.AllBitsSet;        // lanes still probing
                var hit = Vector256<int>.Zero;
                while (true)
                {
                    var k = Avx2.GatherMaskVector256(Vector256<int>.Zero, keys, h, open, 4);
                    var match = Vector256.Equals(k, key) & open;
                    hit |= match;
                    open &= ~(match | Vector256.Equals(k, Vector256<int>.Zero));
                    if (open == Vector256<int>.Zero) break;
                    h = Vector256.ConditionalSelect(open, h + one & mask, h);
                }

                Avx2.GatherMaskVector256(missing, values, h, hit, 4).Store(to + q);
            }
        }

        for (; q < queries.Length; q++) results[q] = OneKey(queries[q]);
    }

    /// <summary>
    /// 8 lanes that never idle: when lanes finish, their (query, value) pairs are compressed into the output and the
    /// next keys expand into them (AVX-512VL); at the end the pairs are placed by query. Falls back to
    /// <see cref="Vertical"/> without AVX-512VL.
    /// </summary>
    public unsafe void VerticalRefill(int[] queries, int[] results, int[] pairQuery, int[] pairValue)
    {
        if (!Avx512F.VL.IsSupported || !Avx2.IsSupported)
        {
            Vertical(queries, results);
            return;
        }

        var multiplier = Vector256.Create(unchecked((int)0x9E3779B1u));
        var mask = Vector256.Create(_mask);
        var one = Vector256.Create(1);
        var missing = Vector256.Create(-1);
        var step = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        int count = queries.Length & ~7, next = 8, done = 0;
        if (count == 0)
        {
            for (int i = 0; i < queries.Length; i++) results[i] = OneKey(queries[i]);
            return;
        }

        fixed (int* keys = Keys, values = Values, from = queries, outQuery = pairQuery, outValue = pairValue)
        {
            var key = Vector256.Load(from);
            var query = step;
            var h = Vector256.ShiftRightLogical(key * multiplier, _shift);
            var live = Vector256<int>.AllBitsSet;           // lanes holding a query
            while (live != Vector256<int>.Zero)
            {
                var k = Avx2.GatherMaskVector256(Vector256<int>.Zero, keys, h, live, 4);
                var match = Vector256.Equals(k, key) & live;
                var finished = (match | Vector256.Equals(k, Vector256<int>.Zero)) & live;
                h = Vector256.ConditionalSelect(live & ~finished, h + one & mask, h);    // the lanes still probing step on
                if (finished != Vector256<int>.Zero)
                {
                    var value = Avx2.GatherMaskVector256(missing, values, h, match, 4);
                    Avx512F.VL.CompressStore(outQuery + done, finished, query);
                    Avx512F.VL.CompressStore(outValue + done, finished, value);
                    done += BitOperations.PopCount(finished.ExtractMostSignificantBits());

                    // Refill the finished lanes with the next keys; past the end they stop holding a query.
                    int left = count - next;
                    uint finishedBits = finished.ExtractMostSignificantBits();
                    int refill = Math.Min(BitOperations.PopCount(finishedBits), left);
                    uint refillBits = finishedBits;
                    for (int drop = BitOperations.PopCount(finishedBits) - refill; drop > 0; drop--)
                        refillBits &= ~(1u << (31 - BitOperations.LeadingZeroCount(refillBits)));   // highest lanes go idle
                    var refilled = Vector256.Equals(Vector256.Create((int)refillBits) & Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128),
                        Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128));
                    if (refill > 0)
                    {
                        key = Avx512F.VL.ExpandLoad(from + next, refilled, key);
                        query = Avx512F.VL.Expand(query, refilled, Vector256.Create(next) + step);
                        h = Vector256.ConditionalSelect(refilled, Vector256.ShiftRightLogical(key * multiplier, _shift), h);
                        next += refill;
                    }

                    live = (live & ~finished) | refilled;
                }
            }
        }

        for (int i = 0; i < done; i++) results[pairQuery[i]] = pairValue[i];
        for (int q = count; q < queries.Length; q++) results[q] = OneKey(queries[q]);
    }

    private int OneKey(int key)
    {
        int h = Slot(key);
        while (true)
        {
            int k = Keys[h];
            if (k == key) return Values[h];
            if (k == 0) return -1;
            h = h + 1 & _mask;
        }
    }
}
