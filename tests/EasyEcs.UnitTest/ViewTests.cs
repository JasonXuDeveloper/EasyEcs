using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// [View] ref structs: one entity's components as ref / ref readonly fields with business methods, taken by [ForEach]
/// and [EntityMethod] methods (the job builds the view over the row; its fields count as the method's own parameters for
/// the schedule, the change ticks and the vector loop) and reached from an entity (AsX / TryGetX).
/// </summary>
public static class Arena
{
    public record struct Health(int Value) : IComponent, ITracked;
    public record struct Armor(int Value) : IComponent;
    public record struct Poison(int PerTick) : IComponent;
    public struct Position : IComponent { public float X, Y; }
    public struct Velocity : IComponent { public float X, Y; }
    public record struct Clock(float Dt) : ISingletonComponent;
    public record struct Stamped(int N) : ISingletonComponent;

    /// <summary>A fighter: its health (written) and armor (read), and what happens when it is hit.</summary>
    [View]
    public ref struct Fighter
    {
        public ref Health Health;
        public ref readonly Armor Armor;

        public readonly bool Dead => Health.Value <= 0;

        public void TakeDamage(int damage) => Health = Health with { Value = Health.Value - Math.Max(0, damage - Armor.Value) };
    }

    /// <summary>A mover: its position (written) and velocity (read); a step is a vector kernel.</summary>
    [View]
    public ref struct Mover
    {
        public ref Position Position;
        public ref readonly Velocity Velocity;

        public readonly float Speed2 => Velocity.X * Velocity.X + Velocity.Y * Velocity.Y;

        public void Step(float dt)
        {
            float x = Velocity.X * dt;
            Position.X += x;
            Position.Y += Velocity.Y * dt;
        }
    }

    /// <summary>
    /// A patient: its health as a Mut (only the rows it writes are stamped) and its poison (read); a tick hurts the
    /// poisoned ones only.
    /// </summary>
    [View]
    public ref struct Patient
    {
        public Mut<Health> Health;
        public ref readonly Poison Poison;

        public void Suffer()
        {
            if (Poison.PerTick > 0) Health.Write() = new Health(Health.Value.Value - Poison.PerTick);
        }
    }

    // A runner (no system): its entry point takes the caller's value.
    [ForEach]
    public static void Strike(Fighter fighter, int damage) => fighter.TakeDamage(damage);

    [EntityMethod]
    public static void Hit(Fighter fighter, int damage, ref bool dead)
    {
        fighter.TakeDamage(damage);
        dead = fighter.Dead;
    }
}

[EcsSystem]
public ref struct Poisoning
{
    [ForEach] static void Tick(Arena.Fighter fighter, in Arena.Poison poison) => fighter.TakeDamage(poison.PerTick);
}

[EcsSystem]
public ref struct Suffering
{
    [ForEach] static void Tick(Arena.Patient patient) => patient.Suffer();
}

[EcsSystem]
public ref struct SufferingParallel
{
    [ForEach(Parallel = true)] static void Tick(Arena.Patient patient) => patient.Suffer();
}

[EcsSystem]
public ref struct ArmorAudit
{
    // Reads Armor only: runs beside Poisoning, which reads it too.
    [ForEach] static void Check(in Arena.Armor armor, ref Arena.Poison poison) => poison = poison with { PerTick = Math.Max(poison.PerTick, armor.Value) };
}

[EcsSystem]
public ref struct ViewMovement
{
    [ForEach] static void Move(Arena.Mover mover, in Arena.Clock clock) => mover.Step(clock.Dt);
}

[EcsSystem]
public ref struct ViewMovementScalar
{
    [ForEach(SimdMode = SimdMode.Off)] static void Move(Arena.Mover mover, in Arena.Clock clock) => mover.Step(clock.Dt);
}

[EcsSystem]
public ref struct ViewMovementParallel
{
    [ForEach(Parallel = true)] static void Move(Arena.Mover mover, in Arena.Clock clock) => mover.Step(clock.Dt);
}

/// <summary>The hand-written form of ViewMovement: what the view costs nothing over.</summary>
[EcsSystem]
public ref struct RefMovement
{
    [ForEach]
    static void Move(ref Arena.Position position, in Arena.Velocity velocity, in Arena.Clock clock)
    {
        float x = velocity.X * clock.Dt;
        position.X += x;
        position.Y += velocity.Y * clock.Dt;
    }
}

