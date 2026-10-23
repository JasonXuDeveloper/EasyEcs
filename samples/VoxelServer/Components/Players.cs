using System.Numerics;
using EasyEcs;

namespace VoxelServer;

public record struct Player(int Client) : IComponent;

// What the client asked for this tick: a direction to walk in (x, z) and a dig of the block underfoot.
public record struct Input(Vector2 Walk, bool Dig) : IComponent;

// The chunk a player's view is centered on: its client has the loaded chunks of the square of Rules.PlayerVision around
// it, so the view is this one value, not a list of chunks. AoiViewSystem moves it when the player crosses into another
// chunk, after sending what the step brings into view and forgetting what it takes out. An index of the players by it
// (world.ViewCenterIndex) counts the players of a chunk in O(1): who sees a chunk, who a zombie can hunt, who picks up
// an item. A cleanup component: a player who leaves keeps it, Retired, until AoiViewSystem has released its view.
public partial record struct ViewCenter([IndexKey] ChunkPos Chunk) : ICleanupComponent;

// The blocks a player dug: the scoreboard. An ordered index (world.ScoreIndex.Descending()) walks the players from
// the best digger down, kept sorted as the scores change, so the top of the board is never a sort.
public partial record struct Score([IndexKey] int BlocksDug) : IComponent;

// One slot of a player's inventory: a DynamicBuffer<ItemStack> on the player.
public record struct ItemStack(Block Kind, int Count);
