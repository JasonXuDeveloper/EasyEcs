using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

#pragma warning disable EECS031 // these tests grow tables entity by entity on purpose: the doubling steps are what they check

namespace EasyEcs.UnitTest;

public record struct ResizeValue(long Value) : IComponent;
public record struct ResizeTracked(int Value) : IComponent, ITracked;
public record struct ResizeName(string Name) : IComponent;

/// <summary>
/// A table's growth and trim (Archetype.Resize) allocate their arrays uninitialized and clear only what the copy leaves:
/// over memory the process dirtied before, every byte past the rows (columns' slack and unwritten rows, entity rows,
/// presence bits, change-tick summaries, managed columns) reads zero, and values, saves and snapshots read as they did.
/// </summary>
public class ResizeTests
{
    private const int Many = 300_000; // 2.4 MB per long column: doubling steps of every size up to the large object heap
    /// <summary>
    /// Fill pinned memory with <paramref name="dirt"/> and free it, so the next pinned allocations are handed dirty memory;
    /// returns whether an uninitialized allocation afterwards saw some (else the test cannot tell a clear from luck).
    /// </summary>
    private static bool DirtyThePinnedHeap(byte dirt = 0xA5)
    {
        for (int round = 0; round < 2; round++)
        {
            var held = new byte[24][];
            for (int i = 0; i < held.Length; i++)
            {
                held[i] = GC.AllocateUninitializedArray<byte>(1024 << (i % 14), pinned: true);
                held[i].AsSpan().Fill(dirt);
            }

            held = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        bool seen = false;
        for (int i = 0; i < 14 && !seen; i++)
        {
            var probe = GC.AllocateUninitializedArray<byte>(1024 << i, pinned: true);
            seen = probe.AsSpan().Contains(dirt);
            probe.AsSpan().Fill(dirt);
        }

        GC.Collect();
        return seen;
    }

    /// <summary>The offset of row 0 in an unmanaged column's byte array.</summary>
    private static int Head(byte[] column) =>
        (int)Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(column), ref Archetype.Aligned(column));

