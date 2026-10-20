using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;

namespace EasyEcs;

// Saves: a world's entities (ids and versions), components and singletons as bytes, loaded into a world of this or
// another process (persistence across restarts, replays on disk). Unmanaged components are written as their raw rows
// (one block copy per column) unless they are [NinoType]s; those, managed ones, buffers' managed elements and
// singletons by Nino. Types are recorded by Nino type id ([NinoFormerName] keeps a renamed type's id) and each
// column is a length-prefixed slice, so a load skips the components this process no longer has. Systems, options, indexes and groupings are not data: the loading world brings its own (indexes rebuild).
public sealed partial class World
{
    private const int SaveMagic = 0x53434545; // "EECS"
    private const int SaveFormat = 2; // 2: types by Nino type id, [NinoType] unmanaged rows by Nino

    // Reused by every Save: the bytes when the output is not a NinoArrayBufferWriter, and the type index of each bit.
    private NinoArrayBufferWriter _saveBuffer;
    private int[] _saveTypes = [];

    // Reused by every Load: the rows read from the bytes, put in place by Restore (arrays grow once, then stay).
    private WorldSnapshot _loadSnapshot;

    // The order of a group the last Load read (SaveEntities' order): LoadEntities hands back handles in it.
    private EntityId[] _loadOrder = [];
    private int _loadOrderCount;

    /// <summary>
    /// Write the entities (ids and versions), their components and the singletons into <paramref name="output"/>, for
    /// <see cref="Load"/>. Unmanaged components are their raw rows (a layout change makes old saves unloadable) unless
    /// they are <c>[NinoType]</c>s, whose formatter writes them (its format decides what a type may change: fields
    /// appended with <c>VersionTolerant</c>, any change of members with <c>Keyed</c>); managed components and
    /// singletons need a Nino formatter. Types are recorded by Nino type id: a type renamed with
    /// <c>[NinoFormerName]</c> loads its old rows.
    /// Allocates nothing once its buffers are large enough. O(entities × components). Not while a pass iterates.
    /// </summary>
    public void Save(IBufferWriter<byte> output) => Save(output, default, true, default, false);

    /// <summary>
    /// Write <paramref name="entities"/> (stale ids and repeats are skipped) and their components into
    /// <paramref name="output"/>, for <see cref="LoadEntities"/> or <see cref="LoadEntitiesInto"/>: a group of entities
    /// that moves to another world (a player and what it owns, to another server). Singletons and the other entities
    /// are not written; collect a hierarchy with <see cref="Descendants"/>. Allocates nothing once its buffers are large
    /// enough. O(entities given × components). Not while a pass iterates.
    /// </summary>
    public void SaveEntities(IBufferWriter<byte> output, ReadOnlySpan<EntityId> entities) => Save(output, entities, false, default, false);

    /// <summary>
    /// <see cref="SaveEntities(IBufferWriter{byte}, ReadOnlySpan{EntityId})"/> of the components in
    /// <paramref name="components"/> only (a record of the components that persist, without the runtime ones): the
    /// others are not written and need no Nino formatter; a tag is written as present or not, a disabled component
    /// with its kept value. Entities without any of them are written too, empty. Allocates nothing once its buffers
    /// are large enough. O(entities given × components). Not while a pass iterates.
    /// </summary>
    public void SaveEntities(IBufferWriter<byte> output, ReadOnlySpan<EntityId> entities, in ComponentSet components) =>
        Save(output, entities, false, in components, true);

    private void Save(IBufferWriter<byte> output, ReadOnlySpan<EntityId> entities, bool all, in ComponentSet components, bool filtered)
    {
        ArgumentNullException.ThrowIfNull(output);
        using (Structural())
        {
            ThrowIfIterating();
            FillDirtyTables(); // tables are dense from here
            if (all) SelectAll();
            else Select(entities);
            var buffer = output as NinoArrayBufferWriter ?? (_saveBuffer ??= new NinoArrayBufferWriter());
            if (!ReferenceEquals(buffer, output)) buffer.ResetWrittenCount();
            var writer = new Writer(buffer);
            try
            {
                WriteSave(ref writer, all, in components, filtered);
            }
            finally
            {
                Array.Clear(_saveTables, 0, _saveTableCount); // no table outlives its world's use (types unload with theirs)
                _saveTableCount = 0;
            }

            writer.Flush();
            if (ReferenceEquals(buffer, output)) return;
            var bytes = buffer.WrittenSpan;
            bytes.CopyTo(output.GetSpan(bytes.Length));
            output.Advance(bytes.Length);
        }
    }

