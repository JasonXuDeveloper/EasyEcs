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
    public void CreateEntitiesPlacesABatchAndDestroyAllRemovesMatches()
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
        Assert.That(_world.DestroyAll<Position>(QueryFilter.Create().With<Frozen>()), Is.EqualTo(50));
        Assert.That(_world.EntityCount, Is.EqualTo(51));
        Assert.That(batch[0].IsAlive, Is.False);
        Assert.That(batch[1].IsAlive, Is.True);

        Assert.That(_world.DestroyAll<Position>(), Is.EqualTo(51));
        Assert.That(existing.IsAlive, Is.False);
        Assert.That(_world.EntityCount, Is.Zero);
    }

    [Test]
    public void DestroyEntitiesByIdSkipsStaleIdsAndRepeats()
    {
        var batch = new Entity[4];
        _world.CreateEntities(batch, new Health(1));
        var gone = batch[3].Id;
        batch[3].Destroy();
        var reused = _world.CreateEntity(new Health(2));                     // may take the destroyed slot

        Assert.That(_world.DestroyEntities(batch[0].Id, batch[1].Id, batch[0].Id, gone, default), Is.EqualTo(2));
        Assert.That(batch[0].IsAlive || batch[1].IsAlive, Is.False);
        Assert.That(batch[2].IsAlive && reused.IsAlive, Is.True);             // a stale id never reaches a reused slot
        Assert.That(_world.DestroyEntities(), Is.Zero);
    }

    [Test]
    public void DestroyEntitiesByIdRetiresCleanupAndCascadesOnce()
    {
        var parent = _world.CreateEntity(new Health(1));
        var leased = _world.CreateEntity(new Lease(1));
        var child = _world.CreateEntity(new ChildOf(parent));
        var grandchild = _world.CreateEntity(new ChildOf(child));

        Assert.That(_world.DestroyEntities(parent.Id, leased.Id, child.Id), Is.EqualTo(3));
        Assert.That(parent.IsAlive || child.IsAlive || grandchild.IsAlive, Is.False); // the list and the cascade
        Assert.That(leased.IsAlive && leased.Has<Retired>(), Is.True);                // retired, kept for its cleanup
        Assert.That(_world.DestroyEntities(leased.Id), Is.Zero);                      // retired already
    }

    [Test]
    public void DestroyEntitiesByIdDuringAPassSkipsTheDestroyed()
    {
        var batch = new Entity[10];
        _world.CreateEntities(batch, new Health(1));
        var ids = new EntityId[10];
        for (int i = 0; i < ids.Length; i++) ids[i] = batch[i].Id;
        int visited = 0;
        foreach (var item in _world.Query<Health>())
        {
            if (visited++ == 0) Assert.That(_world.DestroyEntities(ids), Is.EqualTo(10));
        }
        Assert.That(visited, Is.EqualTo(1));
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
            if (visited++ == 0) Assert.That(_world.DestroyAll<Health>(), Is.EqualTo(10));
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

        Assert.That(reused.Id.Index, Is.EqualTo(old.Id.Index));
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
            handle.TryGetRef<Health>(out bool found);
            Assert.That(handle.TryGet(out Health _) || found, Is.False);
            // Accesses and changes: an InvalidOperationException naming the cause.
            Assert.Throws<InvalidOperationException>(() => handle.Get<Health>());
            Assert.Throws<InvalidOperationException>(() => handle.Read<Health>());
            Assert.Throws<InvalidOperationException>(() => handle.GetOrAdd<Velocity>());
            Assert.Throws<InvalidOperationException>(() => handle.Set(new Velocity()));
            Assert.Throws<InvalidOperationException>(() => handle.Remove<Health>());
            Assert.Throws<InvalidOperationException>(() => handle.Clone());
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
        ref var p = ref e.TryGetRef<Position>(out bool has);
        Assert.That(has, Is.True);
        p.X = 5;
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
        Assert.That(_world.TryGetEntity(new EntityId(-1, 1), out _), Is.False);
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
        _world.CreateEntity();                                       // the same id in this world
        Assert.That(_world.TryGetEntity(foreign.Id, out var mine) && mine == foreign, Is.False);
        foreign.Destroy();                                           // destroys it in its own world only
        Assert.That(foreign.IsAlive, Is.False);
        Assert.That(mine.IsAlive, Is.True);
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
    public void ReservingAComponentSetMakesItsCreationsGrowNothing()
    {
        using (var warm = new World()) warm.CreateEntity(new Position(), new Velocity()).Destroy(); // JIT and caches
        var kept = _world.CreateEntity(new Position(1, 2), new Velocity(3, 4));
        _world.Reserve<Position, Velocity>(5000);
        Assert.That(kept.Read<Position>(), Is.EqualTo(new Position(1, 2)));     // its row moved to the grown columns
        long before = Allocations.Baseline();
        for (int i = 0; i < 5000; i++) _world.CreateEntity(new Position(i, 0), new Velocity());
        Assert.That((GC.GetAllocatedBytesForCurrentThread() - before, _world.EntityCount), Is.EqualTo((0L, 5001)));
        var table = Array.Find(_world.DescribeTables(), t => t.Entities == 5001);
        Assert.That(table.Capacity, Is.EqualTo(5056)); // 5,001 rows in whole presence words, not the next power of two
        _world.Reserve<Position, Velocity>(0); // nothing to reserve
        _world.Reserve<Position, Velocity>(-1);
        Assert.That(Array.Find(_world.DescribeTables(), t => t.Entities == 5001).Capacity, Is.EqualTo(5056));
    }

    [Test]
    public void APassGoesOnInTheGrownColumnsAfterAReservation()
    {
        _world.EnsureEntityCapacity(20_000); // only the table grows below
        for (int i = 0; i < 100; i++) _world.CreateEntity(new Position(i, 0));
        int visited = 0;
        foreach (var item in _world.Query<Position>())
        {
            item.Position.Y = 1; // before the reservation: a ref is valid until the next structural change
            if (visited++ == 10) _world.Reserve<Position>(10_000); // the columns move under the pass
        }

        int written = 0;
        foreach (var item in _world.Query<Position>()) written += (int)item.Position.Y;
        Assert.That(written, Is.EqualTo(100));
    }

    [Test]
    public void BulkCreationGrowsToWhatItNeedsAndStillDoubles()
    {
        _world.CreateEntities(new Entity[100], new Health(1));
        var table = Array.Find(_world.DescribeTables(), t => t.Entities == 100);
        Assert.That((table.Capacity, _world.Meta.Length), Is.EqualTo((128, 100))); // exact (rows: whole words)
        _world.CreateEntities(new Entity[30], new Health(2));                     // a small batch: one doubling
        Assert.That((Array.Find(_world.DescribeTables(), t => t.Entities == 130).Capacity, _world.Meta.Length), Is.EqualTo((256, 200)));
    }

    [Test]
    public void AnIdNamesOneIncarnationAndNullNamesNone()
    {
        Assert.That(_world.TryGetEntity(EntityId.Null, out _), Is.False);  // versions start at 1
        var first = _world.CreateEntity(new Health(1));
        Assert.That((first.Id.IsNull, first.Id.Version), Is.EqualTo((false, 1)));
        EntityId id = first;                                                  // a handle converts to its id
        Assert.That(_world.GetEntity(id), Is.EqualTo(first));
        first.Destroy();
        var reused = _world.CreateEntity(new Health(2));
        Assert.That(reused.Id.Index, Is.EqualTo(id.Index));
        Assert.That((_world.IsAlive(id), _world.TryGetEntity(id, out _)), Is.EqualTo((false, false))); // not the new one
        Assert.Throws<InvalidOperationException>(() => _world.GetEntity(id));
    }

    [Test]
    public void ComponentsHoldingIdsKeepUnmanagedColumns()
    {
        var parent = _world.CreateEntity();
        var child = _world.CreateEntity(new ChildOf(parent));
        Assert.That(ComponentInfo.Of(ComponentType<ChildOf>.Bit).Managed, Is.False);
        Assert.That(_world.GetEntity(child.Read<ChildOf>().Parent), Is.EqualTo(parent));
    }

    [Test]
    public void HandlesCompareByIdAndVersion()
    {
        var e = _world.CreateEntity();
        var same = _world.GetEntity(e.Id);
        Assert.That(same, Is.EqualTo(e));
        Assert.That(same == e && !(same != e), Is.True);
        Assert.That(same.GetHashCode(), Is.EqualTo(e.Id.GetHashCode()));   // deterministic
        Assert.That(e.ToString(), Is.EqualTo($"Entity {e.Id.Index}v{e.Id.Version}"));

        e.Destroy();
        var recycled = _world.CreateEntity();
        Assert.That(recycled.Id.Index, Is.EqualTo(e.Id.Index));
        Assert.That(recycled, Is.Not.EqualTo(e));                             // another incarnation
    }

    [Test]
    public void DestroyingWholeTablesFreesEveryEntityAndItsIndexEntries()
    {
        using var world = new World();
        var parent = world.CreateEntity();
        var children = world.Index<ChildOf, EntityId>();
        var spawned = new Entity[300];
        world.CreateEntities(spawned, new ChildOf(parent), new Position(), new Callsign("x"));
        spawned[7].Remove<Position>();                                  // a row lacking a component still goes
        Assert.That(children.Count(parent), Is.EqualTo(300));
        Assert.That(world.DestroyAll<ChildOf>(), Is.EqualTo(300));
        Assert.That(spawned, Has.All.Matches<Entity>(e => !e.IsAlive));
        Assert.That(children.Count(parent), Is.Zero);
        Assert.That(world.EntityCount, Is.EqualTo(1));
        var again = new Entity[300];
        world.CreateEntities(again, new ChildOf(parent), new Position(), new Callsign("y")); // the ids come back
        Assert.That(again[0].Has<Position>() && again[0].Read<Callsign>().Value == "y", Is.True);
        Assert.That(spawned[0].IsAlive, Is.False);                     // old handles stay stale
        Assert.That(children.Count(parent), Is.EqualTo(300));
    }

    [Test]
    public void DestroyingAWholeTableDuringAPassLeavesTombstones()
    {
        using var world = new World();
        world.CreateEntities(new Entity[100], new Position(), new Health(1));
        int visited = 0;
        foreach (var item in world.Query<Health>())
        {
            if (visited++ == 0) Assert.That(world.DestroyAll<Position>(), Is.EqualTo(100));
        }

        Assert.That(visited, Is.EqualTo(1));                            // destroyed rows are skipped
        Assert.That(world.EntityCount, Is.Zero);
        world.CreateEntities(new Entity[10], new Position(), new Health(2));
        Assert.That(world.Count<Health>(), Is.EqualTo(10));
    }

    [Test]
    public void RemovingSeveralComponentsTellsIndexesAndTracksRemovals()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1), new Callsign("x"), new Position(), new Seen());
        var callsigns = world.Index<Callsign, string>();
        Assert.That(callsigns.Count("x"), Is.EqualTo(1));
        world.Update();
        ulong mark = world.ChangeTick;
        e.Remove<Armor, Callsign, Frozen>();                            // Frozen it lacks: ignored
        Assert.That(callsigns.Count("x"), Is.Zero);
        Assert.That(world.Count<Seen>(QueryFilter.Create().Removed<Armor>(mark)), Is.EqualTo(1));
        Assert.That(e.Has<Position>() && !e.Has<Armor>() && !e.Has<Callsign>(), Is.True);
    }

    [Test]
    public void TheDebuggerViewListsComponentValues()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(5), new Armor(2), new Callsign("x"));
        e.Disable<Armor>();
        var proxy = typeof(Entity).GetCustomAttributes(typeof(System.Diagnostics.DebuggerTypeProxyAttribute), false);
        var view = System.Activator.CreateInstance(System.Type.GetType(((System.Diagnostics.DebuggerTypeProxyAttribute)proxy[0]).ProxyTypeName)!, e)!;
        var components = (ComponentValue[])view.GetType().GetProperty("Components")!.GetValue(view)!;
        Assert.That(components, Is.EquivalentTo(new[]
        {
            new ComponentValue(new Health(5), true), new ComponentValue(new Armor(2), false), new ComponentValue(new Callsign("x"), true),
        }));
        e.Destroy();
        Assert.That((ComponentValue[])view.GetType().GetProperty("Components")!.GetValue(view)!, Is.Empty);
    }
}
