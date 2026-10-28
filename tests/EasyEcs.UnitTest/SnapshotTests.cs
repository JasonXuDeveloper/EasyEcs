using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SnapshotTests
{
    [Test]
    public void RestorePutsEntitiesComponentsAndSingletonsBack()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 2 });
        var a = world.CreateEntity(new Position(1, 1), new Velocity(1, 0));
        var b = world.CreateEntity(new Position(2, 2), new Name("b"));
        var c = world.CreateEntity(new Health(3));
        c.Remove<Health>();                       // a presence bit, captured too
        world.AddSingleton<Score>() = new Score(1, 2);
        var score = world.SingletonRef<Score>();

        var snapshot = world.Capture();
        Assert.That(snapshot.EntityCount, Is.EqualTo(3));

        a.Get<Position>() = new Position(9, 9);
        a.Set(new Frozen());
        b.Destroy();
        var later = world.CreateEntity(new Position(5, 5));
        var reused = world.CreateEntity(new Health(7));   // may reuse b's id
        c.Set(new Health(4));
        world.AddSingleton<Clock>();
        score.Value = new Score(5, 5);

        world.Restore(snapshot);

        Assert.That(world.EntityCount, Is.EqualTo(3));
        Assert.That(a.Get<Position>(), Is.EqualTo(new Position(1, 1)));
        Assert.That(a.Has<Frozen>(), Is.False);
        Assert.That(b.IsAlive, Is.True);
        Assert.That(b.Get<Name>().Value, Is.EqualTo("b"));
        Assert.That(c.Has<Health>(), Is.False);
        Assert.That(later.IsAlive, Is.False);
        Assert.That(reused.IsAlive, Is.False);
        Assert.That(score.Value, Is.EqualTo(new Score(1, 2)));
        Assert.That(world.HasSingleton<Clock>(), Is.False);
        Assert.That(world.Count<Position>(), Is.EqualTo(2));
        Assert.That(world.Count<Health>(), Is.Zero);
    }

    [Test]
    public void HandlesMadeAfterTheCaptureNeverMatchLaterEntities()
    {
        using var world = new World();
        var snapshot = world.Capture();
        var after = world.CreateEntity(new Health(1));
        world.Restore(snapshot);

        var fresh = world.CreateEntity(new Health(2));   // reuses the id of `after`
        Assert.That(fresh.Id.Index, Is.EqualTo(after.Id.Index));
        Assert.That(after.IsAlive, Is.False);
        Assert.Throws<InvalidOperationException>(() => after.Get<Health>());
        Assert.That(fresh.Get<Health>().Value, Is.EqualTo(2));
    }

    [Test]
    public void ASnapshotRestoresAnyNumberOfTimesAndTheWorldKeepsWorking()
    {
        using var world = new World();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Position(i, 0), new Velocity(1, 0));
        var snapshot = world.Capture();

        for (int round = 0; round < 3; round++)
        {
            int destroyed = world.DestroyAll<Position>(QueryFilter.Create().With<Velocity>());
            Assert.That(destroyed, Is.EqualTo(100));
            for (int i = 0; i < 50; i++) world.CreateEntity(new Health(i));
            world.Restore(snapshot);

            float sum = 0;
            foreach (var item in world.Query<Position, Velocity>()) sum += item.Component1.X;
            Assert.That(sum, Is.EqualTo(4950));
            Assert.That(world.Count<Health>(), Is.Zero);
            world.CreateEntity(new Position(0, 0)).Destroy();   // ids and tables still consistent
        }
    }

    [Test]
    public void SnapshotsBelongToTheirWorldAndNeverToAPass()
    {
        using var world = new World();
        using var other = new World();
        world.CreateEntity(new Health(1));
        Assert.Throws<ArgumentException>(() => other.Restore(world.Capture()));
        foreach (var _ in world.Query<Health>())
            Assert.Throws<InvalidOperationException>(() => world.Capture());
    }

    [Test]
    public void CapturingIntoASnapshotReusesItAndAllocatesNothing()
    {
        using var world = new World();
        var entities = new Entity[100];
        world.CreateEntities(entities, new Health(1), new Position(0, 0));
        world.AddSingleton<Released>();
        var snapshot = world.Capture();
        for (int round = 0; round < 3; round++)                                // warm-up
            Assert.That(world.Capture(snapshot), Is.SameAs(snapshot));

        long before = Allocations.Baseline();
        for (int round = 0; round < 50; round++)
        {
            entities[round].Get<Health>().Value = round + 10;
            world.Capture(snapshot);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        entities[0].Destroy();
        world.GetSingleton<Released>().Count = 5;
        world.Restore(snapshot);                                                // the last capture
        Assert.That((entities[0].IsAlive, entities[49].Get<Health>().Value, world.GetSingleton<Released>().Count),
            Is.EqualTo((true, 59, 0)));

        world.DestroyAll<Health>();                                        // fewer rows: the copy shrinks to them
        world.Capture(snapshot);
        world.CreateEntity(new Health(3));
        world.Restore(snapshot);
        Assert.That(world.EntityCount, Is.Zero);
        using var other = new World();
        Assert.Throws<ArgumentException>(() => other.Capture(snapshot));
    }

    // Incremental captures and restores (a snapshot in sync with the world copies only the rows written since): each
    // checked against what the world held at the capture, read entity by entity.

    [EcsSystem]
    public ref struct SnapshotWear
    {
        [ForEach]
        private static void Apply(ref Armor armor, in Position position) => armor.Value += position.X > 6000 ? 1 : 0;
    }

    [EcsSystem]
    public ref struct SnapshotWearParallel
    {
        [ForEach(Parallel = true)]
        private static void Apply(ref Shield shield, in Position position) => shield.Value += position.X < 3000 ? 2 : 0;
    }

    private record struct Row(bool Alive, Position Position, int Armor, int Shield, bool HasShield, int[] Items, string Name);

    private static Row[] Read(Entity[] entities)
    {
        var rows = new Row[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i];
            if (!e.IsAlive) continue;
            rows[i] = new Row(true, e.Read<Position>(), e.Read<Armor>().Value, e.Has<Shield>() ? e.Read<Shield>().Value : -1,
                e.Has<Shield>(), e.Read<DynamicBuffer<int>>().AsReadOnlySpan().ToArray(), e.Has<Name>() ? e.Read<Name>().Value : null);
        }

        return rows;
    }

    private static void AssertSame(Row[] expected, Row[] actual, string when)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            var (a, b) = (expected[i], actual[i]);
            Assert.That((b.Alive, b.Position, b.Armor, b.Shield, b.HasShield, b.Name), Is.EqualTo((a.Alive, a.Position, a.Armor, a.Shield, a.HasShield, a.Name)),
                $"{when}: entity {i}");
            Assert.That(b.Items, Is.EqualTo(a.Items), $"{when}: entity {i}'s buffer");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void IncrementalCapturesAndRestoresMatchTheWorldAtTheCapture(bool structural)
    {
        // Two blocks of 4,096 rows and a partial third; tracked (Armor, Shield, the buffer) and untracked (Position, Name) columns.
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        world.AddSystem<SnapshotWear>();
        world.AddSystem<SnapshotWearParallel>();
        var entities = new Entity[9_000];
        world.Reserve<Position, Armor, DynamicBuffer<int>>(entities.Length);
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Position(i, 0), new Armor(i), new DynamicBuffer<int>());
            entities[i].Get<DynamicBuffer<int>>().Add(i);
            if (i % 3 == 0) entities[i].Set(new Shield(i));
        }

        var random = new Random(7);
        var a = world.Capture();
        var b = world.Capture();
        var atA = Read(entities);
        var atB = atA;
        for (int round = 0; round < 60; round++)
        {
            int writes = random.Next(1, 40);
            for (int w = 0; w < writes; w++)
            {
                var e = entities[random.Next(entities.Length)];
                if (!e.IsAlive) continue;
                switch (random.Next(6))
                {
                    case 0: e.Get<Armor>().Value += 100; break;                                  // tracked
                    case 1: e.Get<Position>().Y += 1; break;                                     // untracked
                    case 2: e.Get<DynamicBuffer<int>>().Add(round); break;                       // a buffer grows
                    case 3: e.Get<DynamicBuffer<int>>().AsSpan()[0] = -round; break;             // a buffer written in place
                    case 4: if (e.Has<Shield>()) e.Get<Shield>().Value -= 1; break;
                    case 5: e.Set(new Name("n" + round)); break;                                 // a row moves (structural)
                }
            }

            if (structural && round % 7 == 3)
            {
                var victim = entities[random.Next(entities.Length)];
                if (victim.IsAlive) victim.Destroy();
                var stunned = entities[random.Next(entities.Length)];
                if (stunned.IsAlive && stunned.Has<Shield>()) stunned.Remove<Shield>();       // a presence bit
            }

            if (round % 4 == 1) world.Update();                                                 // vector and parallel writes
            switch (random.Next(5))
            {
                case 0: world.Capture(a); atA = Read(entities); break;
                case 1: world.Capture(b); atB = Read(entities); break;
                case 2: world.Restore(a); AssertSame(atA, Read(entities), $"round {round}, restored a"); break;
                case 3: world.Restore(b); AssertSame(atB, Read(entities), $"round {round}, restored b"); break;
                case 4: world.Restore(a); world.Restore(a); AssertSame(atA, Read(entities), $"round {round}, restored a twice"); break;
            }
        }

        world.Restore(b);
        AssertSame(atB, Read(entities), "the end");
    }

    [Test]
    public void AnInSyncRestoreCountsTheRowsItChangesAsWritten()
    {
        using var world = new World();
        world.AddSystem<ChangedTests.ArmorWatch>();
        var entities = new Entity[10];
        world.CreateEntities(entities, new Armor(1), new Seen());
        world.Update();                                                    // first run: every row is new
        var snapshot = world.Capture();
        world.Update();
        entities[3].Get<Armor>().Value = 5;
        world.Restore(snapshot);                                           // in sync: copies back row 3 only
        Assert.That(entities[3].Read<Armor>().Value, Is.EqualTo(1));
        world.Update();
        Assert.That((entities[0].Read<Seen>().Count, entities[3].Read<Seen>().Count), Is.EqualTo((1, 2))); // Seen came back to 1

        entities[3].Destroy();                                             // a structural change: the restore rewrites the
        world.Restore(snapshot);                                           // tables whole, every row counts as written
        world.Update();
        Assert.That((entities[0].Read<Seen>().Count, entities[3].Read<Seen>().Count), Is.EqualTo((2, 2)));
    }

    [Test]
    public void ARingOfSnapshotsStaysInSyncThroughARollback()
    {
        using var world = new World();
        var entities = new Entity[5_000];
        world.CreateEntities(entities, new Armor(0), new Position(0, 0));
        var ring = new WorldSnapshot[4];
        var at = new int[4][];
        int[] Armors()
        {
            var armors = new int[entities.Length];
            for (int i = 0; i < armors.Length; i++) armors[i] = entities[i].Read<Armor>().Value;
            return armors;
        }

        for (int tick = 0; tick < 40; tick++)
        {
            for (int i = 0; i < 20; i++) entities[(tick * 131 + i * 17) % entities.Length].Get<Armor>().Value = tick * 100 + i;
            int slot = tick & 3;
            ring[slot] = world.Capture(ring[slot]);
            at[slot] = Armors();
            if (tick % 5 != 4) continue;
            int back = (tick - 2) & 3;                                     // a late input: roll back two ticks, replay
            world.Restore(ring[back]);
            Assert.That(Armors(), Is.EqualTo(at[back]), $"tick {tick}");
            for (int replay = tick - 1; replay <= tick; replay++)
            {
                entities[replay % entities.Length].Get<Armor>().Value = -replay;
                int s = replay & 3;
                world.Capture(ring[s]);
                at[s] = Armors();
            }
        }

        for (int slot = 0; slot < 4; slot++)
        {
            world.Restore(ring[slot]);
            Assert.That(Armors(), Is.EqualTo(at[slot]), $"slot {slot}");
        }
    }

    [Test]
    public void ALoadBetweenKeepsSnapshotsRight()
    {
        using var world = new World();
        var e = world.CreateEntity(new Armor(1), new Position(1, 1));
        var save = new Nino.Core.NinoArrayBufferWriter();
        world.Save(save);
        e.Get<Armor>().Value = 2;
        var snapshot = world.Capture();                                    // Armor 2
        world.Load(save.WrittenSpan);                                      // Armor 1, its tick cleared: older than the capture
        Assert.That(e.Read<Armor>().Value, Is.EqualTo(1));
        world.Restore(snapshot);
        Assert.That(e.Read<Armor>().Value, Is.EqualTo(2));
        world.Load(save.WrittenSpan);
        world.Capture(snapshot);                                           // after a load: copied whole again
        e.Get<Armor>().Value = 3;
        world.Restore(snapshot);
        Assert.That(e.Read<Armor>().Value, Is.EqualTo(1));
    }

    [Test]
    public void ARollbackInSyncAllocatesNothing()
    {
        using var world = new World();
        world.AddSystem<SnapshotWearParallel>();
        var entities = new Entity[20_000];
        world.CreateEntities(entities, new Position(0, 0), new Shield(1), new DynamicBuffer<int>());
        foreach (var e in entities) e.Get<DynamicBuffer<int>>().Add(1);
        var snapshot = world.Capture();
        void Tick(int round)
        {
            world.Capture(snapshot);
            for (int i = 0; i < 300; i++) entities[i * 61 % entities.Length].Get<DynamicBuffer<int>>().AsSpan()[0] = round + i;
            world.Update();
            world.Restore(snapshot);
        }

        for (int round = 0; round < 4; round++) Tick(round);              // rehearsals: the first writes copy the shared lists
        long before = Allocations.Baseline();
        for (int round = 0; round < 20; round++) Tick(round);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(entities[0].Read<DynamicBuffer<int>>()[0], Is.EqualTo(1));
        Assert.That(entities[0].Read<Shield>().Value, Is.EqualTo(1));
    }
}
