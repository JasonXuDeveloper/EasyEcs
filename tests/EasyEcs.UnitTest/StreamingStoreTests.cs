using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Fuel(float Value) : IComponent;
public record struct Flare(float Value) : IComponent;
public record struct Spark(int Value) : IComponent;
public record struct Torch(double Value) : IComponent;
public record struct Breeze3(float X, float Y, float Z) : IComponent;
public record struct Wake3(float X, float Y, float Z) : IComponent;

/// <summary>
/// Methods writing a column without reading it, whose vector loops stream that column past the last-level cache
/// (StreamingStores), and methods that read what they write, which keep plain stores.
/// </summary>
public static class StreamKernels
{
    [ForEach]
    internal static void Shine(in Fuel fuel, ref Flare flare) => flare.Value = fuel.Value * 1.5f + 2f;

    [ForEach]
    internal static void Count(in Fuel fuel, ref Spark spark) => spark.Value = (int)fuel.Value * 3 - 7;

    // A float row widened into a double column: the column takes two vectors per row vector (Kernel.Pairs.cs).
    [ForEach]
    internal static void Widen(in Fuel fuel, ref Torch torch) => torch.Value = fuel.Value * 0.25;

    // Each field alike: the flat loop.
    [ForEach]
    internal static void Follow(in Breeze3 breeze, ref Wake3 wake) => wake = new Wake3(breeze.X * 2f, breeze.Y * 2f, breeze.Z * 2f);

    // Chunks of 1,100 rows: the second starts 4,400 bytes into the column, so its stream starts after a head of rows.
    [ForEach(Parallel = true, ChunkSize = 1100)]
    internal static void ShineParallel(in Fuel fuel, ref Flare flare) => flare.Value = fuel.Value * 0.5f - 1f;

    // Reads what it writes, and writes under a condition: plain stores.
    [ForEach]
    internal static void Brighten(in Fuel fuel, ref Flare flare) => flare.Value += fuel.Value;

    [ForEach]
    internal static void Dim(in Fuel fuel, ref Flare flare)
    {
        if (fuel.Value > 10f) flare.Value = 0f;
    }
}

/// <summary>
/// Streaming stores in generated vector loops (StreamingStores, Kernel.Streams.cs): which columns stream, the gate,
/// the results bit for bit against the method row by row with the gate on and off, heads of rows, 0 B.
/// </summary>
public class StreamingStoreTests
{
    /// <summary>Runs <paramref name="body"/> with the last-level cache taken as <paramref name="bytes"/>, then restores it.</summary>
    private static void WithCache(long bytes, Action body)
    {
        long was = CacheSizes.LastLevel;
        CacheSizes.LastLevel = bytes;
        try { body(); }
        finally { CacheSizes.LastLevel = was; }
    }

    private static float Input(int i) => (i * 37 % 1001 - 500) * 0.37f + (i % 7 == 0 ? float.NaN : 0);

    /// <summary>The gate: a machine with the stores, a run of at least MinRows, the table's columns past the cache.</summary>
    [Test]
    public void StreamsOnlyPastTheLastLevelCache()
    {
        using var world = new World();
        world.Reserve<Fuel, Flare>(4000);
        for (int i = 0; i < 4000; i++) world.CreateEntity(new Fuel(i), new Flare(0));
        bool pays = false;
        var job = new GateProbe();
        WithCache(4000L * 8 - 1, () => { world.ForEachBatch<Fuel, Flare, GateProbe>(ref job); pays = job.Pays; });
        Assert.That(pays, Is.EqualTo(StreamingStores.IsSupported), "past the cache");
        WithCache(4000L * 8, () => { world.ForEachBatch<Fuel, Flare, GateProbe>(ref job); pays = job.Pays; });
        Assert.That(pays, Is.False, "in the cache");
        WithCache(0, () => { world.ForEachBatch<Fuel, Flare, GateProbe>(ref job); pays = job.Pays; });
        Assert.That(pays, Is.False, "an unknown cache");
        job.Rows = StreamingStores.MinRows - 1;
        WithCache(1, () => { world.ForEachBatch<Fuel, Flare, GateProbe>(ref job); pays = job.Pays; });
        Assert.That(pays, Is.False, "a short run");
    }

    private struct GateProbe : IForEachBatch<Fuel, Flare>
    {
        public nint Rows;
        public bool Pays;

        public void Execute(EntitySpan entities, Span<Fuel> fuels, Span<Flare> flares) =>
            Pays = StreamingStores.Pays(in entities, Rows > 0 ? Rows : fuels.Length, 8);
    }

