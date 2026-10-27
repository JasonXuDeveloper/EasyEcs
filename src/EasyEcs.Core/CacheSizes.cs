using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The sizes of the processor's caches the prefetch gates compare a pass's memory with, read once per process from the
/// operating system the way the runtime's GC sizes its gen0 budget (dotnet/runtime <c>gcenv.windows.cpp</c> /
/// <c>gcenv.unix.cpp</c>, GetLogicalProcessorCacheSizeFromOS); .NET has no public API for them. A gate per form
/// (borrowed-ideas.md item 1): a probe on one thread and a vector loop's chases prefetch only past
/// <see cref="LastLevel"/> (just past the L2 a prefetch costs them more than it hides); the forms that pay as soon as
/// the data leaves the L2 (the row form's chases of other entities, on one thread, in parallel, in fused walks and over
/// a buffer's elements; parallel probes; the core's lists of ids) past <see cref="L2OrLastLevel"/>.
/// Read only where a prefetch exists (<see cref="Prefetching.IsSupported"/>: x86, ARM64 with SVE): other ARM64 and
/// WebAssembly never read them. 0 means unknown: an operating system not listed, a read that fails or returns nothing.
/// </summary>
internal static partial class CacheSizes
{
    /// <summary>
    /// The last-level cache in bytes, 0 when unknown (never past it: no prefetch). Settable by tests and the
    /// benchmarks' forced gates; nothing else writes it.
    /// </summary>
    internal static long LastLevel = Prefetching.IsSupported ? ReadLastLevel() : 0;

    /// <summary>
    /// One core's L2 in bytes (the largest data or unified level-2 cache, so a hybrid processor's larger one), 0 when
    /// unknown. Settable as <see cref="LastLevel"/> is.
    /// </summary>
    internal static long L2 = Prefetching.IsSupported ? ReadL2() : 0;

    /// <summary>
    /// The gate of the forms that pay past the L2: <see cref="L2"/>, or <see cref="LastLevel"/> when the L2 is unknown,
    /// so an unknown L2 is never more eager than the last-level gate.
    /// </summary>
    internal static long L2OrLastLevel => L2 > 0 ? L2 : LastLevel;

    /// <summary>The operating system's last-level cache, 0 when it gives none (every failure included).</summary>
    internal static long ReadLastLevel() => Read(0);

    /// <summary>The operating system's L2, 0 when it gives none (every failure included).</summary>
    internal static long ReadL2() => Read(2);