    // A save's tables (_saveTables) and the number of rows each writes (_saveCounts). A group save writes the rows
    // _saveRows holds from _saveStarts[t] (its entities' rows, table by table, in the order given) and its entities in
    // the order given (_saveOrder); one bit per id of the group (_saveSelected) skips repeats while it is selected.
    private Archetype[] _saveTables = [];
    private int[] _saveCounts = [];
    private int[] _saveStarts = [];
    private int _saveTableCount;
    private ulong[] _saveSelected = [];
    private EntityId[] _saveOrder = [];
    private int _saveOrderCount;
    private int[] _saveRows = [];
    private ulong[] _saveBits = [];
    private int _saveStamp; // marks the tables a group save found (Archetype.SaveMark)

    /// <summary>Every table with rows, all of them written. Inside the gate.</summary>
    private void SelectAll()
    {
        _saveTableCount = 0;
        foreach (var table in _allArchetypes)
            if (table.Count != 0) AddSaveTable(table, table.Count);
    }

    private int AddSaveTable(Archetype table, int count)
    {
        int index = _saveTableCount++;
        if (_saveTables.Length == index)
        {
            int length = Math.Max(8, index * 2);
            Array.Resize(ref _saveTables, length);
            Array.Resize(ref _saveCounts, length);
            Array.Resize(ref _saveStarts, length);
        }

        _saveTables[index] = table;
        _saveCounts[index] = count;
        return index;
    }

    /// <summary>
    /// The living, distinct ids of <paramref name="entities"/> for a group save, and their rows table by table: O(group),
    /// whatever the size of the world. Inside the gate.
    /// </summary>
    private void Select(ReadOnlySpan<EntityId> entities)
    {
        int words = (_nextId + 63) >> 6;
        if (_saveSelected.Length < words) _saveSelected = new ulong[words]; // all clear: each Select clears its bits
        if (_saveOrder.Length < entities.Length) _saveOrder = new EntityId[entities.Length];
        if (++_saveStamp == 0) // the stamps wrapped: forget them once
        {
            foreach (var table in _allArchetypes) table.SaveMark = 0;
            _saveStamp = 1;
        }

        _saveOrderCount = 0;
        _saveTableCount = 0;
        foreach (var id in entities)
        {
            if (!IsAlive(id) || (_saveSelected[id.Index >> 6] & (1UL << id.Index)) != 0) continue;
            _saveSelected[id.Index >> 6] |= 1UL << id.Index;
            _saveOrder[_saveOrderCount++] = id;
            var table = Meta[id.Index].Archetype;
            if (table.SaveMark != _saveStamp)
            {
                table.SaveMark = _saveStamp;
                table.SaveIndex = AddSaveTable(table, 0);
            }

            _saveCounts[table.SaveIndex]++;
        }

        var order = _saveOrder.AsSpan(0, _saveOrderCount);
        foreach (var id in order) _saveSelected[id.Index >> 6] = 0; // clear for the next Select
        if (_saveRows.Length < order.Length) _saveRows = new int[Math.Max(16, order.Length)];
        int start = 0;
        for (int t = 0; t < _saveTableCount; t++)
        {
            _saveStarts[t] = start;
            start += _saveCounts[t];
            _saveCounts[t] = 0; // counted again while the rows are placed
        }

        foreach (var id in order)
        {
            ref var meta = ref Meta[id.Index];
            int t = meta.Archetype.SaveIndex;
            _saveRows[_saveStarts[t] + _saveCounts[t]++] = meta.Slot;
        }
    }

    /// <summary>Whether a save writes column <paramref name="bit"/> (<paramref name="info"/>): data, and in the set when there is one.</summary>
    private static bool Saves(ComponentInfo info, int bit, in ComponentSet components, bool filtered) =>
        info.Owner < 0 // a change-tick column is not data
        && (!filtered || components.Contains(info.DisabledOf >= 0 ? info.DisabledOf : bit)); // a Disabled<T> tag goes with T

