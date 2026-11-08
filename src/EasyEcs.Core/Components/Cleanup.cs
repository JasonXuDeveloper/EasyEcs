using Nino.Core;

namespace EasyEcs;

/// <summary>
/// Tag of a retired entity: <c>Destroy</c> found <see cref="ICleanupComponent"/>s on it, so it took the other
/// components away and kept the entity alive until its last cleanup component is removed (then the entity is freed:
/// destroyed). Select such entities with <c>With&lt;Retired&gt;</c> (a cleanup system) and new ones with
/// <c>Without&lt;Retired&gt;</c> (an acquiring system).
/// <para>Saves record it by its former name (<c>EasyEcs.Destroyed</c>, the tag's name before 4.7), so saves written
/// before the rename load their retired entities.</para>
/// </summary>
[NinoFormerName("EasyEcs.Destroyed")]
public struct Retired : IComponent;
