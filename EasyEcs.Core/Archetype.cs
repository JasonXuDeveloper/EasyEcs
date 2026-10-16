using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
namespace EasyEcs;

/// <summary>
/// The entities with one component configuration and their data: a table with one column per component
/// type, row = slot (structure of arrays). Iteration walks every column front to back; a structural change copies the
/// entity's row into the table of its new archetype (<see cref="Transition"/>).
/// <br/>
/// Outside passes a row that loses its entity is filled by the last row at once (tables stay dense). While a pass
/// iterates, it becomes a tombstone (id -1) instead, so the pass never sees rows shift under it; the world closes those
/// tombstones (<see cref="FillHoles"/>) when the last pass ends. The slot of each entity is tracked by the owning
/// <see cref="World"/> (entity id -> slot).
/// <br/>
/// Removing a component keeps the entity in its table: the row only clears the component's presence bit
/// (<see cref="Present"/>), so a state that comes and goes (stunned, burning, dead) never moves rows. Only adding a
/// component the table has no column for moves the entity.
/// </summary>
internal sealed class Archetype
{
    public readonly Tag ComponentMask;

    /// <summary>Component bits of this archetype, ascending.</summary>
    public readonly ushort[] Components;

    /// <summary>Component columns, parallel to <see cref="Components"/>; each has <see cref="EntityIds"/>.Length rows.</summary>
    public readonly Array[] Columns;

    /// <summary>Column descriptions, parallel to <see cref="Components"/>.</summary>
    private readonly ComponentInfo[] _infos;

    /// <summary>Component bit -> index into <see cref="Columns"/>, -1 when absent (covers bits up to the largest one).</summary>
    private readonly short[] _columnOf;

    /// <summary>
    /// Component bit -> its column and presence bits (null column when absent; covers bits up to the largest one). One
    /// 16-byte entry per bit, so a handle lookup (<see cref="Entity.Get{T}"/>) reads both from one cache line. Kept in
    /// sync with <see cref="Columns"/> and <see cref="Present"/>.
    /// </summary>
    public readonly Lookup[] ByBit;

    /// <summary>Whether any column holds managed references (cleared when a row dies, so they can be collected).</summary>
    private readonly bool _hasManaged;

    /// <summary>
    /// Per column: one bit per row, set while the entity has the component (64 rows per word, so queries test 64 rows
    /// with one load). Allocated when a row of the column first loses its component; null means every row has it.
    /// Bits of tombstones and of rows at or above <see cref="Count"/> are meaningless (a new row sets its own).
    /// </summary>
    public readonly ulong[][] Present;

    /// <summary>Per column: alive rows without the component (their <see cref="Present"/> bit is clear).</summary>
    public readonly int[] Absent;

    /// <summary>
    /// Per column: the latest change tick stamped into it, for change-tick columns (an upper bound: rows that move in
    /// raise it to their source table's). A <c>Changed</c> filter skips the table when it is older than the pass's tick.
    /// </summary>
    public readonly ulong[] LastWrite;

    /// <summary>
    /// Per change-tick column (null for others): the latest tick of each 64-row word (exact for the word's rows below
    /// the table's capacity). A <c>Changed</c> filter skips a word older than the pass's tick with one load.
    /// </summary>
    public readonly ulong[][] WordWrite;

    /// <summary>
    /// Per change-tick column: for each word, the rows whose tick is <see cref="WordWrite"/>, valid while
    /// <see cref="WordRest"/> is below it. Then it answers a <c>Changed</c> filter without reading the ticks (see
    /// <see cref="NoteWord"/>).
    /// </summary>
    public readonly ulong[][] WordRows;

    /// <summary>Per change-tick column: for each word, an upper bound of the ticks of the rows not in <see cref="WordRows"/>.</summary>
    public readonly ulong[][] WordRest;

    /// <summary>
    /// Per change-tick column (null for others): the latest tick stamped into each block of 64 words (4,096 rows), an
    /// upper bound like <see cref="WordWrite"/>. A <c>Changed</c> filter skips a block older than the pass's tick.
    /// </summary>
    public readonly ulong[][] BlockWrite;

    private readonly bool _tracked; // has change-tick columns

