using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ArchetypeTests
{
    [Test]
    public void TogglingComponentsKeepsMembershipConsistent()
    {
        using var world = new World();
        for (int i = 0; i < 100; i++) world.CreateEntity().Value.AddComponent<Health>();

        for (int round = 0; round < 3; round++)
        {
            foreach (var item in world.Query<Health>())
            {
                item.Entity.AddComponent<Frozen>();
                item.Entity.RemoveComponent<Frozen>();
                item.Entity.AddComponent<Frozen>();
            }

            Assert.That(world.Count<Health, Frozen>(), Is.EqualTo(100));
            foreach (var item in world.Query<Health, Frozen>()) item.Entity.RemoveComponent<Frozen>();
            Assert.That(world.Count<Frozen>(), Is.EqualTo(0));
        }
    }

    [Test]
    public void CompactionRemovesTombstonesAndKeepsSlotsValid()
    {
        using var world = new World();
        var refs = new EntityRef[100];
        for (int i = 0; i < refs.Length; i++)
        {
            refs[i] = world.CreateEntity();
            refs[i].Value.AddComponent<Health>().Value.Value = i;
        }

        for (int i = 0; i < refs.Length; i += 2) world.DestroyEntity(refs[i]);
        var (slots, alive, fragmentation) = world.GetFragmentationStats();
        Assert.That(alive, Is.EqualTo(50));
        Assert.That(fragmentation, Is.GreaterThan(0.4f));

        world.CompactArchetypes();
        (slots, alive, fragmentation) = world.GetFragmentationStats();
        Assert.That(slots, Is.EqualTo(50));
        Assert.That(fragmentation, Is.EqualTo(0f));

        // Removal after compaction must hit the right (moved) slots.
        for (int i = 1; i < refs.Length; i += 4) world.DestroyEntity(refs[i]);
        Assert.That(world.Count<Health>(), Is.EqualTo(25));
        foreach (var item in world.Query<Health>())
            Assert.That(item.Component1.Value % 4, Is.EqualTo(3));
    }
}
