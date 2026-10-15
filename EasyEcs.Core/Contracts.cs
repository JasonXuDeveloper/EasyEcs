namespace EasyEcs;

/// <summary>
/// Marks a struct as component data. Components are plain data stored in one dense column per type and indexed by
/// entity id; keep them small (ideally a few fields, well under a 64-byte cache line) and split rarely-used data
/// into separate components so iteration only loads what it touches.
/// </summary>
public interface IComponent;

/// <summary>Marks a struct as a world singleton: exactly one value per world (clock, configuration, shared state).</summary>
public interface ISingletonComponent;

