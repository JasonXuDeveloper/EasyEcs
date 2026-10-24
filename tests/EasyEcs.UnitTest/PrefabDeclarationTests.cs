using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [Prefab] structs: a prefab declared as the components its entities start with and their defaults. The generator
/// writes the static Prefab built once from them and the constructor field initializers need; creation is Instantiate.
/// </summary>
public static partial class Bestiary
{
    public record struct Health(int Value) : IComponent;
    public struct Position : IComponent { public float X, Y; }
    public record struct Speed(float Value) : IComponent;
    public record struct Treasure(int Gold) : IComponent;
    public struct IsGoblin : IComponent;
    public record struct Drops(int Item);

    /// <summary>A goblin starts with 30 health and speed 2, at the origin; its tag lets systems find goblins.</summary>
    [Prefab]
    public partial struct Goblin
    {
        public Health Health = new(30);
        public Position Position;
        public Speed Speed = new(2f);
        public IsGoblin Tag;
    }

    /// <summary>A chest declares its own constructor: the generator writes none.</summary>
    [Prefab]
    public partial struct Chest
    {
        public Position Position;
        public Treasure Treasure = new(10);

        public Chest() => Position = new Position { X = 1, Y = 1 };
    }
}

public class PrefabDeclarationTests
{
    [Test]
    public void InstantiateWritesEveryComponentAtItsDefault()
    {
        using var world = new World();
        var goblin = world.Instantiate(Bestiary.Goblin.Prefab);
        Assert.That(goblin.Read<Bestiary.Health>().Value, Is.EqualTo(30));
        Assert.That(goblin.Read<Bestiary.Speed>().Value, Is.EqualTo(2f));
        Assert.That(goblin.Read<Bestiary.Position>().X, Is.Zero);
        Assert.That(goblin.Has<Bestiary.IsGoblin>());
        Assert.That(world.DescribeTables().Count(t => t.Entities > 0), Is.EqualTo(1)); // straight into its table
    }

    [Test]
    public void ValuesGivenToInstantiateReplaceTheDefaults()
    {
        using var world = new World();
        var goblin = world.Instantiate(Bestiary.Goblin.Prefab, new Bestiary.Position { X = 5, Y = 6 });
        Assert.That((goblin.Read<Bestiary.Health>().Value, goblin.Read<Bestiary.Position>().Y), Is.EqualTo((30, 6f)));
    }

    [Test]
    public void ADeclarationsOwnConstructorGivesItsDefaults()
    {
        using var world = new World();
        var chest = world.Instantiate(Bestiary.Chest.Prefab);
        Assert.That((chest.Read<Bestiary.Treasure>().Gold, chest.Read<Bestiary.Position>().X), Is.EqualTo((10, 1f)));
    }

    [Test]
    public void ASpanOfEntitiesIsInstantiatedAtOnce()
    {
        using var world = new World();
        var goblins = new Entity[50];
        world.Instantiate(Bestiary.Goblin.Prefab, goblins);
        world.Instantiate(Bestiary.Goblin.Prefab, goblins.AsSpan(0, 10), new Bestiary.Speed(9f));
        Assert.That(world.EntityCount, Is.EqualTo(60));
        Assert.That(goblins[0].Read<Bestiary.Speed>().Value, Is.EqualTo(9f));
        Assert.That(goblins[49].Read<Bestiary.Speed>().Value, Is.EqualTo(2f));
        Assert.That(world.Count<Bestiary.IsGoblin>(), Is.EqualTo(60)); // the tag names the kind
    }

    [Test]
    public void AVariantGetsABufferTheDeclarationCannotHold()
    {
        using var world = new World();
        var boss = new Prefab(Bestiary.Goblin.Prefab).SetBuffer(new Bestiary.Drops(7)).Set(new Bestiary.Health(300));
        var made = world.Instantiate(boss);
        Assert.That((made.Read<Bestiary.Health>().Value, made.Read<DynamicBuffer<Bestiary.Drops>>()[0].Item), Is.EqualTo((300, 7)));
        Assert.That(world.Instantiate(Bestiary.Goblin.Prefab).Read<Bestiary.Health>().Value, Is.EqualTo(30)); // the declaration's prefab is untouched
    }