    /// <summary>Columns with <see cref="Present"/> bits (indices into <see cref="Columns"/>), copy-on-write.</summary>
    private short[] _bitColumns = [];

    public int[] EntityIds;
    public int[] Versions; // parallel to EntityIds: version of the entity in each slot (handles without a meta lookup)
    public int Count; // rows in use, tombstones included
    public int AliveCount; // living entities only

    /// <summary>Has tombstones that the world closes when the last pass ends (registered once, see World.Remove).</summary>
    public bool Dirty;

    // Transition cache: component bit -> transition reached by adding that component (removing never moves rows).
    // Avoids hashing a 256-bit Tag on every single-component structural change.
    private Transition[] _addEdges = [];

    // Same for multi-component adds (Set(a, b), CreateEntity(a, b, ...)), indexed by the dense index of the generic
    // signature (TypeIndex<ComponentSignature, (T1, ..., Tn)>): no mask cloning or hashing per call.
    private Transition[] _addSetEdges = [];

    private const int Tombstone = -1;

    public Archetype(in Tag componentMask, int initialCapacity = 64)
    {
        ComponentMask = componentMask;
        Components = componentMask.ToBitArray();
        EntityIds = new int[initialCapacity];
        Versions = new int[initialCapacity];

        Columns = new Array[Components.Length];
        Present = new ulong[Components.Length][];
        Absent = new int[Components.Length];
        LastWrite = new ulong[Components.Length];
        WordWrite = new ulong[Components.Length][];
        WordRows = new ulong[Components.Length][];
        WordRest = new ulong[Components.Length][];
        BlockWrite = new ulong[Components.Length][];
        _infos = new ComponentInfo[Components.Length];
        _columnOf = new short[Components.Length == 0 ? 0 : Components[^1] + 1];
        _columnOf.AsSpan().Fill(-1);
        ByBit = new Lookup[_columnOf.Length];
        for (int i = 0; i < Components.Length; i++)
        {
            var info = ComponentInfo.Of(Components[i]);
            _infos[i] = info;
            _hasManaged |= info.Managed;
            Columns[i] = info.Create(initialCapacity);
            if (info.Owner >= 0)
            {
                WordWrite[i] = new ulong[(initialCapacity + 63) >> 6];
                WordRows[i] = new ulong[(initialCapacity + 63) >> 6];
                WordRest[i] = new ulong[(initialCapacity + 63) >> 6];
                BlockWrite[i] = new ulong[(initialCapacity + 4095) >> 12];
            }

            _tracked |= info.Owner >= 0;
            _columnOf[Components[i]] = (short)i;
            ByBit[Components[i]].Column = Columns[i];
        }
    }

    /// <summary>A column and its presence bits (<see cref="ByBit"/>).</summary>
    public struct Lookup
    {
        public Array Column;
        public ulong[] Bits;
    }

    /// <summary>Presence bits of column <paramref name="column"/> (a valid index), or null when every row has it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong[] Bits(int column) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Present), column);

