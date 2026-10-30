using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// A large copy of a column (borrowed-ideas.md item 5: the full copies of Capture / Restore, saves and column
/// resizes), the core's <c>Unsafe.CopyBlockUnaligned</c> (the C runtime's memmove) against a loop of non-temporal
/// stores (Locality/Streams.cs). The source is 64-byte aligned like a table's column, the target 16 bytes past a
/// line like a byte[]'s data (<see cref="ToTable"/> swaps them: a restore). 16 MiB sits in the last-level cache, 192
/// MiB in it but past glibc's streaming threshold, 512 MiB past it (260 MiB on the reference VM).
/// </summary>
[MemoryDiagnoser, IsaColumn]
public unsafe class StreamingCopyBenchmark
{
    [Params(16 << 20, 192 << 20, 512 << 20)] public int Bytes;

    /// <summary>Whether the target is the aligned side (a restore into a table) instead of the source (a capture).</summary>
    [Params(false, true)] public bool ToTable;

    private byte* _a, _b, _src, _dst;

    [GlobalSetup]
    public void Setup()
    {
        Isa.Print();
        _a = (byte*)NativeMemory.AlignedAlloc((nuint)Bytes + 64, 64);
        _b = (byte*)NativeMemory.AlignedAlloc((nuint)Bytes + 64, 64);
        for (nint i = 0; i < Bytes + 64; i++)
        {
            _a[i] = (byte)i;
            _b[i] = 0;
        }

        _src = ToTable ? _b + 16 : _a;
        _dst = ToTable ? _a : _b + 16;
        Check(&Streams.NonTemporalCopy);
        Check(&Streams.NonTemporalCopyPages);
    }

    private void Check(delegate*<byte*, byte*, nuint, void> copy)
    {
        new Span<byte>(_dst, Bytes).Clear();
        copy(_src, _dst, (nuint)Bytes);
        if (!new ReadOnlySpan<byte>(_src, Bytes).SequenceEqual(new ReadOnlySpan<byte>(_dst, Bytes)))
            throw new InvalidOperationException("A streamed copy differs");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree(_a);
        NativeMemory.AlignedFree(_b);
    }

    [Benchmark(Baseline = true)]
    public void Memmove() => Streams.Memmove(_src, _dst, (nuint)Bytes);

    [Benchmark]
    public void NonTemporal() => Streams.NonTemporalCopy(_src, _dst, (nuint)Bytes);

    [Benchmark]
    public void NonTemporalPages() => Streams.NonTemporalCopyPages(_src, _dst, (nuint)Bytes);
}
