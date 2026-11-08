using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="World.ParallelFor{TJob}"/>: a range or a list's elements in chunks, on the world's runner; and the lambda
/// form over a list's elements (<c>world.ParallelFor(ref items, keep, static (ref Heat h, float keep) =&gt; …)</c>), the
/// generator's IForSpan job with the element as its vector loop's one column.
/// </summary>
public class ParallelForTests
{
    /// <summary>An element of two float lanes.</summary>
    public record struct Heat(float Value, float Rate);

    /// <summary>An element of one int lane.</summary>
    public record struct Count(int N);

    /// <summary>Each index written by exactly one chunk.</summary>
    private struct Fill : IForRange
    {
        public int[] Cells;

        public void Execute(int start, int end)
        {
            for (int i = start; i < end; i++) Cells[i] += i + 1;
        }
    }

    /// <summary>Counts the chunks and the threads that ran them (Interlocked: shared on purpose).</summary>
    private struct Visits : IForRange
    {
        public int[] Chunks;
        public int[] Smallest;

        public void Execute(int start, int end)
        {
            Interlocked.Increment(ref Chunks[0]);
            int length = end - start;
            int seen;
            while (length < (seen = Volatile.Read(ref Smallest[0])) && Interlocked.CompareExchange(ref Smallest[0], length, seen) != seen) { }
        }
    }

    /// <summary>The scenario of the records a parallel pass emits: each hit's amount scaled in place, chunk by chunk.</summary>
    private struct Scale : IForSpan<EmitTests.Hit>
    {
        public int Factor;

        public void Execute(int start, Span<EmitTests.Hit> items)
        {
            for (int i = 0; i < items.Length; i++) items[i] = items[i] with { Amount = items[i].Amount * Factor + start + i };
        }
    }

    private struct Structural : IForRange
    {
        public World World;

        public void Execute(int start, int end) => World.CreateEntity(new Health(start));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(1025)]
    [TestCase(100_003)]
    public void EveryIndexRunsOnce(int count)
    {
        foreach (int threads in new[] { 1, 4 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            var job = new Fill { Cells = new int[count] };
            for (int run = 1; run <= 20; run++)
            {
                world.ParallelFor(count, ref job);
                for (int i = 0; i < count; i++)
                    if (job.Cells[i] != run * (i + 1)) Assert.Fail($"{threads} threads, run {run}: index {i} holds {job.Cells[i]}");
            }
        }
    }

    [Test]
    public void ChunksFollowChunkRows()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var census = new Visits { Chunks = new int[1], Smallest = [int.MaxValue] };
        world.ParallelFor(10_000, ref census, chunkSize: 100); // rounded up to 128
        Assert.That(census.Chunks[0], Is.EqualTo((10_000 + 127) / 128));
        Assert.That(census.Smallest[0], Is.EqualTo(10_000 % 128));
        census = new Visits { Chunks = new int[1], Smallest = [int.MaxValue] };
        world.ParallelFor(500, ref census); // below the smallest default chunk: one chunk, on the caller
        Assert.That(census.Chunks[0], Is.EqualTo(1));
    }

