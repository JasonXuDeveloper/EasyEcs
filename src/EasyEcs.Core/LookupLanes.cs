using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// Reads of other entities in a generated vector loop (a <c>[ForEach]</c> method taking a <see cref="Lookup{T}"/>): the
/// lanes' ids resolved by AVX2 gathers of their records (<see cref="Lookup{T}.Resolve(Vector256{int}, Vector256{int}, ref int, out Vector256{int}, out Vector256{int}, out nint)"/>),
/// then each field the method reads gathered from the one table every lane's entity lives in (<see cref="Gather{TLane}(nint, Vector256{int}, Vector256{int}, int)"/>).
/// Lanes whose entities live in several tables are not resolved: the loop sends that vector's rows to the method, which
/// reads them one by one. Generated code calls this.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class LookupLanes
{
    /// <summary>
    /// Whether the gathers run in hardware (AVX2, 16-byte records): a vector loop reading other entities runs only then;
    /// otherwise every row runs the method.
    /// </summary>
    public static bool IsAccelerated
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Avx2.IsSupported && Unsafe.SizeOf<EntityMeta>() == 16;
    }

    // Where a record keeps its version and its row (the runtime lays out the reference first): read once, constants at Tier1.
    private static readonly int VersionAt = Offset(true), SlotAt = Offset(false);

    private static int Offset(bool version)
    {
        EntityMeta meta = default;
        ref byte start = ref Unsafe.As<EntityMeta, byte>(ref meta);
        return (int)Unsafe.ByteOffset(ref start, ref Unsafe.As<int, byte>(ref version ? ref meta.Version : ref meta.Slot));
    }

    /// <summary>
    /// Field <paramref name="offset"/> (bytes into the component) of the rows of <paramref name="has"/>'s lanes, at byte
    /// <paramref name="rows"/> past <paramref name="column"/>; 0 in the other lanes, which read no memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<TLane> Gather<TLane>(nint column, Vector256<int> rows, Vector256<int> has, int offset) where TLane : unmanaged
    {
        // A JIT-time constant: the vector loop gathers 4-byte lanes (float, int, uint).
        if (typeof(TLane) == typeof(float))
            return Avx2.GatherMaskVector256(Vector256<float>.Zero, (float*)(column + offset), rows, has.AsSingle(), 1).As<float, TLane>();
        return Avx2.GatherMaskVector256(Vector256<int>.Zero, (int*)(column + offset), rows, has, 1).As<int, TLane>();
    }

    /// <inheritdoc cref="Gather{TLane}(nint, Vector256{int}, Vector256{int}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector512<TLane> Gather<TLane>(nint column, Vector512<int> rows, Vector512<int> has, int offset) where TLane : unmanaged =>
        // .NET 10 has no AVX-512 gather: two AVX2 gathers of 8 lanes.
        Vector512.Create(Gather<TLane>(column, rows.GetLower(), has.GetLower(), offset), Gather<TLane>(column, rows.GetUpper(), has.GetUpper(), offset));

    /// <inheritdoc cref="Gather{TLane}(nint, Vector256{int}, Vector256{int}, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector<TLane> Gather<TLane>(nint column, Vector<int> rows, Vector<int> has, int offset) where TLane : unmanaged
    {
        // Vector<T> is 256 bits on AVX2 hardware, 512 with DOTNET_PreferredVectorBitWidth=512 (IsAccelerated gates the loop).
        if (Vector<int>.Count == 16) return Gather<TLane>(column, rows.AsVector512(), has.AsVector512(), offset).AsVector();
        return Gather<TLane>(column, rows.AsVector256(), has.AsVector256(), offset).AsVector();
    }

    /// <summary>
    /// Whether <see cref="Lookup{T}.Prefetch(EntityId)"/> and <see cref="Lookup{T}.PrefetchRow(EntityId)"/> issue
    /// anything (<see cref="Prefetching"/>): x86's <c>PREFETCHT0</c>, or SVE's <c>PRFB</c> on ARM64 with SVE. Elsewhere
    /// (ARM64 without SVE, Apple silicon among them) they do nothing and the reads that follow stall as they do without them.
    /// </summary>
    public static bool CanPrefetch
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Prefetching.IsSupported;
    }

    /// <summary>
    /// Whether a pass reading rows of <paramref name="rowSize"/> bytes of other entities of <paramref name="world"/>
    /// prefetches them (<see cref="Lookup{T}.Prefetches"/>): only where a prefetch exists (<see cref="CanPrefetch"/>) and
    /// when the records and rows a chase lands on, those of every live entity, are past the last-level cache
    /// (<see cref="LastLevelCache"/>; unknown: never). In the cache, a prefetch has no miss to hide and costs up to twice
    /// the chase's time (LookupChaseBenchmark at 1,000 entities); past it, it hides most of two misses per row. Read
    /// once per pass, by the job's initializer.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool Prefetches(World world, int rowSize) =>
        CanPrefetch && PastCache((long)world.EntityCount * (Unsafe.SizeOf<EntityMeta>() + rowSize), LastLevelCache.Bytes);

    /// <summary>How many ids ahead a core loop over a list of ids prefetches records (<see cref="PrefetchesIds"/>).</summary>
    internal const int IdsAhead = 16;

    /// <summary>
    /// Whether a core loop over <paramref name="count"/> ids of <paramref name="world"/> (DestroyEntities, a save or a
    /// move of entities) prefetches the record of the id <see cref="IdsAhead"/> ahead of the one it handles: where a
    /// prefetch exists, for a list longer than the distance, and when every live entity's record and a line of its row
    /// are past the last-level cache. Read once per call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PrefetchesIds(World world, int count) =>
        CanPrefetch && count > IdsAhead && PastCache((long)world.EntityCount * (Unsafe.SizeOf<EntityMeta>() + 64), LastLevelCache.Bytes);

    /// <summary>Whether <paramref name="bytes"/> are past a last-level cache of <paramref name="cache"/> bytes (0: unknown, never).</summary>
    internal static bool PastCache(long bytes, long cache) => cache > 0 && bytes > cache;

    /// <summary>
    /// The prefetches of a vector loop's chase, per vector of <paramref name="count"/> rows from row
    /// <paramref name="i"/>: the records of the rows <paramref name="distance"/> * 2 past it (stage 1) and the rows of
    /// the rows <paramref name="distance"/> past it (stage 2), those below <paramref name="rows"/>. Row r's entity
    /// index is lane <paramref name="index"/> of the <paramref name="stride"/> lanes of row r in
    /// <paramref name="column"/> (the component's lanes, 4 bytes each: an EntityId's Index).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PrefetchAhead<TLane>(World world, uint bit, int size, ref TLane column, int stride, int index, nint i, nint rows, int distance, int count)
        where TLane : unmanaged
    {
        if (!Prefetching.IsSupported) return;
        ref int lanes = ref Unsafe.As<TLane, int>(ref column);
        for (nint r = i + 2 * distance, end = Math.Min(r + count, rows); r < end; r++)
            PrefetchRecord(world, Unsafe.Add(ref lanes, r * stride + index));
        for (nint r = i + distance, end = Math.Min(r + count, rows); r < end; r++)
            PrefetchRow(world, Unsafe.Add(ref lanes, r * stride + index), bit, size);
    }

    /// <summary>
    /// Stage 1 of a chase (group prefetching, AMAC): the record of entity <paramref name="index"/> into the cache, so the
    /// lookup or <see cref="PrefetchRow"/> that reaches it a few rows later finds it there. An index past the records
    /// prefetches nothing. A prefetch never faults: the address of an element of a managed array a collection moves
    /// later only wastes the prefetch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PrefetchRecord(World world, int index)
    {
        if (!Prefetching.IsSupported) return;
        var meta = world.Meta;
        if ((uint)index < (uint)meta.Length)
            Prefetching.Line(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(meta), index)));
    }

    /// <summary>
    /// Stage 2 of a chase: the row of entity <paramref name="index"/> in its table's column of component
    /// <paramref name="bit"/> (<paramref name="size"/> bytes a row) into the cache, from its record (in cache once stage 1
    /// ran for it). Nothing for a free record, a table without the column, or a managed column. The version is not
    /// checked: a stale id prefetches a row of the entity that took its record, which only wastes the prefetch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PrefetchRow(World world, int index, uint bit, int size)
    {
        if (!Prefetching.IsSupported) return;
        var meta = world.Meta;
        if ((uint)index >= (uint)meta.Length) return;
        ref var record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(meta), index);
        var table = record.Archetype;
        if (table == null) return;
        var map = table.ByBit;
        if (bit >= (uint)map.Length) return;
        // An unmanaged column's row 0 (pinned, Archetype.Lookup.Rows), 0 for a managed or missing one; a record's slot
        // is a row of its table, below the column's length.
        nint rows = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(map), bit).Rows;
        if (rows != 0) Prefetching.Line((void*)(rows + (nint)record.Slot * size));
    }

    /// <summary>
    /// After a vector that was not resolved, the vectors that follow it to send to the method without trying:
    /// 3, 15, then 63 after misses in a row, none once one resolves. A vector whose entities live in several tables costs
    /// its gathers before its rows run one at a time; where most do (targets spread over many tables), trying each made
    /// the pass up to 1.5 times as slow as the method row by row (D131).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Missed(ref int backoff) => backoff = Math.Min(backoff * 4 + 3, 63);

    /// <summary>
    /// The records of the ids in lanes (<paramref name="version"/>, <paramref name="index"/>): <paramref name="has"/>, the
    /// lanes of live entities (all bits set), <paramref name="slots"/> their rows (0 elsewhere), and <paramref name="table"/>
    /// the table they live in (left as it is when no lane is live). False when the live lanes' entities live in several
    /// tables, or in another table than <paramref name="table"/> was: the vector's rows read one at a time.
    /// </summary>
    internal static bool Records(World world, Vector256<int> version, Vector256<int> index, ref Archetype table, out Vector256<int> has, out Vector256<int> slots)
    {
        var meta = world.Meta;
        // Record r at byte 16 r = 8 * (2 r): 2 r fits an int for 2^30 records (16 GB of them; a world past that reads
        // row by row), and a lane past them reads record 0, which every world has, then fails its version test.
        if (meta.Length > 1 << 30)
        {
            has = slots = default;
            return false;
        }

        var inRange = Vector256.LessThan(index.AsUInt32(), Vector256.Create((uint)meta.Length)).AsInt32();
        var twice = (index & inRange) << 1;
        ref var first = ref MemoryMarshal.GetArrayDataReference(meta);
        fixed (byte* records = &Unsafe.As<EntityMeta, byte>(ref first))
        {
            has = Vector256.Equals(Avx2.GatherVector256((int*)(records + VersionAt), twice, 8), version) & inRange;
            slots = Avx2.GatherMaskVector256(Vector256<int>.Zero, (int*)(records + SlotAt), twice, has, 8);
        }

        // Matching versions mean live entities, which have a table (Entity.LocateArchetype). The references are read
        // as references, never gathered as addresses a collection could move between two reads.
        uint live = has.ExtractMostSignificantBits();
        while (live != 0)
        {
            var at = Unsafe.Add(ref first, index.GetElement(BitOperations.TrailingZeroCount(live))).Archetype;
            if (table == null) table = at;
            else if (at != table) return false;
            live &= live - 1;
        }

        return true;
    }

    /// <summary>
    /// The column of component <paramref name="bit"/> in <paramref name="table"/> (row 0's address); <paramref name="none"/>
    /// set when the table lacks it (no lane has it). False when the column cannot be gathered from (some
    /// rows lack the component: presence bits; a managed column): the vector's rows read one at a time.
    /// </summary>
    internal static bool Column(Archetype table, uint bit, out nint column, ref bool none)
    {
        column = 0;
        if (table == null) return true; // no lane is live: nothing is gathered
        var map = table.ByBit;
        if (bit >= (uint)map.Length || map[bit].Column == null)
        {
            none = true; // the table lacks the component: no lane has it
            return true;
        }

        ref var lookup = ref map[bit];
        // An unmanaged column is pinned (Lookup.Rows): its row 0 stays where it is while the column is the table's,
        // and a row's byte offset (slot * size) is below the column's length, an int.
        if (lookup.Bits != null || lookup.Rows == 0) return false;
        column = lookup.Rows;
        return true;
    }
}
