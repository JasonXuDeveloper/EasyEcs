using EasyEcs;

namespace MultiWorld;

public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

public record struct Health(int Value) : IComponent;

// How many units died in this zone in the last update. Each world has its own. [Sum] makes it a reduction: the
// generator writes how two partial counts combine (they add up). A field, not a record's property: a vector loop
// folds its lanes into fields.
public partial struct Tally : ISingletonComponent
{
    [Sum] public int Deaths;
}
