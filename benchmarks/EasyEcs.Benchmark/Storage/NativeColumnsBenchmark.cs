using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// #159, allocators: what a world's unmanaged arrays would gain on native memory (<c>NativeMemory.AlignedAlloc</c>, the
/// C runtime's allocator, which mimalloc would replace) instead of the pinned object heap they live on (D28). A world of
/// <see cref="Rows"/> entities holds five such arrays (three columns of 4, 12 and 8 bytes, the 16-byte entity records,
/// the 8-byte entity rows: 48 bytes a row); each case allocates them, writes every byte once (the first touch a
/// creation pays) and lets them go, as a world created and disposed does. <c>Pinned</c> is today's allocation; the
/// native forms free at the end (a world's Dispose), with and without telling the GC of the memory
/// (<c>AddMemoryPressure</c>, which a library holding native memory owes it). <c>WorldCreate</c> is the whole operation
/// (a world created with that capacity, the entities made in bulk, disposed), to read the allocation's share of it.
/// The prototype decides whether the core's columns are worth moving off the GC heap: a large refactor (every column
/// is an <c>Array</c> today), so it is built only past the noise in both rounds at every size.
/// </summary>
[MemoryDiagnoser, PageFaultDiagnoser]
public unsafe class NativeColumnsBenchmark
{
    [Params(100_000, 1_000_000, 4_000_000)] public int Rows;

    private static readonly int[] RowBytes = [4, 12, 8, 16, 8];
    private readonly byte[][] _pinned = new byte[5][];
    private readonly void*[] _native = new void*[5];

    [Benchmark(Baseline = true)]
    public long Pinned()
    {
        long sum = 0;
        for (int i = 0; i < RowBytes.Length; i++)
        {
            int bytes = Rows * RowBytes[i];
            var array = GC.AllocateUninitializedArray<byte>(bytes + 64, pinned: true);
            array.AsSpan().Fill(1);
            _pinned[i] = array;
            sum += array[bytes - 1];
        }

        Array.Clear(_pinned);
        return sum;
    }

    [Benchmark]
    public long Native() => NativeColumns(false);

    [Benchmark]
    public long NativeWithPressure() => NativeColumns(true);

    private long NativeColumns(bool pressure)
    {
        long sum = 0;
        for (int i = 0; i < RowBytes.Length; i++)
        {
            int bytes = Rows * RowBytes[i];
            var column = NativeMemory.AlignedAlloc((nuint)bytes, 64);
            if (pressure) GC.AddMemoryPressure(bytes);
            new Span<byte>(column, bytes).Fill(1);
            _native[i] = column;
            sum += ((byte*)column)[bytes - 1];
        }

        for (int i = 0; i < RowBytes.Length; i++)
        {
            NativeMemory.AlignedFree(_native[i]);
            if (pressure) GC.RemoveMemoryPressure(Rows * RowBytes[i]);
            _native[i] = null;
        }

        return sum;
    }

    [Benchmark]
    public int WorldCreate()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = Rows });
        world.Reserve<ColumnHp, ColumnPlace, ColumnDrift>(Rows);
        var entities = GC.AllocateUninitializedArray<Entity>(Rows);
        world.CreateEntities(entities, new ColumnHp(1), new ColumnPlace(Vector3.One), new ColumnDrift(2));
        return world.EntityCount;
    }

    public record struct ColumnHp(int Value) : IComponent;
    public record struct ColumnPlace(Vector3 Value) : IComponent;
    public record struct ColumnDrift(long Value) : IComponent;
}
