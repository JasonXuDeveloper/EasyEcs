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
/// iterates, it becomes a tombstone (a negative id, the complement of the entity's) instead, so the pass never sees rows shift under it; the world closes those
/// tombstones (<see cref="FillHoles"/>) when the last pass ends. The slot of each entity is tracked by the owning
/// <see cref="World"/> (entity id -> slot).
/// <br/>
/// Removing a component keeps the entity in its table: the row only clears the component's presence bit
/// (<see cref="Present"/>), so a state that comes and goes (stunned, burning, dead) never moves rows. Only adding a
/// component the table has no column for moves the entity.
/// </summary>
internal sealed class Archetype
{
    public readonly BitMask ComponentMask;

    /// <summary>Component bits of this archetype, ascending.</summary>
    public readonly ushort[] Components;

    /// <summary>Component columns, parallel to <see cref="Components"/>; each has <see cref="Slots"/>.Length rows.</summary>
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

    /// <summary>Whether any column is a <see cref="DynamicBuffer{T}"/> (its lists go back to their pool when a row dies).</summary>
    private readonly bool _hasBuffers;

    /// <summary>A save of entities' mark (World._saveStamp when it holds some of them) and its index among the save's tables.</summary>
    internal int SaveMark, SaveIndex;

    /// <summary>
    /// The signature of this table's components as the components a load puts on entities of another world
    /// (<see cref="World.LoadEntitiesInto"/>): it keys their tables' add edges like a Set of those types. -1 until then.
    /// </summary>
    internal int LoadSignature = -1;

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
    /// Per change-tick column (null for others): the summary of each 64-row word (<see cref="WordTicks"/>), one entry
    /// per word so that a <c>Changed</c> filter or a write reads one cache line for it, not one per summary.
    /// </summary>
    public readonly WordTicks[][] Words;

    /// <summary>
    /// Per change-tick column (null for others): the summary of the <see cref="WordTicks.Write"/> ticks of each block of
    /// 64 words (4,096 rows). A <c>Changed</c> filter skips a block older than the pass's tick, and finds the next
    /// written word of the others with one bit scan (<see cref="BlockTicks"/>).
    /// </summary>
    public readonly BlockTicks[][] Blocks;

    /// <summary>
    /// Per change-tick column: <see cref="Blocks"/> for the <see cref="WordTicks.Removed"/> ticks, which a
    /// <c>Removed</c> filter skips words by: writes of the component do not make it visit their words.
    /// </summary>
    public readonly BlockTicks[][] Removals;

    private readonly bool _tracked; // has change-tick columns

    /// <summary>Whether the table has change-tick columns (an <see cref="ITracked"/> component).</summary>
    public bool Tracked => _tracked;

    /// <summary>Whether the table has a <see cref="Disabled{T}"/> column (rows may keep a disabled component).</summary>
    public readonly bool HasDisabled;

    /// <summary>Whether the table has an <see cref="ICleanupComponent"/> column (destroying a row may keep it).</summary>
    public readonly bool HasCleanup;

    /// <summary>Whether a column is a unique component (<see cref="IUniqueIndexed{TKey}"/>): copies of its rows claim their keys.</summary>
    public readonly bool Unique;

    /// <summary>
    /// The group of each <see cref="IGroup"/> column (by column; 0 for other columns), or null when the table has none
    /// set: every row's grouped component is in this group (<see cref="World.GroupBy{T}"/>).
    /// </summary>
    public readonly int[] Groups;

    // Tables of the same components in another group, by (column, group): the moves of World.Regroup. Created on use.
    private System.Collections.Generic.Dictionary<long, Transition> _regroupEdges;

    /// <summary>Columns with <see cref="Present"/> bits (indices into <see cref="Columns"/>), copy-on-write.</summary>
    private short[] _bitColumns = [];

    public EntityId[] Slots; // the entity of each row (id and version: handles without a meta lookup)
    public int Count; // rows in use, tombstones included
    public int AliveCount; // living entities only

    /// <summary>Has tombstones that the world closes when the last pass ends (registered once, see World.Remove).</summary>
    public bool Dirty;

    // The rows made tombstones since the table was last dense, so FillHoles visits only them instead of scanning every
    // row for holes. Reused (it grows to the most tombstones a table held at once, at most its rows).
    private int[] _holes = [];
    private int _holeCount;

    // Transition cache: component bit -> transition reached by adding that component (removing never moves rows).
    // Avoids hashing a 256-bit BitMask on every single-component structural change.
    private Transition[] _addEdges = [];

    // Same for multi-component adds (Set(a, b), CreateEntity(a, b, ...)), indexed by the dense index of the generic
    // signature (TypeIndex<ComponentSignature, (T1, ..., Tn)>): no mask cloning or hashing per call.
    private Transition[] _addSetEdges = [];

    private const int Tombstone = -1;

    /// <summary>Rows of a new table, and the fewest a trimmed one keeps (<see cref="Trim"/>).</summary>
    public const int InitialCapacity = 64;

    public Archetype(in BitMask componentMask, int[] groups = null, int initialCapacity = InitialCapacity)
    {
        ComponentMask = componentMask;
        Groups = groups;
        Components = componentMask.ToBitArray();
        Slots = new EntityId[initialCapacity];

        Columns = new Array[Components.Length];
        Present = new ulong[Components.Length][];
        Absent = new int[Components.Length];
        LastWrite = new ulong[Components.Length];
        Words = new WordTicks[Components.Length][];
        Blocks = new BlockTicks[Components.Length][];
        Removals = new BlockTicks[Components.Length][];
        _infos = new ComponentInfo[Components.Length];
        _columnOf = new short[Components.Length == 0 ? 0 : Components[^1] + 1];
        _columnOf.AsSpan().Fill(-1);
        ByBit = new Lookup[_columnOf.Length];
        for (int i = 0; i < Components.Length; i++)
        {
            var info = ComponentInfo.Of(Components[i]);
            _infos[i] = info;
            _hasManaged |= info.Managed;
            _hasBuffers |= info.IsBuffer;
            Columns[i] = info.Create(initialCapacity);
            if (info.Owner >= 0)
            {
                Words[i] = new WordTicks[(initialCapacity + 63) >> 6];
                Blocks[i] = new BlockTicks[(initialCapacity + 4095) >> 12];
                Removals[i] = new BlockTicks[(initialCapacity + 4095) >> 12];
            }

            _tracked |= info.Owner >= 0;
            HasDisabled |= info.DisabledOf >= 0;
            HasCleanup |= info.Cleanup;
            Unique |= info.Unique;
            _columnOf[Components[i]] = (short)i;
            ByBit[Components[i]].Set(Columns[i], info.Managed);
        }
    }

