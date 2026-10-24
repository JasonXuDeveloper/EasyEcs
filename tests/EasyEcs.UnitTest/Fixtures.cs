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
public record struct Armor(int Value) : IComponent, ITracked;        // tracked: rows record their last write
public record struct Seen(int Count) : IComponent;       // how often a Changed<Armor> method visited the entity
public record struct Shield(int Value) : IComponent, ITracked;       // a second tracked component
public record struct Follows(EntityId Target) : IComponent, IIndexed<EntityId> // a relation: indexed by its target
{
    public EntityId Key => Target;
}
public record struct Callsign(string Value) : IComponent, IIndexed<string> // a managed key
{
    public string Key => Value;
}
public record struct Login(int Id) : IComponent, IUniqueIndexed<int> // unique: one entity per login
{
    public int Key => Id;
}
public record struct Handle(string Value) : IComponent, IUniqueIndexed<string> // unique, a managed key (null: none)
{
    public string Key => Value;
}
public record struct GridCell(int X, int Y) : IComponent, IDirectIndexed<long> // unique, its keys in 16 slots (x, y & 3)
{
    public long Key => ((long)X << 32) | (uint)Y;
    static int IDirectIndexed<long>.Slots => 16;
    // A negative x is out of range (the hash alone); keys 4 apart share a slot.
    static int IDirectIndexed<long>.SlotOf(long key) => (int)(key >> 32) < 0 ? -1 : (int)((key >> 32) & 3) * 4 + (int)(key & 3);
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

/// <summary>Allocation measurements of code that wakes helper threads.</summary>
public static class Allocations
{
    /// <summary>
    /// The bytes one run of <paramref name="run"/> allocated (<paramref name="bytes"/>: this thread's allocated bytes, or
    /// another counter such as a system's stats). One run: helpers are the process's fixed threads (no thread pool
    /// thread starts while it runs) and their state is set up on all of them at once, so a run allocates nothing whichever
    /// thread runs which chunk.
    /// </summary>
    public static long Of(System.Action run, System.Func<long> bytes = null)
    {
        bytes ??= System.GC.GetAllocatedBytesForCurrentThread;
        long before = bytes();
        run();
        return bytes() - before;
    }
}