    [Test]
    public void InstantiatingAllocatesNothingOnceItsTableHasRoom()
    {
        using var world = new World();
        world.Reserve<Bestiary.Health, Bestiary.Position, Bestiary.Speed, Bestiary.IsGoblin>(200);
        world.Instantiate(Bestiary.Goblin.Prefab, new Bestiary.Speed(0)); // the overload measured: its target table found once
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) world.Instantiate(Bestiary.Goblin.Prefab, new Bestiary.Speed(i));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void OnePrefabServesWorldsOnSeveralThreads()
    {
        var counts = new int[4];
        Parallel.For(0, 4, t =>
        {
            using var world = new World();
            var made = new Entity[1000];
            world.Instantiate(Bestiary.Goblin.Prefab, made);
            foreach (var e in made) counts[t] += e.Read<Bestiary.Health>().Value;
        });
        Assert.That(counts, Is.All.EqualTo(30_000));
    }
}

/// <summary>The rule of prefab declarations (EECS102) and what the generator writes for one.</summary>
public class PrefabDeclarationRuleTests
{
    private const string Kinds = """
        [Prefab] public partial struct Pair { public P P = new() { V = 1 }; public Q Q; }

        """;

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, Kinds, source]))
            .Where(d => d.Id is "EECS102" || d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    [TestCase("[Prefab] public struct K { public P P; }")]                                  // not partial
    [TestCase("[Prefab] public partial struct K<TC> where TC : struct { public P P; }")]    // generic
    [TestCase("[Prefab] public partial struct K { public P P; public int N; }")]            // no component
    [TestCase("[Prefab] public partial struct K { public P P { get; set; } }")]             // a property
    [TestCase("[Prefab] public partial struct K { public P A; public P B; }")]              // twice
    [TestCase("[Prefab] public partial struct K { public const int N = 1; }")]              // nothing
    [TestCase("[Prefab] public ref partial struct K { public ref P P; }")]                  // a ref struct
    [TestCase("[Prefab] public partial struct K { public P P; public static int Prefab; }")] // the name is taken
    [TestCase("public static class Outer { [Prefab] public partial struct K { public P P; } }")] // the type around it is not partial
    public async Task ADeclarationTheGeneratorCannotWriteIsAnError(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS102" }));

    [Test]
    public async Task ABufferFieldIsAnError() => // a buffer held by value is EECS012 as well
        Assert.That(await Ids("public struct L; [Prefab] public partial struct K { public DynamicBuffer<L> B; }"), Is.EqualTo(new[] { "EECS012", "EECS102" }));

    [TestCase("public static class U { public static Entity F(World w) => w.CreateEntity(new P(), new Q { V = 2 }); }")] // a creation of its components
    [TestCase("public static class U { public static Entity F(World w) => w.Instantiate(Pair.Prefab, new Q { V = 2 }); }")]
    public async Task CreationsAreNotReported(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    [Test]
    public void TheGeneratorWritesThePrefabAndTheConstructor()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, Kinds), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("public Pair()"));
        Assert.That(generated, Does.Contain("Q = default;").And.Not.Contain("P = default;"));
        Assert.That(generated, Does.Contain("public static readonly global::EasyEcs.Prefab Prefab"));
        Assert.That(generated, Does.Contain("new global::EasyEcs.Prefab().Set(in d.P, in d.Q)"));
        Assert.That(generated, Does.Not.Contain("Filter").And.Not.Contain("CreatePair"));
    }

    [Test]
    public void MoreThanNineComponentsTakeOneSetPerNine()
    {
        string many = string.Concat(Enumerable.Range(0, 11).Select(i => $"public struct C{i} : IComponent {{ public int V; }} "))
                      + "[Prefab] public partial struct Big { " + string.Concat(Enumerable.Range(0, 11).Select(i => $"public C{i} F{i}; ")) + "}";
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, many), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain(".Set(in d.F9, in d.F10)"));
    }
}
