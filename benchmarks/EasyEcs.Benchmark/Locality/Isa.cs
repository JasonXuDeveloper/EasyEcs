using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace EasyEcs.Benchmark.Locality;

/// <summary>
/// The vector instructions the prototypes run on: AVX-512, AVX2, 128-bit SSE (x86 without AVX, as
/// <c>DOTNET_EnableAVX=0</c> makes it), NEON (ARM64) or none. A kernel picks its width by
/// <see cref="Vector256.IsHardwareAccelerated"/> (false on ARM64, where a Vector256 is two 128-bit halves in software)
/// and every x86 or ARM intrinsic sits behind its own <c>IsSupported</c>.
/// </summary>
public static class Isa
{
    public static string Name =>
        Avx512F.IsSupported ? "AVX-512"
        : Avx2.IsSupported ? "AVX2"
        : AdvSimd.Arm64.IsSupported ? "NEON"
        : Sse41.IsSupported ? "SSE4.1"
        : Vector128.IsHardwareAccelerated ? "128-bit"
        : "scalar";

    /// <summary>The line a benchmark's setup prints, so the log of the process that measured says what it ran on.</summary>
    public static void Print() =>
        Console.WriteLine($"// ISA: {Name} (Vector256 {(Vector256.IsHardwareAccelerated ? "hardware" : "software")}, " +
                          $"Vector128 {(Vector128.IsHardwareAccelerated ? "hardware" : "software")})");
}