    private void WriteSave(ref Writer writer, bool all, in ComponentSet components, bool filtered)
    {
        writer.Write(SaveMagic);
        writer.Write(SaveFormat);
        var tables = _saveTables.AsSpan(0, _saveTableCount);

        // The types: every saved column of a table with rows, by type id (a Disabled<T> tag as T, flagged), with how
        // its rows are written (the size and layout of raw rows; a load refuses rows written another way).
        int maxBit = 0;
        foreach (var table in tables)
            foreach (var bit in table.Components) maxBit = Math.Max(maxBit, bit);
        if (_saveTypes.Length <= maxBit) _saveTypes = new int[maxBit + 1];
        _saveTypes.AsSpan(0, maxBit + 1).Fill(-1);
        int types = 0;
        int countAt = writer.ReserveInt32();
        foreach (var table in tables)
        {
            foreach (var bit in table.Components)
            {
                if (_saveTypes[bit] >= 0) continue;
                var info = ComponentInfo.Of(bit);
                if (!Saves(info, bit, in components, filtered)) continue;
                _saveTypes[bit] = types++;
                bool disabled = info.DisabledOf >= 0;
                var named = disabled ? ComponentInfo.Of(info.DisabledOf) : info;
                writer.Write(named.TypeId);
                writer.Write(disabled);
                writer.Write(disabled ? 0 : info.SavedSize);
                writer.Write(disabled ? 0 : info.SavedLayout);
            }
        }

        writer.PatchInt32(countAt, types);

        // The entity records: versions of every id used, and the free ids (LIFO order kept). A group has none: its
        // rows name their ids.
        writer.Write(all ? _nextId : 0);
        for (int id = 0; all && id < _nextId; id++) writer.Write(Meta[id].Version);
        writer.Write<int>(all ? _freeIds.AsSpan(0, _freeIdCount) : default);

        // The tables: their columns' types, their rows' entities, then one slice per column. Groups are not saved: they
        // follow the values and the loading world's GroupBy.
        writer.Write(tables.Length);
        for (int t = 0; t < tables.Length; t++)
        {
            var table = tables[t];
            int count = _saveCounts[t];
            var rows = all ? default : _saveRows.AsSpan(_saveStarts[t], count);
            int columns = 0;
            for (int c = 0; c < table.Components.Length; c++) if (_saveTypes[table.Components[c]] >= 0) columns++;
            writer.Write(columns);
            for (int c = 0; c < table.Components.Length; c++)
            {
                int type = _saveTypes[table.Components[c]];
                if (type < 0) continue;
                writer.Write(type);
            }

            writer.Write(count);
            if (all) writer.WriteUnmanagedRaw<EntityId>(table.Slots.AsSpan(0, count));
            else foreach (int row in rows) writer.Write(table.Slots[row]);
            for (int c = 0; c < table.Components.Length; c++)
            {
                if (_saveTypes[table.Components[c]] < 0) continue;
                int slice = writer.ReserveInt32();
                if (all) WriteColumn(ref writer, table, c, count);
                else WriteRows(ref writer, table, c, rows);
                writer.PatchLength(slice);
                writer.ReleaseReferences(); // a value refers to nothing in another slice: see Ends
            }
        }

        // The singletons, by type id, each a slice (a group has none).
        int singletons = 0;
        foreach (var box in _singletons) if (all && box != null) singletons++;
        writer.Write(singletons);
        foreach (var value in _singletons)
        {
            if (!all || value is not SingletonBox box) continue;
            writer.Write(box.TypeId);
            int slice = writer.ReserveInt32();
            box.Write(ref writer);
            writer.PatchLength(slice);
            writer.ReleaseReferences();
        }

        // A group's entities in the order given (LoadEntities hands their new handles back in it).
        writer.Write<EntityId>(all ? default : _saveOrder.AsSpan(0, _saveOrderCount));
    }

