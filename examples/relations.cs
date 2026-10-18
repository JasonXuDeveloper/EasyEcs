#:package EasyEcs@4.0.1
// Relations: an indexed EntityId points at an entity (one link per entity), an IRelation buffer at many.
using EasyEcs;

using var world = new World();

// One-to-many: each arrow has one owner; the index lists the arrows of an owner.
var archer = world.CreateEntity(new Health(10));
for (int i = 0; i < 3; i++) world.CreateEntity(new OwnedBy(archer), new Arrow());
var arrows = world.OwnedByIndex;                                     // world.Index<OwnedBy, EntityId>()
Console.WriteLine($"the archer owns {arrows.Count(archer)} arrows");
arrows.WhenTargetDestroyed(TargetPolicy.Destroy);                   // arrows go with their owner

// Many-to-many: an entity in several parties at once.
var red = world.CreateEntity();
var blue = world.CreateEntity();
var hero = world.CreateEntity().AddRelation(new MemberOf(red)).AddRelation(new MemberOf(blue));
world.CreateEntity().AddRelation(new MemberOf(red));
var parties = world.Relations<MemberOf>();
Console.WriteLine($"red has {parties.Count(red)} members, blue {parties.Count(blue)}, hero in blue: {hero.HasRelation<MemberOf>(blue)}");
hero.RemoveRelation<MemberOf>(blue);
Console.WriteLine($"after leaving: blue has {parties.Count(blue)}");

archer.Destroy();
Console.WriteLine($"arrows left after the archer died: {world.Count<Arrow>()}");   // 0

public record struct Health(int Value) : IComponent;
public struct Arrow : IComponent;
public record struct OwnedBy(EntityId Owner) : IComponent, IIndexed<EntityId> { public EntityId Key => Owner; }
public record struct MemberOf(EntityId Target) : IRelation;
