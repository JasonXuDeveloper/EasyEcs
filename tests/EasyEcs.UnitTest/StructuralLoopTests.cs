using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public record struct Stamina(int Value) : IComponent;
public struct Felled : IComponent;
public struct Shoved : IComponent;
public record struct Daze(int Ticks) : IComponent;
public record struct Aegis(int Layers) : IComponent;
public partial record struct Banner([LookupKey] int Team) : IComponent;
public partial record struct Crest([UniqueKey] int Id) : IComponent;
public record struct Remains(int Slot) : ICleanupComponent;
public record struct Fallen(EntityId Who);
public record struct Peak(int Value);

/// <summary>
/// Structural changes under a condition, in vector loops: each method marks the rows its condition passes, and the
/// system applies the change to them in bulk when the pass ends (Kernel.Structural.cs, MarkedRows). Each method has a twin
/// in <see cref="ScalarRules"/> that runs one row at a time; the two worlds must end alike.
/// </summary>
[EcsSystem]
public ref struct VectorRules
{
    // Rows move between tables before the vector passes (a structural change by another method in the same tick).
    [ForEach(SimdMode = SimdMode.Off)]
    static void Shove(Entity e, in Stamina v)
    {
        if ((v.Value & 15) == 5) e.Set(new Shoved());
        else if ((v.Value & 15) == 6) e.Remove<Shoved>();
    }

    [ForEach(SimdMode = SimdMode.Require), Without<Felled>]
    static void Fall(Entity e, in Stamina v, Emit<Fallen> fallen)
    {
        if (v.Value > 0) return;
        e.SetFelled();
        fallen.Add(new Fallen(e.Id));
    }

    [ForEach(SimdMode = SimdMode.Require), Without<Daze>]
    static void Stun(Entity e, in Stamina v)
    {
        if ((v.Value & 7) == 3) e.SetDaze(2);
    }

    [ForEach(SimdMode = SimdMode.Require), With<Aegis>]
    static void Strip(Entity e, in Stamina v)
    {
        if (v.Value > 50 && v.Value < 60) e.RemoveAegis();
    }

    [ForEach(SimdMode = SimdMode.Require), With<Aegis>]
    static void Hide(Entity e, in Stamina v)
    {
        if ((v.Value & 15) == 0) e.Disable<Aegis>();
    }

    [ForEach(SimdMode = SimdMode.Require), With<Disabled<Aegis>>]
    static void Show(Entity e, in Stamina v)
    {
        if ((v.Value & 3) == 0) e.Enable<Aegis>();
    }

    [ForEach(SimdMode = SimdMode.Require), Without<Banner>]
    static void Enlist(Entity e, in Stamina v)
    {
        if ((v.Value & 31) == 1) e.Set(new Banner(7));
    }

    [ForEach(SimdMode = SimdMode.Require)]
    static void Peaks(in Stamina v, Emit<Peak> peaks)
    {
        if (v.Value > 90) peaks.Add(new Peak(v.Value));
    }

    [ForEach(SimdMode = SimdMode.Require), With<Felled>]
    static void Reap(Entity e, in Stamina v)
    {
        if (v.Value < -80) e.Destroy();
    }
}

/// <summary><see cref="VectorRules"/> one row at a time.</summary>
[EcsSystem]
public ref struct ScalarRules
{
    [ForEach(SimdMode = SimdMode.Off)]
    static void Shove(Entity e, in Stamina v)
    {
        if ((v.Value & 15) == 5) e.Set(new Shoved());
        else if ((v.Value & 15) == 6) e.Remove<Shoved>();
    }

    [ForEach(SimdMode = SimdMode.Off), Without<Felled>]
    static void Fall(Entity e, in Stamina v, Emit<Fallen> fallen)
    {
        if (v.Value > 0) return;
        e.SetFelled();
        fallen.Add(new Fallen(e.Id));
    }

    [ForEach(SimdMode = SimdMode.Off), Without<Daze>]
    static void Stun(Entity e, in Stamina v)
    {
        if ((v.Value & 7) == 3) e.SetDaze(2);
    }

    [ForEach(SimdMode = SimdMode.Off), With<Aegis>]
    static void Strip(Entity e, in Stamina v)
    {
        if (v.Value > 50 && v.Value < 60) e.RemoveAegis();
    }

    [ForEach(SimdMode = SimdMode.Off), With<Aegis>]
    static void Hide(Entity e, in Stamina v)
    {
        if ((v.Value & 15) == 0) e.Disable<Aegis>();
    }

    [ForEach(SimdMode = SimdMode.Off), With<Disabled<Aegis>>]
    static void Show(Entity e, in Stamina v)
    {
        if ((v.Value & 3) == 0) e.Enable<Aegis>();
    }

    [ForEach(SimdMode = SimdMode.Off), Without<Banner>]
    static void Enlist(Entity e, in Stamina v)
    {
        if ((v.Value & 31) == 1) e.Set(new Banner(7));
    }

    [ForEach(SimdMode = SimdMode.Off)]
    static void Peaks(in Stamina v, Emit<Peak> peaks)
    {
        if (v.Value > 90) peaks.Add(new Peak(v.Value));
    }

    [ForEach(SimdMode = SimdMode.Off), With<Felled>]
    static void Reap(Entity e, in Stamina v)
    {
        if (v.Value < -80) e.Destroy();
    }
}

