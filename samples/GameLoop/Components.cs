using System.Buffers;
using EasyEcs;

namespace GameLoop;

// ---- Requests: input as data ----
// ReceiveInputSystem turns each client message into an entity holding one of these.
// The request systems handle them and destroy them in the same tick.

public record struct JoinRequest(int Client) : IComponent;

public record struct MoveRequest(int Client, float X, float Y) : IComponent;

public record struct FireRequest(int Client) : IComponent;

public record struct LeaveRequest(int Client) : IComponent;

// ---- Simulation state ----

// A connected player. IIndexed makes world.ClientIndex find a player by its client id.
public record struct Client(int Id) : IComponent, IIndexed<int>
{
    public int Key => Id;
}

// ITracked records every write, so replication can visit only the entities that moved.
public record struct Position(float X, float Y) : IComponent, ITracked;

public record struct Velocity(float X, float Y) : IComponent;

// How many ticks a bullet has left before it disappears.
public record struct Lifetime(int Ticks) : IComponent;

// ---- Singletons: one value per world ----

// The fixed time step and the number of ticks simulated so far.
public record struct Simulation(float DeltaTime, long Tick) : ISingletonComponent;

// The recipes that players and bullets are created from.
public record struct Prefabs(Prefab Player, Prefab Bullet) : ISingletonComponent;

// Replicated positions waiting to be sent, and how many updates they hold.
public record struct Outbox(ArrayBufferWriter<byte> Bytes, int Updates) : ISingletonComponent;

// Counters the host prints when it stops.
public record struct Stats(
    long Received,
    long Joined,
    long Left,
    long Fired,
    long Replicated,
    long SentBytes) : ISingletonComponent;

// ---- System groups: when systems run ----
// Systems without a group run on world.Update(), the fixed simulation tick.
// Systems in these groups run only when the host calls world.Update<TGroup>().

public struct InputGroup : ISystemGroup;

public struct ReplicationGroup : ISystemGroup;