    /// <summary>Whether any column has rows without its component.</summary>
    public bool HasAbsent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bitColumns.Length != 0;
    }

    /// <summary>Whether row <paramref name="slot"/> has the component of column <paramref name="column"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(int column, int slot) => Bits(column) is not { } bits || (bits[slot >> 6] & (1UL << slot)) != 0; // shift is mod 64

    /// <summary>
    /// Give row <paramref name="slot"/> the component of column <paramref name="column"/> back (its column exists).
    /// Returns true when the row lacked it. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Restore(int column, int slot)
    {
        if (Bits(column) is not { } bits) return false;
        ref ulong word = ref bits[slot >> 6];
        ulong bit = 1UL << slot;
        if ((word & bit) != 0) return false;
        word |= bit;
        Absent[column]--;
        return true;
    }

    /// <summary>
    /// Take the component of column <paramref name="column"/> from row <paramref name="slot"/>: clear its presence bit
    /// (and a managed value, so the GC can collect what it referenced). Returns true when the row had it. Inside the
    /// structural gate.
    /// </summary>
    public bool Take(int column, int slot)
    {
        var bits = Bits(column) ?? AllocateBits(column);
        ref ulong word = ref bits[slot >> 6];
        ulong bit = 1UL << slot;
        if ((word & bit) == 0) return false;
        word &= ~bit;
        Absent[column]++;
        if (_infos[column].Managed) _infos[column].Clear(Columns[column], slot, 1);
        return true;
    }

    /// <summary>A row dies (destroyed or moved away): it no longer counts as lacking components.</summary>
    private void ForgetAbsent(int slot)
    {
        foreach (short c in _bitColumns)
            if ((Present[c][slot >> 6] & (1UL << slot)) == 0)
                Absent[c]--;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong[] AllocateBits(int column)
    {
        var bits = new ulong[(EntityIds.Length + 63) >> 6];
        bits.AsSpan().Fill(ulong.MaxValue);
        var columns = new short[_bitColumns.Length + 1];
        _bitColumns.CopyTo(columns, 0);
        columns[^1] = (short)column;
        _bitColumns = columns;
        Volatile.Write(ref Present[column], bits); // lock-free readers see a filled array
        Volatile.Write(ref ByBit[Components[column]].Bits, bits);
        return bits;
    }

    /// <summary>Copy the presence bits of row <paramref name="from"/> to row <paramref name="to"/>.</summary>
    private void CopyBits(int from, int to)
    {
        foreach (short c in _bitColumns)
        {
            var bits = Present[c];
            ulong bit = 1UL << to;
            bits[to >> 6] = (bits[from >> 6] & (1UL << from)) != 0 ? bits[to >> 6] | bit : bits[to >> 6] & ~bit;
        }
    }

    /// <summary>Set every presence bit of row <paramref name="slot"/> (a new row has all its components).</summary>
    private void SetBits(int slot)
    {
        foreach (short c in _bitColumns) Present[c][slot >> 6] |= 1UL << slot;
    }

    /// <summary>Index of the column of component <paramref name="bit"/>, or -1 when the archetype does not have it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnIndex(int bit)
    {
        var map = _columnOf;
        return (uint)bit < (uint)map.Length ? map[bit] : -1;
    }

    /// <summary>Bytes per cache line: unmanaged columns start their rows on one (<see cref="Aligned"/>).</summary>
    public const int CacheLine = 64;

    /// <summary>
    /// Row 0 of an unmanaged column: the first 64-byte boundary of its pinned byte array. The array never moves (pinned
    /// object heap) and has a line of slack (<see cref="ComponentInfo{T}.Create"/>), so the rows fit after it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ref byte Aligned(Array column)
    {
        ref byte data = ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(column));
        return ref Unsafe.Add(ref data, (nint)(-(nint)Unsafe.AsPointer(ref data) & (CacheLine - 1)));
    }

    /// <summary>Row 0 of column <paramref name="index"/> (from <see cref="ColumnIndex"/>; must be valid).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Data<T>(int index) where T : struct, IComponent =>
        ref Row0<T>(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Columns), index));

    /// <summary>Row 0 of a column of <typeparamref name="T"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T Row0<T>(Array column) where T : struct, IComponent
    {
        // A JIT-time constant: each instantiation keeps one branch.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return ref MemoryMarshal.GetArrayDataReference(Unsafe.As<T[]>(column));
        return ref Unsafe.As<byte, T>(ref Aligned(column));
    }

    /// <summary>Row 0 of the column of <paramref name="bit"/>, which the archetype must have (iteration base).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Base<T>(int bit) where T : struct, IComponent => ref Data<T>(ColumnIndex(bit));

    /// <summary>
    /// Ref to component <paramref name="bit"/> (which the archetype must have) in <paramref name="slot"/>; slots are
    /// below the column length (columns have as many rows as <see cref="EntityIds"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Ref<T>(int bit, int slot) where T : struct, IComponent => ref Unsafe.Add(ref Base<T>(bit), slot);

    /// <summary>
    /// Record a write of <typeparamref name="T"/> in row <paramref name="slot"/> at <paramref name="tick"/> when
    /// <typeparamref name="T"/> tracks changes (a JIT-time constant: nothing for other components).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Stamp<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITrackChanges) Ticks<T>(tick, slot, 1UL << slot).Tick = tick; // shift is mod 64
    }

    /// <summary>
    /// <see cref="Stamp{T}"/> for a pass that writes row after row (a query, a per-entity job): the row's word loses
    /// its exact rows, which such a pass would rebuild bit by bit, so readers compare the word's ticks (stores only).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampVisited<T>(int slot, ulong tick) where T : struct, IComponent
    {
        NoteVisited<T>(slot, tick);
        StampTick<T>(slot, tick);
    }

    /// <summary>
    /// The summaries of <see cref="StampVisited{T}"/> alone: they then cover any row of the word stamped at
    /// <paramref name="tick"/> with <see cref="StampTick{T}"/> (a pass notes each word once).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NoteVisited<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITrackChanges)
        {
            int column = ColumnIndex(ComponentType<ChangeTicks<T>>.Bit);
            // WordRest == WordWrite: WordRows is not read until a later tick notes the word again (NoteWord).
            WordWrite[column][slot >> 6] = tick;
            WordRest[column][slot >> 6] = tick;
            BlockWrite[column][slot >> 12] = tick;
            LastWrite[column] = tick;
        }
    }

    /// <summary>The row's change tick alone; its word must have been noted at <paramref name="tick"/> (<see cref="NoteVisited{T}"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampTick<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITrackChanges)
            Unsafe.Add(ref Data<ChangeTicks<T>>(ColumnIndex(ComponentType<ChangeTicks<T>>.Bit)), slot).Tick = tick;
    }

    /// <summary>
    /// Row <paramref name="slot"/> of the change ticks of <typeparamref name="T"/>, noting a write at
    /// <paramref name="tick"/> of rows <paramref name="rows"/> of the row's word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref ChangeTicks<T> Ticks<T>(ulong tick, int slot, ulong rows) where T : struct, IComponent
    {
        int column = ColumnIndex(ComponentType<ChangeTicks<T>>.Bit);
        // Summaries only grow (LastWrite >= BlockWrite >= WordWrite) and no row holds a later tick than the world's:
        // a word already at this tick has its block and column at it too, so only its rows change.
        if (WordWrite[column][slot >> 6] == tick) WordRows[column][slot >> 6] |= rows;
        else NoteWord(column, slot, rows, tick);
        return ref Unsafe.Add(ref Data<ChangeTicks<T>>(column), slot);
    }

    /// <summary>
    /// Rows <paramref name="rows"/> of the word of <paramref name="slot"/> get tick <paramref name="tick"/>, the world's
    /// current one (no row of the column holds a later tick). Keeps the word's summaries exact: the rows of
    /// <see cref="WordRows"/> hold <see cref="WordWrite"/>, the others at most <see cref="WordRest"/>.
    /// </summary>
    private void NoteWord(int column, int slot, ulong rows, ulong tick)
    {
        int word = slot >> 6;
        ref ulong latest = ref WordWrite[column][word];
        ref ulong written = ref WordRows[column][word];
        if (latest == tick) written |= rows;
        else
        {
            WordRest[column][word] = latest; // every row of the word held at most the previous latest tick
            latest = tick;
            written = rows;
        }

        BlockWrite[column][slot >> 12] = tick;
        LastWrite[column] = tick;
    }

    /// <summary>
    /// Raise the change-tick summaries (word, block, column) of row <paramref name="to"/> of <paramref name="target"/> to
    /// those of row <paramref name="from"/> of <paramref name="source"/> (a row copy).
    /// </summary>
    internal static void RaiseWords(Archetype source, int from, Archetype target, int to)
    {
        for (int c = 0; c < source.WordWrite.Length; c++)
        {
            if (source.WordWrite[c] is null) continue;
            int t = target.ColumnIndex(source.Components[c]);
            if (t < 0) continue;
            // The copied tick (the row was copied first); it may be older than the target word's latest.
            ulong tick = Unsafe.Add(ref Unsafe.As<ChangeTick, ulong>(ref target.Data<ChangeTick>(t)), to);
            int w = to >> 6;
            ulong bit = 1UL << to; // shift is mod 64
            ref ulong latest = ref target.WordWrite[t][w];
            ref ulong written = ref target.WordRows[t][w];
            if (tick > latest)
            {
                target.WordRest[t][w] = latest;
                latest = tick;
                written = bit;
            }
            else if (tick == latest) written |= bit;
            else
            {
                written &= ~bit;
                ref ulong rest = ref target.WordRest[t][w];
                rest = Math.Max(rest, tick);
            }

            ref ulong block = ref target.BlockWrite[t][to >> 12];
            block = Math.Max(block, tick);
            target.LastWrite[t] = Math.Max(target.LastWrite[t], tick);
        }
    }

    /// <summary><see cref="Stamp{T}"/> for rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampRows<T>(int start, int count, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITrackChanges && count > 0)
        {
            int end = start + count;
            MemoryMarshal.CreateSpan(ref Unsafe.As<ChangeTicks<T>, ulong>(ref Ticks<T>(tick, start, RunMask(start, end, start >> 6))), count).Fill(tick);
            int column = ColumnIndex(ComponentType<ChangeTicks<T>>.Bit);
            int first = (start >> 6) + 1, last = (end - 1) >> 6;
            if (last >= first)
            {
                // Whole words between the first and the last: every row gets the tick, so the rest bound may be any
                // tick up to it (the previous latest is one); the last word may be partial.
                int whole = last - first;
                WordWrite[column].AsSpan(first, whole).CopyTo(WordRest[column].AsSpan(first, whole));
                WordWrite[column].AsSpan(first, whole).Fill(tick);
                WordRows[column].AsSpan(first, whole).Fill(ulong.MaxValue);
                NoteWord(column, last << 6, RunMask(start, end, last), tick);
            }

            BlockWrite[column].AsSpan(start >> 12, ((end - 1) >> 12) - (start >> 12) + 1).Fill(tick);
        }
    }

    /// <summary>The rows of [<paramref name="start"/>, <paramref name="end"/>) in word <paramref name="word"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RunMask(int start, int end, int word)
    {
        int first = Math.Max(start - (word << 6), 0), last = Math.Min(end - (word << 6), 64); // rows [first, last)
        ulong upTo = last == 64 ? ulong.MaxValue : (1UL << last) - 1;
        return upTo & (ulong.MaxValue << first);
    }

    /// <summary>Stamp every row of every change-tick column at <paramref name="tick"/> (after a restore). Cold path.</summary>
    public void StampTracked(ulong tick)
    {
        if (Count == 0) return;
        for (int c = 0; c < Components.Length; c++)
            if (ComponentInfo.Of(Components[c]).Owner >= 0)
            {
                MemoryMarshal.CreateSpan(ref Unsafe.As<ChangeTick, ulong>(ref Data<ChangeTick>(c)), Count).Fill(tick);
                for (int w = 0; w <= (Count - 1) >> 6; w++) NoteWord(c, w << 6, RunMask(0, Count, w), tick);
                BlockWrite[c].AsSpan(0, ((Count - 1) >> 12) + 1).Fill(tick);
                LastWrite[c] = tick;
            }
    }

    /// <summary><see cref="Stamp{T}"/> for the rows of word <paramref name="word"/> whose bit is set in <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampWord<T>(int word, ulong rows, ulong tick) where T : struct, IComponent
    {
        if (!(default(T) is ITrackChanges)) return;
        if (rows == 0) return;
        ref var ticks = ref Ticks<T>(tick, word << 6, rows);
        for (; rows != 0; rows &= rows - 1) Unsafe.Add(ref ticks, BitOperations.TrailingZeroCount(rows)).Tick = tick;
    }

    /// <summary>Append an entity and return its row (a pass that started earlier does not visit it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Add(int entityId, int version)
    {
        if (Count >= EntityIds.Length) Grow(EntityIds.Length * 2);
        int slot = Count++;
        // slot < EntityIds.Length == Versions.Length (grown above): no bounds checks.
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(EntityIds), slot) = entityId;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Versions), slot) = version;
        AliveCount++;
        if (_bitColumns.Length != 0) SetBits(slot); // a transition then clears what the entity lacks
        return slot;
    }

    /// <summary>
    /// Next run of alive rows at or after <paramref name="start"/> among the first <paramref name="count"/> rows:
    /// returns false when there is none, else sets <paramref name="start"/> and <paramref name="length"/>. Both scans
    /// are vectorized (<c>IndexOfAnyExcept</c> / <c>IndexOf</c> on the id array).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NextRun(ref int start, int count, out int length)
    {
        var ids = new ReadOnlySpan<int>(EntityIds, 0, count);
        int skip = ids.Slice(start).IndexOfAnyExcept(Tombstone);
        if (skip < 0)
        {
            length = 0;
            return false;
        }

        start += skip;
        length = ids.Slice(start).IndexOf(Tombstone);
        if (length < 0) length = count - start;
        return true;
    }

    /// <summary>Make room for <paramref name="rows"/> rows in one growth (bulk creation).</summary>
    public void Reserve(int rows)
    {
        if (rows > EntityIds.Length) Grow((int)BitOperations.RoundUpToPowerOf2((uint)rows));
    }

    /// <summary>Grow the id arrays and every column to <paramref name="capacity"/> rows (cold path).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity)
    {
        Array.Resize(ref EntityIds, capacity);
        Array.Resize(ref Versions, capacity);
        for (int i = 0; i < Columns.Length; i++)
            ByBit[Components[i]].Column = Columns[i] = _infos[i].Grow(Columns[i], capacity);
        foreach (short c in _bitColumns)
        {
            Array.Resize(ref Present[c], (capacity + 63) >> 6);
            ByBit[Components[c]].Bits = Present[c];
        }

        for (int c = 0; c < WordWrite.Length; c++)
            if (WordWrite[c] != null)
            {
                Array.Resize(ref WordWrite[c], (capacity + 63) >> 6);
                Array.Resize(ref WordRows[c], (capacity + 63) >> 6);
                Array.Resize(ref WordRest[c], (capacity + 63) >> 6);
                Array.Resize(ref BlockWrite[c], (capacity + 4095) >> 12);
            }
    }

    /// <summary>
    /// Remove the entity in row <paramref name="slot"/> (its data was copied away or is dead). Outside passes the last
    /// row moves into the slot (one row copy, tables stay dense); during a pass, or when the table already has
    /// tombstones, the row becomes a tombstone. Returns true when the table has just become <see cref="Dirty"/>.
    /// </summary>
    public bool Remove(int slot, EntityMeta[] meta, bool inPass)
    {
        AliveCount--;
        if (_bitColumns.Length != 0) ForgetAbsent(slot);
        // slot < Count <= EntityIds.Length: no bounds checks.
        ref int ids = ref MemoryMarshal.GetArrayDataReference(EntityIds);
        if (inPass || Count != AliveCount + 1)
        {
            Unsafe.Add(ref ids, slot) = Tombstone;
            if (Dirty) return false;
            Dirty = true;
            return true;
        }

        int last = --Count;
        if (slot != last) MoveRow(last, slot, meta);
        Unsafe.Add(ref ids, last) = Tombstone;
        if (_hasManaged) ReleaseRows(last, 1);
        return false;
    }

    /// <summary>Move the entity of row <paramref name="from"/> (data, id, version, presence bits, meta slot) into row <paramref name="to"/>.</summary>
    private void MoveRow(int from, int to, EntityMeta[] meta)
    {
        var columns = Columns;
        var infos = _infos.AsSpan(0, columns.Length); // parallel arrays of equal length: no bounds checks below
        for (int c = 0; c < infos.Length; c++)
            CopyElement(infos[c], columns[c], from, columns[c], to);
        if (_bitColumns.Length != 0) CopyBits(from, to);
        if (_tracked) RaiseWords(this, from, this, to);
        // Rows below Count are within the id arrays, and ids of rows are valid meta indices.
        ref int ids = ref MemoryMarshal.GetArrayDataReference(EntityIds);
        ref int versions = ref MemoryMarshal.GetArrayDataReference(Versions);
        int id = Unsafe.Add(ref ids, from);
        Unsafe.Add(ref ids, to) = id;
        Unsafe.Add(ref versions, to) = Unsafe.Add(ref versions, from);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(meta), id).Slot = to;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseRows(int start, int count)
    {
        for (int i = 0; i < _infos.Length; i++)
            if (_infos[i].Managed)
                _infos[i].Clear(Columns[i], start, count);
    }

    /// <summary>The rows of a table at one moment (<see cref="Capture"/>): ids, versions, columns, presence bits.</summary>
    internal sealed class Rows(int count, int[] ids, int[] versions, Array[] columns, ulong[][] present, int[] absent)
    {
        public readonly int Count = count;
        public readonly int[] Ids = ids;
        public readonly int[] Versions = versions;
        public readonly Array[] Columns = columns;
        public readonly ulong[][] Present = present;
        public readonly int[] Absent = absent;
    }

    /// <summary>Copy the rows (a dense table: outside passes). O(rows × columns). Inside the structural gate.</summary>
    public Rows Capture()
    {
        int count = Count;
        var columns = new Array[Columns.Length];
        var present = new ulong[Columns.Length][];
        for (int c = 0; c < columns.Length; c++)
        {
            int size = _infos[c].Size;
            if (size < 0)
            {
                columns[c] = _infos[c].Create(count);
                Array.Copy(Columns[c], columns[c], count);
            }
            else
            {
                var bytes = new byte[count * size];
                Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetArrayDataReference(bytes), ref Aligned(Columns[c]), (uint)bytes.Length);
                columns[c] = bytes;
            }

            if (Present[c] is { } bits) present[c] = bits.AsSpan(0, (count + 63) >> 6).ToArray();
        }

        return new Rows(count, EntityIds.AsSpan(0, count).ToArray(), Versions.AsSpan(0, count).ToArray(), columns, present,
            (int[])Absent.Clone());
    }

    /// <summary>
    /// Put the rows of <paramref name="rows"/> back (null: empty the table). The entity records are restored by the
    /// world. O(rows × columns). Inside the structural gate, outside passes.
    /// </summary>
    public void Restore(Rows rows)
    {
        int count = rows?.Count ?? 0;
        if (_hasManaged && Count > count) ReleaseRows(count, Count - count);
        Reserve(count);
        if (rows != null)
        {
            rows.Ids.CopyTo(EntityIds, 0);
            rows.Versions.CopyTo(Versions, 0);
            for (int c = 0; c < Columns.Length; c++)
            {
                if (_infos[c].Size < 0) Array.Copy(rows.Columns[c], Columns[c], count);
                else
                    Unsafe.CopyBlockUnaligned(ref Aligned(Columns[c]), ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(rows.Columns[c])),
                        (uint)Unsafe.As<byte[]>(rows.Columns[c]).Length);
            }
        }

        EntityIds.AsSpan(count, Count > count ? Count - count : 0).Fill(Tombstone);
        for (int c = 0; c < Columns.Length; c++)
        {
            // A column that gained presence bits after the capture keeps them, every row present.
            var saved = rows?.Present[c];
            if (saved != null) saved.CopyTo(Bits(c) ?? AllocateBits(c), 0);
            else if (Bits(c) is { } bits) bits.AsSpan().Fill(ulong.MaxValue);
            Absent[c] = rows?.Absent[c] ?? 0;
        }

        Count = AliveCount = count;
        Dirty = false;
    }

    /// <summary>
    /// Close the tombstones by moving the last alive rows into them (O(tombstones x columns); row order does not
    /// matter for a table), rewriting the moved entities' slots. Never while a pass iterates.
    /// </summary>
    public void FillHoles(EntityMeta[] meta)
    {
        // Holes below AliveCount are exactly as many as alive rows at or above it: move those down, found with
        // vectorized scans (IndexOf / LastIndexOfAnyExcept on the id array) instead of a test per row.
        var ids = EntityIds.AsSpan(0, Count);
        int alive = AliveCount;
        int end = ids.Length; // alive rows still to move live in [alive, end)
        for (int hole = 0; ; hole++)
        {
            int found = ids.Slice(hole, alive - hole).IndexOf(Tombstone);
            if (found < 0) break;
            hole += found;
            end = alive + ids.Slice(alive, end - alive).LastIndexOfAnyExcept(Tombstone);
            MoveRow(end, hole, meta);
            ids[end] = Tombstone;
        }

        if (_hasManaged && ids.Length > alive) ReleaseRows(alive, ids.Length - alive);
        Count = alive;
        Dirty = false;
    }

    /// <summary>
    /// Copy one element between two columns of the same component type. Unmanaged elements move as bytes with
    /// fixed-width loads for the common sizes (no memmove call); managed ones through their typed copy.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyElement(ComponentInfo info, Array source, int sourceSlot, Array target, int targetSlot)
    {
        int size = info.Size;
        if (size < 0)
        {
            info.CopyManaged(source, sourceSlot, target, targetSlot);
            return;
        }

        ref byte from = ref Unsafe.Add(ref Aligned(source), (nint)sourceSlot * size);
        ref byte to = ref Unsafe.Add(ref Aligned(target), (nint)targetSlot * size);
        switch (size)
        {
            case 1:
                to = from;
                break;
            case 2:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<short>(ref from));
                break;
            case 4:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<int>(ref from));
                break;
            case 8:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<long>(ref from));
                break;
            case 12:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<long>(ref from));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, 8), Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref from, 8)));
                break;
            case 16:
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Vector128<byte>>(ref from));
                break;
            default:
                Unsafe.CopyBlockUnaligned(ref to, ref from, (uint)size);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Transition GetAddEdge(int bit) => bit < _addEdges.Length ? _addEdges[bit] : null;

    public void SetAddEdge(int bit, Transition target) => SetEdge(ref _addEdges, bit, target);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Transition GetAddSetEdge(int signature) => signature < _addSetEdges.Length ? _addSetEdges[signature] : null;

    public void SetAddSetEdge(int signature, Transition target) => SetEdge(ref _addSetEdges, signature, target);

    private static void SetEdge(ref Transition[] edges, int index, Transition target)
    {
        if (index >= edges.Length)
            Array.Resize(ref edges, Math.Max(index + 1, edges.Length * 2));
        edges[index] = target;
    }

    /// <summary>Description of column <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentInfo Info(int index) => _infos[index];
}

