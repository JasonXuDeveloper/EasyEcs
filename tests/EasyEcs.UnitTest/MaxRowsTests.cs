using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [ForEach(MaxRows = N)]: at most N rows per run, collected first and run one by one outside the walk
/// (World.ForEachCollected), so the method may call what a pass refuses (LoadEntities, SaveEntities); against the
/// OnUpdate written by hand (CollectIds, then a loop over the ids), the rows an earlier row's method destroyed or
/// changed, ref components written back, [Consume], a runner's entry point, exceptions and 0 B.
/// </summary>
public class MaxRowsTests
{
    public record struct Wanted(int Key) : IComponent;                    // a request: load the stored entity of Key
    public record struct Stored(int Key, int Value) : IComponent;         // what a load brings back
    public struct Parked : IComponent;                                    // a request that waits
    public record struct Level(int Value) : IComponent, ITracked;
    public struct Leaving : IComponent;                                   // an entity marked to go to the store
    public record struct Served(int Count, long Order) : ISingletonComponent;
    public record struct HandServed(int Count, long Order) : ISingletonComponent;
    public record struct Sum(int Value) : ISingletonComponent, IReduction<Sum>
    {
        public static Sum Identity => default;
        public static Sum Combine(Sum a, Sum b) => new(a.Value + b.Value);
    }
    public record struct Loaded(int Key);

    /// <summary>The entities saved by key, the store the methods read and write (owner thread only).</summary>
    public sealed class Store
    {
        public readonly System.Collections.Generic.Dictionary<int, byte[]> Saved = new();
        public readonly ArrayBufferWriter<byte> Buffer = new();
    }

    public record struct Shelf(Store Store) : ISingletonComponent;

    // The plain form: four requests a run, each loaded from the store (a pass refuses LoadEntities), then consumed.
    [EcsSystem]
    public ref struct LoadSome
    {
        [ForEach(MaxRows = 4), Consume, Without<Parked>]
        static void Load(in Wanted wanted, World world, ref Served served, in Shelf shelf)
        {
            world.LoadEntities(shelf.Store.Saved[wanted.Key]);
            served.Count++;
            served.Order = served.Order * 31 + wanted.Key;
        }
    }

    // The same by hand: what the plain form replaces (EECS119).
    [EcsSystem]
    public ref struct HandLoadSome
    {
        static void OnUpdate(World world)
        {
            Span<EntityId> ids = stackalloc EntityId[4];
            int count = world.CollectIds<Wanted>(ids, NotParked);
            ref var shelf = ref world.GetSingleton<Shelf>();
            foreach (var id in ids[..count])
            {
                var request = world.GetEntity(id);
                int key = request.Get<Wanted>().Key;
                world.LoadEntities(shelf.Store.Saved[key]);
                ref var served = ref world.GetOrAddSingleton<HandServed>();
                served.Count++;
                served.Order = served.Order * 31 + key;
                request.Destroy();
            }
        }

        static readonly QueryFilter NotParked = QueryFilter.Create().Without<Parked>();
    }

    // Saves the marked entities, two a run, and destroys them: a save is refused in a pass too.
    [EcsSystem]
    public ref struct SaveSome
    {
        [ForEach(MaxRows = 2), With<Leaving>, Consume]
        static void Save(Entity entity, in Stored stored, World world, in Shelf shelf)
        {
            var store = shelf.Store;
            store.Buffer.ResetWrittenCount();
            entity.RemoveLeaving();                                       // not in the save
            world.SaveEntities(store.Buffer, entity.Id);
            store.Saved[stored.Key] = store.Buffer.WrittenSpan.ToArray();
        }
    }

    // Writes its row, and moves the entity to another table while it runs: the write lands on the new row.
    [EcsSystem]
    public ref struct RaiseSome
    {
        [ForEach(MaxRows = 3), Without<Parked>]
        static void Raise(Entity entity, ref Level level)
        {
            level.Value += 10;
            entity.SetParked();                                           // moves the row: the write follows it
        }
    }

    // Destroys the next requests and parks others while it runs: those collected rows are skipped.
    [EcsSystem]
    public ref struct Greedy
    {
        [ForEach(MaxRows = 8), Consume, Without<Parked>]
        static void Take(in Wanted wanted, World world, ref Served served)
        {
            served.Count++;
            served.Order = served.Order * 31 + wanted.Key;
            foreach (var other in world.Query<Wanted>())
                if (other.Component1.Key == wanted.Key + 1) other.Entity.Destroy();
                else if (other.Component1.Key == wanted.Key + 2) other.Entity.SetParked();
        }
    }

