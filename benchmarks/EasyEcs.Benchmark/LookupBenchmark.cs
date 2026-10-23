using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// Reads of other entities from a [ForEach] pass: chasers aim at a target's position (one id per chaser, 1,000 targets),
/// and seekers find an account by its unique key. <see cref="Lookup{T}"/> in parallel and on one thread, against what a
/// method writes without it: a <see cref="World"/> parameter, <c>world.TryGetEntity(id, out var e) &amp;&amp;
/// e.TryGet&lt;T&gt;(out var value)</c> (and <c>world.AccountIndex.TryGet</c>), which keeps the method on one thread.
/// </summary>
[MemoryDiagnoser]
public class LookupBenchmark
{
    [Params(10_000, 100_000)] public int Entities;

    private World _world;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World(new WorldOptions { InitialEntityCapacity = Entities * 2 + 2_000 });
        var targets = new Entity[1_000];
        _world.CreateEntities(targets, new Place(0, 0));
        for (int i = 0; i < targets.Length; i++) targets[i].Place = new Place(i, -i);
        var chasers = new Entity[Entities];
        _world.CreateEntities(chasers, new Place(0, 0), new Chaser(), new Aim());
        for (int i = 0; i < chasers.Length; i++)
        {
            chasers[i].Place = new Place(i % 7, i % 11);
            chasers[i].Chaser = new Chaser(targets[(i * 31) % targets.Length].Id);
        }

        _world.Reserve<Account>(1_000);
        for (int i = 0; i < 1_000; i++) _world.CreateEntity(new Account(i * 3));
        var seekers = new Entity[Entities];
        _world.CreateEntities(seekers, new Wants(0), new Found());
        for (int i = 0; i < seekers.Length; i++) seekers[i].Wants = new Wants(i % 3_000);
        for (int i = 0; i < 5; i++) ChaseParallel();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(Baseline = true)]
    public void ChaseWorld() => Chasing.ByWorld(_world);

    [Benchmark]
    public void ChaseSerial() => Chasing.BySerialLookup(_world);

    [Benchmark]
    public void ChaseParallel() => Chasing.ByLookup(_world);

    [Benchmark]
    public void FindWorld() => Chasing.FindByWorld(_world);

    [Benchmark]
    public void FindSerial() => Chasing.FindBySerialLookup(_world);

    [Benchmark]
    public void FindParallel() => Chasing.FindByLookup(_world);

    public record struct Place(float X, float Y) : IComponent;
    public record struct Chaser(EntityId Target) : IComponent;
    public record struct Aim(float X, float Y) : IComponent;
    public record struct Account(int Login) : IComponent, IUniqueIndexed<int>
    {
        public readonly int Key => Login;
    }

    public record struct Wants(int Login) : IComponent;
    public record struct Found(EntityId Account) : IComponent;

    public static class Chasing
    {
        private static Aim Toward(in Place from, in Place to) => new(to.X - from.X, to.Y - from.Y);

        [ForEach]
        public static void ByWorld(World world, in Place place, in Chaser chaser, ref Aim aim) =>
            aim = world.TryGetEntity(chaser.Target, out var target) && target.TryGet<Place>(out var at) ? Toward(in place, in at) : default;

        [ForEach]
        public static void BySerialLookup(in Place place, in Chaser chaser, ref Aim aim, Lookup<Place> places)
        {
            var at = places[chaser.Target];
            aim = at.HasValue ? Toward(in place, in at.Value) : default;
        }

        [ForEach(Parallel = true)]
        public static void ByLookup(in Place place, in Chaser chaser, ref Aim aim, Lookup<Place> places)
        {
            var at = places[chaser.Target];
            aim = at.HasValue ? Toward(in place, in at.Value) : default;
        }

        [ForEach]
        public static void FindByWorld(World world, in Wants wants, ref Found found) =>
            found = new Found(world.AccountIndex.TryGet(wants.Login, out var account) ? account.Id : default);

        [ForEach]
        public static void FindBySerialLookup(in Wants wants, ref Found found, Lookup<Account> accounts) =>
            found = new Found(accounts.TryFind(wants.Login, out var entity) ? entity : default);

        [ForEach(Parallel = true)]
        public static void FindByLookup(in Wants wants, ref Found found, Lookup<Account> accounts) =>
            found = new Found(accounts.TryFind(wants.Login, out var entity) ? entity : default);
    }
}
