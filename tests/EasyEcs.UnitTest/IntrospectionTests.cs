using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Payload(long A, long B, long C, long D, long E, long F, long G, long H) : IComponent; // 64 bytes a row

public class IntrospectionTests
{
    [Test]
    public void ATableShowsItsComponentsRowsAndMemory()
    {
        using var world = new World();
        world.CreateEntity(new Health(1), new Armor(2));
        var unarmored = world.CreateEntity(new Health(3), new Armor(4));
        world.CreateEntity(new Position(0, 0));
        unarmored.Remove<Armor>();                                            // stays in its table, without the column

        var table = world.Describe().Single(t => t.Columns.Any(c => c.Type == typeof(Armor)));
        Assert.That(table.Columns.Select(c => c.Type), Is.EquivalentTo(new[] { typeof(Health), typeof(Armor) })); // no tick column
        Assert.That(table.Columns.Single(c => c.Type == typeof(Armor)).Without, Is.EqualTo(1));
        Assert.That((table.Entities, table.Rows), Is.EqualTo((2, 2)));
        Assert.That(table.Capacity, Is.GreaterThanOrEqualTo(2));
        Assert.That(table.Bytes, Is.GreaterThanOrEqualTo(table.Capacity * (8L + sizeof(int) * 2 + sizeof(ulong)))); // slots, values, ticks
    }