/// <summary>
/// A cached move from one archetype to another: the target and the copy plan of the components both have (pairs of
/// column indices), computed once per edge so a structural change copies the row without searching columns.
/// </summary>
internal sealed class Transition
{
    public readonly Archetype Source;
    public readonly Archetype Target;

    // One step per shared component (source column, target column, its description).
    private readonly Step[] _plan;

    // Whether the tables share change-tick columns: a moved row raises the target's summaries to the source's.
    private readonly bool _ticks;

    private readonly struct Step(int from, int to, ComponentInfo info)
    {
        public readonly int From = from;
        public readonly int To = to;
        public readonly ComponentInfo Info = info;
    }

    public Transition(Archetype source, Archetype target)
    {
        Source = source;
        Target = target;
        var plan = new System.Collections.Generic.List<Step>();
        for (int i = 0; i < source.Components.Length; i++)
        {
            int to = target.ColumnIndex(source.Components[i]);
            if (to >= 0) plan.Add(new Step(i, to, source.Info(i)));
        }

        _plan = plan.ToArray();
        foreach (var step in _plan) _ticks |= step.Info.Owner >= 0;
    }

    /// <summary>
    /// Copy the shared components of row <paramref name="slot"/> into row <paramref name="targetSlot"/> of the target,
    /// and which of them the row lacks (cleared presence bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyRow(int slot, int targetSlot)
    {
        // Step columns are valid indices of both archetypes' Columns (built from them above): no bounds checks.
        ref Array from = ref MemoryMarshal.GetArrayDataReference(Source.Columns);
        ref Array to = ref MemoryMarshal.GetArrayDataReference(Target.Columns);
        foreach (ref readonly var step in _plan.AsSpan())
            Archetype.CopyElement(step.Info, Unsafe.Add(ref from, step.From), slot, Unsafe.Add(ref to, step.To), targetSlot);
        if (Source.HasAbsent) CopyAbsent(slot, targetSlot);
        if (_ticks) Archetype.RaiseWords(Source, slot, Target, targetSlot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopyAbsent(int slot, int targetSlot)
    {
        foreach (ref readonly var step in _plan.AsSpan())
            if (!Source.Has(step.From, slot))
                Target.Take(step.To, targetSlot);
    }
}