    // Reduces and emits like any [ForEach] method.
    [EcsSystem]
    public ref struct CountSome
    {
        [ForEach(MaxRows = 5)]
        static void Count(in Wanted wanted, ref Reduce<Sum> sum, Emit<Loaded> loaded)
        {
            sum.Value = new Sum(sum.Value.Value + wanted.Key);
            loaded.Add(new Loaded(wanted.Key));
        }
    }

    // Throws on key 3: the rows before it are consumed, the rest wait.
    [EcsSystem]
    public ref struct Fragile
    {
        [ForEach(MaxRows = 10), Consume]
        static void Take(in Wanted wanted, ref Served served)
        {
            if (wanted.Key == 3) throw new InvalidOperationException("three");
            served.Count++;
        }
    }

    // A runner: an entry point that takes the world.
    public static class Loader
    {
        [ForEach(MaxRows = 70), Consume]
        internal static void Load(in Wanted wanted, World world, ref Served served)
        {
            world.CreateEntity(new Stored(wanted.Key, wanted.Key * 2));
            served.Count++;
            served.Order = served.Order * 31 + wanted.Key;
        }
    }

    private static Stored[] Stores(World world)
    {
        var all = new System.Collections.Generic.List<Stored>();
        foreach (var item in world.Read<Stored>()) all.Add(item.Component1);
        return all.ToArray();
    }

    private static int[] Wants(World world)
    {
        var all = new System.Collections.Generic.List<int>();
        foreach (var item in world.Read<Wanted>()) all.Add(item.Component1.Key);
        return all.ToArray();
    }

    private static World StoreWorld(int stored, out Store store)
    {
        var world = new World();
        store = new Store();
        world.AddSingleton<Shelf>() = new Shelf(store);
        world.AddSingleton<Served>();
        world.AddSingleton<HandServed>();
        world.Reserve<Stored>(stored);
        for (int key = 0; key < stored; key++)
        {
            var entity = world.CreateEntity(new Stored(key, key * 7));
            store.Buffer.ResetWrittenCount();
            world.SaveEntities(store.Buffer, entity.Id);
            store.Saved[key] = store.Buffer.WrittenSpan.ToArray();
            entity.Destroy();
        }

        return world;
    }

    [Test]
    public void EachRunServesTheBudgetInQueryOrderAndTheRestWait()
    {
        using var world = StoreWorld(10, out _);
        world.AddSystem<LoadSome>();
        for (int key = 0; key < 10; key++) world.CreateEntity(new Wanted(key));
        var parked = world.CreateEntity(new Wanted(5), new Parked());
        world.Update();
        Assert.That(world.Served.Count, Is.EqualTo(4));
        Assert.That(world.Count<Wanted>(), Is.EqualTo(7));
        Assert.That(Stores(world).Select(s => s.Key).OrderBy(k => k), Is.EqualTo(new[] { 0, 1, 2, 3 }));
        world.Update();
        world.Update();
        Assert.That(world.Served.Count, Is.EqualTo(10));
        Assert.That(world.Count<Wanted>(), Is.EqualTo(1));                   // the parked one
        Assert.That(parked.IsAlive, Is.True);
        Assert.That(Stores(world).Select(s => s.Value).OrderBy(v => v), Is.EqualTo(Enumerable.Range(0, 10).Select(k => k * 7)));
        world.Update();                                                      // nothing left
        Assert.That(world.Served.Count, Is.EqualTo(10));
    }

    [Test]
    public void ThePlainFormServesWhatTheHandFormServes()
    {
        using var plain = StoreWorld(20, out _);
        using var hand = StoreWorld(20, out _);
        plain.AddSystem<LoadSome>();
        hand.AddSystem<HandLoadSome>();
        foreach (var world in new[] { plain, hand })
            for (int key = 19; key >= 0; key--) world.CreateEntity(new Wanted(key));
        for (int tick = 0; tick < 6; tick++)
        {
            plain.Update();
            hand.Update();
            Assert.That((plain.Served.Count, plain.Served.Order), Is.EqualTo((hand.HandServed.Count, hand.HandServed.Order)));
            Assert.That(plain.Count<Wanted>(), Is.EqualTo(hand.Count<Wanted>()));
            Assert.That(Stores(plain), Is.EqualTo(Stores(hand)));
        }
    }