[EcsSystem]
public ref struct HealedSince
{
    [ForEach, Changed<Arena.Health>] static void Count(ref Arena.Stamped stamped) => stamped = stamped with { N = stamped.N + 1 };
}



public class ViewTests
{
    private static Entity Fighter(World world, int health, int armor, int poison) =>
        world.CreateEntity(new Arena.Health(health), new Arena.Armor(armor), new Arena.Poison(poison));

    [Test]
    public void AViewParameterVisitsTheEntitiesThatHaveEveryComponent()
    {
        using var world = new World();
        world.AddSystem<Poisoning>();
        var armored = Fighter(world, 100, 2, 5);
        var bare = Fighter(world, 100, 0, 5);
        var noArmor = world.CreateEntity(new Arena.Health(100), new Arena.Poison(5)); // no Armor: not a fighter
        world.Update();
        Assert.That((armored.Read<Arena.Health>().Value, bare.Read<Arena.Health>().Value, noArmor.Read<Arena.Health>().Value), Is.EqualTo((97, 95, 100)));
    }

    [Test]
    public void AViewsFieldsAreTheSystemsReadsAndWrites()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<Poisoning>();
        world.AddSystem<ViewMovement>();
        var systems = world.DescribeSchedule().Groups[0].Stages.SelectMany(s => s.Systems).ToDictionary(s => s.Name);
        Assert.That(systems[nameof(Poisoning)].Writes, Is.EquivalentTo(new[] { nameof(Arena.Health) }));
        Assert.That(systems[nameof(Poisoning)].Reads, Is.EquivalentTo(new[] { nameof(Arena.Armor), nameof(Arena.Poison) }));
        Assert.That(systems[nameof(ViewMovement)].Writes, Is.EquivalentTo(new[] { nameof(Arena.Position) }));
        // Disjoint data: one stage.
        Assert.That(world.DescribeSchedule().Groups[0].Stages, Has.Length.EqualTo(1));
    }

    [Test]
    public void AViewsRefFieldOfATrackedComponentStampsTheRowsItVisits()
    {
        using var world = new World();
        world.AddSingleton<Arena.Stamped>();
        world.AddSystem<Poisoning>();
        world.AddSystem<HealedSince>();
        Fighter(world, 100, 0, 1);
        Fighter(world, 100, 0, 1);
        world.CreateEntity(new Arena.Health(100)); // not visited by Poisoning: written only by its creation
        world.Update(); // the creations and the poison count once
        world.GetSingleton<Arena.Stamped>() = new Arena.Stamped(0);
        world.Update();
        Assert.That(world.GetSingleton<Arena.Stamped>().N, Is.EqualTo(2));
    }

    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(true, 4)]
    public void AViewsMutFieldStampsOnlyTheRowsItWrites(bool parallel, int threads)
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.AddSingleton<Arena.Stamped>();
        if (parallel) world.AddSystem<SufferingParallel>();
        else world.AddSystem<Suffering>();
        world.AddSystem<HealedSince>();
        var poisoned = new Entity[100];
        for (int i = 0; i < 1000; i++)
        {
            var e = world.CreateEntity(new Arena.Health(100), new Arena.Poison(i % 10 == 0 ? 3 : 0));
            if (i % 10 == 0) poisoned[i / 10] = e;
        }

        world.Update();
        world.Update(); // the creations are behind HealedSince, whatever the order of the two systems
        world.GetSingleton<Arena.Stamped>() = new Arena.Stamped(0);
        world.Update();
        Assert.That(world.GetSingleton<Arena.Stamped>().N, Is.EqualTo(100)); // the poisoned rows, not the 1,000 visited
        Assert.That(poisoned.All(e => e.Read<Arena.Health>().Value == 91));
    }

    [Test]
    public void ALambdaPassTakesAView()
    {
        using var world = new World();
        var armored = Fighter(world, 100, 2, 5);
        var bare = Fighter(world, 100, 0, 5);
        world.ForEach(static (Arena.Fighter fighter, in Arena.Poison poison) => fighter.TakeDamage(poison.PerTick));
        Assert.That((armored.Read<Arena.Health>().Value, bare.Read<Arena.Health>().Value), Is.EqualTo((97, 95)));
    }

    [TestCase(1)]
    [TestCase(4)]
    public void AParallelLambdaPassThroughAViewWithAMutFieldStampsOnlyTheRowsItWrites(int threads)
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
        world.AddSingleton<Arena.Stamped>();
        world.AddSystem<HealedSince>();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Arena.Health(100), new Arena.Poison(i % 10 == 0 ? 3 : 0));
        world.Update(); // the creations counted
        world.GetSingleton<Arena.Stamped>() = new Arena.Stamped(0);
        world.ForEachParallel(static (Arena.Patient patient) => patient.Suffer());
        world.Update();
        Assert.That(world.GetSingleton<Arena.Stamped>().N, Is.EqualTo(100)); // the poisoned rows, not the 1,000 visited
    }

    [Test]
    public void AnEntitysViewWithAMutFieldWritesThroughIt()
    {
        using var world = new World();
        var e = world.CreateEntity(new Arena.Health(10), new Arena.Poison(4));
        e.AsPatient().Suffer();
        Assert.That(e.Read<Arena.Health>().Value, Is.EqualTo(6));
        Assert.That(world.CreateEntity(new Arena.Health(1)).TryGetPatient(out _), Is.False);
    }

    [Test]
    public void APassThroughAViewWithAMutFieldAllocatesNothing()
    {
        using var world = new World();
        world.AddSystem<Suffering>();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Arena.Health(100), new Arena.Poison(i & 1));
        world.Update();
        long before = Allocations.Baseline();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void AnEntityReachesItsViewAndWritesThroughIt()
    {
        using var world = new World();
        var e = Fighter(world, 10, 3, 0);
        var fighter = e.AsFighter();
        fighter.TakeDamage(5);
        Assert.That((e.Read<Arena.Health>().Value, fighter.Dead), Is.EqualTo((8, false)));
        Assert.That(e.TryGetFighter(out var again) && again.Health.Value == 8);

        var lone = world.CreateEntity(new Arena.Health(1));
        Assert.That(lone.TryGetFighter(out var none), Is.False);
        Assert.That(() => lone.AsFighter(), Throws.Exception);
        e.Destroy();
        Assert.That(e.TryGetFighter(out _), Is.False);
    }

    [Test]
    public void RunnersAndEntityMethodsTakeViews()
    {
        using var world = new World();
        var e = Fighter(world, 10, 1, 0);
        Arena.Strike(world, 4);
        Assert.That(e.Read<Arena.Health>().Value, Is.EqualTo(7));
        bool dead = false;
        Assert.That(Arena.Hit(e, 8, ref dead), Is.True);
        Assert.That((e.Read<Arena.Health>().Value, dead), Is.EqualTo((0, true)));
        var lone = world.CreateEntity(new Arena.Health(1));
        Assert.That(Arena.Hit(lone, 8, ref dead), Is.False);
    }

    [Test]
    public void AViewMethodThatIsAKernelGetsAVectorLoop()
    {
        using var world = new World();
        world.AddSystem<ViewMovement>();
        world.AddSystem<ViewMovementScalar>();
        var loops = world.DescribeSchedule().Groups[0].Stages.SelectMany(s => s.Systems).ToDictionary(s => s.Name, s => s.Loops[0]);
        Assert.That(loops[nameof(ViewMovement)], Does.StartWith("Move: vector"));
        Assert.That(loops[nameof(ViewMovementScalar)], Is.EqualTo("Move: scalar (SimdMode = SimdMode.Off)"));
    }

    [TestCase(1003)]
    [TestCase(37)]
    public void TheVectorLoopThroughAViewEqualsTheScalarMethodAndTheRefsByHand(int count)
    {
        var results = new[] { typeof(ViewMovement), typeof(ViewMovementScalar), typeof(RefMovement) }.Select(system =>
        {
            using var world = new World();
            world.AddSingleton<Arena.Clock>() = new Arena.Clock(0.37f);
            var entities = Movers(world, count);
            if (system == typeof(ViewMovement)) world.AddSystem<ViewMovement>();
            else if (system == typeof(ViewMovementScalar)) world.AddSystem<ViewMovementScalar>();
            else world.AddSystem<RefMovement>();
            for (int i = 0; i < 3; i++) world.Update();
            return entities.Select(e => (e.Get<Arena.Position>().X, e.Get<Arena.Position>().Y)).ToArray();
        }).ToArray();
        Assert.That(results[0], Is.EqualTo(results[1]));
        Assert.That(results[0], Is.EqualTo(results[2]));
    }

    [Test]
    public void AParallelPassThroughAViewIsBitIdenticalAtOneAndFourThreads()
    {
        (float, float)[] Run(int threads)
        {
            using var world = new World(new WorldOptions { MaxDegreeOfParallelism = threads });
            world.AddSingleton<Arena.Clock>() = new Arena.Clock(0.1f);
            var entities = Movers(world, World.MaxChunkRows * 3 + 17);
            world.AddSystem<ViewMovementParallel>();
            for (int i = 0; i < 4; i++) world.Update();
            return entities.Select(e => (e.Get<Arena.Position>().X, e.Get<Arena.Position>().Y)).ToArray();
        }

        Assert.That(Run(4), Is.EqualTo(Run(1)));
    }

    [Test]
    public void APassThroughAViewAllocatesNothing()
    {
        using var world = new World();
        world.AddSingleton<Arena.Clock>() = new Arena.Clock(0.1f);
        Movers(world, 500);
        for (int i = 0; i < 100; i++) Fighter(world, 1000, 1, 2);
        world.AddSystem<ViewMovement>();
        world.AddSystem<ViewMovementScalar>();
        world.AddSystem<Poisoning>();
        world.Update();
        world.Update();
        var e = world.CreateEntity(new Arena.Health(5), new Arena.Armor(0));
        e.AsFighter().TakeDamage(0);
        long before = Allocations.Baseline();
        for (int i = 0; i < 10; i++) world.Update();
        long passes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = Allocations.Baseline();
        for (int i = 0; i < 100; i++) e.AsFighter().TakeDamage(0);
        Assert.That((passes, GC.GetAllocatedBytesForCurrentThread() - before), Is.EqualTo((0L, 0L)));
    }

    private static Entity[] Movers(World world, int count)
    {
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            var velocity = new Arena.Velocity { X = i * 0.25f - 3f, Y = 1f / (i + 1) };
            entities[i] = i % 7 == 0
                ? world.CreateEntity(new Arena.Position { X = i, Y = -i }, velocity, new Arena.Armor(1)) // a second table
                : world.CreateEntity(new Arena.Position { X = i, Y = -i }, velocity);
        }

        return entities;
    }
}

