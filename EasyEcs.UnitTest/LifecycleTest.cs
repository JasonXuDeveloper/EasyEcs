using System;
using EasyEcs.Core;
using EasyEcs.Core.Components;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class LifecycleTest
{
    private struct Hp : IComponent
    {
        public int Value;
        public object Payload;
    }

    private struct Mp : IComponent
    {
        public int Value;
    }

    [Test]
    public void DestroyedIdsAreRecycledAndStaleRefsRejected()
    {
        var ctx = new Context(new Context.Options(false, initialEntityCapacity: 4));
        var a = ctx.CreateEntity();
        int id = a.Value.Id;
        a.Value.AddComponent<Hp>().Value.Payload = new object();
        Entity staleCopy = a.Value;

        ctx.DestroyEntity(a);
        var b = ctx.CreateEntity();

        Assert.That(b.Value.Id, Is.EqualTo(id), "id should be recycled");
        Assert.Throws<InvalidOperationException>(() => _ = a.Value, "old EntityRef must be invalid");
        Assert.Throws<InvalidOperationException>(() => staleCopy.AddComponent<Mp>(),
            "stale Entity copy must not mutate the new occupant");
        Assert.That(b.Value.HasComponent<Hp>(), Is.False);
        Assert.That(ctx.CountOf<Hp>(), Is.EqualTo(0));
    }

    [Test]
    public void EntityArraysStayBoundedUnderChurn()
    {
        var ctx = new Context(new Context.Options(false, initialEntityCapacity: 16));
        var refs = new EntityRef[16];
        for (int round = 0; round < 1000; round++)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                refs[i] = ctx.CreateEntity();
                refs[i].Value.AddComponents<Hp, Mp>();
            }

            for (int i = 0; i < refs.Length; i++) ctx.DestroyEntity(refs[i]);
        }

        var e = ctx.CreateEntity();
        Assert.That(e.Value.Id, Is.LessThan(16));
        Assert.That(ctx.EntityCount, Is.EqualTo(1));
    }

    [Test]
    public void DestroyClearsComponentData()
    {
        var ctx = new Context(new Context.Options(false));
        var a = ctx.CreateEntity();
        a.Value.AddComponent<Hp>().Value = new Hp { Value = 7, Payload = "x" };
        ctx.DestroyEntity(a);

        var b = ctx.CreateEntity();
        var hp = b.Value.AddComponent<Hp>();
        Assert.That(hp.Value.Value, Is.EqualTo(0));
        Assert.That(hp.Value.Payload, Is.Null);
    }

    [Test]
    public void ToggleComponentKeepsQueriesConsistent()
    {
        var ctx = new Context(new Context.Options(false));
        for (int i = 0; i < 100; i++)
            ctx.CreateEntity().Value.AddComponent<Hp>();

        foreach (var r in ctx.GroupOf<Hp>())
        {
            r.Entity.AddComponent<Mp>();
        }

        Assert.That(ctx.CountOf<Hp, Mp>(), Is.EqualTo(100));

        foreach (var r in ctx.GroupOf<Hp, Mp>())
        {
            r.Entity.RemoveComponent<Mp>();
            r.Entity.AddComponent<Mp>();
            r.Entity.RemoveComponent<Mp>();
        }

        Assert.That(ctx.CountOf<Mp>(), Is.EqualTo(0));
        Assert.That(ctx.CountOf<Hp>(), Is.EqualTo(100));

        ctx.CompactArchetypes();
        int n = 0;
        foreach (var _ in ctx.GroupOf<Hp>()) n++;
        Assert.That(n, Is.EqualTo(100));

        // Removal after compaction must still hit the right slot
        foreach (var r in ctx.GroupOf<Hp>())
        {
            if (r.Entity.Id % 2 == 0) ctx.DestroyEntity(r.Entity);
        }

        Assert.That(ctx.CountOf<Hp>(), Is.EqualTo(50));
    }

    [Test]
    public void StaleEntityCopyCannotDestroyNewOccupant()
    {
        var ctx = new Context(new Context.Options(false));
        var a = ctx.CreateEntity();
        Entity staleCopy = a.Value;
        ctx.DestroyEntity(a);

        var b = ctx.CreateEntity();
        b.Value.AddComponent<Hp>();
        Assert.That(b.Value.Id, Is.EqualTo(staleCopy.Id));

        // A second destroy through the stale copy must be ignored, not kill the recycled id's new owner.
        ctx.DestroyEntity(staleCopy);
        Assert.That(ctx.EntityCount, Is.EqualTo(1));
        Assert.That(b.Value.HasComponent<Hp>(), Is.True);
        Assert.That(ctx.TryGetEntityById(staleCopy.Id, out var found) && found == b, Is.True);
    }

    [Test]
    public void GroupIterationSurvivesDestroyAndRecreate()
    {
        var ctx = new Context(new Context.Options(false));
        for (int i = 0; i < 10; i++)
            ctx.CreateEntity().Value.AddComponent<Hp>().Value.Value = 1;

        // Destroy each visited entity and spawn a replacement that reuses its id (respawn pattern).
        int visited = 0;
        foreach (var r in ctx.GroupOf<Hp>())
        {
            visited++;
            ctx.DestroyEntity(r.Entity);
            ctx.CreateEntity().Value.AddComponent<Mp>();
        }

        Assert.That(visited, Is.EqualTo(10));
        Assert.That(ctx.CountOf<Hp>(), Is.EqualTo(0));
        Assert.That(ctx.CountOf<Mp>(), Is.EqualTo(10));
        Assert.That(ctx.EntityCount, Is.EqualTo(10));
    }
}
