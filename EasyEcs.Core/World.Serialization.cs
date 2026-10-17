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
// (one block copy per column); managed ones, buffers' managed elements and singletons by Nino ([NinoType]). Types are
// recorded by name and each column is a length-prefixed slice, so a load skips the components this process no longer
// has. Systems, options, indexes and groupings are not data: the loading world brings its own (indexes rebuild).
public sealed partial class World
{
    private const int SaveMagic = 0x53434545; // "EECS"
    private const int SaveFormat = 1;

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
    /// <see cref="Load"/>. Unmanaged components are their raw rows (a layout change makes old saves unloadable: add
    /// a new component type instead); managed components and singletons need a Nino formatter (<c>[NinoType]</c>).
    /// Allocates nothing once its buffers are large enough. O(entities × components). Not while a pass iterates.
    /// </summary>
    public void Save(IBufferWriter<byte> output) => Save(output, default, true);

    /// <summary>
    /// Write <paramref name="entities"/> (stale ids and repeats are skipped) and their components into
    /// <paramref name="output"/>, for <see cref="LoadEntities"/>: a group of entities that moves to another world (a
    /// player and what it owns, to another server). Singletons and the other entities are not written; collect a
    /// hierarchy with <see cref="Children"/>. Allocates nothing once its buffers are large enough.
    /// O(entities given × components). Not while a pass iterates.
    /// </summary>
    public void SaveEntities(IBufferWriter<byte> output, ReadOnlySpan<EntityId> entities) => Save(output, entities, false);

    private void Save(IBufferWriter<byte> output, ReadOnlySpan<EntityId> entities, bool all)
    {
        ArgumentNullException.ThrowIfNull(output);
        using (Structural())
        {
            ThrowIfIterating();
            FillDirtyTables(); // tables are dense from here
            if (!all) Select(entities);
            var buffer = output as NinoArrayBufferWriter ?? (_saveBuffer ??= new NinoArrayBufferWriter());
            if (!ReferenceEquals(buffer, output)) buffer.ResetWrittenCount();
            var writer = new Writer(buffer);
            WriteSave(ref writer, all);
            writer.Flush();
            if (ReferenceEquals(buffer, output)) return;
            var bytes = buffer.WrittenSpan;
            bytes.CopyTo(output.GetSpan(bytes.Length));
            output.Advance(bytes.Length);
        }
    }

    // A group save's entities: one bit per id (_saveSelected), the ids in the order given (_saveOrder), and per table
    // the rows that hold them (_saveRows, filled table by table).
    private ulong[] _saveSelected = [];
    private EntityId[] _saveOrder = [];
    private int _saveOrderCount;
    private int[] _saveRows = [];
    private ulong[] _saveBits = [];

    /// <summary>Mark the living, distinct ids of <paramref name="entities"/> for a group save. Inside the gate.</summary>
    private void Select(ReadOnlySpan<EntityId> entities)
    {
        int words = (_nextId + 63) >> 6;
        if (_saveSelected.Length < words) _saveSelected = new ulong[words];
        else Array.Clear(_saveSelected, 0, words);
        if (_saveOrder.Length < entities.Length) _saveOrder = new EntityId[entities.Length];
        _saveOrderCount = 0;
        foreach (var id in entities)
        {
            if (!IsAlive(id) || (_saveSelected[id.Index >> 6] & (1UL << id.Index)) != 0) continue;
            _saveSelected[id.Index >> 6] |= 1UL << id.Index;
            _saveOrder[_saveOrderCount++] = id;
        }
    }

    /// <summary>The rows of <paramref name="table"/> to write: all of them, or a group's (into <see cref="_saveRows"/>).</summary>
    private int SavedRows(Archetype table, bool all)
    {
        if (all || table.Count == 0) return table.Count;
        int rows = 0;
        for (int slot = 0; slot < table.Count; slot++)
        {
            int id = table.Slots[slot].Index;
            if ((_saveSelected[id >> 6] & (1UL << id)) == 0) continue;
            if (_saveRows.Length == rows) Array.Resize(ref _saveRows, Math.Max(16, rows * 2));
            _saveRows[rows++] = slot;
        }

        return rows;
    }