    /// <summary>
    /// A column, its presence bits and the address of its row 0 (<see cref="ByBit"/>). 32 bytes: an entry never
    /// straddles a cache line.
    /// </summary>
    public struct Lookup
    {
        public Array Column;
        public ulong[] Bits;

        /// <summary>
        /// Row 0 of an unmanaged column (<see cref="Aligned"/>): the column is pinned, so the address stays valid while
        /// <see cref="Column"/> holds it. A handle's access then needs no column header or alignment arithmetic. 0 for
        /// a managed column.
        /// </summary>
        public nint Rows;

        private nint _pad;

        /// <summary>Point the entry at <paramref name="column"/> (inside the structural gate).</summary>
        public unsafe void Set(Array column, bool managed)
        {
            Column = column;
            Rows = managed ? 0 : (nint)Unsafe.AsPointer(ref Aligned(column));
        }
    }

    /// <summary>Presence bits of column <paramref name="column"/> (a valid index), or null when every row has it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong[] Bits(int column) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Present), column);

    /// <summary>
    /// The presence word of row <paramref name="slot"/> in <paramref name="bits"/> (a column's presence bits). No bounds
    /// check: presence bits cover the table's capacity (allocated for Slots.Length rows, resized with it in Resize),
    /// and a slot is a row of the table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong Word(ulong[] bits, int slot) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), slot >> 6);

    /// <summary>Alive rows of column <paramref name="column"/> (a valid index: Absent has one entry per column) without it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int AbsentOf(int column) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Absent), column);

