using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Counts stunned and free entities; stuns every free one and frees every stunned one (one bit flip each).</summary>
public class StunCycle : EcsSystem
{
    [ForEach, Without<Stun>]
    private static void Free(Entity entity, in Health health, ref Score score)
    {
        score.Points++;
        entity.Set(new Stun(3));
    }

    [ForEach]
    private static void Stunned(Entity entity, ref Stun stun, ref Score score)
    {
        score.Deaths++;
        stun.Ticks--;
        entity.Remove<Stun>();
    }
}

/// <summary>Batch and no-structural-change iteration over presence bits.</summary>
public class StunnedHealth : EcsSystem
{
    [ForEach, With<Stun>]
    private static void Batch(System.Span<Health> health)
    {
        for (int i = 0; i < health.Length; i++) health[i] = health[i] with { Value = health[i].Value + 100 };
    }

    [ForEach, Without<Stun>]
    private static void Free(ref Health health) => health = health with { Value = health.Value + 1 };
}

/// <summary>The entity to take <see cref="Health"/> from during a pass.</summary>
public record struct Victim(EntityId Target) : ISingletonComponent;

/// <summary>Counts visits; the first visit takes Health from the victim, a later row of the same full table.</summary>
public class Cull : EcsSystem
{
    [ForEach]
    private static void Visit(Entity entity, in Health health, ref Score score, in Victim victim)
    {
        score.Points++;
        if (victim.Target != entity.Id && entity.World.TryGetEntity(victim.Target, out var target) && target.Has<Health>())
            target.Remove<Health>();
    }
}

/// <summary>Visits the free once each and stuns those whose health is a multiple of 4,999 (a structural pass).</summary>
public class SparseStun : EcsSystem
{
    [ForEach, Without<Stun>]
    private static void Visit(Entity entity, in Health health, ref Score score)
    {
        score.Points++;
        if (health.Value % 4_999 == 0) entity.Set(new Stun(1));
    }
}

public class PresenceTests
{
    [Test]
    public void LongStretchesWithoutPassingRowsAreSkippedExactly()
    {
        // 20,000 rows, a handful stunned: words without a stunned row (With) or with only free rows (Without) are
        // skipped or spanned whole, by every walk: queries, counts, stable and structural passes, bulk changes.
        const int n = 20_000;
        using var world = new World();
        var entities = new Entity[n];
        world.CreateEntities(entities, new Health(0), new Stun(1));
        int[] stunned = [5, 6_000, 6_001, 13_000, 19_999];
        for (int i = 0; i < n; i++)
        {
            entities[i].Set(new Health(i));
            if (System.Array.IndexOf(stunned, i) < 0) entities[i].Remove<Stun>();
        }

        entities[9_000].Destroy();
        var with = QueryFilter.Create().With<Stun>();
        var without = QueryFilter.Create().Without<Stun>();
        var seen = new System.Collections.Generic.List<int>();
        foreach (var item in world.Query<Health>(with)) seen.Add(item.Component1.Value);
        Assert.That(seen, Is.EquivalentTo(stunned)); // the destroyed row took the last row
        Assert.That((world.Count<Health>(with), world.Count<Health>(without)), Is.EqualTo((5, n - 6)));
        Assert.That(world.Any<Health>(with), Is.True);

        world.AddSystem<StunnedHealth>();
        world.Update();
        for (int i = 0; i < n; i++)
            if (i != 9_000) Assert.That(entities[i].Read<Health>().Value, Is.EqualTo(i + (System.Array.IndexOf(stunned, i) < 0 ? 1 : 100)), $"entity {i}");
        world.RemoveSystem<StunnedHealth>();

        for (int i = 0; i < n; i++)
            if (i != 9_000) entities[i].Set(new Health(i));
        world.AddSingleton<Score>();
        world.AddSystem<SparseStun>();
        world.Update(); // visits the n - 6 free rows, stuns 0, 4,999, 14,997 and 19,996 (9,998 is free too)
        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(n - 6));
        Assert.That(world.Count<Health>(with), Is.EqualTo(10));
        world.RemoveSystem<SparseStun>();

