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
/// Large pinned arrays on 2 MiB pages (HugePages): which arrays are advised, that the kernel backs them with huge
/// pages where transparent huge pages are on, and that worlds, lookups and snapshots read the same values either way.
/// </summary>
public class HugePagesTests
{
    private const int Many = 600_000; // 9.6 MB of entity records, 2.4 MB per int column: past one huge page each

    [Test]
    public void SmallArraysAndOffAreNotAdvised()
    {
        Assert.That(HugePages.Advise(GC.AllocateArray<byte>(64 << 10, pinned: true), 64 << 10), Is.False, "under one huge page");
        bool was = HugePages.Enabled;
        HugePages.Enabled = false;
        try
        {
            Assert.That(HugePages.Advise(GC.AllocateArray<byte>(8 << 20, pinned: true), 8 << 20), Is.False, "off");
        }
        finally
        {
            HugePages.Enabled = was;
        }
    }

    [Test]
    public void OnLinuxTheKernelBacksAnAdvisedArrayWithHugePages()
    {
        if (!HugePages.Enabled) Assert.Ignore("transparent huge pages are off here, or this is not Linux");
        var array = GC.AllocateArray<byte>(16 << 20, pinned: true);
        Assert.That(HugePages.Advise(array, array.Length), Is.True);
        long huge = AnonHugeBytes(array);
        if (huge == 0) Assert.Inconclusive("the kernel found no free huge page (fragmented memory); khugepaged collapses it later");
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