    /// <summary>
    /// What the generator writes: a method of the job (<c>__Stream</c>) behind the gate, the column's load dropped and
    /// its stores non-temporal; nothing for a method that reads what it writes or writes under a condition, nor in the
    /// masked words; the schedule's line names it.
    /// </summary>
    [Test]
    public void OnlyColumnsWrittenWithoutBeingReadStream()
    {
        var compilation = GeneratorTests.Compile("""
            namespace Game;
            public struct In : IComponent { public float Value; }
            public struct Out : IComponent { public float Value; }
            public struct Big : IComponent { public double Value; }
            public record struct V3(float X, float Y, float Z) : IComponent;
            public record struct P3(float X, float Y, float Z) : IComponent;
            public static class K
            {
                [ForEach] public static void Set(in In a, ref Out b) => b.Value = a.Value * 2f;
                [ForEach] public static void Add(in In a, ref Out b) => b.Value += a.Value;
                [ForEach] public static void Some(in In a, ref Out b) { if (a.Value > 0) b.Value = a.Value; }
                [ForEach] public static void Late(in In a, ref Out b) { b.Value = a.Value; b.Value = b.Value * 3f; }
                [ForEach] public static void Wide(in In a, ref Big b) => b.Value = a.Value;
                [ForEach] public static void Flat(in V3 v, ref P3 p) => p = new P3(v.X, v.Y, v.Z);
                [ForEach] public static void Own(ref Out b) => b.Value = 1f;
            }
            """);
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string text = string.Concat(output.SyntaxTrees.Where(GeneratorTests.IsCode).Select(t => t.ToString()));
        string Job(string name) => text.Split("internal ref struct ").Single(j => j.StartsWith(name + " "));

        string set = Job("SetJob0");
        Assert.That(set, Does.Contain("if (global::EasyEcs.StreamingStores.Pays(in entities, rows, ").And.Contain("i = __Stream("));
        Assert.That(set, Does.Contain("var __c2_0 = default(global::System.Runtime.Intrinsics.Vector512<float>);")
            .And.Contain("global::EasyEcs.StreamingStores.Store(__c2_0, ref e2, (nuint)i);")
            .And.Contain("global::EasyEcs.StreamingStores.Head(ref e2, i, 1, ").And.Contain("global::EasyEcs.StreamingStores.Fence();"));
        Assert.That(set.Split("public void Execute(").Last(), Does.Not.Contain("StreamingStores.Pays"), "masked words keep plain stores");
        foreach (var plain in new[] { "AddJob1", "SomeJob2" })
            Assert.That(Job(plain), Does.Not.Contain("StreamingStores"), plain);
        Assert.That(Job("LateJob3"), Does.Contain("i = __Stream("), "a value read after the method wrote it is not the column's");
        Assert.That(Job("WideJob4"), Does.Contain("StreamingStores.Store(__c2_0_0, ref e2, ").And.Contain("StreamingStores.Store(__c2_0_1, ref e2, "));
        Assert.That(Job("FlatJob5"), Does.Contain("StreamingStores.Head(ref e2, i * 3, 3, ").And.Contain("StreamingStores.Store(__c2_0, ref e2, (nuint)i);"));
        Assert.That(Job("OwnJob6"), Does.Contain("var __c1_0 = default("), "a column only written: no load at all");

        using var world = new World();
        world.AddSystem<StreamLoopsSystem>();
        var loops = world.DescribeSchedule().ToString();
        Assert.That(loops, Does.Contain("Shine: vector").And.Contain("write-only columns streamed past the last-level cache"));
        Assert.That(loops.Split('\n').Single(l => l.Contains("Brighten: ")), Does.Not.Contain("streamed"));
    }