    /// <summary>Whether any column has rows without its component.</summary>
    public bool HasAbsent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _bitColumns.Length != 0;
    }

    /// <summary>Whether row <paramref name="slot"/> has the component of column <paramref name="column"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(int column, int slot) => Bits(column) is not { } bits || (Word(bits, slot) & (1UL << slot)) != 0; // shift is mod 64

    /// <summary>The group of grouped component <paramref name="bit"/> in this table (0 when it has no such column or no groups).</summary>
    public int GroupOf(int bit) => Groups != null && ColumnIndex(bit) is var column and >= 0 ? Groups[column] : 0;

    /// <summary>The cached move to this table's sibling with group <paramref name="group"/> in column <paramref name="column"/>.</summary>
    public Transition GetRegroupEdge(int column, int group) =>
        _regroupEdges != null && _regroupEdges.TryGetValue((long)column << 32 | (uint)group, out var edge) ? edge : null;

    public void SetRegroupEdge(int column, int group, Transition edge) =>
        (_regroupEdges ??= new())[(long)column << 32 | (uint)group] = edge;

    /// <summary>Whether row <paramref name="slot"/> has a cleanup component (<see cref="ICleanupComponent"/>).</summary>
    public bool HoldsCleanup(int slot)
    {
        for (int c = 0; c < _infos.Length; c++)
            if (_infos[c].Cleanup && Has(c, slot))
                return true;
        return false;
    }

    /// <summary>
    /// Rows [<paramref name="to"/>, +<paramref name="count"/>) lose their cleanup components as if they never had them:
    /// absent (managed values discarded) and never written, so no removal is reported (cleanup components cannot be
    /// disabled: no tag goes with them). A moved entity's cleanup components stay behind (<see cref="World.MoveEntities"/>),
    /// so its copy in the target world does not release the resource a second time. Inside the structural gate.
    /// </summary>
    public void DropCleanup(int to, int count)
    {
        for (int c = 0; c < _infos.Length; c++)
        {
            var info = _infos[c];
            if (!info.Cleanup) continue;
            for (int row = to; row < to + count; row++) MarkAbsent(c, row);
            if (Tracked && info.Companion is var ticks and >= 0) ClearTicks(ColumnIndex(ticks), to, count);
        }
    }

    /// <summary>
    /// Give row <paramref name="slot"/> the component of column <paramref name="column"/> back (its column exists).
    /// Returns true when the row lacked it. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MarkPresent(int column, int slot)
    {
        if (Bits(column) is not { } bits) return false;
        ref ulong word = ref Word(bits, slot);
        ulong bit = 1UL << slot;
        if ((word & bit) != 0) return false;
        word |= bit;
        AbsentOf(column)--;
        return true;
    }

    /// <summary>
    /// Mark row <paramref name="slot"/> as lacking the component of column <paramref name="column"/>: clear its presence bit
    /// (and a managed value, so the GC can collect what it referenced, unless <paramref name="keep"/>: a disabled
    /// component keeps its value). Returns true when the row had it. Inside the structural gate.
    /// </summary>
    public bool MarkAbsent(int column, int slot, bool keep = false)
    {
        ref ulong word = ref Word(Bits(column) ?? AllocateBits(column), slot);
        ulong bit = 1UL << slot;
        if ((word & bit) == 0) return false;
        word &= ~bit;
        AbsentOf(column)++;
        if (!keep && _hasManaged && Info(column) is { Managed: true } info) info.Discard(Columns[column], slot, 1);
        return true;
    }

    /// <summary>
    /// <see cref="MarkAbsent"/> for the rows of presence word <paramref name="word"/> set in <paramref name="rows"/>
    /// (rows of the table): a bulk change's 64 rows at once. Returns the rows that had the component. Inside the
    /// structural gate.
    /// </summary>
    public ulong MarkAbsentWord(int column, int word, ulong rows, bool keep = false)
    {
        ref ulong present = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Bits(column) ?? AllocateBits(column)), word);
        rows &= present;
        present &= ~rows;
        AbsentOf(column) += BitOperations.PopCount(rows);
        if (!keep && _hasManaged && Info(column) is { Managed: true } info)
            for (ulong r = rows; r != 0; r &= r - 1) info.Discard(Columns[column], (word << 6) + BitOperations.TrailingZeroCount(r), 1);
        return rows;
    }

    /// <summary><see cref="MarkPresent"/> for the rows of presence word <paramref name="word"/> set in <paramref name="rows"/>. Inside the structural gate.</summary>
    public void MarkPresentWord(int column, int word, ulong rows)
    {
        if (Bits(column) is not { } bits) return;
        ref ulong present = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(bits), word);
        AbsentOf(column) -= BitOperations.PopCount(rows & ~present);
        present |= rows;
    }

    /// <summary>A row dies (destroyed or moved away): it no longer counts as lacking components.</summary>
    private void ForgetAbsent(int slot)
    {
        foreach (short c in _bitColumns)
            if ((Word(Bits(c), slot) & (1UL << slot)) == 0)
                AbsentOf(c)--;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong[] AllocateBits(int column)
    {
        var bits = new ulong[(Slots.Length + 63) >> 6];
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
        foreach (short c in _bitColumns) Word(Bits(c), slot) |= 1UL << slot;
    }

    /// <summary>The change-tick column of tracked component <typeparamref name="T"/>, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TicksColumn<T>() where T : struct, IComponent => ColumnIndex(ComponentType<T>.TicksBit);

    /// <summary>Index of the column of component <paramref name="bit"/>, or -1 when the archetype does not have it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnIndex(int bit)
    {
        var map = _columnOf;
        // bit < map.Length: no bounds check.
        return (uint)bit < (uint)map.Length ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(map), bit) : -1;
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
    /// below the column length (columns have as many rows as <see cref="Slots"/>).
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
        if (default(T) is ITracked) Ticks<T>(tick, slot, 1UL << slot).Value = tick; // shift is mod 64
    }

    /// <summary>
    /// <see cref="Stamp{T}"/> for a pass that writes row after row (a query, a per-entity job): the row's word loses
    /// its exact rows, which such a pass would rebuild bit by bit, so readers compare the word's ticks (stores only).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampVisited<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked)
        {
            int column = TicksColumn<T>();
            NoteVisited(column, slot, tick);
            Unsafe.Add(ref Data<RowTick>(column), slot).Value = tick;
        }
    }

    /// <summary>
    /// The summaries of <see cref="StampVisited{T}"/> alone: they then cover any row of the word stamped at
    /// <paramref name="tick"/> with <see cref="StampRowTick{T}"/> (a pass notes each word once).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NoteVisited<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked) NoteVisited(TicksColumn<T>(), slot, tick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteVisited(int column, int slot, ulong tick)
    {
        // A pass stamps row after row: the word's first row at this tick notes the summaries, the others find Rest
        // at the tick (it is never above Write, nor Write above the world's tick) with one load.
        ref var word = ref Summary(Words, column, slot >> 6);
        if (word.Rest != tick) NoteVisitedWord(column, slot, tick, ref word);
    }

    private void NoteVisitedWord(int column, int slot, ulong tick, ref WordTicks word)
    {
        // Rest == Write: Rows is not read until a later tick notes the word again (NoteWord).
        word.Rest = tick;
        word.Write = tick;
        NoteBlock(column, slot >> 12, 1UL << (slot >> 6), tick);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(LastWrite), column) = tick;
    }

    /// <summary>
    /// Entry <paramref name="index"/> of the summaries of change-tick column <paramref name="column"/>, without bounds
    /// checks. Invariant: the column comes from <see cref="ColumnIndex"/> of a change-tick bit the table has (so its
    /// summaries exist), and the index is of a row below the capacity (summaries are resized with the rows, <c>Resize</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref TSummary Summary<TSummary>(TSummary[][] summaries, int column, int index) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(summaries), column)), index);

    /// <summary>The row's change tick alone; its word must have been noted at <paramref name="tick"/> (<see cref="NoteVisited{T}"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampRowTick<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked)
            Unsafe.Add(ref Data<RowTick>(TicksColumn<T>()), slot).Value = tick;
    }

    /// <summary>
    /// Row <paramref name="slot"/> of the change ticks of <typeparamref name="T"/>, noting a write at
    /// <paramref name="tick"/> of rows <paramref name="rows"/> of the row's word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref RowTick Ticks<T>(ulong tick, int slot, ulong rows) where T : struct, IComponent
    {
        int column = TicksColumn<T>();
        // Summaries only grow (LastWrite >= the block's Latest >= Write) and no row holds a later tick than the world's: a
        // word already at this tick has its block and column at it too, so only its rows change.
        ref var word = ref Summary(Words, column, slot >> 6);
        if (word.Write == tick) word.Rows |= rows;
        else NoteWord(column, slot, rows, tick);
        return ref Unsafe.Add(ref Data<RowTick>(column), slot);
    }

    /// <summary>
    /// Rows <paramref name="rows"/> of the word of <paramref name="slot"/> get tick <paramref name="tick"/>, the world's
    /// current one (no row of the column holds a later tick). Keeps the word's summary exact (<see cref="WordTicks"/>).
    /// </summary>
    private void NoteWord(int column, int slot, ulong rows, ulong tick)
    {
        ref var word = ref Summary(Words, column, slot >> 6);
        if (word.Write == tick) word.Rows |= rows;
        else
        {
            word.Rest = word.Write; // every row of the word held at most the previous latest tick
            word.Write = tick;
            word.Rows = rows;
            NoteBlock(column, slot >> 12, 1UL << (slot >> 6), tick);
        }

        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(LastWrite), column) = tick;
    }

    /// <summary>
    /// Raise the change-tick summaries (word, block, column) of row <paramref name="to"/> of <paramref name="target"/> to
    /// those of row <paramref name="from"/> of <paramref name="source"/> (a row copy).
    /// </summary>
    internal static void RaiseWords(Archetype source, int from, Archetype target, int to)
    {
        for (int c = 0; c < source.Words.Length; c++)
        {
            if (source.Words[c] is null) continue;
            int t = target.ColumnIndex(source.Components[c]);
            if (t < 0) continue;
            // The copied tick (the row was copied first); it may be older than the target word's latest.
            target.NoteTick(t, to, Unsafe.Add(ref Unsafe.As<RowTick, ulong>(ref target.Data<RowTick>(t)), to));
        }
    }

    /// <summary>
    /// Set row <paramref name="slot"/> of change-tick column <paramref name="column"/> to <paramref name="tick"/>, which
    /// may be older than its word's latest (a copied or cleared row), keeping the summaries exact.
    /// </summary>
    public void SetTick(int column, int slot, ulong tick)
    {
        Unsafe.Add(ref Unsafe.As<RowTick, ulong>(ref Data<RowTick>(column)), slot) = tick;
        NoteTick(column, slot, tick);
    }

    /// <summary>The summaries of change-tick column <paramref name="column"/> after row <paramref name="slot"/> got <paramref name="tick"/>.</summary>
    private void NoteTick(int column, int slot, ulong tick)
    {
        int w = slot >> 6;
        ulong bit = 1UL << slot; // shift is mod 64
        ref var word = ref Words[column][w];
        if (tick > word.Write)
        {
            word.Rest = word.Write;
            word.Write = tick;
            word.Rows = bit;
            NoteBlock(column, slot >> 12, 1UL << w, tick);
        }
        else if (tick == word.Write) word.Rows |= bit;
        else
        {
            word.Rows &= ~bit;
            word.Rest = Math.Max(word.Rest, tick);
        }

        NoteRemoved(column, slot, tick); // the row may lack the component (a bound: rows that have it only cost a visit)

        LastWrite[column] = Math.Max(LastWrite[column], tick);
    }

    /// <summary><see cref="Stamp{T}"/> for rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampRows<T>(int start, int count, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked && count > 0) StampRange(TicksColumn<T>(), start, count, tick);
    }

    /// <summary>
    /// Rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>) (at least one) of change-tick
    /// column <paramref name="column"/> get <paramref name="tick"/>, the world's current one (no row holds a later tick).
    /// </summary>
    private void StampRange(int column, int start, int count, ulong tick)
    {
        int end = start + count;
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<RowTick, ulong>(ref Data<RowTick>(column)), start), count).Fill(tick);
        ulong rows = RunMask(start, end, start >> 6);
        ref var head = ref Summary(Words, column, start >> 6);
        if (head.Write == tick) head.Rows |= rows;
        else NoteWord(column, start, rows, tick);
        int first = (start >> 6) + 1, last = (end - 1) >> 6;
        if (last >= first)
        {
            // Whole words between the first and the last: every row gets the tick, so the rest bound may be any
            // tick up to it (the previous latest is one); the last word may be partial.
            foreach (ref var word in Words[column].AsSpan(first, last - first))
                word = new WordTicks { Write = tick, Rest = word.Write, Rows = ulong.MaxValue };
            for (int w = first; w < last; w = (w | 63) + 1) // per block, its words of [first, last)
                NoteBlock(column, w >> 6, RunMask(w, Math.Min(last, (w | 63) + 1), w >> 6), tick);
            NoteWord(column, last << 6, RunMask(start, end, last), tick);
        }
    }

    /// <summary>
    /// Words <paramref name="words"/> (bits) of block <paramref name="block"/> of change-tick column
    /// <paramref name="column"/> got <see cref="WordTicks.Write"/> <paramref name="tick"/> (raised, never lowered).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteBlock(int column, int block, ulong words, ulong tick) => Summary(Blocks, column, block).Raise(words, tick);

    /// <summary>
    /// Row <paramref name="slot"/> of change-tick column <paramref name="column"/> got <paramref name="tick"/> while it
    /// may lack the component (a removal, a copied row, a restore): its word's <see cref="WordTicks.Removed"/> bound and
    /// the block's <see cref="Removals"/> are raised to it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteRemoved(int column, int slot, ulong tick)
    {
        ref var word = ref Summary(Words, column, slot >> 6);
        if (tick <= word.Removed) return;
        word.Removed = tick;
        Summary(Removals, column, slot >> 12).Raise(1UL << (slot >> 6), tick); // shift is mod 64
    }

    /// <summary>
    /// <see cref="Stamp{T}"/> of a removal of <typeparamref name="T"/> from row <paramref name="slot"/>: the row's tick
    /// is the world's <paramref name="tick"/>, and <c>Removed</c> filters find its word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampRemoved<T>(int slot, ulong tick) where T : struct, IComponent
    {
        if (default(T) is not ITracked) return;
        Stamp<T>(slot, tick);
        NoteRemoved(TicksColumn<T>(), slot, tick);
    }

    /// <summary>
    /// Rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>) of change-tick column
    /// <paramref name="column"/> were never written (tick 0): new rows whose component is absent, not removed.
    /// </summary>
    private void ClearTicks(int column, int start, int count)
    {
        int end = start + count;
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<RowTick, ulong>(ref Data<RowTick>(column)), start), count).Clear();
        for (int w = start >> 6; w <= (end - 1) >> 6; w++) Words[column][w].Rows &= ~RunMask(start, end, w); // rest bounds stay
    }

    /// <summary>The rows of [<paramref name="start"/>, <paramref name="end"/>) in word <paramref name="word"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RunMask(int start, int end, int word)
    {
        int first = Math.Max(start - (word << 6), 0), last = Math.Min(end - (word << 6), 64); // rows [first, last)
        ulong upTo = last == 64 ? ulong.MaxValue : (1UL << last) - 1;
        return upTo & (ulong.MaxValue << first);
    }

    /// <summary>
    /// Clear every row of every change-tick column (after a load that leaves the ticks alone): no row counts as written
    /// or removed, at any tick. Cold path.
    /// </summary>
    public void ClearTracked()
    {
        if (Count == 0) return;
        for (int c = 0; c < Components.Length; c++)
            if (_infos[c].Owner >= 0) ClearTicks(c, 0, Count);
    }

    /// <summary>Stamp every row of every change-tick column at <paramref name="tick"/> (after a restore). Cold path.</summary>
    public void StampTracked(ulong tick)
    {
        if (Count == 0) return;
        for (int c = 0; c < Components.Length; c++)
            if (_infos[c].Owner >= 0)
            {
                MemoryMarshal.CreateSpan(ref Unsafe.As<RowTick, ulong>(ref Data<RowTick>(c)), Count).Fill(tick);
                for (int w = 0; w <= (Count - 1) >> 6; w++)
                {
                    NoteWord(c, w << 6, RunMask(0, Count, w), tick);
                    NoteRemoved(c, w << 6, tick); // rows lacking the component count as removed, like the others as written
                }
                LastWrite[c] = tick;
            }
    }

    /// <summary>
    /// Open the change-tick blocks of <typeparamref name="T"/> holding rows [<paramref name="start"/>,
    /// <paramref name="end"/>) at <paramref name="tick"/> (<see cref="BlockTicks.Open"/>), before a parallel pass whose
    /// chunks split blocks stamps them. Nothing for an untracked component (a JIT-time constant).
    /// </summary>
    public void OpenBlocks<T>(int start, int end, ulong tick) where T : struct, IComponent
    {
        int column;
        if (default(T) is not ITracked || start >= end || (column = TicksColumn<T>()) < 0) return; // a Maybe<T> the table lacks
        var blocks = Blocks[column];
        for (int b = start >> 12; b <= (end - 1) >> 12; b++) blocks[b].Open(tick);
    }

    /// <summary><see cref="Stamp{T}"/> for the rows of word <paramref name="word"/> whose bit is set in <paramref name="rows"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StampWord<T>(int word, ulong rows, ulong tick) where T : struct, IComponent
    {
        if (default(T) is not ITracked || rows == 0) return;
        ref var ticks = ref Ticks<T>(tick, word << 6, rows);
        for (; rows != 0; rows &= rows - 1) Unsafe.Add(ref ticks, BitOperations.TrailingZeroCount(rows)).Value = tick;
    }

    /// <summary>
    /// Copy row <paramref name="from"/> of <paramref name="source"/>, a table with the same components or a subset of
    /// them (of this world or a prefab's), into the <paramref name="count"/> (at least one) new rows from
    /// <paramref name="to"/>: values (each dynamic buffer gets a list of its own), the components the row lacks, and
    /// change ticks: a component the row has counts as written at <paramref name="tick"/>, one it lacks was never removed
    /// here. Columns <paramref name="source"/> does not have (and their change ticks) are left for the caller to write
    /// (Instantiate with values). Unmanaged values are copied into the first row, then into the others by doubling
    /// copies. Inside the structural gate.
    /// </summary>
    public void CloneRows(Archetype source, int from, int to, int count, ulong tick)
    {
        // Same column count: the same components, so the same columns (both sorted by bit); else map by bit.
        bool same = source.Columns.Length == Columns.Length;
        for (int c = 0; c < Columns.Length; c++)
        {
            int s = same ? c : source.ColumnIndex(Components[c]);
            if (s < 0) continue; // a component the caller gives
            var info = Info(c);
            if (info.Owner >= 0)
            {
                if (source.Has(same ? ColumnIndex(info.Owner) : source.ColumnIndex(info.Owner), from)) StampRange(c, to, count, tick);
                else ClearTicks(c, to, count);
                continue;
            }

            bool present = source.Has(s, from);
            if (!present && !(source.HasDisabled && source.Keeps(s, from)))
            {
                for (int row = to; row < to + count; row++) MarkAbsent(c, row);
                continue;
            }

            if (info.Managed)
                for (int row = to; row < to + count; row++) info.CloneManaged(source.Columns[s], from, Columns[c], row);
            else
            {
                ref byte first = ref Unsafe.Add(ref Aligned(Columns[c]), (nint)to * info.Size);
                CopyBytes(ref Unsafe.Add(ref Aligned(source.Columns[s]), (nint)from * info.Size), ref first, info.Size);
                // Rows [to, to + done) hold the value: copy them after themselves until every row does
                for (nint done = info.Size, total = (nint)count * info.Size; done < total; done *= 2)
                    Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref first, done), ref first, (uint)Math.Min(done, total - done));
            }

            if (!present) // disabled: the copies keep the value too
                for (int row = to; row < to + count; row++) MarkAbsent(c, row, keep: true);
        }
    }

    /// <summary>
    /// Copy rows [<paramref name="from"/>, +<paramref name="count"/>) of <paramref name="source"/>, a table with the same
    /// components (of any world: columns line up, bits are global), into the new rows from <paramref name="to"/>: as
    /// <see cref="CloneRows"/> does for one row, with one block copy per unmanaged column. With <paramref name="take"/>
    /// the source's buffer lists move instead of being copied, and its rows are left without them (a load's private
    /// world, whose rows serve nothing else). Inside the structural gate.
    /// </summary>
    public void CopyRun(Archetype source, int from, int to, int count, ulong tick, bool take = false)
    {
        for (int c = 0; c < Columns.Length; c++)
        {
            var info = Info(c);
            if (info.Owner >= 0)
            {
                int owner = ColumnIndex(info.Owner);
                if (source.Bits(owner) == null) StampRange(c, to, count, tick);
                else
                    for (int r = 0; r < count; r++)
                        if (source.Has(owner, from + r)) StampRange(c, to + r, 1, tick);
                        else ClearTicks(c, to + r, 1);
                continue;
            }

            bool all = source.Bits(c) == null; // every source row has the component
            if (info.Managed)
                for (int r = 0; r < count; r++)
                {
                    if (!all && !source.Has(c, from + r) && !(source.HasDisabled && source.Keeps(c, from + r))) continue;
                    if (!take || !info.IsBuffer) info.CloneManaged(source.Columns[c], from + r, Columns[c], to + r);
                    else
                    {
                        info.CopyManaged(source.Columns[c], from + r, Columns[c], to + r);
                        info.Clear(source.Columns[c], from + r, 1); // the list moved: the next load takes another
                    }
                }
            else
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref Aligned(Columns[c]), (nint)to * info.Size),
                    ref Unsafe.Add(ref Aligned(source.Columns[c]), (nint)from * info.Size), (uint)(count * info.Size));

            if (all) continue;
            for (int r = 0; r < count; r++)
                if (!source.Has(c, from + r)) MarkAbsent(c, to + r, keep: source.HasDisabled && source.Keeps(c, from + r));
        }
    }

    /// <summary>Whether row <paramref name="slot"/> keeps the component of <paramref name="column"/> disabled (its <see cref="Disabled{T}"/> tag is present).</summary>
    public bool Keeps(int column, int slot) => DisablerColumn(column) is var tag and >= 0 && Has(tag, slot);

    /// <summary>The column of the <see cref="Disabled{T}"/> tag of the component of <paramref name="column"/>, or -1 when this table has none.</summary>
    public int DisablerColumn(int column) => Info(column).Disabler is var tag and >= 0 ? ColumnIndex(tag) : -1;

    /// <summary>
    /// Empty the table at once (every row is destroyed; no pass is in progress and it has no tombstones): the rows'
    /// entities must be freed by the caller first. Managed values are cleared for the GC. Inside the structural gate.
    /// </summary>
    public void Clear()
    {
        if (_hasManaged) ReleaseRows(0, Count);
        foreach (ref var row in Slots.AsSpan(0, Count)) row = new EntityId(Tombstone, row.Version); // versions stay, as Remove leaves them
        Count = 0;
        AliveCount = 0;
        if (HasAbsent) Absent.AsSpan().Clear();
    }

    /// <summary>Append an entity and return its row (a pass that started earlier does not visit it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Add(int entityId, int version)
    {
        if (Count >= Slots.Length) Resize(Slots.Length * 2);
        int slot = Count++;
        // slot < Slots.Length (grown above): no bounds check.
        ref var row = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Slots), slot);
        row = new EntityId(entityId, version);
        AliveCount++;
        if (HasAbsent) SetBits(slot); // a transition then clears what the entity lacks
        return slot;
    }

    /// <summary>
    /// Next run of alive rows at or after <paramref name="start"/> among the first <paramref name="count"/> rows:
    /// returns false when there is none, else sets <paramref name="start"/> and <paramref name="length"/>. Both scans
    /// are vectorized (range searches for negative rows in <see cref="Rows64"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NextRun(ref int start, int count, out int length)
    {
        var rows = Rows64(count);
        int skip = rows.Slice(start).IndexOfAnyExceptInRange(long.MinValue, -1);
        if (skip < 0)
        {
            length = 0;
            return false;
        }

        start += skip;
        length = rows.Slice(start).IndexOfAnyInRange(long.MinValue, -1);
        if (length < 0) length = count - start;
        return true;
    }

    /// <summary>
    /// The first <paramref name="count"/> rows (at most the capacity) as <c>long</c>s: negative exactly for the
    /// tombstones (the id is the high half, see <see cref="EntityId"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<long> Rows64(int count) => MemoryMarshal.Cast<EntityId, long>(Slots.AsSpan(0, count));

    /// <summary>
    /// Make room for <paramref name="rows"/> rows in one growth (bulk creation, pre-sizing): to <paramref name="rows"/>
    /// rounded up to whole presence words (64 rows), or to twice the capacity when that is more, so tables that grow by
    /// many reservations still double. Not to the next power of two: the 100,000 rows of a start-up are 100,032 rows,
    /// not 131,072 (each column and the entity rows are large-object allocations, which count toward the full
    /// collections a process pays for; see decision D28, docs/content/decisions.mdx).
    /// </summary>
    public void Reserve(int rows)
    {
        if (rows > Slots.Length) Resize(Math.Max((rows + 63) & ~63, Slots.Length * 2));
    }

    /// <summary>
    /// Give up the capacity a peak left: shrink the entity rows, the columns, presence bits and change-tick summaries
    /// to <see cref="Count"/> rows rounded up to a power of two (at least <see cref="InitialCapacity"/>), when that is
    /// less than the capacity, and the tombstone list. Returns whether the rows shrank. Rows past the count hold nothing
    /// a reader uses (presence bits and summaries past it are meaningless, a new row sets its own), so they go. Inside the
    /// structural gate, outside passes (the table is dense). Cold path.
    /// </summary>
    public bool Trim()
    {
        if (_holeCount == 0) _holes = [];
        long capacity = Math.Max(InitialCapacity, (long)BitOperations.RoundUpToPowerOf2((ulong)Count));
        if (capacity >= Slots.Length) return false;
        Resize((int)capacity);
        return true;
    }

    /// <summary>
    /// Resize the entity rows (<see cref="Slots"/>) and every column to <paramref name="capacity"/> rows, at least
    /// <see cref="Count"/> (cold path: growth, <see cref="Trim"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Resize(int capacity)
    {
        Array.Resize(ref Slots, capacity);
        for (int i = 0; i < Columns.Length; i++)
            ByBit[Components[i]].Set(Columns[i] = _infos[i].Resize(Columns[i], capacity), _infos[i].Managed);
        foreach (short c in _bitColumns)
        {
            Array.Resize(ref Present[c], (capacity + 63) >> 6);
            ByBit[Components[c]].Bits = Present[c];
        }

        for (int c = 0; c < Words.Length; c++)
            if (Words[c] != null)
            {
                Array.Resize(ref Words[c], (capacity + 63) >> 6);
                Array.Resize(ref Blocks[c], (capacity + 4095) >> 12);
                Array.Resize(ref Removals[c], (capacity + 4095) >> 12);
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
        if (HasAbsent) ForgetAbsent(slot);
        // slot < Count <= Slots.Length: no bounds checks.
        ref var rows = ref MemoryMarshal.GetArrayDataReference(Slots);
        if (inPass || Count != AliveCount + 1)
        {
            // ~id: the pass that removed it reads the id back (its loops keep no id across the job); the version stays:
            // a pass compares it.
            ref var row = ref Unsafe.Add(ref rows, slot);
            row = new EntityId(~row.Index, row.Version);
            if (_holeCount == _holes.Length) Array.Resize(ref _holes, Math.Max(16, _holes.Length * 2));
            _holes[_holeCount++] = slot; // a tombstone stays one until FillHoles (new rows go to the end)
            if (Dirty) return false;
            Dirty = true;
            return true;
        }

        int last = --Count;
        if (slot != last) MoveRow(last, slot, meta);
        Unsafe.Add(ref rows, last) = new EntityId(Tombstone, Unsafe.Add(ref rows, last).Version);
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
        if (HasAbsent) CopyBits(from, to);
        if (_tracked) RaiseWords(this, from, this, to);
        // Rows below Count are within Slots, and ids of rows are valid meta indices.
        ref var rows = ref MemoryMarshal.GetArrayDataReference(Slots);
        var entity = Unsafe.Add(ref rows, from);
        Unsafe.Add(ref rows, to) = entity;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(meta), entity.Index).Slot = to;
    }

    /// <summary>
    /// The entities of rows [<paramref name="start"/>, +<paramref name="count"/>) die: their buffers' lists go back to
    /// their pool (see <see cref="ComponentInfo.Discard"/>). Before the rows are removed. Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseBuffers(int start, int count)
    {
        if (_hasBuffers) ReleaseBufferColumns(start, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseBufferColumns(int start, int count)
    {
        for (int i = 0; i < _infos.Length; i++)
            if (_infos[i].IsBuffer)
                _infos[i].Discard(Columns[i], start, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseRows(int start, int count)
    {
        for (int i = 0; i < _infos.Length; i++)
            if (_infos[i].Managed)
                _infos[i].Clear(Columns[i], start, count);
    }

    /// <summary>
    /// The rows of a table at one moment (<see cref="Capture"/>): entities, columns (unmanaged ones as plain bytes),
    /// presence bits. The arrays may be longer than <see cref="Count"/> rows: a capture into an older copy reuses them.
    /// </summary>
    internal sealed class TableCopy(Archetype table)
    {
        /// <summary>The table the copy is of: a copy is reused for no other (tables shift when RemoveTypes drops some).</summary>
        public readonly Archetype Table = table;
        public int Count;
        public EntityId[] Slots = [];
        public readonly Array[] Columns = new Array[table.Columns.Length];
        public readonly ulong[][] Present = new ulong[table.Columns.Length][];
        public readonly int[] Absent = new int[table.Columns.Length];
        private readonly ulong[][] _spare = new ulong[table.Columns.Length][]; // presence bits dropped, kept for reuse

        /// <summary>Column <paramref name="c"/> has every row present again; its bits are kept for <see cref="TakeBits"/>.</summary>
        public void DropBits(int c)
        {
            if (Present[c] is { } bits) _spare[c] = bits;
            Present[c] = null;
        }

        /// <summary>Zeroed presence bits for column <paramref name="c"/>, at least <paramref name="words"/> long: the dropped ones when they fit.</summary>
        public ulong[] TakeBits(int c, int words)
        {
            var bits = _spare[c];
            _spare[c] = null;
            if (bits == null || bits.Length < words) return new ulong[words];
            bits.AsSpan().Clear();
            return bits;
        }
    }

    /// <summary>
    /// Copy the rows (a dense table: outside passes) into <paramref name="into"/> (an earlier copy, its arrays reused
    /// when large enough, if it is of this table) or a new copy. O(rows × columns). Inside the structural gate.
    /// </summary>
    public TableCopy Capture(TableCopy into)
    {
        int count = Count;
        if (into?.Table != this) into = null;
        var rows = into ?? new TableCopy(this);
        if (rows.Slots.Length < count) rows.Slots = new EntityId[count];
        Slots.AsSpan(0, count).CopyTo(rows.Slots);
        for (int c = 0; c < Columns.Length; c++)
        {
            var info = _infos[c];
            ref var column = ref rows.Columns[c];
            if (info.Size < 0)
            {
                int old = into?.Count ?? 0;
                if (column == null || column.Length < count) column = info.Create(count);
                else if (old > count) info.Clear(column, count, old - count); // no stale references kept alive
                info.CopyRows(Columns[c], column, count);
            }
            else
            {
                int bytes = count * info.Size;
                if (column == null || column.Length < bytes) column = new byte[bytes];
                Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(column)), ref Aligned(Columns[c]), (uint)bytes);
            }

            int words = (count + 63) >> 6;
            if (Present[c] is { } bits)
            {
                ref var saved = ref rows.Present[c];
                if (saved == null || saved.Length < words) saved = rows.TakeBits(c, words);
                bits.AsSpan(0, words).CopyTo(saved);
            }
            else rows.DropBits(c);
        }

        Absent.CopyTo(rows.Absent, 0);
        rows.Count = count;
        return rows;
    }

    /// <summary>
    /// Put the rows of <paramref name="rows"/> back (null: empty the table). The entity records are restored by the
    /// world. With <paramref name="swap"/> the lists of buffer columns change sides (see
    /// <see cref="ComponentInfo.CopyRows"/>). O(rows × columns). Inside the structural gate, outside passes.
    /// </summary>
    public void Restore(TableCopy rows, bool swap = false)
    {
        int count = rows?.Count ?? 0;
        if (_hasManaged && Count > count) ReleaseRows(count, Count - count);
        Reserve(count);
        if (rows != null)
        {
            rows.Slots.AsSpan(0, count).CopyTo(Slots);
            for (int c = 0; c < Columns.Length; c++)
            {
                if (_infos[c].Size < 0) _infos[c].CopyRows(rows.Columns[c], Columns[c], count, swap);
                else
                    Unsafe.CopyBlockUnaligned(ref Aligned(Columns[c]), ref MemoryMarshal.GetArrayDataReference(Unsafe.As<byte[]>(rows.Columns[c])),
                        (uint)(count * _infos[c].Size));
            }
        }

        foreach (ref var row in Slots.AsSpan(count, Math.Max(Count - count, 0))) row = new EntityId(Tombstone, row.Version);
        for (int c = 0; c < Columns.Length; c++)
        {
            // A column that gained presence bits after the capture keeps them, every row present.
            var saved = rows?.Present[c];
            if (saved != null) saved.AsSpan(0, (count + 63) >> 6).CopyTo(Bits(c) ?? AllocateBits(c));
            else if (Bits(c) is { } bits) bits.AsSpan().Fill(ulong.MaxValue);
            Absent[c] = rows?.Absent[c] ?? 0;
        }

        Count = AliveCount = count;
        _holeCount = 0;
        Dirty = false;
    }

    /// <summary>
    /// Close the tombstones by moving the last alive rows into them (O(tombstones x columns); row order does not
    /// matter for a table), rewriting the moved entities' slots. Never while a pass iterates.
    /// </summary>
    public void FillHoles(EntityMeta[] meta)
    {
        // Holes below AliveCount are exactly as many as alive rows at or above it: each takes the last of those, found
        // from the end (a vectorized range search for a non-negative row in Rows64). Holes at or above AliveCount fall
        // off with the end. The recorded holes are every tombstone below Count, each once.
        var rows = Rows64(Count);
        int alive = AliveCount;
        int end = rows.Length; // alive rows still to move live in [alive, end)
        var holes = _holes.AsSpan(0, _holeCount);
        for (int i = 0; i < holes.Length; i++)
        {
            int hole = holes[i];
            if (hole >= alive) continue;
            end = alive + rows.Slice(alive, end - alive).LastIndexOfAnyExceptInRange(long.MinValue, -1);
            MoveRow(end, hole, meta);
            Slots[end] = new EntityId(Tombstone, Slots[end].Version);
        }

        if (_hasManaged && rows.Length > alive) ReleaseRows(alive, rows.Length - alive);
        Count = alive;
        _holeCount = 0;
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

        CopyBytes(ref Unsafe.Add(ref Aligned(source), (nint)sourceSlot * size),
            ref Unsafe.Add(ref Aligned(target), (nint)targetSlot * size), size);
    }

    /// <summary>
    /// One unmanaged component of <paramref name="size"/> (at least 1) bytes, without calls: two overlapping loads and
    /// stores cover any size up to 16, larger ones copy 16-byte blocks and finish with an overlapping last block. No call
    /// keeps the loops that copy many components (CopyRow) free of spills around a memcpy. The ranges never overlap
    /// (different tables, or different rows of one column), so every load may come before its store.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyBytes(ref byte from, ref byte to, int size)
    {
        nint last = size;
        if (size > 16)
        {
            for (nint at = 0; at < last - 16; at += 16)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, at), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref from, at)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, last - 16), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref from, last - 16)));
        }
        else if (size >= 8)
        {
            long head = Unsafe.ReadUnaligned<long>(ref from), tail = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref from, last - 8));
            Unsafe.WriteUnaligned(ref to, head);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, last - 8), tail);
        }
        else if (size >= 4)
        {
            int head = Unsafe.ReadUnaligned<int>(ref from), tail = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref from, last - 4));
            Unsafe.WriteUnaligned(ref to, head);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, last - 4), tail);
        }
        else if (size >= 2)
        {
            short head = Unsafe.ReadUnaligned<short>(ref from), tail = Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref from, last - 2));
            Unsafe.WriteUnaligned(ref to, head);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, last - 2), tail);
        }
        else
        {
            to = from;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Transition GetAddEdge(int bit) => bit < _addEdges.Length ? _addEdges[bit] : null;

    /// <summary>Forget every cached transition (a table they lead to was dropped: World.RemoveTypes). Inside the gate.</summary>
    public void ClearEdges()
    {
        _addEdges = [];
        _addSetEdges = [];
        _regroupEdges = null;
    }

    /// <summary>
    /// Forget the cached transitions that lead to a table of <paramref name="dropped"/> (World.TrimExcess): the next
    /// change resolves them again, to a new table. Inside the gate.
    /// </summary>
    public void ForgetEdges(System.Collections.Generic.HashSet<Archetype> dropped)
    {
        Forget(_addEdges, dropped);
        Forget(_addSetEdges, dropped);
        if (_regroupEdges == null) return;
        foreach (var pair in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, Transition>>(_regroupEdges))
            if (dropped.Contains(pair.Value.Target))
                _regroupEdges.Remove(pair.Key);

        static void Forget(Transition[] edges, System.Collections.Generic.HashSet<Archetype> dropped)
        {
            for (int i = 0; i < edges.Length; i++)
                if (edges[i] != null && dropped.Contains(edges[i].Target))
                    edges[i] = null;
        }
    }

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

    /// <summary>Description of column <paramref name="index"/> (a valid index: one description per column, no bounds check).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ComponentInfo Info(int index) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_infos), index);
}


