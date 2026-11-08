using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

// Slices: a pass that visits part of each matching table's rows, so a system spreads its rows over several updates
// ([ForEach(Slices = N)]). The fields (SliceIndex, SliceCount) live with the other fields in Query.cs: a struct with
// a declared layout keeps its fields in one declaration.
public readonly partial struct QueryFilter
{
    /// <summary>
    /// Visit only slice <paramref name="index"/> of <paramref name="of"/> of every matching table's rows: passes with
    /// each index from 0 to <paramref name="of"/> - 1 in turn visit every row once, each about
    /// 1 / <paramref name="of"/> of the rows. <c>[ForEach(Slices = N)]</c> methods pass the slice of their system's
    /// run (<see cref="SystemHandle.RunCount"/> mod N). O(1).
    /// <para>A table is cut at multiples of 64 rows (its presence words), from its row count when the pass starts, so a
    /// table of fewer than 64 rows is one piece; table i of the match starts its cut at slice
    /// (<paramref name="index"/> + i) mod <paramref name="of"/>, so many small tables spread over the slices too.
    /// Rows that move between passes (a swap-remove fills a hole with the table's last row, a structural change moves
    /// an entity to another table, the table grows or shrinks across a cut) may be skipped or visited twice in one
    /// cycle of <paramref name="of"/> passes; the next cycle sees them again.</para>
    /// <para>Honoured by <c>ForEach</c>, <c>ForEachEntity</c>, <c>ForEachBatch</c> and their <c>Parallel</c> forms;
    /// the calls that need every matching row (<c>Query</c>, <c>Read</c>, <c>Count</c>, <c>Any</c>, <c>DestroyAll</c>,
    /// <c>entity.Run</c>, <c>SetAll</c> and the other bulk calls) throw <see cref="ArgumentException"/> for a sliced
    /// filter. A slice of 1 is the whole tables.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="of"/> is below 1, or <paramref name="index"/> is
    /// not in [0, <paramref name="of"/>).</exception>
    public QueryFilter Slice(int index, int of)
    {
        if (of < 1 || (uint)index >= (uint)of) ThrowSlice(index, of);
        var filter = this;
        Unsafe.AsRef(in filter.SliceIndex) = of == 1 ? 0 : index;
        Unsafe.AsRef(in filter.SliceCount) = of == 1 ? 0 : of;
        return filter;
    }

    /// <summary>
    /// The rows [Start, End) of table <paramref name="table"/> of the match (of <paramref name="rows"/> rows) a pass
    /// visits: all of them unless the filter is sliced. Start is a multiple of 64 (a presence word's first row).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal (int Start, int End) Rows(int table, int rows) => SliceCount == 0 ? (0, rows) : Cut(table, rows);

    // The slice's whole words: words * k / n to words * (k + 1) / n, the last one ending at the table's last row.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (int Start, int End) Cut(int table, int rows)
    {
        long words = (rows + 63) >> 6;
        long k = ((long)SliceIndex + table) % SliceCount;
        return ((int)(words * k / SliceCount) << 6, (int)Math.Min(words * (k + 1) / SliceCount << 6, rows));
    }

    /// <summary>Throws when the filter is sliced: the call needs every matching row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Whole()
    {
        if (SliceCount != 0) ThrowSliced();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSlice(int index, int of) => throw new ArgumentOutOfRangeException(nameof(index),
        $"Slice {index} of {of}: 'of' must be 1 or more, and the index in [0, of).");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSliced() => throw new ArgumentException(
        "A sliced filter (QueryFilter.Slice) applies to ForEach passes only: this call needs every matching row.", "filter");
}
