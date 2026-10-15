namespace EasyEcs;

/// <summary>
/// Marks a struct as component data. Components are plain data stored in archetype tables, one column per type and
/// one row per entity; keep them small (ideally a few fields, well under a 64-byte cache line) and split rarely-used data
/// into separate components so iteration only loads what it touches.
/// </summary>
public interface IComponent;

/// <summary>Marks a struct as a world singleton: exactly one value per world (clock, configuration, shared state).</summary>
public interface ISingletonComponent;

/// <summary>
/// Marks a <c>ForEach</c> job that makes no structural change (no create/destroy/add/remove/set on any entity of the
/// world it iterates). ForEach then walks tables with no per-entity bookkeeping. The source generator adds it to
/// <c>[ForEach]</c> methods that are static, take no <see cref="World"/> and use their <see cref="Entity"/> parameter
/// (if any) only to read; implement it by hand only when the job honors the contract.
/// </summary>
public interface INoStructuralChanges;
