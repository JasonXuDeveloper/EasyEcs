using EasyEcs;

namespace VoxelServer;

// The three parts of a tick. The host runs them in this order (Host/Server.cs), each with its own
// world.Update<TGroup>(), so each part could run at its own rate: input as the packets arrive, the simulation 20
// times a second, replication at whatever rate the clients need.

// What the clients asked for: walking and digging (Host/FakeClients.cs stands in for the network).
public struct ClientInput : ISystemGroup;

// The world itself: chunks, water, movement, the rules of the game.
public struct Simulation : ISystemGroup;

// What goes back to the clients.
public struct Replication : ISystemGroup;
