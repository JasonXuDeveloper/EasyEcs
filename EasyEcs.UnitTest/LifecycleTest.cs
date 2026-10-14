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

    private struct World : ISingletonComponent
    {
        public int Id;
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
    public void SingletonsAreScopedPerContext()
    {
        var c1 = new Context();
        var c2 = new Context();
        c1.AddSingletonComponent<World>().Value.Id = 1;
        Assert.That(c2.TryGetSingletonComponent<World>(out _), Is.False);

        c2.AddSingletonComponent<World>().Value.Id = 2;
        Assert.That(c1.GetSingletonComponent<World>().Value.Id, Is.EqualTo(1));
        Assert.That(c2.GetSingletonComponent<World>().Value.Id, Is.EqualTo(2));

        c1.RemoveSingletonComponent<World>();
        Assert.That(c1.HasSingletonComponent<World>(), Is.False);
        Assert.That(c2.HasSingletonComponent<World>(), Is.True);
    }

    private struct SumJob : IForEach<Hp, Mp>
    {
        public int Sum;
        public Context Ctx;

        public void Execute(int entityId, ref Hp hp, ref Mp mp)
        {
            Sum += hp.Value + mp.Value;
            hp.Value++;
            // Structural change mid-iteration forces component arrays to grow
            if (entityId == 0)
                for (int i = 0; i < 200; i++)
                    Ctx.CreateEntity();
        }
    }

    [Test]
    public void ForEachVisitsMatchingEntitiesAndSurvivesResize()
    {
        var ctx = new Context(new Context.Options(false, initialEntityCapacity: 4));
        for (int i = 0; i < 10; i++)
        {
            var e = ctx.CreateEntity();
            var (hp, mp) = e.Value.AddComponents<Hp, Mp>();
            hp.Value.Value = i;
            mp.Value.Value = 1;
        }

        ctx.CreateEntity().Value.AddComponent<Hp>();

        var job = new SumJob { Ctx = ctx };
        ctx.ForEach<Hp, Mp, SumJob>(ref job);
        Assert.That(job.Sum, Is.EqualTo(45 + 10));

        int total = 0;
        foreach (var r in ctx.GroupOf<Hp, Mp>()) total += r.Component1.Value;
        Assert.That(total, Is.EqualTo(45 + 10), "writes must land in the live (resized) arrays");
    }
}
