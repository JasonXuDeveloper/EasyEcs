using EasyEcs;

namespace VoxelServer;

// The parts of a tick. The host runs them in this order (Host/Server.cs): the input group, then the simulation,
// which is every system in no group, on the default world.Update(), then the replication group, so each part could run
// at its own rate: input as the packets arrive, the simulation 20 times a second, replication at whatever rate the
// clients need.

// What the clients asked for: walking and digging (Host/FakeClients.cs stands in for the network).
public struct ClientInput : ISystemGroup;

// What goes back to the clients.
public struct Replication : ISystemGroup;
