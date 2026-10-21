using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nino.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

[NinoType]
public partial struct Title : IComponent   // managed: written by its Nino formatter
{
    public string Text;
}

public struct Padded : IComponent { public byte Flag; public long Value; } // 7 bytes of padding after Flag

public struct Grip { private EntityId _holder; public Grip(EntityId holder) => _holder = holder; public EntityId Holder => _holder; }
public struct Held : IComponent { public Grip Grip; }                 // an id two levels down, in a private field

// One component in three builds: Stats as it was, renamed to Vitals with a field appended (both [NinoType]s, written by
// their formatter), and a raw one that took over the name. A test registers one of them at a time, as a deploy would.
[NinoType(format: NinoFormat.VersionTolerant)]
public partial struct Stats : IComponent { public int Hp; }

[NinoType(format: NinoFormat.VersionTolerant), NinoFormerName("EasyEcs.UnitTest.Stats")]
public partial struct Vitals : IComponent { public int Hp; public int Mana; }

[NinoFormerName("EasyEcs.UnitTest.Stats")]
public struct RawStats : IComponent { public int Hp; }

// A restart's types: configuration the new version brings, a world-local cache Nino cannot write, persisted balances.
[NinoType]
public partial struct Config : ISingletonComponent { public int Rate; }
public record struct Cache(object Handle) : ISingletonComponent;     // no formatter: a save that writes it throws
public record struct Balance(int Gold) : IComponent, ITrackChanges;
public record struct Session(object Socket) : IComponent;             // runtime only, no formatter

public class SerializationTests
{
    private static byte[] Save(World world)
    {
        var output = new NinoArrayBufferWriter();
        world.Save(output);
        return output.WrittenSpan.ToArray();
    }

    [Test]
    public void ALoadedWorldHasTheSavedEntitiesComponentsAndSingletons()
    {
        byte[] bytes;
        EntityId hero, crate, gone;
        using (var world = new World())
        {
            hero = world.CreateEntity(new Position(1, 2), new Armor(5), new Title { Text = "hero" });
            gone = world.CreateEntity(new Health(1));
            crate = world.CreateEntity(new Position(3, 4), new Health(9));
            var entity = world.GetEntity(crate);
            entity.Disable<Health>();                                    // kept value, absent
            entity.Set(new DynamicBuffer<DynamicBufferTests.Loot>());
            entity.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(7, 2));
            world.GetEntity(gone).Destroy();                             // a free id, version bumped
            world.AddSingleton<Score>() = new Score(3, 4);
            bytes = Save(world);
        }

        using var loaded = new World();
        loaded.CreateEntity(new Velocity(1, 1));                         // replaced by the load
        loaded.Load(bytes);