    /// <summary>
    /// Every write-only form, with the gate on and off, bit for bit against the method row by row (NaN included), over
    /// runs shorter and longer than MinRows, with rows after the last whole vector and after holes (masked words).
    /// </summary>
    [TestCase(StreamingStores.MinRows - 1)]
    [TestCase(StreamingStores.MinRows)]
    [TestCase(4099)]
    [TestCase(10_007)]
    public void StreamedLoopsGiveTheMethodsResults(int count)
    {
        foreach (long cache in new long[] { 0, 1 })
        {
            WithCache(cache, () =>
            {
                using var world = new World();
                var fuels = new Fuel[count];
                var breezes = new Breeze3[count];
                var entities = new Entity[count];
                world.Reserve<Fuel, Flare, Spark, Torch, Breeze3, Wake3>(count);
                for (int i = 0; i < count; i++)
                {
                    fuels[i] = new Fuel(Input(i));
                    breezes[i] = new Breeze3(Input(i + 1), Input(i + 2), -Input(i));
                    entities[i] = world.CreateEntity(fuels[i], new Flare(-1), new Spark(-1), new Torch(-1), breezes[i], new Wake3(-1, -1, -1));
                }
                StreamKernels.Shine(world);
                StreamKernels.Count(world);
                StreamKernels.Widen(world);
                StreamKernels.Follow(world);
                for (int i = 0; i < count; i++)
                {
                    Flare flare = default;
                    Spark spark = default;
                    Torch torch = default;
                    Wake3 wake = default;
                    StreamKernels.Shine(in fuels[i], ref flare);
                    StreamKernels.Count(in fuels[i], ref spark);
                    StreamKernels.Widen(in fuels[i], ref torch);
                    StreamKernels.Follow(in breezes[i], ref wake);
                    var e = entities[i];
                    Assert.That(BitConverter.SingleToInt32Bits(e.Read<Flare>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(flare.Value)), $"Flare {i}, cache {cache}");
                    Assert.That(e.Read<Spark>(), Is.EqualTo(spark), $"Spark {i}");
                    Assert.That(BitConverter.DoubleToInt64Bits(e.Read<Torch>().Value), Is.EqualTo(BitConverter.DoubleToInt64Bits(torch.Value)), $"Torch {i}");
                    Assert.That(Bits(e.Read<Wake3>()), Is.EqualTo(Bits(wake)), $"Wake3 {i}");
                }

                // Rows leaving the table in the middle of it, and a pass reading what it writes in between.
                for (int i = 0; i < count; i += 97) entities[i].Disable<Fuel>();
                StreamKernels.Brighten(world);
                StreamKernels.Shine(world);
                for (int i = 0; i < count; i++)
                {
                    Flare flare = default; // a disabled row kept the first pass's value, the others got it again
                    StreamKernels.Shine(in fuels[i], ref flare);
                    Assert.That(BitConverter.SingleToInt32Bits(entities[i].Read<Flare>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(flare.Value)), $"Flare {i} after holes");
                }
            });
        }
    }

    private static (int, int, int) Bits(Wake3 w) =>
        (BitConverter.SingleToInt32Bits(w.X), BitConverter.SingleToInt32Bits(w.Y), BitConverter.SingleToInt32Bits(w.Z));

    /// <summary>
    /// Parallel chunks that start inside a vector's bytes: each streams after a head of rows through the method, with
    /// the results of the method row by row, on 1 and 4 threads.
    /// </summary>
    [Test]
    public void ParallelChunksStreamAfterTheirHead([Values(1, 4)] int threads)
    {
        WithCache(1, () =>
        {
            const int count = 9_001;
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var entities = new Entity[count];
            world.Reserve<Fuel, Flare>(count);
            for (int i = 0; i < count; i++) entities[i] = world.CreateEntity(new Fuel(Input(i)), new Flare(-1));
            StreamKernels.ShineParallel(world);
            for (int i = 0; i < count; i++)
            {
                Flare flare = default;
                StreamKernels.ShineParallel(new Fuel(Input(i)), ref flare);
                Assert.That(BitConverter.SingleToInt32Bits(entities[i].Read<Flare>().Value), Is.EqualTo(BitConverter.SingleToInt32Bits(flare.Value)), $"row {i}");
            }
        });
    }

    /// <summary>The head: the rows to the first aligned one, whatever the lanes per row; 0 when none is within a vector.</summary>
    [Test]
    public unsafe void TheHeadEndsAtTheFirstAlignedRow()
    {
        var memory = (float*)NativeMemory.AlignedAlloc(4096, 64);
        try
        {
            ref float column = ref *memory;
            Assert.That(StreamingStores.Head(ref column, 0, 1, 64), Is.EqualTo((nint)0));
            Assert.That(StreamingStores.Head(ref column, 1, 1, 64), Is.EqualTo((nint)15));
            Assert.That(StreamingStores.Head(ref column, 13, 1, 32), Is.EqualTo((nint)3));
            Assert.That(StreamingStores.Head(ref column, 3, 3, 64), Is.EqualTo((nint)15), "rows of 12 bytes from row 1: row 16 is at 192");
            ref byte bytes = ref *(byte*)memory;
            Assert.That(StreamingStores.Head(ref Unsafe(ref bytes, 2), 0, 1, 64), Is.EqualTo((nint)0), "2-byte offsets of 4-byte rows never align");
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
        }
    }

    private static ref float Unsafe(ref byte bytes, int offset) =>
        ref System.Runtime.CompilerServices.Unsafe.As<byte, float>(ref System.Runtime.CompilerServices.Unsafe.Add(ref bytes, offset));

