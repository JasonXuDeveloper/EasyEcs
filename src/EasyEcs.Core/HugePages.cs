using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The large pinned arrays of the tables on 2 MiB pages (Linux transparent huge pages): the unmanaged columns and the
/// entity records whose size a reservation, the initial capacity or a copy of a table decided (not the steps of a
/// doubling, which a table grown row by row discards one after another). A random lookup across them (a
/// <see cref="Lookup{T}"/> chase, <c>world.TryGetEntity</c>) misses the TLB on nearly every read once they outgrow its
/// reach with 4 KiB pages (a second-level TLB of 1,536 to 2,048 entries reaches 6-8 MiB of them, 3-4 GiB of 2 MiB
/// ones): the page walks saved make such chases 1.1-1.4x faster at 1M entities and 1.3-1.7x at 16M
/// (LookupChaseHugePagesBenchmark).
/// <para>
/// How an array gets there: it is allocated uninitialized and its 2 MiB-aligned interior advised
/// <c>MADV_HUGEPAGE</c> before anything touches it. Each 2 MiB the process never touched is dropped
/// (<c>MADV_DONTNEED</c>: it reads as zero, and its first touch faults one zeroed huge page); each 2 MiB the GC hands
/// out again (resident, used by an array freed before) is zeroed in place, keeping the pages it has (huge ones stay
/// huge, small ones stay small until khugepaged collapses the advised range), since dropping and faulting it again
/// costs the kernel what the GC's own zeroing does not (dropping every 2 MiB made a world grown to 16M entities
/// 1.2-1.5x slower); the head and tail around the interior are zeroed here. Not advised after the GC zeroed the array:
/// pages it touched stay small, and a 16M-entity world was made 1.2-1.3x slower. Not collapsed (<c>MADV_COLLAPSE</c>): each synchronous collapse
/// costs 13-19 ms of kernel time and the chases gain the same without it.
/// </para>
/// The arrays stay ordinary pinned arrays the GC frees, so a <c>ref</c> kept across a resize still reads live memory: no
/// native block to free late. Nothing elsewhere: other systems, THP set to <c>never</c>, a libc without
/// <c>madvise</c> or <c>mincore</c> (the first failure turns it off).
/// </summary>
internal static unsafe partial class HugePages
{
    /// <summary>
    /// Whether arrays are advised: Linux with transparent huge pages not set to <c>never</c>. Settable by tests and the
    /// benchmark's off / on comparison; nothing else writes it but a failed call.
    /// </summary>
    internal static bool Enabled = OperatingSystem.IsLinux() && ThpAvailable();

    /// <summary>A huge page: 2 MiB on x86-64 and on arm64 with 4 KiB base pages.</summary>
    internal const nint Page = 2 << 20;

    private const int BasePage = 4096;
    private const int MadvDontNeed = 4;
    private const int MadvHugePage = 14;

    /// <summary>
    /// A zeroed array of <paramref name="length"/> elements on the pinned object heap, its whole 2 MiB pages on huge
    /// pages where they are on (an ordinary pinned array otherwise, or under two huge pages). O(length) for the zeroing,
    /// as <c>GC.AllocateArray</c>; a <c>mincore</c> call per 2 MiB.
    /// </summary>
    internal static T[] AllocatePinned<T>(int length) where T : struct
    {
        nint bytes = (nint)length * Unsafe.SizeOf<T>();
        if (!Enabled || bytes < 2 * Page) return GC.AllocateArray<T>(length, pinned: true);
        // An element with references (EntityMeta's table) comes zeroed by the GC whatever is asked: advised as it is.
        bool zeroed = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        var array = zeroed ? GC.AllocateArray<T>(length, pinned: true) : GC.AllocateUninitializedArray<T>(length, pinned: true);
        byte* start = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
        byte* first = (byte*)(((nuint)start + (nuint)Page - 1) & ~((nuint)Page - 1));
        byte* end = (byte*)(((nuint)start + (nuint)bytes) & ~((nuint)Page - 1));
        if (!Advise(first, (nuint)(end - first)) || zeroed)
        {
            if (!zeroed) new Span<T>(array).Clear();
            return array;
        }

        new Span<byte>(start, (int)(first - start)).Clear();
        new Span<byte>(end, (int)(start + bytes - end)).Clear();
        byte* residency = stackalloc byte[(int)(Page / BasePage)];
        for (byte* chunk = first; chunk < end; chunk += Page)
        {
            // Untouched: dropped (it may hold swapped-out data, so nothing is assumed zero); resident: zeroed in place.
            if (Untouched(chunk, residency)) madvise(chunk, (nuint)Page, MadvDontNeed);
            else new Span<byte>(chunk, (int)Page).Clear();
        }

        return array;
    }

    /// <summary>Whether no 4 KiB page of the 2 MiB at <paramref name="chunk"/> is resident (false when unknown).</summary>
    private static bool Untouched(byte* chunk, byte* residency)
    {
        if (mincore(chunk, (nuint)Page, residency) != 0) return false;
        for (int i = 0; i < (int)(Page / BasePage); i++)
            if ((residency[i] & 1) != 0) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Advise(void* first, nuint length)
    {
        try
        {
            if (madvise(first, length, MadvHugePage) != 0) return false;
            byte probe;
            return mincore(first, (nuint)BasePage, &probe) == 0; // both calls exist: the loop above can use them
        }
        catch (Exception) // no libc.so.6 (musl), or no madvise / mincore in it
        {
            Enabled = false;
            return false;
        }
    }

    private static bool ThpAvailable()
    {
        try
        {
            return !File.ReadAllText("/sys/kernel/mm/transparent_hugepage/enabled").Contains("[never]", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "madvise")]
    private static partial int madvise(void* address, nuint length, int advice);

    [LibraryImport("libc.so.6", EntryPoint = "mincore")]
    private static partial int mincore(void* address, nuint length, byte* residency);
}
