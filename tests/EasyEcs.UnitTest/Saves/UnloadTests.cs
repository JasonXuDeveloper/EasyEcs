using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using EasyEcs.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// Game logic as a plugin: an assembly of components, a singleton and a system, compiled here with the generators and
/// loaded into a collectible context, then reloaded as a new version that keeps the data.
/// </summary>
public class UnloadTests
{
    // Version 1 adds the bonus once per update; version 2 twice. The plugin's components ride on entities of the host.
    private const string Plugin = """
        using EasyEcs;
        using EasyEcs.UnitTest;
        namespace Plugin;

        public record struct Score(int Value) : IComponent, ITracked;
        public record struct Badge(int Level) : IComponent, IIndexed<int> { public int Key => Level; }
        public record struct Team(int Id) : IComponent, IGroup { public int Group => Id; }
        public record struct Settings(int Bonus) : ISingletonComponent;

        [EcsSystem]
        public ref struct Scoring
        {
            [ForEach, With<Position>]
            static void Add(ref Score score, in Settings settings) => score.Value += settings.Bonus * VERSION;
        }

        public static class Entry
        {
            public static void Setup(World world, Entity[] players)
            {
                world.SetSettings(1);
                world.AddSystem<Scoring>();
                foreach (var player in players) player.SetScore(0).SetBadge(1).SetTeam(7);
                world.BadgeIndex.Count(1);   // an index the world keeps
                world.GroupBy<Team>();       // tables split by a plugin's group
            }

            public static void AddScoring(World world) => world.AddSystem<Scoring>();

            public static int ScoreOf(Entity player) => player.Score.Value;
        }
        """;

    // Pair methods over a host's component (its order type is the plugin's) and over a plugin's, with a managed
    // component on the sorted tables (the sorts keep spare columns of its type).
    private const string Crowds = """
        using EasyEcs;
        using EasyEcs.UnitTest;
        namespace Plugin;

        public record struct Near(int Count) : IComponent;
        public record struct Spot(float X, float Y) : IComponent;
        public record struct Label(string Name) : IComponent;

        [EcsSystem]
        public ref struct Crowd
        {
            [ForEach]
            static void ByPosition(in Position p, ref Near near, [Neighbor] in Position q)
            {
                float dx = q.X - p.X, dy = q.Y - p.Y;
                if (dx * dx + dy * dy < 4f) near.Count += VERSION;
            }

            [ForEach]
            static void BySpot(in Spot p, ref Near near, [Neighbor] in Spot q)
            {
                float dx = q.X - p.X, dy = q.Y - p.Y;
                if (dx * dx + dy * dy < 4f) near.Count += VERSION;
            }
        }

        public static class Entry
        {
            public static Entity[] Setup(World world, Entity[] players)
            {
                world.AddSystem<Crowd>();
                foreach (var player in players) player.Set(new Near(0)).Set(new Label("player"));
                var spots = new Entity[20];
                for (int i = 0; i < spots.Length; i++) spots[i] = world.CreateEntity(new Spot(i, 0), new Near(0), new Label("spot"));
                return spots;
            }

            public static int NearOf(Entity entity) => entity.Get<Near>().Count;
        }
        """;

    private sealed class PluginContext() : AssemblyLoadContext(isCollectible: true); // the host's assemblies by fallback