    [Test]
    public void AnEntityShowsItsValuesAndWhichAreDisabled()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Health(1), new Armor(2));
        entity.Disable<Armor>();
        Assert.That(entity.Describe(), Is.EquivalentTo(new[]
        {
            new ComponentValue(new Health(1), true),
            new ComponentValue(new Armor(2), false),
        }));

        entity.Destroy();
        Assert.That(entity.Describe(), Is.Empty);
    }

    [Test]
    public void AGroupedTableShowsItsGroup()
    {
        using var world = new World();
        world.GroupBy<Zone>();
        world.CreateEntity(new Zone(7));
        var table = world.Describe().Single(t => t.Entities == 1);
        Assert.That(table.Columns.Single().Group, Is.EqualTo(7));
    }

    // An entity of one of 256 tables: the bits of i choose its components among C1..C8.
    private static Entity Spawn(World world, int i)
    {
        var entity = world.CreateEntity(new Health(i), new Payload(), new Armor(i));
        if ((i & 1) != 0) entity.Set(new C1(i));
        if ((i & 2) != 0) entity.Set(new C2(i));
        if ((i & 4) != 0) entity.Set(new C3(i));
        if ((i & 8) != 0) entity.Set(new C4(i));
        if ((i & 16) != 0) entity.Set(new C5(i));
        if ((i & 32) != 0) entity.Set(new C6(i));
        if ((i & 64) != 0) entity.Set(new C7(i));
        if ((i & 128) != 0) entity.Set(new C8(i));
        return entity;
    }

    private static long Bytes(World world) => world.Describe().Sum(t => t.Bytes);

    [Test]
    public void TrimExcessDropsEmptyTablesAndShrinksTheOthers()
    {
        using var world = new World();
        var survivors = new List<(Entity Entity, int I)>();
        var all = new Entity[100_000];
        for (int i = 0; i < all.Length; i++) all[i] = Spawn(world, i);
        for (int i = 0; i < all.Length; i++)
            if (i % 1_009 == 0) survivors.Add((all[i], i));
            else all[i].Destroy();

        world.Update(); // the query caches and edges of a running world
        int c1 = world.Count<C1>();
        var tables = world.Describe();
        long bytes = Bytes(world);
        long memory = GC.GetTotalMemory(true);
        Assert.That(tables.Length, Is.GreaterThan(256));

        world.TrimExcess();

        var trimmed = world.Describe();
        Assert.That(trimmed.Count(t => t.Entities == 0), Is.EqualTo(1));                       // the empty table only
        Assert.That(trimmed.Length, Is.EqualTo(1 + tables.Count(t => t.Entities > 0)));
        foreach (var table in trimmed)
            Assert.That(table.Capacity, Is.EqualTo(Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)table.Rows))));
        Assert.That(Bytes(world), Is.LessThan(bytes / 4));
        Assert.That(memory - GC.GetTotalMemory(true), Is.GreaterThan(4L << 20));                // columns of ~25,000 rows went

        // Handles, values and queries are what they were.
        foreach (var (entity, i) in survivors)
        {
            Assert.That(entity.IsAlive && entity.Get<Health>().Value == i && entity.Read<Armor>().Value == i, Is.True);
            Assert.That(entity.Has<C1>(), Is.EqualTo((i & 1) != 0));
        }

        Assert.That(world.Count<C1>(), Is.EqualTo(c1));
        Assert.That(world.Count<Health>(), Is.EqualTo(survivors.Count));

        // Re-growing recreates the tables: every query cached before sees them.
        for (int i = 0; i < 10_000; i++) Spawn(world, i);
        Assert.That(world.Count<Health>(), Is.EqualTo(survivors.Count + 10_000));
        Assert.That(world.Count<C1>(), Is.EqualTo(c1 + 5_000));
        Assert.That(world.Count<C1, C8>(), Is.EqualTo(survivors.Count(s => (s.I & 129) == 129) + Enumerable.Range(0, 10_000).Count(i => (i & 129) == 129)));
        Assert.That(world.Describe().Length, Is.EqualTo(tables.Length));                     // the same tables again
    }

    [Test]
    public void TrimExcessKeepsChangeTrackingIndexesGroupsAndHierarchy()
    {
        using var world = new World();
        var callsigns = world.Index<Callsign, string>();
        world.GroupBy<Zone>();
        var root = world.CreateEntity(new Health(0));
        var kept = new List<Entity>();
        for (int i = 0; i < 20_000; i++)
        {
            var entity = Spawn(world, i);
            entity.Set(new Zone(i % 7));
            entity.Set(new Callsign(i % 2 == 0 ? "even" : "odd"));
            if (i % 101 == 0)
            {
                entity.Set(new ChildOf(root));
                kept.Add(entity);
            }
            else if (i % 3 != 0) entity.Destroy();
        }

        world.DestroyEntities<Payload>(QueryFilter.Create().Without<ChildOf>()); // only the children stay
        world.Update();                                                                       // the creations are older than the mark
        ulong mark = world.ChangeTick;
        for (int i = 0; i < kept.Count; i += 2) kept[i].Set(new Armor(-1));                // written before the trim
        world.TrimExcess();

        var changed = QueryFilter.Create().Changed<Armor>(mark);
        Assert.That(world.Count<Armor>(changed), Is.EqualTo((kept.Count + 1) / 2));
        world.Update();
        ulong after = world.ChangeTick;
        for (int i = 1; i < kept.Count; i += 2) kept[i].Set(new Armor(-2));                // and after it
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(after)), Is.EqualTo(kept.Count / 2));

        int even = kept.Count(e => e.Read<Callsign>().Value == "even");
        Assert.That(callsigns.Count("even"), Is.EqualTo(even));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Zone>(3)), Is.EqualTo(kept.Count(e => e.Read<Zone>().Id == 3)));

        kept[0].Set(new Zone(3)); // to a group's table that may have been dropped
        world.CreateEntity(new Callsign("even"), new Zone(5));
        Assert.That(world.Count<Health>(QueryFilter.Create().InGroup<Zone>(3)), Is.EqualTo(kept.Count(e => e.Read<Zone>().Id == 3)));
        Assert.That(callsigns.Count("even"), Is.EqualTo(even + 1));

        int children = 0;
        foreach (var _ in world.Children(root)) children++;
        Assert.That(children, Is.EqualTo(kept.Count));
        root.Destroy();                                                                       // the children follow
        Assert.That(kept.All(e => !e.IsAlive), Is.True);
    }

    [Test]
    public void TrimExcessKeepsWhatASnapshotHolds()
    {
        using var world = new World();
        var entities = new Entity[1_000];
        for (int i = 0; i < entities.Length; i++) entities[i] = Spawn(world, i);
        var snapshot = world.Capture();
        foreach (var entity in entities) entity.Destroy();
        for (int i = 0; i < 100; i++) world.CreateEntity(new Position(i, i));                  // a table created after the capture

        world.TrimExcess();
        world.Restore(snapshot);
        Assert.That(world.Count<Health>(), Is.EqualTo(entities.Length));
        Assert.That(world.Count<Position>(), Is.Zero);
        for (int i = 0; i < entities.Length; i++)
            Assert.That(entities[i].IsAlive && entities[i].Get<Health>().Value == i && entities[i].Has<C8>() == ((i & 128) != 0), Is.True);

        foreach (var entity in entities) entity.Destroy();
        world.TrimExcess();                                                                   // the tables stay: the snapshot holds their rows
        world.Restore(snapshot);
        Assert.That(world.Count<Health>(), Is.EqualTo(entities.Length));

        foreach (var entity in entities) entity.Destroy();
        world.Capture(snapshot);                                                              // now it holds no rows
        int tables = world.Describe().Length;
        world.TrimExcess();
        Assert.That(world.Describe().Length, Is.LessThan(tables));
        world.Restore(snapshot);                                                              // by the new order
        Assert.That(world.Count<Health>(), Is.Zero);
        Assert.That(world.Count<Position>(), Is.Zero);
        Spawn(world, 255).Set(new Position(1, 2));
        world.Capture(snapshot);                                                              // a capture into it, a table it had no copy of
        world.Restore(snapshot);
        Assert.That(world.Count<Health, C8, Position>(), Is.EqualTo(1));
    }

    [Test]
    public void TrimExcessIsRefusedDuringAPass()
    {
        using var world = new World();
        world.CreateEntity(new Health(1));
        Assert.Throws<InvalidOperationException>(() =>
        {
#pragma warning disable EECS041 // the refusal is what this test covers
            foreach (var item in world.Query<Health>()) world.TrimExcess();
#pragma warning restore EECS041
        });
        world.TrimExcess(); // the pass ended
        Assert.That(world.Count<Health>(), Is.EqualTo(1));
    }

    // ---------------- DescribeSchedule ----------------

    private sealed class Move : EcsSystem
    {
        [ForEach] private static void Run(ref Position position, in Velocity velocity) => position.X += velocity.X;
    }

    private sealed class Heal : EcsSystem
    {
        [ForEach] private static void Run(ref Health health) => health.Value++;
    }

    private sealed class Watch : EcsSystem // reads what Move writes, ordered by nothing: registration order decides
    {
        [ForEach] private static void Run(in Position position, ref Seen seen) => seen.Count += (int)position.X;
    }

    [UpdateAfter<Heal>]
    private sealed class Wake : EcsSystem // disjoint from Heal, but ordered after it
    {
        [ForEach] private static void Run(ref Stun stun) => stun.Ticks--;
    }

    [UpdateAfter<Move>]
    private sealed class Follow : EcsSystem // ordered after Move and reads what it writes
    {
        [ForEach] private static void Run(in Position position, ref C3 trail) => trail.V = (int)position.X;
    }

    private sealed class Tick : EcsSystem
    {
        [ForEach] private static void Run(ref C1 c, ref Clock clock) => clock.Tick += ++c.V;
    }

    private sealed class Read : EcsSystem
    {
        [ForEach] private static void Run(ref C2 c, in Clock clock) => c.V = (int)clock.Tick;
    }

    private struct LateGroup : ISystemGroup;

    [UpdateInGroup<LateGroup>]
    private sealed class Late : EcsSystem
    {
        protected override void OnUpdate() { } // runs alone
    }

    private static World Scheduled()
    {
        var world = new World();
        world.AddSystem<Move>();
        world.AddSystem<Heal>();
        world.AddSystem<Watch>();
        world.AddSystem<Wake>();
        world.AddSystem<Follow>();
        world.AddSystem<Tick>();
        world.AddSystem<Read>();
        world.AddSystem<Late>();
        return world;
    }

    [Test]
    public void AScheduleShowsStagesAccessAndWhyASystemWaits()
    {
        using var world = Scheduled();
        var schedule = world.DescribeSchedule();
        Assert.That(schedule.Groups.Select(g => g.Name), Is.EqualTo(new[] { "Default", "LateGroup" }));
        var stages = schedule.Groups[0].Stages;
        Assert.That(stages.Select(s => s.Systems.Select(x => x.Name)), Is.EqualTo(new[]
        {
            new[] { "Move", "Heal", "Tick" },
            new[] { "Watch", "Wake", "Follow", "Read" },
        }));

        var move = stages[0].Systems[0];
        Assert.That((move.Reads, move.Writes, move.RunsAlone, move.After.Length), Is.EqualTo((new[] { "Velocity" }, new[] { "Position" }, false, 0)));
        var tick = stages[0].Systems[2];
        Assert.That((tick.Writes, tick.WritesSingletons, tick.ReadsSingletons), Is.EqualTo((new[] { "C1" }, new[] { "Clock" }, Array.Empty<string>())));

        var waits = stages[1].Systems.ToDictionary(s => s.Name, s => s.After.Single());
        Assert.That(waits["Watch"], Has.Property("System").EqualTo("Move").And.Property("Ordered").False);
        Assert.That(waits["Watch"].Conflicts, Is.EqualTo(new[] { "Position" }));
        Assert.That(waits["Wake"], Has.Property("System").EqualTo("Heal").And.Property("Ordered").True);
        Assert.That(waits["Wake"].Conflicts, Is.Empty);                                         // ordered, nothing shared
        Assert.That(waits["Follow"], Has.Property("System").EqualTo("Move").And.Property("Ordered").True);
        Assert.That(waits["Follow"].Conflicts, Is.EqualTo(new[] { "Position" }));
        Assert.That(waits["Read"].Conflicts, Is.EqualTo(new[] { "Clock (singleton)" }));

        // Conflicting and ordered by nothing: Move / Watch and Tick / Read (Move / Follow have an ordering).
        Assert.That(schedule.Groups[0].RegistrationOrdered.Select(p => (p.First, p.Second, string.Join(", ", p.Conflicts))),
            Is.EqualTo(new[] { ("Move", "Watch", "Position"), ("Tick", "Read", "Clock (singleton)") }));
        Assert.That(schedule.Groups[1].Stages.Single().Systems.Single().RunsAlone, Is.True);
    }

    [Test]
    public void AScheduleReadsAsTextAndAsAMermaidFlowchart()
    {
        using var world = Scheduled();
        var schedule = world.DescribeSchedule();
        string text = schedule.ToString();
        Assert.That(text, Does.Contain("Group Default\n  Stage 1\n    Move, reads Velocity, writes Position\n"));
        Assert.That(text, Does.Contain("      after Move: Position\n"));
        Assert.That(text, Does.Contain("      after Heal: ordered\n"));
        Assert.That(text, Does.Contain("  Registration order only: Move before Watch (Position)\n"));
        Assert.That(text, Does.Contain("Group LateGroup\n  Stage 1\n    Late (runs alone)\n"));

        var lines = schedule.ToMermaid().Split('\n');
        Assert.That(lines[0], Is.EqualTo("flowchart TD"));
        Assert.That(lines, Does.Contain("  subgraph g0[\"Default\"]").And.Contain("    subgraph g0s1[\"Stage 2\"]"));
        Assert.That(lines, Does.Contain("      g0n0[\"Move<br/>reads: Velocity<br/>writes: Position\"]"));
        Assert.That(lines, Does.Contain("      g0n2[\"Tick<br/>writes: C1, Clock (singleton)\"]"));
        Assert.That(lines, Does.Contain("  g0n0 -->|\"Position\"| g0n3"));                         // Move -> Watch
        Assert.That(lines, Does.Contain("  g0n1 -->|\"ordered\"| g0n4"));                          // Heal -> Wake
        Assert.That(lines, Does.Contain("  g0n0 -. \"registration order\" .-> g0n3"));
        Assert.That(lines, Does.Contain("  subgraph g1[\"LateGroup\"]"));
        Assert.That(lines.Count(l => l.Trim() == "end"), Is.EqualTo(3 + 2));                        // 3 stages, 2 groups
    }

    [Test]
    public void AScheduleNamesGenericAndRepeatedSystems()
    {
        Assert.That(ScheduleInfo.NameOf(typeof(System.Collections.Generic.Dictionary<int, Position>)), Is.EqualTo("Dictionary<Int32, Position>"));
        using var world = new World();
        world.AddSystem(new Heal());
        world.AddSystem(new Heal());                                                               // the same type twice
        var stages = world.DescribeSchedule().Groups.Single().Stages;
        Assert.That(stages.Select(s => s.Systems.Single().Name), Is.EqualTo(new[] { "Heal", "Heal #2" }));
        Assert.That(stages[1].Systems[0].After.Single().System, Is.EqualTo("Heal"));
    }
}