        Assert.That(loaded.EntityCount, Is.EqualTo(2));
        var h = loaded.GetEntity(hero);                                   // same id and version
        Assert.That(h.Read<Position>(), Is.EqualTo(new Position(1, 2)));
        Assert.That(h.Read<Armor>(), Is.EqualTo(new Armor(5)));
        Assert.That(h.Get<Title>().Text, Is.EqualTo("hero"));
        var c = loaded.GetEntity(crate);
        Assert.That(c.Has<Health>(), Is.False);
        Assert.That(c.IsDisabled<Health>(), Is.True);
        Assert.That(c.Enable<Health>() && c.Read<Health>().Value == 9, Is.True);
        Assert.That(c.Read<DynamicBuffer<DynamicBufferTests.Loot>>()[0], Is.EqualTo(new DynamicBufferTests.Loot(7, 2)));
        Assert.That(loaded.IsAlive(gone), Is.False);
        Assert.That(loaded.GetSingleton<Score>(), Is.EqualTo(new Score(3, 4)));
        Assert.That(loaded.Count<Velocity>(), Is.Zero);
        Assert.That(loaded.CreateEntity().Index, Is.EqualTo(gone.Index)); // the free list is the saved one
    }

    [Test]
    public void IndexesHierarchiesAndGroupsFollowTheLoadedValues()
    {
        byte[] bytes;
        EntityId parent;
        using (var world = new World())
        {
            parent = world.CreateEntity(new Health(1));
            world.CreateEntity(new ChildOf(parent), new Zone(2), new Health(5));
            world.CreateEntity(new ChildOf(parent), new Zone(3), new Health(7));
            bytes = Save(world);
        }

        using var loaded = new World();
        loaded.GroupBy<Zone>();
        loaded.Load(bytes);

        int sum = 0;
        foreach (var item in loaded.Query<Health>(QueryFilter.Create().InGroup<Zone>(3))) sum += item.Component1.Value;
        Assert.That(sum, Is.EqualTo(7));
        Assert.That(loaded.Index<ChildOf, EntityId>().Count(parent), Is.EqualTo(2));
        loaded.GetEntity(parent).Destroy();                              // the cascade works on loaded entities
        Assert.That(loaded.EntityCount, Is.Zero);
    }

    [Test]
    public void SavingAgainAllocatesNothing()
    {
        using var world = new World();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Position(i, i), new Health(i));
        var output = new NinoArrayBufferWriter();
        world.Save(output);
        output.ResetWrittenCount();
        world.Save(output);

        output.ResetWrittenCount();
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.Save(output);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void ALoadCanLeaveTheChangeTicksAlone()
    {
        byte[] saved;
        using (var world = new World())
        {
            for (int i = 0; i < 200; i++) world.CreateEntity(new Armor(i), new Seen());
            for (int i = 0; i < 100; i++) world.CreateEntity(new Seen()).Set(new Armor(1)).Remove<Armor>(); // removed before the save
            saved = Save(world);
        }

        using var restarted = new World();                                    // the next process: written ticks before the load
        restarted.AddSystem<ChangedTests.ArmorWatch>();
        for (int i = 0; i < 300; i++) restarted.CreateEntity(new Armor(i), new Seen());
        restarted.Update();
        restarted.Update();
        restarted.Load(saved, markWritten: false);
        restarted.AddSystem<RemovedTests.ArmorLoss>();                         // added after the load: its first run
        restarted.Update();
        Assert.That(SeenCount(restarted), Is.Zero);                            // nothing counts as written or removed
        Assert.That(restarted.Count<Armor>(QueryFilter.Create().Changed<Armor>(1)), Is.Zero);

        Entity written = default;
        foreach (var item in restarted.Query<Seen>())
            if (item.Entity.TryGet(out Armor armor) && armor.Value == 7) written = item.Entity;
        written.Set(new Armor(8));
        restarted.Update();
        Assert.That(SeenCount(restarted), Is.EqualTo(1));                      // a write after the load is seen

        restarted.Load(saved);                                                 // the default: every loaded row is written
        restarted.Update();
        Assert.That(SeenCount(restarted), Is.EqualTo(300));                    // 200 changed, 100 removed

        static int SeenCount(World world)
        {
            int sum = 0;
            foreach (var item in world.Query<Seen>()) sum += item.Component1.Count;
            return sum;
        }
    }

    [Test]
    public void LoadingAgainReusesItsRowsAndDropsWhatTheBytesLack()
    {
        byte[] full, empty;
        using (var world = new World())
        {
            for (int i = 0; i < 1000; i++) world.CreateEntity(new Position(i, i), new Health(i));
            for (int i = 0; i < 100; i++)                                // lists too: a load refills the last load's
            {
                var bag = world.CreateEntity(new DynamicBuffer<DynamicBufferTests.Loot>());
                for (int k = 0; k <= i % 5; k++) bag.Get<DynamicBuffer<DynamicBufferTests.Loot>>().Add(new DynamicBufferTests.Loot(i, k));
            }

            world.AddSingleton<Score>() = new Score(1, 2);
            full = Save(world);
        }

        using (var world = new World()) empty = Save(world);

        using var loaded = new World();
        loaded.Load(full);
        loaded.Load(empty);                                               // nothing of the first load stays
        Assert.That((loaded.EntityCount, loaded.HasSingleton<Score>()), Is.EqualTo((0, false)));
        loaded.Load(full);
        loaded.Load(full);                                                // warm: first calls compile and cache

        long before = GC.GetAllocatedBytesForCurrentThread();
        loaded.Load(full);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);  // types are looked up by id
        int sum = 0;
        foreach (var item in loaded.Query<Health>()) sum += item.Component1.Value;
        int loot = 0;
        foreach (var item in loaded.Read<DynamicBuffer<DynamicBufferTests.Loot>>()) loot += item.Component1.Count;
        Assert.That((loaded.EntityCount, sum, loot, loaded.GetSingleton<Score>()), Is.EqualTo((1100, 999 * 1000 / 2, 300, new Score(1, 2))));
    }

    [Test]
    public void AMovedGroupGetsNewIdsAndItsReferencesFollow()
    {
        byte[] bytes;
        EntityId player, sword;
        var output = new NinoArrayBufferWriter();
        using (var world = new World())
        {
            var guild = world.CreateEntity(new Health(100));              // stays behind
            player = world.CreateEntity(new Health(10), new Follows(guild));
            sword = world.CreateEntity(new ChildOf(player), new Follows(player), new Held { Grip = new Grip(player) });
            world.GetEntity(player).AddRelation(new MemberOf(sword));
            world.GetEntity(player).AddRelation(new MemberOf(guild));
            world.SaveEntities(output, [sword, player, EntityId.Null]);   // order kept, an empty id skipped
            bytes = output.WrittenSpan.ToArray();
        }

        using var arrival = new World();
        for (int i = 0; i < 5; i++) arrival.CreateEntity(new Position(i, i)); // ids taken here: the group gets others
        var moved = new Entity[2];
        Assert.That(arrival.LoadEntities(bytes, moved), Is.EqualTo(2));
        var (s, p) = (moved[0], moved[1]);
        Assert.That((s.Id, p.Id), Is.Not.EqualTo((sword, player)));
        Assert.That(s.Read<ChildOf>().Parent, Is.EqualTo(p.Id));         // inside the group: the new entity
        Assert.That(s.Read<Follows>().Target, Is.EqualTo(p.Id));
        Assert.That(s.Get<Held>().Grip.Holder, Is.EqualTo(p.Id));
        Assert.That(p.Read<Follows>().Target, Is.EqualTo(EntityId.Null)); // the guild did not come
        ref readonly var parties = ref p.Read<DynamicBuffer<MemberOf>>();                // buffer elements too
        Assert.That((parties[0], parties[1]), Is.EqualTo((new MemberOf(s), new MemberOf(EntityId.Null))));
        Assert.That(arrival.Index<Follows, EntityId>().Count(p), Is.EqualTo(1)); // indexes learn the new values
        Assert.That(arrival.Index<ChildOf, EntityId>().Count(p), Is.EqualTo(1));
        p.Destroy();                                                      // the cascade reaches the moved child
        Assert.That((s.IsAlive, arrival.EntityCount), Is.EqualTo((false, 5)));
    }

    [Test]
    public void AMovedGroupLandsInItsZonesAndItsIndexes()
    {
        var output = new NinoArrayBufferWriter();
        var group = new EntityId[300];
        using (var world = new World())
        {
            for (int i = 0; i < group.Length; i++) group[i] = world.CreateEntity(new Zone(i % 3), new Health(i));
            for (int i = 0; i < group.Length; i++) world.GetEntity(group[i]).Set(new Follows(group[i % 7]));
            world.SaveEntities(output, group);
        }

        using var arrival = new World();
        arrival.GroupBy<Zone>();
        arrival.Index<Follows, EntityId>();
        var moved = new Entity[group.Length];
        Assert.That(arrival.LoadEntities(output.WrittenSpan, moved), Is.EqualTo(group.Length));
        for (int zone = 0; zone < 3; zone++)
            Assert.That(arrival.Count<Health>(QueryFilter.Create().InGroup<Zone>(zone)), Is.EqualTo(100)); // every row moved, none skipped
        for (int i = 0; i < moved.Length; i++)
            Assert.That((moved[i].Read<Zone>().Id, moved[i].Read<Health>().Value), Is.EqualTo((i % 3, i)));
        Assert.That(arrival.Index<Follows, EntityId>().Count(moved[0].Id), Is.EqualTo(43)); // i = 0, 7, ..., 294
        Assert.That(moved[294].Read<Follows>().Target, Is.EqualTo(moved[0].Id));
    }

    [Test]
    public void AChildWhoseParentStayedIsARootAfterTheMove()
    {
        var output = new NinoArrayBufferWriter();
        using var world = new World();
        var parent = world.CreateEntity(new Health(1));
        var child = world.CreateEntity(new ChildOf(parent), new Health(2));
        world.SaveEntities(output, [child]);
        var moved = new Entity[1];
        world.LoadEntities(output.WrittenSpan, moved);                    // a copy into the same world
        Assert.That((moved[0].Has<ChildOf>(), moved[0].Read<Health>().Value, world.EntityCount), Is.EqualTo((false, 2, 3)));
    }

    [Test]
    public void EqualWorldsSaveEqualBytesWhateverTheirPadding()
    {
        byte[] Saved(byte garbage)
        {
            using var world = new World();
            var e = world.CreateEntity(new Padded { Flag = 1, Value = 2 });
            ref var value = ref e.Get<Padded>();
            Unsafe.Add(ref Unsafe.As<Padded, byte>(ref value), 1) = garbage;   // a padding byte after Flag
            return Save(world);
        }

        Assert.That(Saved(0xAB), Is.EqualTo(Saved(0)));                       // padding is not saved as it lies in memory
    }

    [Test]
    public void ADamagedSaveIsRefusedAndTheWorldStaysAsItWas()
    {
        byte[] bytes;
        using (var world = new World())
        {
            world.CreateEntity(new Position(1, 2), new Title { Text = "kept" });
            world.CreateEntity(new Health(3)).Set(new DynamicBuffer<DynamicBufferTests.Loot>());
            world.AddSingleton<Score>() = new Score(1, 1);
            bytes = Save(world);
        }

        using var loaded = new World();
        var mine = loaded.CreateEntity(new Velocity(5, 5));
        for (int length = 0; length < bytes.Length; length++)          // cut anywhere
            Assert.Throws<InvalidDataException>(() => loaded.Load(bytes.AsSpan(0, length)), $"cut at {length}");
        Assert.That((loaded.EntityCount, mine.Read<Velocity>()), Is.EqualTo((1, new Velocity(5, 5))));

        var count = bytes.AsSpan().ToArray();
        BitConverter.TryWriteBytes(count.AsSpan(8), int.MaxValue);       // a type count no save could hold
        Assert.Throws<InvalidDataException>(() => loaded.Load(count));
        loaded.Load(bytes);
        Assert.That(loaded.EntityCount, Is.EqualTo(2));
    }

    [Test]
    public void ASetSavesItsComponentsOnly()
    {
        var output = new NinoArrayBufferWriter();
        Entity[] moved = new Entity[2];
        using (var world = new World())
        {
            var actor = world.CreateEntity(new Position(1, 2), new Health(3), new Name("runtime"));   // Name has no formatter
            actor.Set(new Frozen()).Set(new DynamicBuffer<DynamicBufferTests.Loot>());
            actor.Disable<Health>();                                       // kept value, absent
            var bare = world.CreateEntity(new Name("runtime only"));
            Assert.Catch(() => world.SaveEntities(output, [actor.Id]));   // every column: Name cannot be written

            output.ResetWrittenCount();
            var stored = ComponentSet.Create().With<Position>().With(typeof(Health)).With<Frozen>();
            world.SaveEntitiesWith(output, [actor.Id, bare.Id], in stored);
        }

        using var arrival = new World();
        Assert.That(arrival.LoadEntities(output.WrittenSpan, moved), Is.EqualTo(2));
        var (a, b) = (moved[0], moved[1]);
        Assert.That((a.Read<Position>(), a.Has<Frozen>(), a.Has<Name>(), a.Has<DynamicBuffer<DynamicBufferTests.Loot>>()),
            Is.EqualTo((new Position(1, 2), true, false, false)));
        Assert.That((a.IsDisabled<Health>(), a.Enable<Health>(), a.Read<Health>()), Is.EqualTo((true, true, new Health(3))));
        Assert.That((b.IsAlive, b.Has<Name>()), Is.EqualTo((true, false)));      // in the group, with none of the set
    }

    [Test]
    public void ASetIsBuiltFromTypesOrTypeArguments()
    {
        var set = ComponentSet.Create().With(typeof(Position)).With<Armor>();
        Assert.That((set.Contains<Position>(), set.Contains<Armor>(), set.Contains<Health>()), Is.EqualTo((true, true, false)));
        Assert.That(ComponentSet.Create().Contains<Position>(), Is.False);
        Assert.Throws<ArgumentException>(() => ComponentSet.Create().With(typeof(string)));   // not a component type
    }

    [Test]
    public void SavingOneEntityOfALargeWorldAgainAllocatesNothing()
    {
        using var world = new World();
        world.CreateEntities(new Entity[10_000], new Position(), new Health(1));
        var actor = world.CreateEntity(new Position(1, 1), new Health(1), new Armor(1));
        var stored = ComponentSet.Create().With<Position>().With<Health>().With<Armor>();
        var output = new NinoArrayBufferWriter();
        for (int i = 0; i < 3; i++)
        {
            output.ResetWrittenCount();
            world.SaveEntitiesWith(output, [actor.Id], in stored);
        }

        output.ResetWrittenCount();
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.SaveEntitiesWith(output, [actor.Id], in stored);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void ARenamedTypeWithAnAppendedFieldLoadsItsOldRows()
    {
        byte[] bytes;
        try
        {
            SavedTypes.Forget(type => type == typeof(Vitals) || type == typeof(RawStats)); // the build that has Stats
            using (var world = new World())
            {
                world.CreateEntity(new Stats { Hp = 7 }, new Position(1, 1));
                bytes = Save(world);
            }

            SavedTypes.Forget(type => type == typeof(Stats));                                // the build that renamed it
            SavedTypes.Component<Vitals>();
            using var loaded = new World();
            loaded.Load(bytes);
            foreach (var item in loaded.Query<Vitals, Position>())
                Assert.That((item.Component1.Hp, item.Component1.Mana, item.Component2), Is.EqualTo((7, 0, new Position(1, 1))));
            Assert.That(loaded.Count<Vitals>(), Is.EqualTo(1));

            SavedTypes.Forget(type => type == typeof(Vitals));                               // a raw type of that name
            SavedTypes.Component<RawStats>();
            var refused = Assert.Throws<InvalidDataException>(() => loaded.Load(bytes));
            Assert.That(refused!.Message, Does.Contain("Nino formatter"));
        }
        finally
        {
            SavedTypes.Component<Stats>();
            SavedTypes.Component<Vitals>();
            SavedTypes.Component<RawStats>();
        }
    }

    [Test]
    public void TypesSharingAnIdAreRefused()
    {
        byte[] bytes;
        using (var world = new World())
        {
            world.CreateEntity(new Stats { Hp = 1 });
            bytes = Save(world);
        }

        using var loaded = new World();
        var refused = Assert.Throws<InvalidDataException>(() => loaded.Load(bytes)); // Stats, Vitals and RawStats share it
        Assert.That(refused!.Message, Does.Contain("NinoFormerName"));
    }

    [Test]
    public void BytesThatAreNotASaveAreRefused()
    {
        using var world = new World();
        Assert.Throws<InvalidDataException>(() => world.Load(new byte[16]));
    }

    private static readonly ComponentSet Runtime = ComponentSet.Create().WithSingleton<Config>().WithSingleton<Cache>().With<Session>();
    private static readonly ComponentSet NewConfig = ComponentSet.Create().WithSingleton<Config>();

    private static byte[] OldProcess()
    {
        using var world = new World();
        world.AddSingleton<Config>() = new Config { Rate = 1 };
        world.AddSingleton<Cache>() = new Cache(new object());
        world.AddSingleton<Score>() = new Score(1, 2);
        for (int i = 0; i < 10; i++) world.CreateEntity(new Balance(i), new Session(new object()));
        world.CreateEntity(new Session(new object()));                     // only runtime state: written empty
        Assert.Catch(() => world.Save(new NinoArrayBufferWriter()));         // the cache and the sessions cannot be written
        var output = new NinoArrayBufferWriter();
        world.SaveWithout(output, in Runtime);                                     // never touched: no formatter needed
        return output.WrittenSpan.ToArray();
    }

    [Test]
    public void ARestartLeavesTheRuntimeTypesOutAndKeepsTheNewConfiguration()
    {
        var bytes = OldProcess();
        using var resumed = new World();                                    // the new process
        resumed.AddSingleton<Config>() = new Config { Rate = 2 };
        var config = resumed.SingletonRef<Config>();
        resumed.LoadKeeping(bytes, in NewConfig);

        Assert.That((resumed.GetSingleton<Config>().Rate, config.Value.Rate), Is.EqualTo((2, 2)));   // the same box
        config.Value.Rate = 3;
        Assert.That(resumed.GetSingleton<Config>().Rate, Is.EqualTo(3));
        Assert.That((resumed.HasSingleton<Cache>(), resumed.GetSingleton<Score>()), Is.EqualTo((false, new Score(1, 2))));
        Assert.That((resumed.EntityCount, resumed.Count<Balance>(), resumed.Count<Session>()), Is.EqualTo((11, 10, 0)));
        int gold = 0;
        foreach (var item in resumed.Read<Balance>()) gold += item.Component1.Gold;
        Assert.That(gold, Is.EqualTo(45));

        resumed.Load(bytes);                                                // without keep: the save lacks Config
        Assert.That(resumed.HasSingleton<Config>(), Is.False);
    }

    [Test]
    public void AKeptSingletonIsLeftAsTheWorldHasIt()
    {
        byte[] withConfig, without;
        using (var world = new World())
        {
            world.AddSingleton<Config>() = new Config { Rate = 1 };
            world.CreateEntity(new Balance(5));
            withConfig = Save(world);
            world.RemoveSingleton<Config>();
            without = Save(world);
        }

        using var loaded = new World();
        loaded.AddSingleton<Config>() = new Config { Rate = 7 };
        var config = loaded.SingletonRef<Config>();
        loaded.LoadKeeping(without, in NewConfig);                                 // the save lacks it: not removed
        Assert.That(loaded.GetSingleton<Config>().Rate, Is.EqualTo(7));
        loaded.LoadKeeping(withConfig, in NewConfig);                              // the save has it: its value is skipped
        Assert.That((loaded.GetSingleton<Config>().Rate, config.Value.Rate), Is.EqualTo((7, 7)));

        using var bare = new World();                                       // a kept singleton the world lacks stays absent
        bare.LoadKeeping(withConfig, in NewConfig);
        Assert.That((bare.HasSingleton<Config>(), bare.EntityCount), Is.EqualTo((false, 1)));
        bare.Load(withConfig);
        Assert.That(bare.GetSingleton<Config>().Rate, Is.EqualTo(1));
        bare.LoadKeeping(withConfig, in NewConfig);                                // kept as the last load left it
        Assert.That(bare.GetSingleton<Config>().Rate, Is.EqualTo(1));
    }

    [Test]
    public void MarkWrittenAppliesToTheLoadedRowsOnly()
    {
        var bytes = OldProcess();
        using var resumed = new World();
        resumed.AddSingleton<Config>() = new Config { Rate = 2 };
        resumed.LoadKeeping(bytes, in NewConfig, markWritten: false);
        Assert.That(resumed.Count<Balance>(QueryFilter.Create().Changed<Balance>(1)), Is.Zero);
        Assert.That(resumed.GetSingleton<Config>().Rate, Is.EqualTo(2));
        resumed.LoadKeeping(bytes, in NewConfig);                                  // the default: loaded rows are written
        Assert.That(resumed.Count<Balance>(QueryFilter.Create().Changed<Balance>(1)), Is.EqualTo(10));
        Assert.That(resumed.GetSingleton<Config>().Rate, Is.EqualTo(2));
    }

    [Test]
    public void TheCallsOfEarlierVersionsBindAsBefore()
    {
        var bytes = OldProcess();
        using var resumed = new World();
        resumed.AddSingleton<Config>() = new Config { Rate = 2 };
        resumed.Load(bytes, default);                                       // markWritten: false, not an empty set
        Assert.That(resumed.Count<Balance>(QueryFilter.Create().Changed<Balance>(1)), Is.Zero);
        Assert.That(resumed.HasSingleton<Config>(), Is.False);              // nothing kept: the save has none
        resumed.Load(bytes, false);
        Assert.That(resumed.Count<Balance>(QueryFilter.Create().Changed<Balance>(1)), Is.Zero);
        resumed.Load(bytes);
        Assert.That(resumed.Count<Balance>(QueryFilter.Create().Changed<Balance>(1)), Is.EqualTo(10));
    }

    [Test]
    public void ASetOfTheWrongKindIsRefused()
    {
        using var world = new World();
        world.CreateEntity(new Balance(1));
        var bytes = Save(world);
        Assert.Throws<ArgumentException>(() => world.LoadKeeping(bytes, ComponentSet.Create().With<Balance>()));   // entities are replaced
        Assert.Throws<ArgumentException>(() => world.SaveEntitiesWith(new NinoArrayBufferWriter(), [], in NewConfig)); // no singletons there
        Assert.That(world.EntityCount, Is.EqualTo(1));

        var set = ComponentSet.Create().With(typeof(Config)).With(typeof(Balance));
        Assert.That((set.ContainsSingleton<Config>(), set.Contains<Balance>(), set.ContainsSingleton<Cache>(), set.Contains<Session>()),
            Is.EqualTo((true, true, false, false)));
    }

    [Test]
    public void SavingWithoutAndLoadingKeepingAgainAllocateNothing()
    {
        using var world = new World();
        world.AddSingleton<Config>() = new Config { Rate = 1 };
        world.AddSingleton<Cache>() = new Cache(new object());
        world.AddSingleton<Score>() = new Score(1, 2);
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Balance(i), new Session(null));
        var output = new NinoArrayBufferWriter();
        for (int i = 0; i < 3; i++)
        {
            output.ResetWrittenCount();
            world.SaveWithout(output, in Runtime);
            world.LoadKeeping(output.WrittenSpan, in NewConfig);
        }

        output.ResetWrittenCount();
        long before = GC.GetAllocatedBytesForCurrentThread();
        world.SaveWithout(output, in Runtime);
        world.LoadKeeping(output.WrittenSpan, in NewConfig);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That((world.Count<Balance>(), world.GetSingleton<Config>().Rate, world.HasSingleton<Cache>()), Is.EqualTo((1000, 1, false)));
    }
}