    [Test]
    public void AListsElementsAreWrittenInPlace()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        ref var hits = ref world.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items;
        for (int i = 0; i < 50_000; i++) hits.Add(new EmitTests.Hit(default, i));
        var job = new Scale { Factor = 3 };
        world.ParallelFor(ref hits, ref job);
        for (int i = 0; i < hits.Count; i++) Assert.That(hits[i].Amount, Is.EqualTo(i * 3 + i));
        hits.Clear();
        world.ParallelFor(ref hits, ref job); // no elements: nothing to run
        using var fresh = new World();
        ref var none = ref fresh.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items;
        Assert.That(none.Capacity, Is.Zero);
        fresh.ParallelFor(ref none, ref job); // no list at all
    }

    [Test]
    public void AStructuralChangeInsideThrows()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var job = new Structural { World = world };
        Assert.Throws<InvalidOperationException>(() => world.ParallelFor(10_000, ref job));
        Assert.That(world.EntityCount, Is.Zero);
        var fill = new Fill { Cells = new int[10] };
        world.ParallelFor(10, ref fill); // the world works after the throw
        Assert.That(fill.Cells[9], Is.EqualTo(10));
    }

    [Test]
    public void ANegativeCountThrows()
    {
        using var world = new World();
        var job = new Fill { Cells = [] };
        Assert.Throws<ArgumentOutOfRangeException>(() => world.ParallelFor(-1, ref job));
    }

    [Test]
    public void ParallelForDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var fill = new Fill { Cells = new int[100_000] };
        world.GetOrAddSingleton<Emitted<EmitTests.Hit>>().Items.SetCount(100_000);
        var scale = new Scale { Factor = 1 };
        void Calls(int count)
        {
            ref var hits = ref world.GetSingleton<Emitted<EmitTests.Hit>>().Items;
            for (int i = 0; i < count; i++)
            {
                world.ParallelFor(fill.Cells.Length, ref fill);
                world.ParallelFor(ref hits, ref scale);
            }
        }

        Calls(20);
        Assert.That(Allocations.Of(() => Calls(200)), Is.Zero);
    }

    // ---------------- the lambda form ----------------

    /// <summary>Elements 0..count-1 of an Emitted list of the world, filled with values that depend on the index.</summary>
    private static ref DynamicBuffer<Heat> Heats(World world, int count)
    {
        ref var items = ref world.GetOrAddSingleton<Emitted<Heat>>().Items;
        items.Clear();
        for (int i = 0; i < count; i++) items.Add(new Heat(i * 0.37f - 5f, (i % 7) * 0.125f));
        return ref items;
    }

    // Chunk edges (a multiple of 64, 1,024 and past), the elements after the last whole vector, none at all.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(63)]
    [TestCase(1023)]
    [TestCase(1025)]
    [TestCase(100_003)]
    public void ALambdaRunsEveryElementAsItsBodyDoes(int count)
    {
        foreach (int threads in new[] { 1, 4 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            ref var items = ref Heats(world, count);
            for (int run = 0; run < 3; run++)
                world.ParallelFor(ref items, 0.5f, static (ref Heat h, float keep) => h.Value = h.Value * keep + h.Rate);
            for (int i = 0; i < count; i++)
            {
                var expected = new Heat(i * 0.37f - 5f, (i % 7) * 0.125f);
                for (int run = 0; run < 3; run++) expected.Value = expected.Value * 0.5f + expected.Rate;
                if (items[i] != expected) Assert.Fail($"{threads} threads: element {i} holds {items[i]}, the body gives {expected}");
            }
        }
    }

    [Test]
    public void ALambdaUnderAConditionWritesOnlyTheElementsThatPass()
    {
        foreach (int threads in new[] { 1, 4 })
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            ref var items = ref world.GetOrAddSingleton<Emitted<Count>>().Items;
            for (int i = 0; i < 10_007; i++) items.Add(new Count(i % 13));
            world.ParallelFor(ref items, 6, static (ref Count c, int cap) =>
            {
                if (c.N < cap) c.N += 100;
            });
            for (int i = 0; i < items.Count; i++)
                Assert.That(items[i].N, Is.EqualTo(i % 13 < 6 ? i % 13 + 100 : i % 13), $"{threads} threads, element {i}");
        }
    }

    [Test]
    public void ALambdaWithoutValuesAndOneReadingSeveralRun()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        ref var items = ref Heats(world, 5_000);
        world.ParallelFor(ref items, static (ref Heat h) => h.Rate = -h.Rate);
        world.ParallelFor(ref items, 2f, 1f, static (ref Heat h, float scale, float bias) => h.Value = h.Value * scale + bias);
        for (int i = 0; i < items.Count; i++)
            Assert.That(items[i], Is.EqualTo(new Heat((i * 0.37f - 5f) * 2f + 1f, -((i % 7) * 0.125f))), $"element {i}");
    }

    [Test]
    public void ALambdaDoesNotAllocateAfterItsFirstCall()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        Heats(world, 100_000);
        void Calls(int count)
        {
            ref var items = ref world.GetSingleton<Emitted<Heat>>().Items;
            for (int i = 0; i < count; i++)
                world.ParallelFor(ref items, 0.999f, static (ref Heat h, float keep) => h.Value = h.Value * keep + 1f);
        }

        Calls(20);
        Assert.That(Allocations.Of(() => Calls(200)), Is.Zero);
    }

    private const string Elements = """
        public struct Cell { public float V, W; }
        public struct Tagged { public bool On; public float V; }
        """;

    [Test]
    public void ALambdaGetsAnIForSpanJobWithAVectorLoop()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, Elements,
            "public static class Run { public static void F(World world, ref DynamicBuffer<Cell> items, float k) => world.ParallelFor(ref items, k, static (ref Cell c, float k) => c.V = c.V * k + c.W); }"), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Where(t => t.FilePath.EndsWith(".Lambdas.g.cs")).Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("internal struct LambdaJob0 : global::EasyEcs.IForSpan<global::Cell>")
            .And.Contain("__world.ParallelFor(ref __items, ref job);").And.Contain("Interleaved.Load2(ref e1, (nuint)i").And.Contain("((__c1_0 * __u0) + __c1_1)"), generated);
    }

    private static async Task<Diagnostic[]> RulesOf(string call) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, Elements,
            $"public static class Run {{ public static void F(World world, ref DynamicBuffer<Cell> items, ref DynamicBuffer<Tagged> tags, float k, int n) => {call}; }}"]))
        .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097" || d.Severity == DiagnosticSeverity.Error).ToArray();

    // What the generator compiles (no diagnostic) and what it refuses (EECS095) or leaves scalar (EECS097), with the reason.
    [TestCase("world.ParallelFor(ref items, k, static (ref Cell c, float k) => c.V *= k)", null, null)]
    [TestCase("world.ParallelFor(ref items, static (in Cell c) => System.Console.WriteLine(c.V))", "EECS097", "Console.WriteLine")]
    [TestCase("world.ParallelFor(ref items, k, static (float k, ref Cell c) => c.V *= k)", "EECS095", "takes no element first")]
    [TestCase("world.ParallelFor(ref items, n, static (ref Cell c, float k) => c.V *= k)", "EECS095", "value 1 before the lambda is a int")]
    [TestCase("world.ParallelFor(ref items, static (ref Cell c, float k) => c.V *= k)", "EECS095", "a value the call does not pass")]
    [TestCase("world.ParallelFor(ref items, k, n, static (ref Cell c, float k) => c.V *= k)", "EECS095", "passes 2 values")]
    [TestCase("world.ParallelFor(ref items, k, static (ref Cell c, ref float k) => c.V *= k)", "EECS095", "by ref")]
    [TestCase("world.ParallelFor(ref items, k, (ref Cell c, float x) => c.V *= k)", "EECS095", "captures 'k'")]
    [TestCase("world.ParallelFor(ref items, world, static (ref Cell c, World w) => w.CreateEntity())", "EECS095", "can change structure")]
    public async Task ALambdasRules(string call, string id, string reason)
    {
        var diagnostics = await RulesOf(call);
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(id == null ? Array.Empty<string>() : new[] { id }),
            string.Join("\n", diagnostics.Select(d => d.ToString())));
        if (reason != null) Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
    }
}
