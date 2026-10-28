using EasyEcs;

namespace MultiWorld;

public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

// A ghost row: a copy of a unit of the neighbor zone that stands within sight of the border (MPI's ghost cell). It
// holds the Position the neighbors read and no Velocity, so the systems that move units never write it: it is
// read-only, and the border throws it away and copies it again between two updates.
public struct Ghost : IComponent;

// How many times units of this zone saw another unit in the last update, ghosts included. Each world has its own.
// [Sum] makes it a reduction: the generator writes how two partial counts combine (they add up). A field, not a
// record's property: a vector loop folds its lanes into fields.
public partial struct Tally : ISingletonComponent
{
    [Sum] public int Sightings;
}
