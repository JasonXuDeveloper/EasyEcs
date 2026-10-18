using System.Buffers;
using EasyEcs;

namespace GameLoop;

// Messages: what clients send. They are not components; ReceiveInput turns each one into an entity holding a request.
public enum MessageKind : byte { Join, Move, Fire, Leave }
public readonly record struct ClientMessage(int Client, MessageKind Kind, float X = 0, float Y = 0);

// Requests: input as data, consumed and destroyed by the simulation's systems in the same tick.
public record struct JoinRequest(int Client) : IComponent;
public record struct MoveIntent(int Client, float X, float Y) : IComponent;
public record struct FireIntent(int Client) : IComponent;
public record struct LeaveRequest(int Client) : IComponent;

// Simulation state.
public record struct Client(int Id) : IComponent, IIndexed<int> { public int Key => Id; }   // a player, by client id
public record struct Position(float X, float Y) : IComponent, ITrackChanges;                // tracked: replicated
public record struct Velocity(float X, float Y) : IComponent;
public record struct Lifetime(int Ticks) : IComponent;                                       // a bullet's remaining ticks

// Singletons: per-world configuration and state.
public record struct Simulation(float DeltaTime, long Tick) : ISingletonComponent;
public record struct Prefabs(Prefab Player, Prefab Bullet) : ISingletonComponent;
public record struct Outbox(ArrayBufferWriter<byte> Bytes, int Updates) : ISingletonComponent;  // replication, unsent
public record struct Stats(long Received, long Joined, long Left, long Fired) : ISingletonComponent;

// Cadences: the default group is the fixed simulation tick; these run when the host says so.
public struct InputGroup : ISystemGroup;
public struct ReplicationGroup : ISystemGroup;