/// <summary>
/// The change-tick summary of one 64-row word of a change-tick column, 32 bytes so a word's summaries share a cache
/// line (a Changed filter or a write reads one line, not three). <see cref="Rows"/> hold <see cref="Write"/>, the other
/// rows at most <see cref="Rest"/>; while <see cref="Rest"/> is below a pass's tick, <see cref="Rows"/> answer it
/// without reading the ticks.
/// </summary>
internal struct WordTicks
{
    /// <summary>The latest tick of the word's rows (exact for the rows below the table's capacity).</summary>
    public ulong Write;

    /// <summary>An upper bound of the ticks of the rows not in <see cref="Rows"/>.</summary>
    public ulong Rest;

    /// <summary>The rows whose tick is <see cref="Write"/> (all of them while <see cref="Rest"/> is below it).</summary>
    public ulong Rows;

    /// <summary>
    /// An upper bound of the ticks of the rows lacking the component: their removal ticks (or ticks copied with them).
    /// Writes of rows that have the component leave it, so a <c>Removed</c> filter skips words that were only written.
    /// </summary>
    public ulong Removed;
}

/// <summary>
/// The summary of one tick of each word of a block of 64 words (<see cref="Archetype.Blocks"/>,
/// <see cref="Archetype.Removals"/>): <see cref="Words"/> are exactly the words at <see cref="Latest"/>, the others at
/// most <see cref="Rest"/>. While <see cref="Rest"/> is below a pass's tick, the block's words newer than it are
/// <see cref="Words"/>: one bit scan finds the next.
/// </summary>
internal struct BlockTicks
{
    /// <summary>The latest tick of the block's words.</summary>
    public ulong Latest;