    private void WriteSave(ref Writer writer, bool all)
    {
        writer.Write(SaveMagic);
        writer.Write(SaveFormat);

        // The types: every component column of a table with rows, by name (a Disabled<T> tag as T, flagged), with the
        // row size of an unmanaged one (a changed layout is refused at load). Change-tick columns are not data.
        var tables = _allArchetypes;
        int maxBit = 0;
        foreach (var table in tables)
            if (SavedRows(table, all) != 0) foreach (var bit in table.Components) maxBit = Math.Max(maxBit, bit);
        if (_saveTypes.Length <= maxBit) _saveTypes = new int[maxBit + 1];
        _saveTypes.AsSpan(0, maxBit + 1).Fill(-1);
        int types = 0;
        int countAt = writer.ReserveInt32();
        foreach (var table in tables)
        {
            if (SavedRows(table, all) == 0) continue;
            foreach (var bit in table.Components)
            {
                if (_saveTypes[bit] >= 0) continue;
                var info = ComponentInfo.Of(bit);
                if (info.Owner >= 0) continue; // a change-tick column
                _saveTypes[bit] = types++;
                bool disabled = info.DisabledOf >= 0;
                var named = disabled ? ComponentInfo.Of(info.DisabledOf) : info;
                writer.Write(named.Name);
                writer.Write(disabled);
                writer.Write(disabled ? 0 : info.Size);
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
        int tableCount = 0;
        foreach (var table in tables) if (SavedRows(table, all) != 0) tableCount++;
        writer.Write(tableCount);
        foreach (var table in tables)
        {
            int count = SavedRows(table, all);
            if (count == 0) continue;
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
            else for (int r = 0; r < count; r++) writer.Write(table.Slots[_saveRows[r]]);
            for (int c = 0; c < table.Components.Length; c++)
            {
                if (_saveTypes[table.Components[c]] < 0) continue;
                int slice = writer.ReserveInt32();
                if (all) WriteColumn(ref writer, table, c, count);
                else WriteRows(ref writer, table, c, count);
                writer.PatchLength(slice);
            }
        }

        // The singletons, by name, each a slice (a group has none).
        int singletons = 0;
        foreach (var box in _singletons) if (all && box != null) singletons++;
        writer.Write(singletons);
        foreach (var value in _singletons)
        {
            if (!all || value is not SingletonBox box) continue;
            writer.Write(box.Name);
            int slice = writer.ReserveInt32();
            box.Write(ref writer);
            writer.PatchLength(slice);
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
        else writer.WriteRaw(MemoryMarshal.CreateReadOnlySpan(ref Archetype.Aligned(table.Columns[c]), count * info.Size));
    }

    /// <summary><see cref="WriteColumn"/> for a group: the rows <see cref="_saveRows"/> names, in that order.</summary>
    private void WriteRows(ref Writer writer, Archetype table, int c, int count)
    {
        bool sparse = table.Bits(c) != null;
        writer.Write(sparse);
        if (sparse)
        {
            int words = (count + 63) >> 6;
            if (_saveBits.Length < words) _saveBits = new ulong[words];
            Array.Clear(_saveBits, 0, words);
            for (int r = 0; r < count; r++)
                if (table.Has(c, _saveRows[r])) _saveBits[r >> 6] |= 1UL << r;
            writer.WriteUnmanagedRaw<ulong>(_saveBits.AsSpan(0, words));
        }

        var info = table.Info(c);
        for (int r = 0; r < count; r++)
            if (info.Managed) info.Write(ref writer, table.Columns[c], _saveRows[r], 1);
            else writer.WriteRaw(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref Archetype.Aligned(table.Columns[c]), _saveRows[r] * info.Size), info.Size));
    }

    /// <summary>
    /// Replace this world's entities, components and singletons with those <see cref="Save"/> wrote (in this process
    /// or another). Entities keep their ids and versions, so <see cref="EntityId"/>s stored in components still point
    /// at the same entities, and handles made before the save work again. Components and singletons this process
    /// has no type for are skipped (their slices); a type is found by name when the process used it or the generator
    /// registered it (every component and singleton of a compilation using EasyEcs). Indexes rebuild; groups follow
    /// this world's <see cref="GroupBy{T}"/>. Every loaded row counts as written (<c>[Changed&lt;T&gt;]</c>).
    /// O(entities × components). Not while a pass iterates.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a save, or an unmanaged component's size changed.</exception>
    public void Load(ReadOnlySpan<byte> input)
    {
        var reader = new Reader(input);
        if (reader.Read<int>() != SaveMagic || reader.Read<int>() != SaveFormat)
            throw new InvalidDataException("Not an EasyEcs save (or one of another format version)");

        // The types: resolved by name to this process's bits (-1: unknown, skipped).
        int typeCount = reader.Read<int>();
        var bits = new int[typeCount];
        for (int t = 0; t < typeCount; t++)
        {
            string name = reader.ReadString();
            bool disabled = reader.Read<bool>();
            int size = reader.Read<int>();
            int bit = SavedTypes.FindComponent(name);
            if (bit < 0) bits[t] = -1;
            else
            {
                var info = ComponentInfo.Of(bit);
                if (!disabled && size != info.Size)
                    throw new InvalidDataException($"The size of {name} changed ({size} bytes saved, {info.Size} now): its saves cannot be loaded");
                bits[t] = disabled ? info.DisabledBit : bit;
            }
        }

        var snapshot = _loadSnapshot ??= new WorldSnapshot(this);
        int nextId = reader.Read<int>();
        if (snapshot.Meta.Length < nextId) snapshot.Meta = new EntityMeta[nextId];
        else Array.Clear(snapshot.Meta, 0, nextId); // an id the bytes give no row is dead
        snapshot.NextId = nextId;
        for (int id = 0; id < nextId; id++) snapshot.Meta[id].Version = reader.Read<int>();
        snapshot.FreeCount = reader.Read<int>();
        if (snapshot.FreeIds.Length < snapshot.FreeCount) snapshot.FreeIds = new int[snapshot.FreeCount];
        reader.ReadUnmanagedRaw(snapshot.FreeIds.AsSpan(0, snapshot.FreeCount));
        snapshot.EntityCount = 0;

        using (Structural())
        {
            ThrowIfIterating();
            for (int i = 0; i < snapshot.TableCount; i++) Empty(snapshot.Tables[i], _allArchetypes[i]);
            int tableCount = reader.Read<int>();
            for (int t = 0; t < tableCount; t++) LoadTable(ref reader, bits, snapshot);

            var tables = _allArchetypes; // with the tables the load created
            if (snapshot.Tables.Length < tables.Length) Array.Resize(ref snapshot.Tables, tables.Length);
            snapshot.TableCount = tables.Length;
            for (int i = 0; i < tables.Length; i++) snapshot.EntityCount += snapshot.Tables[i]?.Count ?? 0;

            // The singletons, into the boxes of the last load where it had them; the others are dropped.
            int singletons = reader.Read<int>();
            var loaded = snapshot.Singletons;
            Span<int> read = singletons <= 64 ? stackalloc int[singletons] : new int[singletons];
            int found = 0;
            for (int i = 0; i < singletons; i++)
            {
                string name = reader.ReadString();
                var slice = reader.ReadSlice(); // skipped when this process has no such singleton
                var box = Find(loaded, name) ?? SavedTypes.NewSingleton(name);
                if (box == null) continue;
                box.Read(ref slice);
                reader.EndSlice(in slice);
                if (loaded.Length <= box.Slot) Array.Resize(ref loaded, box.Slot + 1);
                loaded[box.Slot] = box;
                read[found++] = box.Slot;
            }

            for (int slot = 0; slot < loaded.Length; slot++)
                if (loaded[slot] != null && read[..found].IndexOf(slot) < 0) loaded[slot] = null;
            snapshot.Singletons = loaded;
        }

        _loadOrderCount = reader.Read<int>();
        if (_loadOrder.Length < _loadOrderCount) _loadOrder = new EntityId[_loadOrderCount];
        reader.ReadUnmanagedRaw(_loadOrder.AsSpan(0, _loadOrderCount));
        Restore(snapshot);
        if (_groupByBit == null) return;
        using (Structural())
            for (int bit = 0; bit < _groupByBit.Length; bit++)
                if (_groupByBit[bit] != null) RegroupAll(bit); // loaded rows start in group 0
    }

    /// <summary>
    /// Read one saved table into the rows of the table of this world it maps to (several saved tables map to one when
    /// a component is skipped). Records each entity's table and row in the snapshot's entity records.
    /// </summary>
    private void LoadTable(ref Reader reader, int[] bits, WorldSnapshot snapshot)
    {
        int columns = reader.Read<int>();
        Span<int> columnBits = columns <= 32 ? stackalloc int[columns] : new int[columns];
        var mask = new BitMask();
        for (int c = 0; c < columns; c++)
        {
            columnBits[c] = bits[reader.Read<int>()];
            if (columnBits[c] >= 0) mask.SetBit(columnBits[c]);
        }

        var table = GetOrCreateArchetype(in mask);
        int index = Array.LastIndexOf(_allArchetypes, table); // few tables: a scan, no map
        if (snapshot.Tables.Length <= index) Array.Resize(ref snapshot.Tables, _allArchetypes.Length);
        var rows = snapshot.Tables[index] ??= new Archetype.Rows(table.Columns.Length);

        int count = reader.Read<int>();
        int start = rows.Count;
        GrowRows(rows, table, start + count);
        var ids = rows.Slots.AsSpan(start, count);
        reader.ReadUnmanagedRaw(ids);
        for (int r = 0; r < count; r++)
        {
            int id = ids[r].Index;
            if (id >= snapshot.NextId) Extend(snapshot, id + 1); // a group's bytes record no ids of their own
            ref var meta = ref snapshot.Meta[id];
            meta.Version = ids[r].Version;
            meta.Archetype = table;
            meta.Slot = start + r;
        }

        for (int c = 0; c < columns; c++)
        {
            var slice = reader.ReadSlice();
            int column = columnBits[c] < 0 ? -1 : table.ColumnIndex(columnBits[c]);
            if (column >= 0) LoadColumn(ref slice, table.Info(column), rows, column, start, count);
            reader.EndSlice(in slice);
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
        else reader.ReadRaw(count * info.Size).CopyTo(Unsafe.As<byte[]>(rows.Columns[column]).AsSpan(start * info.Size));
    }

    /// <summary>Make the entity records of <paramref name="snapshot"/> cover ids below <paramref name="count"/> (dead ones).</summary>
    private static void Extend(WorldSnapshot snapshot, int count)
    {
        if (snapshot.Meta.Length < count) Array.Resize(ref snapshot.Meta, Math.Max(count, snapshot.Meta.Length * 2));
        Array.Clear(snapshot.Meta, snapshot.NextId, count - snapshot.NextId);
        snapshot.NextId = count;
    }

    /// <summary>The box in <paramref name="boxes"/> of the singleton saved as <paramref name="name"/>, or null.</summary>
    private static SingletonBox Find(SingletonBox[] boxes, string name)
    {
        foreach (var box in boxes)
            if (box != null && box.Name == name) return box;
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
            if (info.Managed && rows.Columns[c] != null) info.Clear(rows.Columns[c], 0, rows.Count);
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
            if (info.Managed) column = column == null ? info.Create(count) : info.Grow(column, count);
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
