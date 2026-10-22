using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>A summon dies with its summoner in every world: the policy is declared on the type.</summary>
[WhenTargetDestroyed(TargetPolicy.Destroy)]
public partial record struct SummonedBy([Key] EntityId Summoner) : IComponent;

/// <summary>A pact's links go when its target is destroyed, in every world.</summary>
[WhenTargetDestroyed(TargetPolicy.Remove)]
public record struct PactWith(EntityId Target) : IRelation;

public class TargetPolicyTests
{
    [Test]
    public void ADeclaredPolicyHoldsFromAWorldsFirstTable()
    {
        using var world = new World();
        var summoner = world.CreateEntity(new Health(1));
        var summon = world.CreateEntity(new SummonedBy(summoner), new Health(2));
        var other = world.CreateEntity(new SummonedBy(summon));
        var ally = world.CreateEntity().Link(new PactWith(summoner)).Link(new PactWith(other));
        summoner.Destroy();                                                   // no setup call in this world
        Assert.That((summon.IsAlive, other.IsAlive, ally.IsAlive), Is.EqualTo((false, false, true)));
        Assert.That(ally.Read<DynamicBuffer<PactWith>>().Count, Is.Zero);    // both targets went: their links went
    }

    [Test]
    public void AWorldsOwnPolicyReplacesTheDeclaredOne()
    {
        using var world = new World();
        world.Index<SummonedBy, EntityId>().WhenTargetDestroyed(TargetPolicy.Keep); // before the first table
        world.Relations<PactWith>().WhenTargetDestroyed(TargetPolicy.Keep);
        var summoner = world.CreateEntity(new Health(1));
        var summon = world.CreateEntity(new SummonedBy(summoner));
        var ally = world.CreateEntity().Link(new PactWith(summoner));
        summoner.Destroy();
        Assert.That((summon.IsAlive, ally.Read<DynamicBuffer<PactWith>>().Count), Is.EqualTo((true, 1)));

        using var later = new World();
        var first = later.CreateEntity(new Health(1));
        var bound = later.CreateEntity(new SummonedBy(first));
        later.Index<SummonedBy, EntityId>().WhenTargetDestroyed(TargetPolicy.Remove); // after the first table, before a destroy
        first.Destroy();
        Assert.That((bound.IsAlive, bound.Has<SummonedBy>()), Is.EqualTo((true, false)));
    }

    [Test]
    public void ADeclaredPolicyAppliesToAnIndexCreatedFirst()
    {
        using var world = new World();
        var index = world.Index<SummonedBy, EntityId>();                      // no table yet: the index brings the rule
        var summoner = world.CreateEntity(new Health(1));
        var summon = world.CreateEntity(new SummonedBy(summoner));
        Assert.That(index.Count(summoner), Is.EqualTo(1));
        summoner.Destroy();
        Assert.That(summon.IsAlive, Is.False);
    }

    [Test]
    public void ARemovedPolicyTakesTheComponentFromEveryFollower()
    {
        using var world = new World();
        world.Index<Follows, EntityId>().WhenTargetDestroyed(TargetPolicy.Remove);
        var leader = world.CreateEntity(new Health(1));
        var a = world.CreateEntity(new Follows(leader), new Health(2));
        var b = world.CreateEntity(new Follows(leader));
        var other = world.CreateEntity(new Follows(a));
        leader.Destroy();
        Assert.That((a.IsAlive, a.Has<Follows>(), b.Has<Follows>(), other.Has<Follows>()), Is.EqualTo((true, false, false, true)));
        Assert.That(world.Index<Follows, EntityId>().Count(leader), Is.Zero);
    }

    [Test]
    public void ADestroyPolicyCascadesThroughChainsAndOtherRules()
    {
        using var world = new World();
        world.Index<Follows, EntityId>().WhenTargetDestroyed(TargetPolicy.Destroy);
        var summoner = world.CreateEntity(new Health(1));
        var summon = world.CreateEntity(new Follows(summoner));
        var minion = world.CreateEntity(new Follows(summon));
        var pet = world.CreateEntity(new ChildOf(minion));                  // ChildOf: destroyed with its parent
        var grandchild = world.CreateEntity(new Follows(pet));
        var stranger = world.CreateEntity(new Health(9));
        summoner.Destroy();
        Assert.That((summon.IsAlive, minion.IsAlive, pet.IsAlive, grandchild.IsAlive, stranger.IsAlive),
            Is.EqualTo((false, false, false, false, true)));
    }

    [Test]
    public void KeepIsTheDefaultAndChildOfCanBeDetached()
    {
        using var world = new World();
        var leader = world.CreateEntity(new Health(1));
        var follower = world.CreateEntity(new Follows(leader));
        var parent = world.CreateEntity(new Health(2));
        var child = world.CreateEntity(new ChildOf(parent));
        world.Index<ChildOf, EntityId>().WhenTargetDestroyed(TargetPolicy.Remove); // children become roots
        leader.Destroy();
        parent.Destroy();
        Assert.That(follower.Read<Follows>().Target, Is.EqualTo(leader.Id));    // kept, findable by the dead id
        Assert.That((child.IsAlive, child.Has<ChildOf>()), Is.EqualTo((true, false)));
    }

    [Test]
    public void ARelationDropsItsLinksOrItsSources()
    {
        using var world = new World();
        world.Relations<MemberOf>().WhenTargetDestroyed(TargetPolicy.Remove);
        var party = world.CreateEntity(new Health(1));
        var guild = world.CreateEntity(new Health(2));
        var member = world.CreateEntity().Link(new MemberOf(party)).Link(new MemberOf(guild)).Link(new MemberOf(party));
        party.Destroy();
        Assert.That((member.Links<MemberOf>(party.Id), member.Links<MemberOf>(guild.Id)), Is.EqualTo((false, true)));
        Assert.That(world.Relations<MemberOf>().Count(party), Is.Zero);

        world.Relations<MemberOf>().WhenTargetDestroyed(TargetPolicy.Destroy);
        guild.Destroy();
        Assert.That(member.IsAlive, Is.False);
    }

    [Test]
    public void DestroyingAWholeTableAppliesThePolicy()
    {
        using var world = new World();
        world.Index<Follows, EntityId>().WhenTargetDestroyed(TargetPolicy.Remove);
        var leaders = new Entity[3];
        world.CreateEntities(leaders, new Health(1));
        var follower = world.CreateEntity(new Follows(leaders[1]), new Position(0, 0));
        Assert.That(world.DestroyAll<Health>(), Is.EqualTo(3));          // the bulk path frees the table at once
        Assert.That(follower.Has<Follows>(), Is.False);
    }
}
