using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class EntityTests
{
    private World _world;

    [SetUp]
    public void SetUp() => _world = new World(new WorldOptions { InitialEntityCapacity = 2 });

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void HandlesReadTheirRowsAfterTheTableGrows()
    {
        // The table starts at two rows: every growth moves the pinned columns a handle reads through.
        var first = _world.CreateEntity(new Position(1, 2), new Name("first"));
        var all = new List<Entity> { first };
        for (int i = 1; i < 1000; i++) all.Add(_world.CreateEntity(new Position(i, -i), new Name("n")));
        first.Get<Position>().X = 7;
        Assert.That(first.Get<Position>(), Is.EqualTo(new Position(7, 2)));
        Assert.That(first.Get<Name>().Value, Is.EqualTo("first"));
        for (int i = 1; i < all.Count; i++) Assert.That(all[i].Get<Position>(), Is.EqualTo(new Position(i, -i)));
    }

    [Test]
    public void CreateEntitiesPlacesABatchAndDestroyEntitiesRemovesMatches()
    {
        var existing = _world.CreateEntity(new Position(9, 9));
        var batch = new Entity[100];
        _world.CreateEntities(batch, new Position(1, 2), new Velocity(3, 4));

        Assert.That(_world.EntityCount, Is.EqualTo(101));
        for (int i = 0; i < batch.Length; i++)
        {
            Assert.That(batch[i].Get<Position>(), Is.EqualTo(new Position(1, 2)));
            Assert.That(batch[i].Get<Velocity>(), Is.EqualTo(new Velocity(3, 4)));
        }

        for (int i = 0; i < batch.Length; i += 2) batch[i].Set(new Frozen());
        Assert.That(_world.DestroyEntities<Position>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(50));
        Assert.That(_world.EntityCount, Is.EqualTo(51));
        Assert.That(batch[0].IsAlive, Is.False);
        Assert.That(batch[1].IsAlive, Is.True);

        Assert.That(_world.DestroyEntities<Position>(), Is.EqualTo(51));
        Assert.That(existing.IsAlive, Is.False);
        Assert.That(_world.EntityCount, Is.Zero);
    }

    [Test]
    public void DestroyEntitiesDuringAPassSkipsTheDestroyed()
    {
        var batch = new Entity[10];
        _world.CreateEntities(batch, new Health(1));
        int visited = 0;
        foreach (var item in _world.Query<Health>())
        {
            if (visited++ == 0) Assert.That(_world.DestroyEntities<Health>(), Is.EqualTo(10));
        }
        Assert.That(visited, Is.EqualTo(1));
        Assert.That(_world.EntityCount, Is.Zero);
    }

    [Test]
    public void CreateAndDestroyTrackTheCount()
    {
        var a = _world.CreateEntity();
        var b = _world.CreateEntity();
        Assert.That(a.Id, Is.Not.EqualTo(b.Id));
        Assert.That(_world.EntityCount, Is.EqualTo(2));

        a.Destroy();
        a.Destroy(); // destroying a stale handle is a no-op
        Assert.That(_world.EntityCount, Is.EqualTo(1));
        Assert.That(a.IsAlive, Is.False);
        Assert.That(b.IsAlive, Is.True);
    }

    [Test]
    public void RecycledIdsGetANewVersionAndOldHandlesStayStale()
    {
        var old = _world.CreateEntity().Set(new Health(1));
        old.Destroy();
        var reused = _world.CreateEntity().Set(new Health(2));

        Assert.That(reused.Id, Is.EqualTo(old.Id));
        Assert.That(reused.Version, Is.EqualTo(old.Version + 1));
        Assert.That(reused, Is.Not.EqualTo(old));
        Assert.That(reused.Get<Health>().Value, Is.EqualTo(2), "the stale handle never touches the new occupant");
    }

    [Test]
    public void StaleAndDefaultHandlesAnswerQuestionsAndRejectAccess()
    {
        var old = _world.CreateEntity().Set(new Health(1), new Position(1, 1));
        old.Destroy();
        _world.CreateEntity().Set(new Health(2), new Position(2, 2));     // reuses the id
        foreach (var handle in new[] { old, default })
        {
            // Questions: false, never the new occupant's answer.
            Assert.That(handle.IsAlive || handle.Has<Health>() || handle.Has<Health, Position>(), Is.False);
            Assert.That(handle.TryGet(out Health _) || handle.TryGetRef(out ComponentRef<Health> _), Is.False);
            // Accesses and changes: an InvalidOperationException naming the cause.
            Assert.Throws<InvalidOperationException>(() => handle.Get<Health>());
            Assert.Throws<InvalidOperationException>(() => handle.Read<Health>());
            Assert.Throws<InvalidOperationException>(() => handle.Add<Velocity>());
            Assert.Throws<InvalidOperationException>(() => handle.Set(new Velocity()));
            Assert.Throws<InvalidOperationException>(() => handle.Remove<Health>());
            handle.Destroy();                                              // a no-op
        }

        Assert.That(_world.EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void HasSeveralComponentsAndRemoveChains()
    {
        var e = _world.CreateEntity(new Health(1), new Position(1, 1), new Velocity(1, 1));
        Assert.That(e.Has<Health, Position>() && e.Has<Health, Position, Velocity>(), Is.True);
        Assert.That(e.Has<Health, Name>(), Is.False);                      // no column for Name
        var same = e.Remove<Velocity>().Remove<Health>();
        Assert.That(same, Is.EqualTo(e));
        Assert.That(e.Has<Position, Velocity>() || e.Has<Health, Position>(), Is.False);   // rows without the bits
        Assert.That(e.Has<Position>(), Is.True);
        Assert.That(e.TryGetRef(out ComponentRef<Position> p), Is.True);
        p.Value.X = 5;
        Assert.That(e.Read<Position>().X, Is.EqualTo(5));
    }

    [Test]
    public void LookupByIdAndEnumeration()
    {
        var entities = new List<Entity>();
        for (int i = 0; i < 100; i++) entities.Add(_world.CreateEntity()); // grows past the initial capacity
        entities[10].Destroy();

        Assert.That(_world.TryGetEntity(entities[5].Id, out var found), Is.True);
        Assert.That(found, Is.EqualTo(entities[5]));
        Assert.That(_world.TryGetEntity(entities[10].Id, out _), Is.False);
        Assert.That(_world.TryGetEntity(-1, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => _world.GetEntity(entities[10].Id));

        var seen = new List<Entity>();
        foreach (var e in _world.Entities) seen.Add(e);
        entities.RemoveAt(10);
        Assert.That(seen, Is.EqualTo(entities));
    }

    [Test]
    public void HandlesFromAnotherWorldAreRejected()
    {
        using var other = new World();
        var foreign = other.CreateEntity();
        _world.DestroyEntity(foreign);
        Assert.That(foreign.IsAlive, Is.True);
        Assert.That(default(Entity).IsAlive, Is.False);
    }

    [Test]
    public void DisposeInvalidatesEveryHandle()
    {
        var e = _world.CreateEntity().Set(new Health(1));
        _world.Dispose();
        Assert.That(e.IsAlive, Is.False);
        Assert.Throws<InvalidOperationException>(() => e.Get<Health>());
    }

    [Test]
    public void EnsureEntityCapacityKeepsEveryHandle()
    {
        var before = new List<Entity>();
        for (int i = 0; i < 5; i++) before.Add(_world.CreateEntity().Set(new Health(i)));
        _world.EnsureEntityCapacity(10_000);
        _world.EnsureEntityCapacity(10);                                      // smaller: no-op
        for (int i = 0; i < 5; i++) Assert.That(before[i].Get<Health>().Value, Is.EqualTo(i));
        for (int i = 0; i < 9_000; i++) _world.CreateEntity();
        Assert.That(_world.EntityCount, Is.EqualTo(9_005));
    }

    [Test]
    public void HandlesCompareByIdAndVersion()
    {
        var e = _world.CreateEntity();
        var same = _world.GetEntity(e.Id);
        Assert.That(same, Is.EqualTo(e));
        Assert.That(same == e && !(same != e), Is.True);
        Assert.That(same.GetHashCode(), Is.EqualTo(unchecked(e.Id * 397 ^ e.Version)));   // deterministic
        Assert.That(e.ToString(), Is.EqualTo($"Entity {e.Id}v{e.Version}"));

        e.Destroy();
        var recycled = _world.CreateEntity();
        Assert.That(recycled.Id, Is.EqualTo(e.Id));
        Assert.That(recycled, Is.Not.EqualTo(e));                             // another incarnation
    }
}
