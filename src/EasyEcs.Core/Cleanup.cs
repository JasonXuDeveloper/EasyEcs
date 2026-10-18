namespace EasyEcs;

/// <summary>
/// Tag of an entity that was destroyed while holding <see cref="ICleanupComponent"/>s: its other components are gone,
/// it stays alive until its last cleanup component is removed. Select such entities with <c>With&lt;Destroyed&gt;</c>
/// (a cleanup system) and new ones with <c>Without&lt;Destroyed&gt;</c> (an acquiring system).
/// </summary>
public struct Destroyed : IComponent;
