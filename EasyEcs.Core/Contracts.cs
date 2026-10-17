namespace EasyEcs;

/// <summary>
/// Marks a struct as component data. Components are plain data stored in archetype tables, one column per type and
/// one row per entity; keep them small (ideally a few fields, well under a 64-byte cache line) and split rarely-used data
/// into separate components so iteration only loads what it touches.
/// </summary>
public interface IComponent;

/// <summary>
/// Marks a component whose changes are tracked: every row records the world's <see cref="World.ChangeTick"/> of its
/// last write, and <c>[Changed&lt;T&gt;]</c> / <c>QueryFilter.Changed&lt;T&gt;(since)</c> select the rows written since a
/// system last ran. A write is any <c>ref</c> access: <c>ref</c> parameters of <c>[ForEach]</c> methods and jobs,
/// <c>Query</c> items, <c>Entity.Get</c> / <c>Add</c> / <c>GetOrAdd</c> / <c>Set</c>, and creating the entity or adding
/// the component. Costs 8 bytes per row and one store per write; untracked components pay nothing.
/// </summary>
public interface ITrackChanges;

/// <summary>
/// Marks a component that outlives the destruction of its entity: destroying an entity holding one removes its other
/// components and adds the <see cref="Destroyed"/> tag instead of freeing it, so a system can release what the
/// component stands for (a socket, a reservation, a slot in another service) and then remove it. Removing the entity's
/// last cleanup component frees it. Use it for resources outside the world that must be released exactly once.
/// </summary>
public interface ICleanupComponent : IComponent;

/// <summary>Marks a struct as a world singleton: exactly one value per world (clock, configuration, shared state).</summary>
public interface ISingletonComponent;

/// <summary>
/// Marks a <c>ForEach</c> job that makes no structural change (no create/destroy/add/remove/set on any entity of the
/// world it iterates). ForEach then walks tables with no per-entity bookkeeping. The source generator adds it to
/// <c>[ForEach]</c> methods that are static, take no <see cref="World"/> and use their <see cref="Entity"/> parameter
/// (if any) only to read; implement it by hand only when the job honors the contract.
/// </summary>
public interface INoStructuralChanges;
