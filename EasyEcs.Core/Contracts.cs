namespace EasyEcs;

/// <summary>
/// Marks a struct as component data. Components are plain data stored in one dense column per type and indexed by
/// entity id; keep them small (ideally a few fields, well under a 64-byte cache line) and split rarely-used data
/// into separate components so iteration only loads what it touches.
/// </summary>
public interface IComponent;

/// <summary>Marks a struct as a world singleton: exactly one value per world (clock, configuration, shared state).</summary>
public interface ISingletonComponent;

/// <summary>
/// Marks a <c>ForEach</c> job that makes no structural change (no create/destroy/add/remove/set on any entity of the
/// world it iterates). ForEach then walks archetypes without tombstones with no per-entity checks. The source generator
/// adds it to <c>[ForEach]</c> methods that are static and take neither an <see cref="Entity"/> nor a
/// <see cref="World"/>; implement it by hand only when the job honors the contract.
/// </summary>
public interface INoStructuralChanges;