/// <summary>A unique key given to the rows a condition passes: one value for many rows, so the method stays scalar.</summary>
[EcsSystem]
public ref struct CrownRule
{
    [ForEach]
    static void Crown(Entity e, in Stamina v)
    {
        if (v.Value == 42) e.SetCrest(1);
    }
}

public class StructuralLoopTests
{
    private static readonly WorldOptions Logged = new() { DestroyLogCapacity = 1 << 16 };

    private static World Make<TSystem>(int count, int seed, Entity[] entities) where TSystem : struct, allows ref struct
    {
        var world = new World(Logged);
        world.Reserve<Stamina>(count);
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var e = entities[i] = world.CreateEntity(new Stamina(random.Next(-100, 101)));
            if (i % 2 == 0) e.Set(new Aegis(i));
            if (i % 4 == 1) e.Set(new Remains(i)); // destroyed: retired until its cleanup component goes
            if (i % 3 == 0) e.Set(new Shoved());
        }
        world.AddSystem<TSystem>();
        return world;
    }

    /// <summary>
    /// The vector world and the scalar one over the same random rows, <paramref name="rounds"/> updates with new values
    /// each: every entity ends alike (alive or not, its components, their values), the records come out in the same
    /// order, the index holds the same entities and the destroy log the same ids.
    /// </summary>
    [TestCase(0, 1)]
    [TestCase(1, 2)]
    [TestCase(7, 3)]
    [TestCase(64, 4)]
    [TestCase(65, 5)]
    [TestCase(1000 + 13, 6)]
    [TestCase(10000 + 3, 7)]
    public void VectorPassesChangeWhatTheScalarMethodChanges(int count, int seed)
    {
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<VectorRules>(count, seed, a);
        using var scalar = Make<ScalarRules>(count, seed, b);
        AssertVectorized(vector);
        var random = new Random(seed * 31);
        for (int round = 0; round < 6; round++)
        {
            for (int i = 0; i < count; i++)
            {
                int value = random.Next(-100, 101);
                if (round == 3) value = -100; // every row passes Fall and Reap
                if (round == 4) value = 100;  // no row passes them
                if (a[i].IsAlive && a[i].Has<Stamina>()) a[i].Set(new Stamina(value));
                if (b[i].IsAlive && b[i].Has<Stamina>()) b[i].Set(new Stamina(value));
                if (round == 2 && i % 8 == 0 && a[i].IsAlive && b[i].IsAlive && a[i].Has<Remains>()) // cleanup done: freed
                {
                    a[i].Remove<Remains>();
                    b[i].Remove<Remains>();
                }
            }
            vector.Update();
            scalar.Update();
            AssertSame(vector, scalar, a, b, round);
        }
    }

    [Test]
    public void ATableEmptiedByTheMarkedRowsIsFreedInRowOrder()
    {
        // Every row of the tables Reap visits passes: whole tables are freed at once, their ids in row order.
        const int count = 300;
        var a = new Entity[count];
        var b = new Entity[count];
        using var vector = Make<VectorRules>(count, 9, a);
        using var scalar = Make<ScalarRules>(count, 9, b);
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < count; i++)
            {
                if (a[i].IsAlive && a[i].Has<Stamina>()) a[i].Set(new Stamina(-100));
                if (b[i].IsAlive && b[i].Has<Stamina>()) b[i].Set(new Stamina(-100));
            }
            vector.Update();
            scalar.Update();
            AssertSame(vector, scalar, a, b, round);
        }
        // The freed ids come back in the same order: new entities get the same ids in both worlds.
        for (int i = 0; i < 50; i++)
            Assert.That(vector.CreateEntity(new Stamina(1)).Id, Is.EqualTo(scalar.CreateEntity(new Stamina(1)).Id));
    }

    [Test]
    public void AUniqueKeyGivenUnderAConditionStaysScalar()
    {
        using var world = new World();
        world.AddSystem<CrownRule>();
        Assert.That(world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops,
            Is.EqualTo(new[] { "Crown: scalar (it gives Crest, a unique key, to the rows it marks: one value would be claimed by every row)" }));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void PassesAllocateNothing(bool vectorized)
    {
        const int count = 4096 + 5;
        var a = new Entity[count];
        using var world = vectorized ? Make<VectorRules>(count, 11, a) : Make<ScalarRules>(count, 11, a);
        // Values that repeat every 4 rounds: after the warm-up every table the rules reach exists, so the rounds measured
        // change rows between tables that are there (a new combination of components would allocate its table).
        var values = new int[4, count];
        var random = new Random(3);
        for (int r = 0; r < 4; r++)
            for (int i = 0; i < count; i++) values[r, i] = random.Next(-100, 101);
        int round = 0;
        void Round()
        {
            for (int i = 0; i < count; i++)
                if (a[i].IsAlive && a[i].Has<Stamina>()) a[i].Set(new Stamina(values[round & 3, i]));
            round++;
            world.Update();
            world.GetSingleton<Emitted<Fallen>>().Items.Clear();
            world.GetSingleton<Emitted<Peak>>().Items.Clear();
        }
        for (int i = 0; i < 40; i++) Round(); // warm-up: tables, query caches, marks, JIT
        long before = Allocations.Baseline();
        for (int i = 0; i < 8; i++) Round();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static void AssertVectorized(World world)
    {
        if (!System.Numerics.Vector.IsHardwareAccelerated) Assert.Ignore("no vector hardware: the scalar job runs");
        foreach (var loop in world.DescribeSchedule().Groups[0].Stages[0].Systems[0].Loops)
            if (!loop.StartsWith("Shove", StringComparison.Ordinal))
                Assert.That(loop, Does.Contain(": vector"), loop);
    }

    private static void AssertSame(World vector, World scalar, Entity[] a, Entity[] b, int round)
    {
        for (int i = 0; i < a.Length; i++)
        {
            string at = $"round {round}, entity {i}";
            Assert.That(a[i].IsAlive, Is.EqualTo(b[i].IsAlive), at);
            if (!b[i].IsAlive) continue;
            Assert.That(a[i].Has<Retired>(), Is.EqualTo(b[i].Has<Retired>()), at);
            Assert.That(a[i].Has<Felled>(), Is.EqualTo(b[i].Has<Felled>()), at);
            Assert.That(a[i].Has<Shoved>(), Is.EqualTo(b[i].Has<Shoved>()), at);
            Assert.That(a[i].Has<Daze>() ? a[i].Read<Daze>().Ticks : -1, Is.EqualTo(b[i].Has<Daze>() ? b[i].Read<Daze>().Ticks : -1), at);
            Assert.That(a[i].Has<Aegis>(), Is.EqualTo(b[i].Has<Aegis>()), at);
            Assert.That(a[i].IsDisabled<Aegis>(), Is.EqualTo(b[i].IsDisabled<Aegis>()), at);
            Assert.That(a[i].Has<Banner>(), Is.EqualTo(b[i].Has<Banner>()), at);
        }

        // Records in row order, and rows in the same order: the moves after the pass keep the method's row order.
        Assert.That(Ids(vector.GetSingleton<Emitted<Fallen>>().Items), Is.EqualTo(Ids(scalar.GetSingleton<Emitted<Fallen>>().Items)), $"round {round}");
        Assert.That(Peaks(vector.GetSingleton<Emitted<Peak>>().Items), Is.EqualTo(Peaks(scalar.GetSingleton<Emitted<Peak>>().Items)), $"round {round}");
        vector.GetSingleton<Emitted<Fallen>>().Items.Clear();
        scalar.GetSingleton<Emitted<Fallen>>().Items.Clear();
        vector.GetSingleton<Emitted<Peak>>().Items.Clear();
        scalar.GetSingleton<Emitted<Peak>>().Items.Clear();

        Assert.That(vector.Index<Banner, int>().Count(7), Is.EqualTo(scalar.Index<Banner, int>().Count(7)));
        Assert.That(Destroyed(vector), Is.EqualTo(Destroyed(scalar)), () => $"round {round}: {string.Join(" ", Destroyed(vector))} / {string.Join(" ", Destroyed(scalar))}");
    }

    private static List<EntityId> Ids(in DynamicBuffer<Fallen> records)
    {
        var ids = new List<EntityId>();
        foreach (var record in records.AsReadOnlySpan()) ids.Add(record.Who);
        return ids;
    }

    private static List<int> Peaks(in DynamicBuffer<Peak> records)
    {
        var values = new List<int>();
        foreach (var record in records.AsReadOnlySpan()) values.Add(record.Value);
        return values;
    }

    private static EntityId[] Destroyed(World world)
    {
        var ids = new EntityId[1 << 16];
        world.DestroyedSince(0, ids, out int count);
        return ids.AsSpan(0, count).ToArray();
    }
}