    [Test]
    public void AMethodMaySaveAndDestroyWhatItVisits()
    {
        using var world = StoreWorld(0, out var store);
        world.AddSystem<SaveSome>();
        for (int key = 0; key < 5; key++) world.CreateEntity(new Stored(key, key + 100), new Leaving());
        var staying = world.CreateEntity(new Stored(9, 9));
        world.Update();
        Assert.That(store.Saved.Keys.OrderBy(k => k), Is.EqualTo(new[] { 0, 1 }));
        world.Update();
        world.Update();
        Assert.That(store.Saved.Count, Is.EqualTo(5));
        Assert.That(world.Count<Stored>(), Is.EqualTo(1));
        Assert.That(staying.IsAlive, Is.True);

        // What was saved loads back without its Leaving tag.
        world.LoadEntities(store.Saved[3]);
        Assert.That(Stores(world).Count(s => s == new Stored(3, 103)), Is.EqualTo(1));
        Assert.That(world.Count<Leaving>(), Is.Zero);
    }

    [Test]
    public void RefComponentsAreWrittenBackToTheRowTheEntityMovedTo()
    {
        using var world = new World();
        world.AddSystem<RaiseSome>();
        var entities = Enumerable.Range(0, 5).Select(i => world.CreateEntity(new Level(i))).ToArray();
        ulong before = world.ChangeTick;
        world.Update();
        Assert.That(entities.Select(e => e.Read<Level>().Value), Is.EqualTo(new[] { 10, 11, 12, 3, 4 }));
        Assert.That(entities.Select(e => e.Has<Parked>()), Is.EqualTo(new[] { true, true, true, false, false }));
        // The write-back stamps the rows: a Changed<Level> query since the update sees the three.
        Assert.That(world.Count<Level>(QueryFilter.Create().Changed<Level>(before + 1)), Is.EqualTo(3));
        world.Update();
        Assert.That(entities.Select(e => e.Read<Level>().Value), Is.EqualTo(new[] { 10, 11, 12, 13, 14 }));
    }

    [Test]
    public void RowsAnEarlierRowDestroyedOrChangedAreSkipped()
    {
        using var world = new World();
        world.AddSingleton<Served>();
        world.AddSystem<Greedy>();
        for (int key = 0; key < 8; key++) world.CreateEntity(new Wanted(key * 3 % 8));   // 0 3 6 1 4 7 2 5
        world.Update();
        // 0 takes out 1 and parks 2; 3 takes out 4 and parks 5; 6 takes out 7; 1, 4, 7 are gone and 2, 5 parked: skipped.
        Assert.That(world.Served.Count, Is.EqualTo(3));
        Assert.That(Wants(world).OrderBy(k => k), Is.EqualTo(new[] { 2, 5 }));
        Assert.That(world.Count<Wanted>(QueryFilter.Create().With<Parked>()), Is.EqualTo(2));
    }

