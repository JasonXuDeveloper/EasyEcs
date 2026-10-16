using EasyEcs;

namespace EasyEcs.UnitTest;

// Components carry values so tests can check that data lands in (and stays in) the right slot.
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Name(string Value) : IComponent;   // managed reference inside a component
public struct Frozen : IComponent;                        // tag
public struct Dead : IComponent;                          // tag
public record struct Stun(int Ticks) : IComponent;        // gained and lost often
public record struct Armor(int Value) : IComponent, ITrackChanges;   // tracked: rows record their last write
public record struct Seen(int Count) : IComponent;       // how often a Changed<Armor> method visited the entity
public record struct Shield(int Value) : IComponent, ITrackChanges;  // a second tracked component
public record struct ChildOf(Entity Parent) : IComponent, IIndexed<Entity>  // a relation: indexed by its target
{
    public Entity Key => Parent;
}
public record struct Callsign(string Value) : IComponent, IIndexed<string> // a managed key
{
    public string Key => Value;
}
public struct Marked : IComponent;                        // tag
public record struct C1(int V) : IComponent;
public record struct C2(int V) : IComponent;
public record struct C3(int V) : IComponent;
public record struct C4(int V) : IComponent;
public record struct C5(int V) : IComponent;
public record struct C6(int V) : IComponent;
public record struct C7(int V) : IComponent;
public record struct C8(int V) : IComponent;
public record struct C9(int V) : IComponent;
public record struct Clock(long Tick) : ISingletonComponent;
public record struct Score(int Deaths, int Points) : ISingletonComponent;