    /// <summary>The words whose tick is <see cref="Latest"/>, one bit each.</summary>
    public ulong Words;

    /// <summary>An upper bound of the ticks of the words not in <see cref="Words"/>.</summary>
    public ulong Rest;

    /// <summary>Words <paramref name="words"/> (bits) got tick <paramref name="tick"/>, each raised to it, never lowered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Raise(ulong words, ulong tick)
    {
        // Atomic: chunks of a parallel pass smaller than a block share it, opened at the pass's tick (Open), so they
        // only ever take this branch. Once per word and tick (the word's first stamp), so the lock costs little.
        if (tick == Latest) Interlocked.Or(ref Words, words);
        else if (tick > Latest)
        {
            Rest = Latest; // every word of the block held at most the previous latest
            Latest = tick;
            Words = words;
        }
        // Older than the latest (a copied row): the words were older still, so none is in Words.
        else Rest = Math.Max(Rest, tick);
    }

    /// <summary>
    /// Raise the block to <paramref name="tick"/> with no word at it yet, so that the threads of a pass at that tick
    /// share it through the atomic branch of <see cref="Raise"/>. The words at the previous latest join the rest: a
    /// reader since then visits all the block's words instead of those (the price of a block the pass leaves alone).
    /// </summary>
    public void Open(ulong tick)
    {
        if (tick <= Latest) return;
        Rest = Latest;
        Latest = tick;
        Words = 0;
    }
}

