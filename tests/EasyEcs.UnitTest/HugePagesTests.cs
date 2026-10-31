using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct PageMark(int Value) : IComponent;
public record struct PageLink(EntityId Target) : IComponent;

/// <summary>
/// Large pinned arrays on 2 MiB pages (HugePages): zeroed whatever the memory held before, backed by huge pages where
/// transparent huge pages are on, and worlds, lookups and snapshots reading the same values either way.
/// </summary>
public class HugePagesTests
{
    private const int Many = 600_000; // 9.6 MB of entity records, 2.4 MB per int column: past one huge page each

    [Test]
    public void ArraysAreZeroedOverMemoryUsedBefore([Values(false, true)] bool huge, [Values(64 << 10, (6 << 20) + 4093)] int length)
    {
        bool was = HugePages.Enabled;
        HugePages.Enabled = huge && was;
        try
        {
            for (int round = 0; round < 4; round++)
            {
                var array = HugePages.AllocatePinned<byte>(length);
                Assert.That(array.Length, Is.EqualTo(length));
                Assert.That(array.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1), $"round {round}");
                array.AsSpan().Fill(0xA5); // dirty the memory the next round may be handed
                GC.Collect();
            }

            var records = HugePages.AllocatePinned<EntityMeta>((5 << 20) / 16 + 3);
            for (int i = 0; i < records.Length; i++)
                Assert.That(records[i].Archetype == null && records[i].Version == 0 && records[i].Slot == 0, Is.True, $"record {i}");
        }
        finally
        {
            HugePages.Enabled = was;
        }
    }

    [Test]
    public void OnLinuxTheKernelBacksALargeArrayWithHugePages()
    {
        if (!HugePages.Enabled) Assert.Ignore("transparent huge pages are off here, or this is not Linux");
        var array = HugePages.AllocatePinned<byte>(16 << 20);
        for (int i = 0; i < array.Length; i += 4096) array[i] = 1; // the first touch faults the pages
        long huge = AnonHugeBytes(array);
        if (huge == 0) Assert.Inconclusive("no huge page: memory the GC handed out again keeps its 4 KiB pages, or the kernel found no free 2 MiB (fragmented memory); khugepaged collapses an advised range later");
        Assert.That(huge, Is.GreaterThanOrEqualTo(2L << 20));
    }

    [Test]
    public void WorldsReadTheSameWithAndWithoutHugePages([Values(false, true)] bool huge)
    {
        bool was = HugePages.Enabled;
        HugePages.Enabled = huge && was;
        try
        {
            using var world = new World(new WorldOptions { InitialEntityCapacity = Many });
            var entities = new Entity[Many];
            world.CreateEntities(entities, new PageMark(0), new PageLink());
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i].Get<PageMark>() = new PageMark(i);
                entities[i].Get<PageLink>() = new PageLink(entities[(int)((i * 7919L) % Many)].Id);
            }

            var snapshot = world.Capture();
            for (int i = 0; i < entities.Length; i += 3) entities[i].Get<PageMark>() = new PageMark(-1);
            world.Restore(snapshot);

            var markers = new Lookup<PageMark>(world, null);
            for (int i = 0; i < entities.Length; i++)
            {
                var target = markers[entities[i].Get<PageLink>().Target];
                Assert.That(target.HasValue && target.Value.Value == (int)((i * 7919L) % Many), Is.True, $"row {i}");
            }
        }
        finally
        {
            HugePages.Enabled = was;
        }
    }

    [Test]
    public void GrownAndTrimmedWorldsReadTheSameWithAndWithoutHugePages([Values(false, true)] bool huge)
    {
        bool was = HugePages.Enabled;
        HugePages.Enabled = huge && was;
        try
        {
            // Doubling steps (plain pinned arrays), then a reservation past twice the capacity (a sized one), then a trim.
            using var world = new World();
            var batch = new Entity[4096];
            int made = 0;
            for (; made < Many / 2; made += batch.Length)
            {
                world.CreateEntities(batch, new PageMark(0), new PageLink());
                for (int i = 0; i < batch.Length; i++) batch[i].Get<PageMark>() = new PageMark(made + i);
            }

            world.Reserve<PageMark, PageLink>(4 * Many);
            var more = new Entity[Many];
            world.CreateEntities(more, new PageMark(0), new PageLink());
            for (int i = 0; i < more.Length; i++) more[i].Get<PageMark>() = new PageMark(made + i);
            world.TrimExcess();

            long sum = 0, expected = 0;
            int count = 0;
            foreach (var entity in world.Entities)
            {
                sum += entity.Get<PageMark>().Value;
                expected += count++;
            }

            Assert.That(count, Is.EqualTo(made + Many));
            Assert.That(sum, Is.EqualTo(expected));
        }
        finally
        {
            HugePages.Enabled = was;
        }
    }

    /// <summary>The AnonHugePages of the mapping holding the array's first huge page, from /proc/self/smaps.</summary>
    private static unsafe long AnonHugeBytes(byte[] array)
    {
        ulong at = ((ulong)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array)) + (ulong)HugePages.Page - 1) & ~((ulong)HugePages.Page - 1);
        bool inside = false;
        foreach (var line in File.ReadLines("/proc/self/smaps"))
        {
            int dash = line.IndexOf('-');
            int space = line.IndexOf(' ');
            if (dash > 0 && space > dash && ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, null, out ulong start)
                && ulong.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, null, out ulong end))
            {
                inside = start <= at && at < end;
                continue;
            }

            if (inside && line.StartsWith("AnonHugePages:", StringComparison.Ordinal))
                return long.Parse(line.AsSpan(14).Trim().TrimEnd("kB").Trim(), CultureInfo.InvariantCulture) * 1024;
        }

        return 0;
    }
}
