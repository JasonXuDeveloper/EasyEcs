using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EasyEcs;

/// <summary>
/// The large pinned arrays of the tables on 2 MiB pages (Linux transparent huge pages): the unmanaged columns (a
/// table's, a snapshot's copy) and the entity records sized by a reservation. A random lookup across them (a
/// <see cref="Lookup{T}"/> chase, <c>world.TryGetEntity</c>) misses the TLB on nearly every read once they outgrow its
/// reach with 4 KiB pages (a second-level TLB of 1,536 to 2,048 entries reaches 6-8 MiB of them, 3-4 GiB of 2 MiB
/// ones): the page walks saved make such chases 1.2-1.3x faster at 1M entities and 1.3-1.6x at 16M
/// (LookupChaseHugePagesBenchmark). The 2 MiB-aligned interior of an
/// array is advised <c>MADV_HUGEPAGE</c> and collapsed at once (<c>MADV_COLLAPSE</c>, Linux 6.1; an older kernel
/// refuses it and khugepaged collapses the range later). The arrays stay ordinary pinned arrays the GC frees, so a
/// <c>ref</c> kept across a resize still reads live memory: no native block to free late. Nothing elsewhere: other
/// systems, THP set to <c>never</c>, a libc without <c>madvise</c> (the first failure turns it off).
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

    private const int MadvHugePage = 14;
    private const int MadvCollapse = 25;

    /// <summary>
    /// Advise the whole 2 MiB pages inside the first <paramref name="bytes"/> bytes of <paramref name="pinned"/> (an
    /// array on the pinned object heap). False when nothing was advised (off, or no whole page inside). O(bytes) when
    /// the kernel collapses pages already touched (a copy, once per allocation of the array).
    /// </summary>
    internal static bool Advise(Array pinned, nint bytes)
    {
        if (!Enabled || bytes < Page) return false;
        nuint start = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pinned));
        nuint first = (start + (nuint)Page - 1) & ~((nuint)Page - 1);
        nuint end = (start + (nuint)bytes) & ~((nuint)Page - 1);
        return end > first && Madvise((void*)first, end - first);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Madvise(void* first, nuint length)
    {
        try
        {
            if (madvise(first, length, MadvHugePage) != 0) return false;
            madvise(first, length, MadvCollapse); // best effort: refused by kernels before 6.1 and when memory is short
            return true;
        }
        catch (Exception) // no libc.so.6 (musl) or no madvise in it
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
}