/// <summary>The rules of views (EECS098-EECS101) and what the generators write for one.</summary>
public class ViewRuleTests
{
    private const string Views = """
        [View] public ref struct PQ { public ref P P; public ref readonly Q Q; public void Add() => P.V += Q.V; }

        """;

    private static async Task<string[]> Ids(string source) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, Views, source]))
            .Where(d => d.Id is "EECS098" or "EECS099" or "EECS100" or "EECS101" || d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id).Distinct().OrderBy(x => x).ToArray();

    [TestCase("[View] public struct V { public int N; }")]                                                 // no ref struct
    [TestCase("[View] public ref struct V<TC> where TC : struct { public ref P P; }")]                     // generic
    [TestCase("[View] public ref struct V { public ref P P; public int N; }")]                             // a value
    [TestCase("[View] public ref struct V { public ref P P; public ref int N; }")]                         // no component
    [TestCase("[View] public ref struct V { private ref P P; public void A() => P.V++; }")]                // hidden
    [TestCase("[View] public ref struct V { public readonly ref P P; }")]                                  // readonly ref
    [TestCase("[View] public ref struct V { public ref Owned O; }")]                                       // writes an indexed one
    [TestCase("[View] public ref struct V { public ref P A; public ref readonly P B; }")]                  // twice
    [TestCase("[View] public ref struct V { public void A() { } }")]                                       // nothing
    [TestCase("[View] public ref struct V { public readonly Mut<T> T; }")]                                 // a readonly Mut
    [TestCase("[View] public ref struct V { public Mut<Owned> O; }")]                                      // a Mut of an indexed one
    public async Task AViewTheGeneratorsCannotUseIsAnError(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS098" }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(PQ v, ref P p) => v.Add(); }")]
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(PQ v, Maybe<Q> q) => v.Add(); }")]
    [TestCase("[View] public ref struct OnlyQ { public ref readonly Q Q; } [EcsSystem] public ref struct S { [ForEach] static void A(PQ v, OnlyQ w) => v.Add(); }")]
    [TestCase("public static class R { [EntityMethod] static void A(PQ v, in Q q) => v.Add(); }")]                                // [EntityMethod]
    public async Task AComponentReachedTwiceIsAnError(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS099" }));

    [TestCase("[View] public ref struct V { public ref P P; public void A(World w) { } }")]
    [TestCase("[View] public ref struct V { public ref P P; public void A(Entity e) => P.V = e.Get<Q>().V; }")]
    [TestCase("[View] public ref struct V { public ref P P; public void A(Lookup<Q> qs, EntityId id) { } }")]
    [TestCase("[View] public ref struct V { public ref P P; public void A(PQ other) => P.V = other.P.V; }")]
    [TestCase("[View] public ref struct V { public ref P P; public static int Count; public void A() => Count++; }")]
    [TestCase("public static class Global { public static World World; } [View] public ref struct V { public ref P P; public void A() => P.V = Global.World.EntityCount; }")]
    public async Task AViewMemberThatReachesBeyondItsEntityIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS100" }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p, in Q q) { var v = new PQ { P = ref p, Q = ref q }; v.Add(); } }")]
    [TestCase("public static class R { [EntityMethod] static void A(ref P p, in Q q, int n) { var v = new PQ { P = ref p, Q = ref q }; v.Add(); } }")]
    [TestCase("public static class U { public static void F(Entity e) { var v = new PQ { P = ref e.Get<P>(), Q = ref e.Read<Q>() }; v.Add(); } }")]
    [TestCase("public static class U { public static void F(Entity e) { var v = new PQ { P = ref e.P, Q = ref e.Q }; v.Add(); } }")]
    public async Task AViewBuiltByHandIsReported(string source) =>
        Assert.That(await Ids(source), Is.EqualTo(new[] { "EECS101" }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(PQ v, in T t) { v.Add(); v.P.V += t.V; } }")]                // a view and another component
    [TestCase("[View] public ref struct V { public ref P P; public static readonly int Max = 3; public const int Min = 0; public void A() => P.V = System.Math.Clamp(P.V, Min, Max); }")] // static data
    [TestCase("[View] public ref struct V { public ref P P; public readonly int Twice() => this.P.V * 2; }")]                         // its own fields
    [TestCase("public static class U { public static void F(Entity a, Entity b) { var v = new PQ { P = ref a.Get<P>(), Q = ref b.Read<Q>() }; v.Add(); } }")]  // two entities
    [TestCase("public static class U { public static void F(ref P p, in Q q) { var v = new PQ { P = ref p, Q = ref q }; v.Add(); } }")]  // no pass
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(ref P p) { var v = new PQ { P = ref p }; v.P.V++; } }")]           // not every field
    [TestCase("[View] public ref struct V { public ref P P; public void A(int n, in Q q) => P.V += n + q.V; }")]                          // plain values
    public async Task OtherViewCodeIsNotReported(string source) =>
        Assert.That(await Ids(source), Is.Empty);

    private const string Tracked = """
        [View] public ref struct VT { public ref T T; public ref readonly Q Q; public void Add() => T.V += Q.V; public readonly int Twice() => T.V * 2; }
        [View] public ref struct VM { public Mut<T> T; public ref readonly Q Q; public void Up(int n) { if (n > 0) T.Write().V += n; } }

        """;

    private static async Task<string[]> SomeRows(string source) =>
        (await GeneratorTests.Analyze([GeneratorTests.Components, Tracked, source]))
            .Where(d => d.Id == "EECS053" || d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).ToArray();

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VT v) { if (v.Q.V > 0) v.T.V++; } }")]           // the field on some rows
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VT v) { if (v.Q.V > 0) return; v.Add(); } }")]   // a member on some rows
    [TestCase("public static class R { [EntityMethod] static void A(VT v, int n) { if (n > 0) v.T.V = n; } }")]              // one entity
    [TestCase("[View] public ref struct W { public ref T T; public void Up(int n) { if (n > 0) T.V += n; } }")]           // in the view's member
    public async Task ATrackedComponentWrittenThroughAViewOnSomeRowsIsReported(string source) =>
        Assert.That(await SomeRows(source), Is.EqualTo(new[] { "EECS053" }));

    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VT v) => v.T.V++; }")]                           // every row
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VT v) => v.Add(); }")]                           // a member on every row
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VT v, ref P p) { if (v.Q.V > 0) p.V = v.Twice(); else v.Add(); } }")] // a member naming the field counts as a write (conservative)
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VM v, in P p) => v.Up(p.V); }")]                 // a Mut field
    [TestCase("[EcsSystem] public ref struct S { [ForEach] static void A(VM v) { if (v.Q.V > 0) v.T.Write().V++; } }")]   // a Mut field written on some rows
    public async Task OtherWritesThroughViewsAreNotReported(string source) =>
        Assert.That(await SomeRows(source), Is.Empty);

    [Test]
    public void AJobStampsTheRowsAMutFieldWrites()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, Tracked,
            "public static class R { [EntityMethod] static void A(VM v) => v.Up(1); } [EcsSystem] public ref struct S { [ForEach] static void B(VM v) => v.Up(1); }"), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("global::EasyEcs.MutColumn.Of<global::T>(in entities)"));              // a run's change-tick column
        Assert.That(generated, Does.Contain("T = new global::EasyEcs.Mut<global::T>(ref c1, entity)"));            // one entity
        Assert.That(generated, Does.Contain("T = new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), in v0_0, (int)i)"));
        Assert.That(generated, Does.Contain("T = new global::EasyEcs.Mut<global::T>(ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in @this.Read<global::T>()), @this)")); // AsVM
    }

    // A lambda pass takes a view as a [ForEach] method does; its loop stays scalar (EECS097 says why), a component
    // reached twice or an invalid view is EECS095.
    [TestCase("world.ForEach(static (PQ v) => v.Add())", "EECS097", "takes a view")]
    [TestCase("world.ForEach(static (PQ v, ref P p) => v.Add())", "EECS095", "through view 'v'")]
    [TestCase("world.ForEach(static (Bad v) => { })", "EECS095", "a view the generators cannot use")]
    public async Task ALambdaPassTakesAView(string call, string id, string text)
    {
        var found = (await GeneratorTests.Analyze([GeneratorTests.Components, Views,
                "[View] public ref struct Bad { public int N; }\n" + $"public static class Run {{ public static void F(World world) => {call}; }}"]))
            .Where(d => d.Id is "EECS095" or "EECS096" or "EECS097").ToArray();
        Assert.That(found.Select(d => d.Id), Is.EqualTo(new[] { id }), string.Join("\n", found.Select(d => d.ToString())));
        Assert.That(found[0].GetMessage(), Does.Contain(text));
    }

    [Test]
    public void AnEntityGetsAsAndTryGetOfAView()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, Views), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("AsPQ()"));
        Assert.That(generated, Does.Contain("P = ref @this.Get<global::P>()"));
        Assert.That(generated, Does.Contain("Q = ref @this.Read<global::Q>()"));
        Assert.That(generated, Does.Contain("TryGetPQ(out global::PQ"));
    }

    // A void view method's argument read in place (no local): each field of a component gets the same operation, so the
    // vector loop takes the columns as flat lanes, as it does for the method written over the refs.
    [TestCase("m.Step(dt.Value)", true)]
    [TestCase("m.Step(dt.Value * 2f)", false)]                                  // an expression: a local per row
    public void AViewMethodsPlainArgumentKeepsFlatLanes(string call, bool flat)
    {
        const string source = """
            public struct Pos2 : IComponent { public float X, Y; }
            public struct Vel2 : IComponent { public float X, Y; }
            public struct Dt : ISingletonComponent { public float Value; }
            [View] public ref struct MV { public ref Pos2 P; public ref readonly Vel2 V; public void Step(float d) { P.X += V.X * d; P.Y += V.Y * d; } }

            """;
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(source,
            $"[EcsSystem] public ref struct S {{ [ForEach] static void A(MV m, in Dt dt) => {call}; }}"), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("Interleaved.Load2("));                 // a vector loop either way
        Assert.That(generated.Contains("lanes = rows * 2;"), Is.EqualTo(flat), generated);
    }

    [Test]
    public void AJobBuildsTheViewOverItsRow()
    {
        GeneratorTests.Driver().RunGeneratorsAndUpdateCompilation(GeneratorTests.Compile(GeneratorTests.Components, Views,
            "public static class R { [EntityMethod] static void A(in T t, PQ v) => v.Add(); } [EcsSystem] public ref struct S { [ForEach(Parallel = true)] static void B(PQ v) => v.Add(); }"), out var output, out _);
        Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        string generated = string.Concat(output.SyntaxTrees.Select(t => t.ToString()));
        Assert.That(generated, Does.Contain("new global::PQ { P = ref c2, Q = ref c3 }"));                          // one entity: after the component parameter
        Assert.That(generated, Does.Contain("new global::PQ { P = ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r1, i), Q = ref global::System.Runtime.CompilerServices.Unsafe.Add(ref r2, i) }")); // a run of rows: the vector loop's scalar tail
        Assert.That(generated, Does.Contain("Write<global::P>()").And.Contain("Read<global::Q>()"));
    }
}
