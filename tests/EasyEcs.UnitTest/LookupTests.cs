using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>
/// <see cref="Lookup{T}"/>: reads of any entity's component from a [ForEach] method, scheduled as a read, with indexes
/// built before the pass.
/// </summary>
public class LookupTests
{
    public record struct Place(float X, float Y) : IComponent;
    public record struct Chaser(EntityId Target) : IComponent;
    public record struct Aim2(float X, float Y, bool Lost) : IComponent;
    public record struct Wants(int Login) : IComponent;
    public record struct Found(EntityId Id, int Followers) : IComponent;

    [EcsSystem]
    public ref struct Chase
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Steer(in Place place, in Chaser chaser, ref Aim2 heading, Lookup<Place> places)
        {
            var target = places[chaser.Target];
            heading = target.HasValue
                ? new Aim2(target.Value.X - place.X, target.Value.Y - place.Y, false)
                : new Aim2(0, 0, true);
        }
    }

    [EcsSystem]
    public ref struct MovePlaces
    {
        [ForEach]
        static void Move(ref Place place) => place.X += 1;
    }

    [EcsSystem]
    public ref struct ReadPlaces
    {
        [ForEach]
        static void Read(in Place place, ref Wants wants) => wants.Login = (int)place.X;
    }

    /// <summary>A unique index found by key in a parallel pass.</summary>
    [EcsSystem]
    public ref struct FindLogin
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Find(in Wants wants, ref Found found, Lookup<Login> logins) =>
            found.Id = logins.TryFind(wants.Login, out var entity) ? entity : default;
    }

    /// <summary>A value index counted by key: built before the pass, never by a lookup during it.</summary>
    [EcsSystem]
    public ref struct CountFollowers
    {
        [ForEach(Parallel = true, ChunkRows = 64), With<Place>]
        static void Count(ref Found found, Lookup<Follows> follows) =>
            found.Followers = follows.Count(found.Id);
    }

    /// <summary>The lookup reaches TryFind through a helper: the method has no index for it.</summary>
    [EcsSystem]
    public ref struct FindThroughHelper
    {
        [ForEach]
        static void Find(ref Found found, Lookup<Follows> follows) => found.Followers = Has(follows, found.Id) ? 1 : 0;

        private static bool Has(Lookup<Follows> follows, EntityId target) => follows.TryFind(target, out _);
    }

    [Test]
    public void AParallelLookupReadsEveryTargetOnEveryRun()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var targets = new Entity[1000];
        world.CreateEntities(targets, new Place(0, 0));
        for (int i = 0; i < targets.Length; i++) targets[i].Place = new Place(i, -i);
        var chasers = new Entity[20_000];
        world.CreateEntities(chasers, new Place(0, 0), new Chaser(), new Aim2());
        for (int i = 0; i < chasers.Length; i++)
        {
            chasers[i].Place = new Place(i % 7, i % 11);
            chasers[i].Chaser = new Chaser(targets[i % targets.Length].Id);
        }

        targets[5].Destroy(); // stale ids read as no value
        world.AddSystem<Chase>();
        for (int run = 0; run < 100; run++)
        {
            world.Update();
            for (int i = 0; i < chasers.Length; i++)
            {
                int t = i % targets.Length;
                var expected = t == 5 ? new Aim2(0, 0, true) : new Aim2(t - i % 7, -t - i % 11, false);
                if (chasers[i].Aim2 != expected) Assert.Fail($"run {run}, chaser {i}: {chasers[i].Aim2}");
            }
        }
    }

    [Test]
    public void ReadAndHasCheckTheEntity()
    {
        using var world = new World();
        var placed = world.CreateEntity(new Place(3, 4));
        var bare = world.CreateEntity(new Health(1));
        var check = new Check(world);
        Assert.That(check.Read(placed.Id), Is.EqualTo(new Place(3, 4)));
        Assert.That(check.Has(placed.Id), Is.True);
        Assert.That(check.Has(bare.Id), Is.False);
        Assert.Throws<InvalidOperationException>(() => check.Read(bare.Id));
        placed.Destroy();
        Assert.That(check.Has(placed.Id), Is.False);
    }

    [Test]
    public void AnIdPastTheWorldsRecordsFindsNothing()
    {
        using var world = new World();
        world.CreateEntity(new Place(3, 4));
        var check = new Check(world);
        var foreign = new EntityId(1 << 26, 1); // from data: another world, a message, a save
        Assert.That(check.Has(foreign), Is.False);
        Assert.That(check.Get(foreign), Is.False);
        Assert.Throws<InvalidOperationException>(() => check.Read(foreign));
    }

    private readonly struct Check(World world)
    {
        public bool Get(EntityId id) => new Lookup<Place>(world, null)[id].HasValue;
        public Place Read(EntityId id) => new Lookup<Place>(world, null).Read(id);
        public bool Has(EntityId id) => new Lookup<Place>(world, null).Has(id);
    }

    [Test]
    public void ALookupIsScheduledAsARead()
    {
        using (var world = new World())
        {
            world.AddSystem<Chase>();
            world.AddSystem<MovePlaces>(); // writes Place: another stage
            Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString());
        }

        using (var world = new World())
        {
            world.AddSystem<Chase>();
            world.AddSystem<ReadPlaces>(); // reads Place: the same stage
            Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(1), world.DescribeSchedule().ToString());
        }
    }

    [Test]
    public void AUniqueIndexIsFoundByKeyInParallel()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var accounts = new Entity[500];
        world.Reserve<Login>(accounts.Length);
        for (int i = 0; i < accounts.Length; i++) accounts[i] = world.CreateEntity(new Login(i * 3)); // unique keys
        var seekers = new Entity[10_000];
        world.CreateEntities(seekers, new Wants(0), new Found());
        for (int i = 0; i < seekers.Length; i++) seekers[i].Wants = new Wants(i % 1600);
        world.AddSystem<FindLogin>();
        for (int run = 0; run < 100; run++)
        {
            accounts[run % accounts.Length].Set(new Login(-1 - run)); // re-keyed between passes
            world.Update();
            for (int i = 0; i < seekers.Length; i++)
            {
                int key = i % 1600, account = key / 3; // accounts 0..run lost their key
                var expected = key % 3 == 0 && account < accounts.Length && account > run ? accounts[account].Id : default;
                if (seekers[i].Found.Id != expected) Assert.Fail($"run {run}, seeker {i}: {seekers[i].Found.Id}");
            }
        }
    }

    [Test]
    public void AValueIndexIsBuiltBeforeAParallelPass()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var leaders = new Entity[100];
        world.CreateEntities(leaders, new Place(0, 0), new Found());
        foreach (var leader in leaders) leader.Found = new Found(leader.Id, 0);
        var followers = new Entity[20_000];
        world.CreateEntities(followers, new Follows());
        for (int i = 0; i < followers.Length; i++) followers[i].Set(new Follows(leaders[i % leaders.Length].Id));
        // No index exists yet: the first lookup would build it, on whichever thread got there first.
        world.AddSystem<CountFollowers>();
        for (int run = 0; run < 100; run++)
        {
            followers[run].Set(new Follows(leaders[0].Id)); // kept by the index between passes
            world.Update();
            int total = 0;
            foreach (var leader in leaders) total += leader.Found.Followers;
            Assert.That(total, Is.EqualTo(followers.Length), $"run {run}");
            Assert.That(leaders[0].Found.Followers, Is.EqualTo(200 + run), $"run {run}"); // followers 1..run moved to it
            if (run > 0) Assert.That(leaders[run].Found.Followers, Is.EqualTo(199), $"run {run}");
        }
    }

    /// <summary>Walks the followers of each leader (a value index's Find) in a parallel pass.</summary>
    [EcsSystem]
    public ref struct SumFollowers
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Sum(ref Found found, Lookup<Follows> follows, Lookup<Place> places)
        {
            int total = 0;
            foreach (var follower in follows.Find(found.Id)) total += (int)places.Read(follower.Id).X;
            found.Followers = total;
        }
    }

    [EcsSystem]
    public ref struct Refollow
    {
        [ForEach]
        static void Turn(Entity entity, in Follows follows) => entity.Set(follows);
    }

    /// <summary>Reaches an index around the lookup (a static field): a lazy build in a parallel pass.</summary>
    [EcsSystem]
    public ref struct CountThroughStatic
    {
        [ForEach(Parallel = true, ChunkRows = 64)]
        static void Count(ref Found found) => found.Followers = Stray.Index.Count(found.Id);
    }

    public static class Stray
    {
        public static ValueIndex<Follows, EntityId> Index;
    }

    [Test]
    public void AValueIndexIsWalkedByKeyInParallel()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4, ParallelSystems = true });
        var leaders = new Entity[50];
        world.CreateEntities(leaders, new Found());
        foreach (var leader in leaders) leader.Found = new Found(leader.Id, 0);
        var followers = new Entity[10_000];
        world.CreateEntities(followers, new Follows(), new Place(1, 0));
        for (int i = 0; i < followers.Length; i++)
        {
            followers[i].Set(new Follows(leaders[i % leaders.Length].Id));
            followers[i].Place = new Place(i % 3, 0);
        }

        var expected = new int[leaders.Length];
        for (int i = 0; i < followers.Length; i++) expected[i % leaders.Length] += i % 3;
        world.AddSystem<SumFollowers>();
        world.AddSystem<ReadPlaces>(); // reads Place too: may share the stage
        for (int run = 0; run < 100; run++)
        {
            world.Update();
            for (int l = 0; l < leaders.Length; l++)
                if (leaders[l].Found.Followers != expected[l]) Assert.Fail($"run {run}, leader {l}: {leaders[l].Found.Followers}");
        }
    }

    [Test]
    public void AWriterOfTheLookedUpComponentRunsInAnotherStage()
    {
        using var world = new World(new WorldOptions { ParallelSystems = true });
        world.AddSystem<SumFollowers>();
        world.AddSystem<Refollow>(); // writes Follows: the index changes only between the stages
        Assert.That(world.DescribeSchedule().Groups[0].Stages.Length, Is.EqualTo(2), world.DescribeSchedule().ToString());
    }

    [Test]
    public void AnIndexIsNeverBuiltDuringAParallelPass()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        world.CreateEntities(new Entity[5000], new Found());
        world.CreateEntity(new Follows());
        Stray.Index = world.Index<Follows, EntityId>(); // created, not built
        world.AddSystem<CountThroughStatic>();
        var error = Assert.Throws<InvalidOperationException>(world.Update);
        Assert.That(error!.Message, Does.Contain("not built"));
        Stray.Index.Count(default); // built on the owner thread, outside any pass
        Assert.DoesNotThrow(world.Update);
        Stray.Index = null;
    }

    /// <summary>A serial pass reaching an index through the World: it may still build it on its first lookup.</summary>
    [EcsSystem]
    public ref struct CountInSerial
    {
        [ForEach]
        static void Count(World world, ref Found found) => found.Followers = world.FollowsIndex.Count(found.Id);
    }

    [Test]
    public void ASerialPassStillBuildsAnIndexOnItsFirstLookup()
    {
        using var world = new World();
        var leader = world.CreateEntity(new Found());
        leader.Found = new Found(leader.Id, 0);
        world.CreateEntities(new Entity[10], new Follows(leader.Id));
        world.AddSystem<CountInSerial>(); // a frozen pass on the owner thread, no parallel region: the build is safe
        world.Update();
        Assert.That(leader.Found.Followers, Is.EqualTo(10));
    }

    [Test]
    public void AKeyLookupWithoutAnIndexThrows()
    {
        using var world = new World();
        world.CreateEntity(new Found());
        world.AddSystem<FindThroughHelper>();
        Assert.Throws<InvalidOperationException>(() => world.Update());
    }

    [Test]
    public void AParallelLookupDoesNotAllocateAfterItsFirstPass()
    {
        using var world = new World(new WorldOptions { MaxDegreeOfParallelism = 4 });
        var accounts = new Entity[100];
        world.Reserve<Login>(accounts.Length);
        for (int i = 0; i < accounts.Length; i++) accounts[i] = world.CreateEntity(new Login(i)); // unique keys
        world.CreateEntities(new Entity[World.ParallelChunkRows * 2], new Wants(7), new Found(), new Place(1, 1));
        world.AddSystem<FindLogin>();
        world.AddSystem<CountFollowers>();
        for (int i = 0; i < 10; i++) world.Update();
        Assert.That(Allocations.Of(() => { for (int i = 0; i < 200; i++) world.Update(); }), Is.Zero);
    }
}
