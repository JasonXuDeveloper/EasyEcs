using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// feat-page-faults: a large pinned array faulted in one call (MADV_POPULATE_WRITE, Linux 5.14+) against one fault per
/// page on its first touch, with 2 MiB pages (<c>Huge</c>: allocated uninitialized, the untouched 2 MiB of its interior
/// dropped and advised MADV_HUGEPAGE, as HugePages.AllocatePinned does) and with 4 KiB pages (<c>Small</c>: the same
/// without the advice). Each invocation allocates the array, writes a byte per 4 KiB and drops it. <see cref="Pause"/>:
/// before each invocation a full collection hands the freed memory back to the system and the thread sleeps 4 s, so a
/// VM that reports free pages to its host (virtio-balloon free page reporting) has discarded it; otherwise the memory
/// is reused at once.
/// </summary>
[MemoryDiagnoser, PageFaultDiagnoser]
public unsafe class PrefaultTouchBenchmark
{
    [Params(128)] public int MiB;

    [Params(false, true)] public bool Pause;

    [IterationSetup]
    public void Rest()
    {
        if (!Pause) return;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        Thread.Sleep(4000);
    }

    [Benchmark(Baseline = true)]
    public int HugeTouched() => HugePageTouchBenchmark.Touch(Make(true, false));

    [Benchmark]
    public int HugePopulated() => HugePageTouchBenchmark.Touch(Make(true, true));

    [Benchmark]
    public int SmallTouched() => HugePageTouchBenchmark.Touch(Make(false, false));

    [Benchmark]
    public int SmallPopulated() => HugePageTouchBenchmark.Touch(Make(false, true));

    private byte[] Make(bool huge, bool populate)
    {
        var array = GC.AllocateUninitializedArray<byte>(MiB << 20, pinned: true);
        var (first, length) = HugePageTouchBenchmark.Interior(array);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        if (length == 0 || (huge && HugePageTouchBenchmark.madvise(first, length, 14) != 0))
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
            bool resident = HugePageTouchBenchmark.mincore((nint)chunk, 2 << 20, residency) != 0;
            for (int i = 0; i < 512 && !resident; i++) resident = (residency[i] & 1) != 0;
            if (resident) new Span<byte>(chunk, 2 << 20).Clear();
            else
            {
                HugePageTouchBenchmark.madvise((nint)chunk, 2 << 20, 4);
                if (populate) HugePageTouchBenchmark.madvise((nint)chunk, 2 << 20, 23);
            }
        }

        return array;
    }
}