    private static byte[] Compile(int version, string source = Plugin)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(World).Assembly.Location).Append(typeof(Position).Assembly.Location)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("Plugin",
            [CSharpSyntaxTree.ParseText(source.Replace("VERSION", version.ToString()), parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        CSharpGeneratorDriver.Create([new SystemGenerator().AsSourceGenerator(), new ComponentGenerator().AsSourceGenerator(), new PartGenerator().AsSourceGenerator()],
            parseOptions: parse).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    private static MethodInfo Entry(Assembly plugin, string name) => plugin.GetType("Plugin.Entry")!.GetMethod(name)!;

    [Test]
    public void AReloadKeepsTheDataAndTheOldVersionUnloads()
    {
        using var world = new World();
        var players = new Entity[3];
        for (int i = 0; i < players.Length; i++) players[i] = world.CreateEntity(new Position(i, 0));
        var bystander = world.CreateEntity(new Velocity(1, 1));

        var (unloaded, saved, bits) = RunAndUnload(world, players, Compile(1));
        for (int i = 0; i < 20 && unloaded.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(unloaded.IsAlive, Is.False, "nothing of EasyEcs roots the old version");
        Assert.That(world.Systems.Count, Is.Zero);
        Assert.That(players[0].Get<Position>(), Is.EqualTo(new Position(0, 0)), "the host's components stay");
        Assert.That(bystander.Has<Velocity>(), Is.True);
        Assert.That(world.DescribeTables().SelectMany(t => t.Columns).Any(c => c.Type.Assembly.GetName().Name == "Plugin"), Is.False);

        // Version 2 takes the bits version 1 gave back, and the saved values.
        var context = new PluginContext();
        var plugin = context.LoadFromStream(new MemoryStream(Compile(2)));
        RuntimeHelpers.RunModuleConstructor(plugin.ManifestModule.ModuleHandle); // registers its types for loading
        world.Load(saved);
        Assert.That(ComponentInfo.Count, Is.LessThanOrEqualTo(bits), "released bits are reused");
        Assert.That(Entry(plugin, "ScoreOf").Invoke(null, [players[0]]), Is.EqualTo(3), "three updates of version 1");

        Entry(plugin, "AddScoring").Invoke(null, [world]);
        world.Update();
        Assert.That(Entry(plugin, "ScoreOf").Invoke(null, [players[0]]), Is.EqualTo(5), "version 2 adds twice the bonus");
        Assert.That(bystander.Get<Velocity>(), Is.EqualTo(new Velocity(1, 1)));

        world.RemoveTypes(context);
        context.Unload();
    }

    // Not inlined: no reference to the plugin's types survives on the test's stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, byte[] Saved, int Bits) RunAndUnload(World world, Entity[] players, byte[] image)
    {
        var context = new PluginContext();
        var plugin = context.LoadFromStream(new MemoryStream(image));
        Entry(plugin, "Setup").Invoke(null, [world, players]);
        for (int i = 0; i < 3; i++) world.Update();
        Assert.That(Entry(plugin, "ScoreOf").Invoke(null, [players[0]]), Is.EqualTo(3));

        var output = new ArrayBufferWriter<byte>();
        world.Save(output);
        int bits = ComponentInfo.Count;

        world.RemoveTypes(plugin);
        context.Unload();
        return (new WeakReference(context), output.WrittenSpan.ToArray(), bits);
    }

    [Test]
    public void RemovingTypesKeepsTheRestWorking()
    {
        using var world = new World();
        var context = new PluginContext();
        var plugin = context.LoadFromStream(new MemoryStream(Compile(1)));
        var players = new[] { world.CreateEntity(new Position(0, 0), new Health(10)), world.CreateEntity(new Position(1, 0)) };
        Entry(plugin, "Setup").Invoke(null, [world, players]);
        world.Update();
        int position = world.Count<Position>();
        var snapshot = world.Capture();

        world.RemoveTypes(context);
        context.Unload();

        Assert.Throws<InvalidOperationException>(() => world.Restore(snapshot), "its tables were dropped");

        Assert.That(world.Count<Position>(), Is.EqualTo(position), "cached queries lost the dropped tables, not the rows");
        players[0].Set(new Velocity(1, 0));                     // transitions are rebuilt
        Assert.That(world.Count<Position, Velocity>(), Is.EqualTo(1));
        Assert.That(players[0].Get<Health>(), Is.EqualTo(new Health(10)));
        world.Update();                                          // no system left

        // The old snapshot captured again: its copies were of other tables (the dropped ones shifted the rest), so
        // none is reused for a table it does not belong to.
        world.Capture(snapshot);
        players[1].Set(new Health(3));
        world.Restore(snapshot);
        Assert.That((players[0].Get<Health>(), players[0].Has<Velocity>(), players[1].Has<Health>()),
            Is.EqualTo((new Health(10), true, false)));
    }

    [Test]
    public void OrdersAndPairStatesOfAPluginLetItUnload()
    {
        // The world kept a plugin's order requests (closures over its types), the order of host tables its pair method
        // over a host component asked for, its pair state's clusters of the dropped tables and the sorts' spare columns
        // of its managed component: the context never unloaded (fix-core-lane1). A type taking a reclaimed bit gets
        // its own order, not the request of the type that had the bit.
        using var world = new World();
        var players = new Entity[3];
        for (int i = 0; i < players.Length; i++) players[i] = world.CreateEntity(new Position(i, 0));
        var host = world.CreateEntity(new Position(5, 5)); // a host table the plugin's order of Position reached

        var unloaded = RunCrowdsAndUnload(world, players);
        for (int i = 0; i < 20 && unloaded.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(unloaded.IsAlive, Is.False, "nothing of EasyEcs roots the old version");
        Assert.That(World.Source(host).Table.Order, Is.Null, "the plugin's order of a host table went with it");

        var context = new PluginContext();
        var plugin = context.LoadFromStream(new MemoryStream(Compile(2, Crowds)));
        var spots = (Entity[])Entry(plugin, "Setup").Invoke(null, [world, players])!;
        for (int i = 0; i < 3; i++) world.Update();
        var order = World.Source(spots[0]).Table.Order;
        Assert.That(order?.GetType().GetGenericArguments()[0], Is.EqualTo(plugin.GetType("Plugin.Spot")), "its own order");
        Assert.That(Entry(plugin, "NearOf").Invoke(null, [spots[5]]), Is.EqualTo(3 * 2 * 3), "three neighbors within 2, twice a pass");
        world.RemoveTypes(context);
        context.Unload();
    }

    // Not inlined: no reference to the plugin's types survives on the test's stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunCrowdsAndUnload(World world, Entity[] players)
    {
        var context = new PluginContext();
        var plugin = context.LoadFromStream(new MemoryStream(Compile(1, Crowds)));
        var spots = (Entity[])Entry(plugin, "Setup").Invoke(null, [world, players])!;
        for (int i = 0; i < 12; i++) world.Update(); // the sorts, pair passes, a re-sort
        Assert.That(Entry(plugin, "NearOf").Invoke(null, [spots[5]]), Is.EqualTo(12 * 3), "three neighbors within 2 per pass");
        Assert.That(World.Source(spots[0]).Table.Order, Is.Not.Null);

        world.RemoveTypes(context);
        context.Unload();
        return new WeakReference(context);
    }
}
