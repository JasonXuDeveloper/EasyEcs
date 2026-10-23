using System.Numerics;
using EasyEcs;

namespace VoxelServer;

// Everything that moves: players, zombies, dropped items.

// The feet of a player or a zombie, the bottom of an item. ITracked: replication sends only what moved.
public record struct Position(Vector3 Value) : IComponent, ITracked;

// Blocks per tick.
public record struct Velocity(Vector3 Value) : IComponent;

// What PhysicsSystem found: standing on a block, walking into a wall higher than a step.
public record struct Body(bool OnGround, bool Blocked) : IComponent;

// ITracked: the systems that handle a death visit only the healths written ([Changed<Health>]).
public record struct Health(int Value) : IComponent, ITracked;

// The chunk an entity that moves is in. An index of them by chunk (world.InChunkIndex.Find(chunk)): the grid through
// which players and zombies see who is near. GridSystem keeps it current and writes it only when the entity crosses
// into another chunk; ITracked, so AoiViewSystem and AoiSpawnSystem visit only the entities that crossed.
public partial record struct InChunk([IndexKey] ChunkPos Pos) : IComponent, ITracked;

// The chunk the clients see an entity in (Placed once it has one): what AoiSpawnSystem compares a crossing with. A cleanup
// component: a destroyed entity keeps it, Retired, until AoiDespawnSystem has despawned it for that chunk's viewers.
public record struct Visible(ChunkPos Chunk, bool Placed) : ICleanupComponent;

// The packet of an entity that moved, for the players that have its chunk in view: its position, its health bar where it
// has one (players and zombies; -1 for an item), and how many players get it. A record SendEntitiesSystem emits
// (Emit<EntityUpdate>) and SendPacketsSystem sends, not a component.
public record struct EntityUpdate(EntityId Entity, Vector3 Position, int Health, int Viewers);