    /// <summary>A column's slice: its presence bits when some rows lack the component, then its rows.</summary>
    private static void WriteColumn(ref Writer writer, Archetype table, int c, int count)
    {
        var bits = table.Bits(c);
        writer.Write(bits != null);
        if (bits != null) writer.WriteUnmanagedRaw<ulong>(bits.AsSpan(0, (count + 63) >> 6));
        var info = table.Info(c);
        if (info.Managed) info.Write(ref writer, table.Columns[c], 0, count);
        else info.WriteRows(ref writer, MemoryMarshal.CreateReadOnlySpan(ref Archetype.Aligned(table.Columns[c]), count * info.Size));
    }

    /// <summary><see cref="WriteColumn"/> for a group: the rows of <paramref name="rows"/>, in that order.</summary>
    private void WriteRows(ref Writer writer, Archetype table, int c, ReadOnlySpan<int> rows)
    {
        bool sparse = table.Bits(c) != null;
        writer.Write(sparse);
        if (sparse)
        {
            int words = (rows.Length + 63) >> 6;
            if (_saveBits.Length < words) _saveBits = new ulong[words];
            Array.Clear(_saveBits, 0, words);
            for (int r = 0; r < rows.Length; r++)
                if (table.Has(c, rows[r])) _saveBits[r >> 6] |= 1UL << r;
            writer.WriteUnmanagedRaw<ulong>(_saveBits.AsSpan(0, words));
        }

        var info = table.Info(c);
        foreach (int row in rows)
            if (info.Managed) info.Write(ref writer, table.Columns[c], row, 1);
            else info.WriteRows(ref writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref Archetype.Aligned(table.Columns[c]), row * info.Size), info.Size));
    }

    /// <summary>
    /// Replace this world's entities, components and singletons with those <see cref="Save(System.Buffers.IBufferWriter{byte})"/> wrote (in this process
    /// or another). Entities keep their ids and versions, so <see cref="EntityId"/>s stored in components still point
    /// at the same entities, and handles made before the save work again. Components and singletons this process
    /// has no type for are skipped (their slices); a type is found by its type id when the process used it or the generator
    /// registered it (every component and singleton of a compilation using EasyEcs). Indexes rebuild; groups follow
    /// this world's <see cref="GroupBy{T}"/>. O(entities × components). Not while a pass iterates.
    /// </summary>
    /// <param name="input">The bytes of the save.</param>
    /// <param name="markWritten">
    /// True (the default): every loaded row counts as written now, so <c>[Changed&lt;T&gt;]</c> systems and
    /// <c>Changed&lt;T&gt;(since)</c> queries see it. False: the loaded components have no change tick, as if never
    /// written, and no row counts as removed; a system added after the load skips them on its first run too (its
    /// <c>[Changed&lt;T&gt;]</c> looks at ticks from 1). For a restart that resumes a world saved by another process, whose
    /// records already hold the loaded values.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a save or are damaged, or a component's fields changed since the save (an unmanaged one's
    /// size or layout, a managed one's Nino layout), or the save gives one key of a unique component
    /// (<see cref="IUniqueIndexed{TKey}"/>) to several entities. The world is then left as it was.
    /// </exception>
    public void Load(ReadOnlySpan<byte> input, bool markWritten = true)
    {
        using (Structural()) ThrowIfIterating(); // before reading: a failure below is the bytes'
        try
        {
            Read(input);
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Nino:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("A damaged save: " + e.Message, e);
        }

        if (ComponentInfo.AnyUnique) ClaimLoaded(_loadSnapshot); // a key twice: the world stays as it was
        Restore(_loadSnapshot, true, markWritten);
        if (_groupByBit == null) return;
        using (Structural())
            for (int bit = 0; bit < _groupByBit.Length; bit++)
                if (_groupByBit[bit] != null) RegroupAll(bit); // loaded rows start in group 0
    }

    // Reused by every Load: the bit each saved type maps to, and the mask of a saved table's components.
    private int[] _loadBits = [];
    private BitMask _loadMask;

    /// <summary>Read a save into <see cref="_loadSnapshot"/> (the world itself is not touched).</summary>
    private void Read(ReadOnlySpan<byte> input)
    {
        var reader = new Reader(input);
        if (input.Length < 8 || reader.Read<int>() != SaveMagic || reader.Read<int>() != SaveFormat)
            throw new InvalidDataException("Not an EasyEcs save (or one of another format version)");

        // The types: resolved by type id to this process's bits (-1: unknown, skipped).
        int typeCount = Count(ref reader, 13); // id, disabled, size, layout
        if (_loadBits.Length < typeCount) _loadBits = new int[typeCount];
        var bits = _loadBits.AsSpan(0, typeCount);
        for (int t = 0; t < typeCount; t++)
        {
            int id = reader.Read<int>();
            bool disabled = reader.Read<bool>();
            int size = reader.Read<int>();
            int layout = reader.Read<int>();
            int bit = SavedTypes.FindComponent(id);
            if (bit < 0) bits[t] = -1;
            else
            {
                var info = ComponentInfo.Of(bit);
                if (!disabled) Writes(info, size, layout);
                bits[t] = disabled ? ComponentInfo.DisablerOf(bit) : bit;
            }
        }

        var snapshot = _loadSnapshot ??= new WorldSnapshot(this);
        int nextId = Count(ref reader, sizeof(int));
        if (snapshot.Meta.Length < nextId) snapshot.Meta = new EntityMeta[nextId];
        else Array.Clear(snapshot.Meta, 0, nextId); // an id the bytes give no row is dead
        snapshot.NextId = nextId;
        for (int id = 0; id < nextId; id++) snapshot.Meta[id].Version = reader.Read<int>();
        snapshot.FreeCount = Count(ref reader, sizeof(int));
        if (snapshot.FreeIds.Length < snapshot.FreeCount) snapshot.FreeIds = new int[snapshot.FreeCount];
        reader.ReadUnmanagedRaw(snapshot.FreeIds.AsSpan(0, snapshot.FreeCount));
        foreach (int id in snapshot.FreeIds.AsSpan(0, snapshot.FreeCount))
            if ((uint)id >= (uint)nextId) throw Damaged();
        snapshot.EntityCount = 0;

        using (Structural())
        {
            for (int i = 0; i < snapshot.TableCount; i++) Empty(snapshot.Tables[i], _allArchetypes[i]);
            int tableCount = Count(ref reader, 2 * sizeof(int));
            for (int t = 0; t < tableCount; t++) LoadTable(ref reader, bits, snapshot);

            var tables = _allArchetypes; // with the tables the load created
            if (snapshot.Tables.Length < tables.Length) Array.Resize(ref snapshot.Tables, tables.Length);
            snapshot.TableCount = tables.Length;
            for (int i = 0; i < tables.Length; i++) snapshot.EntityCount += snapshot.Tables[i]?.Count ?? 0;

            // The singletons, into the boxes of the last load where it had them; the others are dropped. A box is
            // emptied before it is read: its old value's references may be the world's or a snapshot's (shallow copies).
            int singletons = Count(ref reader, 2 * sizeof(int)); // id, slice length
            var loaded = snapshot.Singletons;
            Span<int> read = singletons <= 64 ? stackalloc int[singletons] : new int[singletons];
            int found = 0;
            for (int i = 0; i < singletons; i++)
            {
                int id = reader.Read<int>();
                var slice = reader.ReadSlice(); // skipped when this process has no such singleton
                var box = Find(loaded, id) ?? SavedTypes.NewSingleton(id);
                if (box == null) continue;
                box.Clear();
                box.Read(ref slice);
                Ends(ref slice, box.Name);
                if (loaded.Length <= box.Slot) Array.Resize(ref loaded, box.Slot + 1);
                loaded[box.Slot] = box;
                read[found++] = box.Slot;
            }

            for (int slot = 0; slot < loaded.Length; slot++)
                if (loaded[slot] != null && read[..found].IndexOf(slot) < 0) loaded[slot] = null;
            snapshot.Singletons = loaded;
        }

        _loadOrderCount = Count(ref reader, Unsafe.SizeOf<EntityId>());
        if (_loadOrder.Length < _loadOrderCount) _loadOrder = new EntityId[_loadOrderCount];
        reader.ReadUnmanagedRaw(_loadOrder.AsSpan(0, _loadOrderCount));
    }

    /// <summary>A count the rest of the bytes can hold (<paramref name="size"/> bytes per item at least), else the save is damaged.</summary>
    private static int Count(ref Reader reader, int size)
    {
        int count = reader.ReadLength(size);
        return count >= 0 ? count : throw Damaged();
    }

    /// <summary>
    /// Refuse rows of <paramref name="info"/>'s type written another way than this process writes them: raw rows of
    /// another size or layout, or raw rows where Nino writes them now (or the other way round).
    /// </summary>
    private static void Writes(ComponentInfo info, int size, int layout)
    {
        int now = info.SavedSize;
        if (size == now && (size == 0 || layout == 0 || info.SavedLayout == 0 || layout == info.SavedLayout)) return;
        string name = info.Name;
        if (size == 0 || now == 0)
            throw new InvalidDataException($"{name} was saved {(size == 0 ? "by its Nino formatter" : "as raw rows")} and is written " +
                $"{(now == 0 ? "by its Nino formatter" : "as raw rows")} now: its saves cannot be loaded");
        if (size != now)
            throw new InvalidDataException($"The size of {name} changed ({size} bytes saved, {now} now): its saves cannot be loaded " +
                "(make it a [NinoType] with a VersionTolerant or Keyed format to change its fields)");
        throw new InvalidDataException($"The fields of {name} changed since the save: its saves cannot be loaded " +
            "(make it a [NinoType] with a VersionTolerant or Keyed format to change its fields)");
    }

    /// <summary>
    /// End a managed value's slice: Nino's reference tracking ends with it (a value never refers into another slice,
    /// so a skipped slice cannot shift the references of the others), and every byte must have been read.
    /// </summary>
    private static void Ends(ref Reader slice, string type)
    {
        slice.ReleaseReferences();
        if (!slice.Eof)
            throw new InvalidDataException($"The fields of {type} changed since the save: its saves cannot be loaded");
    }

    private static InvalidDataException Damaged() => new("A damaged save: a count or an id is out of range");

    /// <summary>
    /// Read one saved table into the rows of the table of this world it maps to (several saved tables map to one when
    /// a component is skipped). Records each entity's table and row in the snapshot's entity records.
    /// </summary>
    private void LoadTable(ref Reader reader, Span<int> bits, WorldSnapshot snapshot)
    {
        int columns = Count(ref reader, sizeof(int));
        Span<int> columnBits = columns <= 32 ? stackalloc int[columns] : new int[columns];
        ref var mask = ref _loadMask; // reused: bits past 256 keep their overflow vectors
        for (int c = 0; c < columns; c++)
        {
            int type = reader.Read<int>();
            if ((uint)type >= (uint)bits.Length) throw Damaged();
            int bit = columnBits[c] = bits[type];
            if (bit < 0) continue;
            mask.SetBit(bit);
            if (ComponentInfo.Of(bit).Companion is var ticks and >= 0) mask.SetBit(ticks); // saves leave tick columns out
        }

        var table = GetOrCreateArchetype(in mask);
        foreach (var component in table.Components) mask.ClearBit(component);
        int index = Array.LastIndexOf(_allArchetypes, table); // few tables: a scan, no map
        if (snapshot.Tables.Length <= index) Array.Resize(ref snapshot.Tables, _allArchetypes.Length);
        var rows = snapshot.Tables[index] ??= new Archetype.Rows(table.Columns.Length);

        int count = Count(ref reader, Unsafe.SizeOf<EntityId>());
        int start = rows.Count;
        GrowRows(rows, table, start + count);
        var ids = rows.Slots.AsSpan(start, count);
        reader.ReadUnmanagedRaw(ids);
        for (int r = 0; r < count; r++)
        {
            int id = ids[r].Index;
            if (id < 0 || ids[r].IsNull) throw Damaged();
            if (id >= snapshot.NextId) Extend(snapshot, id + 1); // a group's bytes record no ids of their own
            ref var meta = ref snapshot.Meta[id];
            if (meta.Archetype != null) throw Damaged(); // one row per entity
            meta.Version = ids[r].Version;
            meta.Archetype = table;
            meta.Slot = start + r;
        }

        for (int c = 0; c < columns; c++)
        {
            var slice = reader.ReadSlice();
            int column = columnBits[c] < 0 ? -1 : table.ColumnIndex(columnBits[c]);
            if (column < 0) continue; // a component this process does not have
            LoadColumn(ref slice, table.Info(column), rows, column, start, count);
            Ends(ref slice, table.Info(column).Name);
        }

        rows.Count = start + count;
    }

    /// <summary>Read one column slice: its presence bits, then its rows, into rows [<paramref name="start"/>, +<paramref name="count"/>).</summary>
    private static void LoadColumn(ref Reader reader, ComponentInfo info, Archetype.Rows rows, int column, int start, int count)
    {
        if (reader.Read<bool>())
        {
            var saved = MemoryMarshal.Cast<byte, ulong>(reader.ReadRaw(((count + 63) >> 6) * sizeof(ulong)));
            ref var present = ref rows.Present[column];
            if (present == null) // every earlier row present
            {
                present = new ulong[(rows.Slots.Length + 63) >> 6];
                for (int r = 0; r < start; r++) present[r >> 6] |= 1UL << r;
            }

            for (int r = 0; r < count; r++)
                if ((saved[r >> 6] & (1UL << r)) != 0) present[(start + r) >> 6] |= 1UL << (start + r);
                else rows.Absent[column]++;
        }
        else if (rows.Present[column] is { } present)
            for (int r = start; r < start + count; r++) present[r >> 6] |= 1UL << r;

        if (info.Managed) info.Read(ref reader, rows.Columns[column], start, count);
        else info.ReadRows(ref reader, Unsafe.As<byte[]>(rows.Columns[column]).AsSpan(start * info.Size, count * info.Size));
    }

    /// <summary>Make the entity records of <paramref name="snapshot"/> cover ids below <paramref name="count"/> (dead ones).</summary>
    private static void Extend(WorldSnapshot snapshot, int count)
    {
        if (snapshot.Meta.Length < count) Array.Resize(ref snapshot.Meta, Math.Max(count, snapshot.Meta.Length * 2));
        Array.Clear(snapshot.Meta, snapshot.NextId, count - snapshot.NextId);
        snapshot.NextId = count;
    }

    /// <summary>The box in <paramref name="boxes"/> of the singleton saved as type <paramref name="id"/>, or null.</summary>
    private static SingletonBox Find(SingletonBox[] boxes, int id)
    {
        foreach (var box in boxes)
            if (box != null && box.TypeId == id) return box;
        return null;
    }

    /// <summary>
    /// Empty rows an earlier load filled (null: none), keeping their arrays: managed values are cleared (no
    /// references kept alive), presence bits dropped (a load sets them again where rows are absent).
    /// </summary>
    private static void Empty(Archetype.Rows rows, Archetype table)
    {
        if (rows == null) return;
        for (int c = 0; c < rows.Columns.Length; c++)
        {
            var info = table.Info(c);
            // Buffer columns keep their lists: the next read refills them (ComponentInfo.Read).
            if (info.Managed && !info.IsBuffer && rows.Columns[c] != null) info.Clear(rows.Columns[c], 0, rows.Count);
            rows.Present[c] = null;
            rows.Absent[c] = 0;
        }

        rows.Count = 0;
    }

    /// <summary>Make <paramref name="rows"/> (of <paramref name="table"/>) hold <paramref name="count"/> rows, keeping the rows it has.</summary>
    private static void GrowRows(Archetype.Rows rows, Archetype table, int count)
    {
        if (rows.Slots.Length >= count) return;
        Array.Resize(ref rows.Slots, count);
        for (int c = 0; c < table.Columns.Length; c++)
        {
            var info = table.Info(c);
            ref var column = ref rows.Columns[c];
            if (info.Managed) column = column == null ? info.Create(count) : info.Resize(column, count);
            else
            {
                var bytes = new byte[count * info.Size];
                if (column != null) Unsafe.As<byte[]>(column).CopyTo(bytes, 0);
                column = bytes;
            }

            if (rows.Present[c] != null) Array.Resize(ref rows.Present[c], (count + 63) >> 6);
        }
    }
}