    /// <summary>The cache of <paramref name="level"/> (0: the last level, the largest), 0 when unknown.</summary>
    private static long Read(int level)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return FromWindows(level);
            if (OperatingSystem.IsLinux()) return FromSysfs("/sys/devices/system/cpu/cpu0/cache", level) is var bytes and > 0 ? bytes : FromSysconf(level);
            if (OperatingSystem.IsMacOS()) return FromSysctl(level);
        }
        catch (Exception)
        {
            // A missing file, a library or entry point that is not there (musl's libc, a sandbox): unknown.
        }

        return 0;
    }

    /// <summary>
    /// Linux's cache descriptions under <paramref name="root"/>: the largest <c>index*/size</c> ("266240K": the last
    /// level's) for <paramref name="level"/> 0, else the largest of the data and unified caches whose <c>level</c> file
    /// says <paramref name="level"/> (cpu0's L2: "2048K"); 0 when there is none.
    /// </summary>
    internal static long FromSysfs(string root, int level = 0)
    {
        if (!Directory.Exists(root)) return 0;
        long largest = 0;
        foreach (var index in Directory.GetDirectories(root, "index*"))
        {
            string size = Path.Combine(index, "size");
            if (!File.Exists(size)) continue;
            if (level != 0)
            {
                string levelFile = Path.Combine(index, "level"), type = Path.Combine(index, "type");
                if (!File.Exists(levelFile) || ParseSize(File.ReadAllText(levelFile)) != level) continue;
                if (File.Exists(type) && File.ReadAllText(type).AsSpan().Trim().SequenceEqual("Instruction")) continue;
            }

            largest = Math.Max(largest, ParseSize(File.ReadAllText(size)));
        }

        return largest;
    }

    /// <summary>A sysfs size ("48K", "2048K", "260M", a plain number of bytes), 0 for anything else.</summary>
    internal static long ParseSize(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        long unit = 1;
        if (text.Length > 0)
        {
            switch (char.ToUpperInvariant(text[^1]))
            {
                case 'K': unit = 1L << 10; break;
                case 'M': unit = 1L << 20; break;
                case 'G': unit = 1L << 30; break;
            }

            if (unit != 1) text = text[..^1];
        }

        return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value * unit
            : 0;
    }

    private const int ScLevel2CacheSize = 191; // _SC_LEVEL2_CACHE_SIZE (glibc)
    private const int ScLevel3CacheSize = 194; // _SC_LEVEL3_CACHE_SIZE (glibc)

    /// <summary>
    /// glibc's <c>sysconf(_SC_LEVEL3_CACHE_SIZE)</c> for the last level (0), <c>_SC_LEVEL2_CACHE_SIZE</c> for the L2; 0
    /// when it gives none (-1, or a libc without it).
    /// </summary>
    internal static long FromSysconf(int level = 0) => Math.Max(Sysconf(level == 2 ? ScLevel2CacheSize : ScLevel3CacheSize), 0);

    /// <summary>
    /// macOS. The last level: <c>hw.l3cachesize</c>, else <c>hw.perflevel0.l2cachesize</c> (Apple silicon has no L3; its
    /// performance cluster's shared L2 is the last level the system names). The L2: <c>hw.perflevel0.l2cachesize</c>,
    /// else <c>hw.l2cachesize</c> (Intel Macs). Only Intel Macs read them today: Apple silicon has no prefetch .NET can
    /// issue (<see cref="Prefetching.IsSupported"/> is false: no SSE, and its SVE runs in streaming mode only), so its
    /// gates are off without a read. Were a form ever gated on them there, the cluster's L2 (16 MB on an M4's
    /// performance cores) understates what the cores share, since the system level cache behind it is not exposed:
    /// such a gate would turn on early, for worlds that still fit that cache, never late.
    /// </summary>
    internal static unsafe long FromSysctl(int level = 0)
    {
        if (level == 2)
            return Sysctl("hw.perflevel0.l2cachesize\0"u8) is var l2 and > 0 ? l2 : Sysctl("hw.l2cachesize\0"u8);
        long l3 = Sysctl("hw.l3cachesize\0"u8);
        return l3 > 0 ? l3 : Sysctl("hw.perflevel0.l2cachesize\0"u8);
    }

    private static unsafe long Sysctl(ReadOnlySpan<byte> name)
    {
        long value = 0;
        nint length = sizeof(long);
        fixed (byte* text = name)
            return SysctlByName(text, &value, &length, null, 0) == 0 && length is 4 or 8 ? (length == 4 ? (int)value : value) : 0;
    }

    /// <summary>
    /// Windows: <c>GetLogicalProcessorInformationEx(RelationCache)</c> describes every cache of every level, one record
    /// each: the largest of them for <paramref name="level"/> 0 (the last level's), else the largest data or unified
    /// cache of that level.
    /// </summary>
    internal static unsafe long FromWindows(int level = 0)
    {
        const int relationCache = 2, instructionCache = 1;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationCache, null, &length); // fails with the length it needs
        if (length == 0) return 0;
        var buffer = new byte[length];
        long largest = 0;
        fixed (byte* start = buffer)
        {
            if (GetLogicalProcessorInformationEx(relationCache, start, &length) == 0) return 0;
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (4 bytes), Size (4), then CACHE_RELATIONSHIP:
            // Level (1) at byte 8, Associativity (1), LineSize (2), CacheSize (4) at byte 12, Type (4) at byte 16.
            for (uint at = 0; at + 20 <= length;)
            {
                byte* record = start + at;
                uint size = *(uint*)(record + 4);
                if (*(int*)record == relationCache && (level == 0 || (record[8] == level && *(int*)(record + 16) != instructionCache)))
                    largest = Math.Max(largest, *(uint*)(record + 12));
                if (size == 0) break;
                at += size;
            }
        }

        return largest;
    }

    [LibraryImport("libc.so.6", EntryPoint = "sysconf")]
    private static partial long Sysconf(int name);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctlbyname")]
    private static unsafe partial int SysctlByName(byte* name, void* value, nint* length, void* newValue, nint newLength);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetLogicalProcessorInformationEx(int relationship, byte* buffer, uint* length);
}