    /// <summary>Every byte of <paramref name="table"/>'s arrays past its first <paramref name="rows"/> rows is zero.</summary>
    private static void AssertClearPast(Archetype table, int rows, string step)
    {
        for (int c = 0; c < table.Columns.Length; c++)
        {
            var info = ComponentInfo.Of(table.Components[c]);
            if (info.Managed)
            {
                var values = table.Columns[c];
                object empty = Activator.CreateInstance(values.GetType().GetElementType()!); // a component struct's default
                for (int i = rows; i < values.Length; i++) Assert.That(values.GetValue(i), Is.EqualTo(empty), $"{step}: managed column {c}, row {i}");
                continue;
            }

            var column = (byte[])table.Columns[c];
            int head = Head(column);
            Assert.That(column.AsSpan(0, head).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: column {c}, slack before row 0");
            Assert.That(column.AsSpan(head + rows * info.Size).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: column {c}, past row {rows}");
        }

        Assert.That(MemoryMarshal.AsBytes(table.Slots.AsSpan(rows)).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: entity rows");
        int words = (rows + 63) >> 6, blocks = (rows + 4095) >> 12;
        for (int c = 0; c < table.Columns.Length; c++)
        {
            if (table.Present[c] is { } bits) Assert.That(bits.AsSpan(words).IndexOfAnyExcept(0UL), Is.EqualTo(-1), $"{step}: presence bits {c}");
            if (table.Words[c] == null) continue;
            Assert.That(MemoryMarshal.AsBytes(table.Words[c].AsSpan(words)).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: word ticks {c}");
            Assert.That(MemoryMarshal.AsBytes(table.Blocks[c].AsSpan(blocks)).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: block ticks {c}");
            Assert.That(MemoryMarshal.AsBytes(table.Removals[c].AsSpan(blocks)).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"{step}: removal ticks {c}");
        }
    }

    [Test]
    public void ATableGrownOverDirtyMemoryReadsZeroPastItsRows()
    {
        if (!DirtyThePinnedHeap()) Assert.Inconclusive("the GC handed out no memory dirtied before: a missing clear would not show");
        using var world = new World();
        var entities = new Entity[Many];
        Archetype table = null;
        int capacity = 0;
        for (int i = 0; i < Many; i++)
        {
            entities[i] = world.CreateEntity(new ResizeValue(i + 1), new ResizeTracked(i + 1), new ResizeName("e"));
            table ??= entities[i].LocateArchetype(out _, true);
            if (i % 7 == 3) entities[i].Remove<ResizeTracked>(); // presence bits, kept in place
            if (table.Slots.Length == capacity) continue;
            capacity = table.Slots.Length;
            AssertClearPast(table, table.Count, $"grown to {capacity}");
            DirtyThePinnedHeap(); // the next step's arrays land on dirty memory too
        }

        AssertClearPast(table, table.Count, "end");
        for (int i = 0; i < Many; i++)
        {
            Assert.That(entities[i].Get<ResizeValue>().Value, Is.EqualTo(i + 1), $"entity {i}");
            Assert.That(entities[i].Has<ResizeTracked>(), Is.EqualTo(i % 7 != 3), $"entity {i}");
            if (i % 7 != 3) Assert.That(entities[i].Read<ResizeTracked>().Value, Is.EqualTo(i + 1), $"entity {i}");
            Assert.That(entities[i].Get<ResizeName>().Name, Is.EqualTo("e"), $"entity {i}");
        }
    }

    [Test]
    public void AComponentAddedInAGrownTableReadsItsDefault()
    {
        using var world = new World();
        for (int i = 0; i < Many; i += 4096)
        {
            DirtyThePinnedHeap();
            world.CreateEntities(new Entity[4096], new ResizeValue(1), new ResizeTracked(-1));
        }

        DirtyThePinnedHeap();
        for (int i = 0; i < Many; i++) // each entity moves into the table of both, which grows past 524,288 rows on the way
        {
            var entity = world.CreateEntity(new ResizeValue(2));
            Assert.That(entity.GetOrAdd<ResizeTracked>().Value, Is.Zero, $"entity {i}");
        }
    }

    [Test]
    public void ATrimmedTableKeepsItsRowsAndClearsItsSlack()
    {
        using var world = new World();
        var entities = new Entity[Many];
        world.CreateEntities(entities, new ResizeValue(0), new ResizeTracked(0));
        for (int i = 0; i < Many; i++) entities[i].Get<ResizeValue>() = new ResizeValue(i);
        for (int i = Many - 1; i >= 1000; i--) entities[i].Destroy();
        DirtyThePinnedHeap();
        world.TrimExcess();
        var table = entities[0].LocateArchetype(out _, true);
        Assert.That(table.Slots.Length, Is.LessThan(Many));
        for (int c = 0; c < table.Columns.Length; c++)
        {
            var column = (byte[])table.Columns[c];
            int head = Head(column), end = head + table.Slots.Length * ComponentInfo.Of(table.Components[c]).Size;
            Assert.That(column.AsSpan(0, head).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"column {c}, slack before row 0");
            Assert.That(column.AsSpan(end).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"column {c}, slack after the rows");
        }

        for (int i = 0; i < 1000; i++) Assert.That(entities[i].Get<ResizeValue>().Value, Is.EqualTo(i));
    }

    [Test]
    public void SavesAndSnapshotsOfWorldsGrownOverOtherDirtMatch()
    {
        // Two worlds grown alike over memory dirtied with other bytes: any byte past the rows reaching a capture, a
        // restore or a save would make them differ.
        byte[] dirt = [0xA5, 0x5A];
        var saves = new ArrayBufferWriter<byte>[2];
        for (int w = 0; w < 2; w++)
        {
            DirtyThePinnedHeap(dirt[w]);
            using var world = new World();
            for (int i = 0; i < Many; i++)
            {
                var entity = world.CreateEntity(new ResizeValue(i * 3L), new ResizeTracked(i));
                if (i % 5 == 0) entity.Remove<ResizeTracked>();
                if (i % 4096 == 0) DirtyThePinnedHeap(dirt[w]);
            }

            var snapshot = world.Capture();
            foreach (var entity in world.Entities) entity.Get<ResizeValue>() = new ResizeValue(-1);
            DirtyThePinnedHeap(dirt[w]);
            world.CreateEntity(new ResizeValue(5)); // a structural change: the restore rewrites the tables
            world.Restore(snapshot);
            saves[w] = new ArrayBufferWriter<byte>();
            world.Save(saves[w]);
        }

        Assert.That(saves[0].WrittenSpan.SequenceEqual(saves[1].WrittenSpan), Is.True, "worlds grown over other dirt save alike");
        DirtyThePinnedHeap();
        using var loaded = new World();
        loaded.Load(saves[0].WrittenSpan);
        Assert.That(loaded.EntityCount, Is.EqualTo(Many));
        long sum = 0;
        foreach (var entity in loaded.Entities) sum += entity.Get<ResizeValue>().Value;
        Assert.That(sum, Is.EqualTo(3L * Many * (Many - 1) / 2));
    }
}