        Assert.That(world.SetAll(new Frozen(), with), Is.EqualTo(10));    // rows move out: walks down
        Assert.That(world.RemoveAll<Stun>(with), Is.EqualTo(10));         // in place: a presence word at a time
        Assert.That((world.Any<Health>(with), world.Count<Frozen>()), Is.EqualTo((false, 10)));
    }

    [Test]
    public void ARowThatLosesAComponentDuringAPassIsNotVisited()
    {
        using var world = new World();
        var entities = new Entity[100];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.CreateEntity(new Health(i));
        world.AddSingleton<Score>();
        world.AddSingleton<Victim>() = new Victim(entities[^1]);
        world.AddSystem<Cull>();

        world.Update(); // the table had no presence bits when the pass reached it
        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(99));
        Assert.That(world.Count<Health>(), Is.EqualTo(99));

        int visited = 0;
        entities[^1].Set(new Health(0));
        world.GetSingleton<Victim>() = new Victim(entities[^2]);
        foreach (var item in world.Query<Health>())
        {
            visited++;
            if (entities[^2].Has<Health>()) entities[^2].Remove<Health>();
        }

        Assert.That(visited, Is.EqualTo(99));
    }

    [Test]
    public void RemovingAManagedComponentReleasesItsReference()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1));
        var weak = SetName(e);
        e.Remove<Name>();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        Assert.That(weak.IsAlive, Is.False);
        Assert.That(e.Has<Name>(), Is.False);
        Assert.That(e.Get<Health>().Value, Is.EqualTo(1));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static System.WeakReference SetName(Entity e)
    {
        var value = new string('x', 32);
        e.Set(new Name(value));
        return new System.WeakReference(value);
    }

    [Test]
    public void RemovingAComponentKeepsTheRow()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1));
        e.Set(new Stun(5));                       // a new column: moves into the archetype with Stun
        ref var health = ref e.Get<Health>();

        e.Remove<Stun>();                         // a bit flip: the row (and the ref) stay where they are
        Assert.That(e.Has<Stun>(), Is.False);
        Assert.That(e.TryGet<Stun>(out _), Is.False);
        Assert.Throws<System.InvalidOperationException>(() => e.Get<Stun>());
        health = new Health(7);

        e.Set(new Stun(2));
        Assert.That(e.Has<Stun>(), Is.True);
        Assert.That(e.Get<Stun>().Ticks, Is.EqualTo(2));
        Assert.That(e.Get<Health>().Value, Is.EqualTo(7));
        Assert.That(e.GetOrAdd<Marked>(), Is.EqualTo(default(Marked)));
        Assert.That(e.Has<Marked>(), Is.True);
    }

    [Test]
    public void QueriesTreatARemovedComponentAsAbsent()
    {
        using var world = new World();
        var entities = new Entity[200];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Health(0), new Stun(1));
            if (i % 4 != 0) entities[i].Remove<Stun>();
        }

        var stunned = QueryFilter.Create().With<Stun>();
        var free = QueryFilter.Create().Without<Stun>();
        Assert.That(world.Count<Health>(stunned), Is.EqualTo(50));
        Assert.That(world.Count<Health>(free), Is.EqualTo(150));
        Assert.That(world.Count<Stun>(), Is.EqualTo(50));
        int visited = 0;
        foreach (var item in world.Query<Stun>())
        {
            visited++;
            Assert.That(item.Entity.Id.Index % 4, Is.Zero);
        }

        Assert.That(visited, Is.EqualTo(50));

        entities[0].Destroy(); // a destroyed entity never matches
        Assert.That(world.Count<Health>(stunned), Is.EqualTo(49));
        Assert.That(world.Count<Health>(free), Is.EqualTo(150));
    }

    [Test]
    public void CountsAndAnyMatchTheRowsQueriesVisit()
    {
        using var world = new World();
        var entities = new Entity[300];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Health(0), new Stun(1), new Frozen());
            if (i % 3 == 0) entities[i].Remove<Stun>();
            if (i % 5 == 0) entities[i].Remove<Frozen>();
        }

        entities[1].Destroy();
        QueryFilter[] filters =
        [
            default, QueryFilter.Create().With<Stun>(), QueryFilter.Create().Without<Stun>(),
            QueryFilter.Create().With<Stun>().With<Frozen>(), QueryFilter.Create().Without<Stun>().Without<Frozen>(),
            QueryFilter.Create().With<Stun>().Without<Frozen>(), QueryFilter.Create().Without<Frozen>().With<Health>(),
            QueryFilter.Create().WithAny<Stun>().WithAny<Frozen>(),
        ];
        foreach (var filter in filters)
        {
            int visited = 0;
            foreach (var _ in world.Query<Health>(filter)) visited++;
            Assert.That(world.Count<Health>(filter), Is.EqualTo(visited));
            Assert.That(world.Any<Health>(filter), Is.EqualTo(visited > 0));
        }

        Assert.That(world.Count<Health>(QueryFilter.Create().Without<Stun>()), Is.EqualTo(100));
        foreach (var e in entities)
            if (e.IsAlive && e.Has<Stun>()) e.Remove<Stun>();
        Assert.That(world.Any<Health>(QueryFilter.Create().With<Stun>()), Is.False);
    }

    [Test]
    public void SystemsSeeRemovalsImmediatelyAndVisitEachEntityOnce()
    {
        using var world = new World();
        for (int i = 0; i < 300; i++) world.CreateEntity(new Health(0));
        world.AddSingleton<Score>();
        world.AddSystem<StunCycle>();

        world.Update(); // Free stuns all 300 (moves: first add), then Stunned frees them (bit flips)
        Assert.That(world.GetSingleton<Score>(), Is.EqualTo(new Score(Deaths: 300, Points: 300)));
        world.Update(); // the same cycle with bit flips only
        Assert.That(world.GetSingleton<Score>(), Is.EqualTo(new Score(Deaths: 600, Points: 600)));
        Assert.That(world.Count<Stun>(), Is.Zero);
    }

    [Test]
    public void BatchAndDenseIterationFilterOnPresenceBits()
    {
        using var world = new World();
        var entities = new Entity[150];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity(new Health(0), new Stun(1));
            if (i % 3 != 0) entities[i].Remove<Stun>();
        }

        entities[3].Destroy();
        world.AddSystem<StunnedHealth>();
        world.Update();

        for (int i = 0; i < entities.Length; i++)
        {
            if (i == 3) continue;
            Assert.That(entities[i].Get<Health>().Value, Is.EqualTo(i % 3 == 0 ? 100 : 1), $"entity {i}");
        }
    }

    [Test]
    public void MultiComponentSetAndRemoveKeepTheRow()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1), new Stun(1));
        e.Remove<Stun, Health>();                  // both keep their columns, the row loses them
        Assert.That(e.Has<Health>(), Is.False);
        Assert.That(e.Has<Stun>(), Is.False);
        e.Set(new Health(2), new Stun(4));
        Assert.That(e.Get<Health>().Value, Is.EqualTo(2));
        Assert.That(e.Get<Stun>().Ticks, Is.EqualTo(4));
    }
}