    /// <summary>A store at an address that is not aligned is a plain one: the lanes land where a plain store puts them.</summary>
    [Test]
    public unsafe void StoresAtAnyAddressWriteTheirLanes()
    {
        var memory = (float*)NativeMemory.AlignedAlloc(1024, 64);
        try
        {
            for (int offset = 0; offset < 17; offset++)
            {
                new Span<float>(memory, 256).Clear();
                var lanes = Vector<float>.Indices + new Vector<float>(offset);
                StreamingStores.Store(lanes, ref *memory, (nuint)offset);
                if (Vector512.IsHardwareAccelerated) StreamingStores.Store(Vector512<float>.Indices * 2f, ref *memory, (nuint)(offset + 64));
                StreamingStores.Fence();
                for (int i = 0; i < Vector<float>.Count; i++) Assert.That(memory[offset + i], Is.EqualTo(offset + i));
                if (Vector512.IsHardwareAccelerated)
                    for (int i = 0; i < 16; i++) Assert.That(memory[offset + 64 + i], Is.EqualTo(i * 2f));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
        }
    }

    /// <summary>A streamed pass allocates nothing.</summary>
    [Test]
    public void AStreamedPassAllocatesNothing()
    {
        WithCache(1, () =>
        {
            using var world = new World();
            world.Reserve<Fuel, Flare, Breeze3, Wake3>(5000);
            for (int i = 0; i < 5000; i++) world.CreateEntity(new Fuel(i), new Flare(0), new Breeze3(i, i, i), new Wake3());
            StreamKernels.Shine(world);
            StreamKernels.Follow(world);
            Assert.That(Allocations.Of(() =>
            {
                StreamKernels.Shine(world);
                StreamKernels.Follow(world);
            }), Is.Zero);
        });
    }

    // Large copies (borrowed-ideas.md item 5): StreamingStores.Copy writes what memmove writes at every length and target
    // offset (a head to the first line, four pages at a time, a tail), nothing past it.
    [TestCase(0, 0), TestCase(1, 16), TestCase(63, 16), TestCase(16_384, 0), TestCase(16_449, 16), TestCase(70_000, 48)]
    public unsafe void StreamedCopyWritesWhatMemmoveWrites(int bytes, int offset)
    {
        if (!System.Runtime.Intrinsics.X86.Avx.IsSupported) Assert.Ignore("no AVX");
        var from = new byte[bytes + 64];
        var to = new byte[bytes + 128];
        for (int i = 0; i < from.Length; i++) from[i] = (byte)(i * 31 + 7);
        Array.Fill(to, (byte)0xAB);
        StreamingStores.Copy(ref to[offset], ref from[0], (nuint)bytes);
        Assert.That(to.AsSpan(offset, bytes).SequenceEqual(from.AsSpan(0, bytes)), Is.True);
        Assert.That(to[offset + bytes], Is.EqualTo(0xAB));
        if (offset > 0) Assert.That(to[offset - 1], Is.EqualTo(0xAB));
    }

    // Capture and Restore with the copies streamed (the cache taken as 1 byte) hold what they hold with memmove.
    [Test]
    public void StreamedSnapshotsKeepEveryValue()
    {
        if (!System.Runtime.Intrinsics.X86.Avx.IsSupported) Assert.Ignore("no AVX");
        WithCache(1, () =>
        {
            Assert.That(StreamingStores.CopyPays(2), Is.True);
            using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
            var entities = new Entity[5000];
            world.Reserve<Fuel, Flare>(entities.Length);
            for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Fuel(i), new Flare(-i));
            var snapshot = world.Capture();
            for (int i = 0; i < entities.Length; i++) entities[i].Get<Fuel>() = new Fuel(0);
            world.Restore(snapshot);
            for (int i = 0; i < entities.Length; i++)
                Assert.That((entities[i].Get<Fuel>().Value, entities[i].Get<Flare>().Value), Is.EqualTo(((float)i, (float)-i)));
            entities[7].Get<Flare>() = new Flare(99);
            world.Capture(snapshot); // in sync: the untracked columns copied whole
            entities[7].Get<Flare>() = new Flare(1);
            world.Restore(snapshot);
            Assert.That(entities[7].Get<Flare>().Value, Is.EqualTo(99f));
            Assert.That(entities[4999].Get<Fuel>().Value, Is.EqualTo(4999f));
        });
    }

}

/// <summary>A system whose schedule lines name a streamed loop and a plain one.</summary>
[EcsSystem]
public ref struct StreamLoopsSystem
{
    [ForEach]
    static void Shine(in Fuel fuel, ref Flare flare) => flare.Value = fuel.Value + 1f;

    [ForEach]
    static void Brighten(in Fuel fuel, ref Flare flare) => flare.Value += fuel.Value;
}