/// <summary>
/// A cached move from one archetype to another: the target and the copy plan of the components both have (pairs of
/// column indices), computed once per edge so a structural change copies the row without searching columns.
/// </summary>
internal sealed class Transition
{
    public readonly Archetype Source;
    public readonly Archetype Target;

    // One step per shared component (source column, target column, its description), and the same steps split by
    // kind: the unmanaged ones copy bytes in a loop without calls, the managed ones call their typed copy.
    private readonly Step[] _plan;
    private readonly Bytes[] _unmanaged;
    private readonly Step[] _managed;

    // Whether the tables share change-tick columns: a moved row raises the target's summaries to the source's.
    private readonly bool _ticks;

    private readonly struct Step(int from, int to, ComponentInfo info)
    {
        public readonly int From = from;
        public readonly int To = to;
        public readonly ComponentInfo Info = info;
    }

    // An unmanaged step: 12 bytes, five to a cache line, nothing the byte copy does not use.
    private readonly struct Bytes(int from, int to, int size)
    {
        public readonly int From = from;
        public readonly int To = to;
        public readonly int Size = size;
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
        var unmanaged = new System.Collections.Generic.List<Bytes>();
        var managed = new System.Collections.Generic.List<Step>();
        foreach (var step in plan)
            if (step.Info.Size >= 0) unmanaged.Add(new Bytes(step.From, step.To, step.Info.Size));
            else managed.Add(step);
        _unmanaged = unmanaged.ToArray();
        _managed = managed.ToArray();
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
        foreach (ref readonly var step in _unmanaged.AsSpan())
        {
            int size = step.Size;
            Archetype.CopyBytes(ref Unsafe.Add(ref Archetype.Aligned(Unsafe.Add(ref from, step.From)), (nint)slot * size),
                ref Unsafe.Add(ref Archetype.Aligned(Unsafe.Add(ref to, step.To)), (nint)targetSlot * size), size);
        }

        foreach (ref readonly var step in _managed.AsSpan())
            step.Info.CopyManaged(Unsafe.Add(ref from, step.From), slot, Unsafe.Add(ref to, step.To), targetSlot);
        if (Source.HasAbsent) CopyAbsent(slot, targetSlot);
        if (_ticks) Archetype.RaiseWords(Source, slot, Target, targetSlot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CopyAbsent(int slot, int targetSlot)
    {
        foreach (ref readonly var step in _plan.AsSpan())
            if (!Source.Has(step.From, slot))
                Target.MarkAbsent(step.To, targetSlot, keep: true); // a removed managed value was cleared already; a disabled one stays
    }
}