    [Test]
    public void ReductionsAndRecordsComeFromTheRowsRun()
    {
        using var world = new World();
        world.AddSystem<CountSome>();
        for (int key = 1; key <= 8; key++) world.CreateEntity(new Wanted(key));
        world.Update();
        Assert.That(world.GetSingleton<Sum>().Value, Is.EqualTo(1 + 2 + 3 + 4 + 5));
        ref var loaded = ref world.GetSingleton<Emitted<Loaded>>().Items;
        Assert.That(loaded.AsReadOnlySpan().ToArray().Select(l => l.Key), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        loaded.Clear();
    }

    [Test]
    public void AThrowingRowKeepsTheRowsAfterIt()
    {
        using var world = new World();
        world.AddSingleton<Served>();
        world.AddSystem<Fragile>();
        var all = Enumerable.Range(0, 6).Select(k => world.CreateEntity(new Wanted(k))).ToArray();
        Assert.Throws<InvalidOperationException>(world.Update);
        Assert.That(world.Served.Count, Is.EqualTo(3));
        Assert.That(all.Select(e => e.IsAlive), Is.EqualTo(new[] { false, false, false, true, true, true }));
    }

    [Test]
    public void ARunnerTakesItsBudgetFromTheArrayPoolPastTheStack()
    {
        using var world = new World();
        world.AddSingleton<Served>();
        for (int key = 0; key < 100; key++) world.CreateEntity(new Wanted(key));
        Loader.Load(world);
        Assert.That(world.Served.Count, Is.EqualTo(70));
        Assert.That(world.Count<Wanted>(), Is.EqualTo(30));
        Assert.That(Stores(world).Select(s => s.Key), Is.EqualTo(Enumerable.Range(0, 70)));
        Loader.Load(world);
        Assert.That(world.Served.Count, Is.EqualTo(100));
        Assert.That(world.Count<Wanted>(), Is.Zero);
        Loader.Load(world);                                                  // nothing left: nothing rented
        Assert.That(world.Served.Count, Is.EqualTo(100));
    }

    [Test]
    public void TheScheduleNamesTheBudget()
    {
        using var world = new World();
        world.AddSystem<LoadSome>();
        var loops = world.DescribeSchedule().ToString();
        Assert.That(loops, Does.Contain("Load: at most 4 rows per run, collected first, then run one by one outside the walk"));
    }

    [Test]
    public void SteadyStateAllocatesNothing()
    {
        using var world = StoreWorld(1, out var store);
        world.AddSystem<RaiseSome>();
        world.AddSystem<SaveSome>();
        for (int i = 0; i < 200; i++) world.CreateEntity(new Level(i));
        ForEachCollectedJob job = default;
        void Step()
        {
            world.Update();
            world.RemoveAll<Parked>();                                       // the next run raises the first three again
            world.ForEachCollected<Level, ForEachCollectedJob>(ref job, 100);  // past the stack: an array of the pool
        }

        Allocations.WarmUp(Step, 50);
        Assert.That(Allocations.Of(Step), Is.Zero);
    }

    /// <summary>A hand-written job run on collected rows: counts them.</summary>
    private struct ForEachCollectedJob : IForEachEntity<Level>
    {
        public int Rows;

        public static int WriteMask => 0;

        public void Execute(Entity entity, ref Level level) => Rows++;
    }

    // ---------------- generated code and rules ----------------

    private const string Data = """
        using EasyEcs;
        public struct P : IComponent { public int V; }
        public struct Q : IComponent { public int V; }
        public struct T : IComponent, ITracked { public int V; }
        public struct Mark : IComponent;
        public struct Tally : ISingletonComponent { public long N; }

        """;

    [Test]
    public void TheGeneratedPassCollectsTheRowsAndRunsTheJobOnEach()
    {
        string source = Data + "[EcsSystem] public ref struct S { [ForEach(MaxRows = 6), Consume, Without<Mark>] static void A(in P p, ref Q q, World w, ref Tally t) { t.N += p.V; q.V++; w.CreateEntity(new Mark()); } }\n"
                        + "public static class R { [ForEach(MaxRows = 3), With<Mark>] public static void B(Entity e, ref int n) => n++; }";
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string text = string.Join("\n", output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(text, Does.Contain("internal ref struct AJob0 : global::EasyEcs.IForEachEntity<global::P, global::Q>"));
        Assert.That(text, Does.Contain("__ForEach.A_0(default, in c1, ref c2, World, ref s3);\n            entity.Destroy();"));  // [Consume]: after its row
        Assert.That(text, Does.Contain("static int global::EasyEcs.IForEachEntity<global::P, global::Q>.WriteMask => 2;"));      // Q written back
        Assert.That(text, Does.Contain("world.ForEachCollected<global::P, global::Q, __ForEach.AJob0>(ref job0, 6, in __ForEach.AFilter0);"));
        Assert.That(text, Does.Contain("global::System.Math.Min(world.Count<global::P, global::Q>(in __ForEach.AFilter0), 6)"));
        Assert.That(text, Does.Not.Contain("DestroyAll"));                                                                  // a budget leaves rows behind
        Assert.That(text, Does.Contain("\"A: at most 6 rows per run, collected first, then run one by one outside the walk\""));
        Assert.That(text, Does.Contain("world.ForEachCollected<global::Mark, __ForEach.BJob0>(ref job, 3, in __ForEach.BFilter0);"));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = -1)] static void A(ref P p) { } }", "MaxRows = -1 must be 0 (every row) or more")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4, Parallel = true)] static void A(ref P p) { } }", "it runs in parallel")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4, Slices = 2)] static void A(ref P p) { } }", "it visits a slice of its rows")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4), Changed<T>] static void A(ref P p) { } }", "selects rows by change tick")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4, SimdMode = SimdMode.Require)] static void A(ref P p) { } }", "SimdMode.Require")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4)] static void A(System.Span<P> p) { } }", "a run of rows")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4)] static void A(ref P p, Maybe<Q> q) { } }", "a Maybe")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4)] static void A(Mut<T> t) { } }", "a Mut")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4)] static void A(ref P p, Scratch<Tally> s) { } }", "a Scratch")]
    public async Task AnInvalidBudgetIsReported(string source, string reason)
    {
        var found = (await GeneratorTests.Analyze([Data + source])).Where(d => d.Id == "EECS118").Select(d => d.GetMessage()).ToArray();
        Assert.That(found, Has.Exactly(1).Items);
        Assert.That(found[0], Does.Contain(reason));
    }

    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 4), Consume] static void A(in P p, ref Q q, World w, ref Tally t, Lookup<T> l, Emit<int> e) { } }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach(MaxRows = 0)] static void A(ref P p) { } }")]                                       // 0: every row
    [TestCase("public static class R { [ForEach(MaxRows = 2), With<Mark>] static void A(Entity e, World w, int n) { } }")]
    public async Task AValidBudgetIsNotReported(string source) =>
        Assert.That((await GeneratorTests.Analyze([Data + source])).Where(d => d.Id is "EECS118" || d.Severity == DiagnosticSeverity.Error), Is.Empty);

    [Test]
    public async Task ACallAPassRefusesIsAllowedInACappedMethodAndTheOthersAreToldOfIt()
    {
        const string capped = "[EcsSystem] public ref struct S { [ForEach(MaxRows = 2)] static void A(in P p, World w) => w.LoadEntities(default); }";
        Assert.That((await GeneratorTests.Analyze([Data + capped])).Where(d => d.Id == "EECS041"), Is.Empty);
        const string plain = "[EcsSystem] public ref struct S { [ForEach] static void A(in P p, World w) => w.LoadEntities(default); }";
        var found = (await GeneratorTests.Analyze([Data + plain])).Where(d => d.Id == "EECS041").Select(d => d.GetMessage()).ToArray();
        Assert.That(found, Has.Exactly(1).Items);
        Assert.That(found[0], Does.Contain("[ForEach(MaxRows = N)]"));
    }

    private const string Collect = """
        [EcsSystem] public ref struct ServeSystem
        {
            static void OnUpdate(World world)
            {
                System.Span<EntityId> ids = stackalloc EntityId[4];
                int count = world.CollectIds<P>(ids, Waiting);
                BODY
            }

            static readonly QueryFilter Waiting = QueryFilter.Create().Without<Mark>();
        }
        """;

    [TestCase("foreach (var id in ids[..count]) { var e = world.GetEntity(id); e.Get<P>().V++; }", true)]
    [TestCase("foreach (var id in ids.Slice(0, count)) { var e = world.GetEntity(id); if (e.Get<P>().V > 2) continue; e.Get<P>().V++; }", true)]
    [TestCase("foreach (var id in ids[..count]) { var e = world.GetEntity(id); if (e.Get<P>().V > 2) break; }", false)]             // break: not mechanical
    [TestCase("foreach (var id in ids[..count]) { var e = world.GetEntity(id); if (e.Get<P>().V > 2) return; }", false)]            // return ends the hook
    [TestCase("for (int i = 0; i < count; i++) world.GetEntity(ids[i]).Get<P>().V++;", false)]                                    // a for loop: reported, no fix
    [TestCase("foreach (var id in ids[..count]) { var e = world.GetEntity(id); e.Get<P>().V += count; }", false)]                  // reads the count
    [TestCase("world.DestroyEntities(ids[..count]); foreach (var id in ids[..count]) { }", false)]                                 // a statement the fix cannot move
    public async Task RowsCollectedAndLoopedOverByHandAreReported(string body, bool fixable)
    {
        var found = (await GeneratorTests.Analyze([Data + Collect.Replace("BODY", body)])).Where(d => d.Id == "EECS119").ToArray();
        Assert.That(found, Has.Exactly(1).Items);
        Assert.That(found[0].GetMessage(), Does.Contain("'ServeSystem.OnUpdate' collects up to 4 rows of P and loops over them by hand: a [ForEach(MaxRows = 4)] method over P"));
        Assert.That(found[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(found[0].Properties.ContainsKey("CollectedBody"), Is.EqualTo(fixable));
    }

    [TestCase("world.DestroyEntities(ids[..count]);")]                                                                            // no loop
    [TestCase("foreach (var e in world.Query<P>()) { }")]                                                                         // a loop over something else
    public async Task CollectingWithoutALoopOverTheIdsIsLeftAlone(string body) =>
        Assert.That((await GeneratorTests.Analyze([Data + Collect.Replace("BODY", body)])).Where(d => d.Id == "EECS119"), Is.Empty);

    [Test]
    public async Task CollectingOutsideAnUpdateHookIsLeftAlone() =>
        Assert.That((await GeneratorTests.Analyze([Data + "public static class H { public static void F(World world) { System.Span<EntityId> ids = stackalloc EntityId[4]; "
                                                     + "int count = world.CollectIds<P>(ids); foreach (var id in ids[..count]) world.GetEntity(id).Get<P>().V++; } }"]))
            .Where(d => d.Id == "EECS119"), Is.Empty);
}
