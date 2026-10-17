using NUnit.Framework;

namespace EasyEcs.UnitTest;

public partial class Movement : EcsSystem
{
    [ForEach, Without<Frozen>]
    private static void Move(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

/// <summary>
/// Instance method, Entity and singleton parameters, With filter, structural changes, declaration order. Stateless: the
/// outcome is data only: the entity's state (Dead) and the world's statistics (Score singleton).
/// </summary>
public partial class Damage : EcsSystem
{
    [ForEach, With<Velocity>, Without<Dead>]
    private void Hurt(Entity entity, ref Health health, ref Score score)
    {
        if (--health.Value > 0) return;
        entity.Set(new Dead());
        score.Deaths++; // singleton parameter: resolved once per pass (added if missing)
    }

    [ForEach, With<Dead>]   // no component parameter: Dead is queried through the filter
    private static void Corpses(Entity entity) => entity.Remove<Velocity>();
}

/// <summary>Optional component: one method for entities with and without Armor.</summary>
public partial class Regenerate : EcsSystem
{
    [ForEach]
    private static void Heal(ref Health health, Maybe<Armor> armor)
    {
        health = health with { Value = health.Value + (armor.HasValue ? armor.Value.Value : 1) };
        if (armor.HasValue) armor.Value = armor.Value with { Value = armor.Value.Value - 1 }; // worn by use
    }
}

/// <summary>Derived partial systems compose: base [ForEach] methods run first.</summary>
public partial class MovementWithGravity : Movement
{
    [ForEach]
    private static void Fall(ref Velocity v) => v.Y -= 1;
}

/// <summary>Changes the archetype of the visited entity, then keeps writing through its refs.</summary>
public partial class BurnThenHeal : EcsSystem
{
    [ForEach, Without<Dead>]
    private static void Tick(Entity entity, ref Health health, in Position position)
    {
        health = health with { Value = health.Value - 1 };
        entity.Set(new Dead());                                  // moves the row: health now points at the old row
        health = health with { Value = health.Value + 10 };      // carried over to the new row after the method returns
        entity.Set(new Position(position.X + 1, position.Y));    // in parameter: the Set value is not overwritten
    }
}

/// <summary>Moves every visited entity into another archetype that the same pass also matches.</summary>
public partial class Promote : EcsSystem
{
    [ForEach]
    private static void Visit(Entity entity, ref Health health)
    {
        health = health with { Value = health.Value + 1 };
        if (!entity.Has<Frozen>()) entity.Set(new Frozen());
        else entity.Remove<Frozen>();
    }
}

/// <summary>Batch method: whole runs of rows as spans (vectorizable).</summary>
public partial class BatchMovement : EcsSystem
{
    [ForEach, Without<Frozen>]
    private static void Move(System.Span<Position> p, System.ReadOnlySpan<Velocity> v, ref Score score)
    {
        // Position and Velocity are two floats each: one float span per column, added with SIMD.
        var positions = System.Runtime.InteropServices.MemoryMarshal.Cast<Position, float>(p);
        var velocities = System.Runtime.InteropServices.MemoryMarshal.Cast<Velocity, float>(v);
        int i = 0;
        for (int width = System.Numerics.Vector<float>.Count; i + width <= positions.Length; i += width)
            (new System.Numerics.Vector<float>(positions[i..]) + new System.Numerics.Vector<float>(velocities[i..])).CopyTo(positions[i..]);
        for (; i < positions.Length; i++) positions[i] += velocities[i];
        score.Deaths += p.Length; // counts visited rows
    }
}

/// <summary>Batch method with the run's entities: records the id of every row whose health is low.</summary>
public partial class FindLowHealth : EcsSystem
{
    [ForEach, Without<Frozen>]
    private static void Find(EntitySpan entities, System.ReadOnlySpan<Health> health, ref Score score)
    {
        for (int i = 0; i < health.Length; i++)
            if (health[i].Value < 10) score.Points += entities[i].Id.Index;
    }
}

/// <summary>A batch job that tries a structural change (it must throw: the spans point into the tables).</summary>
public struct DestroyInBatch : IForEachBatch<Health>
{
    public static int Writes => 0;
    public void Execute(EntitySpan entities, System.Span<Health> health) => entities[0].Destroy();
}

/// <summary>Promises no structural change but destroys the entity it visits (a broken promise the gate catches).</summary>
public struct DestroyDespitePromise : IForEachEntity<Health>, INoStructuralChanges
{
    public static int Writes => 0;
    public void Execute(Entity entity, ref Health health) => entity.Destroy();
}

/// <summary>Counts positioned entities that are frozen or dead (or both): [WithAny] filters.</summary>
public partial class CountHalted : EcsSystem
{
    [ForEach, WithAny<Frozen>, WithAny<Dead>]
    static void Count(in Position position, ref Score score) => score.Points++;
}

public class ForEachSystemTests
{
    [Test]
    public void WithAnyVisitsEntitiesHavingAtLeastOneOfTheComponents()
    {
        using var world = new World();
        world.CreateEntity(new Position());
        world.CreateEntity(new Position(), new Frozen());
        world.CreateEntity(new Position(), new Dead());
        world.CreateEntity(new Position(), new Frozen(), new Dead());
        world.CreateEntity(new Frozen());
        world.AddSingleton<Score>();
        world.AddSystem<CountHalted>();
        world.Update();

        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(3));
    }

    [Test]
    public void MaybeParametersSeeTheOptionalComponentWhenPresent()
    {
        using var world = new World();
        var bare = world.CreateEntity(new Health(10));
        var armored = world.CreateEntity(new Health(10), new Armor(5));
        var disabled = world.CreateEntity(new Health(10), new Armor(5));
        disabled.Disable<Armor>();
        world.AddSystem<Regenerate>();
        ulong mark = world.ChangeTick;
        world.Update();

        Assert.That(bare.Read<Health>().Value, Is.EqualTo(11));
        Assert.That(armored.Read<Health>().Value, Is.EqualTo(15));
        Assert.That(armored.Read<Armor>().Value, Is.EqualTo(4));                   // written through the Maybe
        Assert.That(world.Count<Armor>(QueryFilter.Create().Changed<Armor>(mark + 1)), Is.EqualTo(1)); // a tracked write stamps the row
        Assert.That(disabled.Read<Health>().Value, Is.EqualTo(11));               // a disabled component is absent
        Assert.Throws<System.InvalidOperationException>(static () => new Maybe<Armor>(ref System.Runtime.CompilerServices.Unsafe.NullRef<Armor>()).Value.ToString());
    }

    [Test]
    public void BatchMethodsReceiveEveryMatchingRowOnce()
    {
        using var world = new World();
        var entities = new Entity[1000];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.CreateEntity(new Position(i, 0), new Velocity(1, 2));
        for (int i = 0; i < entities.Length; i += 3) entities[i].Destroy();   // tombstones: several runs per table
        entities[1].Set(new Frozen());

        world.AddSingleton<Score>();
        world.AddSystem<BatchMovement>();
        world.Update();

        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(666 - 1));
        Assert.That(entities[1].Get<Position>(), Is.EqualTo(new Position(1, 0)));
        Assert.That(entities[2].Get<Position>(), Is.EqualTo(new Position(3, 2)));
        Assert.That(entities[999 - 1].Get<Position>(), Is.EqualTo(new Position(999, 2)));
    }

