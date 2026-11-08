using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs;

/// <summary>
/// The one software prefetch every prefetching path calls (lookup chases, index probes, the core's loops over lists of
/// ids): a line into the cache, issued without waiting for it. x86: <c>PREFETCHT0</c> (<see cref="Sse.Prefetch0"/>).
/// ARM64: .NET 10 has no <c>PRFM</c> intrinsic; where SVE is hardware (Graviton 3 and 4, Azure Cobalt 100, Neoverse V1,
/// N2 and V2: not Apple silicon, whose SVE runs only in streaming mode) SVE's <c>PRFB</c> with a predicate of one
/// element prefetches the line of one byte. Elsewhere (ARM64 without SVE, WebAssembly) nothing: the read that follows
/// misses as it would without it, and <see cref="IsSupported"/> is false, so the gates read once per pass keep today's
/// loops and never even read the cache size.
/// </summary>
/// <remarks>
/// The SVE API is <c>[Experimental("SYSLIB5003")]</c> in .NET 10. EasyEcs uses one call of it, here, behind
/// <see cref="Sve.IsSupported"/> (a JIT-time constant, so no other machine compiles the call), with the diagnostic
/// suppressed at that call only; every other machine runs the x86 path or none. A runtime that changes the API gets an
/// EasyEcs built for it (the package targets one .NET at a time).
/// </remarks>
internal static unsafe class Prefetching
{
    /// <summary>Whether <see cref="Line"/> issues a prefetch on this machine (a JIT-time constant).</summary>
    public static bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable SYSLIB5003 // Sve is experimental in .NET 10: IsSupported only, here and in Line
        get => Sse.IsSupported || Sve.IsSupported;
#pragma warning restore SYSLIB5003
    }

    /// <summary>
    /// The cache line holding <paramref name="address"/> into every level of the cache, without waiting for it. A
    /// prefetch never faults: an address the GC has moved, or one past what is mapped, only wastes it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Line(void* address)
    {
        if (Sse.IsSupported) Sse.Prefetch0(address);
#pragma warning disable SYSLIB5003 // the one call of the experimental SVE API (see the remarks)
        else if (Sve.IsSupported) Sve.Prefetch8Bit(Sve.CreateTrueMaskByte(SveMaskPattern.VectorCount1), address, SvePrefetchType.LoadL1Temporal);
#pragma warning restore SYSLIB5003
    }
}
