using System;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ComponentTests
{
    private World _world;

    [SetUp]
    public void SetUp() => _world = new World();

    [TearDown]
    public void TearDown() => _world.Dispose();

    [Test]
    public void AddGetAndRefsWriteThrough()
    {
        var e = _world.CreateEntity();
        e.Add<Health>().Value = 10;
        e.Get<Health>().Value += 5;

        Assert.That(e.Has<Health>(), Is.True);
        Assert.That(e.Get<Health>().Value, Is.EqualTo(15));
        Assert.That(e.Add<Health>().Value, Is.Zero, "Add resets an existing component");
        Assert.Throws<InvalidOperationException>(() => e.Get<Position>());
    }

    [Test]
    public void TryGetAndGetOrAdd()
    {
        var e = _world.CreateEntity();
        Assert.That(e.TryGet<Health>(out _), Is.False);

        e.GetOrAdd<Health>().Value = 3;
        e.GetOrAdd<Health>().Value++;
        Assert.That(e.TryGet<Health>(out var health), Is.True);
        Assert.That(health.Value, Is.EqualTo(4));
    }

    [Test]
    public void SetAndRemoveUpToNineComponentsAtOnce()
    {
        var e = _world.CreateEntity(new C1(1), new C2(2), new C3(3), new C4(4), new C5(5), new C6(6), new C7(7), new C8(8), new C9(9));
        Assert.That(e.Get<C1>().V + e.Get<C5>().V + e.Get<C9>().V, Is.EqualTo(15));

        e.Remove<C1, C2, C3, C4, C5, C6, C7, C8>();
        Assert.That(e.Has<C1>() || e.Has<C8>(), Is.False);
        Assert.That(e.Get<C9>().V, Is.EqualTo(9));

        e.Set(new C1(10), new C9(90));
        Assert.That((e.Get<C1>().V, e.Get<C9>().V), Is.EqualTo((10, 90)), "Set overwrites existing components");
        e.Remove<Position>(); // missing components are ignored
    }

    [Test]
    public void DataSurvivesArchetypeMovesOfOtherEntities()
    {
        var entities = new Entity[100];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = _world.CreateEntity(new Position(i, -i));

        for (int i = 0; i < entities.Length; i += 2) entities[i].Set(new Velocity(1, 1));
        for (int i = 0; i < entities.Length; i += 3) entities[i].Remove<Velocity>();

        for (int i = 0; i < entities.Length; i++)
            Assert.That(entities[i].Get<Position>(), Is.EqualTo(new Position(i, -i)));
    }

    [Test]
    public void RemovedAndDestroyedComponentsReleaseManagedReferences()
    {
        var weak = Spawn(_world, out var removed, out var destroyed);
        removed.Remove<Name>();
        destroyed.Destroy();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.That(weak.removed.IsAlive || weak.destroyed.IsAlive, Is.False);

        static (WeakReference removed, WeakReference destroyed) Spawn(World world, out Entity a, out Entity b)
        {
            string x = new('x', 10), y = new('y', 10);
            a = world.CreateEntity(new Name(x));
            b = world.CreateEntity(new Name(y));
            return (new WeakReference(x), new WeakReference(y));
        }
    }

    [Test]
    public void StructuralChangesOnStaleHandlesThrow()
    {
        var e = _world.CreateEntity();
        e.Destroy();
        Assert.Throws<InvalidOperationException>(() => e.Add<Health>());
        Assert.Throws<InvalidOperationException>(() => e.Set(new Health(1)));
        Assert.Throws<InvalidOperationException>(() => e.Remove<Health>());
    }

    [Test]
    public void MoreThan256ComponentTypes()
    {
        // 300 distinct component types: bits past 255 live in the mask's overflow vectors.
        var e = _world.CreateEntity();
        var other = _world.CreateEntity();
        Wide.AddAll(e);
        other.Add<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>().Value = 7;

        Assert.That(e.Has<Wide.Bit<Wide.D0, Wide.D0, Wide.D0>>(), Is.True);
        Assert.That(e.Has<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>(), Is.True);
        Assert.That(_world.Count<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>(), Is.EqualTo(2));
        Assert.That(_world.Count<Wide.Bit<Wide.D0, Wide.D0, Wide.D0>, Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>(), Is.EqualTo(1));

        e.Remove<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>();
        Assert.That(e.Has<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>(), Is.False);
        Assert.That(e.Has<Wide.Bit<Wide.D2, Wide.D9, Wide.D8>>(), Is.True);
        Assert.That(other.Get<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>().Value, Is.EqualTo(7));
        Assert.That(_world.Count<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>(), Is.EqualTo(1));

        // Queries on bits past 255 look the cache up without allocating their key.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) _ = _world.Count<Wide.Bit<Wide.D2, Wide.D9, Wide.D9>>();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}

/// <summary>300 component types built from three decimal digits (Bit&lt;D0,D0,D0&gt; .. Bit&lt;D2,D9,D9&gt;).</summary>
internal static class Wide
{
    public struct Bit<TA, TB, TC> : IComponent { public int Value; }

    public struct D0; public struct D1; public struct D2; public struct D3; public struct D4;
    public struct D5; public struct D6; public struct D7; public struct D8; public struct D9;

    public static void AddAll(Entity e)
    {
        Tens<D0>(e); Tens<D1>(e); Tens<D2>(e);
    }

    private static void Tens<TA>(Entity e)
    {
        Ones<TA, D0>(e); Ones<TA, D1>(e); Ones<TA, D2>(e); Ones<TA, D3>(e); Ones<TA, D4>(e);
        Ones<TA, D5>(e); Ones<TA, D6>(e); Ones<TA, D7>(e); Ones<TA, D8>(e); Ones<TA, D9>(e);
    }

    private static void Ones<TA, TB>(Entity e)
    {
        e.Add<Bit<TA, TB, D0>>(); e.Add<Bit<TA, TB, D1>>(); e.Add<Bit<TA, TB, D2>>(); e.Add<Bit<TA, TB, D3>>();
        e.Add<Bit<TA, TB, D4>>(); e.Add<Bit<TA, TB, D5>>(); e.Add<Bit<TA, TB, D6>>(); e.Add<Bit<TA, TB, D7>>();
        e.Add<Bit<TA, TB, D8>>(); e.Add<Bit<TA, TB, D9>>();
    }
}