    [Test]
    public void BatchMethodsGetTheEntitiesOfTheirRows()
    {
        using var world = new World();
        world.AddSingleton<Score>();
        int expected = 0;
        for (int i = 0; i < 300; i++)
        {
            var e = i % 3 == 0 ? world.CreateEntity(new Health(i % 20), new Frozen()) : world.CreateEntity(new Health(i % 20));
            if (i % 3 != 0 && i % 20 < 10) expected += e.Id.Index;
        }

        world.AddSystem<FindLowHealth>();
        world.Update();
        Assert.That(world.GetSingleton<Score>().Points, Is.EqualTo(expected));
    }

    [Test]
    public void StructuralChangesThrowInsideBatchJobs()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1));
        var job = new DestroyInBatch();
        Assert.Throws<System.InvalidOperationException>(() => world.ForEachBatch<Health, DestroyInBatch>(ref job));
        Assert.That(e.IsAlive, Is.True);
        e.Destroy();                                           // the gate is open again after the throw
        Assert.That(e.IsAlive, Is.False);
    }

    [Test]
    public void StructuralChangesThrowInsideJobsThatPromiseNone()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(1));
        var job = new DestroyDespitePromise();
        Assert.Throws<System.InvalidOperationException>(() => world.ForEachEntity<Health, DestroyDespitePromise>(ref job));
        Assert.That(e.IsAlive, Is.True);
        e.Destroy();                                           // the gate is open again after the throw
        Assert.That(e.IsAlive, Is.False);
    }

    [Test]
    public void WritesThroughRefsFollowTheVisitedEntityAcrossItsStructuralChanges()
    {
        using var world = new World();
        var e = world.CreateEntity(new Health(5), new Position(1, 1));
        world.AddSystem<BurnThenHeal>();
        world.Update();

        Assert.That(e.Has<Dead>(), Is.True);
        Assert.That(e.Get<Health>().Value, Is.EqualTo(14));
        Assert.That(e.Get<Position>(), Is.EqualTo(new Position(2, 1)));
    }

    [Test]
    public void APassVisitsEachEntityOnceEvenWhenItMovesToAMatchingArchetype()
    {
        using var world = new World();
        var plain = new Entity[100];
        var frozen = new Entity[100];
        for (int i = 0; i < 100; i++)
        {
            plain[i] = world.CreateEntity(new Health(0));
            frozen[i] = world.CreateEntity(new Health(0), new Frozen());
        }

        world.AddSystem<Promote>();
        world.Update();
        world.Update();

        for (int i = 0; i < 100; i++)
        {
            Assert.That(plain[i].Get<Health>().Value, Is.EqualTo(2));
            Assert.That(frozen[i].Get<Health>().Value, Is.EqualTo(2));
            Assert.That(plain[i].Has<Frozen>(), Is.False);
            Assert.That(frozen[i].Has<Frozen>(), Is.True);
        }
    }

    [Test]
    public void StaticForEachWithFilter()
    {
        using var world = new World();
        var moving = world.CreateEntity(new Position(0, 0), new Velocity(1, 2));
        var frozen = world.CreateEntity(new Position(0, 0), new Velocity(1, 2), new Frozen());
        world.AddSystem<Movement>();
        world.Update();
        world.Update();

        Assert.That(moving.Get<Position>(), Is.EqualTo(new Position(2, 4)));
        Assert.That(frozen.Get<Position>(), Is.EqualTo(new Position(0, 0)));
    }

    [Test]
    public void InstanceForEachWithEntityAndStructuralChanges()
    {
        using var world = new World();
        for (int hp = 1; hp <= 3; hp++) world.CreateEntity(new Health(hp), new Velocity());
        var bystander = world.CreateEntity(new Health(1)); // no Velocity: filtered out
        world.AddSystem<Damage>();

        world.Update();
        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(1));
        Assert.That(world.Count<Velocity>(), Is.EqualTo(2), "Corpses ran after Hurt in the same update");

        world.Update();
        world.Update();
        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(3), "Without<Dead>: each death counted once");
        Assert.That(world.Count<Dead>(), Is.EqualTo(3));
        Assert.That(bystander.Get<Health>().Value, Is.EqualTo(1));
    }

    [Test]
    public void EntityParametersCarryTheCurrentVersionOfRecycledIds()
    {
        // Entity parameters are built from the archetype's entity rows: recycled ids, transitions and
        // rows filled by the last row must keep the version in step, or the handle would be stale.
        using var world = new World();
        var old = new Entity[64];
        for (int i = 0; i < old.Length; i++) old[i] = world.CreateEntity(new Health(1), new Velocity());
        for (int i = old.Length - 1; i >= 0; i -= 2) old[i].Destroy();
        var fresh = new Entity[32];
        for (int i = 0; i < fresh.Length; i++) fresh[i] = world.CreateEntity(new Health(1), new Velocity());
        world.AddSystem<Damage>();

        world.Update(); // Hurt sets Dead through its Entity, Corpses removes Velocity through its Entity

        Assert.That(world.GetSingleton<Score>().Deaths, Is.EqualTo(64));
        for (int i = 0; i < fresh.Length; i++)
            Assert.That((fresh[i].Has<Dead>(), fresh[i].Has<Velocity>()), Is.EqualTo((true, false)));
    }

    [Test]
    public void DerivedSystemsRunBaseMethodsFirst()
    {
        using var world = new World();
        var e = world.CreateEntity(new Position(0, 0), new Velocity(0, 0));
        world.AddSystem<MovementWithGravity>();
        world.Update(); // Move (v = 0), then Fall (v = -1)
        world.Update(); // Move (y = -1), then Fall (v = -2)
        Assert.That(e.Get<Position>().Y, Is.EqualTo(-1));
        Assert.That(e.Get<Velocity>().Y, Is.EqualTo(-2));
    }
}
