using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// #164: what a large pinned array costs from allocation to its last page touched, by how it gets onto 2 MiB pages
/// (Linux transparent huge pages). <c>Plain</c>: zeroed by the GC, 4 KiB pages. <c>Collapse</c>: zeroed, then its
/// aligned interior advised MADV_HUGEPAGE and collapsed (MADV_COLLAPSE: the kernel copies the 4 KiB pages the zeroing
/// touched into huge ones). <c>Advise</c>: zeroed, then advised only (pages already touched stay small until
/// khugepaged). <c>Dropped</c>: allocated uninitialized, the interior's pages dropped (MADV_DONTNEED: they read as
/// zero again) and advised before anything touches them, the head and tail zeroed by hand: the first touch of each
/// 2 MiB faults one zeroed huge page. <c>Kept</c> (the shipped form, HugePages.AllocatePinned): as <c>Dropped</c> for
/// each 2 MiB no page of which is resident (mincore), the resident ones zeroed in place instead of dropped and faulted
/// again. Each invocation allocates the array, writes a byte per 4 KiB and drops it, so
/// the GC hands out memory it used before, as a world growing its columns gets. Setup prints the AnonHugePages each
/// form leaves.
/// </summary>
[MemoryDiagnoser]
public unsafe partial class HugePageTouchBenchmark
{
    [Params(8, 128)] public int MiB;

    [GlobalSetup]
    public void Setup()
    {
        foreach (var (name, form) in new (string, Func<byte[]>)[] { ("Plain", MakePlain), ("Collapse", MakeCollapsed), ("Advise", MakeAdvised), ("Dropped", MakeDropped), ("Kept", MakeKept) })
        {
            var array = form();
            Touch(array);
            Console.WriteLine($"// {name}: {HugeBytes(array) >> 20} MiB on huge pages in the mapping holding its {MiB} MiB");
        }
    }

    [Benchmark(Baseline = true)]
    public int Plain() => Touch(MakePlain());

    [Benchmark]
    public int Collapse() => Touch(MakeCollapsed());

    [Benchmark]
    public int Advise() => Touch(MakeAdvised());

    [Benchmark]
    public int Dropped() => Touch(MakeDropped());

    [Benchmark]
    public int Kept() => Touch(MakeKept());

    private byte[] MakePlain() => GC.AllocateArray<byte>(MiB << 20, pinned: true);

    private byte[] MakeCollapsed()
    {
        var array = MakePlain();
        var (first, length) = Interior(array);
        if (length > 0 && madvise(first, length, 14) == 0) madvise(first, length, 25);
        return array;
    }

    private byte[] MakeAdvised()
    {
        var array = MakePlain();
        var (first, length) = Interior(array);
        if (length > 0) madvise(first, length, 14);
        return array;
    }

    private byte[] MakeDropped()
    {
        var array = GC.AllocateUninitializedArray<byte>(MiB << 20, pinned: true);
        var (first, length) = Interior(array);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        if (length == 0 || madvise(first, length, 4) != 0)
        {
            array.AsSpan().Clear();
            return array;
        }

        madvise(first, length, 14);
        new Span<byte>(start, (int)((byte*)first - start)).Clear();
        byte* end = (byte*)first + length;
        new Span<byte>(end, (int)(start + array.Length - end)).Clear();
        return array;
    }

    private byte[] MakeKept()
    {
        var array = GC.AllocateUninitializedArray<byte>(MiB << 20, pinned: true);
        var (first, length) = Interior(array);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        if (length == 0 || madvise(first, length, 14) != 0)
        {
            array.AsSpan().Clear();
            return array;
        }

        new Span<byte>(start, (int)((byte*)first - start)).Clear();
        byte* end = (byte*)first + length;
        new Span<byte>(end, (int)(start + array.Length - end)).Clear();
        byte* residency = stackalloc byte[512];
        for (byte* chunk = (byte*)first; chunk < end; chunk += 2 << 20)
        {
            bool resident = mincore((nint)chunk, 2 << 20, residency) != 0;
            for (int i = 0; i < 512 && !resident; i++) resident = (residency[i] & 1) != 0;
            if (resident) new Span<byte>(chunk, 2 << 20).Clear();
            else madvise((nint)chunk, 2 << 20, 4);
        }

        return array;
    }

    private static (nint First, nuint Length) Interior(byte[] array)
    {
        const nuint page = 2 << 20;
        nuint start = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        nuint first = (start + page - 1) & ~(page - 1);
        nuint end = (start + (nuint)array.Length) & ~(page - 1);
        return end > first ? ((nint)first, end - first) : (0, 0);
    }

    private static int Touch(byte[] array)
    {
        for (int i = 0; i < array.Length; i += 4096) array[i] = 1;
        return array[^1];
    }

    private static long HugeBytes(byte[] array)
    {
        ulong at = (ulong)Interior(array).First;
        bool inside = false;
        foreach (var line in File.ReadLines("/proc/self/smaps"))
        {
            int dash = line.IndexOf('-'), space = line.IndexOf(' ');
            if (dash > 0 && space > dash && ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, null, out ulong start)
                && ulong.TryParse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, null, out ulong end))
                inside = start <= at && at < end;
            else if (inside && line.StartsWith("AnonHugePages:", StringComparison.Ordinal))
                return long.Parse(line.AsSpan(14).Trim().TrimEnd("kB").Trim(), CultureInfo.InvariantCulture) * 1024;
        }

        return 0;
    }

    [LibraryImport("libc.so.6", EntryPoint = "madvise")]
    private static partial int madvise(nint address, nuint length, int advice);

    [LibraryImport("libc.so.6", EntryPoint = "mincore")]
    private static partial int mincore(nint address, nuint length, byte* residency);
}
